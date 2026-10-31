using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    // The three engines of the watermarker and the batch that runs them. The mark itself is drawn once per file size
    // (WatermarkManager.Render.cs) and each engine lays that picture over its kind of file:
    //   pictures - ImageMagick composite, same format, camera rotation applied;
    //   PDFs     - PDFsharp draws the picture on every page, whatever its size, rotation or crop box;
    //   videos   - ffmpeg overlay filter (the picture is re-encoded as H.264, the sound is copied when it can be).
    // Originals are never touched: results are written to a hidden temporary name and moved to a free name.
    public static partial class WatermarkManager
    {
        /// <summary>Working files (the mark as a PNG for ffmpeg) live here and are removed after each file.</summary>
        public static string WorkRoot => Path.Combine(Path.GetTempPath(), "Smart Tools", "Watermark");

        public static string FfmpegFolder => ConverterManager.FfmpegFolder;

        /// <summary>Null when ffmpeg and ffprobe are both there, else what is missing, in a sentence.</summary>
        public static string? MissingVideoEngine()
        {
            bool ffmpeg = ConverterManager.FfmpegExe != null, ffprobe = ConverterManager.FfprobeExe != null;
            if (ffmpeg && ffprobe) return null;
            string which = !ffmpeg && !ffprobe ? "ffmpeg.exe and ffprobe.exe" : !ffmpeg ? "ffmpeg.exe" : "ffprobe.exe";
            return $"{which} wasn't found in {FfmpegFolder}. Videos need it.";
        }

        // =================================================================
        //  Reading a file's details (size, pages, length) for the list
        // =================================================================

        public sealed record VideoInfo(int Width, int Height, double Seconds, string? AudioCodec);

        /// <summary>"1920 × 1080", "12 pages" or "1920 × 1080 · 0:32", or the reason the file can't be marked.</summary>
        public static Task<(string Info, string? Problem)> ReadInfoAsync(WatermarkItem item, CancellationToken token) =>
            Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    switch (item.Kind)
                    {
                        case WatermarkFileKind.Image:
                        {
                            using var image = new MagickImage();
                            image.Ping(item.FullPath);
                            bool turned = image.Orientation is ImageMagick.Orientation.LeftTop or ImageMagick.Orientation.RightTop or ImageMagick.Orientation.RightBottom or ImageMagick.Orientation.LeftBottom;
                            return ($"{(turned ? image.Height : image.Width)} × {(turned ? image.Width : image.Height)}", (string?)null);
                        }
                        case WatermarkFileKind.Pdf:
                        {
                            using var doc = OpenPdf(item.FullPath, PdfDocumentOpenMode.InformationOnly);
                            return (doc.PageCount == 1 ? "1 page" : $"{doc.PageCount:N0} pages", null);
                        }
                        default:
                        {
                            if (MissingVideoEngine() is { } missing) return ("", missing);
                            var video = ProbeVideo(item.FullPath, out string? problem);
                            if (video == null) return ("", problem);
                            return ($"{video.Width} × {video.Height} · {FormatLength(video.Seconds)}", null);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return ("", FriendlyProblem(ex));
                }
            }, token);

        /// <summary>Opens a PDF; anything that is not a disk problem becomes a sentence (password-protected, or damaged / not a real PDF).</summary>
        private static PdfSharp.Pdf.PdfDocument OpenPdf(string path, PdfDocumentOpenMode mode)
        {
            try
            {
                return PdfReader.Open(path, mode);
            }
            catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
            {
                throw new InvalidOperationException(ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                    ? "This PDF is protected with a password, so it can't be marked."
                    : "This PDF couldn't be read. It may be damaged or not a real PDF.", ex);
            }
        }

        private static string FormatLength(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        /// <summary>A sentence a person can act on instead of a library's exception text.</summary>
        public static string FriendlyProblem(Exception ex) => ex switch
        {
            MagickException => "This picture couldn't be read. It may be damaged or in a format this tool doesn't know.",
            IOException => "The file couldn't be read or written: " + ex.Message,
            UnauthorizedAccessException => "Access denied. The file or the folder is protected.",
            _ => ex.Message,
        };

        // =================================================================
        //  Running a batch
        // =================================================================

        private static readonly object MoveLock = new();

        /// <summary>
        /// Marks every file. Pictures and PDFs run a few at a time, videos one at a time (they use every core). A file that fails is
        /// reported on its own row and the rest carry on; Stop cancels what is running (partial files are removed) and keeps the finished ones.
        /// <paramref name="outputFolder"/> / <paramref name="sameAsSource"/> decide where results go; a taken name gets " (2)".
        /// Call it from the UI thread: item status changes are posted back to that thread.
        /// </summary>
        public static async Task<WatermarkRunResult> RunAsync(IReadOnlyList<WatermarkItem> items, WatermarkSettings settings, string? outputFolder, bool sameAsSource,
            IProgress<WatermarkProgress>? progress, CancellationToken token, bool saveByFileType = false)
        {
            var result = new WatermarkRunResult();
            var started = Stopwatch.StartNew();
            var s = Normalize(settings);
            var logo = s.Kind == WatermarkKind.Image ? LoadLogo(s.LogoFile) : null;
            if (!HasMark(s)) throw new InvalidOperationException(s.Kind == WatermarkKind.Text ? "Type the text of the mark first." : "Choose a logo first.");

            var ui = SynchronizationContext.Current;
            void Post(Action action)
            {
                if (ui != null) ui.Post(_ => action(), null);
                else action();
            }

            int done = 0, failed = 0, skipped = 0;
            var fractions = new double[items.Count];
            var gates = new Dictionary<WatermarkFileKind, SemaphoreSlim>
            {
                [WatermarkFileKind.Image] = new SemaphoreSlim(2),
                [WatermarkFileKind.Pdf] = new SemaphoreSlim(2),
                [WatermarkFileKind.Video] = new SemaphoreSlim(1),
            };

            void Report(string current)
            {
                double total;
                lock (fractions) total = fractions.Sum();
                progress?.Report(new WatermarkProgress(Volatile.Read(ref done), items.Count, Volatile.Read(ref failed), Volatile.Read(ref skipped), current,
                    items.Count == 0 ? 100 : Math.Min(100, total * 100 / items.Count)));
            }

            var tasks = items.Select(async (item, index) =>
            {
                var gate = gates[item.Kind];
                try
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;                                         // never started: stays as it was
                }
                try
                {
                    if (token.IsCancellationRequested) return;
                    Post(() => { item.Status = WatermarkStatus.Processing; item.StatusText = ""; item.Progress = 0; });
                    Report(item.Name);

                    // No folder chosen: each file goes to its own kind's folder (pictures, videos, PDFs).
                    var outcome = await ProcessItemAsync(item, s, logo, saveByFileType && !sameAsSource ? DefaultFolderFor(item.Kind) : outputFolder, sameAsSource, p =>
                    {
                        lock (fractions) fractions[index] = Math.Clamp(p / 100, 0, 1);
                        Post(() => item.Progress = p);
                        Report(item.Name);
                    }, token).ConfigureAwait(false);

                    lock (fractions) fractions[index] = 1;
                    if (outcome.Status == WatermarkStatus.Done) { Interlocked.Increment(ref done); lock (result) result.OutputFiles.Add(outcome.Output!); }
                    else if (outcome.Status == WatermarkStatus.Skipped) Interlocked.Increment(ref skipped);
                    else Interlocked.Increment(ref failed);
                    Post(() =>
                    {
                        item.Status = outcome.Status;
                        item.StatusText = outcome.Text;
                        item.OutputPath = outcome.Output ?? "";
                        item.Progress = outcome.Status == WatermarkStatus.Done ? 100 : 0;
                    });
                    Report(item.Name);
                }
                catch (OperationCanceledException)
                {
                    Post(() => { item.Status = WatermarkStatus.Pending; item.StatusText = ""; item.Progress = 0; });
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            result.Done = done;
            result.Failed = failed;
            result.Skipped = skipped;
            result.Cancelled = token.IsCancellationRequested;
            result.Elapsed = started.Elapsed;
            return result;
        }

        private sealed record Outcome(WatermarkStatus Status, string Text, string? Output);

        private static async Task<Outcome> ProcessItemAsync(WatermarkItem item, WatermarkSettings s, System.Windows.Media.Imaging.BitmapSource? logo,
            string? outputFolder, bool sameAsSource, Action<double> progress, CancellationToken token)
        {
            string source = item.FullPath;
            if (!File.Exists(source)) return new Outcome(WatermarkStatus.Failed, "The file isn't there any more.", null);

            string destination = sameAsSource || string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(Path.GetFullPath(source)) ?? "" : outputFolder;
            string temp = Path.Combine(destination, "." + Path.GetFileNameWithoutExtension(source) + ".creating-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            try
            {
                Directory.CreateDirectory(destination);
                string? skipReason = item.Kind switch
                {
                    WatermarkFileKind.Image => await RunOnSta(() => MarkImage(source, s, logo, temp)).ConfigureAwait(false),
                    WatermarkFileKind.Pdf => await RunOnSta(() => MarkPdf(source, s, logo, temp, progress, token)).ConfigureAwait(false),
                    _ => await MarkVideoAsync(source, s, logo, temp, progress, token).ConfigureAwait(false),
                };
                if (skipReason != null)
                {
                    DeleteQuietly(temp);
                    return new Outcome(WatermarkStatus.Skipped, skipReason, null);
                }
                token.ThrowIfCancellationRequested();

                string target;
                lock (MoveLock)
                {
                    // Chosen and moved under one lock, so two files can't be given the same free name.
                    target = OutputFolderManager.ResultPath(source, outputFolder, sameAsSource, OutputFileName(source, item.Kind));
                    File.Move(temp, target, overwrite: false);
                }
                return new Outcome(WatermarkStatus.Done, "", target);
            }
            catch (OperationCanceledException)
            {
                DeleteQuietly(temp);
                throw;
            }
            catch (Exception ex)
            {
                DeleteQuietly(temp);
                return new Outcome(WatermarkStatus.Failed, FriendlyProblem(ex), null);
            }
        }

        private static void DeleteQuietly(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a hidden leftover is harmless */ }
        }

        // =================================================================
        //  Pictures
        // =================================================================

        private static MagickFormat ImageFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => MagickFormat.Png,
            ".bmp" => MagickFormat.Bmp,
            ".tif" or ".tiff" => MagickFormat.Tiff,
            ".webp" => MagickFormat.WebP,
            ".gif" => MagickFormat.Gif,
            _ => MagickFormat.Jpeg,
        };

        /// <summary>Lays the mark over the picture and writes it in the same format. Returns a reason when the picture is left alone.</summary>
        private static string? MarkImage(string source, WatermarkSettings s, System.Windows.Media.Imaging.BitmapSource? logo, string temp)
        {
            using (var frames = new MagickImageCollection())
            {
                frames.Ping(source);
                if (frames.Count > 1) return "Animated pictures are left alone (the mark would be lost or the animation flattened).";
            }
            using var image = new MagickImage(source);
            image.AutoOrient();                                        // camera rotation first, so the mark sits where the preview shows it
            var stamp = RenderStamp((int)image.Width, (int)image.Height, s, logo) ?? throw new InvalidOperationException("The mark doesn't fall on this picture.");
            using var mark = new MagickImage(ToPng(stamp.Bitmap));
            image.Composite(mark, stamp.X, stamp.Y, CompositeOperator.Over);

            var format = ImageFormat(source);
            if (format is MagickFormat.Jpeg or MagickFormat.WebP) image.Quality = 92;
            using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            image.Write(stream, format);
            return null;
        }

        // =================================================================
        //  PDFs
        // =================================================================

        /// <summary>
        /// Draws the mark on every page. A page's size, /Rotate and crop box are all respected: the mark is drawn for the page as it is
        /// shown, then turned back into the page's own space. Pages of the same size share one picture.
        /// </summary>
        private static string? MarkPdf(string source, WatermarkSettings s, System.Windows.Media.Imaging.BitmapSource? logo, string temp, Action<double> progress, CancellationToken token)
        {
            using var doc = OpenPdf(source, PdfDocumentOpenMode.Modify);
            var cache = new Dictionary<(int W, int H), (Stamp? Stamp, XImage? Image)>();
            var streams = new List<MemoryStream>();
            try
            {
                int count = doc.PageCount;
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var page = doc.Pages[i];

                    // The part of the page that is shown: the crop box when there is one, else the whole media box.
                    // (Reading CropBox on a page that has none would create one and change the page, so look for the entry first.)
                    var media = page.MediaBox;
                    var crop = page.Elements.ContainsKey("/CropBox") ? page.CropBox : media;
                    bool hasCrop = !crop.IsEmpty && crop.Width > 0 && crop.Height > 0 && (crop.X1 != media.X1 || crop.Y1 != media.Y1 || crop.X2 != media.X2 || crop.Y2 != media.Y2);
                    double pw = hasCrop ? crop.Width : media.Width, ph = hasCrop ? crop.Height : media.Height;
                    double shiftX = hasCrop ? crop.X1 - media.X1 : 0, shiftY = hasCrop ? media.Y2 - crop.Y2 : 0;

                    int rotation = ((page.Rotate % 360) + 360) % 360;
                    bool sideways = rotation is 90 or 270;
                    double dw = sideways ? ph : pw, dh = sideways ? pw : ph;           // as shown on screen
                    if (dw < 1 || dh < 1) continue;

                    double k = Math.Clamp(3000.0 / Math.Max(dw, dh), 0.5, 2.0);          // stamp pixels per point
                    int sw = Math.Max(1, (int)Math.Round(dw * k)), sh = Math.Max(1, (int)Math.Round(dh * k));
                    if (!cache.TryGetValue((sw, sh), out var entry))
                    {
                        var stamp = RenderStamp(sw, sh, s, logo);
                        XImage? image = null;
                        if (stamp != null)
                        {
                            var ms = new MemoryStream(ToPng(stamp.Bitmap));
                            streams.Add(ms);
                            image = XImage.FromStream(ms);
                        }
                        entry = (stamp, image);
                        cache[(sw, sh)] = entry;
                    }

                    if (entry.Stamp != null && entry.Image != null)
                    {
                        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                        gfx.TranslateTransform(shiftX, shiftY);
                        // Shown (x, y) -> the page's own space. 90 and 270 turn the page on its side.
                        switch (rotation)
                        {
                            case 90: gfx.MultiplyTransform(new XMatrix(0, -1, 1, 0, 0, dw), XMatrixOrder.Prepend); break;
                            case 180: gfx.MultiplyTransform(new XMatrix(-1, 0, 0, -1, dw, dh), XMatrixOrder.Prepend); break;
                            case 270: gfx.MultiplyTransform(new XMatrix(0, 1, -1, 0, dh, 0), XMatrixOrder.Prepend); break;
                        }
                        gfx.DrawImage(entry.Image, entry.Stamp.X / k, entry.Stamp.Y / k, entry.Stamp.Bitmap.PixelWidth / k, entry.Stamp.Bitmap.PixelHeight / k);
                    }
                    progress((i + 1) * 100.0 / count);
                }
                doc.Save(temp);
                return null;
            }
            finally
            {
                foreach (var (_, image) in cache.Values) image?.Dispose();
                foreach (var ms in streams) ms.Dispose();
            }
        }

        // =================================================================
        //  Videos
        // =================================================================

        /// <summary>Size (turned for phone videos), length and sound codec from ffprobe. Null with the reason when it can't be used.</summary>
        public static VideoInfo? ProbeVideo(string path, out string? problem)
        {
            problem = null;
            string? exe = ConverterManager.FfprobeExe;
            if (exe == null) { problem = $"ffprobe wasn't found in {FfmpegFolder}."; return null; }
            string json;
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in new[]
                {
                    "-v", "error",
                    "-show_entries", "stream=index,codec_type,codec_name,width,height:stream_disposition=attached_pic:stream_side_data=rotation:stream_tags=rotate:format=duration",
                    "-of", "json", path,
                }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(30_000))
                {
                    try { p.Kill(true); } catch (Exception) { /* already gone */ }
                    problem = "ffprobe took too long. The video may be damaged.";
                    return null;
                }
                json = outTask.Result;
                if (p.ExitCode != 0) { problem = "The video couldn't be read. It may be damaged."; return null; }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                problem = "The video couldn't be read: " + ex.Message;
                return null;
            }
            var info = ParseVideoProbe(json);
            if (info == null) problem = "This file has no picture to put a mark on.";
            return info;
        }

        /// <summary>Reads ffprobe's JSON. Null when there is no real video stream (cover art is not video).</summary>
        public static VideoInfo? ParseVideoProbe(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("streams", out var streams)) return null;

                int w = 0, h = 0;
                string? audio = null;
                foreach (var s in streams.EnumerateArray())
                {
                    string type = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                    if (type == "audio" && audio == null) audio = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : "";
                    if (type != "video" || w > 0) continue;
                    if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1) continue;
                    int sw = s.TryGetProperty("width", out var wv) ? wv.GetInt32() : 0, sh = s.TryGetProperty("height", out var hv) ? hv.GetInt32() : 0;
                    if (sw <= 0 || sh <= 0) continue;

                    double rotation = 0;
                    if (s.TryGetProperty("side_data_list", out var side))
                        foreach (var sd in side.EnumerateArray())
                            if (sd.TryGetProperty("rotation", out var r) && r.TryGetDouble(out double rd)) rotation = rd;
                    if (rotation == 0 && s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rt)
                        && double.TryParse(rt.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rtd))
                        rotation = rtd;
                    if (Math.Abs(Math.Abs(rotation) % 180 - 90) < 1) (sw, sh) = (sh, sw);     // ffmpeg turns the picture upright
                    (w, h) = (sw, sh);
                }
                if (w <= 0) return null;

                double seconds = 0;
                if (root.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String
                    && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ds) && double.IsFinite(ds))
                    seconds = ds;
                return new VideoInfo(w, h, seconds, audio);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        /// <summary>The ffmpeg arguments (after "ffmpeg -hide_banner"): overlay the mark, H.264 picture, sound copied when the container can hold it.</summary>
        public static List<string> VideoArguments(string source, string stampPng, int stampX, int stampY, VideoInfo info, string temp, string outputExtension)
        {
            string ext = outputExtension.ToLowerInvariant();
            bool matroska = ext == ".mkv";
            string filter = string.Create(CultureInfo.InvariantCulture,
                $"[0:v:0][1:v]overlay={stampX}:{stampY}:format=auto,scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p[v]");

            var args = new List<string>
            {
                "-y", "-nostdin", "-i", source, "-i", stampPng,
                "-filter_complex", filter, "-map", "[v]", "-map", "0:a?", "-map_metadata", "0",
                "-c:v", "libx264", "-preset", "medium", "-crf", "18",
            };
            if (info.AudioCodec != null)
            {
                bool canCopy = matroska || info.AudioCodec is "aac" or "mp3" or "ac3" or "eac3" or "alac";
                if (canCopy) args.AddRange(new[] { "-c:a", "copy" });
                else args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            }
            if (!matroska) args.AddRange(new[] { "-movflags", "+faststart" });
            args.AddRange(new[] { "-f", matroska ? "matroska" : ext == ".mov" ? "mov" : "mp4", "-nostats", "-progress", "pipe:1", temp });
            return args;
        }

        private static async Task<string?> MarkVideoAsync(string source, WatermarkSettings s, System.Windows.Media.Imaging.BitmapSource? logo, string temp,
            Action<double> progress, CancellationToken token)
        {
            if (MissingVideoEngine() is { } missing) throw new InvalidOperationException(missing);
            var info = await Task.Run(() => ProbeVideo(source, out string? problem) ?? throw new InvalidOperationException(problem), token).ConfigureAwait(false);
            var stamp = await RunOnSta(() => RenderStamp(info.Width, info.Height, s, logo)).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The mark doesn't fall on this video.");

            string work = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N")[..10]);
            try
            {
                Directory.CreateDirectory(work);
                string png = Path.Combine(work, "mark.png");
                await File.WriteAllBytesAsync(png, await RunOnSta(() => ToPng(stamp.Bitmap)).ConfigureAwait(false), token).ConfigureAwait(false);
                var args = VideoArguments(source, png, stamp.X, stamp.Y, info, temp, OutputExtension(source, WatermarkFileKind.Video));
                await ConverterManager.RunFfmpegAsync(args, info.Seconds, progress, token).ConfigureAwait(false);
                return null;
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept on the next start */ }
            }
        }

        /// <summary>Removes work folders left behind by a crashed run (older than a day).</summary>
        public static void SweepWorkFolder()
        {
            try
            {
                if (!Directory.Exists(WorkRoot)) return;
                foreach (string dir in Directory.EnumerateDirectories(WorkRoot))
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try again next time */ }
        }
    }
}
