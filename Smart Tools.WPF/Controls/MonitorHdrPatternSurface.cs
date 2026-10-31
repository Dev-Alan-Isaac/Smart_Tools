using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Draws one picture of the HDR &amp; Color Depth Checker pixel-exactly (laid out in device pixels, one column per step, so banding
    /// shows as it is). What each picture contains is decided by <see cref="MonitorHdrManager"/>; this only paints it.
    /// It is a fixed test surface with data colours, so it does not follow the theme.
    /// </summary>
    public sealed class MonitorHdrPatternSurface : FrameworkElement
    {
        private static DependencyProperty Reg<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(MonitorHdrPatternSurface), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PatternIdProperty = Reg(nameof(PatternId), "ramp_gray");
        public static readonly DependencyProperty WindowPercentProperty = Reg(nameof(WindowPercent), 10);
        public static readonly DependencyProperty PrimariesProperty = Reg<MonitorHdrPrimaries?>(nameof(Primaries), null);
        public static readonly DependencyProperty ShowLabelsProperty = Reg(nameof(ShowLabels), true);

        private static readonly Dictionary<int, SolidColorBrush> BrushCache = new();
        private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        public string PatternId { get => (string)GetValue(PatternIdProperty); set => SetValue(PatternIdProperty, value); }

        /// <summary>For the white-window picture: how much of the screen the white area covers.</summary>
        public int WindowPercent { get => (int)GetValue(WindowPercentProperty); set => SetValue(WindowPercentProperty, value); }

        /// <summary>For the gamut picture: the screen's own primaries, drawn next to the standards.</summary>
        public MonitorHdrPrimaries? Primaries { get => (MonitorHdrPrimaries?)GetValue(PrimariesProperty); set => SetValue(PrimariesProperty, value); }

        public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

        public MonitorHdrPatternSurface()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        private static SolidColorBrush B(byte r, byte g, byte b)
        {
            int key = (r << 16) | (g << 8) | b;
            if (!BrushCache.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                BrushCache[key] = brush;
            }
            return brush;
        }

        private static SolidColorBrush B(byte v) => B(v, v, v);

        private DpiScale _dpi;
        private double Dx(double px) => px / _dpi.DpiScaleX;
        private double Dy(double px) => px / _dpi.DpiScaleY;
        private Rect PxRect(double x, double y, double w, double h) => new(Dx(x), Dy(y), Dx(w), Dy(h));

        protected override void OnRender(DrawingContext dc)
        {
            _dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(ActualWidth * _dpi.DpiScaleX), h = (int)Math.Round(ActualHeight * _dpi.DpiScaleY);
            if (w < 2 || h < 2) return;

            switch (PatternId)
            {
                case "ramp_gray": DrawRamp(dc, w, h, 0, 255, 1, 1, 1); break;
                case "ramp_dark": DrawRamp(dc, w, h, 0, 48, 1, 1, 1); break;
                case "ramp_rgb": DrawRgbRamps(dc, w, h); break;
                case "black_steps": DrawBars(dc, w, h, MonitorHdrManager.BlackSteps); break;
                case "white_steps": DrawBars(dc, w, h, MonitorHdrManager.WhiteSteps); break;
                case "gamut": DrawGamut(dc); break;
                case "white_window": DrawWhiteWindow(dc, w, h); break;
                default: dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight)); break;
            }
        }

        // =================================================================
        //  Ramps and level bars
        // =================================================================

        /// <summary>One run of equal values per rectangle, one device pixel at the finest: each visible band is one 8-bit step.</summary>
        private void DrawRamp(DrawingContext dc, int w, int h, int low, int high, int r, int g, int b, int top = 0, int height = -1)
        {
            if (height < 0) height = h - top;
            int runStart = 0, runValue = MonitorHdrManager.RampValue(0, w, low, high);
            for (int x = 1; x <= w; x++)
            {
                int v = x < w ? MonitorHdrManager.RampValue(x, w, low, high) : -1;
                if (v == runValue) continue;
                dc.DrawRectangle(B((byte)(runValue * r), (byte)(runValue * g), (byte)(runValue * b)), null, PxRect(runStart, top, x - runStart, height));
                runStart = x;
                runValue = v;
            }
        }

        private void DrawRgbRamps(DrawingContext dc, int w, int h)
        {
            int rows = 4, rowH = h / rows;
            DrawRamp(dc, w, h, 0, 255, 1, 0, 0, 0, rowH);
            DrawRamp(dc, w, h, 0, 255, 0, 1, 0, rowH, rowH);
            DrawRamp(dc, w, h, 0, 255, 0, 0, 1, rowH * 2, rowH);
            DrawRamp(dc, w, h, 0, 255, 1, 1, 1, rowH * 3, h - rowH * 3);
        }

        private void DrawBars(DrawingContext dc, int w, int h, IReadOnlyList<byte> values)
        {
            int n = values.Count;
            for (int i = 0; i < n; i++)
            {
                int x0 = w * i / n, x1 = w * (i + 1) / n;
                dc.DrawRectangle(B(values[i]), null, PxRect(x0, 0, x1 - x0, h));
                if (!ShowLabels) continue;
                byte textLevel = values[i] < 128 ? (byte)Math.Min(255, values[i] + 70) : (byte)Math.Max(0, values[i] - 70);
                Text(dc, values[i].ToString(CultureInfo.InvariantCulture), Dx((x0 + x1) / 2.0), Dy(h * 0.88), 16, B(textLevel), center: true);
            }
        }

        // =================================================================
        //  White window
        // =================================================================

        /// <summary>A white area on black. Many HDR screens dim it as it grows (auto brightness limiting); watch the brightness change as the size changes.</summary>
        private void DrawWhiteWindow(DrawingContext dc, int w, int h)
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double share = Math.Clamp(WindowPercent, 1, 100) / 100.0;
            double side = Math.Sqrt(share);                       // same shape as the screen
            int ww = Math.Max(2, (int)Math.Round(w * side)), wh = Math.Max(2, (int)Math.Round(h * side));
            int x = (w - ww) / 2, y = (h - wh) / 2;
            dc.DrawRectangle(B(255), null, PxRect(x, y, ww, wh));
            if (ShowLabels && WindowPercent < 100)
                Text(dc, WindowPercent.ToString(CultureInfo.InvariantCulture) + " %", Dx(w / 2.0), Dy(h * 0.94), 18, B(150), center: true);
        }

        // =================================================================
        //  Gamut chart
        // =================================================================

        private void DrawGamut(DrawingContext dc)
        {
            double aw = ActualWidth, ah = ActualHeight;
            dc.DrawRectangle(B(16), null, new Rect(0, 0, aw, ah));

            // Plot area: x 0..0.8, y 0..0.9 in the chromaticity chart.
            double size = Math.Min(aw * 0.9 / 0.8, ah * 0.86 / 0.9);
            double ox = (aw - size * 0.8) / 2, oy = ah * 0.07 + size * 0.9;
            Point P(MonitorHdrXy c) => new(ox + c.X * size, oy - c.Y * size);

            var grid = new Pen(B(50), 1);
            for (int i = 0; i <= 8; i++)
                dc.DrawLine(grid, new Point(ox + i / 10.0 * size, oy), new Point(ox + i / 10.0 * size, oy - 0.9 * size));
            for (int i = 0; i <= 9; i++)
                dc.DrawLine(grid, new Point(ox, oy - i / 10.0 * size), new Point(ox + 0.8 * size, oy - i / 10.0 * size));

            var locus = MonitorHdrManager.SpectralLocus;
            var outline = new StreamGeometry();
            using (var ctx = outline.Open())
            {
                ctx.BeginFigure(P(locus[0]), true, true);
                ctx.PolyLineTo(locus.Skip(1).Select(P).ToList(), true, true);
            }
            outline.Freeze();
            dc.DrawGeometry(B(30), new Pen(B(190), 1.5), outline);

            Color[] colors =
            {
                Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0xFF, 0xB7, 0x4D), Color.FromRgb(0xBA, 0x68, 0xC8),
            };
            int index = 0;
            foreach (var (name, r, g, b) in MonitorHdrManager.StandardSpaces)
            {
                DrawTriangle(dc, P(r), P(g), P(b), new Pen(new SolidColorBrush(colors[index]), 2), null);
                if (ShowLabels)
                {
                    Text(dc, name, 24, ah * 0.08 + index * 22, 15, new SolidColorBrush(colors[index]), center: false);
                }
                index++;
            }

            if (Primaries is { IsKnown: true } p)
            {
                var fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                DrawTriangle(dc, P(p.Red), P(p.Green), P(p.Blue), new Pen(Brushes.White, 2.5) { DashStyle = DashStyles.Dash }, fill);
                dc.DrawEllipse(Brushes.White, null, P(p.White), 4, 4);
                if (ShowLabels) Text(dc, "●", 24, ah * 0.08 + 3 * 22, 15, Brushes.White, center: false);
            }

            // The primary colours themselves, as 8-bit swatches (sRGB values) at the foot of the chart.
            if (ShowLabels)
            {
                double sw = Math.Min(56, aw / 14), y = ah - sw - 14, x = 24;
                foreach (var c in new[] { Color.FromRgb(255, 0, 0), Color.FromRgb(0, 255, 0), Color.FromRgb(0, 0, 255), Color.FromRgb(255, 255, 255) })
                {
                    dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(x, y, sw, sw));
                    x += sw + 8;
                }
            }
        }

        private static void DrawTriangle(DrawingContext dc, Point a, Point b, Point c, Pen pen, Brush? fill)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(a, fill != null, true);
                ctx.LineTo(b, true, false);
                ctx.LineTo(c, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, pen, g);
        }

        private void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool center)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, LabelFace, size, brush, _dpi.PixelsPerDip);
            dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y - (center ? ft.Height / 2 : 0)));
        }
    }
}
