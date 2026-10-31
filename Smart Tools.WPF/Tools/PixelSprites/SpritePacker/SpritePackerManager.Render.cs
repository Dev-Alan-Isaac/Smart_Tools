using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    // The WPF half of the sprite packer: picture files to pixels, thumbnails, and pixels to a picture.
    // Everything frozen here is safe to hand to the UI thread. The packing maths is in SpritePackerManager.cs.
    public static partial class SpritePackerManager
    {
        /// <summary>The size of a picture from its header, without decoding the pixels. False when it can't be read.</summary>
        public static bool TryReadSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                return width > 0 && height > 0;
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                return false;
            }
        }

        /// <summary>
        /// All of a picture as straight-alpha BGRA. Opened through a stream, never a Uri (a # or % in a file name breaks a Uri).
        /// Pictures over <see cref="MaxSide"/> pixels on a side are refused with a code (ImageTooBig / ImageUnreadable).
        /// </summary>
        public static byte[]? LoadBgra(string path, out int width, out int height, out string problem)
        {
            width = height = 0;
            problem = "";
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > MaxSide || frame.PixelHeight > MaxSide)
                {
                    problem = "ImageTooBig";
                    return null;
                }
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                width = converted.PixelWidth;
                height = converted.PixelHeight;
                var bgra = new byte[width * height * 4];
                converted.CopyPixels(bgra, width * 4, 0);
                return bgra;
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                problem = "ImageUnreadable";
                return null;
            }
        }

        /// <summary>A small frozen picture for the list, decoded at its display size (not the full image).</summary>
        public static BitmapSource? LoadThumbnail(string path, int size)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = stream;
                image.DecodePixelWidth = size;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                return null;
            }
        }

        /// <summary>A frozen picture of straight-alpha BGRA pixels (a packed sheet, or a sheet being sliced).</summary>
        public static BitmapSource CreateBitmap(byte[] bgra, int width, int height)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        private static bool IsReadError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException;
    }
}
