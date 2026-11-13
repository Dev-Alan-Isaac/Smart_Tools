using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Rect = System.Windows.Rect;

namespace Smart_Tools.WPF.Controls
{
    public enum GlyphSide { Full, Left, Right }

    /// <summary>
    /// A device picture that fills with sound, like the battery gauge fills with health: a dim icon underneath and the same
    /// icon on top, clipped from the bottom up to the level. Use two of them (Left and Right) on a pair of headphones to show
    /// each side on its own. An optional peak line stays at the loudest point. Colours follow the theme.
    /// </summary>
    public sealed class LevelGlyph : Grid
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(PackIconKind), typeof(LevelGlyph),
            new PropertyMetadata(PackIconKind.Microphone, (d, _) => ((LevelGlyph)d).ApplyKind()));

        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(LevelGlyph),
            new PropertyMetadata(0.0, (d, _) => ((LevelGlyph)d).Update()));

        public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(nameof(Peak), typeof(double), typeof(LevelGlyph),
            new PropertyMetadata(0.0, (d, _) => ((LevelGlyph)d).Update()));

        public static readonly DependencyProperty SideProperty = DependencyProperty.Register(nameof(Side), typeof(GlyphSide), typeof(LevelGlyph),
            new PropertyMetadata(GlyphSide.Full, (d, _) => ((LevelGlyph)d).Update()));

        public static readonly DependencyProperty ShowTrackProperty = DependencyProperty.Register(nameof(ShowTrack), typeof(bool), typeof(LevelGlyph),
            new PropertyMetadata(true, (d, _) => ((LevelGlyph)d)._track.Visibility = (bool)d.GetValue(ShowTrackProperty) ? Visibility.Visible : Visibility.Collapsed));

        /// <summary>When on, the fill turns amber near the top and red at the very top (a microphone about to clip).</summary>
        public static readonly DependencyProperty AutoColorProperty = DependencyProperty.Register(nameof(AutoColor), typeof(bool), typeof(LevelGlyph),
            new PropertyMetadata(true, (d, _) => ((LevelGlyph)d).Update()));

        private readonly PackIcon _track = new() { Opacity = 0.28 };
        private readonly PackIcon _fill = new();
        private readonly PackIcon _peak = new();
        private readonly Grid _fillHost = new() { IsHitTestVisible = false };
        private readonly Grid _peakHost = new() { IsHitTestVisible = false };

        public LevelGlyph()
        {
            _track.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextSecondary");
            _fill.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "StatusSuccessFg");
            _peak.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimary");

            _fillHost.Children.Add(_fill);
            _peakHost.Children.Add(_peak);
            Children.Add(_track);
            Children.Add(_fillHost);
            Children.Add(_peakHost);
            ApplyKind();
            SizeChanged += (_, _) => Update();
        }

        public PackIconKind Kind { get => (PackIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
        public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
        public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
        public GlyphSide Side { get => (GlyphSide)GetValue(SideProperty); set => SetValue(SideProperty, value); }
        public bool ShowTrack { get => (bool)GetValue(ShowTrackProperty); set => SetValue(ShowTrackProperty, value); }
        public bool AutoColor { get => (bool)GetValue(AutoColorProperty); set => SetValue(AutoColorProperty, value); }

        /// <summary>A fixed fill colour (a theme key) for uses that are not a microphone level. Turns AutoColor off.</summary>
        public void SetFillResource(string key)
        {
            AutoColor = false;
            _fillKey = key;
            ApplyFillLook();
        }

        private void ApplyKind()
        {
            _track.Kind = _fill.Kind = _peak.Kind = Kind;
            Update();
        }

        private void Update()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // The icon is square: draw it as large as fits and centred; the clip is measured on that square.
            double s = Math.Min(w, h);
            foreach (var icon in new[] { _track, _fill, _peak })
            {
                icon.Width = icon.Height = s;
                icon.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                icon.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            }
            double left = (w - s) / 2, top = (h - s) / 2;

            double level = Math.Clamp(Level, 0, 1);
            double x = left, width = s;
            if (Side == GlyphSide.Left) width = s / 2;
            else if (Side == GlyphSide.Right) { x = left + s / 2; width = s / 2; }

            _fillHost.Clip = new RectangleGeometry(new Rect(x, top + s * (1 - level), width, s * level));

            double peak = Math.Clamp(Peak, 0, 1);
            _peakHost.Visibility = peak > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (peak > 0) _peakHost.Clip = new RectangleGeometry(new Rect(x, Math.Max(top, top + s * (1 - peak) - 1.5), width, 3));

            if (AutoColor)
                _fillKey = level >= 0.95 ? "StatusErrorFg" : level >= 0.8 ? "StatusWarningFg" : "StatusSuccessFg";
            ApplyFillLook();
        }

        private string _fillKey = "StatusSuccessFg";

        /// <summary>
        /// The fill is a vertical gradient (lighter at the top) with a glow in its own colour, and the empty icon underneath gets a
        /// soft drop shadow, so the picture looks lit and raised instead of a flat silhouette. Re-read on every update, so a theme
        /// change is picked up the next time the level moves.
        /// </summary>
        private void ApplyFillLook()
        {
            if (TryFindResource(_fillKey) is not SolidColorBrush brush) { _fill.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, _fillKey); return; }
            var c = brush.Color;
            var light = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.38), (byte)(c.G + (255 - c.G) * 0.38), (byte)(c.B + (255 - c.B) * 0.38));
            var dark = Color.FromRgb((byte)(c.R * 0.78), (byte)(c.G * 0.78), (byte)(c.B * 0.78));
            var gradient = new LinearGradientBrush(light, dark, 90);
            gradient.GradientStops.Insert(1, new GradientStop(c, 0.45));
            gradient.Freeze();
            _fill.Foreground = gradient;
            _fillHost.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = c, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.65 };
            _track.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 3, Direction = 270, Opacity = 0.35 };
        }
    }
}
