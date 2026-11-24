using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Running a batch: listing folders, ffmpeg with live progress, moving results into place.</summary>
    public sealed partial class ConverterManager
    {
        // =================================================================
        //  ffmpeg / ffprobe
        // =================================================================

        /// <summary>Set by tests (or a settings page) to use a specific ffmpeg/ffprobe.</summary>
        public static string? FfmpegPathOverride { get; set; }
        public static string? FfprobePathOverride { get; set; }

        public static string FfmpegFolder => Smart_Tools.WPF.Controllers.FfmpegLocator.Folder;

        /// <summary>The bundled Engines\FFMPEG copy, else one on PATH, else null.</summary>
        public static string? FfmpegExe => Smart_Tools.WPF.Controllers.FfmpegLocator.Resolve("ffmpeg", FfmpegPathOverride);
        public static string? FfprobeExe => Smart_Tools.WPF.Controllers.FfmpegLocator.Resolve("ffprobe", FfprobePathOverride);

        private static readonly object SvtLock = new();
        private static string? _svtCheckedFor;
        private static bool _svtAvailable;

        /// <summary>Whether this ffmpeg has the fast SVT-AV1 encoder (checked once per ffmpeg).</summary>
        public static bool HasSvtAv1()
        {
            string? exe = FfmpegExe;
            if (exe == null) return false;
            lock (SvtLock)
            {
                if (_svtCheckedFor == exe) return _svtAvailable;
                bool found = false;
                try
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    psi.ArgumentList.Add("-hide_banner");
                    psi.ArgumentList.Add("-encoders");
                    using var p = Process.Start(psi)!;
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    _ = p.StandardError.ReadToEndAsync();   // drained so ffmpeg never blocks on a full pipe
                    if (!p.WaitForExit(10_000))
                    {
                        try { p.Kill(true); } catch (Exception) { /* already gone */ }
                    }
                    string text = outTask.Wait(2_000) ? outTask.Result : "";
                    found = text.Contains("libsvtav1", StringComparison.Ordinal);
                }
                catch (Exception)
                {
                    found = false;
                }
                _svtCheckedFor = exe;
                _svtAvailable = found;
                return found;
            }
        }

        // =================================================================
        //  Queue expansion
        // =================================================================

        public sealed class QueueResult
        {
            public List<QueuedFile> Files { get; } = new();

            /// <summary>Subfolders that couldn't be opened (the old version dropped the whole folder on one of these).</summary>
            public int Inaccessible { get; set; }

            /// <summary>Junctions/shortcut folders not followed.</summary>
            public int Links { get; set; }

            /// <summary>Added files/folders that no longer exist.</summary>
            public int Missing { get; set; }
        }

        private static bool IsMediaFile(string path) => KindOf(path) != MediaKind.Unsupported;

        /// <summary>
        /// Turns the added files and folders into a file list, off the UI
        /// thread. Folders contribute media files only; files added one by one
        /// are always listed (so an unsupported one can say why).
        /// </summary>
        public static Task<QueueResult> ExpandQueueAsync(IEnumerable<ConvertSource> sources, bool includeSubfolders, CancellationToken token = default)
        {
            var list = sources.ToList();
            return Task.Run(() =>
            {
                var result = new QueueResult();
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
                        WalkFolder(source.FullPath, includeSubfolders, result, token);
                    }
                    else
                    {
                        try
                        {
                            var info = new FileInfo(source.FullPath);
                            if (!info.Exists)
                            {
                                result.Missing++;
                                continue;
                            }
                            result.Files.Add(new QueuedFile(info.FullName, null, info.Length));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            result.Missing++;
                        }
                    }
                }
                return result;
            }, token);
        }

        private static void WalkFolder(string root, bool includeSubfolders, QueueResult result, CancellationToken token)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            string rootFull = Path.GetFullPath(root);

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

                foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
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
                    if (attrs.HasFlag(FileAttributes.System)) continue;

                    if (entry is DirectoryInfo sub)
                    {
                        if (!includeSubfolders) continue;
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            result.Links++;
                            continue;
                        }
                        pending.Push(sub);
                    }
                    else if (entry is FileInfo file && IsMediaFile(file.Name))
                    {
                        // Leftovers from a crashed run aren't inputs.
                        if (file.Name.Contains(".converting", StringComparison.OrdinalIgnoreCase)) continue;
                        long length;
                        try
                        {
                            length = file.Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }
                        result.Files.Add(new QueuedFile(file.FullName, rootFull, length));
                    }
                }
            }
        }

        // =================================================================
        //  Running
        // =================================================================

        /// <summary>If ffmpeg writes nothing for this long it's treated as stuck and stopped.</summary>
        public static TimeSpan FfmpegInactivityTimeout { get; set; } = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Converts one file into <c>output</c>, reporting 0-100. Tests swap
        /// this out; the default runs ffmpeg or Magick.NET.
        /// </summary>
        public Func<ConvertFileItem, string, ConvertOptions, Action<double>, CancellationToken, Task>? ConvertOverride { get; set; }

        /// <summary>Guards "is the final name free? -> move there" so two files aiming at one name don't race.</summary>
        private static readonly object FinalPathLock = new();

        /// <summary>Whether a row is converted by a run (retries failed and cancelled ones).</summary>
        public static bool IsPending(ConvertFileItem item) =>
            item.Status is ConvertStatus.Ready or ConvertStatus.Failed or ConvertStatus.Cancelled;

        public async Task<ConvertRunResult> RunAsync(
            IReadOnlyList<ConvertFileItem> items,
            ConvertOptions options,
            IProgress<ConvertProgress>? progress = null,
            CancellationToken token = default)
        {
            var started = Stopwatch.StartNew();
            var work = items.Where(IsPending).ToList();
            var result = new ConvertRunResult();
            int total = work.Count;
            if (total == 0) return result;

            if (options.FileType is ConvertFileType.Video or ConvertFileType.Audio && ConvertOverride == null && FfmpegExe == null)
                throw new InvalidOperationException($"Converting {(options.FileType == ConvertFileType.Video ? "videos" : "audio")} needs ffmpeg.exe in {FfmpegFolder}. It wasn't found there.");

            bool svt = options.FileType == ConvertFileType.Video && ConvertOverride == null && await Task.Run(HasSvtAv1);
            int parallel = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : DefaultParallelism(options.FileType);

            foreach (var item in work)
            {
                item.Status = ConvertStatus.Ready;
                item.Progress = 0;
                item.OutputBytes = 0;
            }

            // Progress = finished files + the fraction of the ones in flight.
            var gate = new object();
            var inFlight = new Dictionary<ConvertFileItem, double>();
            int completed = 0;
            long lastReport = 0;
            string current = "";

            // Reported inside the lock so reports stay in order (Progress<T> only posts).
            void Report(bool force)
            {
                lock (gate)
                {
                    long now = started.ElapsedMilliseconds;
                    // At most ten reports a second, however many small files finish; only the very last state is always sent.
                    if (now - lastReport < 100 && !(force && completed >= total)) return;
                    lastReport = now;
                    double partial = inFlight.Values.Sum() / 100.0;
                    double currentPercent = inFlight.Count > 0 ? inFlight.Values.Max() : 0;
                    progress?.Report(new ConvertProgress(completed, total, current, currentPercent, Math.Min(100, (completed + partial) * 100.0 / total)));
                }
            }

            using var throttle = new SemaphoreSlim(parallel);
            var tasks = work.Select(async item =>
            {
                try
                {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    item.Status = ConvertStatus.Cancelled;
                    item.Message = "Not started - the run was cancelled.";
                    return;
                }

                try
                {
                    lock (gate)
                    {
                        inFlight[item] = 0;
                        current = item.Name;
                    }
                    Report(true);

                    // Off the calling (UI) thread - ffprobe and file checks block.
                    await Task.Run(() => ConvertOneAsync(item, options, svt, percent =>
                    {
                        lock (gate)
                        {
                            if (inFlight.ContainsKey(item)) inFlight[item] = percent;
                        }
                        Report(false);
                    }, token), CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        inFlight.Remove(item);
                        completed++;
                        switch (item.Status)
                        {
                            case ConvertStatus.Converted:
                                result.Converted++;
                                result.InputBytes += item.SizeBytes;
                                result.OutputBytes += item.OutputBytes;
                                break;
                            case ConvertStatus.Cancelled:
                                result.Cancelled++;
                                break;
                            default:
                                result.Failed++;
                                break;
                        }
                    }
                    Report(true);
                    throttle.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Includes rows that never got a slot.
            result.Cancelled = work.Count(i => i.Status == ConvertStatus.Cancelled);
            result.Elapsed = started.Elapsed;
            return result;
        }

        private async Task ConvertOneAsync(ConvertFileItem item, ConvertOptions o, bool svt, Action<double> onPercent, CancellationToken token)
        {
            string? temp = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(item.FullPath)) throw new FileNotFoundException("The file no longer exists.");

                item.Status = ConvertStatus.Converting;
                item.Message = "";

                string planned = PlannedOutputPath(item, o);
                string dir = Path.GetDirectoryName(planned)!;
                Directory.CreateDirectory(dir);

                // Written under a temporary name in the destination folder, then
                // renamed - no half-written files if it fails or is cancelled,
                // and re-encoding a file onto its own name is safe.
                string ext = Path.GetExtension(planned);
                temp = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(planned)}.{Guid.NewGuid().ToString("N")[..8]}.converting{ext}");

                void Percent(double p)
                {
                    p = Math.Clamp(p, 0, 100);
                    item.Progress = p;
                    onPercent(p);
                }

                if (ConvertOverride != null)
                    await ConvertOverride(item, temp, o, Percent, token).ConfigureAwait(false);
                else
                    await ConvertWithEngineAsync(item.FullPath, temp, o, svt, Percent, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                    throw new InvalidOperationException(Loc.T("Str_Convert_EmptyFile", "The converter produced an empty file."));

                string final;
                lock (FinalPathLock)
                {
                    final = planned;
                    if (File.Exists(final) && !o.OverwriteExisting) final = UniquePath(final);
                    // Replacing is a single step: if it fails, the file that was there is still there.
                    File.Move(temp, final, overwrite: o.OverwriteExisting);
                }
                temp = null;

                item.OutputPath = final;
                item.Progress = 100;
                item.OutputBytes = new FileInfo(final).Length;
                item.Status = ConvertStatus.Converted;
                item.Message = Loc.F("Str_Convert_SavedAs", "Saved as {0}", final);

                if (o.DeleteOriginalAfterConversion && !string.Equals(Path.GetFullPath(final), Path.GetFullPath(item.FullPath), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        bool toBin = o.DeleteToRecycleBin && HasRecycleBin(item.FullPath);
                        DeleteOriginal(item.FullPath, o.DeleteToRecycleBin);
                        item.Message = toBin
                            ? Loc.F("Str_Convert_SavedAsBin", "Saved as {0} - original moved to the Recycle Bin.", final)
                            : Loc.F("Str_Convert_SavedAsDeleted", "Saved as {0} - original deleted.", final);
                    }
                    catch (Exception ex)
                    {
                        item.Message = Loc.F("Str_Convert_SavedAsKeepFail", "Saved as {0}, but the original couldn't be deleted: {1}", final, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                item.Status = ConvertStatus.Cancelled;
                item.Progress = 0;
                item.Message = Loc.T("Str_Convert_MsgCancelled", "Cancelled.");
            }
            catch (Exception ex)
            {
                item.Status = ConvertStatus.Failed;
                item.Progress = 0;
                item.Message = ex.Message;
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // best effort
                    }
                }
            }
        }

        private static Task ConvertWithEngineAsync(string input, string output, ConvertOptions o, bool svt, Action<double> onPercent, CancellationToken token)
        {
            switch (o.FileType)
            {
                case ConvertFileType.Video:
                    return RunFfmpegAsync(BuildVideoArgs(input, output, o, svt), ProbeDuration(input), onPercent, token);
                case ConvertFileType.Audio:
                    return RunFfmpegAsync(BuildAudioArgs(input, output, o), ProbeDuration(input), onPercent, token);
                case ConvertFileType.Image:
                    var plan = PlanImage(o);
                    return Task.Run(() =>
                    {
                        onPercent(10);
                        ConvertImageCore(input, output, plan, token);
                        onPercent(100);
                    }, token);
                default:
                    throw new NotSupportedException("Document conversion isn't available.");
            }
        }

        public static bool HasRecycleBin(string path) => Smart_Tools.WPF.Controllers.SafeFileOps.HasRecycleBin(path);

        private static void DeleteOriginal(string path, bool toRecycleBin)
        {
            if (!File.Exists(path)) return;
            if (toRecycleBin && HasRecycleBin(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }

        // =================================================================
        //  ffmpeg process
        // =================================================================

        /// <summary>Length in seconds (0 if unknown - progress then jumps from 0 to 100).</summary>
        public static double ProbeDuration(string input)
        {
            string? exe = FfprobeExe;
            if (exe == null) return 0;
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", input })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit((int)Smart_Tools.WPF.Controllers.ProbeTimeout.For(input).TotalMilliseconds))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    return 0;   // unknown length: RunFfmpegAsync should show indeterminate progress
                }
                return ParseDuration(outTask.Result);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Runs ffmpeg with "-progress pipe:1" and turns out_time into a
        /// percentage of <paramref name="durationSeconds"/> - the old version
        /// sat at 0% until each file finished.
        /// </summary>
        internal static async Task RunFfmpegAsync(List<string> args, double durationSeconds, Action<double> onPercent, CancellationToken token)
        {
            string exe = FfmpegExe ?? throw new InvalidOperationException($"ffmpeg wasn't found in {FfmpegFolder}.");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            var tail = new Queue<string>();
            long lastActivity = DateTime.UtcNow.Ticks;
            void Touch() => Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Touch();
                if (durationSeconds > 0 && ParseProgressSeconds(e.Data) is double seconds)
                    onPercent(Math.Min(99.5, seconds * 100.0 / durationSeconds));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Touch();
                lock (tail)
                {
                    tail.Enqueue(e.Data);
                    if (tail.Count > 30) tail.Dequeue();
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = process.WaitForExitAsync(CancellationToken.None);
            while (true)
            {
                var done = await Task.WhenAny(exited, Task.Delay(200, CancellationToken.None)).ConfigureAwait(false);
                if (done == exited) break;

                bool stuck = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastActivity), DateTimeKind.Utc) > FfmpegInactivityTimeout;
                if (token.IsCancellationRequested || stuck)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                    await exited.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException($"ffmpeg stopped responding for {FfmpegInactivityTimeout.TotalMinutes:0} minutes and was stopped - the file may be damaged.");
                }
            }
            process.WaitForExit();   // flushes the output events

            if (process.ExitCode != 0)
            {
                List<string> lines;
                lock (tail) lines = tail.ToList();
                throw new InvalidOperationException(FriendlyFfmpegError(lines, process.ExitCode));
            }
            onPercent(100);
        }

        /// <summary>A readable reason from the end of ffmpeg's log.</summary>
        public static string FriendlyFfmpegError(IReadOnlyList<string> stderrTail, int exitCode)
        {
            string all = string.Join("\n", stderrTail);
            if (all.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
            {
                if (all.Contains("0:a", StringComparison.Ordinal)) return "This file has no audio track.";
                if (all.Contains("0:v", StringComparison.Ordinal)) return "This file has no video track.";
            }
            if (all.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase))
                return "The file couldn't be read - it may be damaged or not really a media file.";
            if (all.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
                return "The output drive is full.";
            if (all.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return "Access denied - the output folder can't be written to.";

            string? last = stderrTail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("  ", StringComparison.Ordinal));
            return last != null
                ? $"ffmpeg failed: {last.Trim()}"
                : string.Format(CultureInfo.InvariantCulture, "ffmpeg failed (exit code {0}).", exitCode);
        }
    }
}
