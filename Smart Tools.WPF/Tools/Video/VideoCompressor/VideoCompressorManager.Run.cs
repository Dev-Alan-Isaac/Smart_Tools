using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The processes: ffprobe to read a video, FFmpeg to compress or cut it.</summary>
    public static partial class VideoCompressorManager
    {
        /// <summary>Encoders that failed to start this session (no such graphics card): they are skipped from then on.</summary>
        private static readonly HashSet<string> FailedEncoders = new();

        /// <summary>A plain sentence for what is missing before a run can start, or null when everything is there.</summary>
        public static string? MissingEngine() =>
            ConverterManager.FfmpegExe == null || ConverterManager.FfprobeExe == null
                ? $"FFmpeg isn't installed with this copy of Smart Tools (it should be in {ConverterManager.FfmpegFolder})."
                : null;

        /// <summary>Length, size, and sound of one video. Never throws (except on cancel): problems go in <see cref="VcProbe.Error"/>.</summary>
        public static async Task<VcProbe> ProbeAsync(string path, CancellationToken token)
        {
            string? exe = ConverterManager.FfprobeExe;
            if (exe == null) return new VcProbe(null, 0, 0, 0, false, "ffprobe wasn't found.");

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[]
            {
                "-v", "error",
                "-show_entries", "format=duration:stream=codec_type,width,height,avg_frame_rate,r_frame_rate",
                "-of", "json", path,
            })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new VcProbe(null, 0, 0, 0, false, "ffprobe couldn't start: " + ex.Message);
            }

            var outTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                token.ThrowIfCancellationRequested();
                return new VcProbe(null, 0, 0, 0, false, "Reading the file took too long - it may be damaged.");
            }

            string json = await outTask.ConfigureAwait(false);
            string err = (await errTask.ConfigureAwait(false)).Trim();
            var parsed = ParseProbe(json);
            if (parsed != null) return parsed;

            string reason = err.Length > 0 ? err.Split('\n').Last().Trim() : $"exit code {process.ExitCode}";
            return new VcProbe(null, 0, 0, 0, false, "The file couldn't be read - it may be damaged or not really a video (" + reason + ").");
        }

        // =================================================================
        //  The run
        // =================================================================

        /// <summary>
        /// Compresses or cuts the rows that can run, one at a time (encoding already uses the whole CPU). A failed video is marked
        /// and the rest go on; canceling keeps finished files. Call on the UI thread: rows are changed here.
        /// <paramref name="resultPathFor"/> never returns an existing file.
        /// </summary>
        public static async Task<VcRunResult> RunAsync(IReadOnlyList<VcFileItem> items, VcOptions options,
                                                       Func<string, string, string> resultPathFor, IProgress<VcProgress> progress, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var pending = items.Where(i => i.CanRun).ToList();
            var files = new List<string>();
            int finished = 0, failed = 0, cancelled = 0;
            long sourceBytes = 0, resultBytes = 0;
            DateTime lastReport = DateTime.MinValue;

            void Report(string? current, double itemPercent, bool force)
            {
                if (!force && (DateTime.UtcNow - lastReport).TotalMilliseconds < 100) return;
                lastReport = DateTime.UtcNow;
                double percent = pending.Count == 0 ? 0 : (finished + failed + itemPercent / 100.0) * 100.0 / pending.Count;
                progress.Report(new VcProgress(finished, failed, pending.Count, Math.Min(100, percent), current));
            }

            foreach (var item in pending)
            {
                if (token.IsCancellationRequested)
                {
                    item.Status = VcItemStatus.Cancelled;
                    item.Message = "Canceled before it started.";
                    cancelled++;
                    continue;
                }

                Report(item.Name, 0, force: true);
                var outcome = await CompressOneAsync(item, options, resultPathFor, p =>
                {
                    item.Progress = p;
                    Report(item.Name, p, force: false);
                }, token);

                switch (outcome)
                {
                    case VcItemStatus.Done:
                        finished++;
                        files.Add(item.OutputPath);
                        sourceBytes += item.SizeBytes;
                        resultBytes += item.OutputBytes;
                        break;
                    case VcItemStatus.Cancelled:
                        cancelled++;
                        break;
                    default:
                        failed++;
                        break;
                }
                Report(item.Name, 0, force: true);
            }

            watch.Stop();
            return new VcRunResult(finished, failed, cancelled, files, sourceBytes, resultBytes, watch.Elapsed);
        }

        private static async Task<VcItemStatus> CompressOneAsync(VcFileItem item, VcOptions o, Func<string, string, string> resultPathFor,
                                                                 Action<double> onPercent, CancellationToken token)
        {
            item.Status = VcItemStatus.Working;
            item.Progress = 0;
            item.Message = "";
            item.OutputPath = "";
            item.OutputBytes = 0;

            string? temp = null;
            string passLog = Path.Combine(Path.GetTempPath(), "smarttools-vc-" + Guid.NewGuid().ToString("N"));
            try
            {
                double seconds = OutputSeconds(item, o);
                if (seconds < 0.2) throw new InvalidOperationException("The part to keep is empty. Open Trim and pick a longer part.");

                string name = OutputFileName(item, o);
                string target = resultPathFor(item.FullPath, name);
                string folder = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(folder);
                temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(target)}.compressing-{Guid.NewGuid():N}.tmp");

                string warning = "";
                if (o.Mode == VcMode.TargetSize)
                {
                    int audioKbps = o.Audio != VcAudio.Remove && item.HasAudio ? AudioKbps(o.Audio) : 0;
                    TargetVideoKbps(o.TargetMb, seconds, audioKbps, out bool reachable);
                    if (!reachable)
                        warning = $"{o.TargetMb:N0} MB is too small for {VcText.FormatTime(seconds)}, so this is as small as it gets. Pick a larger size or a shorter part.";

                    await ConverterManager.RunFfmpegAsync(BuildArguments(item, o, item.FullPath, temp, 1, passLog), seconds, p => onPercent(p * 0.4), token);
                    await ConverterManager.RunFfmpegAsync(BuildArguments(item, o, item.FullPath, temp, 2, passLog), seconds, p => onPercent(40 + p * 0.6), token);
                }
                else if (o.Mode == VcMode.TrimOnly)
                {
                    await ConverterManager.RunFfmpegAsync(BuildArguments(item, o, item.FullPath, temp, 0, passLog), seconds, onPercent, token);
                }
                else
                {
                    var encoders = await EncodersAsync(token);
                    for (int i = 0; i < encoders.Count; i++)
                    {
                        try
                        {
                            await ConverterManager.RunFfmpegAsync(BuildArguments(item, o, item.FullPath, temp, 0, passLog, encoders[i]), seconds, onPercent, token);
                            break;
                        }
                        catch (InvalidOperationException) when (i < encoders.Count - 1)
                        {
                            FailedEncoders.Add(encoders[i]);   // no such graphics card (or it refused this video): the next one
                            TryDelete(temp);
                            onPercent(0);
                        }
                    }
                }

                string final = MoveIntoPlace(temp, resultPathFor(item.FullPath, name), () => resultPathFor(item.FullPath, name));
                temp = null;
                item.OutputPath = final;
                item.OutputBytes = FileSize(final);

                string result = VcText.FormatBytes(item.OutputBytes);
                if (item.SizeBytes > 0 && o.Mode != VcMode.TrimOnly)
                {
                    int saved = (int)Math.Round((1 - (double)item.OutputBytes / item.SizeBytes) * 100);
                    result += saved > 0 ? $" ({saved}% smaller)" : " (larger)";
                    if (saved <= 0 && warning.Length == 0) warning = "The result is not smaller than the original. Try Small quality, a lower resolution, or a target size.";
                }
                item.ResultText = result;
                item.Message = warning.Length > 0 ? warning : "Saved " + Path.GetFileName(final);
                item.Progress = 100;
                item.Status = VcItemStatus.Done;
                return VcItemStatus.Done;
            }
            catch (OperationCanceledException)
            {
                item.Status = VcItemStatus.Cancelled;
                item.Message = "Canceled. Finished files are kept.";
                return VcItemStatus.Cancelled;
            }
            catch (Exception ex)
            {
                item.Status = VcItemStatus.Failed;
                item.Message = ex.Message;
                return VcItemStatus.Failed;
            }
            finally
            {
                if (temp != null) TryDelete(temp);
                DeletePassFiles(passLog);
            }
        }

        /// <summary>The encoders to try for Quality mode: the graphics card first, the processor last. Windows' own (h264_mf) takes a bitrate, not a quality, so it is left out.</summary>
        private static async Task<List<string>> EncodersAsync(CancellationToken token)
        {
            string? ffmpeg = ConverterManager.FfmpegExe;
            if (ffmpeg == null) return new List<string> { "libx264" };
            var order = await VideoReplayManager.EncoderOrderAsync(ffmpeg, token);
            var usable = order.Where(e => e != "h264_mf" && (e == "libx264" || !FailedEncoders.Contains(e))).ToList();
            if (!usable.Contains("libx264")) usable.Add("libx264");
            return usable;
        }

        /// <summary>Moves the finished file to its name without overwriting anything. If the name was taken in the meantime, asks for another.</summary>
        private static string MoveIntoPlace(string temp, string final, Func<string> nextName)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, final, overwrite: false);
                    return final;
                }
                catch (IOException) when (attempt < 5 && File.Exists(final))
                {
                    final = nextName();
                }
            }
        }

        private static long FileSize(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        private static void TryDelete(string? path)
        {
            try
            {
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>x264's two-pass statistics: "&lt;prefix&gt;-0.log" and "&lt;prefix&gt;-0.log.mbtree".</summary>
        private static void DeletePassFiles(string prefix)
        {
            try
            {
                string dir = Path.GetDirectoryName(prefix)!;
                foreach (string file in Directory.GetFiles(dir, Path.GetFileName(prefix) + "*")) TryDelete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
