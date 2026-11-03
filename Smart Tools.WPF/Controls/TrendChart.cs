using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A small line chart of values over time (the SMART history uses it). Draws a line with a dot per point,
    /// the lowest and highest value on the left and the first and last date underneath. Colours follow the
    /// theme (they are resource references, so a theme change repaints it).
    /// </summary>
    public sealed class TrendChart : FrameworkElement
    {
        public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(TrendChart),
            new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(TrendChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(TrendChart),
            new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty WarnBrushProperty = DependencyProperty.Register(nameof(WarnBrush), typeof(Brush), typeof(TrendChart),
            new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

        private IReadOnlyList<(DateTime When, double Value)> _points = Array.Empty<(DateTime, double)>();
        private Func<double, string> _format = v => v.ToString("0.##", CultureInfo.CurrentCulture);
        private bool _higherIsWorse;

        public TrendChart()
        {
            SetResourceReference(LineBrushProperty, "AccentColor");
            SetResourceReference(TextBrushProperty, "TextSecondary");
            SetResourceReference(GridBrushProperty, "CardBorder");
            SetResourceReference(WarnBrushProperty, "StatusErrorFg");
        }

        public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
        public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
        public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush WarnBrush { get => (Brush)GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }

        /// <summary>Sets what to draw. <paramref name="higherIsWorse"/> paints a rise in red (error counters, heat).</summary>
        public void SetData(IReadOnlyList<(DateTime When, double Value)> points, Func<double, string> format, bool higherIsWorse)
        {
            _points = points;
            _format = format;
            _higherIsWorse = higherIsWorse;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // so the whole area is hit-testable
            if (w < 60 || h < 50) return;

            const double left = 62, right = 14, top = 12, bottom = 26;
            var plot = new Rect(left, top, w - left - right, h - top - bottom);
            var gridPen = new Pen(GridBrush, 1);
            for (int i = 0; i <= 3; i++)
            {
                double y = plot.Top + plot.Height * i / 3;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }

            if (_points.Count == 0)
            {
                Text(dc, "No readings yet", new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), 12, TextBrush, center: true);
                return;
            }

            double min = _points.Min(p => p.Value), max = _points.Max(p => p.Value);
            if (Math.Abs(max - min) < 1e-9) { min -= 1; max += 1; }
            double pad = (max - min) * 0.1;
            double lo = min - pad, hi = max + pad;
            DateTime t0 = _points[0].When, t1 = _points[^1].When;
            double span = Math.Max(1, (t1 - t0).TotalSeconds);

            Point Map(DateTime when, double value) => new(
                _points.Count == 1 ? plot.Left + plot.Width / 2 : plot.Left + plot.Width * (when - t0).TotalSeconds / span,
                plot.Bottom - plot.Height * (value - lo) / (hi - lo));

            Text(dc, _format(hi), new Point(left - 8, plot.Top), 10.5, TextBrush, right: true);
            Text(dc, _format(lo), new Point(left - 8, plot.Bottom), 10.5, TextBrush, right: true, bottom: true);
            Text(dc, t0.ToString("d MMM yyyy", CultureInfo.CurrentCulture), new Point(plot.Left, plot.Bottom + 6), 10.5, TextBrush);
            if (_points.Count > 1) Text(dc, t1.ToString("d MMM yyyy", CultureInfo.CurrentCulture), new Point(plot.Right, plot.Bottom + 6), 10.5, TextBrush, right: true);

            if (_points.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(Map(_points[0].When, _points[0].Value), false, false);
                    for (int i = 1; i < _points.Count; i++) g.LineTo(Map(_points[i].When, _points[i].Value), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round }, geo);
            }

            // A dot per reading (just the ends when there are very many); a rise is red when higher means worse.
            int step = _points.Count > 120 ? _points.Count / 60 : 1;
            for (int i = 0; i < _points.Count; i += step)
                DrawDot(dc, i, Map(_points[i].When, _points[i].Value));
            DrawDot(dc, _points.Count - 1, Map(_points[^1].When, _points[^1].Value), big: true);
            Text(dc, _format(_points[^1].Value), Map(_points[^1].When, _points[^1].Value) + new Vector(-8, -8), 11, LineBrush, right: true, bottom: true, bold: true);
        }

        private void DrawDot(DrawingContext dc, int index, Point p, bool big = false)
        {
            bool rose = _higherIsWorse && index > 0 && _points[index].Value > _points[index - 1].Value;
            dc.DrawEllipse(rose ? WarnBrush : LineBrush, null, p, big ? 4.5 : 3, big ? 4.5 : 3);
        }

        private void Text(DrawingContext dc, string text, Point at, double size, Brush brush, bool right = false, bool bottom = false, bool center = false, bool bold = false)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double x = right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
            double y = bottom ? at.Y - ft.Height : center ? at.Y - ft.Height / 2 : at.Y;
            dc.DrawText(ft, new Point(x, y));
        }
    }
}
