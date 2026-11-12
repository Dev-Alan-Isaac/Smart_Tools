using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Screen Color Picker - UI only. Reading pixels, formats and settings are in
    /// <see cref="ScreenColorPickerManager"/>; the full-screen eyedropper is
    /// <see cref="ScreenColorPickerOverlay"/>. Picked colours also go into the Color Picker's
    /// Recent list, and the star shares its Favorites.
    /// </summary>
    public partial class ScreenColorPickerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private readonly ScreenColorPickerSettings _settings;
        private readonly ColorHistoryStore _history = new();
        private readonly List<ScreenPick> _session = new();
        private readonly List<(string Label, Func<RgbColor, string> Text, TextBlock Value)> _rows = new();
        private readonly DispatcherTimer _statusTimer;
        private readonly bool _viewConstructed;

        private ScreenPick? _current;
        private bool _optionsCollapsed;
        private bool _picking;
        private bool _closed;
        private bool _settingControls;

        private enum StatusKind { Success, Error, Info }

        public ScreenColorPickerView()
        {
            InitializeComponent();

            _settings = ScreenColorPickerManager.LoadSettings();
            _history.Load();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            ApplySettingsToControls();
            BuildRows();
            if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);

            _viewConstructed = true;
            UpdateFormatExample();
            UpdateSessionStrip();
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _statusTimer.Stop();
            SaveSettings();
        }

        // =================================================================
        //  Settings <-> controls
        // =================================================================

        private void ApplySettingsToControls()
        {
            _settingControls = true;
            RadioSamplePixel.IsChecked = _settings.Sample == ScreenColorSample.Pixel;
            RadioSample3.IsChecked = _settings.Sample == ScreenColorSample.Average3;
            RadioSample5.IsChecked = _settings.Sample == ScreenColorSample.Average5;
            RadioFormatHex.IsChecked = _settings.CopyFormat == ScreenColorFormat.Hex;
            RadioFormatRgb.IsChecked = _settings.CopyFormat == ScreenColorFormat.Rgb;
            RadioFormatHsl.IsChecked = _settings.CopyFormat == ScreenColorFormat.Hsl;
            RadioFormatCss.IsChecked = _settings.CopyFormat == ScreenColorFormat.CssRgb;
            ChkAutoCopy.IsChecked = _settings.AutoCopy;
            ChkHideWhilePicking.IsChecked = _settings.HideWhilePicking;
            _settingControls = false;
        }

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            try
            {
                _settings.SidebarCollapsed = _optionsCollapsed;
                ScreenColorPickerManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Screen colour picker settings: {ex.Message}");
            }
        }

        private void Sample_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls || sender is not RadioButton { Tag: string tag }) return;
            if (Enum.TryParse(tag, out ScreenColorSample sample)) _settings.Sample = sample;
        }

        private void Format_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls || sender is not RadioButton { Tag: string tag }) return;
            if (Enum.TryParse(tag, out ScreenColorFormat format)) _settings.CopyFormat = format;
            UpdateFormatExample();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            _settings.AutoCopy = ChkAutoCopy.IsChecked == true;
            _settings.HideWhilePicking = ChkHideWhilePicking.IsChecked == true;
        }

        private void UpdateFormatExample()
        {
            var c = _current?.Color ?? new RgbColor(0x3D, 0x6F, 0xE0);
            TxtFormatExample.Text = ScreenColorPickerManager.Format(c, _settings.CopyFormat);
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!_optionsCollapsed);

        private void SetSidebarCollapsed(bool collapsed)
        {
            _optionsCollapsed = collapsed;
            var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Picking
        // =================================================================

        private async void BtnPick_Click(object sender, RoutedEventArgs e) => await PickAsync();

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                _ = PickAsync();
            }
        }

        private async Task PickAsync()
        {
            if (_picking) return;
            _picking = true;
            BtnPick.IsEnabled = false;

            var main = Window.GetWindow(this);
            bool hide = _settings.HideWhilePicking && main != null;
            int picks = 0;
            try
            {
                if (hide)
                {
                    main!.Hide();
                    await Task.Delay(250);   // let Windows repaint what was behind the app
                }

                var image = await Task.Run(ScreenColorPickerManager.CaptureVirtualScreen);
                var overlay = new ScreenColorPickerOverlay(image, VirtualScreenInDips(), VirtualScreenInPixels(), _settings.Sample, OnPicked);
                overlay.ShowDialog();
                picks = overlay.PicksMade;
            }
            catch (Exception ex)
            {
                // Typically the secure desktop (UAC prompt, lock screen): nothing can be captured then.
                ShowStatus(string.Format(Str("Str_ScreenColor_CaptureFailed"), ex.Message), StatusKind.Error);
            }
            finally
            {
                if (hide && main != null)
                {
                    main.Show();
                    main.Activate();
                }
                _picking = false;
                BtnPick.IsEnabled = true;
                Focus();
            }

            if (_closed || picks == 0 || _current == null) return;
            string key = _settings.AutoCopy ? "Str_ScreenColor_PickedCopied" : "Str_ScreenColor_PickedOnly";
            ShowStatus(string.Format(Str(key), ScreenColorPickerManager.Format(_current.Color, _settings.CopyFormat)), StatusKind.Success);
        }

        private static System.Drawing.Rectangle VirtualPixels() => System.Windows.Forms.SystemInformation.VirtualScreen;

        private Rect VirtualScreenInPixels()
        {
            var px = VirtualPixels();
            return new Rect(px.Left, px.Top, px.Width, px.Height);
        }

        /// <summary>The whole virtual screen (all monitors) in WPF units, for the overlay window.</summary>
        private Rect VirtualScreenInDips()
        {
            var px = VirtualPixels();
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Rect(px.Left / dpi.DpiScaleX, px.Top / dpi.DpiScaleY, px.Width / dpi.DpiScaleX, px.Height / dpi.DpiScaleY);
        }

        /// <summary>Called by the overlay for every click (Shift+click can make several).</summary>
        private void OnPicked(ScreenPick pick)
        {
            ScreenColorPickerManager.AddPick(_session, pick);
            _history.AddRecent(pick.Color);
            ShowColor(pick, animate: true);
            UpdateSessionStrip();
            if (_settings.AutoCopy) CopyText(ScreenColorPickerManager.Format(pick.Color, _settings.CopyFormat), quiet: true);
        }

        // =================================================================
        //  Showing the colour
        // =================================================================

        private void BuildRows()
        {
            void Add(string label, Func<RgbColor, string> text) => _rows.Add((label, text, new TextBlock()));

            Add("HEX", c => c.Hex);
            Add("RGB", ColorManager.ToRgbTriplet);
            Add("HSL", ColorManager.ToHslTriplet);
            Add("HSV", c =>
            {
                var v = ColorManager.RgbToHsv(c);
                return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(v.H)}, {Math.Round(v.S * 100)}%, {Math.Round(v.V * 100)}%");
            });
            Add("CSS rgb()", ColorManager.ToCssRgb);
            Add("CSS hsl()", ColorManager.ToCssHsl);

            foreach (var row in _rows)
            {
                var label = new TextBlock
                {
                    Text = row.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                row.Value.VerticalAlignment = VerticalAlignment.Center;
                row.Value.FontFamily = new FontFamily("Consolas");
                row.Value.FontSize = 17;
                row.Value.FontWeight = FontWeights.SemiBold;
                row.Value.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

                var copy = new Button { Style = (Style)FindResource("NeutralIconButtonStyle"), Width = 28, Height = 28, Padding = new Thickness(0), Tag = row.Text };
                copy.Content = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 14, Height = 14 };
                copy.ToolTip = Str("Str_Common_CopyToClipboardTooltip");
                copy.Click += RowCopy_Click;

                // a tile: the name and copy button on top, the value big underneath
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                row.Value.VerticalAlignment = VerticalAlignment.Center;
                row.Value.TextWrapping = TextWrapping.Wrap;
                row.Value.TextTrimming = TextTrimming.None;
                Grid.SetColumnSpan(row.Value, 2);
                Grid.SetRow(row.Value, 1);
                Grid.SetColumn(copy, 1);
                grid.Children.Add(label);
                grid.Children.Add(copy);
                grid.Children.Add(row.Value);

                var border = new Border { Padding = new Thickness(14, 8, 8, 10), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 8), Child = grid };
                border.SetResourceReference(Border.BackgroundProperty, "RowBg");
                ValueRows.Children.Add(border);
            }
        }

        private void RowCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || sender is not Button { Tag: Func<RgbColor, string> text }) return;
            CopyText(text(_current.Color), quiet: false);
        }

        private void ShowColor(ScreenPick pick, bool animate)
        {
            _current = pick;
            var c = pick.Color;
            var wpf = Color.FromRgb(c.R, c.G, c.B);
            var best = ColorManager.BestTextColor(c);
            var bestBrush = new SolidColorBrush(Color.FromRgb(best.R, best.G, best.B));

            bool first = EmptyState.Visibility == Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;

            SwatchBig.Background = new SolidColorBrush(wpf);
            TxtSwatchHex.Text = c.Hex;
            TxtSwatchPos.Text = string.Format(Str("Str_ScreenColor_AtPosition"), ScreenColorPickerManager.Position(pick));
            TxtSwatchHex.Foreground = bestBrush;
            TxtSwatchPos.Foreground = bestBrush;

            foreach (var row in _rows) row.Value.Text = row.Text(c);

            ChipWhite.Background = ChipBlack.Background = SwatchBig.Background;
            var onWhite = ColorManager.Contrast(c, ColorManager.White);
            var onBlack = ColorManager.Contrast(c, ColorManager.Black);
            TxtWhiteRatio.Text = string.Format(CultureInfo.CurrentCulture, "{0:0.0}:1 · {1}", onWhite.Ratio, onWhite.Grade);
            TxtBlackRatio.Text = string.Format(CultureInfo.CurrentCulture, "{0:0.0}:1 · {1}", onBlack.Ratio, onBlack.Grade);

            BtnFavorite.IsEnabled = true;
            UpdateFavoriteIcon();
            UpdateFormatExample();
            if (animate) UiMotion.Pop(SwatchBig);
            if (first) UiMotion.FadeIn(ResultPanel);
        }

        private void UpdateFavoriteIcon()
        {
            bool fav = _current != null && _history.IsFavorite(_current.Color);
            IconFavorite.Kind = fav ? PackIconKind.Star : PackIconKind.StarOutline;
            // Yellow when it is a favorite, the same as the Color Picker
            if (fav) IconFavorite.SetResourceReference(ForegroundProperty, "StatusWarningFg");
            else IconFavorite.ClearValue(ForegroundProperty);
        }

        private void BtnFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            bool nowFavorite = _history.ToggleFavorite(_current.Color);
            UpdateFavoriteIcon();
            UiMotion.Pop(IconFavorite);
            ShowStatus(Str(nowFavorite ? "Str_ScreenColor_FavoriteAdded" : "Str_ScreenColor_FavoriteRemoved"), StatusKind.Info);
        }

        // =================================================================
        //  Session strip
        // =================================================================

        private void UpdateSessionStrip()
        {
            SessionStrip.Children.Clear();
            foreach (var pick in _session)
            {
                var c = pick.Color;
                bool selected = _current != null && _current.Color == c;
                var chip = new Border
                {
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                    BorderThickness = new Thickness(selected ? 2.5 : 1),
                    Cursor = Cursors.Hand,
                    Tag = pick,
                    ToolTip = $"{c.Hex}  ({ScreenColorPickerManager.Position(pick)})",
                };
                chip.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentColor" : "CardBorder");
                chip.MouseLeftButtonUp += SessionChip_Click;
                SessionStrip.Children.Add(chip);
            }

            bool any = _session.Count > 0;
            SessionPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            BtnClearSession.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SessionChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: ScreenPick pick }) return;
            ShowColor(pick, animate: true);
            UpdateSessionStrip();
        }

        private void BtnClearSession_Click(object sender, RoutedEventArgs e)
        {
            _session.Clear();
            UpdateSessionStrip();
        }

        // =================================================================
        //  Copy and status
        // =================================================================

        private void CopyText(string text, bool quiet)
        {
            try
            {
                Clipboard.SetText(text);
                if (!quiet) ShowStatus(string.Format(Str("Str_ScreenColor_Copied"), text), StatusKind.Success);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (!quiet) ShowStatus(Str("Str_ScreenColor_ClipboardBusy"), StatusKind.Error);
            }
        }

        private void ShowStatus(string message, StatusKind kind)
        {
            TxtStatus.Text = message;
            BorderStatus.ToolTip = message;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, kind switch
            {
                StatusKind.Success => "StatusSuccessFg",
                StatusKind.Error => "StatusErrorFg",
                _ => "TextSecondary",
            });
            IconStatus.Kind = kind switch
            {
                StatusKind.Success => PackIconKind.CheckCircleOutline,
                StatusKind.Error => PackIconKind.AlertCircleOutline,
                _ => PackIconKind.InformationOutline,
            };

            if (BorderStatus.Visibility != Visibility.Visible) UiMotion.FadeVisibility(BorderStatus, true);
            else UiMotion.Pop(BorderStatus);

            _statusTimer.Stop();
            _statusTimer.Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 9 : 5);
            _statusTimer.Start();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;
    }
}
