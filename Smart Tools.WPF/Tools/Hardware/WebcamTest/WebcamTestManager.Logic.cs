using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Webcam test: the pure parts - reading FFmpeg's camera lists, brightness and settings. No process,
    /// no WPF, so it can be tested on its own.
    /// </summary>
    public sealed partial class WebcamTestManager
    {
        /// <summary>Used when the camera won't list its sizes.</summary>
        public static readonly WebcamMode DefaultMode = new(640, 480, 30);

        /// <summary>The highest frame rate asked for. The preview does not need more, and more costs CPU.</summary>
        public const int MaxPreviewFps = 30;

        private static readonly Regex VideoDevice = new("\"(?<name>[^\"]+)\"\\s*\\(video\\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QuotedName = new("\"(?<name>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex MaxSize = new(@"max s=(?<w>\d+)x(?<h>\d+) fps=(?<fps>[\d.]+)", RegexOptions.Compiled);
        private static readonly Regex AnySize = new(@"\bs=(?<w>\d+)x(?<h>\d+) fps=(?<fps>[\d.]+)", RegexOptions.Compiled);

        /// <summary>The cameras in the output of <c>ffmpeg -list_devices true -f dshow -i dummy</c>.</summary>
        public static List<WebcamDevice> ParseVideoDevices(string ffmpegOutput)
        {
            var lines = ffmpegOutput.Replace("\r", "").Split('\n');
            var names = new List<string>();

            // Newer FFmpeg: every line says what it is - "Integrated Camera" (video).
            foreach (string line in lines)
            {
                var m = VideoDevice.Match(line);
                if (m.Success) names.Add(m.Groups["name"].Value);
            }

            // Older FFmpeg: a "DirectShow video devices" heading, then the names.
            if (names.Count == 0)
            {
                bool inVideo = false;
                foreach (string line in lines)
                {
                    if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase)) { inVideo = true; continue; }
                    if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase)) { inVideo = false; continue; }
                    if (!inVideo || line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;
                    var m = QuotedName.Match(line);
                    if (m.Success && !m.Groups["name"].Value.StartsWith('@')) names.Add(m.Groups["name"].Value);
                }
            }
            return names.Distinct().Select(n => new WebcamDevice(n)).ToList();
        }

        /// <summary>The sizes in the output of <c>ffmpeg -list_options true -f dshow -i video=Name</c>, largest first, one per size.</summary>
        public static List<WebcamMode> ParseModes(string ffmpegOutput)
        {
            var best = new Dictionary<string, WebcamMode>();
            foreach (string line in ffmpegOutput.Replace("\r", "").Split('\n'))
            {
                var m = MaxSize.Match(line);
                if (!m.Success) m = AnySize.Match(line);
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups["w"].Value, out int w) || !int.TryParse(m.Groups["h"].Value, out int h)) continue;
                if (!double.TryParse(m.Groups["fps"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fps)) continue;
                if (w < 16 || h < 16 || w > 7680 || h > 4320) continue;

                var mode = new WebcamMode(w, h, Math.Clamp((int)Math.Floor(fps), 1, MaxPreviewFps));
                if (!best.TryGetValue(mode.Key, out var known) || mode.Fps > known.Fps) best[mode.Key] = mode;
            }
            return best.Values.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Fps).ToList();
        }

        /// <summary>The remembered size if the camera still offers it, else 1280 × 720 or the nearest below it, else the first.</summary>
        public static WebcamMode? PreferredMode(IReadOnlyList<WebcamMode> modes, string? savedKey)
        {
            if (modes.Count == 0) return null;
            return modes.FirstOrDefault(m => m.Key == savedKey)
                ?? modes.FirstOrDefault(m => m.Width <= 1280 && m.Height <= 720)
                ?? modes[^1];
        }

        public static WebcamDevice? PreferredDevice(IReadOnlyList<WebcamDevice> devices, string? savedName) =>
            devices.FirstOrDefault(d => d.Name == savedName) ?? devices.FirstOrDefault();

        /// <summary>
        /// Average brightness of a BGR24 picture as 0-100, from every few pixels (enough to judge exposure,
        /// and cheap enough to do on every frame).
        /// </summary>
        public static double AverageBrightness(byte[] bgr, int width, int height)
        {
            int pixels = Math.Min(width * height, bgr.Length / 3);
            if (pixels <= 0) return 0;
            const int step = 7;   // a prime step, so the samples don't line up with the rows
            double sum = 0;
            int count = 0;
            for (int p = 0; p < pixels; p += step)
            {
                int i = p * 3;
                sum += 0.114 * bgr[i] + 0.587 * bgr[i + 1] + 0.299 * bgr[i + 2];
                count++;
            }
            return count == 0 ? 0 : sum / count / 255 * 100;
        }

        public static WebcamBrightness JudgeBrightness(double percent) =>
            percent < 12 ? WebcamBrightness.Dark : percent > 85 ? WebcamBrightness.Bright : WebcamBrightness.Good;

        // ---------------- Settings ----------------

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "webcam_test.json");

        public static WebcamTestSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<WebcamTestSettings>(File.ReadAllText(file), JsonOptions) : null;
                s ??= new WebcamTestSettings();
                s.DeviceName ??= "";
                s.ModeKey ??= "";
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new WebcamTestSettings();
            }
        }

        public static void SaveSettings(WebcamTestSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);
    }
}
