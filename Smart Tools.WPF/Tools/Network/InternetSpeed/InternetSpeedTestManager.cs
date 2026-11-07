using Smart_Tools.WPF.Models;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Internet Speed Test backend, split in two:
    /// <list type="bullet">
    /// <item>InternetSpeedTestManager.cs (this file) - servers, the maths
    /// (median, jitter, steady speed), what the numbers mean, history and
    /// settings.</item>
    /// <item>InternetSpeedTestManager.Engine.cs - the measurements
    /// (<see cref="SpeedTestEngine"/>).</item>
    /// </list>
    /// </summary>
    public static partial class InternetSpeedTestManager
    {
        // =================================================================
        //  Servers
        // =================================================================

        /// <summary>
        /// Cloudflare picks its nearest data centre and does download and
        /// upload. Hetzner's file hosts are download-only fallbacks. The old
        /// list used speed.hetzner.de; Hetzner's test files now live on
        /// fsn1-speed / hel1-speed.hetzner.com.
        /// </summary>
        public static readonly IReadOnlyList<SpeedTestServer> Servers = new[]
        {
            new SpeedTestServer
            {
                Id = "cloudflare",
                Name = "Cloudflare",
                Location = "nearest to you",
                DownloadUrl = "https://speed.cloudflare.com/__down?bytes={0}",
                UploadUrl = "https://speed.cloudflare.com/__up",
                LatencyUrl = "https://speed.cloudflare.com/__down?bytes=0",
                PingHost = "speed.cloudflare.com",
            },
            new SpeedTestServer
            {
                Id = "hetzner-fsn",
                Name = "Hetzner",
                Location = "Falkenstein, Germany · download only",
                DownloadUrl = "https://fsn1-speed.hetzner.com/1GB.bin",
                LatencyUrl = "https://fsn1-speed.hetzner.com/",
                PingHost = "fsn1-speed.hetzner.com",
            },
            new SpeedTestServer
            {
                Id = "hetzner-hel",
                Name = "Hetzner",
                Location = "Helsinki, Finland · download only",
                DownloadUrl = "https://hel1-speed.hetzner.com/1GB.bin",
                LatencyUrl = "https://hel1-speed.hetzner.com/",
                PingHost = "hel1-speed.hetzner.com",
            },
        };

        public static SpeedTestServer ServerById(string? id) => Servers.FirstOrDefault(s => s.Id == id) ?? Servers[0];

        public static readonly IReadOnlyList<int> DurationChoices = new[] { 5, 10, 15 };
        public static readonly IReadOnlyList<int> DataLimitChoices = new[] { 0, 100, 250, 1000 };

        /// <summary>Size of each download request ({0}) and upload request; workers keep asking until time is up.</summary>
        public const long DownloadChunkBytes = 25_000_000;

        /// <summary>Upload requests start small and grow while they finish quickly, so slow lines still complete some and fast lines aren't slowed by tiny requests.</summary>
        public const long FirstUploadChunkBytes = 256_000;
        public const long MaxUploadChunkBytes = 8_000_000;

        public static long NextUploadChunk(long current, TimeSpan took) =>
            took < TimeSpan.FromSeconds(1) ? Math.Min(MaxUploadChunkBytes, current * 2) : current;

        // =================================================================
        //  Progress percent layout (shared with the live chart)
        // =================================================================
        //
        // The whole run is reported as one 0-100 progress value: ping takes
        // the first LatencySharePercent, then download and (when measured)
        // upload split what's left. These are fixed shares of the overall
        // run, not proportions measured live, so the boundary between
        // download and upload on the chart can be drawn once, up front, and
        // never has to move once real samples start arriving.

        public const double LatencySharePercent = 10;
        private const double DownloadShareWithUpload = 0.45, DownloadShareDownloadOnly = 0.9;

        /// <summary>Overall progress (0-100) where download ends and upload begins (or the run ends, if upload isn't measured).</summary>
        public static double DownloadEndPercent(bool uploadPlanned) =>
            LatencySharePercent + (uploadPlanned ? DownloadShareWithUpload : DownloadShareDownloadOnly) * 100;

        /// <summary>The URL for one download request: the size filled in, plus a random part so no proxy serves it from a cache.</summary>
        public static string DownloadRequestUrl(SpeedTestServer server, long bytes) =>
            CacheBust(server.DownloadUrl.Contains("{0}") ? string.Format(server.DownloadUrl, bytes) : server.DownloadUrl);

        public static string CacheBust(string url) => url + (url.Contains('?') ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N")[..12];

        // =================================================================
        //  Maths
        // =================================================================

        public static double Median(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return double.NaN;
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        /// <summary>Jitter: the average change between one round trip and the next.</summary>
        public static double Jitter(IReadOnlyList<double> values)
        {
            if (values.Count < 2) return 0;
            double sum = 0;
            for (int i = 1; i < values.Count; i++) sum += Math.Abs(values[i] - values[i - 1]);
            return sum / (values.Count - 1);
        }

        public static LatencyResult? Summarise(IReadOnlyList<double> rtts, string method) =>
            rtts.Count == 0 ? null : new LatencyResult(Median(rtts), Jitter(rtts), rtts.Min(), rtts.Count, method);

        public static double ToMbps(long bytes, double seconds) => seconds <= 0 ? 0 : bytes * 8.0 / seconds / 1_000_000.0;

        /// <summary>
        /// The speed once the connection has got going: the first quarter of
        /// the test (TCP ramp-up) is left out. The old test timed one fixed
        /// download from the first byte, ramp-up included, which reads low
        /// on fast lines.
        /// </summary>
        public static double SteadyMbps(IReadOnlyList<(double Seconds, long Bytes)> samples)
        {
            if (samples.Count == 0) return 0;
            var last = samples[^1];
            double warm = last.Seconds * 0.25;
            var start = samples.FirstOrDefault(s => s.Seconds >= warm);
            // Readings at (almost) the same moment - uploads finishing together - count as one: start after all of them.
            var first = start;
            start = samples.Where(s => s.Seconds >= first.Seconds && s.Seconds <= first.Seconds + 0.1).MaxBy(s => s.Bytes);
            if (samples.Count < 4 || last.Seconds - start.Seconds < 0.5 || last.Bytes - start.Bytes <= 0)
                return ToMbps(last.Bytes, last.Seconds);
            return ToMbps(last.Bytes - start.Bytes, last.Seconds - start.Seconds);
        }

        /// <summary>Speed over the last second of samples - the moving number while a test runs.</summary>
        public static double RecentMbps(IReadOnlyList<(double Seconds, long Bytes)> samples, double window = 1.0)
        {
            if (samples.Count < 2) return 0;
            var last = samples[^1];
            var first = samples.LastOrDefault(s => s.Seconds <= last.Seconds - window);
            if (first == default) first = samples[0];
            return ToMbps(last.Bytes - first.Bytes, last.Seconds - first.Seconds);
        }

        /// <summary>One line a person can act on, instead of a network exception.</summary>
        public static string PlainError(Exception ex, SpeedTestServer server)
        {
            var inner = ex;
            while (inner.InnerException != null && inner is HttpRequestException or AggregateException) inner = inner.InnerException;
            string host = server.Name;
            return inner switch
            {
                SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => $"Couldn't find {host}'s test server - check that you're online.",
                SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.NetworkUnreachable or SocketError.HostUnreachable } => $"Couldn't connect to {host}'s test server. It may be blocked on this network - try another server.",
                HttpRequestException { StatusCode: { } code } => $"{host}'s test server answered with an error ({(int)code}). Try another server.",
                System.Security.Authentication.AuthenticationException => $"Couldn't open a secure connection to {host} (a proxy or filter may be in the way).",
                TimeoutException or TaskCanceledException => $"{host}'s test server stopped answering.",
                IOException => $"The connection to {host}'s test server was interrupted. Try again.",
                _ => inner.Message.Split('\n')[0].Trim(),
            };
        }

        // =================================================================
        //  What the numbers mean
        // =================================================================

        /// <summary>Plain-words verdicts for streaming, video calls and games, and uploads.</summary>
        public static List<SpeedVerdict> Explain(SpeedTestResult r)
        {
            var list = new List<SpeedVerdict>();
            double down = r.DownloadMbps;
            list.Add(down switch
            {
                >= 100 => new(VerdictLevel.Good, "Streaming and downloads", "Several 4K streams at once, and big downloads go quickly."),
                >= 25 => new(VerdictLevel.Good, "Streaming and downloads", "Fine for 4K streaming; large downloads take a little while."),
                >= 5 => new(VerdictLevel.Fair, "Streaming and downloads", "HD streaming works; 4K may buffer, and several people at once will feel it."),
                _ => new(VerdictLevel.Poor, "Streaming and downloads", "Slow - expect buffering even in HD."),
            });

            double ping = r.PingMs, jitter = r.JitterMs, loaded = r.LoadedPingMs;
            if (ping >= 0)
            {
                var level = ping <= 40 && jitter < 15 ? VerdictLevel.Good : ping <= 100 && jitter < 30 ? VerdictLevel.Fair : VerdictLevel.Poor;
                string detail = level switch
                {
                    VerdictLevel.Good => "Quick responses - good for video calls and online games.",
                    VerdictLevel.Fair => "Calls are fine; fast online games may feel a little delayed.",
                    _ => "Noticeable delay or wobble - calls may stutter and games lag.",
                };
                if (loaded >= 0 && loaded - ping > 100)
                {
                    detail += $" Under load it rises to {loaded:0} ms, so calls suffer while someone downloads (bufferbloat).";
                    if (level == VerdictLevel.Good) level = VerdictLevel.Fair;
                }
                list.Add(new SpeedVerdict(level, "Calls and games", detail));
            }

            if (r.UploadMbps >= 0)
            {
                list.Add(r.UploadMbps switch
                {
                    >= 20 => new(VerdictLevel.Good, "Uploads and video calls", "HD video calls and quick cloud backups."),
                    >= 3 => new(VerdictLevel.Fair, "Uploads and video calls", "Video calls work; uploading big files takes a while."),
                    _ => new(VerdictLevel.Poor, "Uploads and video calls", "Your video may look blurry in calls, and uploads are slow."),
                });
            }
            return list;
        }

        public static string DescribeResult(SpeedTestResult r)
        {
            var parts = new List<string> { $"Download {r.DownloadDisplay}" };
            if (r.UploadMbps >= 0) parts.Add($"upload {r.UploadDisplay}");
            if (r.PingMs >= 0) parts.Add($"ping {r.PingDisplay}");
            parts.Add($"{r.DataDisplay} used");
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  History and settings (%AppData%\Smart Tools - numbers only)
        // =================================================================

        public const int HistoryLimit = 50;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string Folder => AppPaths.DataRoot;

        public static List<SpeedTestResult> LoadHistory(string? path = null)
        {
            try
            {
                string file = path ?? Path.Combine(Folder, "speed_test_history.json");
                if (!File.Exists(file)) return new List<SpeedTestResult>();
                var list = JsonSerializer.Deserialize<List<SpeedTestResult>>(File.ReadAllText(file), JsonOptions) ?? new();
                return list.Where(r => r != null && double.IsFinite(r.DownloadMbps)).OrderByDescending(r => r.Timestamp).Take(HistoryLimit).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new List<SpeedTestResult>();
            }
        }

        /// <summary>Sets each run's history bars relative to the fastest saved run (download or upload).</summary>
        public static void ScaleHistoryBars(IEnumerable<SpeedTestResult> history)
        {
            var list = history.ToList();
            double top = list.Count == 0 ? 0 : list.Max(r => Math.Max(r.DownloadMbps, r.UploadMbps));
            foreach (var r in list)
            {
                r.DownloadBarPercent = top <= 0 ? 0 : Math.Clamp(r.DownloadMbps / top * 100, 0, 100);
                r.UploadBarPercent = top <= 0 || r.UploadMbps < 0 ? 0 : Math.Clamp(r.UploadMbps / top * 100, 0, 100);
            }
        }

        public static void SaveHistory(IEnumerable<SpeedTestResult> history, string? path = null) =>
            WriteJson(path ?? Path.Combine(Folder, "speed_test_history.json"), history.Take(HistoryLimit).ToList());

        public static SpeedTestSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? Path.Combine(Folder, "speed_test.json");
                if (!File.Exists(file)) return new SpeedTestSettings();
                var s = JsonSerializer.Deserialize<SpeedTestSettings>(File.ReadAllText(file), JsonOptions) ?? new SpeedTestSettings();
                if (!DurationChoices.Contains(s.DurationSeconds)) s.DurationSeconds = 10;
                if (!DataLimitChoices.Contains(s.DataLimitMb)) s.DataLimitMb = 0;
                s.ServerId = ServerById(s.ServerId).Id;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new SpeedTestSettings();
            }
        }

        public static void SaveSettings(SpeedTestSettings settings, string? path = null) =>
            WriteJson(path ?? Path.Combine(Folder, "speed_test.json"), settings);

        private static void WriteJson<T>(string file, T value)
        {
            // Losing a history entry is better than crashing the test (file locked, disk full, no rights).
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(value, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort: AtomicFile already removed its temp file
            }
        }
    }
}
