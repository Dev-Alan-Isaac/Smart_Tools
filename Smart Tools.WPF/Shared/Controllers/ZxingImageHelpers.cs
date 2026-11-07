using Smart_Tools.WPF.Models;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZXing.Rendering;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Rendering/export plumbing shared by BarcodeManager and QrCodeManager
    /// (both sit on ZXing's raw-pixel pipeline: BarcodeWriterPixelData ->
    /// recolour -> optional compositing -> encode). Moved out of
    /// BarcodeManager.cs into its own file and the standard namespace.
    ///
    /// Every method QrCodeManager already calls keeps its exact signature;
    /// new helpers are additive.
    /// </summary>
    internal static class ZxingImageHelpers
    {
        /// <summary>
        /// Recolours the renderer's black-on-white output to the requested
        /// colours. Module output is pure black/white (no anti-aliasing), so a
        /// luminance threshold cleanly separates ink from paper.
        /// </summary>
        public static BitmapSource PixelDataToBitmap(PixelData pixels, string foregroundHex, string backgroundHex)
        {
            if (!ColorManager.TryHexToRgb(foregroundHex, out RgbColor fg))
                fg = new RgbColor(0, 0, 0);
            if (!ColorManager.TryHexToRgb(backgroundHex, out RgbColor bg))
                bg = new RgbColor(255, 255, 255);

            byte[] source = pixels.Pixels; // BGRA
            byte[] recolored = new byte[source.Length];

            for (int i = 0; i < source.Length; i += 4)
            {
                int luminance = (source[i] + source[i + 1] + source[i + 2]) / 3;
                RgbColor color = luminance < 128 ? fg : bg;
                recolored[i] = color.B;
                recolored[i + 1] = color.G;
                recolored[i + 2] = color.R;
                recolored[i + 3] = 255;
            }

            var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, recolored, pixels.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// Raster-in-SVG wrapper (PNG embedded as a data URI). Still used by
        /// QR export; linear barcodes now get real vector bars instead
        /// (BarcodeManager.BuildVectorSvg).
        /// </summary>
        public static string BuildSvg(BitmapSource image, string backgroundHex)
        {
            string base64 = Convert.ToBase64String(EncodeToBytes(image, new PngBitmapEncoder()));
            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{image.PixelWidth}\" height=\"{image.PixelHeight}\" ");
            sb.Append($"viewBox=\"0 0 {image.PixelWidth} {image.PixelHeight}\">\n");
            sb.Append($"  <rect width=\"100%\" height=\"100%\" fill=\"{backgroundHex}\" />\n");
            sb.Append($"  <image width=\"{image.PixelWidth}\" height=\"{image.PixelHeight}\" href=\"data:image/png;base64,{base64}\" />\n");
            sb.Append("</svg>");
            return sb.ToString();
        }

        public static byte[] EncodeToBytes(BitmapSource image, BitmapEncoder encoder)
        {
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>Filesystem-safe base name; falls back to "{prefix}_0001".</summary>
        public static string SanitizeFileName(string input, int index, string defaultPrefix)
        {
            if (string.IsNullOrWhiteSpace(input))
                return $"{defaultPrefix}_{index:0000}";

            char[] invalid = Path.GetInvalidFileNameChars();
            string cleaned = new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (cleaned.Length > 60)
                cleaned = cleaned[..60];

            return string.IsNullOrWhiteSpace(cleaned) ? $"{defaultPrefix}_{index:0000}" : cleaned;
        }

        /// <summary>
        /// Returns "name.ext", or "name (2).ext", "name (3).ext"... so batch
        /// entries that sanitize to the same name - or a file left by an
        /// earlier export into the same folder - are never overwritten.
        /// </summary>
        public static string UniquePath(string folder, string baseName, string extension, ISet<string> taken)
        {
            string candidate = Path.Combine(folder, baseName + extension);
            int n = 2;
            while (!taken.Add(candidate) || File.Exists(candidate))
                candidate = Path.Combine(folder, $"{baseName} ({n++}){extension}");
            return candidate;
        }

        /// <summary>
        /// Warns about colour pairs scanners struggle with, or null if fine:
        /// light-on-dark (inverted) marks, or contrast below 4.5:1.
        /// <paramref name="inkNoun"/> names the dark marks ("bars", "squares").
        /// </summary>
        public static string? GetColorWarning(string foregroundHex, string backgroundHex, string inkNoun)
        {
            if (!ColorManager.TryHexToRgb(foregroundHex, out RgbColor fg) ||
                !ColorManager.TryHexToRgb(backgroundHex, out RgbColor bg))
                return null;

            double lf = RelativeLuminance(fg), lb = RelativeLuminance(bg);
            if (lf > lb)
                return $"Light {inkNoun} on a dark background (inverted) can't be read by many scanners.";

            double ratio = (lb + 0.05) / (lf + 0.05);
            if (ratio < 4.5)
                return $"Low contrast ({ratio:0.0}:1) - some scanners may fail. Darker {inkNoun} or a lighter background will help.";

            return null;
        }

        private static double RelativeLuminance(RgbColor c)
        {
            static double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        /// <summary>
        /// Runs work on a dedicated STA thread. WPF drawing objects
        /// (DrawingVisual, RenderTargetBitmap, encoders) require STA - running
        /// them on the thread pool (MTA) is what made every batch row with a
        /// text label fail with "The calling thread must be STA".
        /// </summary>
        public static Task<T> RunOnStaThread<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.SetResult(work());
                }
                catch (OperationCanceledException oce)
                {
                    tcs.SetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    // Release the Dispatcher WPF created for this thread.
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            })
            {
                IsBackground = true,
                Name = "ZXing render worker",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }

    /// <summary>
    /// Minimal single-page PDF writer with no library dependency.
    /// </summary>
    internal static class PdfLiteWriter
    {
        /// <summary>
        /// LOSSLESS: embeds raw RGB pixels under /FlateDecode. Use this for
        /// barcodes/QR codes - JPEG compression softens module edges, which
        /// is exactly what scanners need to be crisp.
        /// </summary>
        public static void SaveBitmapAsPdf(BitmapSource image, string filePath)
        {
            var bgr = new FormatConvertedBitmap(image, PixelFormats.Rgb24, null, 0);
            int width = bgr.PixelWidth, height = bgr.PixelHeight, stride = width * 3;
            byte[] rgb = new byte[stride * height];
            bgr.CopyPixels(rgb, stride, 0);

            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(rgb, 0, rgb.Length);
                compressed = ms.ToArray();
            }

            WritePdf(filePath, width, height, compressed, "/FlateDecode");
        }

        /// <summary>Embeds existing JPEG bytes as-is (/DCTDecode). Kept for QR export compatibility.</summary>
        public static void SaveJpegAsPdf(byte[] jpegBytes, int pixelWidth, int pixelHeight, string filePath) =>
            WritePdf(filePath, pixelWidth, pixelHeight, jpegBytes, "/DCTDecode");

        private static void WritePdf(string filePath, int pixelWidth, int pixelHeight, byte[] imageStream, string filter)
        {
            double pageWidth = pixelWidth, pageHeight = pixelHeight; // 1 px = 1 pt
            using var stream = new MemoryStream();
            var offsets = new List<long>();

            void Raw(string text) { byte[] b = Encoding.ASCII.GetBytes(text); stream.Write(b, 0, b.Length); }
            void Begin(int n) { offsets.Add(stream.Position); Raw($"{n} 0 obj\n"); }
            void End() => Raw("endobj\n");

            Raw("%PDF-1.4\n");
            Begin(1); Raw("<< /Type /Catalog /Pages 2 0 R >>\n"); End();
            Begin(2); Raw("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n"); End();
            Begin(3);
            Raw("<< /Type /Page /Parent 2 0 R " +
                FormattableString.Invariant($"/MediaBox [0 0 {pageWidth:0.##} {pageHeight:0.##}] ") +
                "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\n");
            End();

            Begin(4);
            Raw($"<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter {filter} /Length {imageStream.Length} >>\nstream\n");
            stream.Write(imageStream, 0, imageStream.Length);
            Raw("\nendstream\n");
            End();

            string content = FormattableString.Invariant($"q {pageWidth:0.##} 0 0 {pageHeight:0.##} 0 0 cm /Im0 Do Q");
            Begin(5); Raw($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\n"); End();

            long xref = stream.Position;
            Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
            foreach (long offset in offsets)
                Raw($"{offset:0000000000} 00000 n \n");
            Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");

            AtomicFile.WriteAllBytes(filePath, stream.ToArray());
        }
    }
}
