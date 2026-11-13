using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A round plot of one analog stick: the reading before the dead zone (hollow ring), after it (solid dot), the dead
    /// zone itself, a short trail, and - for the range test - the shape the stick has swept. Up is positive.
    /// Colours come in as properties, so the view can bind them to theme tokens.
    /// </summary>
    public sealed class StickPlotControl : FrameworkElement
    {
        private static DependencyProperty Reg<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(StickPlotControl), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RawXProperty = Reg(nameof(RawX), 0.0);
        public static readonly DependencyProperty RawYProperty = Reg(nameof(RawY), 0.0);
        public static readonly DependencyProperty OutXProperty = Reg(nameof(OutX), 0.0);
        public static readonly DependencyProperty OutYProperty = Reg(nameof(OutY), 0.0);
        public static readonly DependencyProperty DeadzoneProperty = Reg(nameof(Deadzone), 0.0);
        public static readonly DependencyProperty SquareDeadzoneProperty = Reg(nameof(SquareDeadzone), false);
        public static readonly DependencyProperty EnvelopeProperty = Reg<double[]?>(nameof(Envelope), null);
        public static readonly DependencyProperty RestCenterProperty = Reg<Point?>(nameof(RestCenter), null);
        public static readonly DependencyProperty GridBrushProperty = Reg<Brush?>(nameof(GridBrush), null);
        public static readonly DependencyProperty AccentBrushProperty = Reg<Brush?>(nameof(AccentBrush), null);
        public static readonly DependencyProperty DotBrushProperty = Reg<Brush?>(nameof(DotBrush), null);
        public static readonly DependencyProperty BackBrushProperty = Reg<Brush?>(nameof(BackBrush), null);

        private readonly Queue<Point> _trail = new();
        private const int TrailLength = 18;

        public double RawX { get => (double)GetValue(RawXProperty); set => SetValue(RawXProperty, value); }
        public double RawY { get => (double)GetValue(RawYProperty); set => SetValue(RawYProperty, value); }
        public double OutX { get => (double)GetValue(OutXProperty); set => SetValue(OutXProperty, value); }
        public double OutY { get => (double)GetValue(OutYProperty); set => SetValue(OutYProperty, value); }
        public double Deadzone { get => (double)GetValue(DeadzoneProperty); set => SetValue(DeadzoneProperty, value); }
        public bool SquareDeadzone { get => (bool)GetValue(SquareDeadzoneProperty); set => SetValue(SquareDeadzoneProperty, value); }
        public double[]? Envelope { get => (double[]?)GetValue(EnvelopeProperty); set => SetValue(EnvelopeProperty, value); }
        public Point? RestCenter { get => (Point?)GetValue(RestCenterProperty); set => SetValue(RestCenterProperty, value); }
        public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
        public Brush? DotBrush { get => (Brush?)GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }
        public Brush? BackBrush { get => (Brush?)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }

        /// <summary>Forgets the trail (when the controller changes or a test starts).</summary>
        public void ClearTrail()
        {
            _trail.Clear();
            InvalidateVisual();
        }

        /// <summary>Moves the stick and remembers where it was, for the trail.</summary>
        public void SetReading(double rawX, double rawY, double outX, double outY)
        {
            _trail.Enqueue(new Point(rawX, rawY));
            while (_trail.Count > TrailLength) _trail.Dequeue();
            RawX = rawX;
            RawY = rawY;
            OutX = outX;
            OutY = outY;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double s = double.IsInfinity(availableSize.Width) ? 220 : Math.Min(availableSize.Width, double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);
            return new Size(Math.Max(120, s), Math.Max(120, s));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            var centre = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = size / 2 - 6;
            if (r <= 10) return;

            Point ToScreen(double x, double y) => new(centre.X + Math.Clamp(x, -1.05, 1.05) * r, centre.Y - Math.Clamp(y, -1.05, 1.05) * r);

            var grid = GridBrush ?? Brushes.Gray;
            var accent = AccentBrush ?? Brushes.DodgerBlue;
            var dot = DotBrush ?? Brushes.White;
            var gridPen = new Pen(grid, 1.2);
            var faintPen = new Pen(grid, 0.8) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };

            dc.DrawEllipse(BackBrush, gridPen, centre, r, r);
            dc.DrawEllipse(null, faintPen, centre, r * 0.5, r * 0.5);
            dc.DrawLine(faintPen, new Point(centre.X - r, centre.Y), new Point(centre.X + r, centre.Y));
            dc.DrawLine(faintPen, new Point(centre.X, centre.Y - r), new Point(centre.X, centre.Y + r));

            // dead zone
            if (Deadzone > 0)
            {
                var fill = accent.Clone();
                fill.Opacity = 0.16;
                var edge = new Pen(accent, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                if (SquareDeadzone) dc.DrawRectangle(fill, edge, new Rect(centre.X - Deadzone * r, centre.Y - Deadzone * r, Deadzone * r * 2, Deadzone * r * 2));
                else dc.DrawEllipse(fill, edge, centre, Deadzone * r, Deadzone * r);
            }

            // swept shape (range test)
            if (Envelope is { Length: > 2 } env)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    bool started = false;
                    for (int i = 0; i < env.Length; i++)
                    {
                        if (env[i] <= 0) continue;
                        double a = (i + 0.5) / env.Length * 2 * Math.PI;
                        var p = ToScreen(Math.Cos(a) * env[i], Math.Sin(a) * env[i]);
                        if (!started) { ctx.BeginFigure(p, true, true); started = true; }
                        else ctx.LineTo(p, true, false);
                    }
                }
                geometry.Freeze();
                var envFill = accent.Clone();
                envFill.Opacity = 0.22;
                dc.DrawGeometry(envFill, new Pen(accent, 2), geometry);
            }

            // where the stick rests (rest test)
            if (RestCenter is Point rc)
            {
                var p = ToScreen(rc.X, rc.Y);
                dc.DrawLine(new Pen(accent, 1.5), new Point(p.X - 6, p.Y), new Point(p.X + 6, p.Y));
                dc.DrawLine(new Pen(accent, 1.5), new Point(p.X, p.Y - 6), new Point(p.X, p.Y + 6));
            }

            // trail
            var trail = _trail.ToArray();
            for (int i = 1; i < trail.Length; i++)
            {
                var b = grid.Clone();
                b.Opacity = 0.15 + 0.6 * i / trail.Length;
                dc.DrawLine(new Pen(b, 2), ToScreen(trail[i - 1].X, trail[i - 1].Y), ToScreen(trail[i].X, trail[i].Y));
            }

            // before the dead zone: a ring. After: a solid dot.
            dc.DrawEllipse(null, new Pen(dot, 1.6), ToScreen(RawX, RawY), 7, 7);
            dc.DrawEllipse(accent, new Pen(dot, 1.2), ToScreen(OutX, OutY), 5, 5);
        }
    }

    /// <summary>A small line chart of a trigger pull: value (0..1) against time, with the straight line it would follow if it were perfectly even.</summary>
    public sealed class TriggerChartControl : FrameworkElement
    {
        private static DependencyProperty Reg<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(TriggerChartControl), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SamplesProperty = Reg<IReadOnlyList<(double Time, double Value)>?>(nameof(Samples), null);
        public static readonly DependencyProperty ThresholdProperty = Reg(nameof(Threshold), 0.0);
        public static readonly DependencyProperty LiveValueProperty = Reg(nameof(LiveValue), 0.0);
        public static readonly DependencyProperty GridBrushProperty = Reg<Brush?>(nameof(GridBrush), null);
        public static readonly DependencyProperty LineBrushProperty = Reg<Brush?>(nameof(LineBrush), null);
        public static readonly DependencyProperty BackBrushProperty = Reg<Brush?>(nameof(BackBrush), null);

        public IReadOnlyList<(double Time, double Value)>? Samples { get => ((IReadOnlyList<(double Time, double Value)>?)GetValue(SamplesProperty)); set => SetValue(SamplesProperty, value); }
        public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
        public double LiveValue { get => (double)GetValue(LiveValueProperty); set => SetValue(LiveValueProperty, value); }
        public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
        public Brush? BackBrush { get => (Brush?)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            var area = new Rect(8, 8, Math.Max(10, ActualWidth - 16), Math.Max(10, ActualHeight - 16));
            var grid = GridBrush ?? Brushes.Gray;
            var line = LineBrush ?? Brushes.DodgerBlue;
            var faint = new Pen(grid, 0.8) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };

            dc.DrawRoundedRectangle(BackBrush, new Pen(grid, 1), area, 8, 8);
            for (int i = 1; i < 4; i++)
            {
                double y = area.Bottom - area.Height * i / 4;
                dc.DrawLine(faint, new Point(area.Left, y), new Point(area.Right, y));
            }

            if (Threshold > 0)
            {
                double y = area.Bottom - area.Height * Threshold;
                dc.DrawLine(new Pen(line, 1) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) }, new Point(area.Left, y), new Point(area.Right, y));
            }

            // live value bar on the right edge
            double liveH = area.Height * Math.Clamp(LiveValue, 0, 1);
            dc.DrawRoundedRectangle(line, null, new Rect(area.Right - 7, area.Bottom - liveH, 5, liveH), 2, 2);

            var samples = Samples;
            if (samples == null || samples.Count < 2) return;

            double tMax = Math.Max(samples[^1].Time - samples[0].Time, 0.5);
            double t0 = samples[0].Time;
            Point P(double t, double v) => new(area.Left + (t - t0) / tMax * (area.Width - 12), area.Bottom - Math.Clamp(v, 0, 1) * area.Height);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(P(samples[0].Time, samples[0].Value), false, false);
                for (int i = 1; i < samples.Count; i++) ctx.LineTo(P(samples[i].Time, samples[i].Value), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(line, 2) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }
}
