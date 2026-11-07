using ImageMagick;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Makes files viewable in the Find Similar Files compare panel:
    /// playable paths for MediaElement (formats Media Foundation can't
    /// decode get a one-off H.264/AAC proxy through ffmpeg) and frozen
    /// bitmaps for images (Magick.NET fallback for HEIC and friends).
    /// Moved out of the old 2,700-line code-behind; the view only shows
    /// what this returns.
    /// </summary>
    public sealed class SimilarPreviewService : IDisposable
    {
        public const string ProxyPrefix = "SmartToolsPreview_";

        /// <summary>
        /// Commonly undecodable by classic MediaElement even with the Store
        /// codec packs installed. Converted before playback, because
        /// MediaElement doesn't reliably raise MediaFailed for them (it can
        /// just show black or play audio only).
        /// </summary>
        private static readonly HashSet<string> MediaCodecGaps = new(StringComparer.OrdinalIgnoreCase) { ".webm", ".flv", ".ogg", ".opus" };

        /// <summary>Images WIC often can't open without optional OS extensions.</summary>
        private static readonly HashSet<string> ImageCodecGaps = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".avif" };

        private const int MaxCompareImageSide = 2400;
        private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(3);

        private readonly Dictionary<string, string> _proxies = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        private static string FfmpegExe => Path.Combine(SimilarManager.FfmpegFolder, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

        public static bool NeedsConversion(string path) => MediaCodecGaps.Contains(Path.GetExtension(path));

        /// <summary>True when a proxy for <paramref name="path"/> was already made (so a second failure isn't retried).</summary>
        public bool HasProxy(string path)
        {
            lock (_lock) return _proxies.ContainsKey(path);
        }

        /// <summary>
        /// A path MediaElement can play: the file itself, or a cached proxy.
        /// Null when a needed conversion failed (no ffmpeg, broken file).
        /// <paramref name="forceConvert"/> converts any format (used after
        /// MediaElement fails on a file that wasn't on the known list).
        /// Throws OperationCanceledException only when <paramref name="token"/> is cancelled.
        /// </summary>
        public async Task<string?> GetPlayablePathAsync(string path, CancellationToken token, bool forceConvert = false)
        {
            if (!forceConvert && !NeedsConversion(path)) return path;

            lock (_lock)
            {
                if (_proxies.TryGetValue(path, out var cached) && File.Exists(cached)) return cached;
            }

            if (!File.Exists(FfmpegExe)) return null;

            string proxy = Path.Combine(Path.GetTempPath(), $"{ProxyPrefix}{Guid.NewGuid():N}.mp4");
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[] { "-nostdin", "-y", "-i", path, "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-c:a", "aac", "-movflags", "+faststart", proxy })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TranscodeTimeout);

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                process.Start();
                Log.Info($"Transcode started (pid {process.Id}) for playback: {path} -> {proxy}");
                using var kill = timeout.Token.Register(() => TryKill(process));
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);

                if (process.ExitCode != 0 || !File.Exists(proxy))
                {
                    Log.Warn($"Transcode failed (exit {process.ExitCode}) for {path}");
                    TryDelete(proxy);
                    return null;
                }

                Log.Info($"Transcode finished in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0.0}s: {path}");
                lock (_lock) _proxies[path] = proxy;
                return proxy;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Log.Info($"Transcode cancelled (superseded or overlay closed): {path}");
                TryKill(process);
                TryDelete(proxy);
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Log.Warn($"Transcode aborted after {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0.0}s (timeout or error): {path}", ex);
                TryKill(process);
                TryDelete(proxy);
                return null;
            }
        }

        /// <summary>
        /// A frozen bitmap for the compare panel that doesn't keep the file
        /// locked (so Move/Delete still work while it's shown). Large photos
        /// are decoded at up to 2400 px on the long side. Magick.NET covers
        /// HEIC/AVIF and anything WIC can't read. Null if nothing can.
        /// </summary>
        public static BitmapSource? LoadCompareImage(string path)
        {
            if (!ImageCodecGaps.Contains(Path.GetExtension(path)))
            {
                var native = TryLoadNative(path);
                if (native != null) return native;
            }
            return TryLoadViaMagick(path);
        }

        private static BitmapSource? TryLoadNative(string path)
        {
            try
            {
                int decodeWidth = 0;
                using (var stream = File.OpenRead(path))
                {
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (frame.PixelWidth > MaxCompareImageSide || frame.PixelHeight > MaxCompareImageSide)
                        decodeWidth = frame.PixelWidth >= frame.PixelHeight
                            ? MaxCompareImageSide
                            : (int)(MaxCompareImageSide * (double)frame.PixelWidth / frame.PixelHeight);
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // read now, release the file
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or UriFormatException or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        private static BitmapSource? TryLoadViaMagick(string path)
        {
            try
            {
                using var loaded = ImageLoad.Open(path, CancellationToken.None, MaxCompareImageSide);
                var image = loaded.Image;
                image.AutoOrient();
                if (image.Width > MaxCompareImageSide || image.Height > MaxCompareImageSide)
                    image.Resize(new MagickGeometry(MaxCompareImageSide, MaxCompareImageSide));   // keeps aspect

                image.Format = MagickFormat.Png;
                var ms = new MemoryStream(image.ToByteArray());
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Deletes preview proxies left in %TEMP% by a run that crashed (older than a day).</summary>
        public static int CleanupStaleProxies()
        {
            int deleted = 0;
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var file in new DirectoryInfo(Path.GetTempPath()).EnumerateFiles(ProxyPrefix + "*.mp4"))
                {
                    if (file.LastWriteTimeUtc > cutoff) continue;
                    if (TryDelete(file.FullName)) deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // best effort
            }
            return deleted;
        }

        /// <summary>Deletes this session's proxies.</summary>
        public void Dispose()
        {
            List<string> paths;
            lock (_lock)
            {
                paths = _proxies.Values.ToList();
                _proxies.Clear();
            }
            foreach (string p in paths) TryDelete(p);
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
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
            }
        }
    }
}
