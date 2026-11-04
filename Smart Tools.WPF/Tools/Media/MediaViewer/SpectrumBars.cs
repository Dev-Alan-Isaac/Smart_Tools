using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The coloured spectrum bars of the old Music page, as a control: one gradient bar per band (bass red ... treble violet)
    /// with a small peak cap that jumps to each new high and drifts back down. It asks <see cref="Source"/> for fresh levels
    /// about 30 times a second, but only while it is on screen, and settles to rest when the source reports nothing playing.
    /// Colours are computed from hue, so they are the same in Light and Dark and need no theme token.
    /// </summary>
    public sealed class SpectrumBars : UserControl
    {
        private const int BarCount = 40;
        private const double MinBarHeight = 3;
        private const double PeakCapHeight = 2;
        private const double PeakFallPerTick = 1.6;   // px per 30 ms tick
        private const int PeakHoldTicks = 12;         // ~360 ms before a cap starts falling

        private readonly UniformGrid _grid = new() { Rows = 1, Columns = BarCount };
        private readonly Rectangle[] _bars = new Rectangle[BarCount];
        private readonly TranslateTransform[] _caps = new TranslateTransform[BarCount];
        private readonly float[] _levels = new float[BarCount];
        private readonly double[] _heights = new double[BarCount];
        private readonly double[] _peaks = new double[BarCount];
        private readonly int[] _hold = new int[BarCount];
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };

        /// <summary>Fills the 0..1 levels (one per bar) and returns false when nothing is playing.</summary>
        public Func<float[], bool>? Source { get; set; }

        public SpectrumBars()
        {
            IsHitTestVisible = false;
            Content = _grid;
            Build();
            _timer.Tick += (_, _) => Step();
            IsVisibleChanged += (_, _) => UpdateTimer();
            Loaded += (_, _) => UpdateTimer();
            Unloaded += (_, _) => _timer.Stop();
        }

        private void UpdateTimer()
        {
            if (IsVisible && IsLoaded) _timer.Start();
            else _timer.Stop();
        }

        private void Build()
        {
            for (int i = 0; i < BarCount; i++)
            {
                double hue = 270.0 * i / (BarCount - 1);
                Color top = HsvToRgb(hue, 0.75, 1.0), bottom = HsvToRgb(hue, 0.95, 0.55);

                var barBrush = new LinearGradientBrush(bottom, top, new System.Windows.Point(0, 1), new System.Windows.Point(0, 0));
                barBrush.Freeze();
                var capBrush = new SolidColorBrush(top);
                capBrush.Freeze();

                var bar = new Rectangle { Height = MinBarHeight, RadiusX = 1.5, RadiusY = 1.5, Fill = barBrush, VerticalAlignment = VerticalAlignment.Bottom };
                var shift = new TranslateTransform();
                var cap = new Rectangle { Height = PeakCapHeight, RadiusX = 1, RadiusY = 1, Fill = capBrush, Opacity = 0.9, VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = shift };

                var cell = new Grid { Margin = new Thickness(1, 0, 1, 0) };
                cell.Children.Add(bar);
                cell.Children.Add(cap);
                _grid.Children.Add(cell);
                _bars[i] = bar;
                _caps[i] = shift;
            }
        }

        private void Step()
        {
            if (Source?.Invoke(_levels) != true) Array.Clear(_levels);

            double maxHeight = Math.Max(20, ActualHeight - PeakCapHeight - 2);
            for (int i = 0; i < BarCount; i++)
            {
                double target = _levels[i] * maxHeight;
                double next = _heights[i] + (target - _heights[i]) * (target > _heights[i] ? 0.35 : 0.12);   // fast attack, slow release
                _heights[i] = next;
                _bars[i].Height = Math.Max(MinBarHeight, next);

                if (next >= _peaks[i]) { _peaks[i] = next; _hold[i] = PeakHoldTicks; }
                else if (_hold[i] > 0) _hold[i]--;
                else _peaks[i] = Math.Max(0, _peaks[i] - PeakFallPerTick);

                _caps[i].Y = -Math.Max(MinBarHeight, _peaks[i]) - 1;
            }
        }

        private static Color HsvToRgb(double hue, double saturation, double value)
        {
            hue %= 360.0;
            if (hue < 0) hue += 360.0;
            double c = value * saturation, x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1)), m = value - c;
            (double r, double g, double b) = hue switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }
}
