using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>A palette in the sidebar list: the ffmpeg colours as a gradient, and the name.</summary>
    public sealed record PaletteChoice(SpectrogramColorScheme Scheme, string Name, Brush Swatch);

    /// <summary>
    /// Spectrogram Generator - UI only. ffmpeg, probing, the batch run,
    /// saving and the temp folder are in <see cref="SpectrogramManager"/>;
    /// loading pictures in <see cref="SpectrogramImaging"/>. Single mode
    /// shows the picture as soon as a file is chosen and redraws it when a
    /// setting changes; Generate saves it. Batch mode lists files with live
    /// status and thumbnails. Folders picked here are kept for this session
    /// only - never saved.
    /// </summary>
    public partial class SpectogramView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int ThumbnailWidth = 168;
        private static string DialogTitle => Loc.T("Str_Tool_spectrogram_Name", "Spectrogram Generator");

        private readonly BulkObservableCollection<SpectrogramItem> _items = new();
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _thumbGate = new(3);   // rows on screen only, a few at a time
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly DispatcherTimer _previewTimer;
        private readonly bool _viewConstructed;

        private string? _sessionFolder;       // working pictures for this session (deleted on close)
        // Inside the Output card (built in BuildOutputOptions).
        private StackPanel SingleNamePanel = null!;
        private TextBox TxtOutputFileName = null!;
        private CheckBox AutoSaveCheckBox = null!;
        private TextBlock TxtOutputHint = null!;
        private CancellationTokenSource? _previewCts;
        private CancellationTokenSource? _runCts;

        // Single mode
        private string? _singlePath;
        private SpectrogramResult? _singleResult;
        private string? _singleMadeWith;
        private string? _singleSavedPath;
        private string? _singleSavedFrom;     // which working picture was saved (so Generate twice doesn't save twice)

        private bool _isBatchRunning;
        private bool _isRendering;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _zoomActual;
        private int _adding;

        public SpectogramView()
        {
            InitializeComponent();

            ColorSchemeCombo.ItemsSource = Enum.GetValues<SpectrogramColorScheme>()
                .Select(s => new PaletteChoice(s, s.ToString(), SpectrogramImaging.PaletteBrush(s)))
                .ToList();
            ColorSchemeCombo.SelectedIndex = 0;

            DataGridBatch.ItemsSource = _items;
            _items.CollectionChanged += (_, _) => UpdateBatchState();

            OutputCard.DefaultFolder = SpectrogramManager.DefaultFolder;
            BuildOutputOptions();

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _previewTimer.Tick += (_, _) =>
            {
                _previewTimer.Stop();
                if (_singlePath != null && !_isBatchRunning) _ = RenderSingleAsync(saveAfter: false);
            };

            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops ffmpeg and deletes this session's working pictures.</summary>
        public void CancelActiveRun()
        {
            _previewTimer.Stop();
            try
            {
                _previewCts?.Cancel();
                _runCts?.Cancel();
                _lifetimeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            SpectrogramManager.DeleteFolder(_sessionFolder);
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;
        private bool IsBatchMode => RadioModeBatch.IsChecked == true;
        private bool IsBusy => _isBatchRunning || _adding > 0;

        private string SessionFolder => _sessionFolder ??= SpectrogramManager.NewSessionFolder();

        // =================================================================
        //  Layout
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(SpectrogramManager.LoadSettings());
                _ = Task.Run(() => SpectrogramManager.CleanOldSessions(TimeSpan.FromDays(1), _sessionFolder));
            }
            ApplyResponsiveLayout(ActualWidth);
            UpdateNotes();
            UpdateBatchState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

        private void ApplyResponsiveLayout(double width)
        {
            if (width <= 0) return;
            bool roomForPreview = width >= 1150;
            BatchPreviewPanel.Visibility = roomForPreview ? Visibility.Visible : Visibility.Collapsed;
            BatchPreviewColumn.Width = new GridLength(roomForPreview ? 340 : 0);
            BatchPreviewGap.Width = new GridLength(roomForPreview ? 14 : 0);
            ColDuration.Visibility = width < 950 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Settings (no folders, no file names)
        // =================================================================

        private void ApplySettings(SpectrogramSettings s)
        {
            _applyingSettings = true;
            try
            {
                (s.Mode == SpectrogramMode.Batch ? RadioModeBatch : RadioModeSingle).IsChecked = true;
                WidthTextBox.Text = s.WidthPx.ToString();
                HeightTextBox.Text = s.HeightPx.ToString();
                ColorSchemeCombo.SelectedItem = ColorSchemeCombo.Items.OfType<PaletteChoice>().FirstOrDefault(p => p.Scheme == s.ColorScheme);
                CheckByTag(s.FrequencyScale.ToString(), RadioScaleLinear, RadioScaleLog);
                CheckByTag(s.ChannelMode.ToString(), RadioChannelsCombined, RadioChannelsSeparate);
                ShowLegendCheckBox.IsChecked = s.ShowLegend;
                MinFrequencyTextBox.Text = s.MinFrequencyHz?.ToString() ?? "";
                MaxFrequencyTextBox.Text = s.MaxFrequencyHz?.ToString() ?? "";
                CheckByTag(s.OutputFormat.ToString(), RadioFormatPng, RadioFormatJpg, RadioFormatBmp);
                AutoSaveCheckBox.IsChecked = s.AutoSave;
            }
            finally
            {
                _applyingSettings = false;
            }
            SyncPresetChips();
            ApplyMode();
        }

        private SpectrogramSettings ReadSettings()
        {
            int.TryParse(WidthTextBox.Text, out int w);
            int.TryParse(HeightTextBox.Text, out int h);
            var min = SpectrogramManager.ParseHz(MinFrequencyTextBox.Text, out bool minOk);
            var max = SpectrogramManager.ParseHz(MaxFrequencyTextBox.Text, out bool maxOk);
            return new SpectrogramSettings
            {
                Mode = IsBatchMode ? SpectrogramMode.Batch : SpectrogramMode.Single,
                WidthPx = Math.Clamp(w == 0 ? 1024 : w, SpectrogramManager.MinWidth, SpectrogramManager.MaxWidth),
                HeightPx = Math.Clamp(h == 0 ? 512 : h, SpectrogramManager.MinHeight, SpectrogramManager.MaxHeight),
                ColorScheme = SelectedScheme,
                FrequencyScale = ParseTag(CheckedTag(RadioScaleLinear, RadioScaleLog), SpectrogramFrequencyScale.Linear),
                ChannelMode = ParseTag(CheckedTag(RadioChannelsCombined, RadioChannelsSeparate), SpectrogramChannelMode.Combined),
                ShowLegend = ShowLegendCheckBox.IsChecked == true,
                MinFrequencyHz = minOk ? min : null,
                MaxFrequencyHz = maxOk ? max : null,
                OutputFormat = SelectedFormat,
                AutoSave = AutoSaveCheckBox.IsChecked == true,
            };
        }

        private SpectrogramColorScheme SelectedScheme => (ColorSchemeCombo.SelectedItem as PaletteChoice)?.Scheme ?? SpectrogramColorScheme.Intensity;
        private SpectrogramExportFormat SelectedFormat => ParseTag(CheckedTag(RadioFormatPng, RadioFormatJpg, RadioFormatBmp), SpectrogramExportFormat.Png);

        /// <summary>The sidebar as options, or null (and the reason shown in the sidebar) when something typed is off.</summary>
        private SpectrogramOptions? BuildOptions()
        {
            var options = SpectrogramManager.TryBuildOptions(
                WidthTextBox.Text, HeightTextBox.Text, MinFrequencyTextBox.Text, MaxFrequencyTextBox.Text,
                SelectedScheme,
                ParseTag(CheckedTag(RadioScaleLinear, RadioScaleLog), SpectrogramFrequencyScale.Linear),
                ParseTag(CheckedTag(RadioChannelsCombined, RadioChannelsSeparate), SpectrogramChannelMode.Combined),
                ShowLegendCheckBox.IsChecked == true, SelectedFormat, out string? error);
            ShowOptionsError(error);
            return options;
        }

        private void ShowOptionsError(string? error)
        {
            bool show = !string.IsNullOrEmpty(error);
            TxtOptionsError.Text = error ?? "";
            if (show != (OptionsErrorBanner.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(OptionsErrorBanner, show);
        }

        // =================================================================
        //  Option changes
        // =================================================================

        private void Option_Click(object sender, RoutedEventArgs e) => OnOptionsChanged();

        private void Option_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            SyncPresetChips();
            OnOptionsChanged();
        }

        private void ColorSchemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TxtPaletteHint.Text = SpectrogramManager.PaletteHint(SelectedScheme);
            TxtPaletteHint.Visibility = TxtPaletteHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OnOptionsChanged();
        }

        private void SizePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag }) return;
            var parts = tag.Split('x');
            _applyingSettings = true;
            WidthTextBox.Text = parts[0];
            HeightTextBox.Text = parts[1];
            _applyingSettings = false;
            OnOptionsChanged();
        }

        private void RangePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string key }) return;
            var preset = SpectrogramManager.RangePresets.FirstOrDefault(p => p.Key == key);
            _applyingSettings = true;
            MinFrequencyTextBox.Text = preset.Min?.ToString() ?? "";
            MaxFrequencyTextBox.Text = preset.Max?.ToString() ?? "";
            _applyingSettings = false;
            OnOptionsChanged();
        }

        /// <summary>Lights the size/range chip that matches what's typed, or none.</summary>
        private void SyncPresetChips()
        {
            string size = $"{WidthTextBox.Text.Trim()}x{HeightTextBox.Text.Trim()}";
            foreach (var chip in new[] { RadioSizeSmall, RadioSizeMedium, RadioSizeHd, RadioSize4K })
                chip.IsChecked = chip.Tag as string == size;

            var min = SpectrogramManager.ParseHz(MinFrequencyTextBox.Text, out _);
            var max = SpectrogramManager.ParseHz(MaxFrequencyTextBox.Text, out _);
            if (min == 0) min = null;
            var match = SpectrogramManager.RangePresets.FirstOrDefault(p => p.Min == min && p.Max == max);
            foreach (var chip in new[] { RadioRangeFull, RadioRangeSpeech, RadioRangeBass, RadioRangeHighs })
                chip.IsChecked = match.Key != null && chip.Tag as string == match.Key;
        }

        private void OnOptionsChanged()
        {
            if (Ignore) return;
            var options = BuildOptions();
            UpdateNotes();
            SpectrogramManager.SaveSettings(ReadSettings());
            if (options == null) return;

            if (!IsBatchMode)
            {
                // Redraw the picture shortly after the last change (typing a size shouldn't start ffmpeg per key).
                if (_singlePath != null && _singleMadeWith != SpectrogramManager.SettingsKey(options))
                {
                    _previewTimer.Stop();
                    _previewTimer.Start();
                }
            }
            else UpdateBatchState();
        }

        private void UpdateNotes()
        {
            var options = SpectrogramManager.TryBuildOptions(WidthTextBox.Text, HeightTextBox.Text, MinFrequencyTextBox.Text, MaxFrequencyTextBox.Text,
                SelectedScheme, SpectrogramFrequencyScale.Linear, SpectrogramChannelMode.Combined, ShowLegendCheckBox.IsChecked == true, SelectedFormat, out _);
            if (options != null)
            {
                var (w, h) = SpectrogramManager.OutputSize(options);
                TxtSizeNote.Text = options.ShowLegend
                    ? $"Picture {w:N0} × {h:N0} - the legend adds a frame around the {options.WidthPx:N0} × {options.HeightPx:N0} spectrum."
                    : $"Picture {w:N0} × {h:N0}.";
            }

            string? rangeKey = CheckedTag(RadioRangeFull, RadioRangeSpeech, RadioRangeBass, RadioRangeHighs);
            var preset = SpectrogramManager.RangePresets.FirstOrDefault(p => p.Key == rangeKey);
            TxtRangeNote.Text = preset.Key != null
                ? preset.Hint + "."
                : "Type a range, e.g. 50 to 12k. A top above what the file holds is lowered to fit.";

            bool single = !IsBatchMode;
            bool auto = AutoSaveCheckBox.IsChecked == true;
            TxtOutputHint.Text = single
                ? auto ? "Generate saves the picture here. A name that's taken gets \" (2)\" - nothing is replaced."
                       : "Generate only shows the picture - use Save as to keep it."
                : auto ? "Each picture is saved here as it's made, named after its sound file."
                       : "Pictures stay in the list until you use Save all to folder.";
            SingleNamePanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Mode
        // =================================================================

        private void RadioMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;   // fires during InitializeComponent
            ApplyMode();
            if (!_applyingSettings) SpectrogramManager.SaveSettings(ReadSettings());
        }

        private void ApplyMode()
        {
            bool batch = IsBatchMode;
            SingleModeArea.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
            BatchModeArea.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
            UiMotion.FadeIn(batch ? BatchModeArea : SingleModeArea, 4);
            TxtDropTitle.Text = batch ? "Drop to add to the list" : "Drop to make a spectrogram";
            TxtDropHint.Text = batch ? "Audio and video files, and whole folders" : "An audio or video file";
            UpdateRunButton();
            UpdateNotes();
            if (batch) UpdateBatchState();
            else ShowSingleStatus();
        }

        // =================================================================
        //  Adding files (browse, drop)
        // =================================================================

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose an audio or video file", Filter = SpectrogramManager.FileDialogFilter };
            if (_singlePath != null && Directory.Exists(Path.GetDirectoryName(_singlePath))) dialog.InitialDirectory = Path.GetDirectoryName(_singlePath);
            if (dialog.ShowDialog() == true) SetSingleFile(dialog.FileName);
        }

        private void BtnAddBatchFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose audio or video files", Filter = SpectrogramManager.FileDialogFilter, Multiselect = true };
            if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FileNames);
        }

        private void BtnLoadFromFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose folders with audio or video", Multiselect = true };
            if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FolderNames);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isBatchRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>DragLeave also fires when moving between child elements - only hide once the pointer is really outside.</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            if (_isBatchRunning || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            if (IsBatchMode)
            {
                _ = AddPathsAsync(paths);
                return;
            }

            // Single mode: the first supported file. Several files or a folder? Offer batch mode instead of ignoring them.
            var files = paths.Where(File.Exists).Where(SpectrogramManager.IsSupported).ToList();
            if (paths.Length > 1 || paths.Any(Directory.Exists))
            {
                RadioModeBatch.IsChecked = true;
                _ = AddPathsAsync(paths);
                StatusText.Text = "Switched to batch mode for several files.";
                return;
            }
            if (files.Count == 0)
            {
                StatusText.Text = $"{Path.GetFileName(paths[0])} isn't an audio or video file this tool can read.";
                return;
            }
            SetSingleFile(files[0]);
        }

        /// <summary>Adds files and folders to the batch list; folders are read off the UI thread.</summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isBatchRunning || paths.Count == 0) return;
            var token = _lifetimeCts.Token;
            bool subfolders = ChkIncludeSubfolders.IsChecked == true;
            _adding++;
            UpdateBatchState();
            StatusText.Text = "Looking for audio and video…";
            try
            {
                int added = 0, inaccessible = 0, skipped = 0;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var (files, unreadable) = await SpectrogramManager.ScanFolderAsync(folder, subfolders, token);
                    inaccessible += unreadable;
                    added += SpectrogramManager.AddToList(_items, _known, files);
                }
                var loose = paths.Where(p => !Directory.Exists(p)).ToList();
                if (loose.Count > 0)
                {
                    int n = SpectrogramManager.AddToList(_items, _known, loose);
                    skipped += loose.Count - n;
                    added += n;
                }

                if (added > 0) UiMotion.Pop(BatchEntriesCountPill, 0.9);
                var parts = new List<string>
                {
                    added == 0 ? "Nothing new added - the files may already be in the list." : $"Added {added:N0} {SpectrogramText.Plural(added, "file", "files")}.",
                };
                if (inaccessible > 0) parts.Add($"{inaccessible:N0} {SpectrogramText.Plural(inaccessible, "folder", "folders")} couldn't be opened.");
                if (skipped > 0) parts.Add($"{skipped:N0} skipped (not audio/video, or already listed).");
                if (!_isBatchRunning) StatusText.Text = string.Join(" ", parts);
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            finally
            {
                _adding--;
                UpdateBatchState();
            }
        }

        // =================================================================
        //  Single mode
        // =================================================================

        private void SetSingleFile(string path)
        {
            if (!SpectrogramManager.IsSupported(path))
            {
                StatusText.Text = $"{Path.GetFileName(path)} isn't an audio or video file this tool can read.";
                return;
            }
            _singlePath = path;
            PathTextBox.Text = path;
            IconSourceKind.Kind = SpectrogramManager.IsVideo(path) ? PackIconKind.FileVideoOutline : PackIconKind.FileMusicOutline;
            _singleMadeWith = null;
            _ = RenderSingleAsync(saveAfter: false);
        }

        /// <summary>Makes the picture for the chosen file (a newer request cancels an older one), shows it, and saves it when asked.</summary>
        private async Task RenderSingleAsync(bool saveAfter)
        {
            if (_singlePath == null) return;
            var options = BuildOptions();
            if (options == null)
            {
                StatusText.Text = "Fix the setting shown in the sidebar first.";
                return;
            }

            string key = SpectrogramManager.SettingsKey(options);
            if (_singleResult is { Success: true } && _singleMadeWith == key && _singleResult.SourcePath == _singlePath && File.Exists(_singleResult.ImagePath))
            {
                if (saveAfter) SaveSingleIfWanted();
                return;
            }

            _previewTimer.Stop();
            _previewCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _previewCts = cts;
            string source = _singlePath;

            SetRendering(true);
            StatusText.Text = $"Making the spectrogram of {Path.GetFileName(source)}…";
            try
            {
                var result = await SpectrogramManager.GenerateAsync(source, options, SessionFolder, cts.Token);
                if (cts.IsCancellationRequested || result.Cancelled)
                {
                    SpectrogramManager.TryDelete(result.ImagePath);
                    if (ReferenceEquals(_previewCts, cts)) StatusText.Text = "Stopped.";
                    return;
                }
                if (!ReferenceEquals(_previewCts, cts) || source != _singlePath)
                {
                    SpectrogramManager.TryDelete(result.ImagePath);   // a newer request took over
                    return;
                }
                ShowSingleResult(result, key);
                if (saveAfter && result.Success) SaveSingleIfWanted();
            }
            finally
            {
                if (ReferenceEquals(_previewCts, cts))
                {
                    _previewCts = null;
                    SetRendering(false);
                }
                cts.Dispose();
            }
        }

        private void ShowSingleResult(SpectrogramResult result, string key)
        {
            // The old working picture isn't needed once replaced (unless a batch row or the saved copy points at it).
            string? old = _singleResult?.ImagePath;
            if (old != null && old != result.ImagePath && !_items.Any(i => i.ImagePath == old)) SpectrogramManager.TryDelete(old);

            _singleResult = result;
            _singleMadeWith = result.Success ? key : null;
            _singleSavedPath = null;
            _singleSavedFrom = null;
            PillSaved.Visibility = Visibility.Collapsed;
            BtnShowInFolder.IsEnabled = false;

            if (result.Info is { } info)
            {
                TxtInfoDuration.Text = info.DurationText.Length > 0 ? info.DurationText : "Unknown length";
                TxtInfoRate.Text = info.SampleRateText;
                TxtInfoChannels.Text = info.ChannelsText;
                InfoPanel.Visibility = Visibility.Visible;
            }
            else InfoPanel.Visibility = Visibility.Collapsed;

            PillRangeNote.Visibility = result.RangeNote != null ? Visibility.Visible : Visibility.Collapsed;
            TxtRangeNoteResult.Text = result.RangeNote ?? "";

            if (!result.Success)
            {
                PreviewImage.Source = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                SetSingleActions(false);
                StatusText.Text = result.Error ?? "The spectrogram couldn't be made.";
                return;
            }

            var bitmap = SpectrogramImaging.Load(result.ImagePath);
            PreviewImage.Source = bitmap;
            PreviewPlaceholder.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
            TxtInfoSize.Text = bitmap != null ? $"{bitmap.PixelWidth:N0} × {bitmap.PixelHeight:N0}" : "";
            UiMotion.FadeIn(PreviewImage, 0);
            SetSingleActions(bitmap != null);
            ShowSingleStatus();
        }

        private void ShowSingleStatus()
        {
            if (_isRendering) return;
            if (_singlePath == null)
            {
                StatusText.Text = "Choose or drop an audio or video file.";
                return;
            }
            if (_singleResult is not { Success: true }) return;
            StatusText.Text = _singleSavedPath != null
                ? $"Saved as {Path.GetFileName(_singleSavedPath)}."
                : AutoSaveCheckBox.IsChecked == true
                    ? "Ready. Generate saves it to the folder in the sidebar."
                    : "Ready. Use Save as or Copy to keep it.";
        }

        private void SetSingleActions(bool enabled)
        {
            SaveButton.IsEnabled = enabled;
            CopyButton.IsEnabled = enabled;
            BtnZoomActual.IsEnabled = enabled;
        }

        private void SetRendering(bool rendering)
        {
            _isRendering = rendering;
            if (rendering != (PreviewBusy.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(PreviewBusy, rendering);
            BatchProgressBar.IsIndeterminate = rendering;
            if (!rendering) BatchProgressBar.Value = _singleResult is { Success: true } ? 100 : 0;
            TxtProgressPercent.Text = "";
            UpdateRunButton();
        }

        /// <summary>Generate in single mode: save to the sidebar folder when Save automatically is on (never replacing a file).</summary>
        private void SaveSingleIfWanted()
        {
            if (_singleResult is not { Success: true, ImagePath: { } image }) return;
            if (AutoSaveCheckBox.IsChecked != true)
            {
                StatusText.Text = "Made. Use Save as or Copy to keep it (Save automatically is off).";
                return;
            }
            if (_singleSavedFrom == image && _singleSavedPath != null && File.Exists(_singleSavedPath))
            {
                StatusText.Text = $"Already saved as {Path.GetFileName(_singleSavedPath)} - change a setting to make a new one.";
                UiMotion.Pop(PillSaved, 0.9);
                return;
            }
            try
            {
                string folder = OutputFolder;
                string target = Path.Combine(folder, SpectrogramManager.OutputFileName(_singleResult.SourcePath, TxtOutputFileName.Text, SpectrogramManager.FormatOf(image)));
                MarkSingleSaved(SpectrogramManager.SaveCopy(image, target, overwrite: false), image);
                Toast("Spectrogram saved", Path.GetFileName(_singleSavedPath!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText.Text = "Made, but it couldn't be saved: " + SpectrogramManager.PlainSaveError(ex);
            }
        }

        private void MarkSingleSaved(string path, string fromImage)
        {
            _singleSavedPath = path;
            _singleSavedFrom = fromImage;
            TxtSaved.Text = $"Saved · {Path.GetFileName(path)}";
            PillSaved.ToolTip = path;
            PillSaved.Visibility = Visibility.Visible;
            UiMotion.Pop(PillSaved, 0.9);
            BtnShowInFolder.IsEnabled = true;
            ShowSingleStatus();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_singleResult is not { Success: true, ImagePath: { } image }) return;
            var format = SpectrogramManager.FormatOf(image);
            string ext = SpectrogramManager.ExtensionFor(format);
            string folder = OutputFolder;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the spectrogram",
                Filter = $"{format.ToString().ToUpperInvariant()} image|*{ext}",
                DefaultExt = ext,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SpectrogramManager.OutputFileName(_singleResult.SourcePath, TxtOutputFileName.Text, format),
            };
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
            if (dialog.ShowDialog() != true) return;
            try
            {
                MarkSingleSaved(SpectrogramManager.SaveCopy(image, dialog.FileName, overwrite: true), image);   // the dialog already asked about replacing
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ThemedMessageBox.Show(Loc.T("Str_MB_ThePictureCouldnT", "The picture couldn't be saved.\n\n") + SpectrogramManager.PlainSaveError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewImage.Source is not BitmapSource bitmap) return;
            // The clipboard can be briefly held by another program (CLIPBRD_E_CANT_OPEN) - try a few times.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Clipboard.SetImage(bitmap);
                    StatusText.Text = "Copied - paste it into a document or chat.";
                    UiMotion.Pop(CopyButton, 0.85);
                    return;
                }
                catch (COMException)
                {
                    Thread.Sleep(60);
                }
            }
            StatusText.Text = "Another program is using the clipboard - try Copy again.";
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e) => ShowInExplorer(_singleSavedPath);

        private void BtnZoomActual_Click(object sender, RoutedEventArgs e)
        {
            _zoomActual = !_zoomActual;
            PreviewImage.Stretch = _zoomActual ? Stretch.None : Stretch.Uniform;
            var bars = _zoomActual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            PreviewScroller.HorizontalScrollBarVisibility = bars;
            PreviewScroller.VerticalScrollBarVisibility = bars;
            IconZoom.Kind = _zoomActual ? PackIconKind.MagnifyMinusOutline : PackIconKind.MagnifyPlusOutline;
            BtnZoomActual.ToolTip = _zoomActual ? "Fit to the window" : "Show actual pixels (scroll to look around)";
        }

        // =================================================================
        //  Generate / Stop
        // =================================================================

        private async void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBatchMode)
            {
                if (_isRendering)
                {
                    _previewCts?.Cancel();
                    return;
                }
                if (_singlePath == null)
                {
                    BrowseButton_Click(sender, e);
                    return;
                }
                await RenderSingleAsync(saveAfter: true);
                return;
            }

            if (_isBatchRunning)
            {
                _runCts?.Cancel();
                GenerateButton.IsEnabled = false;   // back on when the run unwinds
                StatusText.Text = "Stopping…";
                return;
            }
            await RunBatchAsync();
        }

        private async Task RunBatchAsync()
        {
            var options = BuildOptions();
            if (options == null)
            {
                StatusText.Text = "Fix the setting shown in the sidebar first.";
                return;
            }
            if (_items.Count == 0)
            {
                StatusText.Text = "Add files or a folder first.";
                return;
            }

            string key = SpectrogramManager.SettingsKey(options);
            if (!_items.Any(i => SpectrogramManager.NeedsWork(i, key)))
            {
                StatusText.Text = "Every picture is already made with these settings.";
                return;
            }

            bool autoSave = AutoSaveCheckBox.IsChecked == true;
            if (autoSave && OutputCard.Status.IsError)
            {
                StatusText.Text = "Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Save to.";
                return;
            }
            string folder = OutputFolder;
            int saveFailures = 0;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _runCts = cts;
            SetBatchRunning(true);

            var progress = new Progress<SpectrogramProgress>(p =>
            {
                BatchProgressBar.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                if (!cts.IsCancellationRequested)
                    StatusText.Text = p.Total == 0 ? "Starting…" : $"{p.Done:N0} of {p.Total:N0} · {p.CurrentFile}";
                UpdateBatchCounts();
            });

            Task OnFinished(SpectrogramItem item)
            {
                if (autoSave && item.ImagePath != null)
                {
                    try
                    {
                        string target = Path.Combine(folder, SpectrogramManager.OutputFileName(item.FullPath, null, SpectrogramManager.FormatOf(item.ImagePath)));
                        item.SavedPath = SpectrogramManager.SaveCopy(item.ImagePath, target, overwrite: false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        saveFailures++;
                        item.Message = "Made, but not saved: " + SpectrogramManager.PlainSaveError(ex);
                    }
                }
                // Thumbnail now only if the row is on screen; others load when scrolled to.
                if (DataGridBatch.ItemContainerGenerator.ContainerFromItem(item) != null) RequestThumbnail(item);
                if (ReferenceEquals(DataGridBatch.SelectedItem, item)) ShowBatchPreview(item);
                return Task.CompletedTask;
            }

            try
            {
                var (done, failed, cancelled) = await SpectrogramManager.RunBatchAsync(_items, options, SessionFolder, progress, OnFinished, cts.Token);
                int total = _items.Count;
                string summary = SpectrogramManager.DescribeBatch(_items.Count(i => i.Status == SpectrogramStatus.Done), _items.Count(i => i.Status == SpectrogramStatus.Failed), cancelled, total);
                if (autoSave && saveFailures > 0) summary += $" {saveFailures:N0} couldn't be saved - see the rows.";
                else if (autoSave && done > 0) summary += $" Saved to {folder}.";
                StatusText.Text = summary;
                if (!cancelled)
                {
                    BatchProgressBar.Value = 100;
                    TxtProgressPercent.Text = "100%";
                    Toast("Spectrograms ready", $"Made {done:N0}{(failed > 0 ? $", {failed:N0} failed" : "")}.");
                }
            }
            finally
            {
                if (ReferenceEquals(_runCts, cts)) _runCts = null;
                cts.Dispose();
                SetBatchRunning(false);
            }
        }

        private void SetBatchRunning(bool running)
        {
            _isBatchRunning = running;
            OutputCard.IsLocked = running;
            if (!running) OutputCard.Refresh();   // free space, or "made on the first save" is now "ready"
            if (running)
            {
                BatchProgressBar.Value = 0;
                TxtProgressPercent.Text = "0%";
            }
            UpdateRunButton();
            UpdateBatchState();
        }

        private void UpdateRunButton()
        {
            bool stop = IsBatchMode ? _isBatchRunning : _isRendering;
            GenerateButton.IsEnabled = true;
            IconGenerate.Kind = stop ? PackIconKind.Stop : PackIconKind.Waveform;
            if (stop)
            {
                TxtGenerateLabel.Text = "Stop";
                GenerateButton.ToolTip = IsBatchMode ? "Stop - finished pictures are kept" : "Stop making this picture";
            }
            else if (IsBatchMode)
            {
                TxtGenerateLabel.SetResourceReference(TextBlock.TextProperty, "Str_Spectrogram_GenerateAll");
                GenerateButton.SetResourceReference(ToolTipProperty, "Str_Spectrogram_GenerateAllTooltip");
            }
            else
            {
                TxtGenerateLabel.SetResourceReference(TextBlock.TextProperty, "Str_Spectrogram_Generate");
                GenerateButton.SetResourceReference(ToolTipProperty, "Str_Spectrogram_GenerateTooltip");
            }
            // Settings can't change mid-run (the run uses what was set when it started).
            OptionsScrollViewer.IsEnabled = !_isBatchRunning;
        }

        // =================================================================
        //  Batch list
        // =================================================================

        private void UpdateBatchState()
        {
            int count = _items.Count;
            BatchEmptyStatePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BatchEntriesCountPill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtBatchEntriesCount.Text = $"{count:N0} {SpectrogramText.Plural(count, "file", "files")}";
            UpdateBatchCounts();

            bool idle = !IsBusy;
            BtnAddBatchFiles.IsEnabled = !_isBatchRunning;
            BtnLoadFromFolder.IsEnabled = !_isBatchRunning;
            ChkIncludeSubfolders.IsEnabled = idle;
            BtnRemoveBatchEntry.IsEnabled = idle && DataGridBatch.SelectedItems.Count > 0;
            BtnClearBatchEntries.IsEnabled = idle && count > 0;
            BatchExportButton.IsEnabled = idle && _items.Any(i => i.Status == SpectrogramStatus.Done);

            if (IsBatchMode && !_isBatchRunning && _adding == 0 && count > 0)
            {
                var options = SpectrogramManager.TryBuildOptions(WidthTextBox.Text, HeightTextBox.Text, MinFrequencyTextBox.Text, MaxFrequencyTextBox.Text,
                    SelectedScheme, ParseTag(CheckedTag(RadioScaleLinear, RadioScaleLog), SpectrogramFrequencyScale.Linear),
                    ParseTag(CheckedTag(RadioChannelsCombined, RadioChannelsSeparate), SpectrogramChannelMode.Combined),
                    ShowLegendCheckBox.IsChecked == true, SelectedFormat, out _);
                if (options != null)
                {
                    // From the rows only (no disk checks - this runs on every settings change, with 10,000+ rows).
                    string key = SpectrogramManager.SettingsKey(options);
                    int todo = _items.Count(i => i.Status != SpectrogramStatus.Done || i.MadeWith != key);
                    int stale = _items.Count(i => i.Status == SpectrogramStatus.Done && i.MadeWith != key);
                    TxtBatchExportHint.Text = todo == 0
                        ? "All made with these settings. Save all to folder copies them, named after each sound file."
                        : stale > 0
                            ? $"Settings changed: Generate all remakes {todo:N0} {SpectrogramText.Plural(todo, "picture", "pictures")}."
                            : $"{todo:N0} to make. Double-click a finished row to see it large.";
                }
            }
        }

        private void UpdateBatchCounts()
        {
            int done = _items.Count(i => i.Status == SpectrogramStatus.Done);
            int failed = _items.Count(i => i.Status == SpectrogramStatus.Failed);
            SummaryPill.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryCount.Text = $"{done:N0} made";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtFailedCount.Text = $"{failed:N0} failed";
        }

        private void DataGridBatch_LoadingRow(object? sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is SpectrogramItem item) RequestThumbnail(item);
        }

        private void RequestThumbnail(SpectrogramItem item)
        {
            if (item.ThumbnailRequested || item.ImagePath == null) return;
            item.ThumbnailRequested = true;
            _ = LoadThumbnailAsync(item, item.ImagePath, _lifetimeCts.Token);
        }

        private async Task LoadThumbnailAsync(SpectrogramItem item, string path, CancellationToken token)
        {
            try
            {
                await _thumbGate.WaitAsync(token);
                try
                {
                    var thumb = await Task.Run(() => SpectrogramImaging.Load(path, ThumbnailWidth), token);
                    if (!token.IsCancellationRequested && item.ImagePath == path) item.Thumbnail = thumb;
                }
                finally
                {
                    _thumbGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                item.ThumbnailRequested = false;
            }
        }

        private void DataGridBatch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnRemoveBatchEntry.IsEnabled = !IsBusy && DataGridBatch.SelectedItems.Count > 0;
            ShowBatchPreview(DataGridBatch.SelectedItem as SpectrogramItem);
        }

        private void ShowBatchPreview(SpectrogramItem? item)
        {
            if (item == null)
            {
                BatchPreviewImage.Source = null;
                TxtBatchPreviewEmpty.Visibility = Visibility.Visible;
                TxtBatchPreviewName.Text = "";
                TxtBatchPreviewMessage.Text = "";
                return;
            }
            var bitmap = item.Status == SpectrogramStatus.Done ? SpectrogramImaging.Load(item.ImagePath) : null;
            BatchPreviewImage.Source = bitmap;
            TxtBatchPreviewEmpty.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
            TxtBatchPreviewName.Text = item.FileName;
            TxtBatchPreviewMessage.Text = item.SavedPath != null ? $"Saved as {Path.GetFileName(item.SavedPath)}" : item.Message;
            if (bitmap != null) UiMotion.FadeIn(BatchPreviewImage, 0);
        }

        /// <summary>Opens a finished row in single mode, large, with Save as / Copy.</summary>
        private void DataGridBatch_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_isBatchRunning || DataGridBatch.SelectedItem is not SpectrogramItem { Status: SpectrogramStatus.Done, Result: { } result } item) return;
            _singlePath = item.FullPath;
            PathTextBox.Text = item.FullPath;
            IconSourceKind.Kind = SpectrogramManager.IsVideo(item.FullPath) ? PackIconKind.FileVideoOutline : PackIconKind.FileMusicOutline;
            string? previous = _singleResult?.ImagePath;
            if (previous != null && previous != item.ImagePath && !_items.Any(i => i.ImagePath == previous)) SpectrogramManager.TryDelete(previous);
            _singleResult = null;   // the row keeps its working picture
            RadioModeSingle.IsChecked = true;
            ShowSingleResult(result, item.MadeWith ?? "");
            if (item.SavedPath != null) MarkSingleSaved(item.SavedPath, item.ImagePath!);
        }

        private void DataGridBatch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !IsBusy && DataGridBatch.SelectedItems.Count > 0)
            {
                RemoveItems(DataGridBatch.SelectedItems.Cast<SpectrogramItem>().ToList());
                e.Handled = true;
            }
        }

        private void BtnRemoveSingleBatchEntry_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy && sender is Button { Tag: SpectrogramItem item }) RemoveItems(new[] { item });
        }

        private void BtnRemoveBatchEntry_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy) RemoveItems(DataGridBatch.SelectedItems.Cast<SpectrogramItem>().ToList());
        }

        private void BtnClearBatchEntries_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy) RemoveItems(_items.ToList());
        }

        /// <summary>Takes rows off the list (the sound files stay put) and deletes their working pictures.</summary>
        private void RemoveItems(IReadOnlyCollection<SpectrogramItem> remove)
        {
            if (remove.Count == 0) return;
            var gone = remove.ToHashSet();
            foreach (var item in gone)
            {
                _known.Remove(item.FullPath);
                if (item.ImagePath != null && item.ImagePath != _singleResult?.ImagePath) SpectrogramManager.TryDelete(item.ImagePath);
            }
            if (gone.Count == _items.Count) _items.Clear();
            else foreach (var item in gone) _items.Remove(item);
            ShowBatchPreview(null);
            StatusText.Text = $"Removed {gone.Count:N0} from the list. The files themselves are untouched.";
        }

        private async void BatchExportButton_Click(object sender, RoutedEventArgs e)
        {
            var ready = _items.Where(i => i.Status == SpectrogramStatus.Done).ToList();
            if (ready.Count == 0 || IsBusy) return;
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save all the spectrograms into…" };
            string current = OutputFolder;
            if (Directory.Exists(current)) dialog.InitialDirectory = current;
            if (dialog.ShowDialog() != true) return;

            _adding++;
            UpdateBatchState();
            var progress = new Progress<SpectrogramProgress>(p =>
            {
                BatchProgressBar.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                StatusText.Text = $"Saving {p.Done:N0} of {p.Total:N0}…";
            });
            try
            {
                var (saved, failed) = await SpectrogramManager.ExportAsync(ready, dialog.FolderName, progress, _lifetimeCts.Token);
                StatusText.Text = $"Saved {saved:N0} {SpectrogramText.Plural(saved, "picture", "pictures")} to {dialog.FolderName}" +
                                  (failed > 0 ? $" · {failed:N0} couldn't be saved." : ". Names that were taken got \" (2)\".");
                Toast("Spectrograms saved", $"{saved:N0} saved to {Path.GetFileName(dialog.FolderName.TrimEnd('\\', '/'))}.");
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            finally
            {
                _adding--;
                UpdateBatchState();
            }
        }

        // =================================================================
        //  Output folder (this session only)
        // =================================================================

        /// <summary>The Output card's folder (this session only - never saved), else the default.</summary>
        private string OutputFolder => OutputCard.EffectiveFolder ?? SpectrogramManager.DefaultFolder;

        private void BuildOutputOptions()
        {
            TxtOutputFileName = OutputCardOptions.TextBox("Same as the sound file",
                "Leave blank to name the picture after the sound file. The extension comes from the image format.", maxLength: 150);
            SingleNamePanel = OutputCardOptions.Panel(OutputCardOptions.Label("File name"), TxtOutputFileName);
            AutoSaveCheckBox = OutputCardOptions.CheckBox("Save automatically", true, null);
            AutoSaveCheckBox.SetResourceReference(ToolTipProperty, "Str_MicRecorder_AutoSaveTooltip");
            AutoSaveCheckBox.Click += Option_Click;
            TxtOutputHint = OutputCardOptions.Hint(top: 8);
            OutputCard.Options = OutputCardOptions.Panel(SingleNamePanel, AutoSaveCheckBox, TxtOutputHint);
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private void ShowInExplorer(string? file)
        {
            try
            {
                if (file != null && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
                else StatusText.Text = "The saved picture is no longer there.";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                StatusText.Text = "Couldn't open the folder: " + ex.Message;
            }
        }

        private static void Toast(string title, string message)
        {
            try
            {
                ToastNotificationManager.ShowToast(title, message);
            }
            catch (Exception)
            {
                // a toast is a nice-to-have
            }
        }

        private static string? CheckedTag(params RadioButton[] buttons) => buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as string;

        private static void CheckByTag(string tag, params RadioButton[] buttons)
        {
            var match = buttons.FirstOrDefault(b => b.Tag as string == tag) ?? buttons[0];
            match.IsChecked = true;
        }

        private static T ParseTag<T>(string? tag, T fallback) where T : struct, Enum =>
            Enum.TryParse(tag, out T value) && Enum.IsDefined(value) ? value : fallback;
    }
}
