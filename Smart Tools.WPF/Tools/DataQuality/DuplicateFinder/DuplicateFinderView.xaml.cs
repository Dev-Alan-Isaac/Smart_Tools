using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Engines;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Duplicate Finder view - UI only. Scanning, choosing keepers,
    /// moving/deleting, export and settings live in <see cref="DuplicateManager"/>;
    /// data shapes in DuplicateFinderModel.cs; "show in folder" in
    /// <see cref="ShellExplorer"/>. The grid is grouped by
    /// <see cref="DuplicateGroup"/> so each group has a header row.
    /// </summary>
    public partial class DuplicateFinderView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowColumnThreshold = 1100;
        private static string DialogTitle => Loc.T("Str_Tool_duplicate-data_Name", "Duplicate Finder");
        private const int ThumbnailSize = 320;

        private readonly DuplicateManager _manager = new();
        private readonly ObservableCollection<FolderListEntry> _folders = new();
        private ObservableCollection<DuplicateFileItem> _results = new();
        private ListCollectionView _resultsView;

        private List<DuplicateGroup> _groups = new();
        private DuplicateScanOptions? _lastOptions;
        private DuplicateFinderSettings _settings = new();

        private CancellationTokenSource? _scanCts;
        private readonly ThumbnailWindow<DuplicateFileItem> _thumbs;
        private bool _isScanning;
        private bool _isBusy;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _summaryQueued;
        private bool _lastScanVerified = true;
        private DuplicateGroup? _compareGroup;
        private DuplicateFileItem? _contextItem;
        private readonly bool _viewConstructed;

        public DuplicateFinderView()
        {
            InitializeComponent();

            InputCard.ItemsSource = _folders;
            _folders.CollectionChanged += (_, _) => UpdateFolderHint();

            _resultsView = CreateGroupedView(_results);
            DataGridDuplicates.ItemsSource = _resultsView;
            _thumbs = new(DataGridDuplicates, () => _results, i => i.Thumbnail, (i, v) => i.Thumbnail = v,
                async (i, token) => await ThumbnailService.GetThumbnailAsync(i.FullPath, ThumbnailSize, token))   // each video preview spawns ffmpeg
            {
                Supports = i => ThumbnailService.SupportsThumbnail(i.FullPath),
                Pinned = i => _compareGroup != null && _compareGroup.Files.Contains(i),
            };

            BuildResultsContextMenu();
            AttachShortcuts();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _scanCts?.Cancel();
            _thumbs.Dispose();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(DuplicateManager.LoadSettings());
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateFolderHint();
            UpdateEmptyState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width)
        {
            var visibility = width < NarrowColumnThreshold ? Visibility.Collapsed : Visibility.Visible;
            ColHash.Visibility = visibility;
            ColModified.Visibility = visibility;
        }

        // =================================================================
        //  Sidebar
        // =================================================================

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

        // ---------------- Folders ----------------

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_isScanning) AddFolders(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            _folders.Clear();
            SaveSettings();
        }

        private void BtnRemoveSingleFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: FolderListEntry entry })
            {
                _folders.Remove(entry);
                SaveSettings();
            }
        }

        private static IEnumerable<string> DroppedFolders(DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return Array.Empty<string>();
            return paths.Where(Directory.Exists);
        }

        private void AddFolders(IEnumerable<string> folders)
        {
            bool added = false;
            foreach (string folder in folders)
            {
                if (_folders.Any(f => string.Equals(f.FullPath, folder, StringComparison.OrdinalIgnoreCase))) continue;
                _folders.Add(new FolderListEntry { FullPath = folder });
                added = true;
            }
            if (added)
            {
                InputCard.PopDropArea();
                SaveSettings();
            }
        }

        private void UpdateFolderHint() =>
            InputCard.SetEmpty(_folders.Count == 0);

        // ---------------- Options ----------------

        private void RadioMatch_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool content = RadioMatchContent.IsChecked == true;
            ContentOptionsPanel.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
            UiMotion.FadeVisibility(NameModeWarning, !content);
            if (content) UiMotion.FadeIn(ContentOptionsPanel, 4);
            UpdateSameContentPanel();
            SaveSettings();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateSameContentPanel();
            SaveSettings();
        }

        /// <summary>The tolerance choice only matters for "same content, any format".</summary>
        private void UpdateSameContentPanel()
        {
            bool show = RadioMatchContent.IsChecked == true && SelectedMatchMode == DuplicateMatchMode.SameContent;
            if (show == (SameContentPanel.Visibility == Visibility.Visible)) return;
            if (_applyingSettings) SameContentPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            else UiMotion.FadeVisibility(SameContentPanel, show);
        }

        private SameContentLevel SelectedSameContentLevel =>
            (CmbSameContentLevel.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse(tag, out SameContentLevel l) ? l : SameContentLevel.FormatOnly;

        private void TxtExcludeExtensions_LostFocus(object sender, RoutedEventArgs e)
        {
            // Tidy what was typed into the canonical ".a, .b" form.
            var list = DuplicateManager.ParseExtensionList(TxtExcludeExtensions.Text);
            TxtExcludeExtensions.Text = string.Join(", ", list);
            SaveSettings();
        }

        private DuplicateMatchMode SelectedMatchMode
        {
            get
            {
                if (RadioMatchName.IsChecked == true) return DuplicateMatchMode.FileName;
                if (RadioMatchNameSize.IsChecked == true) return DuplicateMatchMode.NameAndSize;
                string tag = (CmbHashAlgorithm.SelectedItem as ComboBoxItem)?.Tag as string ?? "Sha256";
                return Enum.TryParse(tag, out DuplicateMatchMode mode) ? mode : DuplicateMatchMode.Sha256;
            }
        }

        private FileTypeFilter SelectedFileTypes =>
            new[] { RadioTypeAll, RadioTypeImages, RadioTypeVideos, RadioTypeAudio, RadioTypeDocuments }
                .FirstOrDefault(r => r.IsChecked == true)?.Tag is string tag && Enum.TryParse(tag, out FileTypeFilter f) ? f : FileTypeFilter.All;

        private RadioButton[] MinSizeRadios => new[] { RadioMinAny, RadioMin100K, RadioMin1M, RadioMin10M };

        private long SelectedMinSize =>
            long.TryParse(MinSizeRadios.FirstOrDefault(r => r.IsChecked == true)?.Tag?.ToString(), out long v) ? v : 1;

        private KeepRule SelectedKeepRule =>
            (CmbKeepRule.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse(tag, out KeepRule r) ? r : KeepRule.Oldest;

        private DuplicateScanOptions BuildOptions() => new()
        {
            Folders = _folders.Select(f => f.FullPath).ToList(),
            IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true,
            MatchMode = SelectedMatchMode,
            MinFileSizeBytes = SelectedMinSize,
            IgnoreHiddenFiles = ChkIgnoreHiddenFiles.IsChecked == true,
            IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true,
            VerifyWithByteCompare = ChkCompareFileContents.IsChecked == true,
            FileTypes = SelectedFileTypes,
            ExcludedExtensions = DuplicateManager.ParseExtensionList(TxtExcludeExtensions.Text),
            SameContentLevel = SelectedSameContentLevel,
        };

        // ---------------- Settings ----------------

        private void ApplySettings(DuplicateFinderSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                _folders.Clear();
                foreach (string f in s.Folders.Where(Directory.Exists)) _folders.Add(new FolderListEntry { FullPath = f });

                ChkIncludeSubfolders.IsChecked = s.IncludeSubfolders;
                ChkIgnoreHiddenFiles.IsChecked = s.IgnoreHiddenFiles;
                ChkIgnoreSystemFiles.IsChecked = s.IgnoreSystemFiles;
                ChkCompareFileContents.IsChecked = s.VerifyWithByteCompare;
                ChkRecycleBin.IsChecked = s.SendToRecycleBin;
                TxtExcludeExtensions.Text = s.ExcludedExtensions;

                switch (s.MatchMode)
                {
                    case DuplicateMatchMode.FileName: RadioMatchName.IsChecked = true; break;
                    case DuplicateMatchMode.NameAndSize: RadioMatchNameSize.IsChecked = true; break;
                    default:
                        RadioMatchContent.IsChecked = true;
                        SelectComboTag(CmbHashAlgorithm, s.MatchMode.ToString());
                        break;
                }

                var typeRadio = new[] { RadioTypeAll, RadioTypeImages, RadioTypeVideos, RadioTypeAudio, RadioTypeDocuments }
                    .FirstOrDefault(r => (string)r.Tag == s.FileTypes.ToString()) ?? RadioTypeAll;
                typeRadio.IsChecked = true;

                var minRadio = MinSizeRadios.FirstOrDefault(r => r.Tag?.ToString() == s.MinFileSizeBytes.ToString()) ?? RadioMinAny;
                minRadio.IsChecked = true;

                SelectComboTag(CmbKeepRule, s.KeepRule.ToString());
                SelectComboTag(CmbSameContentLevel, s.SameContentLevel.ToString());

                bool content = RadioMatchContent.IsChecked == true;
                ContentOptionsPanel.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
                NameModeWarning.Visibility = content ? Visibility.Collapsed : Visibility.Visible;
                UpdateSameContentPanel();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private static void SelectComboTag(ComboBox combo, string tag)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
            if (item != null) combo.SelectedItem = item;
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;

            _settings.Folders = _folders.Select(f => f.FullPath).ToList();
            _settings.IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true;
            _settings.IgnoreHiddenFiles = ChkIgnoreHiddenFiles.IsChecked == true;
            _settings.IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true;
            _settings.VerifyWithByteCompare = ChkCompareFileContents.IsChecked == true;
            _settings.SendToRecycleBin = ChkRecycleBin.IsChecked == true;
            _settings.MatchMode = SelectedMatchMode;
            _settings.FileTypes = SelectedFileTypes;
            _settings.MinFileSizeBytes = SelectedMinSize;
            _settings.ExcludedExtensions = TxtExcludeExtensions.Text;
            _settings.KeepRule = SelectedKeepRule;
            _settings.SameContentLevel = SelectedSameContentLevel;

            try
            {
                DuplicateManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Duplicate finder settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  Scan
        // =================================================================

        private async void ButtonScan_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                _scanCts?.Cancel();
                return;
            }

            if (_folders.Count == 0)
            {
                InputCard.PopDropArea();
                AppModal.Show(Loc.T("Str_MB_AddAtLeastOne", "Add at least one folder to search first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await RunScanAsync(BuildOptions());
        }

        private async void BtnRescan_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning || _lastOptions == null) return;
            await RunScanAsync(_lastOptions);
        }

        private async Task RunScanAsync(DuplicateScanOptions options)
        {
            SaveSettings();
            _lastOptions = options;

            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            SetScanning(true);
            _thumbs.Reset();
            CloseCompareDrawer();

            try
            {
                // Hard drives thrash with parallel reads; SSDs love them.
                options.MaxDegreeOfParallelism = await Task.Run(() => PickParallelism(options.Folders), token);

                var progress = new Progress<DuplicateScanProgress>(ReportProgress);
                var result = await _manager.ScanAsync(options, progress, token);

                _lastScanVerified = result.IsContentVerified;
                DuplicateManager.ApplyKeepRule(result.Groups, SelectedKeepRule, options.Folders, selectOthers: result.IsContentVerified, selectApproximate: false);
                ShowGroups(result.Groups);
                ShowScanSummary(result);
                _thumbs.Refresh();

                try
                {
                    ToastNotificationManager.ShowToast(Loc.T("Str_Dup_MsgToastTitle", "Duplicate Scan Complete"),
                        result.Groups.Count > 0
                            ? Loc.F("Str_Dup_MsgToastFound", "{0} files in {1} groups · {2} reclaimable.", result.DuplicateFileCount, result.Groups.Count, DuplicateText.FormatBytes(result.ReclaimableBytes))
                            : Loc.T("Str_Dup_MsgNoneFound", "No duplicates found."));
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = Loc.T("Str_DataQuality_MsgScanCancelled", "Scan cancelled.");
                ProgressDuplicates.IsIndeterminate = false;
            }
            catch (Exception ex)
            {
                TxtProgress.Text = Loc.T("Str_DataQuality_MsgScanFailed", "Scan failed.");
                AppModal.Show(Loc.F("Str_Err_ScanFailed", "Scan failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetScanning(false);
            }
        }

        private static int PickParallelism(IEnumerable<string> folders)
        {
            bool allSolidState = folders.All(f =>
                DriveHealthManager.GetMediaKindForPath(f) is DriveKind.Ssd or DriveKind.StorageClassMemory);
            return allSolidState ? Math.Max(2, Environment.ProcessorCount) : 2;
        }

        private void ReportProgress(DuplicateScanProgress p)
        {
            if (!_isScanning) return;

            if (p.Percent is double pct)
            {
                ProgressDuplicates.IsIndeterminate = false;
                ProgressDuplicates.Value = pct;
                TxtProgressPercent.Text = $"{pct:0}%";
            }
            else
            {
                ProgressDuplicates.IsIndeterminate = true;
                TxtProgressPercent.Text = "";
            }

            string phase = p.Phase switch
            {
                DuplicateScanPhase.Listing => $"Finding files · {p.FilesFound:N0} so far",
                DuplicateScanPhase.Comparing => $"Comparing {p.FilesFound:N0} files · {DuplicateText.FormatBytes(p.BytesProcessed)} of {DuplicateText.FormatBytes(p.BytesTotal)}",
                DuplicateScanPhase.Verifying => $"Double-checking matches · {DuplicateText.FormatBytes(p.BytesProcessed)} of {DuplicateText.FormatBytes(p.BytesTotal)}",
                DuplicateScanPhase.Fingerprinting => Loc.F("Str_Dup_MsgFingerprinting", "Comparing pictures, videos and songs by content · {0:N0} of {1:N0} files", p.BytesProcessed, p.BytesTotal),
                _ => "Finishing...",
            };
            TxtProgress.Text = string.IsNullOrEmpty(p.CurrentPath) ? phase : $"{phase} · {p.CurrentPath}";
        }

        private Log.ActivityScope? _activity;

        private void SetScanning(bool scanning)
        {
            _isScanning = scanning;
            if (scanning) _activity ??= Log.Activity("duplicate-finder");
            else { _activity?.Dispose(); _activity = null; }

            IconScan.Kind = scanning ? PackIconKind.StopCircleOutline : PackIconKind.ContentDuplicate;
            if (scanning) TxtScanLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtScanLabel.SetResourceReference(TextBlock.TextProperty, "Str_Dup_ScanButton");
            UiMotion.Pop(BtnScanForDuplicates, 0.96);

            OptionsScrollViewer.IsEnabled = !scanning;
            BtnRescan.IsEnabled = !scanning && _lastOptions != null;
            ActionsPanel.IsEnabled = !scanning && !_isBusy && _results.Count > 0;

            if (scanning)
            {
                ProgressDuplicates.IsIndeterminate = true;
                ProgressDuplicates.Value = 0;
                TxtProgressPercent.Text = "";
                TxtProgress.Text = Loc.T("Str_DataQuality_MsgFindingFiles", "Finding files...");
            }
            else
            {
                ProgressDuplicates.IsIndeterminate = false;
                TxtProgressPercent.Text = "";
            }
        }

        private void ShowScanSummary(DuplicateScanResult result)
        {
            ProgressDuplicates.Value = 100;
            var notes = new List<string>();
            if (result.InaccessibleFolders > 0) notes.Add(Loc.F("Str_DataQuality_MsgFoldersUnreadable", "{0:N0} folders unreadable", result.InaccessibleFolders));
            if (result.UnreadableFiles > 0) notes.Add(Loc.F("Str_DataQuality_MsgFilesUnreadable", "{0:N0} files unreadable", result.UnreadableFiles));
            if (result.SkippedLinks > 0) notes.Add(Loc.F("Str_DataQuality_MsgLinksSkipped", "{0:N0} links not followed", result.SkippedLinks));
            if (result.MediaSkippedNoFfmpeg) notes.Add(Loc.T("Str_Dup_MsgNoFfmpeg", "videos and songs compared byte for byte only (ffmpeg not found)"));
            if (result.AudioLimited) notes.Add(Loc.F("Str_Dup_MsgAudioLimited", "only the first {0:N0} songs were compared by sound", 6000));
            if (result.HeavyLimited) notes.Add(Loc.F("Str_Dup_MsgHeavyLimited", "only the first {0:N0} pictures were compared for crops and rotations", 800));
            string extra = notes.Count > 0 ? " · " + string.Join(" · ", notes) : "";
            TxtProgress.Text = Loc.F("Str_DataQuality_MsgChecked", "Checked {0:N0} files in {1:0.0} s{2}", result.FilesScanned, result.Elapsed.TotalSeconds, extra);

            NotVerifiedPill.Visibility = result.IsContentVerified ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Results
        // =================================================================

        /// <summary>A view over the results grouped by <see cref="DuplicateGroup"/> (one header row per group).</summary>
        private static ListCollectionView CreateGroupedView(ObservableCollection<DuplicateFileItem> source)
        {
            var view = new ListCollectionView(source);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DuplicateFileItem.GroupInfo)));
            return view;
        }

        private void ShowGroups(List<DuplicateGroup> groups)
        {
            foreach (var item in _results) item.PropertyChanged -= Item_PropertyChanged;

            _groups = groups;

            // Build the list first, then hand the grid a fresh grouped view in
            // one step. Adding items to a ListCollectionView's source while
            // its refresh is deferred throws ("Cannot change or check the
            // contents or Current position of CollectionView while Refresh
            // is being deferred"), and adding them one by one without
            // deferring regroups the grid on every item.
            var items = groups.SelectMany(g => g.Files).ToList();
            foreach (var item in items) item.PropertyChanged += Item_PropertyChanged;

            _results = new ObservableCollection<DuplicateFileItem>(items);
            _resultsView = CreateGroupedView(_results);
            DataGridDuplicates.ItemsSource = _resultsView;

            UpdateSummary();
            UpdateEmptyState();
            if (_results.Count > 0) UiMotion.FadeIn(DataGridDuplicates, 6);
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DuplicateFileItem.IsSelected)) return;

            // Coalesce: auto-select flips thousands at once.
            if (_summaryQueued) return;
            _summaryQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _summaryQueued = false;
                UpdateSummary();
                if (_compareGroup != null) RefreshCompareLabels();
            }));
        }

        private void UpdateSummary()
        {
            bool any = _groups.Count > 0;
            bool firstShow = any && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ActionsPanel.IsEnabled = any && !_isScanning && !_isBusy;
            if (!any) return;

            int files = _groups.Sum(g => g.FileCount);
            long reclaimable = _groups.Sum(g => g.PotentialBytes);
            TxtSummaryCount.Text = $"{files:N0} files in {_groups.Count:N0} groups";
            TxtSummaryWasted.Text = $"{DuplicateText.FormatBytes(reclaimable)} reclaimable";

            int selected = _groups.Sum(g => g.SelectedCount);
            long selectedBytes = _groups.Sum(g => g.SelectedBytes);
            SelectionPill.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSelection.Text = $"{selected:N0} selected · {DuplicateText.FormatBytes(selectedBytes)}";
            BtnMoveSelected.IsEnabled = BtnDeleteSelected.IsEnabled = selected > 0;

            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private void UpdateEmptyState()
        {
            bool empty = _results.Count == 0;
            if (empty && _lastOptions != null && !_isScanning)
                EmptyStatePanel.Description = Loc.T("Str_Dup_MsgNoneTry", "No duplicates found. Try another match mode, more folders or fewer filters.");
            else if (empty)
                EmptyStatePanel.SetResourceReference(Controls.EmptyStateCard.DescriptionProperty, "Str_Dup_ResultsHint");

            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            DataGridDuplicates.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------------- Selection helpers ----------------

        private void BtnAutoSelect_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = BtnAutoSelect, Placement = PlacementMode.Bottom };
            void Add(string text, PackIconKind icon, KeepRule rule)
            {
                var item = new MenuItem { Header = MenuHeader(text, icon) };
                item.Click += (_, _) => AutoSelect(rule);
                menu.Items.Add(item);
            }
            Add(Loc.T("Str_Dup_KeepOldest", "Keep the oldest copy"), PackIconKind.SortClockAscendingOutline, KeepRule.Oldest);
            Add(Loc.T("Str_Dup_KeepNewest", "Keep the newest copy"), PackIconKind.SortClockDescendingOutline, KeepRule.Newest);
            Add(Loc.T("Str_Dup_KeepShortest", "Keep the shortest path"), PackIconKind.FormatLetterCase, KeepRule.ShortestPath);
            Add(Loc.T("Str_Dup_KeepFirstFolder", "Keep the copy in the first folder listed"), PackIconKind.FolderStarOutline, KeepRule.FolderOrder);
            Add(Loc.T("Str_Dup_KeepHighestQuality", "Keep the highest quality (most pixels, then largest file)"), PackIconKind.StarCircleOutline, KeepRule.HighestQuality);
            menu.IsOpen = true;
        }

        private void AutoSelect(KeepRule rule)
        {
            if (!_lastScanVerified)
            {
                var answer = AppModal.Show(Loc.T("Str_MB_TheseResultsWereMatched", "These results were matched by name only, so the files may have different contents.\n\nSelect all copies but one anyway?"),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            DuplicateManager.ApplyKeepRule(_groups, rule, _folders.Select(f => f.FullPath).ToList(), selectOthers: true);
            SelectComboTag(CmbKeepRule, rule.ToString());
            UpdateSummary();
            UiMotion.Pop(SelectionPill, 0.9);
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _results) item.IsSelected = false;
        }

        // =================================================================
        //  Grid interaction
        // =================================================================

        private void DataGridDuplicates_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<ToggleButton>(source) != null || FindAncestor<ButtonBase>(source) != null) return;
            if (FindAncestor<DataGridRow>(source)?.Item is DuplicateFileItem item && item.GroupInfo != null)
                OpenCompareDrawer(item.GroupInfo);
        }

        private void DataGridDuplicates_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var rows = DataGridDuplicates.SelectedItems.OfType<DuplicateFileItem>().ToList();
            switch (e.Key)
            {
                case Key.Space when rows.Count > 0:
                    // Toggle the Remove mark for the highlighted rows.
                    bool mark = !rows.All(r => r.IsSelected);
                    foreach (var r in rows) r.IsSelected = mark;
                    e.Handled = true;
                    break;
                case Key.Enter when rows.Count == 1 && rows[0].GroupInfo != null:
                    OpenCompareDrawer(rows[0].GroupInfo!);
                    e.Handled = true;
                    break;
                case Key.Delete when BtnDeleteSelected.IsEnabled:
                    BtnDeleteSelected_Click(sender, e);
                    e.Handled = true;
                    break;
            }
        }

        // =================================================================
        //  Fold / unfold groups and the shortcuts (keys are set in Settings)
        // =================================================================

        private void AttachShortcuts()
        {
            ShortcutManager.AttachCommon("duplicate-data", ShortcutManager.RunId, () => ButtonScan_Click(this, new RoutedEventArgs()));
            ShortcutManager.Attach("dup.collapseAll", () => SetAllGroupsExpanded(false));
            ShortcutManager.Attach("dup.expandAll", () => SetAllGroupsExpanded(true));
            ShortcutManager.Attach("dup.nextGroup", () => JumpToGroup(+1));
            ShortcutManager.Attach("dup.prevGroup", () => JumpToGroup(-1));
            ShortcutManager.Attach("dup.deleteMarked", () =>
            {
                if (BtnDeleteSelected.IsEnabled) BtnDeleteSelected_Click(this, new RoutedEventArgs());
            });
        }

        private void BtnCollapseGroups_Click(object sender, RoutedEventArgs e) => SetAllGroupsExpanded(false);

        private void BtnExpandGroups_Click(object sender, RoutedEventArgs e) => SetAllGroupsExpanded(true);

        private void SetAllGroupsExpanded(bool expanded)
        {
            foreach (var g in _groups) g.IsExpanded = expanded;
        }

        /// <summary>Moves the highlight to the first row of the next (or previous) group, unfolding it if needed.</summary>
        private void JumpToGroup(int direction)
        {
            if (_groups.Count == 0) return;
            var current = DataGridDuplicates.SelectedItems.OfType<DuplicateFileItem>().FirstOrDefault()?.GroupInfo;
            int index = current == null ? (direction > 0 ? -1 : _groups.Count) : _groups.IndexOf(current);
            index = Math.Clamp(index + direction, 0, _groups.Count - 1);
            var group = _groups[index];
            var first = group.Files.FirstOrDefault();
            if (first == null) return;
            group.IsExpanded = true;
            DataGridDuplicates.SelectedItem = first;
            DataGridDuplicates.UpdateLayout();
            DataGridDuplicates.ScrollIntoView(first);
            DataGridDuplicates.Focus();
        }

        private void BtnGroupCompare_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: DuplicateGroup group }) OpenCompareDrawer(group);
        }

        private void BuildResultsContextMenu()
        {
            var menu = new ContextMenu();

            MenuItem Item(string text, PackIconKind icon, RoutedEventHandler click)
            {
                var mi = new MenuItem { Header = MenuHeader(text, icon) };
                mi.Click += click;
                menu.Items.Add(mi);
                return mi;
            }

            Item(Loc.T("Str_Dup_MenuCompareGroup", "Compare this group"), PackIconKind.FileCompare, (_, _) => { if (_contextItem?.GroupInfo is { } g) OpenCompareDrawer(g); });
            Item(Loc.T("Str_DataQuality_MenuKeepThis", "Keep this copy"), PackIconKind.StarOutline, (_, _) => { if (_contextItem != null) MakeKeeper(_contextItem); });
            menu.Items.Add(new Separator());
            Item(Loc.T("Str_DataQuality_MenuShowInFolder", "Show in folder"), PackIconKind.FolderOpenOutline, (_, _) => { if (_contextItem != null) ShowInFolder(_contextItem.FullPath); });
            Item(Loc.T("Str_DataQuality_MenuCopyPath", "Copy full path"), PackIconKind.ContentCopy, (_, _) =>
            {
                if (_contextItem == null) return;
                try
                {
                    Clipboard.SetText(_contextItem.FullPath);
                }
                catch (Exception ex)
                {
                    Log.Info($"Clipboard busy: {ex.Message}");
                }
            });

            BulkCheckMenu.Append(menu, DataGridDuplicates, () => _results, f => f.IsSelected, (f, v) => f.IsSelected = v,
                UpdateSummary, canRun: () => !_isScanning);

            DataGridDuplicates.ContextMenu = menu;
        }

        private static StackPanel MenuHeader(string text, PackIconKind icon)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new PackIcon { Kind = icon, Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        private void DataGridDuplicates_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            _contextItem = row?.Item as DuplicateFileItem;
            if (row != null && !row.IsSelected)
            {
                DataGridDuplicates.SelectedItems.Clear();
                row.IsSelected = true;
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>Makes <paramref name="keeper"/> the group's keeper and selects the others.</summary>
        private static void MakeKeeper(DuplicateFileItem keeper)
        {
            if (keeper.GroupInfo is not { } group) return;
            foreach (var file in group.Files)
            {
                bool keep = ReferenceEquals(file, keeper);
                file.IsKeep = keep;
                file.IsSelected = !keep;
            }
        }

        private static void ShowInFolder(string path)
        {
            if (ShellExplorer.Show(path) == ShellExplorer.ShowResult.NotFound)
                AppModal.Show(Loc.T("Str_MB_ThatFileOrFolder", "That file or folder no longer exists. Rescan to refresh the results."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Compare drawer
        // =================================================================

        private void OpenCompareDrawer(DuplicateGroup group)
        {
            if (group.Files.Count == 0) return;

            bool wasOpen = ComparePanel.Visibility == Visibility.Visible;
            _compareGroup = group;
            CompareItemsControl.ItemsSource = null;   // fresh containers (radio group state)
            CompareItemsControl.ItemsSource = group.Files;
            CompareScroller.ScrollToTop();
            RefreshCompareLabels();

            // Previews for this group first.
            _thumbs.Request(group.Files);

            if (!wasOpen)
            {
                CompareScrim.Visibility = Visibility.Visible;
                ComparePanel.Visibility = Visibility.Visible;
                var slide = new DoubleAnimation(48, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                ComparePanelShift.BeginAnimation(TranslateTransform.XProperty, slide);
                UiMotion.FadeIn(ComparePanel, 0);
            }
            else
            {
                UiMotion.FadeIn(CompareItemsControl, 6);
            }
        }

        private void RefreshCompareLabels()
        {
            if (_compareGroup == null) return;
            int index = _groups.IndexOf(_compareGroup);
            TxtCompareGroupPosition.Text = index >= 0 ? Loc.F("Str_DataQuality_MsgGroupOf", "Group {0} of {1}", index + 1, _groups.Count) : _compareGroup.Title;
            TxtCompareGroupLabel.Text = $"{_compareGroup.Subtitle} · {_compareGroup.SelectionText}";
            BtnPrevCompareGroup.IsEnabled = BtnNextCompareGroup.IsEnabled = _groups.Count > 1;
            TxtApplyLabel.Text = index >= 0 && index == _groups.Count - 1 ? Loc.T("Str_DataQuality_MsgDone", "Done") : Loc.T("Str_DataQuality_MsgNextGroup", "Next group");
        }

        private void CloseCompareDrawer()
        {
            _compareGroup = null;
            CompareItemsControl.ItemsSource = null;
            CompareScrim.Visibility = Visibility.Collapsed;
            ComparePanel.Visibility = Visibility.Collapsed;
        }

        private void BtnCloseCompare_Click(object sender, RoutedEventArgs e) => CloseCompareDrawer();

        private void CompareScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseCompareDrawer();

        private void BtnPrevCompareGroup_Click(object sender, RoutedEventArgs e) => StepCompareGroup(-1);

        private void BtnNextCompareGroup_Click(object sender, RoutedEventArgs e) => StepCompareGroup(+1);

        private void StepCompareGroup(int direction)
        {
            if (_compareGroup == null || _groups.Count == 0) return;
            int index = _groups.IndexOf(_compareGroup);
            if (index < 0) index = 0;
            OpenCompareDrawer(_groups[(index + direction + _groups.Count) % _groups.Count]);
        }

        /// <summary>
        /// Picking a keeper selects the rest of the group straight away (no
        /// separate Apply step). Click, not Checked: Checked also fires when
        /// the drawer opens and binds the current keeper, which would
        /// silently select the other copies.
        /// </summary>
        private void RadioKeep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DuplicateFileItem chosen }) MakeKeeper(chosen);
        }

        private void BtnSuggestOriginal_Click(object sender, RoutedEventArgs e)
        {
            if (_compareGroup == null) return;
            var keeper = DuplicateManager.PickKeeper(_compareGroup.Files, SelectedKeepRule, _folders.Select(f => f.FullPath).ToList());
            if (keeper != null) MakeKeeper(keeper);
        }

        private void BtnCardShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DuplicateFileItem item }) ShowInFolder(item.FullPath);
        }

        /// <summary>"Next group": step through every group in one pass; closes on the last one.</summary>
        private void BtnApplyOriginal_Click(object sender, RoutedEventArgs e)
        {
            if (_compareGroup == null)
            {
                CloseCompareDrawer();
                return;
            }

            int index = _groups.IndexOf(_compareGroup);
            if (index < 0 || index >= _groups.Count - 1)
            {
                CloseCompareDrawer();
                TxtProgress.Text = $"Reviewed all {_groups.Count} groups - use Move or Delete to act on your choices.";
                UiMotion.Pop(SelectionPill, 0.9);
                return;
            }
            OpenCompareDrawer(_groups[index + 1]);
        }

        // =================================================================
        //  Move / delete / export
        // =================================================================

        private List<DuplicateFileItem> SelectedFiles() => _results.Where(r => r.IsSelected).ToList();

        /// <summary>Warns when an action would remove every copy in some group.</summary>
        private bool ConfirmNoGroupEmptied(string verb)
        {
            var emptied = DuplicateManager.GroupsLosingEveryCopy(_groups);
            if (emptied.Count == 0) return true;

            string which = string.Join(", ", emptied.Take(5).Select(g => g.Number));
            if (emptied.Count > 5) which += ", ...";
            var answer = AppModal.Show(Loc.F("Str_MB_InGroupSGroup", "In {0} group(s) (group {1}) every copy is selected, so {2} would leave no copy behind.\n\nContinue anyway?", emptied.Count, which, verb),
                DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }

        private async void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedFiles();
            if (selected.Count == 0 || _isBusy) return;
            if (!ConfirmNoGroupEmptied("deleting")) return;

            long bytes = selected.Sum(s => s.SizeBytes);
            var rootsWithoutBin = selected.Select(s => Path.GetPathRoot(s.FullPath) ?? "").Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(r => !DuplicateManager.HasRecycleBin(r)).ToList();
            bool recycle = ChkRecycleBin.IsChecked == true && rootsWithoutBin.Count == 0;

            string what = $"{selected.Count:N0} file(s) ({DuplicateText.FormatBytes(bytes)})";
            string message = recycle
                ? Loc.F("Str_DataQuality_MsgAskMoveToBin", "Move {0} to the Recycle Bin?", what)
                : ChkRecycleBin.IsChecked == true
                    ? Loc.F("Str_DataQuality_MsgAskDeleteNoBin", "Permanently delete {0}?\n\n{1} has no Recycle Bin, so this can't be undone.", what, string.Join(", ", rootsWithoutBin))
                    : Loc.F("Str_DataQuality_MsgAskDelete", "Permanently delete {0}?\n\nThis can't be undone.", what);

            var answer = AppModal.Show(message, recycle ? Loc.T("Str_Dlg_Delete", "Delete") : Loc.T("Str_Dlg_DeletePermanently", "Delete permanently"), MessageBoxButton.YesNo,
                recycle ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            await RunActionAsync(recycle ? Loc.T("Str_DataQuality_MsgMovingToBin", "Moving to the Recycle Bin...") : Loc.T("Str_DataQuality_MsgDeleting", "Deleting..."),
                () => _manager.DeleteAsync(selected, recycle),
                r => recycle
                    ? Loc.F("Str_DataQuality_MsgMovedToBin", "Moved {0:N0} file(s) to the Recycle Bin · {1} freed", r.Succeeded.Count, DuplicateText.FormatBytes(r.Bytes))
                    : Loc.F("Str_DataQuality_MsgDeleted", "Deleted {0:N0} file(s) · {1} freed", r.Succeeded.Count, DuplicateText.FormatBytes(r.Bytes)),
                Loc.T("Str_DataQuality_MsgDeleteComplete", "Delete Complete"));
        }

        private async void BtnMoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedFiles();
            if (selected.Count == 0 || _isBusy) return;
            if (!ConfirmNoGroupEmptied("moving them")) return;

            var dialog = new OpenFolderDialog { Title = Loc.F("Str_DataQuality_MsgChooseMoveFolder", "Choose where to move {0} selected file(s)", selected.Count) };
            if (dialog.ShowDialog() != true) return;
            string destination = dialog.FolderName;

            // Moving into a scanned folder would just create new duplicates there.
            if (_folders.Any(f => destination.StartsWith(f.FullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                var answer = AppModal.Show(Loc.T("Str_MB_ThatFolderIsInside", "That folder is inside one of the folders being scanned, so the moved files would show up as duplicates again next scan.\n\nMove there anyway?"),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            await RunActionAsync(Loc.T("Str_DataQuality_MsgMoving", "Moving..."),
                () => _manager.MoveAsync(selected, destination),
                r => Loc.F("Str_DataQuality_MsgMoved", "Moved {0:N0} file(s) · {1} to {2}", r.Succeeded.Count, DuplicateText.FormatBytes(r.Bytes), destination),
                Loc.T("Str_DataQuality_MsgMoveComplete", "Move Complete"));
        }

        /// <summary>Runs a move/delete off the UI thread, then drops handled files and emptied groups.</summary>
        private async Task RunActionAsync(string busyText, Func<Task<DuplicateActionResult>> action, Func<DuplicateActionResult, string> doneText, string toastTitle)
        {
            _isBusy = true;
            ActionsPanel.IsEnabled = false;
            DataGridDuplicates.IsEnabled = false;
            BtnScanForDuplicates.IsEnabled = false;
            ProgressDuplicates.IsIndeterminate = true;
            TxtProgress.Text = busyText;

            try
            {
                var result = await action();

                var remaining = DuplicateManager.RemoveHandled(_groups, result.Succeeded);
                ShowGroups(remaining);

                if (_compareGroup != null)
                {
                    if (remaining.Contains(_compareGroup)) OpenCompareDrawer(_compareGroup);
                    else CloseCompareDrawer();
                }

                TxtProgress.Text = doneText(result);
                try
                {
                    ToastNotificationManager.ShowToast(toastTitle, doneText(result));
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }

                if (result.Failed.Count > 0)
                {
                    var sb = new StringBuilder($"{result.Failed.Count} file(s) couldn't be processed:\n\n");
                    foreach (var (item, error) in result.Failed.Take(8)) sb.AppendLine($"• {item.Name}: {error}");
                    if (result.Failed.Count > 8) sb.AppendLine($"...and {result.Failed.Count - 8} more.");
                    AppModal.Show(sb.ToString(), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_Err_DidntWork", "That didn't work: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                ProgressDuplicates.IsIndeterminate = false;
                DataGridDuplicates.IsEnabled = true;
                BtnScanForDuplicates.IsEnabled = true;
                UpdateSummary();
                UpdateEmptyState();
            }
        }

        private void MenuExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_groups.Count == 0) return;

            var dialog = new SaveFileDialog
            {
                Title = Loc.T("Str_Dup_MsgExportTitle", "Export duplicate results"),
                Filter = Loc.T("Str_DataQuality_MsgCsvFilter", "CSV file (*.csv)|*.csv"),
                FileName = $"duplicates-{DateTime.Now:yyyy-MM-dd}.csv",
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, DuplicateManager.BuildCsv(_groups), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                TxtProgress.Text = Loc.F("Str_Dup_MsgExported", "Exported {0:N0} rows to {1}", _results.Count, dialog.FileName);
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_Err_ExportFailed", "Export failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}