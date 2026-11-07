using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Per-file signatures: OpenCV for images and video (<see cref="SimilarImageEngine"/>, <see cref="SimilarVideoEngine"/>),
    /// ffmpeg + SoundFingerprinting for audio (<see cref="SimilarAudioEngine"/>). ffmpeg/ffprobe (Engines\FFMPEG) also
    /// back up OpenCV for video codecs it can't open. Every call returns null for a file it can't read instead of failing
    /// the scan, and every external process is time-limited and killed on the way out.
    /// </summary>
    public sealed partial class SimilarManager
    {
        public static string FfmpegFolder => Smart_Tools.WPF.Controllers.FfmpegLocator.Folder;
        private static string FfmpegExe => Path.Combine(FfmpegFolder, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        private static string FfprobeExe => Path.Combine(FfmpegFolder, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        /// <summary>Audio comparison and the video fallback need both tools.</summary>
        public static bool FfmpegAvailable => File.Exists(FfmpegExe) && File.Exists(FfprobeExe);

        /// <summary>One hung ffmpeg/ffprobe must not hold a scan slot forever.</summary>
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan ImageDecodeTimeout = TimeSpan.FromSeconds(60);

        private static async Task<MediaSignature?> ComputeSignatureAsync(string path, MediaKind kind, CancellationToken token)
        {
            switch (kind)
            {
                case MediaKind.Image:
                {
                    // OpenCV / Magick cannot be cancelled: if one decode hangs, give up on that file after a
                    // while (the stuck thread is abandoned) so the scan keeps moving instead of freezing.
                    var work = Task.Run(() => SimilarImageEngine.Compute(path));
                    var done = await Task.WhenAny(work, Task.Delay(ImageDecodeTimeout, token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (done == work) return await work.ConfigureAwait(false);
                    Log.Warn($"Picture decode took over {ImageDecodeTimeout.TotalSeconds:0}s, skipped: {path}");
                    _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return null;
                }
                case MediaKind.Video:
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(DecodeTimeout);
                    return await VideoSignatureAsync(path, limit.Token).ConfigureAwait(false);
                }
                default:
                    return await AudioSignatureAsync(path, token).ConfigureAwait(false);
            }
        }

        // =================================================================
        //  Video
        // =================================================================

        private static async Task<MediaSignature?> VideoSignatureAsync(string path, CancellationToken token)
        {
            var sig = await Task.Run(() => SimilarVideoEngine.Sample(path, token), token).ConfigureAwait(false);
            if (sig != null || !FfmpegAvailable) return sig;

            // OpenCV couldn't read it: let ffmpeg decode one tiny gray frame every few seconds instead.
            var probe = await ProbeAsync(path, video: true, token).ConfigureAwait(false);
            if (probe.Duration <= 0) return null;

            int step = SimilarVideoEngine.StepFor(probe.Duration);
            byte[] raw = await RunForBytesAsync(FfmpegExe, new[]
            {
                "-threads", "2", "-i", path, "-an", "-sn", "-dn",
                "-vf", $"fps=1/{step},scale={SimilarHashing.PHashSize}:{SimilarHashing.PHashSize}:flags=area,format=gray",
                "-frames:v", SimilarVideoEngine.MaxSamples.ToString(CultureInfo.InvariantCulture),
                "-f", "rawvideo", "-pix_fmt", "gray", "-",
            }, token, DecodeTimeout).ConfigureAwait(false);

            return SimilarVideoEngine.FromRawFrames(raw, probe.Duration, probe.Width, probe.Height, step);
        }

        // =================================================================
        //  Audio
        // =================================================================

        private static async Task<MediaSignature?> AudioSignatureAsync(string path, CancellationToken token)
        {
            byte[] pcm = await RunForBytesAsync(FfmpegExe, new[]
            {
                "-i", path, "-vn", "-sn", "-dn",
                "-t", SimilarAudioEngine.MaxSeconds.ToString(CultureInfo.InvariantCulture),
                "-ac", "1", "-ar", SimilarAudioEngine.SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-",
            }, token, DecodeTimeout).ConfigureAwait(false);

            var samples = new float[pcm.Length / sizeof(float)];
            Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * sizeof(float));

            var hashes = await SimilarAudioEngine.HashAsync(samples, path).ConfigureAwait(false);
            if (hashes == null) return null;

            // The decode is capped, so the real length comes from ffprobe.
            var probe = await ProbeAsync(path, video: false, token).ConfigureAwait(false);
            return new MediaSignature
            {
                Kind = MediaKind.Audio,
                DurationSeconds = probe.Duration > 0 ? probe.Duration : (double)samples.Length / SimilarAudioEngine.SampleRate,
                AudioHashes = hashes,
            };
        }

        // =================================================================
        //  ffprobe / ffmpeg
        // =================================================================

        /// <summary>Duration (and for video, size corrected for rotation) in one ffprobe call.</summary>
        private static async Task<ProbeResult> ProbeAsync(string path, bool video, CancellationToken token)
        {
            var args = new List<string> { "-v", "error" };
            if (video) args.AddRange(new[] { "-select_streams", "v:0", "-show_entries", "stream=width,height:stream_tags=rotate:stream_side_data=rotation:format=duration" });
            else args.AddRange(new[] { "-show_entries", "format=duration" });
            args.AddRange(new[] { "-of", "default=noprint_wrappers=1", path });

            byte[] output = await RunForBytesAsync(FfprobeExe, args, token).ConfigureAwait(false);
            return ParseProbe(System.Text.Encoding.UTF8.GetString(output));
        }

        /// <summary>
        /// Runs a tool and returns its stdout. Empty on failure or on the
        /// per-file timeout; throws only when <paramref name="token"/> (the
        /// scan) is cancelled. stderr is drained in parallel so its pipe
        /// can't fill up and deadlock the process.
        /// </summary>
        internal static async Task<byte[]> RunForBytesAsync(string exe, IEnumerable<string> args, CancellationToken token, TimeSpan? limit = null)
        {
            if (!File.Exists(exe)) return Array.Empty<byte>();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(exe).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-nostdin");   // never wait on the console
                psi.ArgumentList.Add("-y");
            }
            foreach (string a in args) psi.ArgumentList.Add(a);

            var allowed = limit ?? ProcessTimeout;
            using var process = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(allowed);

            try
            {
                process.Start();
                TrySetLowPriority(process);
                using var kill = timeout.Token.Register(() => TryKill(process));

                using var buffer = new MemoryStream();
                var stdout = process.StandardOutput.BaseStream.CopyToAsync(buffer, 1 << 20, timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return buffer.ToArray();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Not logged per successful run (28k files), only the ones that go wrong.
                Log.Warn($"{Path.GetFileName(exe)} timed out after {allowed.TotalSeconds:0}s and was killed: {string.Join(' ', args.TakeLast(1))}");
                return Array.Empty<byte>();   // this file timed out - skip it
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Log.Warn($"{Path.GetFileName(exe)} failed to run: {string.Join(' ', args.TakeLast(1))}", ex);
                return Array.Empty<byte>();
            }
            finally
            {
                TryKill(process);
            }
        }

        /// <summary>A scan of thousands of files must not starve the UI and the rest of Windows.</summary>
        private static void TrySetLowPriority(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // already exited
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // never started or already gone
            }
        }
    }
}
