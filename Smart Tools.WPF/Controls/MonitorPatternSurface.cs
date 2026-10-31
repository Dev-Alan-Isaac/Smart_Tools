using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Draws one monitor test picture, pixel-exactly: the picture is laid out in device pixels, so a 1-pixel line really is one pixel
    /// whatever the Windows scaling. Used full-screen by the test window and small as the tiles of the picker.
    /// What each picture shows is decided by <see cref="MonitorTestManager"/>; this only paints it.
    /// </summary>
    public sealed class MonitorPatternSurface : FrameworkElement
    {
        private static DependencyProperty Reg<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(MonitorPatternSurface), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PatternIdProperty = Reg(nameof(PatternId), "solid_black");
        public static readonly DependencyProperty TimeProperty = Reg(nameof(Time), 0.0);
        public static readonly DependencyProperty ShowLabelsProperty = Reg(nameof(ShowLabels), true);

        private static readonly Dictionary<int, SolidColorBrush> BrushCache = new();
        private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        public string PatternId { get => (string)GetValue(PatternIdProperty); set => SetValue(PatternIdProperty, value); }

        /// <summary>Seconds since the picture started, for the moving and cycling ones.</summary>
        public double Time { get => (double)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }

        /// <summary>Numbers and captions on the calibration pictures (off for the small tiles).</summary>
        public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

        public MonitorPatternSurface()
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

        // ---- layout in device pixels -------------------------------------------------

        private DpiScale _dpi;

        private double Dx(double px) => px / _dpi.DpiScaleX;
        private double Dy(double px) => px / _dpi.DpiScaleY;
        private Rect PxRect(double x, double y, double w, double h) => new(Dx(x), Dy(y), Dx(w), Dy(h));

        protected override void OnRender(DrawingContext dc)
        {
            _dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(ActualWidth * _dpi.DpiScaleX), h = (int)Math.Round(ActualHeight * _dpi.DpiScaleY);
            if (w < 2 || h < 2) return;

            string id = PatternId;
            if (MonitorTestManager.SolidColor(id) is { } c)
            {
                dc.DrawRectangle(B(c.R, c.G, c.B), null, new Rect(0, 0, ActualWidth, ActualHeight));
                return;
            }

            switch (id)
            {
                case "ramp_gray": DrawRamp(dc, w, h, 1, 1, 1); break;
                case "ramp_red": DrawRamp(dc, w, h, 1, 0, 0); break;
                case "ramp_green": DrawRamp(dc, w, h, 0, 1, 0); break;
                case "ramp_blue": DrawRamp(dc, w, h, 0, 0, 1); break;
                case "steps_gray": DrawBars(dc, w, h, Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToList()); break;
                case "black_levels": DrawBars(dc, w, h, MonitorTestManager.BlackLevels); break;
                case "white_levels": DrawBars(dc, w, h, MonitorTestManager.WhiteLevels); break;
                case "gray_steps": DrawBars(dc, w, h, MonitorTestManager.GrayStepValues); break;
                case "gamma": DrawGamma(dc, w, h); break;
                case "color_bars": DrawColorBars(dc, w, h); break;
                case "sharpness": DrawSharpness(dc, w, h); break;
                case "geometry": DrawGeometry(dc, w, h); break;
                case "checker_1px": DrawTiled(dc, w, h, checker: true); break;
                case "lines": DrawTiled(dc, w, h, checker: false); break;
                case "cycle_colors":
                {
                    var col = MonitorTestManager.CycleColors[MonitorTestManager.CycleIndex(Time, 1.5, MonitorTestManager.CycleColors.Count)];
                    dc.DrawRectangle(B(col.R, col.G, col.B), null, new Rect(0, 0, ActualWidth, ActualHeight));
                    break;
                }
                case "inverse": DrawInverse(dc, w, h); break;
                case "exerciser":
                {
                    var col = MonitorTestManager.ExerciserColor((int)(Time * 15));
                    dc.DrawRectangle(B(col.R, col.G, col.B), null, new Rect(0, 0, ActualWidth, ActualHeight));
                    break;
                }
                case "ufo": DrawUfo(dc, w, h); break;
                case "scroll_bars": DrawScrollBars(dc, w, h); break;
                default:
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
                    break;
            }
        }

        // =================================================================
        //  Gradients and level bars
        // =================================================================

        /// <summary>One column of device pixels per step, so banding shows as it is, not smoothed by the GPU.</summary>
        private void DrawRamp(DrawingContext dc, int w, int h, int r, int g, int b)
        {
            int runStart = 0, runValue = ValueAt(0, w);
            for (int x = 1; x <= w; x++)
            {
                int v = x < w ? ValueAt(x, w) : -1;
                if (v == runValue) continue;
                dc.DrawRectangle(B((byte)(runValue * r), (byte)(runValue * g), (byte)(runValue * b)), null, PxRect(runStart, 0, x - runStart, h));
                runStart = x;
                runValue = v;
            }

            static int ValueAt(int x, int width) => (int)Math.Round(255.0 * x / Math.Max(1, width - 1));
        }

        private void DrawBars(DrawingContext dc, int w, int h, IReadOnlyList<byte> values)
        {
            int n = values.Count;
            for (int i = 0; i < n; i++)
            {
                int x0 = w * i / n, x1 = w * (i + 1) / n;
                dc.DrawRectangle(B(values[i]), null, PxRect(x0, 0, x1 - x0, h));
                if (ShowLabels)
                {
                    // The number is drawn in a grey that stays readable but doesn't change what is being judged.
                    byte textLevel = values[i] < 128 ? (byte)Math.Min(255, values[i] + 70) : (byte)Math.Max(0, values[i] - 70);
                    Label(dc, values[i].ToString(CultureInfo.InvariantCulture), (x0 + x1) / 2.0, h - 40, 16, B(textLevel), center: true);
                }
            }
            if (ShowLabels) Hint(dc, w, h);
        }

        // =================================================================
        //  Calibration pictures
        // =================================================================

        private void DrawGamma(DrawingContext dc, int w, int h)
        {
            dc.DrawRectangle(B(0), null, PxRect(0, 0, w, h));
            var gammas = MonitorTestManager.GammaCandidates;
            int n = gammas.Count;
            int cell = w / n;
            for (int i = 0; i < n; i++)
            {
                int x0 = i * cell + cell / 8, boxW = cell * 3 / 4, boxH = Math.Min(h * 2 / 3, boxW * 2);
                int y0 = (h - boxH) / 2;
                int half = boxW / 2;

                // left: one-pixel lines, black and white; right: the grey that matches them for this gamma
                dc.DrawRectangle(B(0), null, PxRect(x0, y0, half, boxH));
                for (int y = 1; y < boxH; y += 2) dc.DrawRectangle(B(255), null, PxRect(x0, y0 + y, half, 1));
                dc.DrawRectangle(B(MonitorTestManager.GammaMatchGray(gammas[i])), null, PxRect(x0 + half, y0, boxW - half, boxH));

                if (ShowLabels) Label(dc, gammas[i].ToString("0.0", CultureInfo.InvariantCulture), x0 + boxW / 2.0, y0 + boxH + 14, 26, B(200), center: true);
            }
            if (ShowLabels) Hint(dc, w, h);
        }

        private void DrawColorBars(DrawingContext dc, int w, int h)
        {
            var bars = MonitorTestManager.ColorBars;
            int n = bars.Count, topH = h * 3 / 4;
            for (int i = 0; i < n; i++)
            {
                int x0 = w * i / n, x1 = w * (i + 1) / n;
                dc.DrawRectangle(B(bars[i].R, bars[i].G, bars[i].B), null, PxRect(x0, 0, x1 - x0, topH));
            }
            // below: a grey ramp of 16 steps and a strip of pure primaries at 50%
            for (int i = 0; i < 16; i++)
            {
                int x0 = w * i / 16, x1 = w * (i + 1) / 16;
                dc.DrawRectangle(B((byte)(i * 17)), null, PxRect(x0, topH, x1 - x0, h - topH));
            }
        }

        private void DrawSharpness(DrawingContext dc, int w, int h)
        {
            dc.DrawRectangle(B(0), null, PxRect(0, 0, w, h));
            int half = w / 2;

            // left half: black text on white, right half: white text on black, from small to large
            dc.DrawRectangle(B(255), null, PxRect(0, 0, half, h));
            string[] lines = { "The quick brown fox jumps over the lazy dog 0123456789", "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz" };
            double y = 20;
            foreach (double size in new[] { 9, 10, 12, 14, 18 })
            {
                foreach (var line in lines)
                {
                    Label(dc, line, 20, y, size, B(0), center: false, maxWidth: half - 40);
                    Label(dc, line, half + 20, y, size, B(255), center: false, maxWidth: half - 40);
                    y += size * 1.5;
                }
                y += 6;
            }

            // fine line groups at the bottom: 1, 2 and 3 pixel lines in both directions
            double gy = Math.Max(y + 10, h * 0.62);
            int block = Math.Min(160, half / 4);
            for (int g = 0; g < 3; g++)
            {
                int pitch = g + 1;
                for (int side = 0; side < 2; side++)
                {
                    int gx = side * half + 20 + g * (block + 20);
                    var ink = side == 0 ? B(0) : B(255);
                    for (int i = 0; i < block; i += pitch * 2) dc.DrawRectangle(ink, null, PxRect(gx + i, gy, pitch, block / 2.0));
                    double gy2 = gy + block / 2.0 + 10;
                    for (int j = 0; j < block / 2; j += pitch * 2) dc.DrawRectangle(ink, null, PxRect(gx, gy2 + j, block, pitch));
                }
            }
            if (ShowLabels) Hint(dc, w, h);
        }

        private void DrawGeometry(DrawingContext dc, int w, int h)
        {
            dc.DrawRectangle(B(32), null, PxRect(0, 0, w, h));
            var grid = B(110);
            for (int x = 0; x < w; x += 100) dc.DrawRectangle(grid, null, PxRect(x, 0, 1, h));
            for (int y = 0; y < h; y += 100) dc.DrawRectangle(grid, null, PxRect(0, y, w, 1));

            var white = B(255);
            // outer frame, one pixel thick, exactly on the edge
            dc.DrawRectangle(white, null, PxRect(0, 0, w, 1));
            dc.DrawRectangle(white, null, PxRect(0, h - 1, w, 1));
            dc.DrawRectangle(white, null, PxRect(0, 0, 1, h));
            dc.DrawRectangle(white, null, PxRect(w - 1, 0, 1, h));

            var penWhite = new Pen(white, Dx(1.5));
            dc.DrawLine(penWhite, new Point(0, 0), new Point(ActualWidth, ActualHeight));
            dc.DrawLine(penWhite, new Point(ActualWidth, 0), new Point(0, ActualHeight));
            double r = Math.Min(ActualWidth, ActualHeight) / 2 - Dx(2);
            dc.DrawEllipse(null, penWhite, new Point(ActualWidth / 2, ActualHeight / 2), r, r);
            dc.DrawEllipse(null, penWhite, new Point(ActualWidth / 2, ActualHeight / 2), r / 2, r / 2);

            // corner squares in four colours, to see the picture reaches every edge
            int s = Math.Max(24, Math.Min(w, h) / 18);
            dc.DrawRectangle(B(255, 0, 0), null, PxRect(0, 0, s, s));
            dc.DrawRectangle(B(0, 255, 0), null, PxRect(w - s, 0, s, s));
            dc.DrawRectangle(B(0, 0, 255), null, PxRect(0, h - s, s, s));
            dc.DrawRectangle(B(255, 255, 0), null, PxRect(w - s, h - s, s, s));

            if (ShowLabels)
            {
                Label(dc, $"{w} × {h}", w / 2.0, h / 2.0 - 14, 28, white, center: true);
                Hint(dc, w, h);
            }
        }

        /// <summary>Checkerboard or line pattern of single pixels, tiled in device pixels.</summary>
        private void DrawTiled(DrawingContext dc, int w, int h, bool checker)
        {
            var tile = new DrawingGroup();
            using (var tdc = tile.Open())
            {
                tdc.DrawRectangle(B(0), null, new Rect(0, 0, Dx(2), Dy(2)));
                if (checker)
                {
                    tdc.DrawRectangle(B(255), null, new Rect(0, 0, Dx(1), Dy(1)));
                    tdc.DrawRectangle(B(255), null, new Rect(Dx(1), Dy(1), Dx(1), Dy(1)));
                }
                else
                {
                    tdc.DrawRectangle(B(255), null, new Rect(0, 0, Dx(1), Dy(2)));   // vertical lines on the left half below
                }
            }
            var brush = new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, Dx(2), Dy(2)),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, Dx(2), Dy(2)),
                ViewboxUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();

            if (checker)
            {
                dc.DrawRectangle(brush, null, new Rect(0, 0, ActualWidth, ActualHeight));
                return;
            }

            // left half vertical lines, right half horizontal lines
            int half = w / 2;
            dc.DrawRectangle(brush, null, PxRect(0, 0, half, h));
            dc.DrawRectangle(B(0), null, PxRect(half, 0, w - half, h));
            for (int y = 0; y < h; y += 2) dc.DrawRectangle(B(255), null, PxRect(half, y, w - half, 1));
        }

        // =================================================================
        //  Retention and motion
        // =================================================================

        private void DrawInverse(DrawingContext dc, int w, int h)
        {
            bool inverted = MonitorTestManager.InverseOn(Time, 1.0);
            byte a = inverted ? (byte)255 : (byte)0, b = inverted ? (byte)0 : (byte)255;
            int bar = Math.Max(20, w / 24);
            for (int x = 0, i = 0; x < w; x += bar, i++)
                dc.DrawRectangle(B(i % 2 == 0 ? a : b), null, PxRect(x, 0, Math.Min(bar, w - x), h));
        }

        private void DrawUfo(DrawingContext dc, int w, int h)
        {
            dc.DrawRectangle(B(40), null, PxRect(0, 0, w, h));
            var speeds = MonitorTestManager.UfoSpeeds;
            int lane = h / speeds.Count;
            int block = Math.Max(40, lane / 2);
            for (int i = 0; i < speeds.Count; i++)
            {
                int y0 = i * lane;
                dc.DrawRectangle(B((byte)(i % 2 == 0 ? 55 : 70)), null, PxRect(0, y0, w, lane));
                double x = MonitorTestManager.MovingPosition(Time, speeds[i], w, block);
                dc.DrawRectangle(B(255), null, PxRect(x, y0 + (lane - block) / 2.0, block, block));
                if (ShowLabels) Label(dc, $"{speeds[i]} px/s", 16, y0 + 10, 18, B(170), center: false);
            }
        }

        private void DrawScrollBars(DrawingContext dc, int w, int h)
        {
            int bar = Math.Max(20, w / 32);
            double offset = (Time * 960) % (bar * 2);
            dc.DrawRectangle(B(0), null, PxRect(0, 0, w, h));
            for (double x = -bar * 2 + offset; x < w; x += bar * 2)
                dc.DrawRectangle(B(255), null, PxRect(Math.Round(x), 0, bar, h));
        }

        // =================================================================
        //  Text
        // =================================================================

        private void Label(DrawingContext dc, string text, double xPx, double yPx, double size, Brush brush, bool center, double maxWidth = 0)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, LabelFace, size, brush, _dpi.PixelsPerDip);
            if (maxWidth > 0) ft.MaxTextWidth = Dx(maxWidth);
            var origin = new Point(Dx(xPx) - (center ? ft.Width / 2 : 0), Dy(yPx));
            dc.DrawText(ft, origin);
        }

        private void Hint(DrawingContext dc, int w, int h)
        {
            // a small dim caption of the screen size in the corner; the picture itself stays untouched
            Label(dc, $"{w} × {h}", 12, 8, 12, B(120), center: false);
        }
    }
}
