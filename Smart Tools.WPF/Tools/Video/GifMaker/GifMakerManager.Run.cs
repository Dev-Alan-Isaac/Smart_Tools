using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>GIF Maker - ffprobe, frame grabs, making the GIF (video or images), the size estimate and the work folder.</summary>
    public static partial class GifMakerManager
    {
        public static string FfmpegFolder => ConverterManager.FfmpegFolder;

        /// <summary>Working files for this session live here; deleted on close, leftovers older than a day on the next start.</summary>
        public static string WorkRoot => Path.Combine(Path.GetTempPath(), "Smart Tools", "GIF Maker");

        /// <summary>Null when ffmpeg and ffprobe are both there, else what's missing, in a sentence.</summary>
        public static string? MissingEngine()
        {
            bool ffmpeg = ConverterManager.FfmpegExe != null, ffprobe = ConverterManager.FfprobeExe != null;
            if (ffmpeg && ffprobe) return null;
            string which = !ffmpeg && !ffprobe ? "ffmpeg.exe and ffprobe.exe" : !ffmpeg ? "ffmpeg.exe" : "ffprobe.exe";
            return $"{which} wasn't found in {FfmpegFolder}. GIF Maker needs it to read videos and make the GIF.";
        }

        // =================================================================
        //  Processes
        // =================================================================

        /// <summary>Runs a tool to the end and returns its exit code, everything it printed (bytes) and its error text.</summary>
        private static async Task<(int Exit, byte[] Output, string Error)> CaptureAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken token)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = new MemoryStream();
            var outTask = process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
            var errTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"{Path.GetFileNameWithoutExtension(exe)} took too long and was stopped - the file may be damaged.");
            }
            await outTask.ConfigureAwait(false);
            string error = await errTask.ConfigureAwait(false);
            return (process.ExitCode, output.ToArray(), error);
        }

        private static string LastLine(string text)
        {
            string? line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            return line ?? "";
        }

        // =================================================================
        //  ffprobe
        // =================================================================

        /// <summary>The video's length, picture size (turned for phone videos) and frame rate. Error text when it can't be used.</summary>
        public static async Task<(GifVideoInfo? Info, string? Error)> ProbeVideoAsync(string path, CancellationToken token)
        {
            string? exe = ConverterManager.FfprobeExe;
            if (exe == null) return (null, $"ffprobe wasn't found in {FfmpegFolder}.");
            if (!File.Exists(path)) return (null, "The video isn't there any more.");

            var (exit, output, error) = await CaptureAsync(exe, new[]
            {
                "-v", "error", "-select_streams", "v",
                "-show_entries", "format=duration:stream=width,height,r_frame_rate,avg_frame_rate,duration:stream_disposition=attached_pic:stream_side_data=rotation:stream_tags=rotate",
                "-of", "json", path,
            }, TimeSpan.FromSeconds(30), token);

            long size = 0;
            try { size = new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            var info = ParseVideoProbe(System.Text.Encoding.UTF8.GetString(output), path, size);
            if (info != null) return (info, null);
            if (exit != 0) return (null, "The video couldn't be read - it may be damaged (" + LastLine(error) + ").");
            return (null, "This file has no picture to make a GIF from.");
        }

        /// <summary>Reads ffprobe's JSON. Null when there's no real video stream (cover art doesn't count) or no length.</summary>
        public static GifVideoInfo? ParseVideoProbe(string json, string path, long size)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("streams", out var streams)) return null;

                foreach (var s in streams.EnumerateArray())
                {
                    if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1) continue;
                    int w = s.TryGetProperty("width", out var wv) ? wv.GetInt32() : 0;
                    int h = s.TryGetProperty("height", out var hv) ? hv.GetInt32() : 0;
                    if (w <= 0 || h <= 0) continue;

                    double rotation = 0;
                    if (s.TryGetProperty("side_data_list", out var side))
                        foreach (var sd in side.EnumerateArray())
                            if (sd.TryGetProperty("rotation", out var r) && r.TryGetDouble(out double rd)) rotation = rd;
                    if (rotation == 0 && s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rt)
                        && double.TryParse(rt.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rtd))
                        rotation = rtd;
                    if (Math.Abs(Math.Abs(rotation) % 180 - 90) < 1) (w, h) = (h, w);

                    // The base rate is right for normal video; very high ones (timestamps in ms) fall back to the average.
                    double fps = Rate(s, "r_frame_rate");
                    if (fps <= 0 || fps > 120) fps = Rate(s, "avg_frame_rate");

                    double seconds = Seconds(root.TryGetProperty("format", out var f) ? f : default, "duration");
                    if (seconds <= 0) seconds = Seconds(s, "duration");
                    if (seconds <= 0) return null;
                    return new GifVideoInfo(path, TimeSpan.FromSeconds(seconds), w, h, fps, size);
                }
                return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        private static double Rate(JsonElement s, string name)
        {
            if (!s.TryGetProperty(name, out var v) || v.GetString() is not string text) return 0;
            string[] parts = text.Split('/');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0)
                return n / d;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : 0;
        }

        private static double Seconds(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
            return v.ValueKind == JsonValueKind.String
                   && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && double.IsFinite(s)
                ? s
                : 0;
        }

        /// <summary>An image's pixel size, or the reason it can't be read.</summary>
        public static async Task<(int Width, int Height, string? Error)> ProbeImageAsync(string path, CancellationToken token)
        {
            string? exe = ConverterManager.FfprobeExe;
            if (exe == null) return (0, 0, $"ffprobe wasn't found in {FfmpegFolder}.");
            var (exit, output, error) = await CaptureAsync(exe, new[]
            {
                "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", path,
            }, TimeSpan.FromSeconds(20), token);
            string text = System.Text.Encoding.UTF8.GetString(output).Trim();
            string[] parts = text.Split('x', '\n');
            if (exit == 0 && parts.Length >= 2
                && int.TryParse(parts[0].Trim(), out int w) && int.TryParse(parts[1].Trim(), out int h) && w > 0 && h > 0)
                return (w, h, null);
            return (0, 0, "Couldn't read this image" + (error.Length > 0 ? " (" + LastLine(error) + ")" : "."));
        }

        // =================================================================
        //  Frame grabs for the trim bar
        // =================================================================

        /// <summary>One frame at <paramref name="seconds"/> as PNG bytes, at most <paramref name="maxWidth"/> wide. Null when there's no frame there.</summary>
        public static async Task<byte[]?> GrabFrameAsync(string path, double seconds, int maxWidth, CancellationToken token)
        {
            string? exe = ConverterManager.FfmpegExe;
            if (exe == null) return null;
            string w = maxWidth.ToString(CultureInfo.InvariantCulture);
            var (exit, output, _) = await CaptureAsync(exe, new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-ss", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
                "-frames:v", "1", "-an", "-vf", $"scale='min({w},iw)':-2",
                "-f", "image2pipe", "-c:v", "png", "-",
            }, TimeSpan.FromSeconds(20), token);
            return exit == 0 && output.Length > 0 ? output : null;
        }

        // =================================================================
        //  Looping preview of the selection (Video mode)
        // =================================================================

        /// <summary>Most GIF time a loop preview covers; a longer selection previews its first part.</summary>
        public const double LoopPreviewMaxSeconds = 20;

        /// <summary>
        /// What the loop preview is made with: the real selection, speed and (smaller) width, but a light
        /// picture - at most 480 wide, 15 frames a second, 64 colours - so it appears within moments.
        /// </summary>
        public static GifOptions LoopPreviewOptions(GifOptions o) => o with
        {
            Mode = GifSourceMode.Video,
            Fps = Math.Min(o.Fps, 15),
            MaxWidth = o.MaxWidth is > 0 and < 480 ? o.MaxWidth : 480,
            Quality = GifQuality.Small,
            Plays = 0,
            EndSeconds = Math.Min(o.EndSeconds, o.StartSeconds + LoopPreviewMaxSeconds * Math.Clamp(o.Speed, 0.1, 10)),
        };

        /// <summary>The selection as a small GIF (its bytes), made the way the real one is. Null when nothing could be made.</summary>
        public static async Task<byte[]?> RenderLoopPreviewAsync(GifVideoInfo video, GifOptions o, CancellationToken token)
        {
            RequireFfmpeg();
            var preview = LoopPreviewOptions(o);
            if (SelectionSeconds(preview) < 0.1) return null;
            string work = NewWorkFolder();
            try
            {
                string temp = Path.Combine(work, "loop.gif");
                await ConverterManager.RunFfmpegAsync(VideoArguments(video.FullPath, preview, video.Width, temp), 0, _ => { }, token).ConfigureAwait(false);
                return File.Exists(temp) ? await File.ReadAllBytesAsync(temp, token).ConfigureAwait(false) : null;
            }
            finally
            {
                DeleteFolder(work);
            }
        }

        // =================================================================
        //  Making the GIF
        // =================================================================

        /// <summary>
        /// Makes the GIF from a video selection, into a work folder, then
        /// moves it to <paramref name="target"/> (a free name unless the user
        /// chose to replace in the Save dialog). Cancelling leaves nothing behind.
        /// </summary>
        public static async Task<GifResult> CreateFromVideoAsync(
            GifVideoInfo video, GifOptions o, string target, bool overwriteAllowed, IProgress<GifProgress> progress, CancellationToken token)
        {
            RequireFfmpeg();
            CheckTarget(target, new[] { video.FullPath });
            string work = NewWorkFolder();
            try
            {
                string temp = Path.Combine(work, "output.gif");
                var args = VideoArguments(video.FullPath, o, video.Width, temp);
                progress.Report(new GifProgress("Making the GIF", 0));
                await ConverterManager.RunFfmpegAsync(args, GifSeconds(o), p => progress.Report(new GifProgress("Making the GIF", p)), token);
                token.ThrowIfCancellationRequested();

                var (w, h) = OutputSize(video.Width, video.Height, o.MaxWidth);
                string final = MoveIntoPlace(temp, target, overwriteAllowed);
                return new GifResult(final, new FileInfo(final).Length, w, h, FrameCount(o), TimeSpan.FromSeconds(GifSeconds(o)));
            }
            finally
            {
                DeleteFolder(work);
            }
        }

        /// <summary>
        /// Makes the GIF from images in list order: each is fitted into the
        /// first image's shape (scaled down to MaxWidth) on the chosen fill,
        /// then the frames are joined with one palette.
        /// </summary>
        public static async Task<GifResult> CreateFromImagesAsync(
            IReadOnlyList<GifImageItem> images, GifOptions o, string target, bool overwriteAllowed, IProgress<GifProgress> progress, CancellationToken token)
        {
            RequireFfmpeg();
            var usable = images.Where(i => !i.HasProblem && i.Width > 0 && i.Height > 0).ToList();
            if (usable.Count == 0) throw new InvalidOperationException("None of the images could be read.");
            CheckTarget(target, usable.Select(i => i.FullPath));

            var (w, h) = OutputSize(usable[0].Width, usable[0].Height, o.MaxWidth);
            string work = NewWorkFolder();
            try
            {
                await NormalizeAsync(usable, w, h, o.Fill, work, (done, total) =>
                    progress.Report(new GifProgress($"Preparing frames ({done:N0} of {total:N0})", done * 70.0 / total)), token);

                string temp = Path.Combine(work, "output.gif");
                var args = SequenceArguments(Path.Combine(work, "frame%05d.png"), o, temp);
                await ConverterManager.RunFfmpegAsync(args, GifSeconds(o, usable.Count),
                    p => progress.Report(new GifProgress("Making the GIF", 70 + p * 0.3)), token);
                token.ThrowIfCancellationRequested();

                string final = MoveIntoPlace(temp, target, overwriteAllowed);
                return new GifResult(final, new FileInfo(final).Length, w, h, usable.Count, TimeSpan.FromSeconds(GifSeconds(o, usable.Count)));
            }
            finally
            {
                DeleteFolder(work);
            }
        }

        /// <summary>Turns each image into frame00001.png, frame00002.png... (four at a time, list order kept).</summary>
        private static async Task NormalizeAsync(IReadOnlyList<GifImageItem> images, int w, int h, GifFill fill, string work,
            Action<int, int>? onProgress, CancellationToken token)
        {
            int done = 0;
            using var gate = new SemaphoreSlim(4);
            var tasks = images.Select(async (image, index) =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    string frame = Path.Combine(work, $"frame{index + 1:00000}.png");
                    try
                    {
                        await ConverterManager.RunFfmpegAsync(NormalizeImageArguments(image.FullPath, w, h, fill, frame), 0, _ => { }, token).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException($"\"{image.Name}\" couldn't be used: {ex.Message}", ex);
                    }
                    int n = Interlocked.Increment(ref done);
                    onProgress?.Invoke(n, images.Count);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // =================================================================
        //  Size estimate
        // =================================================================

        /// <summary>
        /// Encodes a short sample with the real settings and scales its size
        /// to the whole GIF: up to 3 s from the middle of the selection, or the
        /// first 5 images. Null when there's nothing to measure.
        /// </summary>
        public static async Task<long?> EstimateAsync(GifOptions o, GifVideoInfo? video, IReadOnlyList<GifImageItem> images, CancellationToken token)
        {
            if (ConverterManager.FfmpegExe == null) return null;
            string work = NewWorkFolder();
            try
            {
                string temp = Path.Combine(work, "sample.gif");
                if (o.Mode == GifSourceMode.Video)
                {
                    if (video == null) return null;
                    double selection = SelectionSeconds(o);
                    if (selection < 0.1) return null;
                    double sample = Math.Min(3, selection);
                    double start = o.StartSeconds + (selection - sample) / 2;
                    var args = VideoArguments(video.FullPath, o, video.Width, temp, start, sample);
                    await ConverterManager.RunFfmpegAsync(args, 0, _ => { }, token).ConfigureAwait(false);
                    return ScaleEstimate(new FileInfo(temp).Length, sample, selection);
                }

                var usable = images.Where(i => !i.HasProblem && i.Width > 0 && i.Height > 0).ToList();
                if (usable.Count == 0) return null;
                var (w, h) = OutputSize(usable[0].Width, usable[0].Height, o.MaxWidth);
                var subset = usable.Take(5).ToList();
                await NormalizeAsync(subset, w, h, o.Fill, work, null, token).ConfigureAwait(false);
                await ConverterManager.RunFfmpegAsync(SequenceArguments(Path.Combine(work, "frame%05d.png"), o, temp), 0, _ => { }, token).ConfigureAwait(false);
                return ScaleEstimate(new FileInfo(temp).Length, subset.Count, usable.Count);
            }
            finally
            {
                DeleteFolder(work);
            }
        }

        // =================================================================
        //  Files
        // =================================================================

        private static void RequireFfmpeg()
        {
            if (ConverterManager.FfmpegExe == null) throw new InvalidOperationException($"ffmpeg.exe wasn't found in {FfmpegFolder}.");
        }

        /// <summary>A Save dialog "replace" must never land on one of the inputs.</summary>
        private static void CheckTarget(string target, IEnumerable<string> inputs)
        {
            string full = Path.GetFullPath(target);
            if (inputs.Any(i => string.Equals(Path.GetFullPath(i), full, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The GIF can't be saved over one of its own source files. Choose another name.");
        }

        private static string NewWorkFolder()
        {
            string folder = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Moves the finished GIF into place: replaces only when the user said so, else takes a free name.</summary>
        private static string MoveIntoPlace(string temp, string target, bool overwriteAllowed)
        {
            string? folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            if (overwriteAllowed)
            {
                File.Move(temp, target, overwrite: true);
                return target;
            }
            for (int attempt = 0; ; attempt++)
            {
                string final = OutputFolderManager.UniquePath(target);
                try
                {
                    File.Move(temp, final, overwrite: false);
                    return final;
                }
                catch (IOException) when (attempt < 5 && File.Exists(final))
                {
                    // another program took the name in between: try the next one
                }
            }
        }

        private static void DeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // swept on the next start
            }
        }

        /// <summary>Removes work folders left by a crash (older than a day). Safe to call off the UI thread.</summary>
        public static void SweepWorkFolders()
        {
            try
            {
                if (!Directory.Exists(WorkRoot)) return;
                foreach (string dir in Directory.EnumerateDirectories(WorkRoot))
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromDays(1)) DeleteFolder(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // nothing to do
            }
        }
    }
}
