using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The Windows-only half of the page editor: pictures of pages (thumbnails) and the blank-page finder, both drawn with
    /// Windows' own PDF renderer, so what you see is what Windows shows. Kept apart from the PDF changes so those stay
    /// free of WPF and WinRT.
    /// </summary>
    public static partial class PdfExtrasManager
    {
        /// <summary>The share of a page that has to be ink for it not to count as blank, per sensitivity.</summary>
        public static double BlankThreshold(PdfBlankSensitivity sensitivity) => sensitivity switch
        {
            PdfBlankSensitivity.Strict => 0.00005,
            PdfBlankSensitivity.Loose => 0.006,
            _ => 0.0004,
        };

        /// <summary>
        /// Share of the pixels in a picture that are not near white (0 to 1). A pixel counts as ink when any of its colors
        /// is below 235, so pale paper texture and anti-aliasing fringes do not.
        /// </summary>
        public static double InkShare(BitmapSource picture)
        {
            var converted = picture.Format == PixelFormats.Bgr32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgr32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4;
            if (width == 0 || height == 0) return 0;

            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);
            long ink = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] < 235 || pixels[i + 1] < 235 || pixels[i + 2] < 235) ink++;
            return ink / (double)(width * height);
        }

        /// <summary>One PDF opened for rendering. Create it with <see cref="OpenAsync"/> and dispose it when done.</summary>
        public sealed class PageRenderer : IDisposable
        {
            private readonly Windows.Data.Pdf.PdfDocument _document;

            private PageRenderer(Windows.Data.Pdf.PdfDocument document) => _document = document;

            public int PageCount => (int)_document.PageCount;

            /// <summary>Opens a PDF. Throws when the password is wrong or the file is not a PDF Windows can read.</summary>
            public static async Task<PageRenderer> OpenAsync(string path, string? password)
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().ConfigureAwait(false);
                var document = string.IsNullOrEmpty(password)
                    ? await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().ConfigureAwait(false)
                    : await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file, password).AsTask().ConfigureAwait(false);
                return new PageRenderer(document);
            }

            /// <summary>The size of a page as shown, in points.</summary>
            public (double Width, double Height) SizeOf(int index)
            {
                using var page = _document.GetPage((uint)index);
                return (page.Size.Width, page.Size.Height);
            }

            /// <summary>A picture of a page whose longer side is about <paramref name="longSide"/> pixels. Frozen, so any thread can use it.</summary>
            public async Task<BitmapSource> RenderAsync(int index, int longSide, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                using var page = _document.GetPage((uint)index);
                double scale = longSide / Math.Max(page.Size.Width, page.Size.Height);
                var options = new Windows.Data.Pdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
                };

                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options).AsTask(token).ConfigureAwait(false);

                var bytes = new byte[stream.Size];
                using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync((uint)stream.Size).AsTask(token).ConfigureAwait(false);
                    reader.ReadBytes(bytes);
                }

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }

            public void Dispose()
            {
                // Windows.Data.Pdf.PdfDocument has nothing to release; pages are disposed where they are opened.
            }
        }
    }
}
