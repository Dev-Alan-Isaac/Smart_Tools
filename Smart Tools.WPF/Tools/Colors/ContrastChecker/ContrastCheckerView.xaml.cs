using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
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
        private List<string> _savedPairs = new();
        private const int MaxSavedPairs = 24;

        // Ready-made pairs by use: text, background, optional name key. Colors are the pairs' own data.
        private static readonly (string Title, string Fallback, (string Fg, string Bg, string? NameKey)[] Pairs)[] PairLibrary =
        {
            ("Str_ContrastChecker_GroupLight", "ON LIGHT", new[]
            {
                ("#111827", "#FFFFFF", (string?)"Str_ContrastChecker_NearBlackOnWhite"),
                ("#000000", "#FFFFFF", null),
                ("#4B5563", "#F3F4F6", "Str_ContrastChecker_GreyOnLightGrey"),
                ("#6B7280", "#FFFFFF", null),
            }),
            ("Str_ContrastChecker_GroupDark", "ON DARK", new[]
            {
                ("#FFFFFF", "#111827", (string?)"Str_ContrastChecker_WhiteOnNearBlack"),
                ("#E5E7EB", "#1F2937", null),
                ("#9CA3AF", "#111827", null),
                ("#F9FAFB", "#000000", null),
            }),
            ("Str_ContrastChecker_GroupBrand", "BUTTONS AND BRAND COLORS", new[]
            {
                ("#FFFFFF", "#2563EB", (string?)"Str_ContrastChecker_WhiteOnBlue"),
                ("#FFFFFF", "#DC2626", "Str_ContrastChecker_WhiteOnRed"),
                ("#111827", "#FACC15", "Str_ContrastChecker_DarkOnYellow"),
                ("#FFFFFF", "#7C3AED", null),
                ("#FFFFFF", "#0F766E", null),
                ("#FFFFFF", "#1D4ED8", null),
            }),
            ("Str_ContrastChecker_GroupMessages", "MESSAGES", new[]
            {
                ("#14532D", "#DCFCE7", (string?)null),
                ("#7F1D1D", "#FEE2E2", null),
                ("#78350F", "#FDE68A", "Str_ContrastChecker_BrownOnCream"),
                ("#1E3A8A", "#DBEAFE", null),
            }),
            ("Str_ContrastChecker_GroupFails", "LOOK FINE, DON'T PASS", new[]
            {
                ("#FFFFFF", "#16A34A", (string?)"Str_ContrastChecker_WhiteOnGreen"),
                ("#9CA3AF", "#FFFFFF", null),
                ("#FFFFFF", "#F59E0B", null),
                ("#3B82F6", "#1E3A8A", null),
            }),
        };

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
            _savedPairs = (s.SavedPairs ?? new List<string>()).Where(p => p.Split('|') is { Length: 2 }).Take(MaxSavedPairs).ToList();
            _applying = false;
            BuildPairs();
            Recalculate();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private void SaveSettings() =>
            JsonSettings.Save(ContrastCheckerManager.SettingsName, new ContrastCheckerSettings { Foreground = TxtFg.Text, Background = TxtBg.Text, SavedPairs = _savedPairs });

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

        // ---- quick pairs ----

        private static string GradeText(double ratio) =>
            ratio >= 7 ? "AAA" : ratio >= 4.5 ? "AA" : ratio >= 3 ? Loc.T("Str_ContrastChecker_GradeLarge", "Large text only") : Loc.T("Str_Palette_Fail", "Fail");

        private Button? MakeChip(string fg, string bg, string? nameKey)
        {
            if (!ColorManager.TryParseColor(fg, out var f) || !ColorManager.TryParseColor(bg, out var b)) return null;
            double ratio = ColorManager.Contrast(f, b).Ratio;
            var chip = new Button
            {
                Style = (Style)FindResource("PairChip"),
                Background = BrushOf(b),
                Foreground = BrushOf(f),
                Tag = $"{fg}|{bg}",
            };
            string name = nameKey != null ? (TryFindResource(nameKey) as string ?? "") + "\n" : "";
            chip.ToolTip = $"{name}{fg} / {bg}\n{ContrastCheckerManager.FormatRatio(ratio)} · {GradeText(ratio)}";
            chip.Click += Pair_Click;
            return chip;
        }

        private void BuildPairs()
        {
            PairGroups.Children.Clear();
            foreach (var (title, fallback, pairs) in PairLibrary)
            {
                PairGroups.Children.Add(new TextBlock { Style = (Style)FindResource("PairGroupLabel"), Text = Loc.T(title, fallback) });
                var wrap = new WrapPanel();
                foreach (var (fg, bg, key) in pairs)
                    if (MakeChip(fg, bg, key) is { } chip) wrap.Children.Add(chip);
                PairGroups.Children.Add(wrap);
            }
            BuildCustomPairs();
        }

        private void BuildCustomPairs()
        {
            CustomPairPanel.Children.Clear();
            foreach (string pair in _savedPairs)
            {
                var parts = pair.Split('|');
                if (MakeChip(parts[0], parts[1], null) is not { } chip) continue;

                var remove = new System.Windows.Controls.MenuItem { Header = Loc.T("Str_ContrastChecker_RemovePair", "Remove this pair"), Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.DeleteOutline } };
                string captured = pair;
                remove.Click += (_, _) =>
                {
                    _savedPairs.Remove(captured);
                    SaveSettings();
                    BuildCustomPairs();
                };
                chip.ContextMenu = new System.Windows.Controls.ContextMenu { Items = { remove } };
                CustomPairPanel.Children.Add(chip);
            }
            TxtCustomHint.Visibility = _savedPairs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnSavePair_Click(object sender, RoutedEventArgs e)
        {
            if (!ColorManager.TryParseColor(TxtFg.Text, out var fg) || !ColorManager.TryParseColor(TxtBg.Text, out var bg)) return;
            string key = $"{fg.Hex}|{bg.Hex}";
            if (_savedPairs.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
            if (_savedPairs.Count >= MaxSavedPairs) _savedPairs.RemoveAt(0);
            _savedPairs.Add(key);
            SaveSettings();
            BuildCustomPairs();
            if (CustomPairPanel.Children.Count > 0 && CustomPairPanel.Children[^1] is FrameworkElement last) UiMotion.Pop(last);
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
            BtnSavePair.IsEnabled = fgOk && bgOk;
            TxtColorError.Text = !fgOk ? Loc.T("Str_ContrastChecker_MsgBadText", "The text color isn't a color I can read.") : !bgOk ? Loc.T("Str_ContrastChecker_MsgBadBack", "The background color isn't a color I can read.") : "";

            if (!fgOk || !bgOk)
            {
                CheckList.ItemsSource = null;
                ScaleMarkerHost.Visibility = Visibility.Collapsed;
                TxtExplain.Text = "";
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
            TxtVerdict.Text = ratio >= 7 ? Loc.T("Str_ContrastChecker_MsgExcellent", "Excellent") : ratio >= 4.5 ? Loc.T("Str_ContrastChecker_MsgGood", "Good for all text") : ratio >= 3 ? Loc.T("Str_ContrastChecker_MsgLargeOnly", "Large text only") : Loc.T("Str_ContrastChecker_MsgTooLow", "Too low");
            CheckList.ItemsSource = checks;

            double place = Math.Clamp(Math.Log(Math.Max(ratio, 1)) / Math.Log(21), 0.01, 0.99);
            MarkLeft.Width = new GridLength(place, GridUnitType.Star);
            MarkRight.Width = new GridLength(1 - place, GridUnitType.Star);
            ScaleMarkerHost.Visibility = Visibility.Visible;
            TxtExplain.Text = Loc.F("Str_ContrastChecker_MsgExplain",
                "{0} means the lighter color is about {1} times as bright as the darker one. Body text needs at least 4.5 : 1; large text and icons at least 3 : 1.",
                ContrastCheckerManager.FormatRatio(ratio), ratio.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));

            _fixAa = ContrastCheckerManager.SuggestForeground(fg, bg, 4.5);
            _fixAaa = ContrastCheckerManager.SuggestForeground(fg, bg, 7);
            ShowFix(SwatchAa, TxtAa, BtnUseAa, _fixAa, "AA (4.5 : 1)");
            ShowFix(SwatchAaa, TxtAaa, BtnUseAaa, _fixAaa, "AAA (7 : 1)");
        }

        private static void ShowFix(Border swatch, TextBlock text, Button use, RgbColor? fix, string label)
        {
            swatch.Background = fix is { } c ? BrushOf(c) : null;
            text.Text = label.Length == 0 ? "" : fix is { } f ? $"{label}: {f.Hex}" : Loc.F("Str_ContrastChecker_MsgNotPossible", "{0}: not possible on this background", label);
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
