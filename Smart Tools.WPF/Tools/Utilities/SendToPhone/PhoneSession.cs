using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Timer = System.Threading.Timer;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// One Send to Phone session: a small HTTPS server (Kestrel) that listens only on the one private address the user
    /// picked, a random port, a one-time token in the path, and a device that the user must approve on the PC.
    /// Encrypted mode seals every chunk of every file (and the names) with a key that only the QR code carries (see
    /// PhoneCrypto); TLS-only mode streams plain files over HTTPS. Received files go to a hidden .part file and are
    /// moved into place only when complete, never over an existing file. Nothing here touches the Windows firewall or
    /// trust stores, and the key and token live in memory only.
    /// </summary>
    public sealed class PhoneSession : IAsyncDisposable
    {
        public sealed record Options(IPAddress Address, PhoneSecurity Security, string ReceiveFolder, long MaxFileBytes, TimeSpan Idle, bool BlockPrograms);

        private sealed class Upload
        {
            public required string Id;
            public required byte[] IdBytes;
            public required byte[] Key;
            public required string Name;
            public required long Size;
            public required bool IsText;
            public required PhoneTransfer Transfer;
            public string? PartPath;
            public FileStream? Stream;
            public MemoryStream? TextBuffer;
            public long Next;
            public long Received;
            public DateTime Touched = DateTime.UtcNow;
            public readonly SemaphoreSlim Gate = new(1, 1);
        }

        private readonly Options _o;
        private readonly byte[] _key = PhoneCrypto.NewKey();
        private readonly string _token = PhoneCrypto.NewHexId(16);
        private readonly object _lock = new();
        private readonly List<PhoneOffer> _offers = new();
        private readonly List<PhoneTransfer> _transfers = new();
        private readonly Dictionary<string, PhoneTransfer> _downloads = new();
        private readonly ConcurrentDictionary<string, Upload> _uploads = new();
        private readonly ConcurrentDictionary<string, (int Count, DateTime Since, DateTime BlockedUntil)> _failures = new();
        private readonly HashSet<string> _denied = new();
        private readonly byte[] _stateKey;
        private WebApplication? _app;
        private X509Certificate2? _cert;
        private Timer? _watchdog;
        private int _port;
        private string? _pendingId, _approvedId;
        private long _lastActivity = Environment.TickCount64;
        private int _stopping;
        private int _failuresTotal;
        private readonly Dictionary<string, byte[]> _assets = new();

        public PhoneSession(Options options)
        {
            _o = options;
            _stateKey = PhoneCrypto.StateKey(_key);
        }

        public PhoneState State { get; private set; } = PhoneState.Stopped;
        public string Url { get; private set; } = "";
        public string Fingerprint { get; private set; } = "";
        public int Port => _port;
        public PhoneSecurity Security => _o.Security;

        /// <summary>For tests and the view: the link without the key (what travels to the server).</summary>
        public string UrlWithoutKey => Url.Contains('#') ? Url[..Url.IndexOf('#')] : Url;

        public event Action<PhoneState>? StateChanged;
        /// <summary>A phone asks to connect: (its address, a name from its browser). The user answers with Approve or Deny.</summary>
        public event Action<string, string>? DeviceRequested;
        public event Action<PhoneTransfer>? TransferAdded;
        /// <summary>The session ended: "user", "idle", "failures" or "error".</summary>
        public event Action<string>? Ended;

        public IReadOnlyList<PhoneTransfer> Transfers { get { lock (_lock) return _transfers.ToList(); } }
        public IReadOnlyList<PhoneOffer> Offers { get { lock (_lock) return _offers.ToList(); } }
        public bool HasActiveTransfer => Transfers.Any(t => t.State == PhoneTransferState.Active);

        private void SetState(PhoneState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

        // =================================================================
        //  Start and stop
        // =================================================================

        public async Task StartAsync()
        {
            SetState(PhoneState.Starting);
            try
            {
                var made = PhoneCertificate.Create(_o.Address);
                _cert = made.Certificate;
                Fingerprint = made.Fingerprint;

                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(k =>
                {
                    k.AddServerHeader = false;
                    k.Limits.MaxConcurrentConnections = 12;
                    k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
                    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                    k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
                    k.Limits.MinRequestBodyDataRate = new MinDataRate(512, TimeSpan.FromSeconds(30));
                    k.Limits.MinResponseDataRate = new MinDataRate(512, TimeSpan.FromSeconds(30));
                    k.Listen(_o.Address, 0, l => l.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _cert,
                        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }));
                });
                var app = builder.Build();
                app.Run(Handle);
                _app = app;
                await app.StartAsync();

                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                var uri = new Uri(addresses.First().Replace("[::]", _o.Address.ToString()));
                _port = uri.Port;
                string key = _o.Security == PhoneSecurity.Encrypted ? "#k=" + PhoneCrypto.ToBase64Url(_key) : "";
                Url = $"https://{_o.Address}:{_port}/s/{_token}/{key}";

                Touch();
                _watchdog = new Timer(_ => Watchdog(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                SetState(PhoneState.WaitingForPhone);
            }
            catch
            {
                await CleanupAsync();
                SetState(PhoneState.Stopped);
                throw;
            }
        }

        public async Task StopAsync(string reason = "user")
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
            await CleanupAsync();
            lock (_lock)
            {
                foreach (var t in _transfers.Where(t => t.State is PhoneTransferState.Active or PhoneTransferState.Waiting))
                {
                    t.State = PhoneTransferState.Failed;
                    t.Problem = Loc.T("Str_SendToPhone_ProbStopped", "The session ended before it finished.");
                }
            }
            SetState(PhoneState.Stopped);
            Ended?.Invoke(reason);
        }

        private async Task CleanupAsync()
        {
            _watchdog?.Dispose();
            _watchdog = null;
            if (_app != null)
            {
                try { using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _app.StopAsync(wait.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException) { }
                try { await _app.DisposeAsync(); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException) { }
                _app = null;
            }
            foreach (var u in _uploads.Values) AbortUpload(u, deletePart: true, problem: null);
            _uploads.Clear();
            _cert?.Dispose();
            _cert = null;
            CryptographicOperations.ZeroMemory(_key);
            CryptographicOperations.ZeroMemory(_stateKey);
        }

        public async ValueTask DisposeAsync() => await StopAsync("user");

        private void Watchdog()
        {
            if (State == PhoneState.Stopped || _stopping == 1) return;
            long idle = Environment.TickCount64 - Interlocked.Read(ref _lastActivity);
            if (idle > _o.Idle.TotalMilliseconds && !HasActiveTransfer) { _ = StopAsync("idle"); return; }
            // an upload nobody has touched for two minutes is abandoned: remove its half file
            foreach (var u in _uploads.Values.Where(u => DateTime.UtcNow - u.Touched > TimeSpan.FromMinutes(2)))
                AbortUpload(u, deletePart: true, problem: Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short."));
        }

        // =================================================================
        //  Device approval
        // =================================================================

        public void Approve()
        {
            if (_pendingId == null) return;
            _approvedId = _pendingId;
            _pendingId = null;
            Touch();
            SetState(PhoneState.Connected);
        }

        public void Deny()
        {
            if (_pendingId == null) return;
            lock (_lock) _denied.Add(_pendingId);
            _pendingId = null;
            SetState(PhoneState.WaitingForPhone);
        }

        /// <summary>Forget the connected phone so another one can connect (its link and key stay the same).</summary>
        public void Disconnect()
        {
            if (_approvedId != null) lock (_lock) _denied.Add(_approvedId);
            _approvedId = null;
            _pendingId = null;
            SetState(PhoneState.WaitingForPhone);
        }

        // =================================================================
        //  What the PC offers
        // =================================================================

        public PhoneOffer? AddFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                lock (_lock)
                {
                    if (_offers.Any(o => string.Equals(o.Path, info.FullName, StringComparison.OrdinalIgnoreCase))) return null;
                    var offer = new PhoneOffer { Id = PhoneCrypto.NewHexId(16), Name = info.Name, Path = info.FullName, Size = info.Length, Modified = info.LastWriteTimeUtc };
                    _offers.Add(offer);
                    return offer;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        }

        public PhoneOffer? AddText(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > 64 * 1024) return null;
            lock (_lock)
            {
                var offer = new PhoneOffer { Id = PhoneCrypto.NewHexId(16), Name = text.Length > 40 ? text[..40] + "…" : text, Text = text, Size = Encoding.UTF8.GetByteCount(text) };
                _offers.Add(offer);
                return offer;
            }
        }

        public void RemoveOffer(string id) { lock (_lock) _offers.RemoveAll(o => o.Id == id); }
        public void ClearOffers() { lock (_lock) _offers.Clear(); }

        private PhoneOffer? FindOffer(string id) { lock (_lock) return _offers.FirstOrDefault(o => o.Id == id); }

        // =================================================================
        //  Requests
        // =================================================================

        private static readonly string[] BlockedExtensions =
        {
            ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".lnk", ".hta", ".cpl", ".jar", ".reg", ".dll", ".sys", ".msc", ".msp", ".pif", ".application", ".gadget", ".appx", ".msix",
        };

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>A name that is safe to create in the receive folder: no folders, no reserved or invalid characters.</summary>
        public static string SafeName(string? raw)
        {
            string name = (raw ?? "").Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name) sb.Append(char.IsControl(c) || bad.Contains(c) ? '_' : c);
            name = sb.ToString().Trim().TrimEnd('.', ' ');
            if (name.Length > 150)
            {
                string ext = Path.GetExtension(name);
                name = name[..(150 - Math.Min(ext.Length, 20))] + (ext.Length <= 20 ? ext : "");
            }
            string stem = Path.GetFileNameWithoutExtension(name);
            if (name.Length == 0 || stem.Length == 0 || ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "file" + (name.Length > 0 && name[0] == '.' ? name : "");
            return name;
        }

        private static bool IsPrivate(IPAddress? ip)
        {
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        private void AddFailure(string ip)
        {
            var now = DateTime.UtcNow;
            var f = _failures.AddOrUpdate(ip, (1, now, DateTime.MinValue), (_, old) =>
                now - old.Since > TimeSpan.FromMinutes(1) ? (1, now, old.BlockedUntil)
                : (old.Count + 1, old.Since, old.Count + 1 >= 10 ? now.AddMinutes(5) : old.BlockedUntil));
            if (Interlocked.Increment(ref _failuresTotal) >= 60) _ = StopAsync("failures");
        }

        private static void SecurityHeaders(HttpResponse res)
        {
            var h = res.Headers;
            h["Cache-Control"] = "no-store";
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            h["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        }

        private static async Task Reply(HttpContext ctx, int status, string text = "")
        {
            ctx.Response.StatusCode = status;
            if (text.Length > 0)
            {
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(text);
            }
        }

        private static async Task Json(HttpContext ctx, int status, object value)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(value));
        }

        private async Task Handle(HttpContext ctx)
        {
            SecurityHeaders(ctx.Response);
            var remote = ctx.Connection.RemoteIpAddress;
            string ip = remote?.ToString() ?? "?";
            if (!IsPrivate(remote)) { await Reply(ctx, 403); return; }
            if (_failures.TryGetValue(ip, out var f) && f.BlockedUntil > DateTime.UtcNow) { await Reply(ctx, 429); return; }

            // The Host header must be the address the page was opened with (blocks DNS-rebinding tricks).
            string host = ctx.Request.Host.Value ?? "";
            if (!string.Equals(host, $"{_o.Address}:{_port}", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(host, $"localhost:{_port}", StringComparison.OrdinalIgnoreCase))
            { AddFailure(ip); await Reply(ctx, 400); return; }

            string path = ctx.Request.Path.Value ?? "/";
            string prefix = "/s/" + _token;
            if (!path.StartsWith("/s/", StringComparison.Ordinal)) { AddFailure(ip); await Reply(ctx, 404); return; }
            string given = path.Length >= prefix.Length ? path.Substring(3, _token.Length) : "";
            string rest = path.Length > prefix.Length ? path[prefix.Length..] : "";
            if (!PhoneCrypto.SameText(given, _token) || (rest.Length > 0 && rest[0] != '/'))
            { AddFailure(ip); await Reply(ctx, 404); return; }

            Touch();
            try
            {
                string method = ctx.Request.Method;
                if (rest.Length == 0) { ctx.Response.Redirect(prefix + "/"); return; }
                if (method == "GET" && rest == "/") { await Asset(ctx, "index.html", "text/html; charset=utf-8"); return; }
                if (method == "GET" && rest == "/app.js") { await Asset(ctx, "app.js", "text/javascript; charset=utf-8"); return; }
                if (method == "GET" && rest == "/app.css") { await Asset(ctx, "app.css", "text/css; charset=utf-8"); return; }

                if (method == "POST" && rest == "/api/hello") { await Hello(ctx, ip); return; }

                // everything else needs the device the user approved
                string device = ctx.Request.Headers["X-Device"].FirstOrDefault() ?? ctx.Request.Query["d"].FirstOrDefault() ?? "";
                if (device.Length != 64) { AddFailure(ip); await Json(ctx, 403, new { status = "denied" }); return; }
                if (_approvedId == null || !PhoneCrypto.SameText(device, _approvedId))
                {
                    bool pending = _pendingId != null && PhoneCrypto.SameText(device, _pendingId);
                    if (pending && method == "GET" && rest == "/api/state") { await Json(ctx, 200, new { status = "pending" }); return; }
                    bool wasDenied; lock (_lock) wasDenied = _denied.Contains(device);
                    if (!pending) AddFailure(ip);
                    await Json(ctx, wasDenied ? 403 : 401, new { status = wasDenied ? "denied" : "pending" });
                    return;
                }

                if (method == "GET" && rest == "/api/state") { await State_(ctx); return; }
                if (method == "GET" && rest.StartsWith("/api/dl/")) { await DownloadChunk(ctx, rest[8..]); return; }
                if (method == "GET" && rest.StartsWith("/api/raw/")) { await DownloadRaw(ctx, rest[9..]); return; }
                if (method == "POST" && rest.StartsWith("/api/ul/")) { await UploadChunk(ctx, rest[8..]); return; }
                if (method == "POST" && rest == "/api/rawul") { await UploadRaw(ctx); return; }
                if (method == "POST" && rest == "/api/rawtext") { await UploadRawText(ctx); return; }
                await Reply(ctx, 404);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.AspNetCore.Http.BadHttpRequestException or UnauthorizedAccessException or JsonException or FormatException)
            {
                Log.Warn($"Send to Phone: request failed: {ex.Message}");
                if (!ctx.Response.HasStarted) await Reply(ctx, 400);
            }
        }

        private async Task Asset(HttpContext ctx, string name, string contentType)
        {
            byte[]? data;
            lock (_assets)
            {
                if (!_assets.TryGetValue(name, out data))
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SendToPhone." + name);
                    if (s == null) { data = null; }
                    else { using var ms = new MemoryStream(); s.CopyTo(ms); data = ms.ToArray(); _assets[name] = data; }
                }
            }
            if (data == null) { await Reply(ctx, 404); return; }
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength = data.Length;
            await ctx.Response.Body.WriteAsync(data);
        }

        private async Task Hello(HttpContext ctx, string ip)
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            string device = doc.RootElement.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
            string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (device.Length != 64 || !device.All(Uri.IsHexDigit)) { AddFailure(ip); await Reply(ctx, 400); return; }
            name = DescribeBrowser(name.Length > 400 ? name[..400] : name);

            bool wasDenied; lock (_lock) wasDenied = _denied.Contains(device);
            if (wasDenied) { await Json(ctx, 403, new { status = "denied" }); return; }
            if (_approvedId != null)
            {
                if (PhoneCrypto.SameText(device, _approvedId)) await Json(ctx, 200, new { status = "approved" });
                else await Json(ctx, 409, new { status = "busy" });
                return;
            }
            if (_pendingId != null)
            {
                if (PhoneCrypto.SameText(device, _pendingId)) await Json(ctx, 200, new { status = "pending" });
                else await Json(ctx, 409, new { status = "busy" });
                return;
            }
            _pendingId = device;
            SetState(PhoneState.WaitingForApproval);
            DeviceRequested?.Invoke(ip, name);
            await Json(ctx, 200, new { status = "pending" });
        }

        /// <summary>A short, safe label for the approval prompt ("Android · Chrome") from the browser's long user-agent text.</summary>
        public static string DescribeBrowser(string ua)
        {
            string os = ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
                : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
                : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
                : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
                : ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "Mac"
                : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux" : "";
            string browser = ua.Contains("Edg", StringComparison.Ordinal) ? "Edge"
                : ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
                : ua.Contains("SamsungBrowser", StringComparison.Ordinal) ? "Samsung Internet"
                : ua.Contains("Firefox", StringComparison.Ordinal) || ua.Contains("FxiOS", StringComparison.Ordinal) ? "Firefox"
                : ua.Contains("Chrome", StringComparison.Ordinal) || ua.Contains("CriOS", StringComparison.Ordinal) ? "Chrome"
                : ua.Contains("Safari", StringComparison.Ordinal) ? "Safari" : "";
            return string.Join(" · ", new[] { os, browser }.Where(x => x.Length > 0));
        }

        private async Task State_(HttpContext ctx)
        {
            List<PhoneOffer> offers = Offers.ToList();
            var items = offers.Select(o => new Dictionary<string, object?>
            {
                ["id"] = o.Id,
                ["name"] = o.Name,
                ["size"] = o.Size,
                ["kind"] = o.IsText ? "text" : "file",
                ["text"] = o.Text,
            }).ToList();
            long max = _o.MaxFileBytes;
            if (_o.Security == PhoneSecurity.Encrypted)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { items, maxUpload = max, chunk = PhoneCrypto.ChunkSize });
                var sealedState = PhoneCrypto.SealBlob(_stateKey, payload, Encoding.UTF8.GetBytes("state"));
                await Json(ctx, 200, new { status = "ok", mode = "e2ee", sealedState = Convert.ToBase64String(sealedState) });
            }
            else
            {
                await Json(ctx, 200, new { status = "ok", mode = "tls", items, maxUpload = max, chunk = PhoneCrypto.ChunkSize });
            }
        }

        // ---------------- downloads ----------------

        private PhoneTransfer BeginDownload(PhoneOffer offer)
        {
            var t = new PhoneTransfer { Id = PhoneCrypto.NewHexId(8), Name = offer.Name, Direction = PhoneDirection.ToPhone, Size = offer.Size, State = PhoneTransferState.Active };
            lock (_lock)
            {
                _transfers.Add(t);
                _downloads[offer.Id] = t;
            }
            TransferAdded?.Invoke(t);
            return t;
        }

        private static bool Unchanged(PhoneOffer offer, FileStream fs) => fs.Length == offer.Size && File.GetLastWriteTimeUtc(offer.Path!) == offer.Modified;

        private async Task DownloadChunk(HttpContext ctx, string tail)
        {
            if (_o.Security != PhoneSecurity.Encrypted) { await Reply(ctx, 404); return; }
            var parts = tail.Split('/');
            if (parts.Length != 2 || !long.TryParse(parts[1], out long n) || n < 0) { await Reply(ctx, 400); return; }
            var offer = FindOffer(parts[0]);
            if (offer == null || offer.IsText || offer.Path == null) { await Reply(ctx, 404); return; }

            long chunks = Math.Max(1, (offer.Size + PhoneCrypto.ChunkSize - 1) / PhoneCrypto.ChunkSize);
            if (n >= chunks) { await Reply(ctx, 404); return; }
            long offset = n * PhoneCrypto.ChunkSize;
            int length = (int)Math.Min(PhoneCrypto.ChunkSize, offer.Size - offset);
            byte[] plain = new byte[length];
            try
            {
                using var fs = new FileStream(offer.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                if (!Unchanged(offer, fs)) { FailDownload(offer, Loc.T("Str_SendToPhone_ProbChanged", "The file changed on this PC while it was being sent.")); await Reply(ctx, 409, "changed"); return; }
                fs.Position = offset;
                int got = 0;
                while (got < length)
                {
                    int r = await fs.ReadAsync(plain.AsMemory(got, length - got), ctx.RequestAborted);
                    if (r == 0) break;
                    got += r;
                }
                if (got != length) { FailDownload(offer, Loc.T("Str_SendToPhone_ProbChanged", "The file changed on this PC while it was being sent.")); await Reply(ctx, 409, "changed"); return; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailDownload(offer, ex.Message);
                await Reply(ctx, 410, "gone");
                return;
            }

            PhoneTransfer? t;
            lock (_lock) _downloads.TryGetValue(offer.Id, out t);
            if (n == 0 || t == null || t.State != PhoneTransferState.Active) t = BeginDownload(offer);
            bool last = n == chunks - 1;
            var key = PhoneCrypto.FileKey(_key, "dl", Convert.FromHexString(offer.Id));
            var sealedChunk = PhoneCrypto.SealChunk(key, Convert.FromHexString(offer.Id), n, last, offer.Size, plain);
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = sealedChunk.Length;
            await ctx.Response.Body.WriteAsync(sealedChunk, ctx.RequestAborted);
            t.Done = Math.Max(t.Done, offset + length);
            if (last) t.State = PhoneTransferState.Done;
        }

        private void FailDownload(PhoneOffer offer, string problem)
        {
            lock (_lock)
                if (_downloads.TryGetValue(offer.Id, out var t) && t.State == PhoneTransferState.Active)
                {
                    t.State = PhoneTransferState.Failed;
                    t.Problem = problem;
                }
        }

        private async Task DownloadRaw(HttpContext ctx, string id)
        {
            if (_o.Security != PhoneSecurity.TlsOnly) { await Reply(ctx, 404); return; }
            var offer = FindOffer(id);
            if (offer == null || offer.IsText || offer.Path == null) { await Reply(ctx, 404); return; }
            try
            {
                using var fs = new FileStream(offer.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!Unchanged(offer, fs)) { await Reply(ctx, 409, "changed"); return; }
                long size = fs.Length, start = 0, end = size - 1;
                int status = 200;
                string? range = ctx.Request.Headers.Range.FirstOrDefault();
                if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal) && !range.Contains(','))
                {
                    var bits = range[6..].Split('-');
                    long? a = long.TryParse(bits[0], out long av) ? av : null;
                    long? b = bits.Length > 1 && long.TryParse(bits[1], out long bv) ? bv : null;
                    if (a != null) { start = a.Value; if (b != null) end = Math.Min(b.Value, size - 1); }
                    else if (b != null) { start = Math.Max(0, size - b.Value); }
                    if (start > end || start >= size) { ctx.Response.Headers.ContentRange = $"bytes */{size}"; await Reply(ctx, 416); return; }
                    status = 206;
                    ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{size}";
                }
                long count = end - start + 1;
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength = count;
                ctx.Response.Headers.AcceptRanges = "bytes";
                ctx.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(offer.Name);

                PhoneTransfer? t = null;
                if (start == 0) t = BeginDownload(offer);
                fs.Position = start;
                var buffer = new byte[256 * 1024];
                long sent = 0;
                while (sent < count)
                {
                    int r = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - sent)), ctx.RequestAborted);
                    if (r == 0) break;
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, r), ctx.RequestAborted);
                    sent += r;
                    Touch();
                    if (t != null) t.Done = start + sent;
                }
                if (t != null) t.State = start + sent >= size ? PhoneTransferState.Done : PhoneTransferState.Failed;
                if (t != null && t.State == PhoneTransferState.Failed) t.Problem = Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short.");
            }
            catch (OperationCanceledException)
            {
                FailDownload(offer, Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short."));
            }
        }

        // ---------------- uploads ----------------

        private static bool IsHex(string s, int len) => s.Length == len && s.All(c => Uri.IsHexDigit(c));

        /// <summary>Why a file from the phone is refused, or null when it is fine.</summary>
        private string? Refuse(string name, long size)
        {
            if (_o.MaxFileBytes > 0 && size > _o.MaxFileBytes) return Loc.T("Str_SendToPhone_ProbTooBig", "It is larger than the limit you set.");
            if (_o.BlockPrograms && BlockedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                return Loc.T("Str_SendToPhone_ProbBlocked", "Programs and scripts are not accepted (you can allow them in the options).");
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(_o.ReceiveFolder)) ?? "";
                if (root.Length > 0 && new DriveInfo(root).AvailableFreeSpace < size + 64L * 1024 * 1024)
                    return Loc.T("Str_SendToPhone_ProbNoSpace", "There is not enough free space in the receive folder.");
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
            return null;
        }

        private Upload? StartUpload(string uid, string name, long size, bool isText, byte[]? key, out string? problem)
        {
            problem = null;
            if (_uploads.Count >= 3) { problem = "busy"; return null; }
            string safe = SafeName(name);
            if (isText)
            {
                if (size < 0 || size > 256 * 1024) { problem = Loc.T("Str_SendToPhone_ProbTooBig", "It is larger than the limit you set."); return null; }
            }
            else
            {
                problem = Refuse(safe, size);
                if (problem != null) return null;
            }
            var transfer = new PhoneTransfer { Id = uid, Name = isText ? Loc.T("Str_SendToPhone_TextFromPhone", "Text from the phone") : safe, Direction = PhoneDirection.FromPhone, IsText = isText, Size = size, State = PhoneTransferState.Active };
            var u = new Upload { Id = uid, IdBytes = Convert.FromHexString(uid), Key = key ?? Array.Empty<byte>(), Name = safe, Size = size, IsText = isText, Transfer = transfer };
            try
            {
                if (isText) u.TextBuffer = new MemoryStream();
                else
                {
                    Directory.CreateDirectory(_o.ReceiveFolder);
                    u.PartPath = Path.Combine(_o.ReceiveFolder, ".sendtophone-" + uid + ".part");
                    u.Stream = new FileStream(u.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problem = Loc.F("Str_SendToPhone_ProbCantWrite", "The receive folder cannot be written to: {0}", ex.Message);
                return null;
            }
            if (!_uploads.TryAdd(uid, u)) { u.Stream?.Dispose(); if (u.PartPath != null) TryDelete(u.PartPath); problem = "busy"; return null; }
            lock (_lock) _transfers.Add(transfer);
            TransferAdded?.Invoke(transfer);
            return u;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private void AbortUpload(Upload u, bool deletePart, string? problem)
        {
            if (!_uploads.TryRemove(u.Id, out _) && u.Stream == null && u.TextBuffer == null) return;
            try { u.Stream?.Dispose(); } catch (IOException) { }
            u.TextBuffer?.Dispose();
            if (deletePart && u.PartPath != null) TryDelete(u.PartPath);
            if (u.Transfer.State == PhoneTransferState.Active)
            {
                u.Transfer.State = PhoneTransferState.Failed;
                u.Transfer.Problem = problem ?? Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short.");
            }
            CryptographicOperations.ZeroMemory(u.Key);
        }

        private void CompleteUpload(Upload u)
        {
            _uploads.TryRemove(u.Id, out _);
            if (u.IsText)
            {
                u.Transfer.Text = Encoding.UTF8.GetString(u.TextBuffer!.ToArray());
                u.TextBuffer.Dispose();
                u.Transfer.Done = u.Size;
                u.Transfer.State = PhoneTransferState.Done;
                CryptographicOperations.ZeroMemory(u.Key);
                return;
            }
            u.Stream!.Flush(true);
            u.Stream.Dispose();
            string target = "";
            for (int attempt = 0; attempt < 10; attempt++)
            {
                target = SafeFileOps.UniquePath(Path.Combine(_o.ReceiveFolder, u.Name));
                try { File.Move(u.PartPath!, target, overwrite: false); break; }
                catch (IOException) when (attempt < 9) { /* someone took that name a moment ago: pick the next */ }
            }
            u.Transfer.SavedPath = target;
            u.Transfer.Done = u.Size;
            u.Transfer.State = PhoneTransferState.Done;
            CryptographicOperations.ZeroMemory(u.Key);
        }

        private async Task UploadChunk(HttpContext ctx, string tail)
        {
            if (_o.Security != PhoneSecurity.Encrypted) { await Reply(ctx, 404); return; }
            var parts = tail.Split('/');
            if (parts.Length != 2 || !IsHex(parts[0], 32) || !long.TryParse(parts[1], out long n) || n < 0) { await Reply(ctx, 400); return; }
            string uid = parts[0];
            bool last = ctx.Request.Headers["X-Final"].FirstOrDefault() == "1";
            var maxBody = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxBody is { IsReadOnly: false }) maxBody.MaxRequestBodySize = PhoneCrypto.ChunkSize + PhoneCrypto.TagSize;

            byte[] body;
            using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                body = ms.ToArray();
            }
            if (body.Length > PhoneCrypto.ChunkSize + PhoneCrypto.TagSize) { await Reply(ctx, 413); return; }

            Upload? u;
            if (n == 0)
            {
                string? metaB64 = ctx.Request.Headers["X-Meta"].FirstOrDefault();
                if (string.IsNullOrEmpty(metaB64) || metaB64.Length > 4096) { await Reply(ctx, 400); return; }
                var idBytes = Convert.FromHexString(uid);
                var key = PhoneCrypto.FileKey(_key, "ul", idBytes);
                byte[]? metaPlain;
                try { metaPlain = PhoneCrypto.OpenBlob(key, Convert.FromBase64String(metaB64), Encoding.UTF8.GetBytes("meta" + uid)); }
                catch (FormatException) { metaPlain = null; }
                if (metaPlain == null) { await Json(ctx, 400, new { error = "tampered" }); return; }
                using var meta = JsonDocument.Parse(metaPlain);
                string name = meta.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                long size = meta.RootElement.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long sv) ? sv : -1;
                bool isText = meta.RootElement.TryGetProperty("kind", out var kd) && kd.GetString() == "text";
                if (size < 0) { await Reply(ctx, 400); return; }
                u = StartUpload(uid, name, size, isText, key, out string? problem);
                if (u == null) { await Json(ctx, problem == "busy" ? 429 : 413, new { error = problem ?? "refused" }); return; }
            }
            else if (!_uploads.TryGetValue(uid, out u)) { await Reply(ctx, 404); return; }

            await u.Gate.WaitAsync(ctx.RequestAborted);
            try
            {
                u.Touched = DateTime.UtcNow;
                if (n != u.Next) { await Json(ctx, 409, new { error = "order", expected = u.Next }); return; }
                var plain = PhoneCrypto.OpenChunk(u.Key, u.IdBytes, n, last, u.Size, body);
                if (plain == null)
                {
                    AbortUpload(u, true, Loc.T("Str_SendToPhone_ProbTampered", "It failed the security check: the data was changed or the key is wrong."));
                    await Json(ctx, 400, new { error = "tampered" });
                    return;
                }
                if (u.Received + plain.Length > u.Size || (plain.Length < PhoneCrypto.ChunkSize && !last && u.Size > 0))
                {
                    AbortUpload(u, true, Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short."));
                    await Json(ctx, 400, new { error = "size" });
                    return;
                }
                if (u.IsText) u.TextBuffer!.Write(plain); else await u.Stream!.WriteAsync(plain, ctx.RequestAborted);
                u.Received += plain.Length;
                u.Next++;
                u.Transfer.Done = u.Received;
                if (last)
                {
                    if (u.Received != u.Size)
                    {
                        AbortUpload(u, true, Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short."));
                        await Json(ctx, 400, new { error = "size" });
                        return;
                    }
                    CompleteUpload(u);
                }
                await Json(ctx, 200, new { ok = true, done = last });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AbortUpload(u, true, ex.Message);
                await Json(ctx, 500, new { error = "write" });
            }
            finally
            {
                u.Gate.Release();
            }
        }

        private async Task UploadRaw(HttpContext ctx)
        {
            if (_o.Security != PhoneSecurity.TlsOnly) { await Reply(ctx, 404); return; }
            string uid = ctx.Request.Query["uid"].FirstOrDefault() ?? "";
            string name = ctx.Request.Query["name"].FirstOrDefault() ?? "";
            long size = ctx.Request.ContentLength ?? -1;
            if (!IsHex(uid, 32) || size < 0) { await Reply(ctx, 400); return; }
            var maxBody = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxBody is { IsReadOnly: false }) maxBody.MaxRequestBodySize = _o.MaxFileBytes > 0 ? _o.MaxFileBytes : null;

            var u = StartUpload(uid, name, size, false, null, out string? problem);
            if (u == null) { await Json(ctx, problem == "busy" ? 429 : 413, new { error = problem ?? "refused" }); return; }
            try
            {
                var buffer = new byte[256 * 1024];
                while (u.Received < size)
                {
                    int r = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - u.Received)), ctx.RequestAborted);
                    if (r == 0) break;
                    await u.Stream!.WriteAsync(buffer.AsMemory(0, r), ctx.RequestAborted);
                    u.Received += r;
                    u.Touched = DateTime.UtcNow;
                    u.Transfer.Done = u.Received;
                    Touch();
                }
                if (u.Received != size)
                {
                    AbortUpload(u, true, Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short."));
                    await Json(ctx, 400, new { error = "size" });
                    return;
                }
                CompleteUpload(u);
                await Json(ctx, 200, new { ok = true });
            }
            catch (OperationCanceledException)
            {
                AbortUpload(u, true, Loc.T("Str_SendToPhone_ProbIncomplete", "The transfer was cut short."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                AbortUpload(u, true, ex.Message);
                if (!ctx.Response.HasStarted) await Json(ctx, 500, new { error = "write" });
            }
        }

        private async Task UploadRawText(HttpContext ctx)
        {
            if (_o.Security != PhoneSecurity.TlsOnly) { await Reply(ctx, 404); return; }
            long size = ctx.Request.ContentLength ?? -1;
            if (size < 0 || size > 256 * 1024) { await Reply(ctx, 413); return; }
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var t = new PhoneTransfer
            {
                Id = PhoneCrypto.NewHexId(8), Name = Loc.T("Str_SendToPhone_TextFromPhone", "Text from the phone"), Direction = PhoneDirection.FromPhone,
                IsText = true, Size = ms.Length, Done = ms.Length, State = PhoneTransferState.Done, Text = Encoding.UTF8.GetString(ms.ToArray()),
            };
            lock (_lock) _transfers.Add(t);
            TransferAdded?.Invoke(t);
            await Json(ctx, 200, new { ok = true });
        }
    }
}
