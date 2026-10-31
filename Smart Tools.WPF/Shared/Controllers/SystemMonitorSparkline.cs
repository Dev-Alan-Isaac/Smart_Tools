using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A live graph (the last minute of a value): a line with a soft fill
    /// under it, drawn in OnRender - cheap enough to redraw every second for
    /// a dozen graphs. Set <see cref="Values"/> to a new list to redraw.
    /// <see cref="Maximum"/> 0 = scale to the highest value shown (network,
    /// disk transfer); 100 for percentages. <see cref="Values2"/> adds a
    /// second, dashed line on the same scale (send next to receive, write
    /// next to read). <see cref="ShowGrid"/> draws Task Manager's grid.
    /// </summary>
    public sealed class SystemMonitorSparkline : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
            nameof(Values), typeof(IReadOnlyList<double>), typeof(SystemMonitorSparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty Values2Property = DependencyProperty.Register(
            nameof(Values2), typeof(IReadOnlyList<double>), typeof(SystemMonitorSparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            nameof(Maximum), typeof(double), typeof(SystemMonitorSparkline),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(SystemMonitorSparkline),
            new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
            nameof(Capacity), typeof(int), typeof(SystemMonitorSparkline),
            new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
            nameof(ShowGrid), typeof(bool), typeof(SystemMonitorSparkline),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

        /// <summary>A second line (dashed, no fill) on the same scale. Null = none.</summary>
        public IReadOnlyList<double>? Values2 { get => (IReadOnlyList<double>?)GetValue(Values2Property); set => SetValue(Values2Property, value); }

        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

        /// <summary>The line colour; the fill under it is the same colour, faint.</summary>
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

        /// <summary>How many points fill the width (new points come in on the right).</summary>
        public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

        /// <summary>Faint grid lines (10 across, 10 down), for the big graph.</summary>
        public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

        /// <summary>The value at the top of the scale right now (for a rate graph's label).</summary>
        public double ScaleMaximum(IReadOnlyList<double>? a, IReadOnlyList<double>? b)
        {
            if (Maximum > 0) return Maximum;
            double highest = Math.Max(a is { Count: > 0 } ? a.Max() : 0, b is { Count: > 0 } ? b.Max() : 0);
            return Math.Max(1e-9, highest * 1.15);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 2 || h <= 2) return;

            // Transparent background so the whole graph takes the mouse (tooltips, clicks).
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            if (ShowGrid)
            {
                var grid = new Pen(Stroke, 1);
                grid.Freeze();
                dc.PushOpacity(0.12);
                for (int i = 1; i < 10; i++)
                {
                    double x = Math.Round(w * i / 10) + 0.5, y = Math.Round(h * i / 10) + 0.5;
                    dc.DrawLine(grid, new Point(x, 0), new Point(x, h));
                    dc.DrawLine(grid, new Point(0, y), new Point(w, y));
                }
                dc.Pop();
            }
            else
            {
                var baseline = new Pen(Stroke, 1) { DashStyle = DashStyles.Dot };
                baseline.Freeze();
                dc.PushOpacity(0.25);
                dc.DrawLine(baseline, new Point(0, h - 0.5), new Point(w, h - 0.5));
                dc.Pop();
            }

            var values = Values;
            var values2 = Values2;
            double max = ScaleMaximum(values, values2);
            if (values is { Count: >= 2 }) DrawLine(dc, values, max, w, h, filled: true);
            if (values2 is { Count: >= 2 }) DrawLine(dc, values2, max, w, h, filled: false);
        }

        private void DrawLine(DrawingContext dc, IReadOnlyList<double> values, double max, double w, double h, bool filled)
        {
            int capacity = Math.Max(values.Count, Math.Max(2, Capacity));
            double step = w / (capacity - 1);
            double x0 = w - step * (values.Count - 1);   // newest point on the right edge
            Point P(int i) => new(x0 + i * step, h - 1 - Math.Clamp(values[i] / max, 0, 1) * (h - 3));

            var line = new StreamGeometry();
            using (var lc = line.Open())
            {
                lc.BeginFigure(P(0), false, false);
                for (int i = 1; i < values.Count; i++) lc.LineTo(P(i), true, true);
            }
            line.Freeze();

            if (filled)
            {
                var area = new StreamGeometry();
                using (var ac = area.Open())
                {
                    ac.BeginFigure(new Point(x0, h), true, true);
                    for (int i = 0; i < values.Count; i++) ac.LineTo(P(i), false, false);
                    ac.LineTo(new Point(w, h), false, false);
                }
                area.Freeze();
                var fill = Stroke.CloneCurrentValue();
                fill.Opacity = 0.16;
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }

            var pen = new Pen(Stroke, filled ? 1.6 : 1.4) { LineJoin = PenLineJoin.Round };
            if (!filled) pen.DashStyle = new DashStyle(new double[] { 3, 2 }, 0);
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }
    }
}
