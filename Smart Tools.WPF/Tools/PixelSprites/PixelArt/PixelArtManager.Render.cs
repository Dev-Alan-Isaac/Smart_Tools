using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    // The WPF half of the pixel art editor: pixels to a picture, and a picture file to pixels.
    // Everything that needs no WPF type is in PixelArtManager.cs and PixelArtManager.Export.cs.
    public sealed partial class PixelArtManager
    {
        /// <summary>A frozen picture of one frame (straight alpha), for thumbnails and the preview.</summary>
        public static BitmapSource CreateBitmap(uint[] pixels, int width, int height)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>A frozen picture from PNG bytes (the clipboard copy of an export).</summary>
        public static BitmapSource BitmapFromPng(byte[] png)
        {
            var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }

        /// <summary>
        /// Reads any picture Windows can decode into straight-alpha RGBA. Opened through a stream, never a Uri
        /// (a # or % in a file name breaks a Uri). Pictures over <see cref="PixelArtDocument.MaxSide"/> are refused with a code.
        /// </summary>
        public static byte[]? LoadImage(string path, out int width, out int height, out string problem)
        {
            width = height = 0;
            problem = "";
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > PixelArtDocument.MaxSide || frame.PixelHeight > PixelArtDocument.MaxSide)
                {
                    problem = "ImageTooBig";
                    return null;
                }
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                width = converted.PixelWidth;
                height = converted.PixelHeight;
                var bgra = new byte[width * height * 4];
                converted.CopyPixels(bgra, width * 4, 0);
                var rgba = new byte[bgra.Length];
                for (int i = 0; i < bgra.Length; i += 4)
                {
                    rgba[i] = bgra[i + 2];
                    rgba[i + 1] = bgra[i + 1];
                    rgba[i + 2] = bgra[i];
                    rgba[i + 3] = bgra[i + 3];
                }
                return rgba;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FileFormatException or ArgumentException)
            {
                problem = "ImageUnreadable";
                return null;
            }
        }
    }
}
