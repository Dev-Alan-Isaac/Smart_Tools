using ImageMagick;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Pixels: Magick.NET for export (reads WEBP/HEIC, writes WEBP, good
    /// resampling, keeps colour), WPF imaging for the fast preview and
    /// thumbnails with Magick.NET as the fallback for formats Windows can't
    /// decode. Both apply the photo's camera rotation (EXIF) first, so the
    /// preview, the crop and the export all see the same picture.
    /// </summary>
    public sealed partial class ImageEditingManager
    {
        private static MagickFormat MagickFormatFor(ImageOutputFormat format) => format switch
        {
            ImageOutputFormat.Png => MagickFormat.Png,
            ImageOutputFormat.Webp => MagickFormat.WebP,
            ImageOutputFormat.Bmp => MagickFormat.Bmp,
            ImageOutputFormat.Gif => MagickFormat.Gif,
            ImageOutputFormat.Tiff => MagickFormat.Tiff,
            _ => MagickFormat.Jpg,
        };

        /// <summary>Crop -> rotate -> mirror -> resize -> save, the same order as <see cref="PlannedSize"/>.</summary>
        private static void RenderWithMagick(ImageItem item, ImageEditOptions o, string output, CancellationToken token)
        {
            using var image = new MagickImage(item.FullPath);
            image.AutoOrient();   // camera rotation first - the preview shows it this way too
            token.ThrowIfCancellationRequested();

            if (item.Crop is CropRect crop)
            {
                var px = CropPixels(crop, (int)image.Width, (int)image.Height);
                image.Crop(new MagickGeometry(px.X, px.Y, (uint)px.Width, (uint)px.Height));
                image.Page = new MagickGeometry(0, 0, 0, 0);   // drop the canvas offset the crop leaves behind
            }

            if (item.Orientation.Rotation != 0) image.Rotate(item.Orientation.Rotation);
            if (item.Orientation.Mirror) image.Flop();
            token.ThrowIfCancellationRequested();

            var (w, h) = ResolveTargetSize((int)image.Width, (int)image.Height, o);
            if (w != (int)image.Width || h != (int)image.Height)
                image.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });

            // JPG/BMP can't hold transparency: white behind it, not black.
            if (NeedsFlatten(o.Format) && image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }
            if (UsesQuality(o.Format)) image.Quality = (uint)Math.Clamp(o.Quality, 1, 100);

            token.ThrowIfCancellationRequested();
            image.Write(output, MagickFormatFor(o.Format));
        }

        // =================================================================
        //  Preview and thumbnails
        // =================================================================

        public sealed record LoadedImage(BitmapSource? Image, int Width, int Height, string? Error);

        /// <summary>
        /// The image turned the way the camera meant (the old preview and
        /// thumbnails showed phone photos sideways), at most
        /// <paramref name="maxSide"/> pixels on its longer side (a 50 MP photo
        /// no longer decodes at full size just to be shown). Width/Height are
        /// the full size. Frozen, so it can cross threads.
        /// </summary>
        public static LoadedImage LoadOriented(string path, int maxSide)
        {
            try
            {
                return LoadWithWic(path, maxSide);
            }
            catch (Exception wicError) when (wicError is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException)
            {
                try
                {
                    return LoadWithMagick(path, maxSide);
                }
                catch (Exception ex)
                {
                    return new LoadedImage(null, 0, 0, ex.Message);
                }
            }
            catch (Exception ex)
            {
                return new LoadedImage(null, 0, 0, ex.Message);
            }
        }

        private static LoadedImage LoadWithWic(string path, int maxSide)
        {
            int rawW, rawH, exif = 1;
            using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var frame = BitmapFrame.Create(header, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                rawW = frame.PixelWidth;
                rawH = frame.PixelHeight;
                try
                {
                    if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort tag) exif = tag;
                }
                catch (Exception)
                {
                    // no or unreadable metadata - treat as upright
                }
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (Math.Max(rawW, rawH) > maxSide)
            {
                if (rawW >= rawH) bitmap.DecodePixelWidth = maxSide;
                else bitmap.DecodePixelHeight = maxSide;
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            var orientation = FromExif(exif);
            BitmapSource oriented = Orient(bitmap, orientation);
            var (w, h) = orientation.SwapsSides ? (rawH, rawW) : (rawW, rawH);
            return new LoadedImage(oriented, w, h, null);
        }

        private static BitmapSource Orient(BitmapSource source, ImageOrientation o)
        {
            BitmapSource result = source;
            if (o.Rotation != 0) result = new TransformedBitmap(result, new RotateTransform(o.Rotation));
            if (o.Mirror) result = new TransformedBitmap(result, new ScaleTransform(-1, 1));
            if (!result.IsFrozen) result.Freeze();
            return result;
        }

        private static LoadedImage LoadWithMagick(string path, int maxSide)
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            int w = (int)image.Width, h = (int)image.Height;
            if (Math.Max(w, h) > maxSide)
                image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));   // fits inside, keeps shape

            using var ms = new MemoryStream();
            image.Write(ms, MagickFormat.Bmp);
            ms.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return new LoadedImage(bitmap, w, h, null);
        }

        /// <summary>Thumbnails and sizes for rows that don't have them, a few at a time off the UI thread.</summary>
        public static Task LoadThumbnailsAsync(IReadOnlyList<ImageItem> items, CancellationToken token = default) =>
            Parallel.ForEachAsync(
                items.Where(i => i.Thumbnail == null && i.Status != ImageItemStatus.Unreadable).ToList(),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                (item, _) =>
                {
                    var loaded = LoadOriented(item.FullPath, 160);
                    if (loaded.Image == null)
                    {
                        item.Status = ImageItemStatus.Unreadable;
                        item.Message = loaded.Error ?? "This file couldn't be read as an image.";
                        item.IsSelected = false;
                    }
                    else
                    {
                        item.Thumbnail = loaded.Image;
                        item.PixelWidth = loaded.Width;
                        item.PixelHeight = loaded.Height;
                    }
                    return ValueTask.CompletedTask;
                });
    }
}
