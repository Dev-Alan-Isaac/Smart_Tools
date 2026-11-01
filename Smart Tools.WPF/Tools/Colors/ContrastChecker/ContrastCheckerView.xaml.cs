using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Contrast Checker - UI only. The ratio table and the "closest passing color" are in <see cref="ContrastCheckerManager"/>.</summary>
    public partial class ContrastCheckerView : UserControl
    {
        private readonly DispatcherTimer _saveTimer;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private RgbColor? _fixAa, _fixAaa;

        public ContrastCheckerView()
        {
            InitializeComponent();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _viewConstructed = true;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = JsonSettings.Load<ContrastCheckerSettings>(ContrastCheckerManager.SettingsName);
            _applying = true;
            TxtFg.Text = s.Foreground;
            TxtBg.Text = s.Background;
            _applying = false;
            Recalculate();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private void SaveSettings() =>
            JsonSettings.Save(ContrastCheckerManager.SettingsName, new ContrastCheckerSettings { Foreground = TxtFg.Text, Background = TxtBg.Text });

        private void Color_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            Recalculate();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SwatchFg_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => PickInto(TxtFg);
        private void SwatchBg_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => PickInto(TxtBg);

        /// <summary>Opens the Windows color window, starting at the color in the box, and writes the choice back as #RRGGBB.</summary>
        private static void PickInto(System.Windows.Controls.TextBox box)
        {
            var start = ColorManager.TryParseColor(box.Text, out var c) ? c : new RgbColor(0, 0, 0);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B),
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            box.Text = new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B).Hex;
        }

        private void Pair_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag || tag.Split('|') is not { Length: 2 } parts) return;
            TxtFg.Text = parts[0];
            TxtBg.Text = parts[1];
        }

        private void BtnSwap_Click(object sender, RoutedEventArgs e)
        {
            (TxtFg.Text, TxtBg.Text) = (TxtBg.Text, TxtFg.Text);
        }

        private static System.Windows.Media.Brush BrushOf(RgbColor c) => new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));

        private void Recalculate()
        {
            bool fgOk = ColorManager.TryParseColor(TxtFg.Text, out var fg);
            bool bgOk = ColorManager.TryParseColor(TxtBg.Text, out var bg);

            SwatchFg.Background = fgOk ? BrushOf(fg) : null;
            SwatchBg.Background = bgOk ? BrushOf(bg) : null;
            TxtColorError.Text = !fgOk ? "The text color isn't a color I can read." : !bgOk ? "The background color isn't a color I can read." : "";

            if (!fgOk || !bgOk)
            {
                CheckList.ItemsSource = null;
                TxtRatio.Text = "";
                TxtVerdict.Text = "";
                _fixAa = _fixAaa = null;
                ShowFix(SwatchAa, TxtAa, BtnUseAa, null, "");
                ShowFix(SwatchAaa, TxtAaa, BtnUseAaa, null, "");
                return;
            }

            Preview.Background = BrushOf(bg);
            foreach (var t in new[] { PreviewLarge, PreviewNormal, PreviewSmall, PreviewButtonText }) t.Foreground = BrushOf(fg);
            PreviewButton.BorderBrush = BrushOf(fg);
            HeroTile.Background = BrushOf(bg);
            HeroSample.Foreground = HeroSmall.Foreground = BrushOf(fg);
            HeroSmall.Text = fg.Hex;

            var (ratio, checks) = ContrastCheckerManager.Evaluate(fg, bg);
            TxtRatio.Text = ContrastCheckerManager.FormatRatio(ratio);
            TxtVerdict.Text = ratio >= 7 ? "Excellent" : ratio >= 4.5 ? "Good for all text" : ratio >= 3 ? "Large text only" : "Too low";
            CheckList.ItemsSource = checks;

            _fixAa = ContrastCheckerManager.SuggestForeground(fg, bg, 4.5);
            _fixAaa = ContrastCheckerManager.SuggestForeground(fg, bg, 7);
            ShowFix(SwatchAa, TxtAa, BtnUseAa, _fixAa, "AA (4.5 : 1)");
            ShowFix(SwatchAaa, TxtAaa, BtnUseAaa, _fixAaa, "AAA (7 : 1)");
        }

        private static void ShowFix(Border swatch, TextBlock text, Button use, RgbColor? fix, string label)
        {
            swatch.Background = fix is { } c ? BrushOf(c) : null;
            text.Text = label.Length == 0 ? "" : fix is { } f ? $"{label}: {f.Hex}" : $"{label}: not possible on this background";
            use.Visibility = fix != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnUseAa_Click(object sender, RoutedEventArgs e) { if (_fixAa is { } c) TxtFg.Text = c.Hex; }
        private void BtnUseAaa_Click(object sender, RoutedEventArgs e) { if (_fixAaa is { } c) TxtFg.Text = c.Hex; }

        public void CancelActiveRun()
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
