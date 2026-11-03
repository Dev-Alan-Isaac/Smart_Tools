using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A ring that fills up (clockwise from 12 o'clock) over a set time - the Image Viewer's slideshow
    /// countdown, shared with the Media Viewer and Video Replay. Anything placed inside it in XAML
    /// (usually a round Play button) sits in the middle. Collapsed while it isn't running.
    /// </summary>
    public sealed class CountdownRing : Grid
    {
        public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
            nameof(ArcBrush), typeof(System.Windows.Media.Brush), typeof(CountdownRing),
            new PropertyMetadata(null, (d, e) => ((CountdownRing)d)._arc.Stroke = (System.Windows.Media.Brush?)e.NewValue));

        public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
            nameof(TrackBrush), typeof(System.Windows.Media.Brush), typeof(CountdownRing),
            new PropertyMetadata(null, (d, e) => ((CountdownRing)d)._track.Stroke = (System.Windows.Media.Brush?)e.NewValue));

        public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
            nameof(RingThickness), typeof(double), typeof(CountdownRing),
            new PropertyMetadata(3.0, (d, e) =>
            {
                var ring = (CountdownRing)d;
                ring._arc.StrokeThickness = ring._track.StrokeThickness = (double)e.NewValue;
            }));

        private readonly Ellipse _track;
        private readonly Path _arc;
        private readonly DispatcherTimer _timer;
        private DateTime _start;
        private TimeSpan _duration = TimeSpan.FromSeconds(1);
        private bool _running;

        /// <summary>The time ran out (not raised by <see cref="Stop"/>).</summary>
        public event EventHandler? Completed;

        public CountdownRing()
        {
            Width = 68;
            Height = 68;
            Visibility = Visibility.Collapsed;

            // The grid is bigger than a 48 px button so the ring shows outside its edge.
            _track = new Ellipse
            {
                Width = 62,
                Height = 62,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 3,
            };
            _arc = new Path
            {
                Width = 68,
                Height = 68,
                Stretch = Stretch.None,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeThickness = 3,
            };
            _arc.SetResourceReference(Shape.StrokeProperty, "AccentColor");
            Children.Add(_track);
            Children.Add(_arc);

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            _timer.Tick += (_, _) => Tick();
            Unloaded += (_, _) => Stop();
        }

        public System.Windows.Media.Brush? ArcBrush { get => (System.Windows.Media.Brush?)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
        public System.Windows.Media.Brush? TrackBrush { get => (System.Windows.Media.Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
        public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }

        public bool IsRunning => _running;

        /// <summary>Starts filling from empty over <paramref name="duration"/>. Calling it again restarts it.</summary>
        public void Start(TimeSpan duration)
        {
            _duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
            _start = DateTime.UtcNow;
            _running = true;
            Visibility = Visibility.Visible;
            _arc.Data = null;
            _timer.Start();
        }

        /// <summary>Back to empty, same length (the slideshow moved to the next picture).</summary>
        public void Restart()
        {
            if (!_running) return;
            _start = DateTime.UtcNow;
        }

        public void Stop()
        {
            _running = false;
            _timer.Stop();
            _arc.Data = null;
            Visibility = Visibility.Collapsed;
        }

        private void Tick()
        {
            double fraction = Math.Clamp((DateTime.UtcNow - _start).TotalSeconds / _duration.TotalSeconds, 0, 1);
            _arc.Data = ImageViewerManager.BuildRingGeometry(fraction * 360);
            if (fraction < 1) return;

            // The slideshow keeps its own tick and restarts the ring; the skip countdown ends here.
            if (Completed == null) return;
            Stop();
            Completed.Invoke(this, EventArgs.Empty);
        }
    }
}
