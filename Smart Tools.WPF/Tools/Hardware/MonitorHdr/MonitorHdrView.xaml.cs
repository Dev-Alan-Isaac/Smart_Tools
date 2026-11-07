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
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Monitor HDR &amp; Color Depth Checker - UI only. The readings and verdicts are in <see cref="MonitorHdrManager"/>; this page picks the
    /// screen, shows what Windows reports about it, and opens <see cref="MonitorHdrWindow"/> with the test pictures.
    /// Laid out like the Monitor Test: screen / slideshow / tips on the left, picture tiles then the readings on the right, a Start bar at the foot.
    /// </summary>
    public partial class MonitorHdrView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private enum StatusKind { Success, Error, Info }

        private readonly MonitorHdrSettings _settings;
        private readonly DispatcherTimer _statusTimer;
        private readonly List<MonitorHdrPatternSurface> _gamutSurfaces = new();
        private readonly bool _viewConstructed;

        private List<MonitorHdrInfo> _screens = new();
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _settingControls;
        private bool _testOpen;
        private int _readId;

        public MonitorHdrView()
        {
            InitializeComponent();

            _settings = MonitorHdrManager.LoadSettings();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
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
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _statusTimer.Stop();
            _readId++;      // a reading still in flight is ignored
            SaveSettings();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _ = RefreshAsync();
        }

        // =================================================================
        //  Settings and sidebar
        // =================================================================

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            try
            {
                _settings.SlideshowSeconds = (int)SliderSlideshow.Value;
                _settings.ShowHints = ChkHints.IsChecked == true;
                _settings.SidebarCollapsed = _optionsCollapsed;
                MonitorHdrManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Monitor HDR settings: {ex.Message}");
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
            TxtSlideshow.Text = s == 0 ? Str("Str_MonitorHdr_Off") : string.Format(CultureInfo.CurrentCulture, "{0} s", s);
        }

        // =================================================================
        //  The pictures
        // =================================================================

        private string NameOf(string id) => Str("Str_MonitorHdr_Pat_" + id);

        private string DescOf(string id) => Str("Str_MonitorHdr_PatDesc_" + id);

        private static (string Key, PackIconKind Icon) GroupInfo(MonitorHdrPatternGroup g) => g switch
        {
            MonitorHdrPatternGroup.Gradients => ("Gradients", PackIconKind.GradientHorizontal),
            MonitorHdrPatternGroup.Levels => ("Levels", PackIconKind.ContrastBox),
            _ => ("ColorBrightness", PackIconKind.Palette),
        };

        private void BuildGroups()
        {
            Groups.Children.Clear();
            _gamutSurfaces.Clear();
            foreach (var group in Enum.GetValues<MonitorHdrPatternGroup>())
            {
                var (key, icon) = GroupInfo(group);
                var header = new ContentControl
                {
                    Content = Str("Str_MonitorHdr_Group_" + key),
                    Style = (Style)FindResource("CardHeader"),
                    Tag = icon,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                var note = new TextBlock { Text = Str("Str_MonitorHdr_GroupNote_" + key), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var wrap = new WrapPanel();

                for (int i = 0; i < MonitorHdrManager.Patterns.Count; i++)
                {
                    var pattern = MonitorHdrManager.Patterns[i];
                    if (pattern.Group == group) wrap.Children.Add(BuildTile(pattern, i));
                }

                Groups.Children.Add(header);
                Groups.Children.Add(note);
                Groups.Children.Add(wrap);
            }
        }

        private Border BuildTile(MonitorHdrPattern pattern, int index)
        {
            var surface = new MonitorHdrPatternSurface
            {
                PatternId = pattern.Id,
                ShowLabels = false,
                WindowPercent = 25,
                Width = 168,
                Height = 94,
                IsHitTestVisible = false,
                ClipToBounds = true,
            };
            if (pattern.Id == "gamut") _gamutSurfaces.Add(surface);

            var thumb = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = surface, BorderThickness = new Thickness(1) };
            thumb.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

            var caption = new TextBlock { Text = NameOf(pattern.Id), FontSize = 11.5, Margin = new Thickness(2, 5, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 168 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            var stack = new StackPanel();
            stack.Children.Add(thumb);
            stack.Children.Add(caption);

            var tile = new Border
            {
                Margin = new Thickness(0, 0, 10, 12),
                Cursor = Cursors.Hand,
                Child = stack,
                Background = System.Windows.Media.Brushes.Transparent,
                Tag = index,
                ToolTip = DescOf(pattern.Id),
            };
            tile.MouseLeftButtonUp += Tile_Click;
            return tile;
        }

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: int index }) Launch(index);
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e) => Launch(0);

        private void Launch(int index)
        {
            if (_testOpen) return;
            if (CmbScreen.SelectedItem is not MonitorHdrInfo screen)
            {
                ShowStatus(Str("Str_MonitorHdr_NoScreen"), StatusKind.Error);
                return;
            }

            _testOpen = true;
            try
            {
                var window = new MonitorHdrWindow(
                    screen, MonitorHdrManager.Patterns, index, NameOf, DescOf,
                    () => Str("Str_MonitorHdr_Keys"), (int)SliderSlideshow.Value, ChkHints.IsChecked == true);
                window.ShowDialog();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShowStatus(string.Format(Str("Str_MonitorHdr_OpenFailed"), ex.Message), StatusKind.Error);
            }
            finally
            {
                _testOpen = false;
                Window.GetWindow(this)?.Activate();
            }
        }

        // =================================================================
        //  Reading the screens
        // =================================================================

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

        private async Task RefreshAsync()
        {
            int id = ++_readId;
            List<MonitorHdrInfo> screens;
            try
            {
                screens = await Task.Run(MonitorHdrManager.ReadScreens);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log.Info($"HDR read: {ex.Message}");
                screens = new List<MonitorHdrInfo>();
            }
            if (id != _readId) return;      // a newer reading, or the page was closed

            string? keep = (CmbScreen.SelectedItem as MonitorHdrInfo)?.DeviceName;
            _screens = screens;
            _settingControls = true;
            CmbScreen.ItemsSource = _screens;
            CmbScreen.SelectedItem = _screens.FirstOrDefault(s => s.DeviceName == keep) ?? _screens.FirstOrDefault(s => s.IsPrimary) ?? _screens.FirstOrDefault();
            _settingControls = false;
            Render();
        }

        private void CmbScreen_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            Render();
        }

        // =================================================================
        //  Showing a screen
        // =================================================================

        private void Render()
        {
            ReadingsGrid.Children.Clear();
            ReadingsGrid.RowDefinitions.Clear();
            VerdictPanel.Children.Clear();

            bool any = CmbScreen.SelectedItem is MonitorHdrInfo;
            TxtNoData.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            BtnCopy.IsEnabled = BtnSave.IsEnabled = any;
            if (CmbScreen.SelectedItem is not MonitorHdrInfo m)
            {
                TxtScreenInfo.Text = string.Empty;
                foreach (var s in _gamutSurfaces) s.Primaries = null;
                return;
            }

            var culture = CultureInfo.CurrentCulture;
            string unknown = Str("Str_MonitorHdr_Unknown");
            string Bits(int b) => b > 0 ? string.Format(culture, "{0}-bit", b) : unknown;

            string hz = m.RefreshHz > 0 ? $"{m.RefreshHz} Hz" : unknown;
            TxtScreenInfo.Text = $"{m.Width} × {m.Height} · {hz}" + (m.IsPrimary ? " · " + Str("Str_MonitorHdr_Primary") : string.Empty);
            foreach (var s in _gamutSurfaces) s.Primaries = m.Primaries;

            AddRow("Resolution", string.Format(culture, "{0} × {1} @ {2}", m.Width, m.Height, m.RefreshHz > 0 ? m.RefreshHz + " Hz" : "?"));
            if (m.FriendlyName.Length > 0) AddRow("Monitor", m.FriendlyName);
            AddRow("BitDepth", Bits(m.CurrentBits));
            AddRow("PanelMax", Bits(m.PanelMaxBits));
            AddRow("ColorFormat", m.ColorFormat.Length > 0 ? m.ColorFormat : unknown);
            AddRow("Hdr", m.HdrSupported ? Str(m.HdrEnabled ? "Str_MonitorHdr_On" : "Str_MonitorHdr_Off") : Str("Str_MonitorHdr_NotSupported"));
            if (m.HdrSupported && m.PeakNits > 0)
            {
                AddRow("PeakNits", string.Format(culture, "{0:0} nits", m.PeakNits));
                AddRow("FullFrameNits", string.Format(culture, "{0:0} nits", m.FullFrameNits));
                AddRow("MinNits", string.Format(culture, "{0:0.####} nits", m.MinNits));
            }
            if (m.HdrSupported && m.SdrWhiteNits > 0) AddRow("SdrWhite", string.Format(culture, "{0:0} nits", m.SdrWhiteNits));
            foreach (var g in MonitorHdrManager.GamutCoverage(m.Primaries))
                AddRawRow(g.Name, string.Format(culture, Str("Str_MonitorHdr_GamutValue"), g.CoveragePercent, g.SizePercent));

            foreach (var v in MonitorHdrManager.BuildVerdicts(m))
                VerdictPanel.Children.Add(VerdictRow(v));

            UiMotion.FadeIn(ReadingsGrid);
        }

        private void AddRow(string key, string value) => AddRawRow(Str("Str_MonitorHdr_" + key), value);

        private void AddRawRow(string label, string value)
        {
            int row = ReadingsGrid.RowDefinitions.Count;
            ReadingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var v = new TextBlock { Text = value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            ReadingsGrid.Children.Add(l);
            ReadingsGrid.Children.Add(v);
        }

        private UIElement VerdictRow(MonitorHdrVerdict verdict)
        {
            (PackIconKind icon, string bg, string fg) = verdict.Severity switch
            {
                MonitorHdrSeverity.Good => (PackIconKind.CheckCircleOutline, "StatusSuccessBg", "StatusSuccessFg"),
                MonitorHdrSeverity.Warning => (PackIconKind.AlertOutline, "StatusWarningBg", "StatusWarningFg"),
                _ => (PackIconKind.InformationOutline, "ContentBg", "TextSecondary"),
            };

            var text = new TextBlock { Text = MonitorHdrManager.FormatVerdict(verdict, Text), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var glyph = new PackIcon { Kind = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(ForegroundProperty, fg);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(glyph);
            grid.Children.Add(text);

            var border = new Border { Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10), Child = grid };
            border.SetResourceReference(Border.BackgroundProperty, bg);
            return border;
        }

        // =================================================================
        //  Export
        // =================================================================

        private string Text(string key) => Str("Str_MonitorHdr_" + key);

        private string BuildReport() => MonitorHdrManager.BuildReport(_screens, Text);

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildReport());
                ShowStatus(Str("Str_MonitorHdr_Copied"), StatusKind.Success);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ShowStatus(Str("Str_MonitorHdr_ClipboardBusy"), StatusKind.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Text|*.txt",
                FileName = "monitor-hdr-report.txt",
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                AtomicFile.WriteAllText(dialog.FileName, BuildReport());
                ShowStatus(Str("Str_MonitorHdr_Saved"), StatusKind.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(ex.Message, StatusKind.Error);
            }
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
