using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controllers
{
    // The WPF half of the palette extractor: reading pixels out of a picture and drawing the
    // PNG strip. Everything that needs no WPF type is in PaletteExtractorManager.cs.
    public static partial class PaletteExtractorManager
    {
        /// <summary>The longest side the picture is shrunk to before counting colours.</summary>
        public const int SampleSide = 220;

        /// <summary>
        /// Shrinks the picture (never enlarges) and returns its pixels as straight-alpha BGRA.
        /// Safe on any thread when <paramref name="source"/> is frozen.
        /// </summary>
        public static (byte[] Pixels, int Width, int Height) SamplePixels(BitmapSource source)
        {
            BitmapSource scaled = source;
            double longSide = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longSide > SampleSide)
            {
                double factor = SampleSide / longSide;
                var t = new TransformedBitmap(source, new ScaleTransform(factor, factor));
                t.Freeze();
                scaled = t;
            }

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            var pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);
            return (pixels, w, h);
        }

        /// <summary>
        /// A strip image of the palette: one square block per colour, all the same width and height
        /// (up to 6 per row, then it wraps), with the hex and the share written on each in black or
        /// white. Must run on an STA thread.
        /// </summary>
        public static void SaveStripPng(IReadOnlyList<PaletteColor> colors, string path)
        {
            if (colors.Count == 0) throw new InvalidOperationException(Loc.T("Str_PaletteExtractor_MsgNoColours", "There are no colours to save."));

            const int block = 240, perRow = 6;
            int columns = Math.Min(colors.Count, perRow);
            int rows = (colors.Count + perRow - 1) / perRow;
            int totalWidth = block * columns, height = block * rows;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                for (int i = 0; i < colors.Count; i++)
                {
                    var c = colors[i];
                    double x = (i % perRow) * block, y = (i / perRow) * block;
                    var fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.Color.R, c.Color.G, c.Color.B));
                    fill.Freeze();
                    dc.DrawRectangle(fill, null, new Rect(x, y, block, block));

                    var ink = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.TextColor.R, c.TextColor.G, c.TextColor.B));
                    ink.Freeze();
                    var hex = new FormattedText(c.Hex, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Consolas"), 24, ink, 1.0);
                    var share = new FormattedText(c.SharePercent, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Semibold"), 18, ink, 1.0);
                    double top = y + (block - hex.Height - share.Height - 6) / 2;
                    dc.DrawText(hex, new Point(x + (block - hex.Width) / 2, top));
                    dc.DrawText(share, new Point(x + (block - share.Width) / 2, top + hex.Height + 6));
                }
            }

            var bmp = new RenderTargetBitmap(totalWidth, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            string temp = Path.Combine(Path.GetDirectoryName(path) ?? ".", $".{Path.GetFileName(path)}.creating-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var fs = File.Create(temp)) encoder.Save(fs);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }
    }
}
