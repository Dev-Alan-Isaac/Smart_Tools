using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The classic Wi-Fi channel graph: every network is a hill centred on its channel (wide enough to show the
    /// neighbouring channels it also disturbs) and as tall as its signal. Overlapping hills = a crowded channel.
    /// The network you are connected to is drawn solid in the accent colour; the recommended channel is marked.
    /// </summary>
    public sealed class ChannelChart : FrameworkElement
    {
        public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(ChannelChart),
            new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(ChannelChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(ChannelChart),
            new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GoodBrushProperty = DependencyProperty.Register(nameof(GoodBrush), typeof(Brush), typeof(ChannelChart),
            new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x84, 0xCC, 0x16), Color.FromRgb(0x14, 0xB8, 0xA6),
            Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x64, 0x74, 0x8B),
        };

        private IReadOnlyList<WifiNetwork> _networks = Array.Empty<WifiNetwork>();
        private WifiBand _band = WifiBand.Ghz24;
        private int _recommended;

        public ChannelChart()
        {
            SetResourceReference(AccentBrushProperty, "AccentColor");
            SetResourceReference(TextBrushProperty, "TextSecondary");
            SetResourceReference(GridBrushProperty, "CardBorder");
            SetResourceReference(GoodBrushProperty, "StatusSuccessFg");
        }

        public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
        public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
        public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush GoodBrush { get => (Brush)GetValue(GoodBrushProperty); set => SetValue(GoodBrushProperty, value); }

        /// <summary>Shows the networks of one band. <paramref name="recommended"/> is the channel to mark (0 = none).</summary>
        public void SetData(IReadOnlyList<WifiNetwork> networks, WifiBand band, int recommended)
        {
            _networks = networks.Where(n => n.Band == band && n.Channel > 0).ToList();
            _band = band;
            _recommended = recommended;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            if (w < 120 || h < 80) return;

            const double left = 40, right = 14, top = 16, bottom = 28;
            var plot = new Rect(left, top, w - left - right, h - top - bottom);

            // x range in channel numbers
            double x0, x1;
            if (_band == WifiBand.Ghz24) { x0 = -1; x1 = 16; }
            else
            {
                var cs = _networks.Select(n => n.Channel).Append(_recommended).Where(c => c > 0).ToList();
                int lo = cs.Count > 0 ? cs.Min() : (_band == WifiBand.Ghz5 ? 36 : 1);
                int hi = cs.Count > 0 ? cs.Max() : (_band == WifiBand.Ghz5 ? 64 : 29);
                x0 = lo - 6; x1 = hi + 6;
            }
            double X(double ch) => plot.Left + plot.Width * (ch - x0) / (x1 - x0);
            double Y(int rssi) => plot.Bottom - plot.Height * Math.Clamp((rssi + 100) / 70.0, 0.04, 1);

            var gridPen = new Pen(GridBrush, 1);
            for (int dbm = -90; dbm <= -40; dbm += 10)
            {
                double y = plot.Bottom - plot.Height * (dbm + 100) / 70.0;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                Text(dc, dbm.ToString(CultureInfo.InvariantCulture), new Point(left - 6, y), 9.5, TextBrush, right: true, middle: true);
            }

            // channel labels: every channel on 2.4 GHz, only those that matter on the wider bands
            var labels = _band == WifiBand.Ghz24
                ? Enumerable.Range(1, 14)
                : _networks.Select(n => n.Channel).Append(_recommended).Where(c => c > 0).Distinct().OrderBy(c => c);
            double lastX = double.NegativeInfinity;
            foreach (int c in labels)
            {
                double x = X(c);
                if (x - lastX < 24) continue;
                lastX = x;
                Text(dc, c.ToString(CultureInfo.InvariantCulture), new Point(x, plot.Bottom + 6), 10, TextBrush, center: true);
            }

            if (_recommended > 0)
            {
                double xr = X(_recommended);
                var rec = new Rect(X(_recommended - 2), plot.Top, X(_recommended + 2) - X(_recommended - 2), plot.Height);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 34, 197, 94)), null, rec);
                dc.DrawLine(new Pen(GoodBrush, 1.5) { DashStyle = DashStyles.Dash }, new Point(xr, plot.Top), new Point(xr, plot.Bottom));
                Text(dc, "best", new Point(xr, plot.Top - 2), 10, GoodBrush, center: true, bold: true);
            }

            if (_networks.Count == 0)
            {
                Text(dc, "No networks heard on this band", new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), 12, TextBrush, center: true);
                return;
            }

            // weakest first so strong networks stay on top
            int index = 0;
            foreach (var n in _networks.OrderBy(n => n.Rssi))
            {
                var colour = n.IsConnected ? ((AccentBrush as SolidColorBrush)?.Color ?? Palette[0]) : Palette[index++ % Palette.Length];
                double yTop = Y(n.Rssi);
                double half = 2;   // a 20 MHz channel reaches two channel numbers either side
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(X(n.Channel - half), plot.Bottom), true, true);
                    g.QuadraticBezierTo(new Point(X(n.Channel - half * 0.55), yTop - (plot.Bottom - yTop) * 0.25), new Point(X(n.Channel), yTop), true, true);
                    g.QuadraticBezierTo(new Point(X(n.Channel + half * 0.55), yTop - (plot.Bottom - yTop) * 0.25), new Point(X(n.Channel + half), plot.Bottom), true, true);
                }
                geo.Freeze();
                var fill = new SolidColorBrush(Color.FromArgb(n.IsConnected ? (byte)90 : (byte)55, colour.R, colour.G, colour.B));
                dc.DrawGeometry(fill, new Pen(new SolidColorBrush(colour), n.IsConnected ? 2.5 : 1.5), geo);
                if (!n.IsHidden && (n.IsConnected || n.Rssi > -80))
                    Text(dc, n.Ssid, new Point(X(n.Channel), yTop - 3), 10, new SolidColorBrush(colour), center: true, bottom: true, bold: n.IsConnected);
            }
        }

        private void Text(DrawingContext dc, string text, Point at, double size, Brush brush, bool right = false, bool center = false, bool bottom = false, bool middle = false, bool bold = false)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double x = right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
            double y = bottom ? at.Y - ft.Height : middle ? at.Y - ft.Height / 2 : at.Y;
            dc.DrawText(ft, new Point(x, y));
        }
    }
}
