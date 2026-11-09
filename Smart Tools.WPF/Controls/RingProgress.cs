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
    /// A round progress bar: a track circle with an arc on it that grows clockwise from the top, with round ends.
    /// <see cref="Value"/> is 0..1. It draws itself (no template), so it stays sharp at any size; put the number or the
    /// text inside it by laying other elements over it. Used by Timer &amp; Stopwatch.
    /// </summary>
    public sealed class RingProgress : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, null, (_, v) => double.IsNaN((double)v) ? 0.0 : Math.Clamp((double)v, 0.0, 1.0)));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(RingProgress),
            new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
            nameof(Track), typeof(Brush), typeof(RingProgress),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>How far round the ring is filled, 0 (empty) to 1 (full).</summary>
        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

        /// <summary>Width of the ring's line, in device-independent pixels.</summary>
        public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

        /// <summary>The colour of the filled arc.</summary>
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

        /// <summary>The colour of the empty part of the ring.</summary>
        public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

        protected override Size MeasureOverride(Size availableSize)
        {
            double side = Math.Min(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
            return new Size(side, side);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double side = Math.Min(w, h);
            double t = Math.Min(Thickness, side / 2);
            double r = (side - t) / 2;
            if (r <= 0) return;
            var center = new Point(w / 2, h / 2);

            dc.DrawEllipse(null, new Pen(Track, t), center, r, r);

            double v = Value;
            if (v <= 0) return;

            var arcPen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (v >= 0.9999)
            {
                dc.DrawEllipse(null, new Pen(Stroke, t), center, r, r);
                return;
            }

            double angle = v * 2 * Math.PI;
            var start = new Point(center.X, center.Y - r);
            var end = new Point(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, arcPen, geometry);
        }
    }
}
