using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Running ffprobe and ffmpeg for the Video Stitcher.</summary>
    public static partial class VideoEditorManager
    {
        // =================================================================
        //  Probing
        // =================================================================

        private static readonly SemaphoreSlim ProbeGate = new(3);

        /// <summary>Codecs, size, frame rate, sound and length of a clip; null when ffprobe can't read it.</summary>
        public static async Task<VideoProbe?> ProbeAsync(string path, CancellationToken token)
        {
            string? exe = VideoReplayManager.FfprobeExe;
            if (exe == null) return null;
            await ProbeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var run = await RunToolAsync(exe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, null, ProbeTimeout.For(path), token).ConfigureAwait(false);
                return run.ExitCode == 0 ? ParseProbe(run.Output) : null;
            }
            finally
            {
                ProbeGate.Release();
            }
        }

        // =================================================================
        //  Stitching
        // =================================================================

        /// <summary>
        /// Makes the video from the ticked clips, in list order. Clips that
        /// match are joined as they are (seconds, no quality loss); otherwise
        /// each clip is fitted to one size, frame rate and sound format and
        /// then joined. A clip that can't be read is left out with the reason
        /// (the old code failed the whole video). Everything is written under
        /// temporary names and renamed at the end, so Cancel or an error
        /// leaves nothing half-made and never replaces an existing file.
        /// </summary>
        public static Task<EditResult> RunAsync(IReadOnlyList<EditClip> clips, EditOptions options, IProgress<EditProgress>? progress = null, CancellationToken token = default) =>
            Task.Run(() => RenderAsync(clips, options, null, progress, token));

        /// <summary>%TEMP%\Smart Tools\Editor previews - emptied on close and before each new preview.</summary>
        public static string PreviewFolder => Path.Combine(Path.GetTempPath(), "Smart Tools", "Editor previews");

        private static string WorkRoot => Path.Combine(Path.GetTempPath(), "Smart Tools", "Editor work");

        /// <summary>
        /// A small, quick version of the whole result (at most 640 × 360,
        /// fades and trims included) to watch before the real thing. Returns
        /// its path; throws with the reason when nothing could be made.
        /// </summary>
        public static async Task<string> RenderPreviewAsync(IReadOnlyList<EditClip> clips, double fadeSeconds, IProgress<EditProgress>? progress = null, CancellationToken token = default)
        {
            SweepPreviews();
            Directory.CreateDirectory(PreviewFolder);
            string path = Path.Combine(PreviewFolder, $"preview-{Guid.NewGuid():N}.mp4");
            var options = new EditOptions { OutputFolder = PreviewFolder, FadeSeconds = fadeSeconds, Format = EditOutputFormat.Mp4 };
            var result = await Task.Run(() => RenderAsync(clips, options, path, progress, token), token).ConfigureAwait(false);
            if (result.Cancelled) throw new OperationCanceledException(token);
            if (result.Files.Count == 0) throw new InvalidOperationException(result.Error ?? "No preview could be made.");
            return result.Files[0];
        }

        /// <summary>Deletes old previews and leftovers of interrupted runs (files in use are skipped).</summary>
        public static int SweepPreviews()
        {
            int deleted = 0;
            foreach (string folder in new[] { PreviewFolder, WorkRoot })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // playing right now
                    }
                }
            }
            return deleted;
        }

        /// <summary>Hardware encoders that failed this session (no such card) - not tried again.</summary>
        private static readonly HashSet<string> FailedEncoders = new();

        private static async Task<EditResult> RenderAsync(IReadOnlyList<EditClip> clips, EditOptions options, string? previewPath, IProgress<EditProgress>? progress, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            var result = new EditResult();
            bool preview = previewPath != null;
            var work = clips.Where(c => c.IsIncluded).ToList();
            if (!preview)
                foreach (var clip in work)
                {
                    clip.Status = EditClipStatus.Ready;
                    clip.Message = "";
                }

            string runId = Guid.NewGuid().ToString("N")[..8];
            string workDir = Path.Combine(WorkRoot, runId);
            var finished = new List<(string Temp, List<(EditClip Clip, VideoProbe Probe)> Clips, double Seconds)>();
            var failed = new HashSet<EditClip>();   // left out of this run

            void Report(double done, double total, string text) =>
                progress?.Report(new EditProgress(total > 0 ? Math.Clamp(done * 100 / total, 0, 100) : 0, text));

            void SetStatus(EditClip clip, EditClipStatus status, string message = "")
            {
                if (preview) return;   // a preview doesn't touch the list's statuses
                clip.Status = status;
                clip.Message = message;
            }

            try
            {
                if (work.Count == 0)
                {
                    result.Error = "Tick at least one clip.";
                    return result;
                }
                string? ffmpeg = VideoReplayManager.FfmpegExe;
                if (ffmpeg == null || VideoReplayManager.FfprobeExe == null)
                {
                    result.Error = $"ffmpeg and ffprobe are needed to make videos - put them in {VideoReplayManager.FfmpegFolder}.";
                    return result;
                }

                if (!preview && options.OverwriteAllowed)
                {
                    string target = Path.GetFullPath(OutputNames(options.OutputFolder, options.FileName, options.Format, 1)[0]);
                    if (work.Any(c => string.Equals(Path.GetFullPath(c.FullPath), target, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Error = "The new video can't replace one of the clips it's made from - choose another name.";
                        return result;
                    }
                }

                // 1. Read every clip first (the old code only knew the lengths of clips it had played).
                Report(0, 1, "Checking the clips…");
                var probes = work.Select(c => ProbeAsync(c.FullPath, token)).ToList();
                var usable = new List<(EditClip Clip, VideoProbe Probe)>();
                for (int i = 0; i < work.Count; i++)
                {
                    var clip = work[i];
                    SetStatus(clip, EditClipStatus.Checking);
                    var probe = await probes[i].ConfigureAwait(false);
                    string? problem = probe switch
                    {
                        null => "The clip is damaged or not a video ffmpeg can read.",
                        { HasVideo: false } => "This file has no picture (only sound).",
                        { Seconds: <= 0 } => "ffmpeg couldn't tell how long this clip is.",
                        _ => null,
                    };
                    if (problem != null)
                    {
                        SetStatus(clip, EditClipStatus.Failed, problem);
                        result.Failed++;
                        continue;
                    }
                    clip.Duration ??= TimeSpan.FromSeconds(probe!.Seconds);
                    SetStatus(clip, EditClipStatus.Ready);
                    usable.Add((clip, probe!));
                }
                token.ThrowIfCancellationRequested();
                if (usable.Count == 0)
                {
                    result.Error = "None of the ticked clips could be read, so no video was made. Hover a clip's status for the reason.";
                    return result;
                }

                // 2. Decide how.
                double Length((EditClip Clip, VideoProbe Probe) u) => UsedPart(u.Clip, u.Probe.Seconds).Length;
                bool withAudio = usable.Any(u => u.Probe.HasAudio);
                bool copy = !preview && CanJoinWithoutReencoding(usable, options, out _);
                var frame = preview ? PreviewFrame(usable[0].Probe) : TargetFrame(usable[0].Probe, options.Size);
                var segments = preview
                    ? new List<List<(EditClip Clip, VideoProbe Probe)>> { usable }
                    : BuildSegments(usable, Length, options.SplitSeconds);
                double total = usable.Sum(Length);
                double done = 0;

                string outDir = preview ? Path.GetDirectoryName(previewPath)! : options.OutputFolder;
                Directory.CreateDirectory(outDir);
                Directory.CreateDirectory(workDir);
                string stem = CleanFileName(preview ? "preview" : options.FileName);
                var encoders = preview ? new List<(string Name, IReadOnlyList<string> Args)> { ("libx264", PreviewVideoArgs) } : await EncodersAsync(ffmpeg, options.Format, token).ConfigureAwait(false);
                var audioArgs = AudioArgs(options.Format, preview);
                var (partMuxer, partExt) = PartContainer(preview ? EditOutputFormat.Mp4 : options.Format);

                // 3. Make each file.
                for (int s = 0; s < segments.Count; s++)
                {
                    token.ThrowIfCancellationRequested();
                    var segment = segments[s];
                    string temp = Path.Combine(outDir, $".{stem}.creating-{runId}-{s + 1}.tmp");
                    double segmentStart = done;
                    double segmentSeconds = segment.Sum(Length);
                    string part = segments.Count > 1 ? $" (file {s + 1} of {segments.Count})" : "";
                    bool joined = false;

                    if (copy)
                    {
                        foreach (var u in segment) SetStatus(u.Clip, EditClipStatus.Working);
                        string list = Path.Combine(workDir, $"join-{s + 1}.txt");
                        await File.WriteAllTextAsync(list, ConcatList(segment.Select(u => u.Clip.FullPath)), new UTF8Encoding(false), token).ConfigureAwait(false);
                        var run = await RunToolAsync(ffmpeg, JoinArgs(list, options.Format, withAudio, temp), line =>
                        {
                            if (ParseProgressSeconds(line) is double t) Report(segmentStart + t, total, $"Joining the clips{part}…");
                        }, Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                        if (run.ExitCode == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
                        {
                            joined = true;
                            result.JoinedWithoutReencoding = true;
                            done = segmentStart + segmentSeconds;
                        }
                        else
                        {
                            // The clips looked alike but ffmpeg disagreed - fit them instead (from here on).
                            TryDelete(temp);
                            copy = false;
                            result.JoinedWithoutReencoding = false;
                        }
                    }

                    if (!joined)
                    {
                        var parts = new List<string>();
                        foreach (var u in segment)
                        {
                            token.ThrowIfCancellationRequested();
                            var (start, length) = UsedPart(u.Clip, u.Probe.Seconds);
                            SetStatus(u.Clip, EditClipStatus.Working);
                            string partFile = Path.Combine(workDir, $"part-{parts.Count + 1}-{s + 1}{partExt}");
                            int index = usable.IndexOf(u) + 1;
                            double clipStart = done;
                            string? error = null;
                            while (encoders.Count > 0)
                            {
                                var (name, videoArgs) = encoders[0];
                                var run = await RunToolAsync(ffmpeg, NormalizeArgs(u.Clip.FullPath, u.Probe, start, length, frame, options.FadeSeconds, withAudio, videoArgs, audioArgs, partFile, partMuxer), line =>
                                {
                                    if (ParseProgressSeconds(line) is double t) Report(clipStart + Math.Min(t, length), total, $"Clip {index:N0} of {usable.Count:N0}{part} · {u.Clip.Name}");
                                }, Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                                if (run.ExitCode == 0 && File.Exists(partFile) && new FileInfo(partFile).Length > 0)
                                {
                                    result.Encoder ??= name;
                                    error = null;
                                    break;
                                }
                                error = PlainFfmpegError(run.Errors);
                                TryDelete(partFile);
                                // A graphics-card encoder failing usually means there's no such card - try the next one.
                                if (encoders.Count > 1 && name.StartsWith("h264_", StringComparison.Ordinal))
                                {
                                    lock (FailedEncoders) FailedEncoders.Add(name);
                                    encoders.RemoveAt(0);
                                    continue;
                                }
                                break;
                            }
                            done = clipStart + length;
                            if (error != null)
                            {
                                SetStatus(u.Clip, EditClipStatus.Failed, error);
                                failed.Add(u.Clip);
                                result.Failed++;
                                continue;
                            }
                            parts.Add(partFile);
                        }
                        if (parts.Count == 0) continue;

                        string list = Path.Combine(workDir, $"join-{s + 1}.txt");
                        await File.WriteAllTextAsync(list, ConcatList(parts), new UTF8Encoding(false), token).ConfigureAwait(false);
                        Report(done, total, $"Joining{part}…");
                        var join = await RunToolAsync(ffmpeg, JoinArgs(list, options.Format, withAudio, temp), null, Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                        if (join.ExitCode != 0 || !File.Exists(temp)) throw new InvalidOperationException("Joining the clips failed: " + PlainFfmpegError(join.Errors));
                        foreach (string p in parts) TryDelete(p);   // free the space as we go
                    }

                    var made = segment.Where(u => !failed.Contains(u.Clip)).ToList();
                    finished.Add((temp, made, made.Sum(Length)));
                }

                token.ThrowIfCancellationRequested();
                if (finished.Count == 0)
                {
                    result.Error = "None of the clips could be made into a video. Hover a clip's status for the reason.";
                    return result;
                }

                // 4. Only now do the files get their real names.
                if (preview)
                {
                    File.Move(finished[0].Temp, previewPath!, overwrite: true);
                    result.Files.Add(previewPath!);
                }
                else
                {
                    var names = OutputNames(options.OutputFolder, options.FileName, options.Format, finished.Count);
                    for (int i = 0; i < finished.Count; i++)
                    {
                        bool replace = options.OverwriteAllowed && finished.Count == 1;
                        string target = replace ? names[i] : UniquePath(names[i]);
                        File.Move(finished[i].Temp, target, overwrite: replace);
                        result.Files.Add(target);
                        foreach (var u in finished[i].Clips)
                            SetStatus(u.Clip, EditClipStatus.Done, finished.Count == 1 ? $"In {Path.GetFileName(target)}" : $"In {Path.GetFileName(target)} (file {i + 1} of {finished.Count})");
                    }
                }
                result.ClipsUsed = finished.Sum(f => f.Clips.Count);
                result.Length = TimeSpan.FromSeconds(finished.Sum(f => f.Seconds));
                Report(1, 1, "Done");
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                foreach (var clip in work.Where(c => c.Status is EditClipStatus.Ready or EditClipStatus.Checking or EditClipStatus.Working or EditClipStatus.Done))
                    SetStatus(clip, EditClipStatus.Cancelled, "Cancelled - nothing was saved.");
                return result;
            }
            finally
            {
                foreach (var f in finished) TryDelete(f.Temp);   // moved ones are already gone
                try
                {
                    if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // swept on the next start
                }
                // A cancelled join leaves its temporary output behind - remove any of this run's.
                string dir = preview ? Path.GetDirectoryName(previewPath)! : options.OutputFolder;
                try
                {
                    if (Directory.Exists(dir))
                        foreach (string leftover in Directory.EnumerateFiles(dir, $".*.creating-{runId}-*.tmp")) TryDelete(leftover);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }
                result.Elapsed = clock.Elapsed;
            }
        }

        /// <summary>Video encoders to try: VP9 for WebM; otherwise the graphics card first (shared with Video Replay), the processor last.</summary>
        private static async Task<List<(string Name, IReadOnlyList<string> Args)>> EncodersAsync(string ffmpeg, EditOutputFormat format, CancellationToken token)
        {
            if (format == EditOutputFormat.Webm) return new() { ("libvpx-vp9", WebmVideoArgs) };
            var order = await VideoReplayManager.EncoderOrderAsync(ffmpeg, token).ConfigureAwait(false);
            lock (FailedEncoders) order.RemoveAll(e => FailedEncoders.Contains(e) && e != "libx264");
            if (!order.Contains("libx264")) order.Add("libx264");
            return order.Select(e => (e, (IReadOnlyList<string>)VideoReplayManager.EncoderArgs(e))).ToList();
        }

        /// <summary>Tests: forget which graphics-card encoders failed.</summary>
        public static void ResetEncoderCache()
        {
            lock (FailedEncoders) FailedEncoders.Clear();
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

        // =================================================================
        //  Running a tool
        // =================================================================

        private const long StallLimitMs = 15 * 60 * 1000;

        private sealed record ToolRun(int ExitCode, string Output, List<string> Errors, bool TimedOut);

        /// <summary>
        /// Runs ffmpeg/ffprobe with both outputs read as they come (no
        /// deadlock), kills it on Cancel (the old stitch left ffmpeg running
        /// when the app closed mid-run) and keeps the last error lines.
        /// </summary>
        private static async Task<ToolRun> RunToolAsync(string exe, IEnumerable<string> args, Action<string>? onOutputLine, TimeSpan timeout, CancellationToken token)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            var output = new StringBuilder();
            var errors = new List<string>();
            long lastAdvance = Environment.TickCount64;   // when ffmpeg's progress last moved (stall watchdog)
            string lastProgress = "";
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                // -progress repeats its lines even when stuck, so only a changed position or size counts as life.
                if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) || e.Data.StartsWith("total_size=", StringComparison.Ordinal))
                {
                    if (!string.Equals(e.Data, lastProgress, StringComparison.Ordinal) && !e.Data.EndsWith("N/A", StringComparison.Ordinal))
                    {
                        Volatile.Write(ref lastAdvance, Environment.TickCount64);
                        if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal)) lastProgress = e.Data;
                    }
                }
                lock (output) output.AppendLine(e.Data);
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
            try
            {
                // A long clip keeps ffmpeg on every core for minutes - below normal keeps the app and player responsive meanwhile.
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception) { /* already exited, or not allowed - runs at normal priority */ }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (timeout != Timeout.InfiniteTimeSpan) limit.CancelAfter(timeout);
            // No fixed limit means a long render is allowed - but not a stuck one: kill after 15 min with no progress.
            bool stalled = false;
            Task? watchdog = null;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                watchdog = Task.Run(async () =>
                {
                    try
                    {
                        while (!limit.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), limit.Token).ConfigureAwait(false);
                            if (Environment.TickCount64 - Volatile.Read(ref lastAdvance) > StallLimitMs)
                            {
                                stalled = true;
                                limit.Cancel();
                            }
                        }
                    }
                    catch (OperationCanceledException) { /* finished or cancelled */ }
                });
            }
            try
            {
                await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (errors)
                {
                    if (stalled) errors.Add("ffmpeg stopped making progress and was ended.");
                    return new ToolRun(-1, "", errors.ToList(), true);
                }
            }
            finally
            {
                limit.Cancel();   // ends the watchdog
                if (watchdog != null) await watchdog.ConfigureAwait(false);
            }
            p.WaitForExit();   // flush the output events
            lock (output) lock (errors) return new ToolRun(p.ExitCode, output.ToString(), errors.ToList(), false);
        }
    }
}
