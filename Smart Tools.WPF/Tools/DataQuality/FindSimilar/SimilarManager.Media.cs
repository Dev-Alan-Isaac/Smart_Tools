using ImageMagick;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Decoding for fingerprints: Magick.NET for images, ffmpeg/ffprobe
    /// (Engines\FFMPEG) for video frames and audio PCM. Every call returns
    /// null for a file it can't read instead of failing the scan, and every
    /// external process is time-limited and killed on the way out.
    /// </summary>
    public sealed partial class SimilarManager
    {
        public static string FfmpegFolder => Smart_Tools.WPF.Controllers.FfmpegLocator.Folder;
        private static string FfmpegExe => Path.Combine(FfmpegFolder, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        private static string FfprobeExe => Path.Combine(FfmpegFolder, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        /// <summary>Video and audio comparison need both tools.</summary>
        public static bool FfmpegAvailable => File.Exists(FfmpegExe) && File.Exists(FfprobeExe);

        private const int VideoSampleCount = 5;
        private const int VideoFrameWidth = 64;
        private const int AudioAnalyzeSeconds = 60;
        private const int ImageDecodeHint = 512;

        /// <summary>One hung ffmpeg/ffprobe must not hold a scan slot forever.</summary>
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

        private static async Task<FileFingerprint?> ComputeFingerprintAsync(string path, SimilarDetectionMethod method, CancellationToken token)
        {
            switch (KindOf(method))
            {
                case MediaKind.Image:
                    return await Task.Run(() => ImageFingerprint(path, method), token).ConfigureAwait(false);
                case MediaKind.Video:
                    return await VideoFingerprintAsync(path, token).ConfigureAwait(false);
                default:
                    return await AudioFingerprintAsync(path, token).ConfigureAwait(false);
            }
        }

        // =================================================================
        //  Images
        // =================================================================

        /// <summary>
        /// Decodes once, small: the old code decoded every photo at full
        /// resolution (a 40 MP RAW is hundreds of MB, times the number of
        /// files in flight) and then cloned it three times. Now the size
        /// comes from a header read and the pixels from a reduced decode.
        /// </summary>
        private static FileFingerprint? ImageFingerprint(string path, SimilarDetectionMethod method)
        {
            try
            {
                // Reduced JPEG decode under the byte budget (Width/Height in MagickReadSettings do not shrink a real photo).
                using var loaded = ImageLoad.Open(path, CancellationToken.None, ImageDecodeHint);
                var image = loaded.Image;
                image.AutoOrient();

                // Header size is before rotation; follow the decoded orientation.
                int width = (int)loaded.SourceWidth, height = (int)loaded.SourceHeight;
                if ((image.Width > image.Height) != (width > height) && image.Width != image.Height) (width, height) = (height, width);

                using var small = (MagickImage)image.Clone();
                small.Resize(new MagickGeometry(128, 128) { IgnoreAspectRatio = true });
                small.ColorSpace = ColorSpace.Gray;

                var grid32 = Grid(small, SimilarHashing.PHashSize, SimilarHashing.PHashSize);
                var grid9x8 = Grid(small, 9, 8);
                var grid16 = Grid(small, SimilarHashing.VarianceGridSize, SimilarHashing.VarianceGridSize);

                ulong primary = method switch
                {
                    SimilarDetectionMethod.DHash => SimilarHashing.DHash(grid9x8),
                    SimilarDetectionMethod.WHash => SimilarHashing.WHash(grid32),
                    _ => SimilarHashing.PHash(grid32),
                };
                // The other family as a cross-check (pHash and dHash fail on different images).
                ulong secondary = method == SimilarDetectionMethod.DHash ? SimilarHashing.PHash(grid32) : SimilarHashing.DHash(grid9x8);

                return new FileFingerprint
                {
                    Fingerprint = new Fingerprint(primary),
                    SecondaryHash = secondary,
                    Variance = SimilarHashing.Variance(grid16),
                    Width = width,
                    Height = height,
                };
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Grayscale grid 0-255 at the given size (aspect ignored, like every perceptual hash).</summary>
        private static double[,] Grid(MagickImage gray, int width, int height)
        {
            using var work = (MagickImage)gray.Clone();
            work.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });

            var grid = new double[height, width];
            using var pixels = work.GetPixels();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    grid[y, x] = pixels.GetPixel(x, y).GetChannel(0) / (double)Quantum.Max * 255.0;
            return grid;
        }

        // =================================================================
        //  Video
        // =================================================================

        private static async Task<FileFingerprint?> VideoFingerprintAsync(string path, CancellationToken token)
        {
            var probe = await ProbeAsync(path, video: true, token).ConfigureAwait(false);
            if (probe.Duration <= 0) return null;

            var hashes = new List<ulong>(VideoSampleCount);
            for (int i = 1; i <= VideoSampleCount; i++)
            {
                double t = probe.Duration * i / (VideoSampleCount + 1);
                string seek = t.ToString("0.###", CultureInfo.InvariantCulture);

                // Keyframe-only, single-threaded decode keeps memory per process small
                // (4K/HEVC with one decoder thread per core is what ate RAM on big scans).
                byte[] png = await RunForBytesAsync(FfmpegExe, new[]
                {
                    "-threads", "1", "-skip_frame", "nokey", "-ss", seek, "-i", path,
                    "-an", "-sn", "-dn", "-threads", "1",
                    "-frames:v", "1", "-vf", $"scale={VideoFrameWidth}:-2", "-f", "image2pipe", "-vcodec", "png", "-",
                }, token).ConfigureAwait(false);

                if (png.Length == 0)
                {
                    // Some containers flag no keyframes; retry with a normal (still single-threaded) decode.
                    png = await RunForBytesAsync(FfmpegExe, new[]
                    {
                        "-threads", "1", "-ss", seek, "-i", path,
                        "-an", "-sn", "-dn", "-threads", "1",
                        "-frames:v", "1", "-vf", $"scale={VideoFrameWidth}:-2", "-f", "image2pipe", "-vcodec", "png", "-",
                    }, token).ConfigureAwait(false);
                }
                if (png.Length == 0) continue;

                try
                {
                    using var frame = new MagickImage(png);
                    frame.ColorSpace = ColorSpace.Gray;
                    hashes.Add(SimilarHashing.DHash(Grid(frame, 9, 8)));
                }
                catch (MagickException)
                {
                    // unreadable frame - keep the others
                }
            }

            if (hashes.Count == 0) return null;
            return new FileFingerprint
            {
                Fingerprint = new Fingerprint(hashes.ToArray()),
                DurationSeconds = probe.Duration,
                Width = probe.Width,
                Height = probe.Height,
            };
        }

        // =================================================================
        //  Audio
        // =================================================================

        private static async Task<FileFingerprint?> AudioFingerprintAsync(string path, CancellationToken token)
        {
            byte[] pcmBytes = await RunForBytesAsync(FfmpegExe, new[]
            {
                "-i", path, "-t", AudioAnalyzeSeconds.ToString(CultureInfo.InvariantCulture),
                "-ac", "1", "-ar", SimilarHashing.AudioSampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-",
            }, token).ConfigureAwait(false);

            var pcm = new short[pcmBytes.Length / 2];
            Buffer.BlockCopy(pcmBytes, 0, pcm, 0, pcm.Length * 2);
            var hashes = SimilarHashing.AudioFingerprint(pcm);
            if (hashes == null) return null;

            // Only the first minute is decoded, so the real length needs ffprobe.
            var probe = await ProbeAsync(path, video: false, token).ConfigureAwait(false);
            return new FileFingerprint { Fingerprint = new Fingerprint(hashes), DurationSeconds = probe.Duration };
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
        private static async Task<byte[]> RunForBytesAsync(string exe, IEnumerable<string> args, CancellationToken token)
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

            using var process = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProcessTimeout);

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
                Log.Warn($"{Path.GetFileName(exe)} timed out after {ProcessTimeout.TotalSeconds:0}s and was killed: {string.Join(' ', args.TakeLast(1))}");
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
