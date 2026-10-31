using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind ApiTesterView: sends REST requests, runs the WebSocket
    /// client, keeps the request history, exports logs, and persists saved
    /// endpoints. Has no knowledge of WPF controls - the view passes in
    /// ApiModel types and renders what comes back.
    ///
    /// Threading: create this on the UI thread. Events are raised back on
    /// the thread that created the manager (captured SynchronizationContext),
    /// so handlers can touch controls directly without Dispatcher calls.
    /// </summary>
    public sealed class ApiManager : IDisposable
    {
        // One HttpClient for the whole app - creating one per request
        // exhausts sockets under load. Timeouts are applied per request
        // (see SendAsync) so a timeout can be told apart from a user cancel.
        private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

        public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

        private const int MaxHistoryEntries = 1000;
        private const int MaxReconnectAttempts = 5;
        private const int ReceiveBufferSize = 8 * 1024;

        private static readonly string[] MethodsWithBody = { "POST", "PUT", "PATCH", "DELETE" };

        private SynchronizationContext? _uiContext;
        private readonly List<ApiHistoryEntry> _history = new();
        private readonly List<WebSocketLogEntry> _webSocketLog = new();

        private CancellationTokenSource? _requestCts;

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _socketCts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private string _socketUrl = "";
        private bool _autoReconnect;
        private bool _disposed;

        public ApiManager()
        {
            _uiContext = SynchronizationContext.Current;
        }

        // =================================================================
        //  Events (raised on the creating thread)
        // =================================================================

        /// <summary>WebSocket state changed; string is a short human-readable detail.</summary>
        public event Action<ApiConnectionState, string>? ConnectionStateChanged;

        /// <summary>A line was added to the WebSocket traffic log.</summary>
        public event Action<WebSocketLogEntry>? WebSocketMessageLogged;

        public ApiConnectionState ConnectionState { get; private set; } = ApiConnectionState.Idle;
        public bool IsWebSocketConnected => _socket?.State == WebSocketState.Open;
        public bool IsRequestInFlight => _requestCts != null;

        public IReadOnlyList<ApiHistoryEntry> History => _history;
        public IReadOnlyList<WebSocketLogEntry> WebSocketLog => _webSocketLog;

        // =================================================================
        //  REST
        // =================================================================

        /// <summary>
        /// Resolves the typed URL against the base URL and appends enabled
        /// query params. Returns false with a user-facing message when the
        /// result isn't an absolute http(s) URL.
        /// </summary>
        public static bool TryBuildUrl(
            string url, string? baseUrl, IEnumerable<KeyValueRow> queryParams,
            out string fullUrl, out string error)
        {
            fullUrl = "";
            error = "";
            url = (url ?? "").Trim();

            if (url.Length == 0)
            {
                error = "Enter a URL before sending.";
                return false;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                string trimmedBase = (baseUrl ?? "").Trim().TrimEnd('/');
                if (trimmedBase.Length == 0)
                {
                    // Friendlier than failing: "api.example.com/x" -> https://...
                    url = url.Contains("://") ? url : "https://" + url.TrimStart('/');
                }
                else
                {
                    url = trimmedBase + "/" + url.TrimStart('/');
                }
            }

            var active = queryParams.Where(p => p.IsActive).ToList();
            if (active.Count > 0)
            {
                string query = string.Join("&", active.Select(p =>
                    $"{Uri.EscapeDataString(p.Key.Trim())}={Uri.EscapeDataString(p.Value ?? "")}"));
                url += (url.Contains('?') ? "&" : "?") + query;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "That doesn't look like a valid http(s) URL.";
                return false;
            }

            fullUrl = uri.AbsoluteUri;
            return true;
        }

        /// <summary>
        /// Sends the request. Never throws for network problems - failures
        /// come back as an ApiResponse with ErrorKind set. Starting a new
        /// request cancels any request still in flight.
        /// </summary>
        public async Task<ApiResponse> SendAsync(ApiRequest request, TimeSpan? timeout = null)
        {
            if (!TryBuildUrl(request.Url, request.BaseUrl, request.QueryParams, out string url, out string urlError))
            {
                return new ApiResponse { ErrorKind = ApiErrorKind.InvalidUrl, ErrorMessage = urlError };
            }

            CancelRequest();
            var userCts = new CancellationTokenSource();
            _requestCts = userCts;

            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultRequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, timeoutCts.Token);

            var stopwatch = Stopwatch.StartNew();
            ApiResponse result;

            try
            {
                using var message = BuildHttpRequest(request, url);
                using var response = await SharedHttpClient.SendAsync(
                    message, HttpCompletionOption.ResponseContentRead, linkedCts.Token);

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token);
                stopwatch.Stop();

                result = new ApiResponse
                {
                    RequestUrl = url,
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase ?? "",
                    Body = DecodeBody(bytes, response.Content.Headers.ContentType?.CharSet),
                    HeadersText = FormatHeaders(response),
                    ContentType = response.Content.Headers.ContentType?.MediaType,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    SizeBytes = bytes.LongLength,
                };
            }
            catch (OperationCanceledException) when (userCts.IsCancellationRequested)
            {
                result = Failure(url, stopwatch, ApiErrorKind.Cancelled, "Request cancelled");
            }
            catch (OperationCanceledException)
            {
                result = Failure(url, stopwatch, ApiErrorKind.Timeout,
                    $"Request timed out after {(timeout ?? DefaultRequestTimeout).TotalSeconds:0}s");
            }
            catch (HttpRequestException ex)
            {
                result = Failure(url, stopwatch, ApiErrorKind.ConnectionFailed, $"Connection failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                result = Failure(url, stopwatch, ApiErrorKind.Unexpected, $"Error: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_requestCts, userCts))
                    _requestCts = null;
                userCts.Dispose();
            }

            AddHistory(request.Method, result);
            return result;
        }

        /// <summary>Cancels the in-flight REST request, if any.</summary>
        public void CancelRequest()
        {
            try { _requestCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void ClearHistory() => _history.Clear();

        private static HttpRequestMessage BuildHttpRequest(ApiRequest request, string url)
        {
            string method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim().ToUpperInvariant();
            var message = new HttpRequestMessage(new HttpMethod(method), url);

            if (MethodsWithBody.Contains(method) && !string.IsNullOrEmpty(request.Body))
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8, ContentTypeFor(request.BodyFormat));
            }

            foreach (var header in request.Headers.Where(h => h.IsActive))
            {
                string name = header.Key.Trim();
                string value = header.Value ?? "";

                // Content-Type / Content-Length etc. are CONTENT headers in
                // .NET - adding them to message.Headers silently fails, which
                // is why a user-entered Content-Type used to be ignored.
                if (!message.Headers.TryAddWithoutValidation(name, value) && message.Content != null)
                {
                    message.Content.Headers.Remove(name);
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            ApplyAuth(message, request.Auth);
            return message;
        }

        private static void ApplyAuth(HttpRequestMessage message, ApiAuthSettings auth)
        {
            switch (auth.Type)
            {
                case ApiAuthType.Basic:
                    string creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"));
                    message.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
                    break;

                case ApiAuthType.Bearer:
                    // An empty token would throw inside AuthenticationHeaderValue - just skip it.
                    if (!string.IsNullOrWhiteSpace(auth.Token))
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token.Trim());
                    break;

                case ApiAuthType.ApiKey:
                    if (!string.IsNullOrWhiteSpace(auth.KeyName))
                    {
                        message.Headers.Remove(auth.KeyName.Trim());
                        message.Headers.TryAddWithoutValidation(auth.KeyName.Trim(), auth.KeyValue ?? "");
                    }
                    break;
            }
        }

        private static string ContentTypeFor(ApiBodyFormat format) => format switch
        {
            ApiBodyFormat.Json => "application/json",
            ApiBodyFormat.Xml => "application/xml",
            ApiBodyFormat.FormData => "application/x-www-form-urlencoded",
            _ => "text/plain",
        };

        private static string DecodeBody(byte[] bytes, string? charset)
        {
            Encoding encoding = Encoding.UTF8;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
                catch (ArgumentException) { /* unknown charset - fall back to UTF-8 */ }
            }
            return encoding.GetString(bytes);
        }

        private static string FormatHeaders(HttpResponseMessage response)
        {
            var sb = new StringBuilder();
            foreach (var header in response.Headers)
                sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            foreach (var header in response.Content.Headers)
                sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            return sb.ToString();
        }

        private static ApiResponse Failure(string url, Stopwatch sw, ApiErrorKind kind, string message)
        {
            sw.Stop();
            return new ApiResponse
            {
                RequestUrl = url,
                ErrorKind = kind,
                ErrorMessage = message,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }

        private void AddHistory(string method, ApiResponse response)
        {
            if (response.ErrorKind == ApiErrorKind.InvalidUrl)
                return;

            _history.Add(new ApiHistoryEntry(
                DateTime.Now, method, response.RequestUrl, response.StatusCode,
                response.StatusText, response.ElapsedMs, response.SizeBytes));

            if (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);
        }

        // =================================================================
        //  Formatting helpers
        // =================================================================

        /// <summary>Indents JSON; returns the input unchanged if it isn't JSON.</summary>
        public static string TryPrettyPrintJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        /// <summary>
        /// Theme resource keys (background, foreground) for a status code:
        /// green 2xx, blue 3xx, amber 4xx, red 5xx / no response.
        /// </summary>
        public static (string BackgroundKey, string ForegroundKey) GetStatusColorKeys(int? statusCode) => statusCode switch
        {
            null => ("StatusErrorBg", "StatusErrorFg"),
            >= 200 and < 300 => ("StatusSuccessBg", "StatusSuccessFg"),
            >= 300 and < 400 => ("StatusInfoBg", "StatusInfoFg"),
            >= 400 and < 500 => ("StatusWarningBg", "StatusWarningFg"),
            >= 500 and < 600 => ("StatusErrorBg", "StatusErrorFg"),
            _ => ("RowBg", "TextPrimary"),
        };

        /// <summary>Theme foreground key used to colour-code an HTTP method.</summary>
        public static string GetMethodColorKey(string? method) => (method ?? "").ToUpperInvariant() switch
        {
            "GET" => "StatusSuccessFg",
            "POST" => "StatusInfoFg",
            "PUT" => "StatusWarningFg",
            "PATCH" => "AccentColor",
            "DELETE" => "StatusErrorFg",
            _ => "TextPrimary",
        };

        public static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes:N0} bytes",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
        };

        // =================================================================
        //  WebSocket
        // =================================================================

        public static bool TryValidateWebSocketUrl(string url, out Uri? uri, out string error)
        {
            error = "";
            url = (url ?? "").Trim();
            if (url.Length == 0)
            {
                uri = null;
                error = "Enter a WebSocket URL before connecting.";
                return false;
            }

            if (!url.Contains("://"))
                url = "wss://" + url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
            {
                uri = null;
                error = "WebSocket URLs must start with ws:// or wss://.";
                return false;
            }
            return true;
        }

        /// <summary>Opens the socket and starts the receive loop. Disconnects any existing socket first.</summary>
        public async Task<bool> ConnectWebSocketAsync(string url, bool autoReconnect)
        {
            if (!TryValidateWebSocketUrl(url, out var uri, out string error))
            {
                SetState(ApiConnectionState.Failed, error);
                return false;
            }

            // Fallback if the manager was constructed before WPF installed its
            // SynchronizationContext - connect is always called from the UI.
            _uiContext ??= SynchronizationContext.Current;

            await DisconnectWebSocketAsync(logIt: false);

            _socketUrl = uri!.AbsoluteUri;
            _autoReconnect = autoReconnect;
            _socketCts = new CancellationTokenSource();

            bool ok = await OpenSocketAsync(_socketCts.Token, isReconnect: false);
            if (ok)
                _ = ReceiveLoopAsync(_socket!, _socketCts.Token);
            return ok;
        }

        /// <summary>Closes the socket on purpose (no auto-reconnect).</summary>
        public Task DisconnectWebSocketAsync() => DisconnectWebSocketAsync(logIt: true);

        private async Task DisconnectWebSocketAsync(bool logIt)
        {
            var socket = _socket;
            var cts = _socketCts;
            _socket = null;
            _socketCts = null;

            cts?.Cancel();

            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by user", closeTimeout.Token);
                    }
                }
                catch { /* already gone - nothing useful to report */ }
                finally
                {
                    socket.Dispose();
                }

                if (logIt)
                {
                    Log(WebSocketDirection.System, "Disconnected");
                    SetState(ApiConnectionState.Disconnected, "Disconnected");
                }
            }

            cts?.Dispose();
        }

        /// <summary>Sends a text frame. Returns false (with a log line) if not connected.</summary>
        public async Task<bool> SendWebSocketMessageAsync(string text)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                Log(WebSocketDirection.System, "Not connected - message not sent.");
                return false;
            }

            // ClientWebSocket allows only one SendAsync at a time.
            await _sendLock.WaitAsync();
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true,
                    _socketCts?.Token ?? CancellationToken.None);
                Log(WebSocketDirection.Out, text);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                Log(WebSocketDirection.System, $"Send failed: {ex.Message}");
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void ClearWebSocketLog() => _webSocketLog.Clear();

        private async Task<bool> OpenSocketAsync(CancellationToken token, bool isReconnect)
        {
            SetState(isReconnect ? ApiConnectionState.Reconnecting : ApiConnectionState.Connecting,
                     isReconnect ? "Reconnecting..." : "Connecting...");

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connectTimeout.Token);
                await socket.ConnectAsync(new Uri(_socketUrl), linked.Token);

                _socket = socket;
                Log(WebSocketDirection.System, $"Connected to {_socketUrl}");
                SetState(ApiConnectionState.Connected, "Connected");
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ArgumentException)
            {
                socket.Dispose();
                if (token.IsCancellationRequested)
                    return false;

                string reason = ex is OperationCanceledException ? "timed out" : ex.Message;
                Log(WebSocketDirection.System, $"Connection failed: {reason}");
                SetState(ApiConnectionState.Failed, "Connection failed");
                return false;
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[ReceiveBufferSize];
            using var message = new MemoryStream();
            string? closeReason = null;

            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeReason = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                            ? $"Server closed the connection ({result.CloseStatus})"
                            : $"Server closed the connection: {result.CloseStatusDescription}";
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;

                    string text = result.MessageType == WebSocketMessageType.Binary
                        ? $"[binary message, {FormatBytes(message.Length)}]"
                        : Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

                    message.SetLength(0);
                    Log(WebSocketDirection.In, text);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return; // user disconnected - DisconnectWebSocketAsync reports it
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                closeReason = $"Connection lost: {ex.Message}";
            }

            if (token.IsCancellationRequested)
                return;

            Log(WebSocketDirection.System, closeReason ?? "Connection closed");

            if (ReferenceEquals(_socket, socket))
                _socket = null;
            socket.Dispose();

            if (_autoReconnect)
                await ReconnectWithBackoffAsync(token);
            else
                SetState(ApiConnectionState.Disconnected, "Disconnected");
        }

        /// <summary>Retries 1s, 2s, 4s, 8s, 16s after an UNEXPECTED close.</summary>
        private async Task ReconnectWithBackoffAsync(CancellationToken token)
        {
            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                Log(WebSocketDirection.System, $"Reconnecting in {delay.TotalSeconds:0}s (attempt {attempt}/{MaxReconnectAttempts})...");
                SetState(ApiConnectionState.Reconnecting, $"Reconnecting ({attempt}/{MaxReconnectAttempts})");

                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { return; }

                if (await OpenSocketAsync(token, isReconnect: true))
                {
                    _ = ReceiveLoopAsync(_socket!, token);
                    return;
                }
            }

            Log(WebSocketDirection.System, "Gave up reconnecting.");
            SetState(ApiConnectionState.Failed, "Disconnected");
        }

        private void Log(WebSocketDirection direction, string text)
        {
            var entry = new WebSocketLogEntry { Timestamp = DateTime.Now, Direction = direction, Message = text };
            Post(() =>
            {
                _webSocketLog.Add(entry);
                WebSocketMessageLogged?.Invoke(entry);
            });
        }

        private void SetState(ApiConnectionState state, string detail)
        {
            Post(() =>
            {
                ConnectionState = state;
                ConnectionStateChanged?.Invoke(state, detail);
            });
        }

        /// <summary>Runs on the UI thread (or inline if already there / no context).</summary>
        private void Post(Action action)
        {
            if (_disposed)
                return;

            if (_uiContext == null || SynchronizationContext.Current == _uiContext)
                action();
            else
                _uiContext.Post(_ => { if (!_disposed) action(); }, null);
        }

        // =================================================================
        //  Export
        // =================================================================

        public static string FileFilterFor(ApiExportFormat format) => format switch
        {
            ApiExportFormat.Json => "JSON file (*.json)|*.json",
            ApiExportFormat.Xml => "XML file (*.xml)|*.xml",
            _ => "CSV file (*.csv)|*.csv",
        };

        public static string ExtensionFor(ApiExportFormat format) => format switch
        {
            ApiExportFormat.Json => ".json",
            ApiExportFormat.Xml => ".xml",
            _ => ".csv",
        };

        /// <summary>Writes the REST history. Returns the number of rows written.</summary>
        public int ExportHistory(string path, ApiExportFormat format)
        {
            var rows = _history.ToList();
            string[] columns = { "Timestamp", "Method", "Url", "StatusCode", "Status", "ElapsedMs", "SizeBytes" };

            WriteExport(path, format, "ApiHistory", "Request", columns, rows.Select(h => new object?[]
            {
                h.Timestamp.ToString("o"), h.Method, h.Url, h.StatusCode, h.Status, h.ElapsedMs, h.SizeBytes,
            }));
            return rows.Count;
        }

        /// <summary>Writes the WebSocket traffic log. Returns the number of rows written.</summary>
        public int ExportWebSocketLog(string path, ApiExportFormat format)
        {
            var rows = _webSocketLog.ToList();
            string[] columns = { "Timestamp", "Direction", "Message" };

            WriteExport(path, format, "WebSocketLog", "Message", columns, rows.Select(e => new object?[]
            {
                e.Timestamp.ToString("o"), e.DirectionText, e.Message,
            }));
            return rows.Count;
        }

        private static void WriteExport(
            string path, ApiExportFormat format, string rootName, string rowName,
            string[] columns, IEnumerable<object?[]> rows)
        {
            switch (format)
            {
                case ApiExportFormat.Json:
                    var list = rows.Select(r => columns.Zip(r).ToDictionary(p => p.First, p => p.Second)).ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                    break;

                case ApiExportFormat.Xml:
                    var doc = new XDocument(new XElement(rootName,
                        rows.Select(r => new XElement(rowName,
                            columns.Zip(r).Select(p => new XElement(p.First, p.Second?.ToString() ?? ""))))));
                    doc.Save(path);
                    break;

                default:
                    var sb = new StringBuilder();
                    sb.AppendLine(string.Join(",", columns));
                    foreach (var r in rows)
                        sb.AppendLine(string.Join(",", r.Select(v => CsvEscape(v?.ToString()))));
                    // BOM so Excel opens non-ASCII (e.g. response text) correctly.
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    break;
            }
        }

        private static string CsvEscape(string? value)
        {
            value ??= "";
            bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        // =================================================================
        //  Saved endpoints (persisted - Method + URL only, never secrets)
        // =================================================================

        private static string SavedEndpointsPath => Path.Combine(
            AppPaths.DataRoot, "api-endpoints.json");

        public static List<SavedEndpoint> LoadSavedEndpoints()
        {
            try
            {
                if (!File.Exists(SavedEndpointsPath))
                    return new List<SavedEndpoint>();

                return JsonSerializer.Deserialize<List<SavedEndpoint>>(File.ReadAllText(SavedEndpointsPath))
                       ?? new List<SavedEndpoint>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt/locked file shouldn't stop the tool from opening.
                return new List<SavedEndpoint>();
            }
        }

        public static void SaveSavedEndpoints(IEnumerable<SavedEndpoint> endpoints)
        {
            try
            {
                // Through a temp file: a crash mid-save used to leave a truncated list that then failed to load.
                Smart_Tools.WPF.Controllers.AtomicFile.WriteAllText(SavedEndpointsPath,
                    JsonSerializer.Serialize(endpoints.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: the list still works for this session.
            }
        }

        /// <summary>True if an endpoint with the same method + URL already exists.</summary>
        public static bool ContainsEndpoint(IEnumerable<SavedEndpoint> endpoints, string method, string url) =>
            endpoints.Any(ep =>
                string.Equals(ep.Method, method, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ep.Url, url, StringComparison.OrdinalIgnoreCase));

        // =================================================================
        //  Cleanup
        // =================================================================

        public void Dispose()
        {
            if (_disposed)
                return;

            CancelRequest();
            _socketCts?.Cancel();
            try { _socket?.Abort(); } catch { }
            _socket?.Dispose();
            _socket = null;
            _disposed = true;
            _sendLock.Dispose();
        }
    }
}
