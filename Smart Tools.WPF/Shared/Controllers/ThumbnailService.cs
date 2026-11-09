using ImageMagick;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Engines
{
    /// <summary>
    /// Generates small preview thumbnails for images and videos.
    /// Images go through Magick.NET (broad format coverage - HEIC, RAW, TIFF,
    /// PSD, WebP, etc, not just what WPF's own codecs support natively).
    /// Videos are handled by shelling out to the ffmpeg/ffprobe binaries under
    /// Engines\FFMPEG - a frame is piped straight off ffmpeg's stdout as PNG,
    /// no temp files involved.
    /// </summary>
    public static class ThumbnailService
    {
        // The bundled copy first, then one on PATH (a copy of the app without Engines\FFMPEG still gets video thumbnails).
        private static string FfmpegExe => Smart_Tools.WPF.Controllers.FfmpegLocator.Resolve("ffmpeg") ?? Smart_Tools.WPF.Controllers.FfmpegLocator.FfmpegExe;
        private static string FfprobeExe => Smart_Tools.WPF.Controllers.FfmpegLocator.Resolve("ffprobe") ?? Smart_Tools.WPF.Controllers.FfmpegLocator.FfprobeExe;

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff",
            ".webp", ".heic", ".heif", ".ico", ".psd", ".cr2", ".nef", ".arw", ".dng",
            ".jfif", ".avif", ".jxl", ".tga", ".dds"
        };

        private static readonly SemaphoreSlim VideoGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

        public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
        public static bool SupportsThumbnail(string path) => IsVideo(path) || IsImage(path);

        /// <summary>
        /// Returns a frozen (cross-thread-safe) thumbnail, or null if the file
        /// isn't a supported type or the file couldn't be read/decoded.
        /// Never throws - a bad or corrupt file just means no thumbnail,
        /// it should never take down a scan of thousands of other files.
        /// </summary>
        public static async Task<BitmapSource?> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken)
        {
            try
            {
                if (IsImage(path))
                    return await GetImageThumbnailAsync(path, size, cancellationToken);

                if (IsVideo(path))
                {
                    // A big grid asks for hundreds of frames at once: cap how many ffmpeg processes run together.
                    await VideoGate.WaitAsync(cancellationToken);
                    try
                    {
                        return await GetVideoThumbnailAsync(path, size, cancellationToken);
                    }
                    finally
                    {
                        VideoGate.Release();
                    }
                }
            }
            catch
            {
                // Swallowed intentionally - see summary above.
            }

            return null;
        }

        // ---------------- Images (Magick.NET) ----------------

        private static async Task<BitmapSource?> GetImageThumbnailAsync(string path, int size, CancellationToken cancellationToken)
        {
            // Decoding straight into a bounded raster (rather than full
            // resolution then downscaling) keeps memory sane when a duplicate
            // set includes a 40MP photo or an uncompressed RAW file.
            // (MagickReadSettings.Width/Height do not shrink a real photo; ImageLoad decodes JPEGs reduced and
            // holds the bytes in the shared memory budget.)
            byte[] pngBytes = await Task.Run(() =>
            {
                using var loaded = ImageLoad.Open(path, cancellationToken, (uint)(size * 4));
                var image = loaded.Image;
                image.AutoOrient();
                image.Thumbnail(new MagickGeometry((uint)size, (uint)size));
                image.Format = MagickFormat.Png;
                return image.ToByteArray();
            }, cancellationToken);

            return DecodeToBitmapSource(pngBytes);
        }

        // ---------------- Videos (ffmpeg / ffprobe) ----------------

        private static async Task<BitmapSource?> GetVideoThumbnailAsync(string path, int size, CancellationToken cancellationToken)
        {
            if (!File.Exists(FfmpegExe))
                return null;

            // Canceled while queued: don't start ffprobe/ffmpeg at all (the caller's catch turns this into "no thumbnail").
            cancellationToken.ThrowIfCancellationRequested();

            double seekSeconds = await GetSeekPointAsync(path, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = FfmpegExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(seekSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale={size}:-1");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("image2pipe");
            psi.ArgumentList.Add("-vcodec");
            psi.ArgumentList.Add("png");
            psi.ArgumentList.Add("-");

            using var process = new Process { StartInfo = psi };

            // A handful of malformed/unusual videos make ffmpeg hang instead of
            // erroring out. Without a bound here, one bad file would stall this
            // call (and, transitively, whatever batch is awaiting it) forever.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProcessTimeout);

            try
            {
                process.Start();

                // Standard output is read as raw bytes (the PNG frame); standard
                // error is drained in parallel purely so its pipe never fills up
                // and deadlocks the process - ffmpeg logs everything to stderr.
                var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync();

                // Awaiting both together means neither task is left unobserved if the other one throws.
                await Task.WhenAll(stdoutTask, stderrTask);
                byte[] pngBytes = stdoutTask.Result;
                await process.WaitForExitAsync(timeoutCts.Token);

                return pngBytes.Length == 0 ? null : DecodeToBitmapSource(pngBytes);
            }
            finally
            {
                // Whether we finished normally, hit our own timeout, or the
                // caller's token was cancelled, make sure ffmpeg doesn't outlive
                // this call - otherwise a large batch scan can accumulate
                // orphaned ffmpeg processes. No-op if it already exited.
                TryKillProcessTree(process);
            }
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited between the check and the kill, or couldn't be
                // killed for some other reason - nothing more useful to do.
            }
        }

        // Bounds how long a single ffmpeg/ffprobe invocation is allowed to run
        // before it's treated as hung and killed. Generous enough for normal
        // seeking/probing, short enough that one bad file in a large batch
        // scan doesn't stall everything behind it.
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

        private static async Task<double> GetSeekPointAsync(string path, CancellationToken cancellationToken)
        {
            const double fallbackSeconds = 1.0;

            if (!File.Exists(FfprobeExe))
                return fallbackSeconds;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfprobeExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-show_entries");
            process.StartInfo.ArgumentList.Add("format=duration");
            process.StartInfo.ArgumentList.Add("-of");
            process.StartInfo.ArgumentList.Add("csv=p=0");
            process.StartInfo.ArgumentList.Add(path);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProcessTimeout);

            try
            {
                process.Start();

                // Read both streams concurrently rather than stdout-then-stderr:
                // sequential reads risk a pipe deadlock if a future change makes
                // either stream chatty enough to fill its OS buffer while the
                // other side isn't being drained yet.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(timeoutCts.Token);

                string output = await stdoutTask;
                _ = await stderrTask;

                if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) && duration > 0)
                {
                    // ~10% into the clip tends to skip a black/blank intro
                    // frame; capped so a very short clip can't seek past its own end.
                    return Math.Clamp(duration * 0.1, 0, Math.Max(0, duration - 0.1));
                }
            }
            catch
            {
                // Fall through to the fixed fallback below - covers our own
                // timeout firing (hung ffprobe), a real cancellation, or any
                // parsing/process-start failure alike.
            }
            finally
            {
                // Don't let a hung/timed-out ffprobe linger past this call.
                TryKillProcessTree(process);
            }

            return fallbackSeconds;
        }

        // ---------------- Shared helpers ----------------

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }

        private static BitmapSource? DecodeToBitmapSource(byte[] bytes)
        {
            if (bytes.Length == 0)
                return null;

            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze(); // makes it safe to hand from a background thread to the UI thread
            return frame;
        }
    }
}