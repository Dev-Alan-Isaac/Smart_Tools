using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Batch Watermarker - UI only. Where the mark goes, how it is drawn, the three engines (pictures, PDFs, videos), the presets and the
    /// settings are in <see cref="WatermarkManager"/>. The preview draws the very same mark (<see cref="WatermarkManager.RenderStamp"/>) over the
    /// first page / frame / picture of the chosen file, so what is shown is what is saved. Files, folders and logo paths are never saved;
    /// a logo is kept as an app-owned copy so a preset keeps working.
    /// </summary>
    public partial class WatermarkView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int PreviewSide = 1100;
        private const string CustomPresetTag = "*custom*";

        private readonly BulkObservableCollection<WatermarkItem> _items = new();   // 100,000 files = one change, not 100,000
        private readonly ObservableCollection<WatermarkGroup> _groups = new();
        private readonly DispatcherTimer _previewTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly SemaphoreSlim _infoGate = new(3);
        private readonly bool _viewConstructed;

        private List<WatermarkPreset> _userPresets = new();
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _busy;
        private int _previewRun;
        private string _colorHex = "#FFFFFF";
        private string? _logoFile;
        private BitmapSource? _logo;
        private string? _lastOutput;
        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _scanCts;      // reading folders: cancelled by Clear and by closing the page
        private CancellationTokenSource? _infoCts;      // reading each file's size / pages / length
        private CancellationTokenSource? _previewCts;   // the preview in progress (ffmpeg frame, PDF page)
        private bool _stopping;

        // The last file shown, so changing a setting redraws the mark without reading the file again.
        private string? _baseKey;
        private BitmapSource? _baseBitmap;
        private string? _baseProblem;
        private string? _previewProblem;

        private enum StatusKind { Error, Success, Warning }

        public WatermarkView()
        {
            InitializeComponent();
            FileList.ItemsSource = _items;
            InputCard.ItemsSource = _groups;
            InputCard.FileFilterProvider = () => Str("Str_Watermark_FilterFiles", "Pictures, videos and PDFs") + "|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp;*.gif;*.pdf;*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.wmv;*.webm;*.flv;*.ts;*.mts;*.m2ts|" + Str("Str_Watermark_FilterAll", "All files") + "|*.*";
            _items.CollectionChanged += (_, _) => UpdateCounts();
            _groups.CollectionChanged += (_, _) => InputCard.SetEmpty(_groups.Count == 0);

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RunPreview(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); UiMotion.FadeVisibility(DonePill, false); };

            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            WatermarkManager.SweepWorkFolder();

            _applyingSettings = true;
            try
            {
                foreach (string font in WatermarkManager.FontNames()) CmbFont.Items.Add(font);
            }
            finally
            {
                _applyingSettings = false;
            }
            _userPresets = WatermarkManager.LoadPresets();
            var settings = WatermarkManager.LoadSettings();
            BuildPresetCombo(settings.PresetName);
            ApplyView(settings);
            UpdateCounts();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private void ApplyView(WatermarkViewSettings s)
        {
            _applyingSettings = true;
            try
            {
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                if (s.OptionsCollapsed != _optionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
                _logoFile = s.Mark.LogoFile;
            }
            finally
            {
                _applyingSettings = false;
            }
            ApplyMark(s.Mark, keepPresetSelection: true);
        }

        /// <summary>Puts a setup on the controls (without starting a save or a redraw for every control).</summary>
        private void ApplyMark(WatermarkSettings mark, bool keepPresetSelection)
        {
            var m = WatermarkManager.Normalize(mark);
            _applyingSettings = true;
            try
            {
                (m.Kind == WatermarkKind.Image ? RadioKindImage : RadioKindText).IsChecked = true;
                TxtMarkText.Text = m.Text;
                SelectFont(m.FontFamily);
                ChkBold.IsChecked = m.Bold;
                ChkItalic.IsChecked = m.Italic;
                ChkOutline.IsChecked = m.Outline;
                SetColor(m.Color);
                SldSize.Value = m.SizePercent;
                SldOpacity.Value = m.OpacityPercent;
                SldRotation.Value = m.Rotation;
                SldMargin.Value = m.MarginPercent;
                SldGap.Value = m.TileGapPercent;
                PositionRadio(m.Placement).IsChecked = true;

                // A preset made with a logo brings its own copy; one without keeps the logo already chosen.
                SetLogo(m.LogoFile ?? _logoFile);
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdatePanels();
            UpdateLabels();
            if (!keepPresetSelection) SelectPreset(null);
            SchedulePreview();
            ScheduleSave();
        }

        private WatermarkSettings ReadMark() => WatermarkManager.Normalize(new WatermarkSettings
        {
            Kind = RadioKindImage.IsChecked == true ? WatermarkKind.Image : WatermarkKind.Text,
            Text = TxtMarkText.Text,
            FontFamily = CmbFont.SelectedItem as string ?? "Segoe UI",
            Bold = ChkBold.IsChecked == true,
            Italic = ChkItalic.IsChecked == true,
            Outline = ChkOutline.IsChecked == true,
            Color = _colorHex,
            SizePercent = SldSize.Value,
            OpacityPercent = SldOpacity.Value,
            Rotation = SldRotation.Value,
            Placement = SelectedPlacement(),
            MarginPercent = SldMargin.Value,
            TileGapPercent = SldGap.Value,
            LogoFile = _logoFile,
        });

        private void SaveSettings() => WatermarkManager.SaveSettings(new WatermarkViewSettings
        {
            Mark = ReadMark(),
            PresetName = (CmbPreset.SelectedItem as ComboBoxItem)?.Tag as string is { } tag && tag != CustomPresetTag ? tag : null,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            OptionsCollapsed = _optionsCollapsed,
        });

        private void ScheduleSave()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private string Str(string key, string fallback = "") => TryFindResource(key) as string ?? fallback;

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            ScheduleSave();
        }

        // =================================================================
        //  The mark's controls
        // =================================================================

        private void SelectFont(string family)
        {
            foreach (string f in CmbFont.Items.OfType<string>())
                if (string.Equals(f, family, StringComparison.OrdinalIgnoreCase)) { CmbFont.SelectedItem = f; return; }
            CmbFont.SelectedItem = CmbFont.Items.OfType<string>().FirstOrDefault(f => f == "Segoe UI") ?? (CmbFont.Items.Count > 0 ? CmbFont.Items[0] : null);
        }

        private RadioButton PositionRadio(WatermarkPlacement p) => p switch
        {
            WatermarkPlacement.TopLeft => RadioPos0, WatermarkPlacement.TopCenter => RadioPos1, WatermarkPlacement.TopRight => RadioPos2,
            WatermarkPlacement.MiddleLeft => RadioPos3, WatermarkPlacement.Center => RadioPos4, WatermarkPlacement.MiddleRight => RadioPos5,
            WatermarkPlacement.BottomLeft => RadioPos6, WatermarkPlacement.BottomCenter => RadioPos7, WatermarkPlacement.Tiled => RadioPosTiled,
            _ => RadioPos8,
        };

        private WatermarkPlacement SelectedPlacement() =>
            new[] { RadioPos0, RadioPos1, RadioPos2, RadioPos3, RadioPos4, RadioPos5, RadioPos6, RadioPos7, RadioPos8, RadioPosTiled }
                .FirstOrDefault(r => r.IsChecked == true)?.Tag is string tag && Enum.TryParse<WatermarkPlacement>(tag, out var p) ? p : WatermarkPlacement.BottomRight;

        private void SetColor(string hex)
        {
            if (!WatermarkManager.TryParseColor(hex, out byte r, out byte g, out byte b)) return;
            _colorHex = $"#{r:X2}{g:X2}{b:X2}";
            ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            if (!TxtColor.IsKeyboardFocusWithin) TxtColor.Text = _colorHex;
            TxtColorError.Visibility = Visibility.Collapsed;
            ColorBar.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
        }

        private void TxtColor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _applyingSettings) return;
            if (WatermarkManager.TryParseColor(TxtColor.Text, out _, out _, out _))
            {
                SetColor(TxtColor.Text);
                SettingChanged();
            }
            else
            {
                TxtColorError.Visibility = Visibility.Visible;
                ColorBar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            }
        }

        private void TxtColor_LostFocus(object sender, RoutedEventArgs e)
        {
            TxtColor.Text = _colorHex;                    // put back a readable form (or the last good colour)
            TxtColorError.Visibility = Visibility.Collapsed;
            ColorBar.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
        }

        private void ColorChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string hex) return;
            SetColor(hex);
            TxtColor.Text = _colorHex;
            SettingChanged();
        }

        private void Kind_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            // A logo wants to be wider than text is tall: move the size off the other kind's default, unless the user already set their own.
            bool image = RadioKindImage.IsChecked == true;
            _applyingSettings = true;
            try
            {
                if (image && SldSize.Value < 10) SldSize.Value = 14;
                else if (!image && SldSize.Value >= 12 && SldSize.Value <= 16) SldSize.Value = 6;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateLabels();
            UpdatePanels();
            SettingChanged();
        }

        private void Position_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdatePanels();
            SettingChanged();
        }

        private void Setting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed && !_applyingSettings) SettingChanged();
        }

        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed && !_applyingSettings) SettingChanged();
        }

        private void Setting_Click(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) SettingChanged();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            UpdateLabels();
            if (!_applyingSettings) SettingChanged();
        }

        private void BtnRotationReset_Click(object sender, RoutedEventArgs e) => SldRotation.Value = 0;

        /// <summary>Any change of the mark: the preset becomes "Custom", the preview redraws and the setup is remembered.</summary>
        private void SettingChanged()
        {
            if (_applyingSettings) return;
            SelectPreset(null);
            SchedulePreview();
            ScheduleSave();
        }

        private void UpdatePanels()
        {
            bool image = RadioKindImage.IsChecked == true, tiled = SelectedPlacement() == WatermarkPlacement.Tiled;
            TextPanel.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
            ImagePanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            MarginPanel.Visibility = tiled ? Visibility.Collapsed : Visibility.Visible;
            GapPanel.Visibility = tiled ? Visibility.Visible : Visibility.Collapsed;
            TxtSizeHint.Text = Str(image ? "Str_Watermark_SizeHintImage" : "Str_Watermark_SizeHintText");
        }

        private void UpdateLabels()
        {
            TxtSizeValue.Text = $"{SldSize.Value:0}%";
            TxtOpacityValue.Text = $"{SldOpacity.Value:0}%";
            TxtRotationValue.Text = $"{SldRotation.Value:0}°";
            TxtMarginValue.Text = $"{SldMargin.Value:0}%";
            TxtGapValue.Text = $"{SldGap.Value:0}%";
        }

        // ---- the logo

        private void SetLogo(string? logoFile)
        {
            _logoFile = logoFile;
            _logo = WatermarkManager.LoadLogo(logoFile);
            if (_logo == null) _logoFile = logoFile != null && File.Exists(WatermarkManager.LogoPath(logoFile)) ? logoFile : null;
            LogoPreview.Source = _logo;
            LogoEmpty.Visibility = _logo == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnChooseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Str("Str_Watermark_ChooseLogo"),
                Filter = Str("Str_Watermark_FilterLogo", "Pictures (PNG with see-through areas works best)") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" + Str("Str_Watermark_FilterAll", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            if (!WatermarkManager.IsPicture(dialog.FileName))
            {
                ShowLogoError(Str("Str_Watermark_LogoNotPicture", "That file isn't a picture Windows can open."));
                return;
            }
            string? copy = WatermarkManager.ImportLogo(dialog.FileName);
            if (copy == null)
            {
                ShowLogoError(Str("Str_Watermark_LogoCantCopy", "The logo couldn't be copied into the app's folder."));
                return;
            }
            TxtLogoError.Visibility = Visibility.Collapsed;
            SetLogo(copy);
            CleanLogos();
            SettingChanged();
        }

        private void BtnClearLogo_Click(object sender, RoutedEventArgs e)
        {
            SetLogo(null);
            CleanLogos();
            SettingChanged();
        }

        private void ShowLogoError(string text)
        {
            TxtLogoError.Text = text;
            TxtLogoError.Visibility = Visibility.Visible;
        }

        /// <summary>Copies of logos nobody uses any more (not in a saved preset, not the one chosen now) are removed.</summary>
        private void CleanLogos() =>
            WatermarkManager.DeleteUnusedLogos(_userPresets.Select(p => p.Settings.LogoFile).Append(_logoFile));

        // =================================================================
        //  Presets
        // =================================================================

        private List<WatermarkPreset> AllPresets() => WatermarkManager.BuiltInPresets.Concat(_userPresets).ToList();

        private void BuildPresetCombo(string? select)
        {
            _applyingSettings = true;
            try
            {
                CmbPreset.Items.Clear();
                CmbPreset.Items.Add(new ComboBoxItem { Content = Str("Str_Watermark_PresetCustom", "Custom"), Tag = CustomPresetTag });
                foreach (var p in AllPresets())
                    CmbPreset.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
            }
            finally
            {
                _applyingSettings = false;
            }
            SelectPreset(select);
        }

        private void SelectPreset(string? name)
        {
            bool before = _applyingSettings;
            _applyingSettings = true;
            try
            {
                var item = CmbPreset.Items.OfType<ComboBoxItem>().FirstOrDefault(i => name != null && (i.Tag as string) == name)
                           ?? CmbPreset.Items.OfType<ComboBoxItem>().FirstOrDefault();
                CmbPreset.SelectedItem = item;
            }
            finally
            {
                _applyingSettings = before;
            }
            UpdatePresetState();
        }

        private WatermarkPreset? SelectedPreset() =>
            (CmbPreset.SelectedItem as ComboBoxItem)?.Tag is string name && name != CustomPresetTag ? AllPresets().FirstOrDefault(p => p.Name == name) : null;

        private void UpdatePresetState()
        {
            var preset = SelectedPreset();
            BtnPresetDelete.IsEnabled = preset is { BuiltIn: false };
            TxtPresetHint.Text = preset == null ? Str("Str_Watermark_PresetHintCustom") : preset.BuiltIn ? Str("Str_Watermark_PresetHintBuiltIn") : Str("Str_Watermark_PresetHintUser");
        }

        private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applyingSettings) return;
            var preset = SelectedPreset();
            UpdatePresetState();
            if (preset == null) return;
            ApplyMark(preset.Settings.Clone(), keepPresetSelection: true);
        }

        private void BtnPresetSave_Click(object sender, RoutedEventArgs e)
        {
            PresetNamePanel.Visibility = Visibility.Visible;
            TxtPresetName.Text = SelectedPreset() is { BuiltIn: false } p ? p.Name : "";
            TxtPresetName.Focus();
            TxtPresetName.SelectAll();
        }

        private void BtnPresetCancel_Click(object sender, RoutedEventArgs e) => PresetNamePanel.Visibility = Visibility.Collapsed;

        private void TxtPresetName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { BtnPresetConfirm_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Escape) { BtnPresetCancel_Click(sender, e); e.Handled = true; }
        }

        private void BtnPresetConfirm_Click(object sender, RoutedEventArgs e)
        {
            string? name = WatermarkManager.CleanPresetName(TxtPresetName.Text, AllPresets(), out bool replaces);
            if (name == null)
            {
                TxtPresetHint.Text = Str("Str_Watermark_PresetNameBad", "Type a name that isn't one of the built-in ones.");
                return;
            }
            var mark = ReadMark();
            _userPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            _userPresets.Add(new WatermarkPreset { Name = name, Settings = mark });
            _userPresets = _userPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            WatermarkManager.SavePresets(_userPresets);
            PresetNamePanel.Visibility = Visibility.Collapsed;
            BuildPresetCombo(name);
            TxtPresetHint.Text = string.Format(CultureInfo.CurrentCulture, Str(replaces ? "Str_Watermark_PresetReplaced" : "Str_Watermark_PresetSaved", "Saved \"{0}\"."), name);
            ScheduleSave();
        }

        private void BtnPresetDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPreset() is not { BuiltIn: false } preset) return;
            _userPresets.Remove(preset);
            WatermarkManager.SavePresets(_userPresets);
            BuildPresetCombo(null);
            CleanLogos();
            TxtPresetHint.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_PresetDeleted", "Deleted \"{0}\"."), preset.Name);
            ScheduleSave();
        }

        // =================================================================
        //  Adding files
        // =================================================================

        private async void AddSources(IEnumerable<string> paths)
        {
            try
            {
                await AddSourcesAsync(paths);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Info($"Watermark add: {ex.Message}");
                ShowStatus(ex.Message, StatusKind.Error);
            }
        }

        private async Task AddSourcesAsync(IEnumerable<string> paths)
        {
            var all = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            bool include = InputCard.IncludeSubfolders;
            var added = new List<WatermarkItem>();
            int inaccessible = 0, unsupported = 0;

            // One token for everything being read; Clear and closing the page cancel it. A second drop does not cancel the first.
            var token = (_scanCts ??= new CancellationTokenSource()).Token;
            void Scanning(int found) => Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested)
                    TxtProgress.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_Scanning", "Reading folders... {0} files found"), found.ToString("N0", CultureInfo.CurrentCulture));
            });

            try
            {
                var loose = all.Where(File.Exists).ToList();
                if (loose.Count > 0)
                {
                    var scan = await WatermarkManager.ScanAsync(loose, includeSubfolders: false, token, Scanning);
                    unsupported += scan.Unsupported;
                    added.AddRange(WatermarkManager.AddToList(_items, _groups, scan.Files, null, isFolder: false, includeSubfolders: false, scan.Sizes));
                }
                foreach (string folder in all.Where(Directory.Exists))
                {
                    var scan = await WatermarkManager.ScanAsync(new[] { folder }, include, token, Scanning);
                    inaccessible += scan.InaccessibleFolders;
                    added.AddRange(WatermarkManager.AddToList(_items, _groups, scan.Files, folder, isFolder: true, includeSubfolders: include, scan.Sizes));
                }
            }
            catch (OperationCanceledException)
            {
                UpdateCounts();
                return;
            }

            if (inaccessible > 0)
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_Inaccessible", "{0} folders couldn't be read and were skipped."), inaccessible), StatusKind.Warning);
            else if (added.Count == 0 && all.Count > 0)
                ShowStatus(Str(unsupported > 0 ? "Str_Watermark_Unsupported" : "Str_Watermark_NothingAdded", "No new pictures, videos or PDFs were found there."), StatusKind.Warning);
            else if (_items.Count >= WatermarkManager.MaxFiles)
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_LimitReached", "The list is full ({0} files)."), WatermarkManager.MaxFiles.ToString("N0", CultureInfo.CurrentCulture)), StatusKind.Warning);
            else if (added.Any(i => i.Kind == WatermarkFileKind.Video) && WatermarkManager.MissingVideoEngine() is { } missing)
                ShowStatus(missing, StatusKind.Warning);
            else HideStatus();

            UpdateCounts();                      // clears the "reading folders" line even when nothing was added
            LoadInfo(added);
            if (FileList.SelectedItem == null && _items.Count > 0) SchedulePreview();
        }

        /// <summary>Stops reading folders and file details (Clear, closing the page).</summary>
        private void CancelBackgroundReads()
        {
            _scanCts?.Cancel();
            _scanCts = null;
            _infoCts?.Cancel();
            _infoCts = null;
        }

        /// <summary>Size, pages or length of each new file, a few at a time, so adding a big folder never freezes the page.</summary>
        private async void LoadInfo(IReadOnlyList<WatermarkItem> items)
        {
            if (items.Count == 0) return;
            var token = (_infoCts ??= new CancellationTokenSource()).Token;
            try
            {
                foreach (var item in items)
                {
                    await _infoGate.WaitAsync(token);
                    if (!item.Listed)                         // removed or cleared while waiting its turn
                    {
                        _infoGate.Release();
                        continue;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (info, problem) = await WatermarkManager.ReadInfoAsync(item, token);
                            Dispatcher.BeginInvoke(() =>
                            {
                                if (!item.Listed || item.Status == WatermarkStatus.Processing) return;
                                item.Info = info;
                                if (problem != null) { item.Status = WatermarkStatus.Failed; item.StatusText = problem; }
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            // cleared or closed
                        }
                        finally
                        {
                            _infoGate.Release();
                        }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // cleared or closed while waiting
            }
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_busy) AddSources(e.Paths);
        }

        private async void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            SaveSettings();
            try
            {
                await RescanFoldersAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Info($"Watermark rescan: {ex.Message}");
                ShowStatus(ex.Message, StatusKind.Error);
            }
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            CancelBackgroundReads();
            foreach (var item in _items) item.Listed = false;
            _items.Clear();
            _groups.Clear();
            SchedulePreview();
        }

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.Tag is not WatermarkGroup group) return;
            WatermarkManager.RemoveFromList(_items, _groups, group.Items.ToList());
            SchedulePreview();
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void RemoveSelected()
        {
            if (_busy) return;
            var chosen = FileList.SelectedItems.Cast<WatermarkItem>().ToList();
            if (chosen.Count == 0) return;
            WatermarkManager.RemoveFromList(_items, _groups, chosen);
            SchedulePreview();
        }

        private void FileList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            RemoveSelected();
            e.Handled = true;
        }

        /// <summary>After "Include subfolders" changes: brings every folder row in line with a fresh scan.</summary>
        private async Task RescanFoldersAsync()
        {
            if (!_viewConstructed || _applyingSettings || _busy) return;
            bool include = InputCard.IncludeSubfolders;
            var added = new List<WatermarkItem>();
            foreach (var group in _groups.Where(g => g.IsFolder && g.FolderPath != null).ToList())
            {
                var scan = await WatermarkManager.ScanAsync(new[] { group.FolderPath! }, include, (_scanCts ??= new CancellationTokenSource()).Token);
                added.AddRange(WatermarkManager.ApplyRescan(_items, _groups, group, scan.Files, include, scan.Sizes).Added);
            }
            LoadInfo(added);
            SchedulePreview();
            ScheduleSave();
        }

        // ---- drag and drop

        private static List<string> DroppedPaths(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(p => File.Exists(p) || Directory.Exists(p)).ToList()
                : new List<string>();

        private void ListArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_busy && DroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ListArea_Drop(object sender, DragEventArgs e)
        {
            var paths = DroppedPaths(e);
            if (!_busy && paths.Count > 0) AddSources(paths);
            e.Handled = true;
        }

        // =================================================================
        //  Counts and the list
        // =================================================================

        private void UpdateCounts()
        {
            int pictures = _items.Count(i => i.Kind == WatermarkFileKind.Image), videos = _items.Count(i => i.Kind == WatermarkFileKind.Video), pdfs = _items.Count(i => i.Kind == WatermarkFileKind.Pdf);
            TxtPillCount.Text = string.Format(CultureInfo.CurrentCulture, Str(_items.Count == 1 ? "Str_Watermark_PillFile" : "Str_Watermark_PillFiles", "{0} files"), _items.Count);
            var parts = new List<string>();
            if (pictures > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_KindPictures", "{0} pictures"), pictures));
            if (videos > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_KindVideos", "{0} videos"), videos));
            if (pdfs > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_KindPdfs", "{0} PDFs"), pdfs));
            TxtPillKinds.Text = string.Join(" · ", parts);
            PillKinds.Visibility = parts.Count > 0 && (pictures > 0 ? 1 : 0) + (videos > 0 ? 1 : 0) + (pdfs > 0 ? 1 : 0) > 1 ? Visibility.Visible : Visibility.Collapsed;

            // The Save to card shows where the files will go until a folder is chosen: the folder of the
            // kind there are most of (a mixed batch still saves each kind to its own folder, see BtnRun).
            var mostly = pictures == 0 && videos == 0 && pdfs == 0 ? WatermarkFileKind.Image
                : pictures >= videos && pictures >= pdfs ? WatermarkFileKind.Image
                : videos >= pdfs ? WatermarkFileKind.Video : WatermarkFileKind.Pdf;
            OutputCard.DefaultFolder = WatermarkManager.DefaultFolderFor(mostly);

            bool any = _items.Count > 0;
            ListEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            BtnRemoveSelected.IsEnabled = any && !_busy;
            BtnRun.IsEnabled = any || _busy;
            if (!_busy) TxtProgress.Text = any ? string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_ReadyInfo", "{0} will be marked. Originals are never changed."), TxtPillCount.Text) : Str("Str_Watermark_IdleInfo", "Add files to put a watermark on them.");
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed) SchedulePreview();
        }

        // =================================================================
        //  The preview
        // =================================================================

        private void SchedulePreview()
        {
            if (!_viewConstructed) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private async void RunPreview()
        {
            if (!_viewConstructed) return;
            int run = ++_previewRun;
            var item = FileList.SelectedItems.Cast<WatermarkItem>().FirstOrDefault() ?? _items.FirstOrDefault();
            if (item == null)
            {
                PreviewImage.Source = null;
                PreviewEmpty.Visibility = Visibility.Visible;
                PreviewBusy.Visibility = Visibility.Collapsed;
                TxtPreviewNote.Text = "";
                _baseKey = null;
                return;
            }

            string key = item.FullPath + "|" + item.SizeBytes;
            _previewCts?.Cancel();
            var cts = _previewCts = new CancellationTokenSource();
            try
            {
                if (key != _baseKey)
                {
                    PreviewBusy.Visibility = Visibility.Visible;
                    var source = await WatermarkManager.LoadPreviewAsync(item, PreviewSide, cts.Token);
                    if (run != _previewRun) return;
                    _baseKey = key;
                    _baseBitmap = source.Bitmap;
                    _baseProblem = source.Problem;
                }

                // A file that can't be drawn still gets a mark preview, on a blank sheet of a believable shape.
                var bitmap = _baseBitmap;
                int w = bitmap?.PixelWidth ?? (item.Kind == WatermarkFileKind.Pdf ? 794 : 1000), h = bitmap?.PixelHeight ?? (item.Kind == WatermarkFileKind.Pdf ? 1123 : 640);
                var mark = ReadMark();
                bool hasMark = WatermarkManager.HasMark(mark);
                string? markProblem = hasMark ? null : Str(mark.Kind == WatermarkKind.Text ? "Str_Watermark_NeedText" : "Str_Watermark_NeedLogo");

                var composed = await WatermarkManager.ComposePreviewAsync(bitmap, w, h, mark, _logo, hasMark);
                if (run != _previewRun) return;                  // a newer preview was asked for meanwhile
                markProblem ??= composed.Problem;

                PreviewBusy.Visibility = Visibility.Collapsed;
                PreviewEmpty.Visibility = Visibility.Collapsed;
                PreviewImage.Source = composed.Image;

                _previewProblem = _baseProblem ?? markProblem;
            }
            catch (OperationCanceledException)
            {
                return;                                          // superseded: the newer preview shows
            }

            string note = item.Kind switch
            {
                WatermarkFileKind.Video => string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_NoteVideo", "{0} · a frame from one second in"), item.Name),
                WatermarkFileKind.Pdf => string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_NotePdf", "{0} · page 1"), item.Name),
                _ => item.Name,
            };
            TxtPreviewNote.Text = _previewProblem ?? note;
            TxtPreviewNote.SetResourceReference(TextBlock.ForegroundProperty, _previewProblem != null ? "StatusWarningFg" : "TextSecondary");
        }

        // =================================================================
        //  Running
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                if (_stopping) return;
                _stopping = true;                                // Stop: finished files are kept; the file in progress is cancelled
                _runCts?.Cancel();
                BtnRun.IsEnabled = false;
                TxtRun.SetResourceReference(TextBlock.TextProperty, "Str_Watermark_Stopping");
                TxtProgress.Text = Str("Str_Watermark_StoppingInfo", "Stopping... the files already marked are kept.");
                return;
            }
            if (_items.Count == 0) return;

            var mark = ReadMark();
            if (!WatermarkManager.HasMark(mark))
            {
                ShowStatus(Str(mark.Kind == WatermarkKind.Text ? "Str_Watermark_NeedText" : "Str_Watermark_NeedLogo"), StatusKind.Warning);
                return;
            }
            // Nothing chosen and not "next to the originals": every kind of file goes to its own folder
            // (pictures, videos, PDFs), made when its first file is saved.
            bool byFileType = !OutputCard.IsSameAsSource && OutputCard.FolderPath.Length == 0 && OutputCard.UseDefaultFolder;
            if (!byFileType && OutputCard.Status.IsError)
            {
                ShowStatus(Str("Str_Watermark_CantSave", "Can't save there: ") + OutputCard.Status.Text, StatusKind.Error);
                return;
            }
            string? folder;
            try
            {
                folder = byFileType ? null : OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowStatus(Str("Str_Watermark_CantSave", "Can't save there: ") + ex.Message, StatusKind.Error);
                return;
            }
            bool same = OutputCard.IsSameAsSource;

            HideStatus();
            foreach (var item in _items) { item.Status = WatermarkStatus.Pending; item.StatusText = ""; item.Progress = 0; item.OutputPath = ""; }
            var items = _items.ToList();
            _runCts = new CancellationTokenSource();
            RunProgress.Value = 0;
            TxtProgressPercent.Text = "";
            SetBusy(true);
            var progress = new Progress<WatermarkProgress>(p =>
            {
                RunProgress.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                if (_stopping) return;                           // keeps saying "Stopping..."
                string text = string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_Progress", "{0} of {1} done · {2}"), p.Done + p.Failed + p.Skipped, p.Total, p.Current);
                if (p.Eta is { } eta) text += " · " + string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_Eta", "about {0} left"), FormatEta(eta));
                TxtProgress.Text = text;
            });

            WatermarkRunResult result;
            try
            {
                result = await WatermarkManager.RunAsync(items, mark, folder, same, progress, _runCts.Token, saveByFileType: byFileType);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RunProgress.Value = 0;
                TxtProgressPercent.Text = "";
                SetBusy(false);
                ShowStatus(ex.Message, StatusKind.Error);
                return;
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
            }

            TxtProgressPercent.Text = result.Cancelled ? $"{RunProgress.Value:0}%" : "100%";
            SetBusy(false);
            OutputCard.Refresh();
            _lastOutput = result.OutputFiles.FirstOrDefault();
            BtnOpenFile.IsEnabled = BtnOpenResult.IsEnabled = _lastOutput != null;
            ShowSummary(result);
        }

        private void ShowSummary(WatermarkRunResult r)
        {
            string time = r.Elapsed.TotalSeconds < 1 ? "" : r.Elapsed.TotalMinutes >= 1 ? $" · {r.Elapsed:m\\:ss}" : $" · {r.Elapsed.TotalSeconds:0} s";
            ShowDone(string.Format(CultureInfo.CurrentCulture, Str(r.Cancelled ? "Str_Watermark_Stopped" : "Str_Watermark_Finished", "{0} marked"), r.Done) + time);

            var parts = new List<string>();
            if (r.Failed > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_SummaryFailed", "{0} failed (see the red rows)"), r.Failed));
            if (r.Skipped > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_SummarySkipped", "{0} skipped"), r.Skipped));
            if (r.Cancelled) parts.Add(Str("Str_Watermark_SummaryStopped", "Stopped. The files already marked were kept."));
            if (parts.Count > 0) ShowStatus(string.Join(". ", parts) + ".", r.Failed > 0 ? StatusKind.Error : StatusKind.Warning);
            TxtProgress.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_Watermark_SummaryInfo", "{0} files were written. The originals were not changed."), r.Done);
        }

        private static string FormatEta(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        private void SetBusy(bool busy)
        {
            _busy = busy;
            if (busy) _stopping = false;
            OutputCard.IsLocked = busy;
            SidebarStack.IsEnabled = !busy;
            BtnRemoveSelected.IsEnabled = !busy && _items.Count > 0;
            TxtRun.SetResourceReference(TextBlock.TextProperty, busy ? "Str_Watermark_Stop" : "Str_Watermark_Run");
            IconRun.Kind = busy ? PackIconKind.Stop : PackIconKind.Stamper;
            BtnRun.IsEnabled = busy || _items.Count > 0;
            if (busy) BtnOpenFile.IsEnabled = BtnOpenResult.IsEnabled = false;
        }

        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutput == null || !File.Exists(_lastOutput)) { TxtProgress.Text = Str("Str_Watermark_ResultGone", "The marked file is no longer there."); return; }
            try
            {
                Process.Start(new ProcessStartInfo(_lastOutput) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                TxtProgress.Text = ex.Message;
            }
        }

        private void BtnOpenResult_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutput == null || !File.Exists(_lastOutput)) { TxtProgress.Text = Str("Str_Watermark_ResultGone", "The marked file is no longer there."); return; }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                TxtProgress.Text = ex.Message;
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private void ShowDone(string text)
        {
            TxtDonePill.Text = text;
            DonePill.Visibility = Visibility.Visible;
            UiMotion.Pop(DonePill);
            _doneTimer.Stop();
            _doneTimer.Start();
        }

        private void ShowStatus(string text, StatusKind kind)
        {
            var (bg, fg, icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                _ => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(PackIcon.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
            UiMotion.FadeIn(StatusBanner);
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        // =================================================================
        //  Shell hooks
        // =================================================================

        /// <summary>Called when the window closes: stops a running batch (finished files stay, partial ones are removed) and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _previewRun++;
            _previewCts?.Cancel();
            CancelBackgroundReads();
            _previewTimer.Stop();
            _doneTimer.Stop();
            _runCts?.Cancel();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
