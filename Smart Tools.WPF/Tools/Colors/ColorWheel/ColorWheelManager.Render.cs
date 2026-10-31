using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Rendering half of ColorWheelManager (WPF drawing -> frozen bitmaps).
    /// Call on the UI thread (or any STA thread).
    /// </summary>
    public static partial class ColorWheelManager
    {
        /// <summary>
        /// Itten's "Farbkreis": an outer ring of 12 wedges, a hexagon whose
        /// corners are the primaries and secondaries, and the central
        /// Red-Yellow-Blue triangle around a neutral centre.
        /// </summary>
        /// <param name="pixelSize">Bitmap size in pixels (pass display size × DPI scale for a crisp preview).</param>
        /// <param name="highlightIndex">Wedge to outline (the selected one), or -1.</param>
        /// <param name="includeHexLabels">Print each wedge's hex code (used for the saved PNG).</param>
        public static BitmapSource RenderWheel(List<WheelSwatch> swatches, int pixelSize, int highlightIndex = -1, bool includeHexLabels = false)
        {
            var visual = new DrawingVisual();
            double outerRadius = pixelSize / 2.0;
            var center = new Point(outerRadius, outerRadius);
            double wedgeAngle = 360.0 / swatches.Count;
            double ringInner = outerRadius * RingInnerFraction;
            double line = Math.Max(1, pixelSize / 400.0);

            using (var dc = visual.RenderOpen())
            {
                // Outer ring
                for (int i = 0; i < swatches.Count; i++)
                {
                    double start = i * wedgeAngle - wedgeAngle / 2 - 90; // -90: wedge 0 at 12 o'clock
                    var geometry = AnnulusWedge(center, ringInner, outerRadius - line, start, start + wedgeAngle);
                    dc.DrawGeometry(new SolidColorBrush(ToMedia(swatches[i].Color)), new Pen(Brushes.White, line), geometry);

                    if (includeHexLabels)
                        DrawWedgeLabel(dc, swatches[i].Color, center, (ringInner + outerRadius) / 2, start + wedgeAngle / 2, outerRadius);
                }

                DrawHexagon(dc, swatches, center, ringInner, line * 1.5);

                // Selected wedge: a thick white outline with a soft dark edge so it reads on any hue.
                if (highlightIndex >= 0 && highlightIndex < swatches.Count)
                {
                    double start = highlightIndex * wedgeAngle - wedgeAngle / 2 - 90;
                    var geometry = AnnulusWedge(center, ringInner, outerRadius - line * 3, start, start + wedgeAngle);
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), line * 6), geometry);
                    dc.DrawGeometry(null, new Pen(Brushes.White, line * 3.5), geometry);
                }
            }

            return Rasterize(visual, pixelSize, pixelSize);
        }

        private static PathGeometry AnnulusWedge(Point center, double innerRadius, double outerRadius, double startDeg, double endDeg)
        {
            Point At(double radius, double deg)
            {
                double rad = deg * Math.PI / 180.0;
                return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
            }

            var figure = new PathFigure { StartPoint = At(outerRadius, startDeg), IsClosed = true };
            figure.Segments.Add(new ArcSegment(At(outerRadius, endDeg), new Size(outerRadius, outerRadius), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(At(innerRadius, endDeg), true));
            figure.Segments.Add(new ArcSegment(At(innerRadius, startDeg), new Size(innerRadius, innerRadius), 0, false, SweepDirection.Counterclockwise, true));
            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        private static void DrawHexagon(DrawingContext dc, List<WheelSwatch> s, Point center, double radius, double line)
        {
            Point Vertex(double wheelAngle)
            {
                double rad = (wheelAngle - 90) * Math.PI / 180.0;
                return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
            }

            Point red = Vertex(0), orange = Vertex(60), yellow = Vertex(120), green = Vertex(180), blue = Vertex(240), violet = Vertex(300);
            var outline = new Pen(Brushes.White, line);

            // Secondary corner triangles (each cut off by the chord between two primaries).
            Triangle(dc, red, orange, yellow, s[OrangeIndex].Color, outline);
            Triangle(dc, yellow, green, blue, s[GreenIndex].Color, outline);
            Triangle(dc, blue, violet, red, s[VioletIndex].Color, outline);

            // Central primary triangle split into three corners around a neutral mix.
            Point ry = Mid(red, yellow), yb = Mid(yellow, blue), br = Mid(blue, red);
            Triangle(dc, red, ry, br, s[RedIndex].Color, outline);
            Triangle(dc, yellow, yb, ry, s[YellowIndex].Color, outline);
            Triangle(dc, blue, br, yb, s[BlueIndex].Color, outline);
            Triangle(dc, ry, yb, br, Average(s[RedIndex].Color, s[YellowIndex].Color, s[BlueIndex].Color), outline);
        }

        private static void Triangle(DrawingContext dc, Point a, Point b, Point c, RgbColor fill, Pen outline)
        {
            var figure = new PathFigure { StartPoint = a, IsClosed = true };
            figure.Segments.Add(new LineSegment(b, true));
            figure.Segments.Add(new LineSegment(c, true));
            dc.DrawGeometry(new SolidColorBrush(ToMedia(fill)), outline, new PathGeometry(new[] { figure }));
        }

        private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        private static RgbColor Average(params RgbColor[] colors) => new(
            (byte)colors.Average(c => c.R), (byte)colors.Average(c => c.G), (byte)colors.Average(c => c.B));

        private static void DrawWedgeLabel(DrawingContext dc, RgbColor color, Point center, double radius, double midDeg, double scaleRadius)
        {
            double rad = midDeg * Math.PI / 180.0;
            var p = new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));

            var ink = ColorManager.BestTextColor(color);
            var text = new FormattedText(color.Hex, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), Math.Max(9, scaleRadius * 0.045),
                new SolidColorBrush(ToMedia(ink)), 1.0);

            dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
        }

        /// <summary>
        /// Left-to-right strip of <paramref name="colors"/>. With
        /// <paramref name="includeHexLabels"/>, each block gets its hex code
        /// printed on it in black or white, whichever reads better.
        /// </summary>
        public static BitmapSource RenderGradientStrip(IReadOnlyList<RgbColor> colors, int width, int height, bool includeHexLabels = false)
        {
            var visual = new DrawingVisual();
            double step = width / (double)Math.Max(1, colors.Count);

            using (var dc = visual.RenderOpen())
            {
                for (int i = 0; i < colors.Count; i++)
                {
                    // +1px overlap so hairline gaps never show at odd widths.
                    dc.DrawRectangle(new SolidColorBrush(ToMedia(colors[i])), null, new Rect(i * step, 0, step + 1, height));
                }

                if (includeHexLabels)
                {
                    double fontSize = Math.Clamp(Math.Min(step / 6.5, height / 8.0), 9, 22);
                    for (int i = 0; i < colors.Count; i++)
                    {
                        var ink = ColorManager.BestTextColor(colors[i]);
                        var text = new FormattedText(colors[i].Hex, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI Semibold"), fontSize, new SolidColorBrush(ToMedia(ink)), 1.0);
                        dc.DrawText(text, new Point(i * step + (step - text.Width) / 2, height - text.Height - height * 0.08));
                    }
                }
            }

            return Rasterize(visual, width, height);
        }

        /// <summary>
        /// One export image: the colour wheel on top, then the start colour, the gradient strip and the
        /// end colour underneath, each with its hex code.
        /// </summary>
        public static BitmapSource RenderComposite(
            List<WheelSwatch> swatches, IReadOnlyList<RgbColor> gradient, RgbColor start, RgbColor end,
            int wheelSize, string startLabel, string endLabel)
        {
            const int margin = 48, swatchSize = 120, gap = 24, captionHeight = 44;
            int stripWidth = wheelSize - 2 * (swatchSize + gap);
            int stripHeight = swatchSize;
            int width = wheelSize + 2 * margin;
            int height = margin + wheelSize + gap + stripHeight + captionHeight + margin;

            var wheel = RenderWheel(swatches, wheelSize, highlightIndex: -1, includeHexLabels: true);
            var strip = RenderGradientStrip(gradient, stripWidth, stripHeight, includeHexLabels: true);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawImage(wheel, new Rect(margin, margin, wheelSize, wheelSize));

                double y = margin + wheelSize + gap;
                dc.DrawRectangle(new SolidColorBrush(ToMedia(start)), null, new Rect(margin, y, swatchSize, swatchSize));
                dc.DrawImage(strip, new Rect(margin + swatchSize + gap, y, stripWidth, stripHeight));
                dc.DrawRectangle(new SolidColorBrush(ToMedia(end)), null, new Rect(width - margin - swatchSize, y, swatchSize, swatchSize));

                // Captions are wider than the swatch they sit under, so they hug the image margins instead of
                // centring on it (centred, a long label would run off the edge of the export).
                void Caption(string label, string hex, bool alignRight)
                {
                    var text = new FormattedText($"{label} {hex}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Consolas"), 22, Brushes.Black, 1.0);
                    double x = alignRight ? width - margin - text.Width : margin;
                    dc.DrawText(text, new Point(x, y + stripHeight + 8));
                }
                Caption(startLabel, start.Hex, alignRight: false);
                Caption(endLabel, end.Hex, alignRight: true);
            }

            return Rasterize(visual, width, height);
        }

        private static Color ToMedia(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);

        private static BitmapSource Rasterize(DrawingVisual visual, int width, int height)
        {
            var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Writes a PNG (wheel or gradient strip).</summary>
        public static void SaveBitmapToPng(BitmapSource bitmap, string outputPath)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            encoder.Save(stream);
        }
    }
}
