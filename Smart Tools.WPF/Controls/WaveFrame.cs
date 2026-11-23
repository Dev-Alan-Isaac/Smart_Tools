using System.Windows;
using System.Windows.Controls;
using Border = System.Windows.Controls.Border;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A waveform in a rounded frame with the theme's colours and an empty-state line: the look of the Audio Trimmer's waveform, ready to drop
    /// into any view (Voice Studio's recorder and voice changer). A finished sound goes in with <see cref="Show"/>.
    /// </summary>
    public sealed class WaveFrame : Border
    {
        private readonly TextBlock _empty;

        public AudioWaveformControl Wave { get; } = new();

        /// <summary>Shown while there is no wave.</summary>
        public string EmptyText { get => _empty.Text; set => _empty.Text = value; }

        public WaveFrame()
        {
            MinHeight = 120;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(12);
            ClipToBounds = true;
            SetResourceReference(BorderBrushProperty, "CardBorder");

            Wave.SetResourceReference(AudioWaveformControl.BackBrushProperty, "RowBg");
            Wave.SetResourceReference(AudioWaveformControl.GridBrushProperty, "CardBorder");
            Wave.SetResourceReference(AudioWaveformControl.PlayheadBrushProperty, "StatusErrorFg");
            Wave.SetResourceReference(AudioWaveformControl.SelectionBrushProperty, "AccentColorSubtle");
            Wave.SetResourceReference(AudioWaveformControl.SelectionEdgeBrushProperty, "AccentColor");
            Wave.SetResourceReference(AudioWaveformControl.TextBrushProperty, "TextSecondary");
            Wave.SetResourceReference(AudioWaveformControl.WaveBrushProperty, "AccentColor");

            _empty = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12, IsHitTestVisible = false, Margin = new Thickness(16, 0, 16, 0) };
            _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            var grid = new Grid();
            grid.Children.Add(Wave);
            grid.Children.Add(_empty);
            Child = grid;
        }

        /// <summary>A finished sound: the whole of it, fitted to the width.</summary>
        public void Show(byte[]? peaks, double seconds)
        {
            bool has = peaks is { Length: > 0 } && seconds > 0;
            _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            Wave.AutoScale = true;
            Wave.ClearSelection();
            Wave.Playhead = double.NaN;
            Wave.Peaks = has ? peaks : null;
            Wave.Duration = has ? seconds : 0;
            Wave.ClipStart = 0;
            Wave.ClipEnd = has ? seconds : 0;
            if (has) Wave.ZoomFit();
        }

        /// <summary>
        /// A recording in progress. <paramref name="peaks"/> is the recorder's own growing buffer (only its first
        /// <paramref name="count"/> slices are real); the newest <paramref name="window"/> seconds stay in view.
        /// </summary>
        public void ShowLive(byte[] peaks, int count, double window = 15)
        {
            double seconds = count / (double)Smart_Tools.WPF.Models.AudioTrimmerPeaks.PerSecond;
            _empty.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
            Wave.AutoScale = false;       // true levels while recording (with a gain), not a picture stretched to the loudest part so far
            Wave.Gain = 4;
            Wave.Playhead = double.NaN;
            if (!ReferenceEquals(Wave.Peaks, peaks)) Wave.Peaks = peaks;
            Wave.ClipStart = 0;
            Wave.ClipEnd = seconds;
            Wave.Duration = Math.Max(seconds, window);      // the empty part to the right is the time still to come (dimmed by ClipEnd)
            Wave.SetView(Math.Max(0, seconds - window), window);
            Wave.RefreshPeaks();          // the buffer was filled in place: without this the first (empty) drawing is kept
        }

        public void SetPlayhead(double seconds) => Wave.Playhead = seconds;
    }
}
