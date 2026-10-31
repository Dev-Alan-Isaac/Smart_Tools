using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Clipboard = System.Windows.Clipboard;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Monitor Test &amp; Pixel Inspector - UI only. The pictures and numbers are in <see cref="MonitorTestManager"/>; this page picks the
    /// screen and the picture and opens <see cref="MonitorTestWindow"/>, which covers that screen. Spots marked in the test come back here as a list.
    /// </summary>
    public partial class MonitorTestView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double MeasureSeconds = 5;

        private enum StatusKind { Success, Error, Info }

        private readonly MonitorTestSettings _settings;
        private readonly DispatcherTimer _statusTimer;
        private readonly List<MonitorMark> _marks = new();
        private readonly bool _viewConstructed;

        private List<MonitorScreenInfo> _screens = new();
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _settingControls;
        private bool _testOpen;
        private CancellationTokenSource? _measureCts;

        public MonitorTestView()
        {
            InitializeComponent();

            _settings = MonitorTestManager.LoadSettings();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            _settingControls = true;
            SliderSlideshow.Value = _settings.SlideshowSeconds;
            ChkHints.IsChecked = _settings.ShowHints;
            _settingControls = false;
            if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);

            _viewConstructed = true;
            UpdateSlideshowText();
            BuildGroups();
            UpdateMarksUi();
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _measureCts?.Cancel();
            _statusTimer.Stop();
            SaveSettings();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            RefreshScreens();
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            try
            {
                _settings.SlideshowSeconds = (int)SliderSlideshow.Value;
                _settings.ShowHints = ChkHints.IsChecked == true;
                _settings.SidebarCollapsed = _optionsCollapsed;
                MonitorTestManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Monitor test settings: {ex.Message}");
            }
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

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            _settings.ShowHints = ChkHints.IsChecked == true;
        }

        private void SliderSlideshow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _settingControls) return;
            UpdateSlideshowText();
        }

        private void UpdateSlideshowText()
        {
            int s = (int)SliderSlideshow.Value;
            TxtSlideshow.Text = s == 0 ? Str("Str_Monitor_Off") : string.Format(CultureInfo.CurrentCulture, "{0} s", s);
        }

        // =================================================================
        //  Screens
        // =================================================================

        private void BtnRefreshScreens_Click(object sender, RoutedEventArgs e) => RefreshScreens();

        private void RefreshScreens()
        {
            string? keep = (CmbScreen.SelectedItem as MonitorScreenInfo)?.DeviceName;
            _screens = MonitorTestManager.ListScreens();

            _settingControls = true;
            CmbScreen.ItemsSource = _screens;
            CmbScreen.SelectedItem = _screens.FirstOrDefault(s => s.DeviceName == keep) ?? _screens.FirstOrDefault(s => s.IsPrimary) ?? _screens.FirstOrDefault();
            _settingControls = false;
            UpdateScreenInfo();
        }

        private void CmbScreen_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            UpdateScreenInfo();
        }

        private void UpdateScreenInfo()
        {
            if (CmbScreen.SelectedItem is not MonitorScreenInfo s)
            {
                TxtScreenInfo.Text = string.Empty;
                return;
            }
            string hz = s.RefreshHz > 0 ? $"{s.RefreshHz} Hz" : Str("Str_Monitor_HzUnknown");
            string bpp = s.BitsPerPixel > 0 ? $" · {s.BitsPerPixel}-bit" : string.Empty;
            TxtScreenInfo.Text = $"{s.Width} × {s.Height} · {hz}{bpp}" + (s.IsPrimary ? " · " + Str("Str_Monitor_Primary") : string.Empty);
        }

        // =================================================================
        //  The pictures
        // =================================================================

        private static string KeyOf(string id) => id;

        private string NameOf(string id) => Str("Str_Monitor_Pat_" + KeyOf(id));

        private string DescOf(string id) => Str("Str_Monitor_Desc_" + KeyOf(id));

        private static (string Key, PackIconKind Icon) GroupInfo(MonitorPatternGroup g) => g switch
        {
            MonitorPatternGroup.DeadPixels => ("DeadPixels", PackIconKind.TargetVariant),
            MonitorPatternGroup.Backlight => ("Backlight", PackIconKind.WhiteBalanceSunny),
            MonitorPatternGroup.Gradients => ("Gradients", PackIconKind.GradientHorizontal),
            MonitorPatternGroup.Calibration => ("Calibration", PackIconKind.Tune),
            MonitorPatternGroup.Retention => ("Retention", PackIconKind.ImageFilterHdr),
            _ => ("Motion", PackIconKind.MotionPlayOutline),
        };

        private void BuildGroups()
        {
            Groups.Children.Clear();
            foreach (var group in Enum.GetValues<MonitorPatternGroup>())
            {
                var (key, icon) = GroupInfo(group);
                var header = new ContentControl
                {
                    Content = Str("Str_Monitor_Group_" + key),
                    Style = (Style)FindResource("CardHeader"),
                    Tag = icon,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                var note = new TextBlock { Text = Str("Str_Monitor_GroupNote_" + key), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var wrap = new WrapPanel();

                foreach (var pattern in MonitorTestManager.Patterns.Where(p => p.Group == group))
                {
                    int index = MonitorTestManager.Patterns.ToList().IndexOf(pattern);
                    wrap.Children.Add(BuildTile(pattern, index));
                }

                Groups.Children.Add(header);
                Groups.Children.Add(note);
                Groups.Children.Add(wrap);
            }
        }

        private Border BuildTile(MonitorPattern pattern, int index)
        {
            var surface = new MonitorPatternSurface
            {
                PatternId = pattern.Id,
                ShowLabels = false,
                Time = 0.55,
                Width = 168,
                Height = 94,
                IsHitTestVisible = false,
                ClipToBounds = true,
            };
            var thumb = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = surface, BorderThickness = new Thickness(1) };
            thumb.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

            var caption = new TextBlock { Text = NameOf(pattern.Id), FontSize = 11.5, Margin = new Thickness(2, 5, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 168 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            var stack = new StackPanel();
            stack.Children.Add(thumb);
            stack.Children.Add(caption);

            var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0), IsHitTestVisible = false };
            if (pattern.Animated) badges.Children.Add(BadgeIcon(PackIconKind.Play));
            if (pattern.Flashing) badges.Children.Add(BadgeIcon(PackIconKind.AlertOutline));

            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(badges);

            var tile = new Border
            {
                Margin = new Thickness(0, 0, 10, 12),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Child = grid,
                Background = System.Windows.Media.Brushes.Transparent,
                Tag = index,
                ToolTip = DescOf(pattern.Id),
            };
            tile.MouseLeftButtonUp += Tile_Click;
            return tile;
        }

        private static Border BadgeIcon(PackIconKind kind) => new()
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, 0x14, 0x17, 0x1C)),
            Child = new PackIcon { Kind = kind, Width = 12, Height = 12, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: int index }) Launch(index);
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e) => Launch(0);

        // =================================================================
        //  The full-screen test
        // =================================================================

        private void Launch(int index)
        {
            if (_testOpen) return;
            if (CmbScreen.SelectedItem is not MonitorScreenInfo screen)
            {
                ShowStatus(Str("Str_Monitor_NoScreen"), StatusKind.Error);
                return;
            }

            var patterns = MonitorTestManager.Patterns;
            // The slideshow runs through every picture, the flashing one included.
            if (patterns[index].Flashing || SliderSlideshow.Value > 0)
            {
                var answer = MessageBox.Show(Window.GetWindow(this), Str("Str_Monitor_FlashWarning"), Str("Str_Monitor_FlashTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            _testOpen = true;
            int before = _marks.Count;
            try
            {
                var window = new MonitorTestWindow(
                    screen, patterns, index, (int)SliderSlideshow.Value, ChkHints.IsChecked == true,
                    NameOf, DescOf, key => Str("Str_Monitor_" + key),
                    mark => MonitorTestManager.AddMark(_marks, mark));
                window.ShowDialog();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShowStatus(string.Format(Str("Str_Monitor_OpenFailed"), ex.Message), StatusKind.Error);
            }
            finally
            {
                _testOpen = false;
                Window.GetWindow(this)?.Activate();
            }

            UpdateMarksUi();
            int added = _marks.Count - before;
            if (added > 0) ShowStatus(string.Format(Str("Str_Monitor_MarksAdded"), added), StatusKind.Info);
        }

        // =================================================================
        //  Suspect pixels
        // =================================================================

        private void UpdateMarksUi()
        {
            MarksList.ItemsSource = null;
            MarksList.ItemsSource = _marks.Select(m => string.Create(CultureInfo.InvariantCulture, $"x={m.X,5}  y={m.Y,5}   {NameOf(m.PatternId)}")).ToList();
            TxtMarksEmpty.Visibility = _marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnCopyMarks.IsEnabled = BtnClearMarks.IsEnabled = _marks.Count > 0;
        }

        private void BtnCopyMarks_Click(object sender, RoutedEventArgs e)
        {
            if (_marks.Count == 0) return;
            try
            {
                Clipboard.SetText(MonitorTestManager.FormatMarks(_marks, NameOf));
                ShowStatus(Str("Str_Monitor_MarksCopied"), StatusKind.Success);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ShowStatus(Str("Str_Monitor_ClipboardBusy"), StatusKind.Error);
            }
        }

        private void BtnClearMarks_Click(object sender, RoutedEventArgs e)
        {
            _marks.Clear();
            UpdateMarksUi();
        }

        // =================================================================
        //  Refresh rate
        // =================================================================

        private async void BtnMeasure_Click(object sender, RoutedEventArgs e)
        {
            if (_measureCts != null)
            {
                _measureCts.Cancel();   // the button doubles as Cancel while measuring
                return;
            }

            var cts = new CancellationTokenSource();
            _measureCts = cts;
            TxtMeasure.Text = Str("Str_Monitor_Cancel");
            BarMeasure.Value = 0;
            BarMeasure.Visibility = Visibility.Visible;
            TxtRefreshVerdict.Text = string.Empty;

            var progress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var started = Stopwatch.StartNew();
            progress.Tick += (_, _) => BarMeasure.Value = Math.Min(1, started.Elapsed.TotalSeconds / MeasureSeconds);
            progress.Start();

            try
            {
                var times = await Task.Run(() => MonitorTestManager.MeasureRefreshTimes(MeasureSeconds, cts.Token));
                if (cts.IsCancellationRequested) return;
                ShowRefreshResult(MonitorTestManager.AnalyzeFrames(times));
            }
            finally
            {
                progress.Stop();
                BarMeasure.Visibility = Visibility.Collapsed;
                TxtMeasure.Text = Str("Str_Monitor_Measure");
                _measureCts = null;
                cts.Dispose();
            }
        }

        private void ShowRefreshResult(FrameTimingResult? r)
        {
            if (r == null)
            {
                RefreshPills.Visibility = Visibility.Collapsed;
                TxtRefreshVerdict.Text = Str("Str_Monitor_MeasureFailed");
                return;
            }

            int reported = (CmbScreen.SelectedItem as MonitorScreenInfo)?.RefreshHz ?? 0;
            TxtReported.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_Monitor_Reported"), reported > 0 ? reported.ToString(CultureInfo.CurrentCulture) : "?");
            TxtMeasured.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_Monitor_Measured"), r.Hz);
            TxtJitter.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_Monitor_Jitter"), r.JitterMs);
            TxtMissed.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_Monitor_Missed"), r.Missed, r.MissedPercent);

            bool agree = MonitorTestManager.RatesAgree(reported, r.Hz);
            Tint(PillMeasured, TxtMeasured, agree || reported == 0 ? "StatusSuccessBg" : "StatusWarningBg", agree || reported == 0 ? "StatusSuccessFg" : "StatusWarningFg");
            Tint(PillMissed, TxtMissed, r.MissedPercent < 1 ? "StatusSuccessBg" : "StatusWarningBg", r.MissedPercent < 1 ? "StatusSuccessFg" : "StatusWarningFg");
            RefreshPills.Visibility = Visibility.Visible;
            UiMotion.Pop(RefreshPills);

            var lines = new List<string>
            {
                reported == 0 ? string.Empty : agree
                    ? string.Format(Str("Str_Monitor_VerdictMatch"), reported)
                    : string.Format(Str("Str_Monitor_VerdictMismatch"), reported, r.Hz),
                r.MissedPercent < 1 ? Str("Str_Monitor_VerdictSmooth") : Str("Str_Monitor_VerdictStutter"),
            };
            TxtRefreshVerdict.Text = string.Join("\n", lines.Where(l => l.Length > 0));
        }

        private static void Tint(Border pill, TextBlock text, string background, string foreground)
        {
            pill.SetResourceReference(Border.BackgroundProperty, background);
            text.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        }

        // =================================================================
        //  Status
        // =================================================================

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
            _statusTimer.Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 8 : 4);
            _statusTimer.Start();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;
    }
}
