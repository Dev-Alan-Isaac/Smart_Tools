using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>One program's values, one per slice of time.</summary>
    public sealed class StackSeries
    {
        public string Name { get; init; } = "";

        /// <summary>0-5: the heaviest programs; <see cref="ChartPalette.OtherIndex"/>: everything else. Resolved to a theme brush when drawn.</summary>
        public int ColorIndex { get; init; }

        public double[] Values { get; init; } = Array.Empty<double>();
    }

    /// <summary>What the chart draws: a time for each slice, the stacked programs (first = bottom), and how to write a value.</summary>
    public sealed class StackedData
    {
        public DateTime[] Times { get; init; } = Array.Empty<DateTime>();
        public double[] HasData { get; init; } = Array.Empty<double>();
        public IReadOnlyList<StackSeries> Series { get; init; } = Array.Empty<StackSeries>();
        public Func<double, string> Format { get; init; } = v => v.ToString("0.##", CultureInfo.CurrentCulture);
        public string TimeFormat { get; init; } = "HH:mm";
    }

    /// <summary>The series colours, as theme brushes (ChartSeries1..6 and ChartSeriesOther in Light/Dark.xaml).</summary>
    public static class ChartPalette
    {
        public const int Colors = 6;
        public const int OtherIndex = Colors;

        public static int IndexFor(int rank) => rank < Colors ? rank : OtherIndex;

        public static Brush Brush(int index)
        {
            string key = index >= Colors || index < 0 ? "ChartSeriesOther" : "ChartSeries" + (index + 1);
            return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        }
    }

    /// <summary>
    /// Columns over time, each column the stack of the heaviest programs in that slice. Hover a column for the figures; the chosen
    /// program stays strong and the others fade. Colours of the programs come with the data; the frame follows the theme.
    /// </summary>
    public sealed class StackedUsageChart : FrameworkElement
    {
        public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(StackedUsageChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(StackedUsageChart),
            new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

        // Follows the first series colour so the chart redraws when the theme changes (the series brushes are looked up while drawing).
        public static readonly DependencyProperty ThemeStampProperty = DependencyProperty.Register(nameof(ThemeStamp), typeof(Brush), typeof(StackedUsageChart),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush ThemeStamp { get => (Brush)GetValue(ThemeStampProperty); set => SetValue(ThemeStampProperty, value); }

        public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(nameof(HoverBrush), typeof(Brush), typeof(StackedUsageChart),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        private StackedData _data = new();
        private string? _highlight;
        private int _hover = -1;

        public StackedUsageChart()
        {
            SetResourceReference(TextBrushProperty, "TextSecondary");
            SetResourceReference(GridBrushProperty, "CardBorder");
            SetResourceReference(HoverBrushProperty, "HoverOverlayFill");
            SetResourceReference(ThemeStampProperty, "ChartSeries1");
            ToolTipService.SetInitialShowDelay(this, 0);
            ToolTipService.SetShowDuration(this, 60000);
            ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Mouse);
        }

        public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
        public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }

        public void SetData(StackedData data)
        {
            _data = data;
            _hover = -1;
            ToolTip = null;
            InvalidateVisual();
        }

        /// <summary>The program that stays at full strength (null = all).</summary>
        public string? Highlight
        {
            get => _highlight;
            set { _highlight = value; InvalidateVisual(); }
        }

        private const double Left = 60, Right = 8, Top = 10, Bottom = 24;

        private Rect Plot => new(Left, Top, Math.Max(1, ActualWidth - Left - Right), Math.Max(1, ActualHeight - Top - Bottom));

        private int Columns => _data.Times.Length;

        private static double Nice(double max)
        {
            if (max <= 0) return 1;
            double exp = Math.Pow(10, Math.Floor(Math.Log10(max)));
            double f = max / exp;
            double n = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;   // fifths of 1, 2, 5 or 10 are round numbers
            return n * exp;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // so the whole area is hit-testable
            if (w < 80 || h < 70) return;
            var plot = Plot;
            int n = Columns;

            double[] totals = new double[n];
            for (int i = 0; i < n; i++) totals[i] = _data.Series.Sum(s => i < s.Values.Length ? s.Values[i] : 0);
            double top = Nice(Math.Max(1e-9, totals.DefaultIfEmpty(0).Max()) * 1.05);

            var gridPen = new Pen(GridBrush, 1);
            for (int i = 0; i <= 5; i++)
            {
                double y = plot.Bottom - plot.Height * i / 5;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                if (i > 0) Text(dc, _data.Format(top * i / 5), new Point(plot.Left - 8, y), 10.5, right: true, middle: true);
            }
            Text(dc, _data.Format(0), new Point(plot.Left - 8, plot.Bottom), 10.5, right: true, middle: true);

            if (n == 0 || totals.All(t => t <= 0))
            {
                Text(dc, Loc.T("Str_AppUsage_NoReadings", "No readings in this period"), new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), 12, center: true, middle: true);
                return;
            }

            double colW = plot.Width / n;
            double barW = Math.Max(1, colW - (colW > 6 ? 2 : 0.5));
            for (int i = 0; i < n; i++)
            {
                double x = plot.Left + colW * i + (colW - barW) / 2;
                if (i == _hover) dc.DrawRectangle(HoverBrush, null, new Rect(plot.Left + colW * i, plot.Top, colW, plot.Height));
                double y = plot.Bottom;
                foreach (var s in _data.Series)
                {
                    double v = i < s.Values.Length ? s.Values[i] : 0;
                    if (v <= 0) continue;
                    double hh = plot.Height * v / top;
                    var rect = new Rect(x, y - hh, barW, hh);
                    bool strong = _highlight == null || string.Equals(_highlight, s.Name, StringComparison.OrdinalIgnoreCase);
                    dc.PushOpacity(strong ? 1.0 : 0.22);
                    dc.DrawRectangle(ChartPalette.Brush(s.ColorIndex), null, rect);
                    dc.Pop();
                    y -= hh;
                }
            }

            // Time labels: the start, the middle and the end.
            string F(DateTime t) => t.ToString(_data.TimeFormat, CultureInfo.CurrentCulture);
            Text(dc, F(_data.Times[0]), new Point(plot.Left, plot.Bottom + 6), 10.5);
            if (n > 2) Text(dc, F(_data.Times[n / 2]), new Point(plot.Left + plot.Width / 2, plot.Bottom + 6), 10.5, center: true);
            Text(dc, F(_data.Times[n - 1]), new Point(plot.Right, plot.Bottom + 6), 10.5, right: true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = Columns;
            if (n == 0) return;
            var plot = Plot;
            var p = e.GetPosition(this);
            int i = p.X < plot.Left || p.X > plot.Right ? -1 : Math.Clamp((int)((p.X - plot.Left) / (plot.Width / n)), 0, n - 1);
            if (i == _hover) return;
            _hover = i;
            ToolTip = i < 0 ? null : TipFor(i);
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover == -1) return;
            _hover = -1;
            ToolTip = null;
            InvalidateVisual();
        }

        private string? TipFor(int i)
        {
            var lines = new List<string> { _data.Times[i].ToString(_data.TimeFormat == "HH:mm" ? "ddd HH:mm" : _data.TimeFormat, CultureInfo.CurrentCulture) };
            foreach (var s in _data.Series.Reverse())
                if (i < s.Values.Length && s.Values[i] > 0) lines.Add($"{s.Name}   {_data.Format(s.Values[i])}");
            return lines.Count == 1 ? null : string.Join("\n", lines);
        }

        private void Text(DrawingContext dc, string text, Point at, double size, bool right = false, bool center = false, bool middle = false)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double x = right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
            double y = middle ? at.Y - ft.Height / 2 : at.Y;
            dc.DrawText(ft, new Point(x, y));
        }
    }
}
