using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Video Replay - folders, ffprobe durations, preview copies and deleting files.</summary>
    public static partial class VideoReplayManager
    {
        // =================================================================
        //  ffmpeg / ffprobe
        // =================================================================

        /// <summary>Set by tests to use a specific ffmpeg/ffprobe (or a missing path to simulate none).</summary>
        public static string? FfmpegPathOverride { get; set; }
        public static string? FfprobePathOverride { get; set; }

        public static string FfmpegFolder => Smart_Tools.WPF.Controllers.FfmpegLocator.Folder;
        public static string? FfmpegExe => Smart_Tools.WPF.Controllers.FfmpegLocator.Resolve("ffmpeg", FfmpegPathOverride);
        public static string? FfprobeExe => Smart_Tools.WPF.Controllers.FfmpegLocator.Resolve("ffprobe", FfprobePathOverride);

        // =================================================================
        //  Folders
        // =================================================================

        public sealed class ExpandResult
        {
            public List<(string Path, long Size)> Files { get; } = new();
            public int Inaccessible { get; set; }
            public int Links { get; set; }
            public int Missing { get; set; }
        }

        /// <summary>
        /// Lists the videos in the added files and folders off the UI thread
        /// (the old view read folders on the UI thread, and one locked
        /// subfolder dropped the whole folder). Each folder is sorted by name.
        /// </summary>
        public static Task<ExpandResult> ExpandAsync(IEnumerable<ReplaySource> sources, bool includeSubfolders, CancellationToken token = default)
        {
            var list = sources.ToList();
            return Task.Run(() =>
            {
                var result = new ExpandResult();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (source.IsFolder)
                    {
                        if (!Directory.Exists(source.FullPath))
                        {
                            result.Missing++;
                            continue;
                        }
                        WalkFolder(source.FullPath, includeSubfolders, result, seen, token);
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(source.FullPath);
                        if (!info.Exists) result.Missing++;
                        else if (seen.Add(info.FullName)) result.Files.Add((info.FullName, info.Length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        result.Missing++;
                    }
                }
                return result;
            }, token);
        }

        private static void WalkFolder(string root, bool includeSubfolders, ExpandResult result, HashSet<string> seen, CancellationToken token)
        {
            // Depth-first in name order, so a folder of episodes plays in order.
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = dir.GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    result.Inaccessible++;
                    continue;
                }

                var subdirs = new List<DirectoryInfo>();
                foreach (var entry in entries.OrderBy(e => e.Name, NaturalComparer.Instance))
                {
                    FileAttributes attrs;
                    try
                    {
                        attrs = entry.Attributes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    if (attrs.HasFlag(FileAttributes.System) || attrs.HasFlag(FileAttributes.Hidden)) continue;

                    if (entry is DirectoryInfo sub)
                    {
                        if (!includeSubfolders) continue;
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            result.Links++;
                            continue;
                        }
                        subdirs.Add(sub);
                    }
                    else if (entry is FileInfo file && IsVideo(file.Name))
                    {
                        try
                        {
                            if (seen.Add(file.FullName)) result.Files.Add((file.FullName, file.Length));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // vanished while listing
                        }
                    }
                }
                for (int i = subdirs.Count - 1; i >= 0; i--) pending.Push(subdirs[i]);
            }
        }

        /// <summary>"Episode 2" before "Episode 10" (the old list sorted 1, 10, 11, 2...).</summary>
        public sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new();

            public int Compare(string? a, string? b)
            {
                if (a == null || b == null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                        int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                        if (c != 0) return c;
                        i++;
                        j++;
                    }
                }
                return (a.Length - i).CompareTo(b.Length - j);
            }
        }

        // =================================================================
        //  Durations
        // =================================================================

        private static readonly SemaphoreSlim ProbeGate = new(3);

        /// <summary>
        /// Length from ffprobe (null when unknown). Run in the background so
        /// the playlist shows every clip's length and a total - the old one
        /// only filled a length in after the clip had been played.
        /// </summary>
        public static async Task<TimeSpan?> ProbeDurationAsync(string path, CancellationToken token)
        {
            string? exe = FfprobeExe;
            if (exe == null) return null;
            await ProbeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Big files (or slow / network drives) need longer than a small clip.
                var timeout = ProbeTimeout.For(path);

                var run = await RunAsync(exe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path }, null, timeout, token).ConfigureAwait(false);
                if (run.ExitCode == 0 && ParseSeconds(run.Output) is double s) return TimeSpan.FromSeconds(s);

                // No container length (N/A): try the video stream's own length.
                run = await RunAsync(exe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration", "-of", "default=nw=1:nk=1", path }, null, timeout, token).ConfigureAwait(false);
                return run.ExitCode == 0 && ParseSeconds(run.Output) is double s2 ? TimeSpan.FromSeconds(s2) : null;
            }
            finally
            {
                ProbeGate.Release();
            }
        }

        /// <summary>True when the first video stream is H.264 and the first audio stream (if any) is AAC.</summary>
        private static async Task<bool> IsH264AacAsync(string path, CancellationToken token)
        {
            string? exe = FfprobeExe;
            if (exe == null) return false;
            var run = await RunAsync(exe, new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", path }, null, ProbeTimeout.For(path), token).ConfigureAwait(false);
            if (run.ExitCode != 0) return false;
            bool video = false;
            foreach (string line in run.Output.Split('\n'))
            {
                var parts = line.Trim().Split(',');
                if (parts.Length < 2) continue;
                // csv order follows ffprobe's field order: codec_name,codec_type
                string name = parts[0], type = parts[1];
                if (type == "video" && !video) { if (name != "h264") return false; video = true; }
                else if (type == "audio" && name != "aac") return false;
            }
            return video;
        }

        public static double? ParseSeconds(string text) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v > 0 ? v : null;

        // =================================================================
        //  Preview copies (formats MediaElement can't open)
        // =================================================================

        /// <summary>%TEMP%\Smart Tools\Replay previews - emptied on close and on the next start.</summary>
        public static string PreviewFolder => Path.Combine(Path.GetTempPath(), "Smart Tools", "Replay previews");

        /// <summary>Deletes leftover preview copies (in use ones are skipped). The old view only cleaned up on Unloaded.</summary>
        public static int SweepPreviews(string? folder = null)
        {
            folder ??= PreviewFolder;
            int deleted = 0;
            if (!Directory.Exists(folder)) return 0;
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // playing right now
                }
            }
            return deleted;
        }

        /// <summary>Same source (path, size, date) -> same preview name, so a clip is converted once per session.</summary>
        public static string PreviewPathFor(string source, string? folder = null)
        {
            var info = new FileInfo(source);
            string key = $"{info.FullName.ToUpperInvariant()}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            return Path.Combine(folder ?? PreviewFolder, $"{Path.GetFileNameWithoutExtension(source)}.{hash}.mp4");
        }

        // ---------------- Encoders: graphics card first ----------------

        /// <summary>
        /// H.264 encoders to try, fastest first: NVIDIA, Intel, AMD, then
        /// Windows' own (Media Foundation, uses the GPU on most PCs), then the
        /// CPU (libx264). Only ones this ffmpeg build lists are tried; one that
        /// fails (no such card) is skipped for the rest of the session.
        /// </summary>
        public static readonly IReadOnlyList<string> EncoderPreference = new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264" };

        private static readonly object EncoderGate = new();
        private static List<string>? _listedEncoders;
        private static string? _workingEncoder;
        private static readonly HashSet<string> FailedEncoders = new();

        /// <summary>The encoder that worked last time (shown in the converting panel).</summary>
        public static string? LastEncoder => _workingEncoder;

        public static string EncoderLabel(string encoder) => encoder switch
        {
            "h264_nvenc" => "NVIDIA graphics card",
            "h264_qsv" => "Intel graphics",
            "h264_amf" => "AMD graphics card",
            "h264_mf" => "Windows encoder",
            _ => "processor",
        };

        /// <summary>Encoder names from "ffmpeg -encoders" output.</summary>
        public static List<string> ParseEncoders(string output) =>
            output.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 8 && l[0] == 'V')
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "")
                .Where(n => n.Length > 0)
                .ToList();

        /// <summary>Order to try for this session: the one that worked first, then untried ones, CPU last.</summary>
        public static async Task<List<string>> EncoderOrderAsync(string ffmpeg, CancellationToken token)
        {
            List<string>? listed;
            lock (EncoderGate) listed = _listedEncoders;
            if (listed == null)
            {
                var run = await RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, null, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                listed = run.ExitCode == 0 ? ParseEncoders(run.Output) : new List<string>();
                lock (EncoderGate) _listedEncoders = listed;
            }
            lock (EncoderGate)
            {
                var order = EncoderPreference.Where(e => e == "libx264" || (listed.Contains(e) && !FailedEncoders.Contains(e))).ToList();
                if (_workingEncoder != null && order.Remove(_workingEncoder)) order.Insert(0, _workingEncoder);
                return order;
            }
        }

        public static List<string> EncoderArgs(string encoder) => encoder switch
        {
            "h264_nvenc" => new() { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-pix_fmt", "yuv420p" },
            "h264_qsv" => new() { "-c:v", "h264_qsv", "-preset", "faster", "-global_quality", "23" },
            "h264_amf" => new() { "-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "22", "-qp_p", "24" },
            "h264_mf" => new() { "-c:v", "h264_mf", "-hw_encoding", "1", "-b:v", "8M" },
            _ => new() { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" },
        };

        /// <summary>
        /// Converts <paramref name="source"/> to an H.264/AAC MP4 that
        /// Windows' player can always play, with progress. Only needed when
        /// VLC isn't in Engines\VLC. Uses the graphics card when it can
        /// (usually 5-10x faster than the old CPU-only conversion). Written
        /// to a .part file and renamed when done; reused if it already exists.
        /// </summary>
        public static async Task<string> CreatePreviewAsync(string source, Action<double>? onPercent, CancellationToken token, string? folder = null, Action<string>? onEncoder = null)
        {
            string target = PreviewPathFor(source, folder);
            if (File.Exists(target)) return target;
            string exe = FfmpegExe ?? throw new InvalidOperationException($"This format needs a preview copy, and ffmpeg wasn't found in {FfmpegFolder}.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { OutputFolderManager.EnsureFreeSpace(Path.GetDirectoryName(target)!, new FileInfo(source).Length); }   // the copy is about as big as the source
            catch (FileNotFoundException) { }

            double duration = (await ProbeDurationAsync(source, token).ConfigureAwait(false))?.TotalSeconds ?? 0;
            string part = target + ".part";
            string lastError = "";
            try
            {
                // Already H.264 + AAC: rewrap instead of re-encoding (seconds instead of minutes, even for huge files).
                if (await IsH264AacAsync(source, token).ConfigureAwait(false))
                {
                    var copy = await RunAsync(exe, new[] { "-hide_banner", "-nostdin", "-y", "-fflags", "+genpts", "-i", source, "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", part },
                        null, TimeSpan.FromMinutes(Math.Max(10, duration / 60)), token).ConfigureAwait(false);
                    if (copy.ExitCode == 0 && File.Exists(part) && new FileInfo(part).Length > 0)
                    {
                        File.Move(part, target, overwrite: true);
                        onPercent?.Invoke(100);
                        return target;
                    }
                    TryDelete(part);   // rewrap refused (odd timestamps etc.) - fall through to a real conversion
                }

                foreach (string encoder in await EncoderOrderAsync(exe, token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    onEncoder?.Invoke(encoder);
                    var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-fflags", "+genpts", "-i", source, "-map", "0:v:0?", "-map", "0:a:0?" };
                    args.AddRange(EncoderArgs(encoder));
                    // A keyframe at least every 30 frames: otherwise seeking decodes from a keyframe that can be minutes back (long hangs).
                    args.AddRange(new[] { "-g", "30" });
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", "-f", "mp4", "-progress", "pipe:1", "-nostats", part });

                    var run = await RunAsync(exe, args, line =>
                    {
                        if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                            long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
                            onPercent?.Invoke(Math.Min(99, us / 1e6 * 100 / duration));
                    }, TimeSpan.FromMinutes(Math.Max(10, duration / 60 * 3)), token).ConfigureAwait(false);

                    if (run.TimedOut) throw new TimeoutException("Making the preview copy took too long.");
                    if (run.ExitCode == 0 && File.Exists(part) && new FileInfo(part).Length > 0)
                    {
                        lock (EncoderGate) _workingEncoder = encoder;
                        File.Move(part, target, overwrite: true);
                        onPercent?.Invoke(100);
                        return target;
                    }

                    lastError = run.Errors.LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"exit code {run.ExitCode}";
                    if (encoder == "libx264") break;   // the CPU encoder failing means the file itself is the problem
                    lock (EncoderGate)
                    {
                        FailedEncoders.Add(encoder);   // no such card here - don't try it again this session
                        if (_workingEncoder == encoder) _workingEncoder = null;
                    }
                    TryDelete(part);
                }
                throw new InvalidOperationException("ffmpeg couldn't convert it: " + lastError);
            }
            finally
            {
                TryDelete(part);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // swept on the next start
            }
        }

        /// <summary>Tests: forget which encoders worked or failed.</summary>
        public static void ResetEncoderCache()
        {
            lock (EncoderGate)
            {
                _listedEncoders = null;
                _workingEncoder = null;
                FailedEncoders.Clear();
            }
        }

        private sealed record ToolRun(int ExitCode, string Output, List<string> Errors, bool TimedOut);

        private static async Task<ToolRun> RunAsync(string exe, IEnumerable<string> args, Action<string>? onOutputLine, TimeSpan timeout, CancellationToken token)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            var output = new StringBuilder();
            var errors = new List<string>();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (output)
                {
                    output.AppendLine(e.Data);
                    if (output.Length > 131_072) output.Remove(0, output.Length - 65_536);   // multi-hour renders: keep only the tail
                }
                onOutputLine?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (errors)
                {
                    errors.Add(e.Data);
                    if (errors.Count > 40) errors.RemoveAt(0);
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            try
            {
                await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (errors) return new ToolRun(-1, "", errors.ToList(), true);
            }
            p.WaitForExit();   // flush the output events
            lock (output) lock (errors) return new ToolRun(p.ExitCode, output.ToString(), errors.ToList(), false);
        }

        // =================================================================
        //  Skip log
        // =================================================================

        public static string SkipLogPath => Path.Combine(
            AppPaths.DataRoot, "video_replay_skips.log");

        /// <summary>Adds one line saying why a clip was skipped. Never throws - logging must not break playback.</summary>
        public static void LogSkip(string clipPath, string engine, string reason)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SkipLogPath)!);
                var info = new FileInfo(SkipLogPath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();   // keep it small
                File.AppendAllText(SkipLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{engine}]  {clipPath}  -  {reason}{Environment.NewLine}");
            }
            catch (Exception) { }
        }

        // =================================================================
        //  Deleting a clip
        // =================================================================

        /// <summary>Fixed local drives have a Recycle Bin; network and removable drives don't.</summary>
        public static bool HasRecycleBin(string path)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>To the Recycle Bin where there is one (the old view always deleted permanently).</summary>
        public static void DeleteClip(string path)
        {
            if (!File.Exists(path)) return;
            if (HasRecycleBin(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }
    }
}
