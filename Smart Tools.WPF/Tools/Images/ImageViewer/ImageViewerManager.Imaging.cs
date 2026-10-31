using ImageMagick;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Image Viewer - decoding, orientation, thumbnails and saving (WPF and
    /// ImageMagick). Everything returned is frozen, so it can be made on a
    /// background thread and shown on the UI thread.
    /// </summary>
    public static partial class ImageViewerManager
    {
        /// <summary>WPF can't decode these without an optional Windows extension - ImageMagick reads them.</summary>
        private static readonly HashSet<string> MagickFirst = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".avif" };

        /// <summary>
        /// Longest side shown. Bigger images (a 20,000 px panorama is about
        /// 1.6 GB decoded) are shown scaled down instead of running out of
        /// memory; the info line says so.
        /// </summary>
        public const int MaxDisplaySide = 12000;

        public static string FormatName(string path) =>
            Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
            {
                "JPG" or "JPEG" or "JFIF" => "JPEG",
                "TIF" or "TIFF" => "TIFF",
                "HEIF" => "HEIC",
                var e => e,
            };

        // A stream rather than a Uri: "new Uri(path)" read "#" and "%" in a
        // file name as URL syntax, so "Holiday #2.jpg" couldn't be opened.
        private static FileStream OpenRead(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

        /// <summary>The image for the stage, the right way up, plus what the info line shows. Null when it can't be read.</summary>
        public static (BitmapSource Bitmap, ImageViewInfo Info)? LoadForView(string path)
        {
            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                // shown as 0 B
            }
            string format = FormatName(path);

            if (!MagickFirst.Contains(Path.GetExtension(path)))
            {
                try
                {
                    var (bitmap, w, h, orientation, reduced) = DecodeNative(path, MaxDisplaySide);
                    bool sideways = OrientationTransform(orientation).Rotate % 180 != 0;
                    return (bitmap, new ImageViewInfo(bitmap.PixelWidth, bitmap.PixelHeight, format, size, reduced,
                        sideways ? h : w, sideways ? w : h));
                }
                catch (Exception)
                {
                    // corrupt header or a format WPF doesn't know - try ImageMagick
                }
            }

            try
            {
                using var image = new MagickImage(path);
                image.AutoOrient();
                int ow = (int)image.Width, oh = (int)image.Height;
                bool reduced = Math.Max(ow, oh) > MaxDisplaySide;
                if (reduced) image.Resize(new MagickGeometry((uint)MaxDisplaySide, (uint)MaxDisplaySide));
                var bitmap = ToBitmap(image);
                return (bitmap, new ImageViewInfo(bitmap.PixelWidth, bitmap.PixelHeight, format, size, reduced, ow, oh));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A small picture for the list (only rows on screen ask for one).</summary>
        public static BitmapSource? LoadThumbnail(string path, int longSide = 64)
        {
            if (!MagickFirst.Contains(Path.GetExtension(path)))
            {
                try
                {
                    return DecodeNative(path, longSide).Bitmap;
                }
                catch (Exception)
                {
                    // fall back to ImageMagick
                }
            }
            try
            {
                using var image = new MagickImage(path);
                image.AutoOrient();
                image.Thumbnail(new MagickGeometry((uint)longSide, (uint)longSide));
                return ToBitmap(image);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>WPF decode: reads the header first (size, orientation), then decodes no bigger than needed.</summary>
        private static (BitmapSource Bitmap, int Width, int Height, int Orientation, bool Reduced) DecodeNative(string path, int maxSide)
        {
            using var stream = OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            int orientation = ReadOrientation(frame);

            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bool reduced = Math.Max(w, h) > maxSide;
            if (reduced)
            {
                if (w >= h) bitmap.DecodePixelWidth = maxSide;
                else bitmap.DecodePixelHeight = maxSide;
            }
            bitmap.EndInit();

            BitmapSource result = Orient(bitmap, orientation);
            result.Freeze();
            return (result, w, h, orientation, reduced);
        }

        private static int ReadOrientation(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata metadata
                    && metadata.ContainsQuery("System.Photo.Orientation")
                    && metadata.GetQuery("System.Photo.Orientation") is ushort value)
                    return value;
            }
            catch (Exception)
            {
                // PNG, GIF and others have no such tag (some throw instead of saying so)
            }
            return 1;
        }

        /// <summary>Turns (then mirrors) the pixels the way the photo's orientation tag says.</summary>
        private static BitmapSource Orient(BitmapSource source, int orientation)
        {
            var (rotate, flip) = OrientationTransform(orientation);
            if (rotate == 0 && !flip) return source;
            var group = new TransformGroup();
            if (rotate != 0) group.Children.Add(new RotateTransform(rotate));
            if (flip) group.Children.Add(new ScaleTransform(-1, 1));
            return new TransformedBitmap(source, group);
        }

        private static BitmapSource ToBitmap(MagickImage image)
        {
            using var ms = new MemoryStream();
            image.Format = MagickFormat.Png;
            image.Quality = 10;   // fastest PNG compression - this is only a hand-over to WPF
            image.Write(ms);
            ms.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        // =================================================================
        //  Saving
        // =================================================================

        /// <summary>
        /// Saves the image under a new name. Not turned and the same type:
        /// the file's bytes are copied exactly (no quality loss). Turned, or
        /// saved as another type: re-saved with ImageMagick, turned the way
        /// it's shown - the old Save As always copied the original, so a
        /// photo turned in the viewer was saved the old way round. Written to
        /// a temporary name first, so a failure never leaves half a file.
        /// </summary>
        public static void SaveCopy(string source, string destination, double rotation)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) && NormalizeAngle(rotation, 0) == 0)
                return;   // saving an unturned image over itself changes nothing

            string dir = Path.GetDirectoryName(Path.GetFullPath(destination))!;
            string ext = Path.GetExtension(destination);
            string temp = Path.Combine(dir, $".{Path.GetFileNameWithoutExtension(destination)}.saving-{Guid.NewGuid().ToString("N")[..8]}{ext}");
            bool sameType = string.Equals(FormatName(source), FormatName(destination), StringComparison.OrdinalIgnoreCase);
            try
            {
                if (NormalizeAngle(rotation, 0) == 0 && sameType)
                {
                    File.Copy(source, temp);
                }
                else
                {
                    using var image = new MagickImage(source);
                    image.AutoOrient();
                    if (NormalizeAngle(rotation, 0) != 0) image.Rotate(NormalizeAngle(rotation, 0));
                    image.Write(temp);   // the extension picks the format
                }
                File.Move(temp, destination, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception)
                {
                    // best effort
                }
            }
        }

        /// <summary>The countdown ring's arc (see <see cref="RingArc"/>), frozen.</summary>
        public static Geometry? BuildRingGeometry(double sweepDegrees)
        {
            if (RingArc(sweepDegrees) is not { } arc) return null;
            var figure = new PathFigure { StartPoint = new Point(arc.StartX, arc.StartY), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(arc.EndX, arc.EndY), new Size(RingRadius, RingRadius), 0, arc.LargeArc, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }
    }
}
