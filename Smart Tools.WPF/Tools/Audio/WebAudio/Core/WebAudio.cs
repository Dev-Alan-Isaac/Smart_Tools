using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Models
{
    public enum WebAudioFormat { Mp3, M4a, Opus, Flac, Wav }

    /// <summary>Best = the source's own quality; the others cap the bitrate (ignored for FLAC and WAV, which are lossless).</summary>
    public enum WebAudioQuality { Best, High, Standard }

    /// <summary>What yt-dlp says about a link before anything is downloaded.</summary>
    public sealed record WebAudioInfo(string Title, string Uploader, double? DurationSeconds, bool IsLive, string Site);

    public enum WebAudioError { None, Unsupported, Private, Unavailable, Blocked, SignIn, Network, Live, NoFfmpeg, Other }
}

namespace Smart_Tools.WPF.Controllers
{
    using Smart_Tools.WPF.Models;

    /// <summary>
    /// Command lines for the bundled yt-dlp and the reading of what it prints. Pure text: no process, no network, so it is tested
    /// without them. Arguments are a list (never one string) and the link goes after "--", so a link can never be read as an option.
    /// </summary>
    public static class WebAudioMath
    {
        // =================================================================
        //  The link
        // =================================================================

        /// <summary>
        /// Cleans what was pasted into a web link, or false. Accepts http and https only (no file:, no options), adds https:// to
        /// "www.example.com/..." and "youtu.be/..." typed without it, and drops the spaces around.
        /// </summary>
        public static bool TryNormalizeUrl(string? text, out string url)
        {
            url = "";
            string t = (text ?? "").Trim().Trim('<', '>', '"', '\'');
            if (t.Length == 0 || t.Contains(' ') || t.Contains('\n') || t.StartsWith('-')) return false;
            if (!t.Contains("://", StringComparison.Ordinal))
            {
                if (!Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9\-]*(\.[A-Za-z0-9\-]+)+(/|$|\?|#)")) return false;   // "example.com/..." looks like a link, a word does not
                t = "https://" + t;
            }
            if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (uri.Host.Length == 0 || !uri.Host.Contains('.')) return false;
            url = uri.AbsoluteUri;
            return true;
        }

        // =================================================================
        //  Command lines
        // =================================================================

        /// <summary>Ask about the link only (title, length, site). Nothing is downloaded.</summary>
        public static IReadOnlyList<string> InfoArgs(string url) => new[]
        {
            "--dump-single-json", "--no-playlist", "--skip-download", "--no-warnings", "--", url,
        };

        public static string Extension(WebAudioFormat format) => format switch
        {
            WebAudioFormat.M4a => "m4a",
            WebAudioFormat.Opus => "opus",
            WebAudioFormat.Flac => "flac",
            WebAudioFormat.Wav => "wav",
            _ => "mp3",
        };

        /// <summary>
        /// Download the sound of one link into <paramref name="folder"/> as the chosen format. <paramref name="ffmpegFolder"/> is where ffmpeg is.
        /// The file is named after the title; the caller moves it to its real place under a free name.
        /// </summary>
        public static IReadOnlyList<string> DownloadArgs(string url, string folder, string ffmpegFolder, WebAudioFormat format, WebAudioQuality quality,
            double? startSeconds, double? endSeconds)
        {
            var args = new List<string>
            {
                "-x", "--audio-format", Extension(format),
                "--no-playlist", "--newline", "--no-mtime", "--no-warnings", "--windows-filenames", "--embed-metadata",
                "--progress-template", "download:PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
                "--ffmpeg-location", ffmpegFolder,
                "-o", System.IO.Path.Combine(folder, "%(title).120B.%(ext)s"),
            };
            if (format is not (WebAudioFormat.Flac or WebAudioFormat.Wav))
            {
                args.Add("--audio-quality");
                args.Add(quality switch { WebAudioQuality.High => "192K", WebAudioQuality.Standard => "128K", _ => "0" });
            }
            if (startSeconds != null || endSeconds != null)
            {
                args.Add("--download-sections");
                args.Add("*" + Time(startSeconds ?? 0) + "-" + (endSeconds == null ? "inf" : Time(endSeconds.Value)));
                args.Add("--force-keyframes-at-cuts");
            }
            args.Add("--");
            args.Add(url);
            return args;
        }

        /// <summary>yt-dlp updates its own program file in place (sites change often and break older copies).</summary>
        public static IReadOnlyList<string> UpdateArgs() => new[] { "-U", "--no-warnings" };

        private static string Time(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

        // =================================================================
        //  Times typed by a person: "83", "1:23", "0:01:23.5"
        // =================================================================

        public static bool TryParseTime(string? text, out double seconds)
        {
            seconds = 0;
            string t = (text ?? "").Trim().Replace(',', '.');
            if (t.Length == 0) return false;
            var parts = t.Split(':');
            if (parts.Length > 3) return false;
            double total = 0;
            foreach (string part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0 || double.IsInfinity(v)) return false;
                total = total * 60 + v;
            }
            seconds = total;
            return true;
        }

        public static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        // =================================================================
        //  Reading what yt-dlp prints
        // =================================================================

        /// <summary>The info JSON of a link. Null when it is not one (an error text, a playlist with no entries).</summary>
        public static WebAudioInfo? ParseInfo(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                string title = Str(root, "title");
                if (title.Length == 0) return null;
                double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
                bool live = root.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.True;
                string uploader = Str(root, "uploader");
                if (uploader.Length == 0) uploader = Str(root, "channel");
                string site = Str(root, "extractor_key");
                return new WebAudioInfo(title, uploader, duration, live, site);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        /// <summary>The percent in a progress line ("PROGRESS| 45.3%|1.2MiB/s|00:12"), or null.</summary>
        public static double? ParseProgress(string line)
        {
            var m = Regex.Match(line, @"PROGRESS\|\s*(?<p>\d{1,3}(?:\.\d+)?)%");
            return m.Success && double.TryParse(m.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && p is >= 0 and <= 100 ? p : null;
        }

        /// <summary>Speed and time left from the same line, as yt-dlp shows them ("1.2MiB/s", "00:12"). Empty when there are none.</summary>
        public static (string Speed, string Eta) ParseSpeedEta(string line)
        {
            var m = Regex.Match(line, @"PROGRESS\|[^|]*\|\s*(?<s>[^|]*?)\s*\|\s*(?<e>[^|\r\n]*?)\s*$");
            if (!m.Success) return ("", "");
            string s = m.Groups["s"].Value, e = m.Groups["e"].Value;
            return (s.Contains("NA", StringComparison.Ordinal) || s.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ? "" : s,
                    e.Contains("NA", StringComparison.Ordinal) || e.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ? "" : e);
        }

        /// <summary>Puts yt-dlp's complaint in one of a few plain categories, so the page can say what to do.</summary>
        public static WebAudioError Classify(string output)
        {
            if (output.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return WebAudioError.Unsupported;
            if (output.Contains("Private video", StringComparison.OrdinalIgnoreCase) || output.Contains("members-only", StringComparison.OrdinalIgnoreCase)) return WebAudioError.Private;
            if (output.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) || output.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
                || output.Contains("confirm your age", StringComparison.OrdinalIgnoreCase) || output.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)) return WebAudioError.SignIn;
            if (output.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) || output.Contains("has been removed", StringComparison.OrdinalIgnoreCase)
                || output.Contains("not available in your country", StringComparison.OrdinalIgnoreCase) || output.Contains("This video is not available", StringComparison.OrdinalIgnoreCase)) return WebAudioError.Unavailable;
            if (output.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) || output.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)) return WebAudioError.Blocked;
            if (output.Contains("is a live", StringComparison.OrdinalIgnoreCase) || output.Contains("live event", StringComparison.OrdinalIgnoreCase)) return WebAudioError.Live;
            if (output.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) && output.Contains("not found", StringComparison.OrdinalIgnoreCase)) return WebAudioError.NoFfmpeg;
            if (output.Contains("Unable to download", StringComparison.OrdinalIgnoreCase) || output.Contains("getaddrinfo failed", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) || output.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) || output.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)) return WebAudioError.Network;
            return WebAudioError.Other;
        }

        /// <summary>The last "ERROR: ..." line, without the prefix, or "".</summary>
        public static string LastError(string output)
        {
            string last = "";
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) last = line["ERROR:".Length..].Trim();
            }
            return last;
        }
    }
}
