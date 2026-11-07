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
using Binding = System.Windows.Data.Binding;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Find Similar Files view - UI only. Fingerprinting, grouping, keep
    /// rules and move/delete live in <see cref="SimilarManager"/> (maths in
    /// <see cref="SimilarHashing"/>); previews in <see cref="SimilarPreviewService"/>;
    /// "show in folder" in <see cref="ShellExplorer"/>. This file: sidebar,
    /// scan, grouped results, review drawer, move/delete. The side-by-side
    /// compare overlay is in FindSimilarFilesView.Compare.cs.
    /// </summary>
    public partial class SimilarFileFinderView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowColumnThreshold = 1100;
        private static string DialogTitle => Loc.T("Str_Tool_find-similar_Name", "Find Similar Files");
        private const int ThumbnailSize = 240;
        private const int MaxBackgroundThumbnails = 600;

        private readonly SimilarManager _manager = new();
        private readonly ObservableCollection<FolderListEntry> _folders = new();
        private ObservableCollection<SimilarFileItem> _rows = new();
        private ListCollectionView _rowsView;

        private List<SimilarGroup> _groups = new();
        private SimilarScanOptions? _lastOptions;
        private SimilarFinderSettings _settings = new();

        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _thumbnailCts;
        private bool _isScanning;
        private bool _isBusy;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _summaryQueued;
        private SimilarGroup? _reviewGroup;
        private SimilarFileItem? _contextItem;
        private readonly bool _viewConstructed;

        public SimilarFileFinderView()
        {
            InitializeComponent();

            InputCard.ItemsSource = _folders;
            _folders.CollectionChanged += (_, _) => UpdateFolderHint();

            _rowsView = CreateGroupedView(_rows);
            DataGridDuplicates.ItemsSource = _rowsView;

            BuildResultsContextMenu();
            InitComparePlayers();
            AttachShortcuts();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops work, releases media, shuts VLC down, deletes preview copies.</summary>
        public void CancelActiveRun()
        {
            _scanCts?.Cancel();
            _thumbnailCts?.Cancel();
            CloseCompare();
            ShutDownCompareVlc();
            _preview.Dispose();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(SimilarManager.LoadSettings());
                _ = Task.Run(SimilarPreviewService.CleanupStaleProxies);
                _compareVlcStart = StartCompareVlcAsync();   // plays WebM etc. in the compare without converting
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateFolderHint();
            UpdateEmptyState();
        }

        /// <summary>Switching tools unloads the view: stop playback and release the files.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e) => CloseCompare();

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

        private static IEnumerable<string> DroppedFolders(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths.Where(Directory.Exists) : Array.Empty<string>();

        private void AddFolders(IEnumerable<string> folders)
        {
            bool added = false;
            foreach (string folder in folders)
            {
                if (_folders.Any(f => string.Equals(f.FullPath, folder, StringComparison.OrdinalIgnoreCase))) continue;
                _folders.Add(new FolderListEntry { FullPath = folder });
                added = true;
            }
            if (!added) return;
            InputCard.PopDropArea();
            SaveSettings();
        }

        private void UpdateFolderHint() =>
            InputCard.SetEmpty(_folders.Count == 0);

        // ---------------- What to compare ----------------

        private MediaKind SelectedKind =>
            RadioKindVideos.IsChecked == true ? MediaKind.Video :
            RadioKindAudio.IsChecked == true ? MediaKind.Audio : MediaKind.Image;

        /// <summary>"Auto (all types)": pictures, videos and songs in one run, each compared only with its own kind.</summary>
        private bool IsAuto => RadioKindAuto.IsChecked == true;

        private SimilarDetectionMethod SelectedMethod => IsAuto ? SimilarDetectionMethod.AutoAllTypes : SelectedKind switch
        {
            MediaKind.Video => SimilarDetectionMethod.VideoOpenCv,
            MediaKind.Audio => SimilarDetectionMethod.AudioFingerprint,
            _ => SimilarDetectionMethod.ImageOpenCv,
        };

        private SimilarKeepRule SelectedKeepRule =>
            (CmbKeepRule.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse(tag, out SimilarKeepRule r) ? r : SimilarKeepRule.BestQuality;

        private void RadioKind_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateMethodUi(animate: true);
            SaveSettings();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateMethodUi(animate: false);
            SaveSettings();
        }

        private void UpdateMethodUi(bool animate)
        {
            var kind = SelectedKind;
            bool auto = IsAuto;
            bool images = kind == MediaKind.Image || auto;   // the crop option applies to the pictures of an Auto run too
            if (ImageMethodPanel.Visibility != (images ? Visibility.Visible : Visibility.Collapsed))
            {
                ImageMethodPanel.Visibility = images ? Visibility.Visible : Visibility.Collapsed;
                if (images && animate) UiMotion.FadeIn(ImageMethodPanel, 4);
            }

            TxtMethodHint.Text = auto ? Loc.T("Str_FindSimilar_HintAuto", "Compares pictures with pictures, videos with videos and songs with songs, each with its own method, in one run. A video is never matched against a picture.") : kind switch
            {
                MediaKind.Video => Loc.T("Str_FindSimilar_HintVideos", "Compares the pictures of each video over time. Finds the same video in another format, size or quality, mirrored, or with the start or end cut off."),
                MediaKind.Audio => Loc.T("Str_FindSimilar_HintAudio", "Compares the sound itself. Finds the same recording in another format or bitrate, even with the start or end cut off."),
                _ => Loc.T("Str_FindSimilar_HintImages", "Finds the same picture in another format or size, flipped or turned. With the option below, also cropped or edited copies."),
            };

            // Only audio needs ffmpeg; for video it just backs up OpenCV on formats OpenCV can't read.
            bool needsFfmpeg = (kind == MediaKind.Audio || auto) && !SimilarManager.FfmpegAvailable;
            TxtFfmpegWarning.Text = Loc.F("Str_FindSimilar_NeedsFfmpeg", "Needs ffmpeg.exe and ffprobe.exe in {0} - they weren't found.", SimilarManager.FfmpegFolder);
            UiMotion.FadeVisibility(FfmpegWarning, needsFfmpeg);
        }

        private void SliderThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Fires while InitializeComponent is still building the page: the
            // hint text below the slider doesn't exist yet at that point.
            if (!_viewConstructed) return;
            UpdateThresholdUi(e.NewValue);
            if (!_applyingSettings) SaveSettings();
        }

        private void UpdateThresholdUi(double rawValue)
        {
            if (TxtThresholdValue == null || TxtThresholdHint == null) return;
            int value = (int)Math.Round(rawValue);
            TxtThresholdValue.Text = $"{value}%";
            TxtThresholdHint.Text = value >= 95 ? Loc.T("Str_FindSimilar_ThresholdHint95", "Only near-identical copies (resized or re-saved).")
                : value >= 85 ? Loc.T("Str_FindSimilar_ThresholdHint85", "Copies and lightly edited versions. A good default.")
                : value >= 70 ? Loc.T("Str_FindSimilar_ThresholdHint70", "Also finds bigger edits and shorter clips - expect some look-alikes.")
                : Loc.T("Str_FindSimilar_ThresholdHintLow", "Very loose - finds things that merely look alike. Review before deleting.");
        }

        // ---------------- Settings ----------------

        private void ApplySettings(SimilarFinderSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                _folders.Clear();
                foreach (string f in s.Folders.Where(Directory.Exists)) _folders.Add(new FolderListEntry { FullPath = f });

                if (SimilarManager.IsAuto(s.Method)) RadioKindAuto.IsChecked = true;
                else switch (SimilarManager.KindOf(s.Method))
                {
                    case MediaKind.Video: RadioKindVideos.IsChecked = true; break;
                    case MediaKind.Audio: RadioKindAudio.IsChecked = true; break;
                    default: RadioKindImages.IsChecked = true; break;
                }
                ChkFindCrops.IsChecked = s.FindCroppedCopies;

                SliderThreshold.Value = s.ThresholdPercent;
                UpdateThresholdUi(SliderThreshold.Value);
                ChkIncludeSubfolders.IsChecked = s.IncludeSubfolders;
                ChkIgnoreSystemFiles.IsChecked = s.IgnoreSystemFiles;
                ChkRecycleBin.IsChecked = s.SendToRecycleBin;
                var keepItem = CmbKeepRule.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.KeepRule.ToString());
                if (keepItem != null) CmbKeepRule.SelectedItem = keepItem;

                UpdateMethodUi(animate: false);
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;

            _settings.Folders = _folders.Select(f => f.FullPath).ToList();
            _settings.Method = SelectedMethod;
            _settings.FindCroppedCopies = ChkFindCrops.IsChecked == true;
            _settings.ThresholdPercent = SliderThreshold.Value;
            _settings.IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true;
            _settings.IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true;
            _settings.KeepRule = SelectedKeepRule;
            _settings.SendToRecycleBin = ChkRecycleBin.IsChecked == true;

            try
            {
                SimilarManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Similar files settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  Scan
        // =================================================================

        private async void BtnScanForSimilar_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                _scanCts?.Cancel();
                return;
            }

            if (_folders.Count == 0)
            {
                InputCard.PopDropArea();
                MessageBox.Show(Loc.T("Str_MB_AddAtLeastOne2", "Add at least one folder to scan first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await RunScanAsync(new SimilarScanOptions
            {
                Folders = _folders.Select(f => f.FullPath).ToList(),
                IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true,
                IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true,
                Method = SelectedMethod,
                FindCroppedCopies = ChkFindCrops.IsChecked == true,
                ThresholdPercent = SliderThreshold.Value,
            });
        }

        private async void BtnRescan_Click(object sender, RoutedEventArgs e)
        {
            if (!_isScanning && _lastOptions != null) await RunScanAsync(_lastOptions);
        }

        private async Task RunScanAsync(SimilarScanOptions options)
        {
            SaveSettings();
            _lastOptions = options;

            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            CloseCompare();
            CloseReview();
            _thumbnailCts?.Cancel();
            SetScanning(true);

            try
            {
                options.MaxDegreeOfParallelism = await Task.Run(() => PickParallelism(options), token);

                var progress = new Progress<SimilarScanProgress>(ReportProgress);
                var result = await _manager.ScanAsync(options, SelectedKeepRule, progress, token);

                ShowGroups(result.Groups);
                ShowScanSummary(result);
                StartThumbnailLoading(_rows.Take(MaxBackgroundThumbnails).ToList());

                try
                {
                    ToastNotificationManager.ShowToast("Similar Files Scan Complete",
                        result.Groups.Count > 0
                            ? $"{result.FileCount} files in {result.Groups.Count} groups · {SimilarText.FormatBytes(result.ReclaimableBytes)} reclaimable."
                            : "No similar files found.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Scan cancelled.";
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                TxtProgress.Text = ex.Message;
                MessageBox.Show(ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Scan failed.";
                MessageBox.Show(Loc.F("Str_Err_ScanFailed", "Scan failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetScanning(false);
            }
        }

        /// <summary>
        /// Hard drives thrash with parallel reads; each video/audio file also
        /// spawns ffmpeg, so those stay at 4 even on an SSD.
        /// </summary>
        private static int PickParallelism(SimilarScanOptions options)
        {
            bool solidState = options.Folders.All(f => DriveHealthManager.GetMediaKindForPath(f) is DriveKind.Ssd or DriveKind.StorageClassMemory);
            int cores = Math.Max(2, Environment.ProcessorCount);
            if (!solidState) return 2;
            return SimilarManager.KindOf(options.Method) == MediaKind.Image ? cores : Math.Min(4, cores);
        }

        private void ReportProgress(SimilarScanProgress p)
        {
            if (!_isScanning) return;

            if (p.Percent is double pct)
            {
                ProgressScan.IsIndeterminate = false;
                ProgressScan.Value = pct;
                TxtProgressPercent.Text = $"{pct:0}%";
            }
            else
            {
                ProgressScan.IsIndeterminate = true;
                TxtProgressPercent.Text = "";
            }

            // An Auto run reports which kind it is on right now; a single-kind run knows it from the method.
            string noun = (p.Kind ?? SimilarManager.KindOf(_lastOptions?.Method ?? SimilarDetectionMethod.ImageOpenCv)) switch
            {
                MediaKind.Video => Loc.T("Str_FindSimilar_NounVideos", "videos"),
                MediaKind.Audio => Loc.T("Str_FindSimilar_NounAudio", "audio files"),
                _ => Loc.T("Str_FindSimilar_NounImages", "images"),
            };
            string phase = p.Phase switch
            {
                SimilarScanPhase.Listing => Loc.F("Str_FindSimilar_ProgListing", "Finding {0} · {1:N0} so far", noun, p.Total),
                SimilarScanPhase.Fingerprinting => Loc.F("Str_FindSimilar_ProgFingerprinting", "Reading {0:N0} of {1:N0} {2}", p.Processed, p.Total, noun),
                SimilarScanPhase.Matching => Loc.T("Str_FindSimilar_ProgMatching", "Comparing files with each other..."),
                SimilarScanPhase.Grouping => Loc.T("Str_FindSimilar_ProgGrouping", "Grouping look-alikes..."),
                _ => Loc.T("Str_FindSimilar_ProgFinishing", "Finishing..."),
            };
            TxtProgress.Text = string.IsNullOrEmpty(p.CurrentPath) ? phase : $"{phase} · {Path.GetFileName(p.CurrentPath)}";
        }

        private Log.ActivityScope? _activity;

        private void SetScanning(bool scanning)
        {
            _isScanning = scanning;
            if (scanning) _activity ??= Log.Activity("find-similar-files");
            else { _activity?.Dispose(); _activity = null; }

            IconScan.Kind = scanning ? PackIconKind.StopCircleOutline : PackIconKind.ImageSearchOutline;
            if (scanning) TxtScanLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtScanLabel.SetResourceReference(TextBlock.TextProperty, "Str_FindSimilar_ScanButton");
            UiMotion.Pop(BtnScanForSimilar, 0.96);

            OptionsScrollViewer.IsEnabled = !scanning;
            BtnRescan.IsEnabled = !scanning && _lastOptions != null;
            ActionsPanel.IsEnabled = !scanning && !_isBusy && _groups.Count > 0;

            if (scanning)
            {
                ProgressScan.IsIndeterminate = true;
                ProgressScan.Value = 0;
                TxtProgressPercent.Text = "";
                TxtProgress.Text = "Finding files...";
            }
            else
            {
                ProgressScan.IsIndeterminate = false;
                TxtProgressPercent.Text = "";
            }
        }

        private void ShowScanSummary(SimilarScanResult result)
        {
            ProgressScan.Value = 100;
            var notes = new List<string>();
            if (result.UnreadableFiles > 0) notes.Add($"{result.UnreadableFiles:N0} couldn't be read");
            if (result.InaccessibleFolders > 0) notes.Add($"{result.InaccessibleFolders:N0} folders unreadable");
            if (result.SkippedLinks > 0) notes.Add($"{result.SkippedLinks:N0} links not followed");
            string extra = notes.Count > 0 ? " · " + string.Join(" · ", notes) : "";
            TxtProgress.Text = $"Checked {result.FilesScanned:N0} files in {result.Elapsed.TotalSeconds:0.0} s{extra}";

            if (result.SkippedAudioNoFfmpeg)
                TxtProgress.Text += " Â· " + Loc.T("Str_FindSimilar_SongsSkipped", "songs skipped (ffmpeg not found)");

            if (result.Kind == MediaKind.Unsupported)
            {
                // Auto: pictures show a resolution, videos and songs a length.
                ColDetails.Header = TryFindResource("Str_FindSimilar_ColHeaderShape") as string ?? "Size / length";
                ColDetails.Binding = new Binding(nameof(SimilarFileItem.Shape));
            }
            else
            {
                ColDetails.Header = result.Kind == MediaKind.Image ? "Resolution" : "Length";
                ColDetails.Binding = new Binding(result.Kind == MediaKind.Image ? nameof(SimilarFileItem.Resolution) : nameof(SimilarFileItem.Duration));
            }
        }

        // =================================================================
        //  Results
        // =================================================================

        private static ListCollectionView CreateGroupedView(ObservableCollection<SimilarFileItem> source)
        {
            var view = new ListCollectionView(source);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SimilarFileItem.GroupInfo)));
            return view;
        }

        /// <summary>New list + new grouped view in one step (never add to a view whose refresh is deferred).</summary>
        private void ShowGroups(List<SimilarGroup> groups)
        {
            foreach (var item in _rows) item.PropertyChanged -= Item_PropertyChanged;

            _groups = groups;
            var items = groups.SelectMany(g => g.Items).ToList();
            foreach (var item in items) item.PropertyChanged += Item_PropertyChanged;

            _rows = new ObservableCollection<SimilarFileItem>(items);
            _rowsView = CreateGroupedView(_rows);
            DataGridDuplicates.ItemsSource = _rowsView;

            UpdateSummary();
            UpdateEmptyState();
            if (_rows.Count > 0) UiMotion.FadeIn(DataGridDuplicates, 6);
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(SimilarFileItem.IsSelected) or nameof(SimilarFileItem.IsKeep))) return;
            if (_summaryQueued) return;   // coalesce bulk changes
            _summaryQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _summaryQueued = false;
                UpdateSummary();
                RefreshReviewLabels();
                RefreshCompareActions();
            }));
        }

        private void UpdateSummary()
        {
            bool any = _groups.Count > 0;
            bool firstShow = any && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ActionsPanel.IsEnabled = any && !_isScanning && !_isBusy;
            if (!any) return;

            TxtSummaryGroups.Text = $"{_rows.Count:N0} files in {_groups.Count:N0} groups";
            TxtSummaryReclaimable.Text = $"{SimilarText.FormatBytes(_groups.Sum(g => g.PotentialBytes))} reclaimable";

            int selected = _groups.Sum(g => g.SelectedCount);
            SelectionPill.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSelection.Text = $"{selected:N0} selected · {SimilarText.FormatBytes(_groups.Sum(g => g.SelectedReclaimableBytes))}";
            BtnMoveSelected.IsEnabled = BtnDeleteSelected.IsEnabled = selected > 0;

            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;
            if (empty && _lastOptions != null && !_isScanning)
                EmptyStatePanel.Description = "Nothing similar found. Try a lower threshold, another method or more folders.";
            else if (empty)
                EmptyStatePanel.SetResourceReference(Controls.EmptyStateCard.DescriptionProperty, "Str_FindSimilar_ResultsHint");

            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            DataGridDuplicates.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnAutoSelect_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = BtnAutoSelect, Placement = PlacementMode.Bottom };
            void Add(string text, PackIconKind icon, SimilarKeepRule rule)
            {
                var item = new MenuItem { Header = MenuHeader(text, icon) };
                item.Click += (_, _) =>
                {
                    SimilarManager.ApplyKeepRule(_groups, rule);
                    var keepItem = CmbKeepRule.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == rule.ToString());
                    if (keepItem != null) CmbKeepRule.SelectedItem = keepItem;
                    UpdateSummary();
                    UiMotion.Pop(SelectionPill, 0.9);
                };
                menu.Items.Add(item);
            }
            Add("Keep the best quality", PackIconKind.CheckDecagramOutline, SimilarKeepRule.BestQuality);
            Add("Keep the largest file", PackIconKind.ArrowExpandAll, SimilarKeepRule.LargestFile);
            Add("Keep the oldest", PackIconKind.SortClockAscendingOutline, SimilarKeepRule.Oldest);
            Add("Keep the newest", PackIconKind.SortClockDescendingOutline, SimilarKeepRule.Newest);
            menu.IsOpen = true;
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _rows) item.IsSelected = false;
        }

        // ---------------- Grid interaction ----------------

        private void DataGridDuplicates_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<ButtonBase>(source) != null) return;
            if (FindAncestor<DataGridRow>(source)?.Item is SimilarFileItem { GroupInfo: { } group }) OpenReview(group);
        }

        private void DataGridDuplicates_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var rows = DataGridDuplicates.SelectedItems.OfType<SimilarFileItem>().ToList();
            switch (e.Key)
            {
                case Key.Space when rows.Count > 0:
                    bool mark = !rows.All(r => r.IsSelected);
                    foreach (var r in rows) r.IsSelected = mark;
                    e.Handled = true;
                    break;
                case Key.Enter when rows.Count == 1 && rows[0].GroupInfo != null:
                    OpenCompare(rows[0].GroupInfo!);
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
            ShortcutManager.Attach("similar.scan", () => BtnScanForSimilar_Click(this, new RoutedEventArgs()));
            ShortcutManager.Attach("similar.collapseAll", () => SetAllGroupsExpanded(false));
            ShortcutManager.Attach("similar.expandAll", () => SetAllGroupsExpanded(true));
            ShortcutManager.Attach("similar.nextGroup", () => JumpToGroup(+1));
            ShortcutManager.Attach("similar.prevGroup", () => JumpToGroup(-1));
            ShortcutManager.Attach("similar.deleteMarked", () =>
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
            var current = DataGridDuplicates.SelectedItems.OfType<SimilarFileItem>().FirstOrDefault()?.GroupInfo;
            int index = current == null ? (direction > 0 ? -1 : _groups.Count) : _groups.IndexOf(current);
            index = Math.Clamp(index + direction, 0, _groups.Count - 1);
            var group = _groups[index];
            var first = group.Items.FirstOrDefault();
            if (first == null) return;
            group.IsExpanded = true;
            DataGridDuplicates.SelectedItem = first;
            DataGridDuplicates.UpdateLayout();
            DataGridDuplicates.ScrollIntoView(first);
            DataGridDuplicates.Focus();
        }

        private void BtnGroupReview_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: SimilarGroup group }) OpenReview(group);
        }

        private void BtnGroupSideBySide_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: SimilarGroup group }) OpenCompare(group);
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

            Item("Side by side", PackIconKind.CompareHorizontal, (_, _) => { if (_contextItem?.GroupInfo is { } g) OpenCompare(g); });
            Item("Review this group", PackIconKind.ViewCarouselOutline, (_, _) => { if (_contextItem?.GroupInfo is { } g) OpenReview(g); });
            Item("Keep this copy", PackIconKind.StarOutline, (_, _) => _contextItem?.GroupInfo?.SetKeep(_contextItem));
            Item("Delete the other copies in this group", PackIconKind.DeleteSweepOutline, async (_, _) =>
            {
                if (_contextItem?.GroupInfo is { } g) await DeleteOthersInGroupAsync(g);
            });
            menu.Items.Add(new Separator());
            Item("Show in folder", PackIconKind.FolderOpenOutline, (_, _) => { if (_contextItem != null) ShowInFolder(_contextItem.FullPath); });
            Item("Copy full path", PackIconKind.ContentCopy, (_, _) =>
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

            BulkCheckMenu.Append(menu, DataGridDuplicates, () => _rows, f => f.IsSelected, (f, v) => f.IsSelected = v,
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
            _contextItem = row?.Item as SimilarFileItem;
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

        private static void ShowInFolder(string path)
        {
            if (ShellExplorer.Show(path) == ShellExplorer.ShowResult.NotFound)
                MessageBox.Show(Loc.T("Str_MB_ThatFileNoLonger2", "That file no longer exists. Rescan to refresh the results."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Review drawer
        // =================================================================

        private void OpenReview(SimilarGroup group)
        {
            if (group.Items.Count == 0) return;

            bool wasOpen = KeepSidebarBorder.Visibility == Visibility.Visible;
            _reviewGroup = group;
            KeepSidebarItems.ItemsSource = null;   // fresh containers (radio group state)
            KeepSidebarItems.ItemsSource = group.Items;
            KeepSidebarScroller.ScrollToTop();
            RefreshReviewLabels();
            StartThumbnailLoading(group.Items.Where(i => i.Thumbnail == null).ToList(), cancelPrevious: false);

            if (!wasOpen)
            {
                KeepSidebarScrim.Visibility = Visibility.Visible;
                KeepSidebarBorder.Visibility = Visibility.Visible;
                KeepSidebarShift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(48, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                UiMotion.FadeIn(KeepSidebarBorder, 0);
            }
            else
            {
                UiMotion.FadeIn(KeepSidebarItems, 6);
            }
        }

        private void RefreshReviewLabels()
        {
            if (_reviewGroup == null) return;
            int index = _groups.IndexOf(_reviewGroup);
            TxtGroupPosition.Text = index >= 0 ? $"Group {index + 1} of {_groups.Count}" : _reviewGroup.Title;
            TxtKeepSidebarGroupLabel.Text = $"{_reviewGroup.Subtitle} · {_reviewGroup.SelectionText}";
            BtnPrevGroup.IsEnabled = BtnNextGroup.IsEnabled = _groups.Count > 1;
            TxtNextReviewLabel.Text = index >= 0 && index == _groups.Count - 1 ? "Done" : "Next group";
        }

        private void CloseReview()
        {
            _reviewGroup = null;
            KeepSidebarItems.ItemsSource = null;
            KeepSidebarScrim.Visibility = Visibility.Collapsed;
            KeepSidebarBorder.Visibility = Visibility.Collapsed;
        }

        private void BtnCloseKeepSidebar_Click(object sender, RoutedEventArgs e) => CloseReview();

        private void KeepSidebarScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseReview();

        private void BtnPrevGroup_Click(object sender, RoutedEventArgs e) => StepReview(-1);

        private void BtnNextGroup_Click(object sender, RoutedEventArgs e) => StepReview(+1);

        private void StepReview(int direction)
        {
            if (_reviewGroup == null || _groups.Count == 0) return;
            int index = Math.Max(0, _groups.IndexOf(_reviewGroup));
            OpenReview(_groups[(index + direction + _groups.Count) % _groups.Count]);
        }

        /// <summary>Click, not Checked: Checked also fires when the drawer binds the current keeper.</summary>
        private void RadioKeep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SimilarFileItem chosen }) chosen.GroupInfo?.SetKeep(chosen);
        }

        private void BtnSuggestKeeper_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewGroup != null) SimilarManager.ApplyKeepRule(_reviewGroup, SelectedKeepRule);
        }

        private void BtnCardShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SimilarFileItem item }) ShowInFolder(item.FullPath);
        }

        private void BtnReviewSideBySide_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewGroup != null) OpenCompare(_reviewGroup);
        }

        private void BtnNextReview_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewGroup == null) return;
            int index = _groups.IndexOf(_reviewGroup);
            if (index < 0 || index >= _groups.Count - 1)
            {
                CloseReview();
                TxtProgress.Text = $"Reviewed all {_groups.Count} groups - use Move or Delete to act on your choices.";
                UiMotion.Pop(SelectionPill, 0.9);
                return;
            }
            OpenReview(_groups[index + 1]);
        }

        // =================================================================
        //  Thumbnails
        // =================================================================

        private void StartThumbnailLoading(List<SimilarFileItem> items, bool cancelPrevious = true)
        {
            if (cancelPrevious || _thumbnailCts == null || _thumbnailCts.IsCancellationRequested)
            {
                _thumbnailCts?.Cancel();
                _thumbnailCts = new CancellationTokenSource();
            }
            var token = _thumbnailCts.Token;
            var candidates = items.Where(i => i.Thumbnail == null && ThumbnailService.SupportsThumbnail(i.FullPath)).ToList();
            if (candidates.Count > 0) _ = LoadThumbnailsAsync(candidates, token);
        }

        private async Task LoadThumbnailsAsync(List<SimilarFileItem> items, CancellationToken token)
        {
            using var throttle = new SemaphoreSlim(4);   // video previews each start ffmpeg
            var tasks = items.Select(async item =>
            {
                try
                {
                    await throttle.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    if (token.IsCancellationRequested || item.Thumbnail != null) return;
                    var thumbnail = await ThumbnailService.GetThumbnailAsync(item.FullPath, ThumbnailSize, token);
                    if (thumbnail != null && !token.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() => item.Thumbnail = thumbnail);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    throttle.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        // =================================================================
        //  Move / delete
        // =================================================================

        private List<SimilarFileItem> SelectedFiles() => _rows.Where(r => r.IsSelected).ToList();

        private bool ConfirmNoGroupEmptied(string verb)
        {
            var emptied = SimilarManager.GroupsLosingEveryCopy(_groups);
            if (emptied.Count == 0) return true;
            string which = string.Join(", ", emptied.Take(5).Select(g => g.Id)) + (emptied.Count > 5 ? ", ..." : "");
            return MessageBox.Show(Loc.F("Str_MB_InGroupSGroup2", "In {0} group(s) (group {1}) every file is selected, so {2} would leave none of them.\n\nContinue anyway?", emptied.Count, which, verb),
                DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        private async void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedFiles();
            if (selected.Count == 0 || _isBusy) return;
            if (!ConfirmNoGroupEmptied("deleting")) return;
            await DeleteFilesAsync(selected);
        }

        /// <summary>
        /// The old "Replace the files": keep this group's keeper, delete the
        /// other copies (Recycle Bin by default) in one step.
        /// </summary>
        private async Task DeleteOthersInGroupAsync(SimilarGroup group)
        {
            if (_isBusy) return;
            var others = group.Items.Where(i => !i.IsKeep).ToList();
            if (others.Count == 0 || others.Count == group.Items.Count) return;
            await DeleteFilesAsync(others);
        }

        private async Task DeleteFilesAsync(List<SimilarFileItem> selected)
        {

            long bytes = selected.Sum(s => s.SizeBytes);
            var rootsWithoutBin = selected.Select(s => Path.GetPathRoot(s.FullPath) ?? "").Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(r => !SimilarManager.HasRecycleBin(r)).ToList();
            bool recycle = ChkRecycleBin.IsChecked == true && rootsWithoutBin.Count == 0;

            string what = $"{selected.Count:N0} file(s) ({SimilarText.FormatBytes(bytes)})";
            string message = recycle
                ? $"Move {what} to the Recycle Bin?"
                : ChkRecycleBin.IsChecked == true
                    ? $"Permanently delete {what}?\n\n{string.Join(", ", rootsWithoutBin)} has no Recycle Bin, so this can't be undone."
                    : $"Permanently delete {what}?\n\nThis can't be undone.";
            if (MessageBox.Show(message, recycle ? Loc.T("Str_Dlg_Delete", "Delete") : Loc.T("Str_Dlg_DeletePermanently", "Delete permanently"), MessageBoxButton.YesNo,
                    recycle ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            await RunActionAsync(selected, recycle ? "Moving to the Recycle Bin..." : "Deleting...",
                () => _manager.DeleteAsync(selected, recycle),
                r => recycle
                    ? $"Moved {r.Succeeded.Count:N0} file(s) to the Recycle Bin · {SimilarText.FormatBytes(r.Bytes)} freed"
                    : $"Deleted {r.Succeeded.Count:N0} file(s) · {SimilarText.FormatBytes(r.Bytes)} freed",
                "Delete Complete");
        }

        private async void BtnMoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedFiles();
            if (selected.Count == 0 || _isBusy) return;
            if (!ConfirmNoGroupEmptied("moving them")) return;

            var dialog = new OpenFolderDialog { Title = $"Choose where to move {selected.Count} selected file(s)" };
            if (dialog.ShowDialog() != true) return;
            string destination = dialog.FolderName;

            if (_folders.Any(f => destination.StartsWith(f.FullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                && MessageBox.Show(Loc.T("Str_MB_ThatFolderIsInside2", "That folder is inside one of the scanned folders, so the moved files would be found again next scan.\n\nMove there anyway?"),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            await RunActionAsync(selected, "Moving...",
                () => _manager.MoveAsync(selected, destination),
                r => $"Moved {r.Succeeded.Count:N0} file(s) · {SimilarText.FormatBytes(r.Bytes)} to {destination}",
                "Move Complete");
        }

        private async Task RunActionAsync(List<SimilarFileItem> targets, string busyText, Func<Task<SimilarManager.ActionResult>> action,
            Func<SimilarManager.ActionResult, string> doneText, string toastTitle)
        {
            // A MediaElement keeps its file open - release anything shown before moving/deleting it.
            ReleaseCompareFilesFor(targets);

            _isBusy = true;
            ActionsPanel.IsEnabled = false;
            DataGridDuplicates.IsEnabled = false;
            BtnScanForSimilar.IsEnabled = false;
            ProgressScan.IsIndeterminate = true;
            TxtProgress.Text = busyText;

            try
            {
                var result = await action();
                var remaining = SimilarManager.RemoveHandled(_groups, result.Succeeded, SelectedKeepRule);
                ShowGroups(remaining);

                if (_reviewGroup != null)
                {
                    if (remaining.Contains(_reviewGroup)) OpenReview(_reviewGroup);
                    else if (remaining.Count > 0) OpenReview(remaining[Math.Min(Math.Max(0, _reviewGroup.Id - 1), remaining.Count - 1)]);
                    else CloseReview();
                }
                if (_compareGroup != null && !remaining.Contains(_compareGroup)) CloseCompare();

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
                    MessageBox.Show(sb.ToString(), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_DidntWork", "That didn't work: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                ProgressScan.IsIndeterminate = false;
                DataGridDuplicates.IsEnabled = true;
                BtnScanForSimilar.IsEnabled = true;
                UpdateSummary();
                UpdateEmptyState();
            }
        }
    }
}
