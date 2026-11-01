using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    public enum FrameMode { OneFrame, EverySeconds, EvenlySpaced }
    public enum FrameFormat { Png, Jpg }

    public sealed class FrameExtractorSettings
    {
        public FrameMode Mode { get; set; } = FrameMode.OneFrame;
        public FrameFormat Format { get; set; } = FrameFormat.Png;
        public double AtSeconds { get; set; } = 0;
        public double EverySeconds { get; set; } = 5;
        public int Count { get; set; } = 10;
    }

    public sealed record FrameRunResult(List<string> Files, string? Problem, bool Cancelled);

    /// <summary>
    /// Video Frame Extractor logic: which moments to grab, the ffmpeg command for each, and the run. ffmpeg and ffprobe
    /// are the ones the Convert tools use. Each frame is its own quick seek, so a long video isn't read through.
    /// </summary>
    public static class FrameExtractorManager
    {
        public const string SettingsName = "frame_extractor";
        public const int MaxFrames = 500;

        public static FrameExtractorSettings Clamp(FrameExtractorSettings s)
        {
            if (!Enum.IsDefined(s.Mode)) s.Mode = FrameMode.OneFrame;
            if (!Enum.IsDefined(s.Format)) s.Format = FrameFormat.Png;
            s.AtSeconds = Math.Clamp(double.IsFinite(s.AtSeconds) ? s.AtSeconds : 0, 0, 7 * 24 * 3600);
            s.EverySeconds = Math.Clamp(double.IsFinite(s.EverySeconds) ? s.EverySeconds : 5, 0.04, 24 * 3600);
            s.Count = Math.Clamp(s.Count, 1, MaxFrames);
            return s;
        }

        /// <summary>"90", "1:30", "1:02:03.5" or "1m30s" as seconds. False when it can't be read or is negative.</summary>
        public static bool TryParseTime(string? text, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.Contains(':'))
            {
                var parts = t.Split(':');
                if (parts.Length is < 2 or > 3) return false;
                double total = 0;
                foreach (string p in parts)
                {
                    if (!double.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) || v < 0) return false;
                    total = total * 60 + v;
                }
                seconds = total;
                return true;
            }
            if (t.EndsWith('s') || t.Contains('m') || t.Contains('h'))
            {
                if (!TimerStopwatchManager.TryParseDuration(t, out var d)) return false;
                seconds = d.TotalSeconds;
                return true;
            }
            return double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
        }

        /// <summary>The moments (in seconds) to take frames at, for a video of this length. Empty when nothing fits.</summary>
        public static List<double> Moments(FrameExtractorSettings s, double duration)
        {
            s = Clamp(s);
            var list = new List<double>();
            if (duration <= 0) return list;
            double last = Math.Max(0, duration - 0.05);   // a hair before the end, so the last frame exists

            switch (s.Mode)
            {
                case FrameMode.OneFrame:
                    list.Add(Math.Min(s.AtSeconds, last));
                    break;
                case FrameMode.EverySeconds:
                    for (double t = 0; t <= last && list.Count < MaxFrames; t += s.EverySeconds) list.Add(Math.Round(t, 3));
                    break;
                default:
                    // N frames in the middle of N equal parts, so the first and last aren't black fade-ins/outs.
                    for (int i = 0; i < s.Count; i++) list.Add(Math.Round(Math.Min(last, duration * (i + 0.5) / s.Count), 3));
                    break;
            }
            return list;
        }

        /// <summary>"name_00-01-05.250.png" - the moment is in the name, so frames sort in video order and never clash.</summary>
        public static string FileNameFor(string videoName, double seconds, FrameFormat format)
        {
            var t = TimeSpan.FromSeconds(seconds);
            string stamp = $"{(int)t.TotalHours:00}-{t.Minutes:00}-{t.Seconds:00}.{t.Milliseconds:000}";
            string safe = string.Concat(videoName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            return $"{(safe.Length > 0 ? safe : "video")}_{stamp}{(format == FrameFormat.Png ? ".png" : ".jpg")}";
        }

        public static List<string> Arguments(string input, double seconds, FrameFormat format, string output)
        {
            var args = new List<string>
            {
                "-nostdin", "-y", "-v", "error",
                "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", input,
                "-frames:v", "1", "-an", "-sn", "-dn",
            };
            if (format == FrameFormat.Jpg) { args.Add("-q:v"); args.Add("2"); }
            args.Add("-update"); args.Add("1");
            args.Add(output);
            return args;
        }

        // =================================================================
        //  Running ffmpeg / ffprobe
        // =================================================================

        /// <summary>Null when both programs are there, else what is missing.</summary>
        public static string? MissingTools()
        {
            bool ffmpeg = ConverterManager.FfmpegExe != null, ffprobe = ConverterManager.FfprobeExe != null;
            if (ffmpeg && ffprobe) return null;
            return (!ffmpeg && !ffprobe ? "ffmpeg.exe and ffprobe.exe weren't" : !ffmpeg ? "ffmpeg.exe wasn't" : "ffprobe.exe wasn't") + " found, so frames can't be taken.";
        }

        /// <summary>Length in seconds, or null with the reason.</summary>
        public static double? ProbeDuration(string path, out string? problem)
        {
            problem = null;
            string? exe = ConverterManager.FfprobeExe;
            if (exe == null) { problem = "ffprobe.exe wasn't found."; return null; }
            try
            {
                var info = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                foreach (string a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path }) info.ArgumentList.Add(a);
                using var p = Process.Start(info);
                if (p == null) { problem = "Couldn't start ffprobe."; return null; }
                string output = p.StandardOutput.ReadToEnd().Trim();
                string error = p.StandardError.ReadToEnd().Trim();
                if (!p.WaitForExit(20_000)) { try { p.Kill(); } catch (InvalidOperationException) { } problem = "ffprobe took too long."; return null; }
                if (double.TryParse(output.Split('\n')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0) return d;
                problem = error.Length > 0 ? "That file couldn't be read as a video: " + error.Split('\n')[0] : "That file doesn't look like a video (no length found).";
                return null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                problem = ex.Message;
                return null;
            }
        }

        /// <summary>Takes the frames one by one into the folder (never over an existing file). <paramref name="progress"/> gets the count done.</summary>
        public static FrameRunResult Extract(string video, string folder, IReadOnlyList<double> moments, FrameFormat format, IProgress<int>? progress, CancellationToken token)
        {
            var files = new List<string>();
            string? exe = ConverterManager.FfmpegExe;
            if (exe == null) return new FrameRunResult(files, "ffmpeg.exe wasn't found.", false);

            try
            {
                Directory.CreateDirectory(folder);
                string name = Path.GetFileNameWithoutExtension(video);
                string? firstProblem = null;
                int done = 0;
                foreach (double t in moments)
                {
                    token.ThrowIfCancellationRequested();
                    string output = UniquePath(Path.Combine(folder, FileNameFor(name, t, format)));
                    string? error = RunOne(exe, video, t, format, output, token);
                    if (error == null && File.Exists(output) && new FileInfo(output).Length > 0) files.Add(output);
                    else
                    {
                        try { if (File.Exists(output)) File.Delete(output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        firstProblem ??= error;
                    }
                    progress?.Report(++done);
                }
                if (files.Count == 0)
                    return new FrameRunResult(files, firstProblem ?? "No frame could be taken from that video.", false);
                return new FrameRunResult(files, null, false);
            }
            catch (OperationCanceledException)
            {
                return new FrameRunResult(files, null, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new FrameRunResult(files, ex.Message, false);
            }
        }

        private static string? RunOne(string exe, string video, double t, FrameFormat format, string output, CancellationToken token)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (string a in Arguments(video, t, format, output)) info.ArgumentList.Add(a);
            using var p = Process.Start(info);
            if (p == null) return "Couldn't start ffmpeg.";
            using var registration = token.Register(() => { try { if (!p.HasExited) p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            var errorText = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            token.ThrowIfCancellationRequested();
            if (p.ExitCode == 0) return null;
            string text = errorText.Result.Trim();
            return text.Length > 0 ? text.Split('\n')[0].Trim() : "ffmpeg stopped with code " + p.ExitCode.ToString(CultureInfo.InvariantCulture);
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string folder = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(folder, $"{name} ({n}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }
    }
}
