using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Smart_Tools.WPF.Models;
using System.IO;
using Orientation = ImageMagick.Orientation;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The parts of Images to PDF that need ImageMagick and PdfSharp.</summary>
    public static partial class ImageToPdfManager
    {
        /// <summary>Makes the PDF(s). See <see cref="RunAsync"/> for how.</summary>
        public static Task<ImagePdfResult> CreateAsync(
            IReadOnlyList<ImagePdfItem> items,
            ImagePdfOptions options,
            IProgress<ImagePdfProgress>? progress = null,
            CancellationToken token = default)
        {
            EnsureFonts();
            return RunAsync(items, options, PrepareImage, () => new PdfSharpBuilder(), progress, token);
        }

        // =================================================================
        //  Fonts
        // =================================================================

        private static readonly object FontLock = new();

        /// <summary>
        /// Hands PdfSharp the Windows font finder once. The old code set it in
        /// a static constructor with no check, and PdfSharp throws if it's set
        /// twice (another tool setting it first would break this one).
        /// </summary>
        public static void EnsureFonts()
        {
            lock (FontLock)
            {
                try
                {
                    if (GlobalFontSettings.FontResolver == null)
                        GlobalFontSettings.FontResolver = new WindowsFontResolver();
                }
                catch (InvalidOperationException)
                {
                    // Someone else set one already - use theirs.
                }
            }
        }

        // =================================================================
        //  Images
        // =================================================================

        /// <summary>
        /// Width and height the right way up, from the file's header only
        /// (the old list decoded every whole image on the UI thread just to
        /// show its size).
        /// </summary>
        public static (int Width, int Height) ReadImageSize(string path)
        {
            try
            {
                using var image = new MagickImage();
                image.Ping(path);
                int w = (int)image.Width, h = (int)image.Height;
                return IsQuarterTurn(image.Orientation) ? (h, w) : (w, h);
            }
            catch (Exception)
            {
                return (0, 0);
            }
        }

        private static bool IsQuarterTurn(Orientation o) =>
            o is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom;

        /// <summary>
        /// Turns one image into page-ready JPEG bytes: the right way up,
        /// transparency on white, RGB colours, shrunk to the quality's size
        /// limit (the old "Small file" only lowered JPEG quality, so a 48 MP
        /// photo stayed 48 MP), metadata removed. A plain upright RGB JPEG
        /// at High quality is used as it is - no second round of JPEG loss.
        /// Runs on a worker thread.
        /// </summary>
        public static ImagePdfPrepared PrepareImage(string path, ImagePdfOptions options)
        {
            var (jpegQuality, maxSide) = QualityPreset(options.Quality);

            using (var header = new MagickImage())
            {
                header.Ping(path);
                var (dpiX, dpiY) = DpiOf(header.Density);
                bool plainJpeg = header.Format is MagickFormat.Jpeg or MagickFormat.Jpg
                    && header.ColorSpace == ColorSpace.sRGB
                    && header.Depth == 8
                    && header.Orientation is Orientation.TopLeft or Orientation.Undefined;
                if (plainJpeg && maxSide == 0)
                    return new ImagePdfPrepared(File.ReadAllBytes(path), (int)header.Width, (int)header.Height, dpiX, dpiY);
            }

            using var image = new MagickImage(path);   // first frame of a GIF or multi-page TIFF
            var (dx, dy) = DpiOf(image.Density);
            bool turned = IsQuarterTurn(image.Orientation);   // read before AutoOrient resets it
            image.AutoOrient();
            if (turned) (dx, dy) = (dy, dx);

            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }
            if (image.ColorSpace != ColorSpace.sRGB && image.ColorSpace != ColorSpace.Gray)
                image.ColorSpace = ColorSpace.sRGB;

            uint longest = Math.Max(image.Width, image.Height);
            if (maxSide > 0 && longest > maxSide)
            {
                double scale = (double)maxSide / longest;
                image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide) { Greater = true });
                // Keep the print size: fewer pixels over the same inches.
                dx *= scale;
                dy *= scale;
            }

            image.Strip();
            image.Format = MagickFormat.Jpg;
            image.Quality = (uint)jpegQuality;
            using var ms = new MemoryStream();
            image.Write(ms);
            return new ImagePdfPrepared(ms.ToArray(), (int)image.Width, (int)image.Height, dx, dy);
        }

        /// <summary>Dots per inch (PNG stores per centimetre); 0 when the file doesn't say.</summary>
        private static (double X, double Y) DpiOf(Density density)
        {
            double f = density.Units switch
            {
                DensityUnit.PixelsPerCentimeter => 2.54,
                DensityUnit.PixelsPerInch => 1,
                _ => 0,
            };
            return (density.X * f, density.Y * f);
        }

        // =================================================================
        //  Pages
        // =================================================================

        /// <summary>One PdfSharp document. Each page gets its own size and orientation (the old code used one for every page).</summary>
        private sealed class PdfSharpBuilder : IImagePdfBuilder
        {
            private const string FontFamily = "Verdana";   // WindowsFontResolver falls back to Arial, Tahoma or Segoe UI
            private readonly PdfDocument _doc = new();

            public int ImagePages { get; private set; }

            public void AddTitlePage(string title, ImagePdfOptions options)
            {
                var (pw, ph) = PaperPoints(options.PageSize);
                if (options.Orientation == ImagePdfOrientation.Landscape) (pw, ph) = (ph, pw);
                var page = NewPage(pw, ph);
                using var gfx = XGraphics.FromPdfPage(page);

                double side = Math.Max(MarginPoints(options.Margins), 36);
                double maxWidth = pw - side * 2;

                // Shrink the font until the title fits in a few lines (a long title used to run off the page).
                XFont font = new(FontFamily, 28, XFontStyleEx.Bold);
                List<string> lines = new();
                for (double size = 28; size >= 14; size -= 2)
                {
                    font = new XFont(FontFamily, size, XFontStyleEx.Bold);
                    var f = font;
                    lines = WrapText(title, s => gfx.MeasureString(s, f).Width, maxWidth);
                    if (lines.Count <= 5) break;
                }
                const int MaxLines = 10;
                if (lines.Count > MaxLines)
                {
                    lines = lines.Take(MaxLines).ToList();
                    lines[^1] = lines[^1].TrimEnd() + "…";
                }

                double lineHeight = gfx.MeasureString("Ag", font).Height * 1.15;
                double y = (ph - lineHeight * lines.Count) / 2;
                foreach (string line in lines)
                {
                    double w = gfx.MeasureString(line, font).Width;
                    gfx.DrawString(line, font, XBrushes.Black, new XPoint((pw - w) / 2, y), XStringFormats.TopLeft);
                    y += lineHeight;
                }
            }

            public void AddImagePage(ImagePdfPrepared image, ImagePdfOptions options, int pageNumber)
            {
                double margin = MarginPoints(options.Margins);
                double iw = PixelsToPoints(image.Width, image.DpiX);
                double ih = PixelsToPoints(image.Height, image.DpiY);
                double footer = options.AddPageNumbers ? FooterPoints : 0;

                double pw, ph;
                if (options.PageSize == ImagePdfPageSize.MatchImage)
                {
                    (pw, ph) = MatchImagePage(iw, ih, margin);
                    ph += Math.Max(0, footer - margin);
                }
                else
                {
                    (pw, ph) = PaperPoints(options.PageSize);
                    if (UseLandscape(options.Orientation, image.Width, image.Height)) (pw, ph) = (ph, pw);
                }

                bool fit = options.FitImageToPage || options.PageSize == ImagePdfPageSize.MatchImage;
                var place = PlaceImage(pw, ph, margin, iw, ih, fit, footer);

                var page = NewPage(pw, ph);
                using var gfx = XGraphics.FromPdfPage(page);
                using (var ms = new MemoryStream(image.Jpeg, writable: false))
                using (var ximg = XImage.FromStream(ms))
                {
                    gfx.DrawImage(ximg, place.X, place.Y, place.Width, place.Height);
                }
                ImagePages++;

                if (options.AddPageNumbers)
                {
                    var font = new XFont(FontFamily, 9, XFontStyleEx.Regular);
                    string text = $"Page {pageNumber}";
                    double w = gfx.MeasureString(text, font).Width;
                    gfx.DrawString(text, font, XBrushes.Gray, new XPoint((pw - w) / 2, ph - 20), XStringFormats.BaseLineLeft);
                }
            }

            private PdfPage NewPage(double width, double height)
            {
                var page = _doc.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);
                return page;
            }

            public void Save(string path) => _doc.Save(path);

            public void Dispose() => _doc.Dispose();
        }
    }
}
