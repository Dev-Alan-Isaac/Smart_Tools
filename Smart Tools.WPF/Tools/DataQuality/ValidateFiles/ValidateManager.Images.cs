using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Validate Files - decoding pictures with Windows' codecs (WIC).</summary>
    public sealed partial class ValidateManager
    {
        /// <summary>Rows decoded per CopyPixels call, so a 20000 x 20000 picture doesn't need one 1.6 GB buffer.</summary>
        private const int StripBytes = 32 * 1024 * 1024;

        private static partial ImageProbe DecodeImageCore(string path, bool fullDecode, bool readExif)
        {
            try
            {
                using var fs = OpenShared(path);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) return new ImageProbe(0, 0, Loc.T("Str_Validate_ProbNoFrames", "no picture frames could be decoded"));

                var frame = decoder.Frames[0];
                int w = frame.PixelWidth, h = frame.PixelHeight;
                if (w <= 0 || h <= 0) return new ImageProbe(w, h, Loc.T("Str_Validate_ProbNoSize", "the picture has no size"));

                if (fullDecode)
                {
                    // Every frame of an animation, in strips - damage in frame 7 counts too.
                    foreach (var f in decoder.Frames)
                    {
                        int stride = Math.Max(1, (f.PixelWidth * f.Format.BitsPerPixel + 7) / 8);
                        int rows = Math.Clamp(StripBytes / stride, 1, f.PixelHeight);
                        var buffer = new byte[stride * rows];
                        for (int y = 0; y < f.PixelHeight; y += rows)
                        {
                            int n = Math.Min(rows, f.PixelHeight - y);
                            f.CopyPixels(new Int32Rect(0, y, f.PixelWidth, n), buffer, stride, 0);
                        }
                    }
                }

                string? exifError = null;
                if (readExif)
                {
                    try
                    {
                        if (frame.Metadata is BitmapMetadata meta)
                        {
                            _ = meta.CameraModel;
                            _ = meta.DateTaken;
                            _ = meta.GetQuery("/app1/ifd/{ushort=274}") ?? meta.GetQuery("/ifd/{ushort=274}");
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        exifError = ex.Message;
                    }
                }
                return new ImageProbe(w, h, null, false, exifError);
            }
            catch (Exception ex) when (IsUnsupportedCodec(ex))
            {
                return new ImageProbe(0, 0, ex.Message, Unsupported: true);
            }
            catch (Exception ex) when (ex is FileFormatException or COMException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException or IOException)
            {
                return new ImageProbe(0, 0, ex.Message);
            }
        }

        /// <summary>
        /// A codec gap (CMYK JPEG, some TIFF compressions, HEIC without the
        /// Store extension) rather than damage: WINCODEC_ERR_COMPONENTNOTFOUND.
        /// </summary>
        private static bool IsUnsupportedCodec(Exception ex)
        {
            if (ex is NotSupportedException) return true;
            if (ex is COMException com && (uint)com.HResult == 0x88982F50) return true;
            if (ex.InnerException is COMException inner && (uint)inner.HResult == 0x88982F50) return true;
            return ex.Message.Contains("No imaging component", StringComparison.OrdinalIgnoreCase);
        }
    }
}
