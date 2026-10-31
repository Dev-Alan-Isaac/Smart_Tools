using ImageMagick;
using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Bulk Image Resizer - shrinks and recompresses many pictures with Magick.NET. Originals are never changed: every
    /// result is a new file (a number is added if the name is taken). A file is written to a temporary name and moved
    /// into place when complete, so a failure or cancel never leaves a half-written picture. The planning half (sizes,
    /// names, the quality search) is in <c>BulkImageResizerManager.Plan.cs</c>. Create on the UI thread: updates are
    /// posted back to the captured context.
    /// </summary>
    public sealed partial class BulkImageResizerManager
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        /// <summary>Reads a picture's pixel size from its header (fast, nothing is decoded). False if it can't be read.</summary>
        public static bool ReadSize(BulkImageItem item)
        {
            try
            {
                var info = MagickImageInfo.ReadCollection(item.Path).FirstOrDefault();
                if (info == null) return false;
                item.Width = (int)info.Width;
                item.Height = (int)info.Height;
                return true;
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or TypeInitializationException or DllNotFoundException or BadImageFormatException)
            {
                return false;   // unreadable (or, if the picture library itself can't load, nothing can be read)
            }
        }

        /// <summary>
        /// Resizes every item. Errors are kept per picture (the rest carry on); cancelling stops after the pictures already
        /// being worked on, and what was finished stays finished.
        /// </summary>
        public async Task RunAsync(IReadOnlyList<BulkImageItem> items, ResizeOptions options, string? outputFolder, bool sameAsSource, IProgress<ResizeProgress>? progress, CancellationToken token)
        {
            var gate = new object();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // names picked by other workers that aren't on disk yet
            int done = 0, failed = 0, skipped = 0;
            long before = 0, after = 0;
            var lastReport = DateTime.MinValue;

            void Report(bool force)
            {
                if (progress == null) return;
                ResizeProgress snapshot;
                lock (gate)
                {
                    var now = DateTime.UtcNow;
                    if (!force && now - lastReport < ProgressInterval) return;
                    lastReport = now;
                    snapshot = new ResizeProgress(done + failed + skipped, items.Count, failed, skipped, before, after);
                }
                progress.Report(snapshot);
            }

            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),   // a big picture is hundreds of MB while it is decoded
                CancellationToken = token,
            };

            try
            {
                await Parallel.ForEachAsync(items, parallel, async (item, ct) =>
                {
                    await Task.Run(() =>
                    {
                        Post(() => item.Status = ResizeStatus.Working);
                        try
                        {
                            string ext = OutputExtension(item.Path, options.Format);
                            string target;
                            lock (gate) target = ReserveName(OutputFolderManager.ResultPath(item.Path, outputFolder, sameAsSource, OutputFileName(item.Path, options.Suffix, ext)), reserved);
                            var result = ProcessOne(item.Path, target, ext, options, ct);

                            lock (gate)
                            {
                                done++;
                                before += item.OriginalBytes;
                                after += result.Bytes;
                            }
                            Post(() =>
                            {
                                item.NewPath = target;
                                item.NewBytes = result.Bytes;
                                item.NewWidth = result.Width;
                                item.NewHeight = result.Height;
                                item.Quality = result.Quality;
                                item.Message = result.Note;
                                item.Status = ResizeStatus.Done;
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            Post(() => item.Status = ResizeStatus.Pending);
                            throw;
                        }
                        catch (NotSupportedException ex)
                        {
                            lock (gate) skipped++;
                            Post(() => { item.Message = ex.Message; item.Status = ResizeStatus.Skipped; });
                        }
                        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException or TypeInitializationException or DllNotFoundException or BadImageFormatException)
                        {
                            lock (gate) failed++;
                            string reason = ex is TypeInitializationException or DllNotFoundException or BadImageFormatException
                                ? "The picture library couldn't be loaded, so nothing can be resized."
                                : PlainMessage(ex.Message, item.Path);
                            Post(() => { item.Message = reason; item.Status = ResizeStatus.Failed; });
                        }
                        Report(false);
                    }, ct);
                });
            }
            catch (OperationCanceledException)
            {
                // Cancelled: the pictures that finished keep their results.
            }
            Report(true);
        }

        private readonly record struct Outcome(long Bytes, int Width, int Height, int Quality, string Note);

        private static Outcome ProcessOne(string input, string output, string ext, ResizeOptions o, CancellationToken token)
        {
            if (MagickImageInfo.ReadCollection(input).Skip(1).Any())
                throw new NotSupportedException("Animated and multi-page pictures can't be resized here.");

            using var image = new MagickImage(input);
            token.ThrowIfCancellationRequested();

            image.AutoOrient();   // bake the EXIF rotation in, so the result isn't sideways once the tag is stripped
            var (w, h) = ComputeSize((int)image.Width, (int)image.Height, o, ext);
            if (w != (int)image.Width || h != (int)image.Height) image.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });

            if (ext == ".jpg" && image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;   // JPG has no transparency: white, not black
                image.Alpha(AlphaAction.Remove);
            }
            if (o.StripMetadata) StripProfiles(image);
            image.Format = FormatFor(ext);
            token.ThrowIfCancellationRequested();

            int quality = 0;
            string note = "";
            if (IsLossy(ext))
            {
                quality = Math.Clamp(o.Quality, 1, 100);
                if (o.TargetKb > 0)
                {
                    quality = FindQuality(q => EncodedSize(image, q, token), o.TargetKb * 1024L, quality, out bool fits);
                    if (!fits) note = "Couldn't get under the target size, so it was saved at the lowest quality.";
                }
                image.Quality = (uint)quality;
            }
            else if (o.TargetKb > 0)
            {
                note = "A target size only works for JPG and WebP.";
            }

            // Written beside the final name first, then moved in: a failure or cancel never leaves half a picture.
            string temp = output + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    image.Write(stream);
                token.ThrowIfCancellationRequested();
                File.Move(temp, output);
            }
            catch
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
            return new Outcome(new FileInfo(output).Length, (int)image.Width, (int)image.Height, o.TargetKb > 0 && IsLossy(ext) ? quality : 0, note);
        }

        private static long EncodedSize(MagickImage image, int quality, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            image.Quality = (uint)quality;
            using var counter = new MemoryStream();
            image.Write(counter);
            return counter.Length;
        }

        /// <summary>Removes camera, location, and editing details but keeps the color profile, which changes how colors look if it is lost.</summary>
        private static void StripProfiles(MagickImage image)
        {
            foreach (string name in image.ProfileNames.ToList())
                if (!name.Equals("icc", StringComparison.OrdinalIgnoreCase) && !name.Equals("icm", StringComparison.OrdinalIgnoreCase))
                    image.RemoveProfile(name);
        }

        private static MagickFormat FormatFor(string ext) => ext switch
        {
            ".png" => MagickFormat.Png,
            ".webp" => MagickFormat.WebP,
            ".bmp" => MagickFormat.Bmp,
            ".gif" => MagickFormat.Gif,
            ".tif" => MagickFormat.Tiff,
            _ => MagickFormat.Jpeg,
        };

        /// <summary>Updates items on the UI thread (or right here when there is no captured context, as in tests).</summary>
        private void Post(Action action)
        {
            if (_context == null) action();
            else _context.Post(_ => action(), null);
        }
    }
}
