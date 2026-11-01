using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>OneFrame = the time in the box; EverySeconds / EvenlySpaced = a pattern; Picked = the moments you chose one by one.</summary>
    public enum FrameMode { OneFrame, EverySeconds, EvenlySpaced, Picked }
    public enum FrameFormat { Png, Jpg }

    public sealed class FrameExtractorSettings
    {
        public FrameMode Mode { get; set; } = FrameMode.OneFrame;
        public FrameFormat Format { get; set; } = FrameFormat.Png;
        public double AtSeconds { get; set; } = 0;
        public double EverySeconds { get; set; } = 5;
        public int Count { get; set; } = 10;
        public bool IncludeSubfolders { get; set; } = true;
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

        /// <summary>"1:23.450" (or "23.450", or "1:02:03.450" past an hour).</summary>
        public static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            int hours = (int)t.TotalHours;
            string ms = t.Milliseconds.ToString("000", CultureInfo.InvariantCulture);
            return hours > 0
                ? $"{hours}:{t.Minutes:00}:{t.Seconds:00}.{ms}"
                : $"{t.Minutes}:{t.Seconds:00}.{ms}";
        }

        /// <summary>
        /// The moments (in seconds) to take frames at, for a video of this length. Empty when nothing fits.
        /// For <see cref="FrameMode.Picked"/> they are the ones in <paramref name="picked"/> (sorted, no repeats, none past the end).
        /// </summary>
        public static List<double> Moments(FrameExtractorSettings s, double duration, IReadOnlyList<double>? picked = null)
        {
            s = Clamp(s);
            var list = new List<double>();
            if (duration <= 0) return list;
            double last = Math.Max(0, duration - 0.05);   // a hair before the end, so the last frame exists

            switch (s.Mode)
            {
                case FrameMode.Picked:
                    foreach (double t in (picked ?? Array.Empty<double>()).Where(t => t >= 0).Select(t => Math.Round(Math.Min(t, last), 3)).Distinct().OrderBy(t => t))
                        if (list.Count < MaxFrames) list.Add(t);
                    break;
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
        //  Finding videos
        // =================================================================

        public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp" };

        public const string VideoFilter = "*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.m4v;*.flv;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.3gp";
        public const int MaxVideos = 500;

        public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>Every video under the paths (folders, or loose files), once each, A-Z. Links aren't followed.</summary>
        public static (List<string> Files, int Inaccessible) FindVideos(IEnumerable<string> paths, bool includeSubfolders)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int inaccessible = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = includeSubfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var here = Directory.EnumerateFiles(path, "*", options).Where(IsVideo).ToList();
                        here.Sort(StringComparer.OrdinalIgnoreCase);
                        foreach (string f in here) if (seen.Add(f)) files.Add(f);
                    }
                    else if (File.Exists(path) && IsVideo(path) && seen.Add(path)) files.Add(path);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    inaccessible++;
                }
            }
            return (files, inaccessible);
        }

        // =================================================================
        //  A still at any time (for choosing the moment)
        // =================================================================

        /// <summary>
        /// One frame at <paramref name="seconds"/> as PNG bytes, at most <paramref name="width"/> pixels wide (never enlarged).
        /// Null with the reason in <paramref name="problem"/>; also null when cancelled.
        /// </summary>
        public static byte[]? PreviewFrame(string video, double seconds, int width, out string? problem, CancellationToken token)
        {
            problem = null;
            string? exe = ConverterManager.FfmpegExe;
            if (exe == null) { problem = "ffmpeg.exe wasn't found."; return null; }
            try
            {
                var info = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (string a in new[]
                {
                    "-nostdin", "-v", "error",
                    "-ss", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", video,
                    "-frames:v", "1", "-an", "-sn", "-dn",
                    "-vf", $"scale='min({Math.Clamp(width, 16, 4096)},iw)':-2",
                    "-f", "image2pipe", "-c:v", "png", "pipe:1",
                }) info.ArgumentList.Add(a);

                using var p = Process.Start(info);
                if (p == null) { problem = "Couldn't start ffmpeg."; return null; }
                using var registration = token.Register(() => { try { if (!p.HasExited) p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
                var errorText = p.StandardError.ReadToEndAsync();
                using var ms = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit();
                if (token.IsCancellationRequested) return null;
                if (ms.Length == 0)
                {
                    string text = errorText.Result.Trim();
                    problem = text.Length > 0 ? text.Split('\n')[0].Trim() : "There is no picture at that time.";
                    return null;
                }
                return ms.ToArray();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                problem = ex.Message;
                return null;
            }
        }

        // =================================================================
        //  Several videos at once
        // =================================================================

        public sealed record FrameBatchProgress(int VideoIndex, int VideoCount, string Video, int Done, int Total);

        public sealed record FrameBatchResult(List<string> Files, List<(string Video, string Problem)> Problems, bool Cancelled);

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _action;
            public InlineProgress(Action<T> action) => _action = action;
            public void Report(T value) => _action(value);
        }

        /// <summary>
        /// Takes the frames of every video. With more than one video each gets its own sub-folder (named after it, numbered when
        /// two share a name). "Picked" moments belong to one video, so give it a list with one video. A video that can't be
        /// read is reported and the rest carry on. Never over an existing file.
        /// </summary>
        public static FrameBatchResult ExtractBatch(IReadOnlyList<string> videos, string folder, FrameExtractorSettings settings,
            IReadOnlyList<double>? picked, IProgress<FrameBatchProgress>? progress, CancellationToken token)
        {
            var files = new List<string>();
            var problems = new List<(string Video, string Problem)>();
            bool sub = videos.Count > 1;
            var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < videos.Count; i++)
            {
                if (token.IsCancellationRequested) return new FrameBatchResult(files, problems, true);
                string video = videos[i];

                double? duration = ProbeDuration(video, out string? probeProblem);
                if (duration == null) { problems.Add((video, probeProblem ?? "Couldn't be read")); continue; }
                var moments = Moments(settings, duration.Value, picked);
                if (moments.Count == 0) { problems.Add((video, "No frames to take")); continue; }

                string target = folder;
                if (sub)
                {
                    string baseName = Path.Combine(folder, SafeName(Path.GetFileNameWithoutExtension(video)));
                    target = baseName;
                    for (int n = 2; !usedFolders.Add(target); n++) target = $"{baseName} ({n})";
                }

                int index = i;
                var result = Extract(video, target, moments, settings.Format,
                    new InlineProgress<int>(done => progress?.Report(new FrameBatchProgress(index, videos.Count, video, done, moments.Count))), token);
                files.AddRange(result.Files);
                if (result.Problem != null) problems.Add((video, result.Problem));
                if (result.Cancelled) return new FrameBatchResult(files, problems, true);
            }
            return new FrameBatchResult(files, problems, false);
        }

        private static string SafeName(string name)
        {
            string clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            return clean.Length > 0 ? clean : "video";
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
