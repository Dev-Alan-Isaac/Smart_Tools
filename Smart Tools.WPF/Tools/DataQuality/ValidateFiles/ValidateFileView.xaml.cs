using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Validate Files view - UI only. Kinds, signatures, the file list,
    /// reports and settings are in <see cref="ValidateManager"/>; the checks
    /// in ValidateManager.Checks.cs / .Media.cs / .Images.cs; the scan and
    /// quarantine in ValidateManager.Run.cs.
    /// </summary>
    public partial class ValidateFileView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_validate-data_Name", "Validate Files");
        private const int SkipSizeMax = 999_999;

        private readonly ValidateManager _manager = new();

        private readonly List<ValidationSource> _sources = new();
        private readonly ObservableCollection<ValidationQueueGroup> _queueDisplay = new();
        private List<ValidateManager.QueuedFile> _queued = new();
        private ValidateManager.QueueResult? _lastQueue;
        private ValidateManager.ListResult? _lastList;

        /// <summary>Removed with "Remove selected" - stays removed when the list is rebuilt (the old view brought them back).</summary>
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

        private ObservableCollection<FileValidationResult> _rows = new();
        private ListCollectionView? _view;
        private ValidationRunResult? _lastRun;

        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _expandCts;
        private Task? _expandTask;
        private ValidationSettings _settings = new();
        private string _quarantineFolder = "";

        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        public ValidateFileView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _queueDisplay;
            _viewConstructed = true;
            SetRows(new ObservableCollection<FileValidationResult>());

            // Only the rows on screen change (the "Only problems" filter hides the rest).
            BulkCheckMenu.Append(DataGridResults.ContextMenu, DataGridResults,
                () => _view?.Cast<FileValidationResult>() ?? _rows,
                r => r.IsSelected, (r, v) => r.IsSelected = v,
                UpdateTickButton, canRun: () => !_isRunning);
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a scan (and its ffmpeg processes) and any folder listing.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _expandCts?.Cancel();
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(ValidateManager.LoadSettings());
                _loadedOnce = true;
            }
            ApplyResponsive(ActualWidth);
            UpdateQueueHint();
            UpdateSummary();
            UpdateEmptyState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsive(e.NewSize.Width);

        private void ApplyResponsive(double width)
        {
            ColDetails.Visibility = width < 980 ? Visibility.Collapsed : Visibility.Visible;
            ColFileType.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;
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
        //  Files and folders
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => AddSources(e.Paths);

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) InputCard.SetDropHighlight(true);
            e.Handled = true;
        }

        private void Root_DragLeave(object sender, DragEventArgs e) => InputCard.SetDropHighlight(false);

        private void Root_Drop(object sender, DragEventArgs e)
        {
            InputCard.SetDropHighlight(false);
            if (!_isRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddSources(paths);
            e.Handled = true;
        }

        private void AddSources(IEnumerable<string> paths)
        {
            bool added = false;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) continue;
                // Adding again brings back files that were removed from the list (the folder or file itself, and everything under it).
                added |= Unhide(path);
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new ValidationSource { FullPath = path, IsFolder = isFolder });
                added = true;
            }
            if (!added) return;
            InputCard.PopDropArea();
            _ = RefreshQueueAsync();
        }

        /// <summary>Forgets "removed" rows for a path and everything below it. True when something was restored.</summary>
        private bool Unhide(string path)
        {
            string prefix = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return _removed.RemoveWhere(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || sender is not Button { Tag: ValidationQueueGroup group }) return;
            if (group.IsFolder)
            {
                _sources.RemoveAll(s => s.IsFolder && string.Equals(s.FullPath, group.FolderPath, StringComparison.OrdinalIgnoreCase));
                Unhide(group.FolderPath);
            }
            else
            {
                _sources.RemoveAll(s => !s.IsFolder && group.FilePaths.Contains(s.FullPath, StringComparer.OrdinalIgnoreCase));
                foreach (string file in group.FilePaths) Unhide(file);
            }
            if (_sources.Count == 0) _removed.Clear();
            _ = RefreshQueueAsync();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isRunning) return;
            _expandCts?.Cancel();
            _sources.Clear();
            _queued = new();
            _lastQueue = null;
            _removed.Clear();
            _lastRun = null;
            RebuildQueueDisplay();
            RebuildList();
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            if (_sources.Any(s => s.IsFolder)) _ = RefreshQueueAsync();
            else RebuildQueueDisplay();
        }

        private void RebuildQueueDisplay()
        {
            _queueDisplay.Clear();
            foreach (var group in ValidateManager.BuildQueueGroups(_sources, InputCard.IncludeSubfolders))
                _queueDisplay.Add(group);
            UpdateQueueHint();
        }

        private void UpdateQueueHint() => InputCard.SetEmpty(_queueDisplay.Count == 0);

        /// <summary>Reads the added folders off the UI thread, then lists the files (the old view listed them on the UI thread).</summary>
        private Task RefreshQueueAsync()
        {
            RebuildQueueDisplay();
            _expandCts?.Cancel();
            var cts = _expandCts = new CancellationTokenSource();
            _expandTask = ExpandAsync(cts);
            return _expandTask;
        }

        private async Task ExpandAsync(CancellationTokenSource cts)
        {
            if (_sources.Count == 0)
            {
                _queued = new();
                _lastQueue = null;
                RebuildList();
                return;
            }

            if (_sources.Any(s => s.IsFolder))
            {
                ProgressValidation.IsIndeterminate = true;
                TxtProgress.Text = Loc.T("Str_DataQuality_MsgReadingFolders", "Reading folders...");
            }
            try
            {
                var result = await ValidateManager.ExpandQueueAsync(_sources.ToList(), InputCard.IncludeSubfolders, cts.Token);
                if (cts.IsCancellationRequested) return;
                _queued = result.Files;
                _lastQueue = result;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"Validate: listing failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_expandCts, cts)) ProgressValidation.IsIndeterminate = false;
            }
            _lastRun = null;
            RebuildList();
        }

        // =================================================================
        //  Settings controls
        // =================================================================

        private ValidationType SelectedType =>
            TypeTiles.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out ValidationType t) ? t : ValidationType.Auto;

        private static T ComboTag<T>(ComboBox combo, T fallback) where T : struct, Enum =>
            (combo.SelectedItem as ComboBoxItem)?.Tag is string s && Enum.TryParse(s, out T v) ? v : fallback;

        private static void SelectTag(ComboBox combo, string tag)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
            if (item != null) combo.SelectedItem = item;
        }

        private int SkipSizeValue => int.TryParse(TxtSkipSizeValue.Text, out int v) ? Math.Clamp(v, 0, SkipSizeMax) : 0;

        private string EffectiveQuarantineFolder =>
            string.IsNullOrWhiteSpace(_quarantineFolder) ? ValidateManager.DefaultQuarantineFolder : _quarantineFolder;

        /// <summary>Everything the sidebar says. Read by Tag, not position (the old view cast the combo index to the enum).</summary>
        private ValidationOptions BuildOptions() => new()
        {
            Type = SelectedType,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            SkipFilesLargerThanBytes = ValidateManager.SkipBytes(SkipSizeValue, ComboTag(CmbSkipSizeUnit, SizeUnit.MB)),

            AutoVideo = ChkAutoVideo.IsChecked == true,
            AutoImage = ChkAutoImage.IsChecked == true,
            AutoAudio = ChkAutoAudio.IsChecked == true,
            AutoDocument = ChkAutoDocument.IsChecked == true,
            AutoArchive = ChkAutoArchive.IsChecked == true,

            VideoCheckContainer = ChkVideoContainer.IsChecked == true,
            VideoCheckStream = ChkVideoStream.IsChecked == true,
            VideoCheckAudioStream = ChkVideoAudioStream.IsChecked == true,
            VideoCheckDuration = ChkVideoDuration.IsChecked == true,
            VideoCheckFrames = ChkVideoFrames.IsChecked == true,
            VideoScanDepth = ComboTag(CmbVideoScanDepth, VideoScanDepth.Standard),

            ImageCheckHeader = ChkImageHeader.IsChecked == true,
            ImageFullDecode = ChkImageFullDecode.IsChecked == true,
            ImageCheckTruncated = ChkImageTruncated.IsChecked == true,
            ImageCheckExif = ChkImageExif.IsChecked == true,
            ImageFlagSmall = ChkImageSmall.IsChecked == true,

            AudioCheckHeader = ChkAudioHeader.IsChecked == true,
            AudioFullDecode = ChkAudioFullDecode.IsChecked == true,
            AudioCheckTruncated = ChkAudioTruncated.IsChecked == true,
            AudioCheckDuration = ChkAudioDuration.IsChecked == true,
            AudioFlagSilent = ChkAudioSilent.IsChecked == true,

            DocCheckStructure = ChkDocStructure.IsChecked == true,
            DocVerifyOpens = ChkDocOpens.IsChecked == true,
            DocCheckEmbeddedImages = ChkDocEmbeddedImages.IsChecked == true,
            DocCheckEmbeddedFonts = ChkDocEmbeddedFonts.IsChecked == true,
            DocFlagPassword = ChkDocPassword.IsChecked == true,

            ArchiveVerifyCrc = ChkArchiveCrc.IsChecked == true,
            ArchiveTestExtract = ChkArchiveExtract.IsChecked == true,
            ArchiveFlagPassword = ChkArchivePassword.IsChecked == true,
            ArchiveCheckBomb = ChkArchiveBomb.IsChecked == true,
        };

        private void RadioType_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateTypeUi(animate: true);
            SaveSettings();
            _lastRun = null;
            RebuildList();   // a chosen type lists only files of that kind
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateTypeUi(animate: false);
            SaveSettings();
        }

        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore) return;
            UpdateTypeUi(animate: false);
            SaveSettings();
            if (sender == CmbSkipSizeUnit) RebuildList();
        }

        private void Setting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            UpdateTypeUi(animate: false);
            SaveSettings();
            RebuildList();
        }

        private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

        private void BtnSkipSizeUp_Click(object sender, RoutedEventArgs e) => AdjustSkipSize(1);
        private void BtnSkipSizeDown_Click(object sender, RoutedEventArgs e) => AdjustSkipSize(-1);

        private void AdjustSkipSize(int delta) =>
            TxtSkipSizeValue.Text = Math.Clamp(SkipSizeValue + delta, 0, SkipSizeMax).ToString();

        private void BtnBrowseQuarantine_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("Str_Validate_MsgQuarantineTitle", "Move corrupted files to"), Multiselect = false };
            if (Directory.Exists(EffectiveQuarantineFolder)) dialog.InitialDirectory = EffectiveQuarantineFolder;
            if (dialog.ShowDialog() != true) return;
            _quarantineFolder = dialog.FolderName;
            UpdateTypeUi(animate: false);
            SaveSettings();
        }

        private void BtnResetQuarantine_Click(object sender, RoutedEventArgs e)
        {
            _quarantineFolder = "";
            UpdateTypeUi(animate: false);
            SaveSettings();
        }

        /// <summary>The chosen kind's settings, hints, the ffmpeg warning and option notes.</summary>
        private void UpdateTypeUi(bool animate)
        {
            var type = SelectedType;
            void Show(FrameworkElement panel, bool visible)
            {
                var v = visible ? Visibility.Visible : Visibility.Collapsed;
                if (panel.Visibility == v) return;
                panel.Visibility = v;
                if (visible && animate) UiMotion.FadeIn(panel, 4);
            }
            Show(PanelAutoDetect, type == ValidationType.Auto);
            Show(PanelVideo, type == ValidationType.Video);
            Show(PanelImage, type == ValidationType.Image);
            Show(PanelAudio, type == ValidationType.Audio);
            Show(PanelDocument, type == ValidationType.Document);
            Show(PanelArchive, type == ValidationType.Archive);
            TxtTypeHint.Text = ValidateManager.TypeHint(type);

            // Scan depth only matters when frames are checked.
            PanelScanDepth.IsEnabled = ChkVideoFrames.IsChecked == true;
            PanelScanDepth.Opacity = PanelScanDepth.IsEnabled ? 1 : 0.5;

            bool mediaMatters = type is ValidationType.Auto or ValidationType.Video or ValidationType.Audio;
            TxtFfmpegWarning.Text = $"ffmpeg wasn't found in {ValidateManager.FfmpegFolder}. Videos and audio get header checks only.";
            FfmpegWarning.Visibility = mediaMatters && !ValidateManager.HasFfmpeg ? Visibility.Visible : Visibility.Collapsed;

            long skip = ValidateManager.SkipBytes(SkipSizeValue, ComboTag(CmbSkipSizeUnit, SizeUnit.MB));
            TxtSkipHint.Text = skip > 0
                ? Loc.F("Str_Validate_MsgSkipHint", "Files over {0} are listed as Skipped.", ValidateText.FormatBytes(skip))
                : Loc.T("Str_Validate_MsgNoLimit", "No size limit - every file is checked.");

            UiMotion.FadeVisibility(PanelQuarantineFolder, ChkQuarantine.IsChecked == true);
            TxtQuarantineFolder.Text = EffectiveQuarantineFolder;
            TxtQuarantineFolder.ToolTip = EffectiveQuarantineFolder;
            BtnResetQuarantine.IsEnabled = !string.IsNullOrWhiteSpace(_quarantineFolder);

            TxtReportHint.Text = Loc.F("Str_Validate_MsgReportSavedTo", "Saved to {0}", ValidateManager.ReportsFolder);
            UiMotion.FadeVisibility(TxtReportHint, ChkGenerateReport.IsChecked == true);
        }

        // ---------------- Settings ----------------

        private void ApplySettings(ValidationSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                var o = s.Options;
                foreach (var radio in TypeTiles.Children.OfType<RadioButton>())
                    radio.IsChecked = (string)radio.Tag == o.Type.ToString();

                InputCard.IncludeSubfolders = o.IncludeSubfolders;
                ChkAutoVideo.IsChecked = o.AutoVideo;
                ChkAutoImage.IsChecked = o.AutoImage;
                ChkAutoAudio.IsChecked = o.AutoAudio;
                ChkAutoDocument.IsChecked = o.AutoDocument;
                ChkAutoArchive.IsChecked = o.AutoArchive;

                ChkVideoContainer.IsChecked = o.VideoCheckContainer;
                ChkVideoStream.IsChecked = o.VideoCheckStream;
                ChkVideoAudioStream.IsChecked = o.VideoCheckAudioStream;
                ChkVideoDuration.IsChecked = o.VideoCheckDuration;
                ChkVideoFrames.IsChecked = o.VideoCheckFrames;
                SelectTag(CmbVideoScanDepth, o.VideoScanDepth.ToString());

                ChkImageHeader.IsChecked = o.ImageCheckHeader;
                ChkImageFullDecode.IsChecked = o.ImageFullDecode;
                ChkImageTruncated.IsChecked = o.ImageCheckTruncated;
                ChkImageExif.IsChecked = o.ImageCheckExif;
                ChkImageSmall.IsChecked = o.ImageFlagSmall;

                ChkAudioHeader.IsChecked = o.AudioCheckHeader;
                ChkAudioFullDecode.IsChecked = o.AudioFullDecode;
                ChkAudioTruncated.IsChecked = o.AudioCheckTruncated;
                ChkAudioDuration.IsChecked = o.AudioCheckDuration;
                ChkAudioSilent.IsChecked = o.AudioFlagSilent;

                ChkDocStructure.IsChecked = o.DocCheckStructure;
                ChkDocOpens.IsChecked = o.DocVerifyOpens;
                ChkDocEmbeddedImages.IsChecked = o.DocCheckEmbeddedImages;
                ChkDocEmbeddedFonts.IsChecked = o.DocCheckEmbeddedFonts;
                ChkDocPassword.IsChecked = o.DocFlagPassword;

                ChkArchiveCrc.IsChecked = o.ArchiveVerifyCrc;
                ChkArchiveExtract.IsChecked = o.ArchiveTestExtract;
                ChkArchivePassword.IsChecked = o.ArchiveFlagPassword;
                ChkArchiveBomb.IsChecked = o.ArchiveCheckBomb;

                TxtSkipSizeValue.Text = s.SkipSizeValue.ToString();
                SelectTag(CmbSkipSizeUnit, s.SkipSizeUnit.ToString());
                ChkQuarantine.IsChecked = s.QuarantineAfterScan;
                _quarantineFolder = s.QuarantineFolder;
                ChkGenerateReport.IsChecked = s.ReportAfterScan;
                ChkOnlyProblems.IsChecked = s.OnlyProblems;

                UpdateTypeUi(animate: false);
                ApplyFilter();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            _settings.Options = BuildOptions();
            _settings.SkipSizeValue = SkipSizeValue;
            _settings.SkipSizeUnit = ComboTag(CmbSkipSizeUnit, SizeUnit.MB);
            _settings.QuarantineAfterScan = ChkQuarantine.IsChecked == true;
            _settings.QuarantineFolder = _quarantineFolder;
            _settings.ReportAfterScan = ChkGenerateReport.IsChecked == true;
            _settings.OnlyProblems = ChkOnlyProblems.IsChecked == true;
            try
            {
                ValidateManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Validate settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  The result list
        // =================================================================

        /// <summary>A new collection per list, with a live filter so "Only problems" follows statuses as they change.</summary>
        private void SetRows(ObservableCollection<FileValidationResult> rows)
        {
            _rows = rows;
            _view = new ListCollectionView(_rows) { IsLiveFiltering = true };
            _view.LiveFilteringProperties.Add(nameof(FileValidationResult.Status));
            ApplyFilter();
            DataGridResults.ItemsSource = _view;
        }

        private void ApplyFilter()
        {
            if (_view == null) return;
            _view.Filter = ChkOnlyProblems.IsChecked == true ? o => o is FileValidationResult { IsProblem: true } : null;
        }

        private void ChkOnlyProblems_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            ApplyFilter();
            SaveSettings();
            UpdateEmptyState();
        }

        /// <summary>Rows from the listed files (no disk access): shown at once, filled in by the scan.</summary>
        private void RebuildList()
        {
            if (_isRunning || !_viewConstructed) return;
            _lastList = ValidateManager.BuildList(_queued, BuildOptions(), _removed);
            SetRows(new ObservableCollection<FileValidationResult>(_lastList.Items));
            UpdateSummary();
            UpdateEmptyState();
            UpdateReadyText();
        }

        private void UpdateReadyText()
        {
            if (_isRunning) return;
            ProgressValidation.IsIndeterminate = false;
            ProgressValidation.Value = 0;
            TxtProgressPercent.Text = "";
            if (_rows.Count == 0 && _sources.Count == 0)
            {
                TxtProgress.Text = Loc.T("Str_Validate_MsgAddThenValidate", "Add files or folders, then press Validate.");
                return;
            }

            int pending = _rows.Count(ValidateManager.IsPending);
            var notes = new List<string> { $"{pending:N0} file{(pending == 1 ? "" : "s")} ready to check" };
            int skipped = _rows.Count(r => r.Status == ValidationStatus.Skipped);
            if (skipped > 0) notes.Add($"{skipped:N0} over the size limit");
            if (_lastList?.OtherType > 0) notes.Add($"{_lastList.OtherType:N0} of other kinds left out");
            if (_lastQueue?.Inaccessible > 0) notes.Add($"{_lastQueue.Inaccessible:N0} folders couldn't be read");
            if (_lastQueue?.Links > 0) notes.Add($"{_lastQueue.Links:N0} linked folders not followed");
            if (_lastQueue?.Missing > 0) notes.Add($"{_lastQueue.Missing:N0} no longer exist");
            TxtProgress.Text = string.Join(" · ", notes);
        }

        private void UpdateSummary()
        {
            int valid = 0, warning = 0, corrupted = 0, unknown = 0;
            foreach (var r in _rows)
            {
                switch (r.Status)
                {
                    case ValidationStatus.Valid: valid++; break;
                    case ValidationStatus.Warning: warning++; break;
                    case ValidationStatus.Corrupted: corrupted++; break;
                    case ValidationStatus.Unknown: unknown++; break;
                }
            }
            int checkedCount = valid + warning + corrupted + unknown;
            TxtSummaryChecked.Text = checkedCount > 0
                ? $"{checkedCount:N0} of {_rows.Count:N0} checked"
                : $"{_rows.Count:N0} file{(_rows.Count == 1 ? "" : "s")} · {ValidateText.FormatBytes(_rows.Sum(r => r.SizeBytes))}";
            TxtSummaryValid.Text = $"{valid:N0} valid";
            TxtSummaryCorrupted.Text = $"{corrupted:N0} corrupted";
            TxtSummaryWarning.Text = $"{warning:N0} warning{(warning == 1 ? "" : "s")}";
            TxtSummaryUnknown.Text = $"{unknown:N0} couldn't check";
            PillValid.Visibility = valid > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillCorrupted.Visibility = corrupted > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillWarning.Visibility = warning > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillUnknown.Visibility = unknown > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool show = _rows.Count > 0;
            bool firstShow = show && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
            UpdateTickButton();
        }

        private void UpdateEmptyState()
        {
            bool empty = _view == null || _view.IsEmpty;
            EmptyStatePanel.Description =
                _rows.Count > 0 ? Loc.T("Str_Validate_MsgNoProblems", "No problems to show - untick \"Only problems\" to see every file.") :
                _sources.Count > 0 ? Loc.F("Str_Validate_MsgNoneOfType", "None of the added files are {0} files. Pick All to check everything.", SelectedType.ToString().ToLowerInvariant()) :
                Loc.T("Str_Validate_MsgDropHere", "Drop files or folders here (or on the left), then press Validate to find damaged, cut-off and unreadable files.");
            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            DataGridResults.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;   // hidden until there's something to list
        }

        // ---------------- Ticks, remove, quarantine ----------------

        private void UpdateTickButton()
        {
            bool anyTicked = _rows.Any(r => r.IsSelected);
            // Icon-only button: "tick" icon when nothing is ticked, "cleared" icon once something is.
            IconTickProblems.Kind = anyTicked
                ? PackIconKind.CheckboxMultipleBlankOutline
                : PackIconKind.CheckboxMultipleMarkedOutline;
            UpdateActions();
        }

        /// <summary>Like the Duplicate view: actions wait for results; Quarantine / Remove need something ticked or selected.</summary>
        private void UpdateActions()
        {
            ActionsPanel.IsEnabled = _rows.Count > 0 && !_isRunning;
            BtnRemoveFiles.IsEnabled = _rows.Any(r => r.IsSelected) || DataGridResults.SelectedItems.Count > 0;
            BtnQuarantine.IsEnabled = QuarantineTargets().Count > 0;
        }

        private void DataGridResults_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

        private void BtnTickProblems_Click(object sender, RoutedEventArgs e)
        {
            // Only what's on screen counts (rows hidden by "Only problems" can't hold a stale tick).
            var visible = (_view?.Cast<FileValidationResult>() ?? _rows).ToHashSet();
            bool anyTicked = visible.Any(r => r.IsSelected);
            foreach (var r in _rows)
                r.IsSelected = !anyTicked && visible.Contains(r) && r.Status == ValidationStatus.Corrupted && r.QuarantinedTo == null;
            UpdateTickButton();
        }

        private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateTickButton();

        /// <summary>Ticked rows, or the grid's selected rows when nothing is ticked.</summary>
        private List<FileValidationResult> TargetRows()
        {
            var ticked = _rows.Where(r => r.IsSelected).ToList();
            return ticked.Count > 0 ? ticked : DataGridResults.SelectedItems.Cast<FileValidationResult>().ToList();
        }

        /// <summary>What Quarantine may move: ticked (or selected) rows that are damaged and not already moved - never valid, skipped or unchecked files.</summary>
        private List<FileValidationResult> QuarantineTargets() =>
            TargetRows().Where(r => r.QuarantinedTo == null && r.Status is ValidationStatus.Corrupted or ValidationStatus.Warning).ToList();

        private void BtnRemoveFiles_Click(object sender, RoutedEventArgs e) => RemoveRows(TargetRows());

        private void RemoveRows(List<FileValidationResult> rows)
        {
            if (_isRunning || rows.Count == 0) return;
            foreach (var r in rows)
            {
                _removed.Add(r.FullPath);
                _rows.Remove(r);
            }
            UpdateSummary();
            UpdateEmptyState();
            UpdateReadyText();
        }

        private void DataGridResults_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                RemoveRows(DataGridResults.SelectedItems.Cast<FileValidationResult>().ToList());
                e.Handled = true;
            }
            else if (e.Key == Key.Space && DataGridResults.SelectedItems.Count > 0 && Keyboard.FocusedElement is not System.Windows.Controls.CheckBox)
            {
                var selected = DataGridResults.SelectedItems.Cast<FileValidationResult>().ToList();
                bool tick = selected.Any(r => !r.IsSelected);
                foreach (var r in selected) r.IsSelected = tick;
                UpdateTickButton();
                e.Handled = true;
            }
        }

        private async void BtnQuarantine_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            var rows = QuarantineTargets();
            if (rows.Count == 0)
            {
                ThemedMessageBox.Show(Loc.T("Str_MB_NoDamagedFilesTo", "No damaged files to move. Tick corrupted or warning files first (\"Tick corrupted\" ticks every damaged one)."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await QuarantineAsync(rows, ask: true);
        }

        private async Task QuarantineAsync(List<FileValidationResult> rows, bool ask)
        {
            string folder = EffectiveQuarantineFolder;
            if (ask && ThemedMessageBox.Show(
                    Loc.N(rows.Count,
                        "Str_MB_QuarantineMoveOne", "Move {0:N0} file to\n{1}?\n\nNothing is deleted. A log in that folder lists where each file came from, so you can put it back.",
                        "Str_MB_QuarantineMoveMany", "Move {0:N0} files to\n{1}?\n\nNothing is deleted. A log in that folder lists where each file came from, so you can put it back.",
                        rows.Count, folder),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                var result = await ValidateManager.QuarantineAsync(rows, folder);
                foreach (var r in rows.Where(r => r.QuarantinedTo != null)) _removed.Add(r.FullPath);   // not listed again on the next scan
                TxtProgress.Text = Loc.N(result.Moved, "Str_MB_MovedToQuarantineOne", "Moved {0:N0} file to quarantine", "Str_MB_MovedToQuarantineMany", "Moved {0:N0} files to quarantine", result.Moved)
                    + (result.Failed.Count > 0 ? Loc.F("Str_MB_QuarantineFailedSuffix", " · {0:N0} couldn't be moved", result.Failed.Count) : "");
                if (result.Failed.Count > 0)
                    ThemedMessageBox.Show(Loc.F("Str_MB_FilesCouldntBeMoved", "{0:N0} file(s) couldn't be moved:\n", result.Failed.Count) + string.Join("\n", result.Failed.Take(10)),
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateTickButton();
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(Loc.F("Str_MB_CouldnTUseThe", "Couldn't use the quarantine folder: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------------- Report ----------------

        private void MenuExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (!_rows.Any(r => r.Status is ValidationStatus.Valid or ValidationStatus.Warning or ValidationStatus.Corrupted or ValidationStatus.Unknown))
            {
                ThemedMessageBox.Show(Loc.T("Str_MB_RunAScanFirst", "Run a scan first - there are no results to export yet."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = Loc.T("Str_Validate_MsgExportTitle", "Export validation report"),
                Filter = Loc.T("Str_DataQuality_MsgCsvFilter", "CSV file (*.csv)|*.csv"),
                FileName = $"Validation report {DateTime.Now:yyyy-MM-dd HH-mm}.csv",
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                // What's on screen: respects "Only problems".
                ValidateManager.SaveReport(_view?.Cast<FileValidationResult>().ToList() ?? _rows.ToList(), dialog.FileName);
                ToastNotificationManager.ShowToast(Loc.T("Str_DataQuality_MsgExportComplete", "Export Complete"), Loc.T("Str_Validate_MsgReportSaved", "Report saved."));
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(Loc.F("Str_MB_CouldNotSaveThe2", "Could not save the report: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------------- Context menu / double-click ----------------

        private FileValidationResult? _menuRow;

        private void DataGridResults_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? current = e.OriginalSource as DependencyObject;
            while (current != null && current is not DataGridRow)
                current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);

            _menuRow = (current as DataGridRow)?.Item as FileValidationResult;
            if (_menuRow != null && !DataGridResults.SelectedItems.Contains(_menuRow))
                DataGridResults.SelectedItem = _menuRow;
        }

        private void DataGridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridResults.SelectedItem is FileValidationResult row) ShowInFolder(row);
        }

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((_menuRow ?? DataGridResults.SelectedItem as FileValidationResult) is { } row) ShowInFolder(row);
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var rows = DataGridResults.SelectedItems.Cast<FileValidationResult>().ToList();
            if (rows.Count > 0) CopyText(string.Join(Environment.NewLine, rows.Select(r => r.CurrentPath)));
        }

        private void MenuCopyDetails_Click(object sender, RoutedEventArgs e)
        {
            var rows = DataGridResults.SelectedItems.Cast<FileValidationResult>().ToList();
            if (rows.Count > 0) CopyText(string.Join(Environment.NewLine, rows.Select(r => $"{r.CurrentPath}\t{r.StatusText}\t{r.Details}")));
        }

        private static void CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log.Info($"Clipboard busy: {ex.Message}");   // another program holds it - not worth an error box
            }
        }

        /// <summary>The app's shared Explorer helper (replaces this view's own Shell.Application COM code).</summary>
        private void ShowInFolder(FileValidationResult row)
        {
            string path = File.Exists(row.CurrentPath) ? row.CurrentPath : row.Folder;
            if (ShellExplorer.Show(path) != ShellExplorer.ShowResult.Shown)
                ThemedMessageBox.Show(Loc.T("Str_MB_ThatFileOrFolder2", "That file or folder is no longer there."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Validate
        // =================================================================

        private async void BtnValidate_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = Loc.T("Str_Validate_MsgCancelling", "Cancelling - finishing the files being checked...");
                return;
            }

            if (_expandTask is { IsCompleted: false }) await _expandTask;

            // Validate again: re-check everything that was checked before.
            if (!_rows.Any(ValidateManager.IsPending) && _rows.Any(r => r.Status != ValidationStatus.Skipped))
            {
                _lastRun = null;
                RebuildList();
            }

            var pending = _rows.Where(ValidateManager.IsPending).ToList();
            if (pending.Count == 0)
            {
                InputCard.PopDropArea();
                ThemedMessageBox.Show(_rows.Count == 0
                        ? Loc.T("Str_Validate_MsgAddFirst", "Add some files or a folder first (left side, or drop them anywhere here).")
                        : Loc.T("Str_Validate_MsgAllOverLimit", "Every listed file is over the size limit - raise or clear the limit in Options."),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = BuildOptions();
            options.MaxDegreeOfParallelism = ChooseParallelism();
            SaveSettings();

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetRunning(true);

            try
            {
                var progress = new Progress<ValidationProgress>(ReportProgress);
                var result = await _manager.RunAsync(_rows.ToList(), options, progress, _runCts.Token);
                _lastRun = result;

                SetRunning(false);
                ProgressValidation.Value = result.Cancelled > 0 ? ProgressValidation.Value : 100;
                TxtProgressPercent.Text = result.Cancelled > 0 ? "" : "100%";
                TxtProgress.Text = ValidateManager.DescribeRun(result);

                try
                {
                    ToastNotificationManager.ShowToast(Loc.T("Str_Validate_MsgToastTitle", "Validation Complete"),
                        result.Corrupted > 0
                            ? Loc.F("Str_Validate_MsgToastBad", "Checked {0:N0} files - {1:N0} corrupted.", result.Checked, result.Corrupted)
                            : Loc.F("Str_Validate_MsgToastGood", "Checked {0:N0} files - no damaged files found.", result.Checked));
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }

                if (ChkGenerateReport.IsChecked == true)
                {
                    try
                    {
                        string path = ValidateManager.SaveReport(_rows);
                        TxtProgress.Text += " · report saved";
                        TxtProgress.ToolTip = path;
                    }
                    catch (Exception ex)
                    {
                        ThemedMessageBox.Show(Loc.F("Str_MB_TheReportCouldnT", "The report couldn't be saved: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                if (ChkQuarantine.IsChecked == true && result.Cancelled == 0)
                {
                    var corrupted = _rows.Where(r => r.Status == ValidationStatus.Corrupted && r.QuarantinedTo == null).ToList();
                    if (corrupted.Count > 0) await QuarantineAsync(corrupted, ask: true);
                }
            }
            catch (Exception ex)
            {
                SetRunning(false);
                TxtProgress.Text = Loc.T("Str_Validate_MsgStopped", "Validation stopped - something went wrong.");
                ThemedMessageBox.Show(Loc.F("Str_MB_ValidationFailed", "Validation failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isRunning) SetRunning(false);
                UpdateSummary();
                UpdateEmptyState();
            }
        }

        /// <summary>SSD: many files at once. Hard disks, network and unknown: 2 (seeking kills throughput).</summary>
        private int ChooseParallelism()
        {
            var folders = _rows.Select(r => Path.GetPathRoot(r.FullPath) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            bool solidState = folders.Count > 0 && folders.All(f =>
            {
                try
                {
                    return DriveHealthManager.GetMediaKindForPath(f) is DriveKind.Ssd or DriveKind.StorageClassMemory;
                }
                catch (Exception)   // a probe for a speed hint only: whatever the drive query throws, fall back to the safe setting
                {
                    return false;
                }
            });
            return ValidateManager.DefaultParallelism(solidState);
        }

        private void ReportProgress(ValidationProgress p)
        {
            if (!_isRunning) return;
            ProgressValidation.IsIndeterminate = false;
            ProgressValidation.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:0}%";
            TxtProgress.Text = $"{p.Done:N0} of {p.Total:N0} checked" + (string.IsNullOrEmpty(p.CurrentFile) ? "" : $" · {p.CurrentFile}");
            UpdateSummary();
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("validate-file");
            else { _activity?.Dispose(); _activity = null; }

            IconValidate.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.ShieldCheckOutline;
            if (running) TxtValidateLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtValidateLabel.SetResourceReference(TextBlock.TextProperty, "Str_Validate_ValidateButton");
            UiMotion.Pop(BtnValidate, 0.96);

            // Changing files or settings mid-scan used to be possible for some controls.
            OptionsScrollViewer.IsEnabled = !running;
            UpdateActions();

            if (running)
            {
                ProgressValidation.IsIndeterminate = false;
                ProgressValidation.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = Loc.T("Str_DataQuality_MsgStarting", "Starting...");
                TxtProgress.ToolTip = null;
            }
        }
    }
}
