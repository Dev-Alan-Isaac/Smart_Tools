using System.Globalization;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Internet Speed Test data model. Plain data - no WPF or networking.
    // =====================================================================

    /// <summary>A public test server. <see cref="DownloadUrl"/> takes the size as {0}, or is a fixed file.</summary>
    public sealed class SpeedTestServer
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Location { get; init; } = "";
        public required string DownloadUrl { get; init; }

        /// <summary>Upload target, or null when the server only offers downloads.</summary>
        public string? UploadUrl { get; init; }

        /// <summary>A tiny request for timing round trips when ping (ICMP) is blocked.</summary>
        public required string LatencyUrl { get; init; }

        /// <summary>Host for ping (ICMP).</summary>
        public required string PingHost { get; init; }

        /// <summary>
        /// Add a random "?r=..." to every request so nothing along the way can answer from a cache. A few file hosts
        /// (Hetzner's speed-test servers) reset any request that has a query string, which showed up as "the connection
        /// was interrupted" - for those it is off, and the no-cache headers do the job.
        /// </summary>
        public bool CacheBust { get; init; } = true;

        public bool CanUpload => !string.IsNullOrEmpty(UploadUrl);
        public string Display => string.IsNullOrEmpty(Location) ? Name : $"{Name} · {Location}";
        public override string ToString() => Display;
    }

    public enum SpeedPhase
    {
        Idle,
        Latency,
        Download,
        Upload,
        Done,
    }

    /// <summary>A live reading while a test runs (about ten a second).</summary>
    public readonly record struct NetSpeedSample(SpeedPhase Phase, double Seconds, double Mbps, double Percent, long Bytes, string Text);

    /// <summary>Round-trip times: middle value, jitter (how much it wobbles), and how they were measured.</summary>
    public sealed record LatencyResult(double MedianMs, double JitterMs, double MinMs, int Samples, string Method);

    /// <summary>Everything a run needs, read from the sidebar.</summary>
    public sealed class SpeedTestOptions
    {
        public required SpeedTestServer Server { get; init; }

        /// <summary>How long download and upload each run (a fixed size was too short on fast lines and too long on slow ones).</summary>
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>Stop a direction once this much has moved; 0 = no limit (for metered connections).</summary>
        public long DataLimitBytes { get; init; }

        /// <summary>Parallel connections - one connection can't fill a fast line.</summary>
        public int Streams { get; init; } = 4;

        public bool MeasureUpload { get; init; } = true;
    }

    /// <summary>One finished test. Kept in the history (speed_test_history.json - numbers only, no paths).</summary>
    public sealed class SpeedTestResult
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string ServerName { get; init; } = "";

        /// <summary>-1 = couldn't be measured.</summary>
        public double PingMs { get; init; } = -1;

        public double JitterMs { get; init; } = -1;

        /// <summary>Round trip while downloading (bufferbloat); -1 = not measured.</summary>
        public double LoadedPingMs { get; init; } = -1;

        public double DownloadMbps { get; init; }

        /// <summary>-1 = not measured (the server has no upload, or it was switched off).</summary>
        public double UploadMbps { get; init; } = -1;

        public long BytesUsed { get; init; }
        public string LatencyMethod { get; init; } = "";

        public string PingDisplay => PingMs < 0 ? "–" : $"{PingMs:0} ms";
        public string JitterDisplay => JitterMs < 0 ? "–" : $"{JitterMs:0.#} ms";
        public string LoadedPingDisplay => LoadedPingMs < 0 ? "–" : $"{LoadedPingMs:0} ms";
        public string DownloadDisplay => SpeedText.FormatMbps(DownloadMbps);
        public string UploadDisplay => UploadMbps < 0 ? "–" : SpeedText.FormatMbps(UploadMbps);
        public string TimestampDisplay => Timestamp.ToString("g", CultureInfo.CurrentCulture);
        public string DataDisplay => SpeedText.FormatBytes(BytesUsed);

        /// <summary>History bars: 0-100 of the fastest saved run (set by InternetSpeedTestManager.ScaleHistoryBars, not saved).</summary>
        [JsonIgnore]
        public double DownloadBarPercent { get; set; }

        [JsonIgnore]
        public double UploadBarPercent { get; set; }

        /// <summary>History footer: "Ping 12 ms · jitter 1.5 ms · 340 MB used".</summary>
        public string HistoryFooter =>
            (PingMs < 0 ? "Ping –" : $"Ping {PingMs:0} ms")
            + (JitterMs < 0 ? "" : $" · jitter {JitterDisplay}")
            + (BytesUsed > 0 ? $" · {DataDisplay} used" : "");
    }

    public enum VerdictLevel
    {
        Good,
        Fair,
        Poor,
    }

    /// <summary>A plain-words line about what the numbers mean ("Fine for 4K streaming").</summary>
    public sealed record SpeedVerdict(VerdictLevel Level, string Title, string Detail);

    /// <summary>Remembered between sessions (speed_test.json in %AppData%\Smart Tools).</summary>
    public sealed class SpeedTestSettings
    {
        public string ServerId { get; set; } = "cloudflare";
        public int DurationSeconds { get; set; } = 10;

        /// <summary>0 = no limit.</summary>
        public int DataLimitMb { get; set; }

        public bool MeasureUpload { get; set; } = true;
    }

    public static class SpeedText
    {
        /// <summary>"85.4 Mbps", "945 Mbps", "1.23 Gbps".</summary>
        public static string FormatMbps(double mbps)
        {
            if (mbps < 0 || !double.IsFinite(mbps)) return "–";
            if (mbps >= 1000) return $"{mbps / 1000:0.00} Gbps";
            if (mbps >= 100) return $"{mbps:0} Mbps";
            return $"{mbps:0.0} Mbps";
        }

        /// <summary>The number alone, for the big tiles ("85.4").</summary>
        public static string FormatMbpsNumber(double mbps) =>
            mbps < 0 || !double.IsFinite(mbps) ? "–" : mbps >= 1000 ? $"{mbps / 1000:0.00}" : mbps >= 100 ? $"{mbps:0}" : $"{mbps:0.0}";

        public static string Unit(double mbps) => mbps >= 1000 ? "Gbps" : "Mbps";

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1000 && unit < units.Length - 1)   // network sizes are decimal
            {
                size /= 1000;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }
    }
}
