using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Hash &amp; Checksum Verifier view - UI only. Hashing, checksum lists, comparing, the run and exports are in
    /// <see cref="HashVerifierManager"/> (.cs / .Run.cs / .Export.cs).
    /// </summary>
    public partial class HashVerifierView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_hash-verifier_Name", "Hash & Checksum Verifier");
        private const long MaxListBytes = 64L * 1024 * 1024;

        private readonly HashVerifierManager _manager = new();

        private readonly List<HashSource> _sources = new();
        private readonly ObservableCollection<HashQueueGroup> _queueDisplay = new();
        private List<string> _queuedFiles = new();

        private ObservableCollection<HashFileResult> _rows = new();
        private ListCollectionView? _view;
        private HashRunResult? _lastRun;
        private HashVerifyMode _lastRunMode = HashVerifyMode.Compute;

        private IReadOnlyList<HashChecksumEntry> _entries = Array.Empty<HashChecksumEntry>();
        private string _listPath = "";
        private string _baseFolder = "";

        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _expandCts;
        private HashVerifierSettings _settings = new();

        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        /// <summary>The warning button: show only the rows with a problem.</summary>
        private bool _onlyProblems;

        /// <summary>The "Filter results" box (what is typed, trimmed) - applied after a short pause, like Search File.</summary>
        private string _filter = "";
        private readonly System.Windows.Threading.DispatcherTimer _filterTimer;

        public HashVerifierView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _queueDisplay;
            _filterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _filterTimer.Tick += (_, _) =>
            {
                _filterTimer.Stop();
                _filter = TxtResultFilter.Text.Trim();
                ApplyFilter();
                UpdateEmptyState();
            };
            _viewConstructed = true;
            SetRows(new ObservableCollection<HashFileResult>());
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a run and any folder listing.</summary>
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
                ApplySettings(HashVerifierManager.LoadSettings());
                _loadedOnce = true;
            }
            UpdateQueueHint();
            UpdateSummary();
            UpdateEmptyState();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void ApplySettings(HashVerifierSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                foreach (var (kind, box) in AlgorithmBoxes()) box.IsChecked = s.Algorithms.Contains(kind);
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                ChkUpperCase.IsChecked = s.UpperCase;
                _onlyProblems = s.OnlyProblems;
                UpdateProblemsButton();
                RadioModeCompute.IsChecked = s.Mode == HashVerifyMode.Compute;
                RadioModeExpected.IsChecked = s.Mode == HashVerifyMode.Expected;
                RadioModeList.IsChecked = s.Mode == HashVerifyMode.ChecksumList;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateModePanels();
            UpdateColumns(afterRun: false);
            ApplyFilter();
        }

        private void SaveSettings()
        {
            if (Ignore) return;
            _settings.Algorithms = SelectedAlgorithms().ToList();
            _settings.IncludeSubfolders = InputCard.IncludeSubfolders;
            _settings.UpperCase = ChkUpperCase.IsChecked == true;
            _settings.OnlyProblems = _onlyProblems;
            try
            {
                HashVerifierManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Hash settings not saved: {ex.Message}");   // the tool works without saved settings
            }
        }

        private IEnumerable<(HashAlgorithmKind Kind, CheckBox Box)> AlgorithmBoxes()
        {
            yield return (HashAlgorithmKind.Md5, ChkMd5);
            yield return (HashAlgorithmKind.Sha1, ChkSha1);
            yield return (HashAlgorithmKind.Sha256, ChkSha256);
            yield return (HashAlgorithmKind.Sha384, ChkSha384);
            yield return (HashAlgorithmKind.Sha512, ChkSha512);
            yield return (HashAlgorithmKind.Crc32, ChkCrc32);
        }

        private IEnumerable<HashAlgorithmKind> SelectedAlgorithms() =>
            AlgorithmBoxes().Where(a => a.Box.IsChecked == true).Select(a => a.Kind);

        private DataGridColumn ColumnOf(HashAlgorithmKind kind) => kind switch
        {
            HashAlgorithmKind.Md5 => ColMd5,
            HashAlgorithmKind.Sha1 => ColSha1,
            HashAlgorithmKind.Sha256 => ColSha256,
            HashAlgorithmKind.Sha384 => ColSha384,
            HashAlgorithmKind.Sha512 => ColSha512,
            _ => ColCrc32,
        };

        /// <summary>One hash column per ticked algorithm; after a run also any algorithm the comparison needed.</summary>
        private void UpdateColumns(bool afterRun)
        {
            foreach (var kind in HashVerifierManager.AllAlgorithms)
            {
                bool show = SelectedAlgorithms().Contains(kind) || (afterRun && _rows.Any(r => r.TryGetHash(kind, out _)));
                ColumnOf(kind).Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void Algorithm_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            // At least one algorithm must stay on.
            if (!SelectedAlgorithms().Any() && sender is CheckBox box) box.IsChecked = true;
            UpdateColumns(afterRun: false);
            SaveSettings();
        }

        private void UpperCase_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            bool upper = ChkUpperCase.IsChecked == true;
            foreach (var row in _rows) row.UpperCase = upper;
            SaveSettings();
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
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new HashSource { FullPath = path, IsFolder = isFolder });
                added = true;
            }
            if (!added) return;
            InputCard.PopDropArea();
            _ = RefreshQueueAsync();
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || sender is not Button { Tag: HashQueueGroup group }) return;
            _sources.RemoveAll(s => string.Equals(s.FullPath, group.IsFolder ? group.FolderPath : group.FilePaths.FirstOrDefault(), StringComparison.OrdinalIgnoreCase));
            _ = RefreshQueueAsync();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isRunning) return;
            _expandCts?.Cancel();
            _sources.Clear();
            _queuedFiles = new();
            _lastRun = null;
            RebuildQueueDisplay();
            SetRows(new ObservableCollection<HashFileResult>());
            TxtProgress.SetResourceReference(TextBlock.TextProperty, "Str_Hash_Idle");
            ProgressHash.Value = 0;
            TxtProgressPercent.Text = "";
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            if (_sources.Any(s => s.IsFolder)) _ = RefreshQueueAsync();
        }

        private void RebuildQueueDisplay()
        {
            _queueDisplay.Clear();
            foreach (var group in HashVerifierManager.BuildQueueGroups(_sources)) _queueDisplay.Add(group);
            UpdateQueueHint();
        }

        private void UpdateQueueHint() => InputCard.SetEmpty(_queueDisplay.Count == 0);

        /// <summary>Lists the added folders off the UI thread, then shows every file as a waiting row.</summary>
        private async Task RefreshQueueAsync()
        {
            RebuildQueueDisplay();
            _expandCts?.Cancel();
            var cts = _expandCts = new CancellationTokenSource();

            if (_sources.Count == 0)
            {
                _queuedFiles = new();
                SetRows(new ObservableCollection<HashFileResult>());
                return;
            }

            if (_sources.Any(s => s.IsFolder))
            {
                ProgressHash.IsIndeterminate = true;
                TxtProgress.Text = Loc.T("Str_DataQuality_MsgReadingFolders", "Reading folders...");
            }
            try
            {
                bool sub = InputCard.IncludeSubfolders;
                var sources = _sources.ToList();
                bool upper = ChkUpperCase.IsChecked == true;
                var listing = await HashVerifierManager.ExpandAsync(sources, sub, cts.Token);
                var plan = await Task.Run(() => HashVerifierManager.BuildPlan(listing.Files, new HashRunOptions(), upper), cts.Token);
                if (cts.IsCancellationRequested) return;
                _queuedFiles = listing.Files;
                _lastRun = null;
                SetRows(new ObservableCollection<HashFileResult>(plan.Rows));
                TxtProgress.Text = $"{plan.Rows.Count:N0} file{(plan.Rows.Count == 1 ? "" : "s")} ready" +
                    (listing.Unreadable > 0 ? $" · {listing.Unreadable:N0} folder(s) couldn't be read" : "");
            }
            catch (OperationCanceledException)
            {
                // a newer listing replaced this one
            }
            catch (Exception ex)
            {
                Log.Info($"Hash: listing failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_expandCts, cts)) ProgressHash.IsIndeterminate = false;
            }
        }

        // =================================================================
        //  Compare with: mode, expected hash, checksum list
        // =================================================================

        private HashVerifyMode CurrentMode =>
            RadioModeExpected.IsChecked == true ? HashVerifyMode.Expected :
            RadioModeList.IsChecked == true ? HashVerifyMode.ChecksumList : HashVerifyMode.Compute;

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            _settings.Mode = CurrentMode;
            UpdateModePanels();
            SaveSettings();
        }

        private void UpdateModePanels()
        {
            var mode = CurrentMode;
            PanelExpected.Visibility = mode == HashVerifyMode.Expected ? Visibility.Visible : Visibility.Collapsed;
            PanelList.Visibility = mode == HashVerifyMode.ChecksumList ? Visibility.Visible : Visibility.Collapsed;
            TxtRunLabel.SetResourceReference(TextBlock.TextProperty, mode == HashVerifyMode.Compute ? "Str_Hash_RunButton" : "Str_Hash_VerifyButton");
            UpdateExpectedState();
        }

        private void TxtExpected_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateExpectedState();
        }

        private void UpdateExpectedState()
        {
            if (string.IsNullOrWhiteSpace(TxtExpected.Text))
            {
                TxtExpectedState.Text = "";
                return;
            }
            bool ok = HashVerifierManager.TryParseExpected(TxtExpected.Text, out _, out var algo);
            string text = ok
                ? string.Format(TryFindResource("Str_Hash_ExpectedOk") as string ?? "Recognised as {0}", HashVerifierManager.DisplayName(algo))
                : TryFindResource("Str_Hash_ExpectedBad") as string ?? "Not a valid hash";
            TxtExpectedState.Text = text;
            TxtExpectedState.SetResourceReference(TextBlock.ForegroundProperty, ok ? "StatusSuccessFg" : "StatusErrorFg");
        }

        private void BtnLoadList_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_Hash_MsgOpenTitle", "Open a checksum file"),
                Filter = Loc.T("Str_Hash_MsgOpenFilter", "Checksum files (*.sha256;*.sha512;*.sha1;*.sha384;*.md5;*.sfv;*.txt;SHA*SUMS;MD5SUMS)|*.sha256;*.sha512;*.sha1;*.sha384;*.md5;*.sfv;*.txt;SHA*SUMS;MD5SUMS|All files (*.*)|*.*"),
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                if (new FileInfo(dialog.FileName).Length > MaxListBytes)
                {
                    AppModal.Show(Loc.T("Str_MB_ThatFileIsToo", "That file is too large to be a checksum list."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var entries = HashVerifierManager.ParseChecksumList(File.ReadAllText(dialog.FileName), Path.GetFileName(dialog.FileName), out int skipped);
                if (entries.Count == 0)
                {
                    AppModal.Show(Loc.T("Str_MB_NoChecksumsWereFound", "No checksums were found in that file."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _entries = entries;
                _listPath = dialog.FileName;
                _baseFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
                TxtBaseFolder.Text = _baseFolder;
                TxtBaseFolder.ToolTip = _baseFolder;
                TxtListInfo.Text = string.Format(TryFindResource("Str_Hash_ListLoaded") as string ?? "{0}: {1} entries", Path.GetFileName(_listPath), entries.Count.ToString("N0")) +
                    (skipped > 0 ? string.Format(TryFindResource("Str_Hash_ListSkipped") as string ?? " ({0} skipped)", skipped) : "");
                TxtListInfo.ToolTip = _listPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppModal.Show(Loc.F("Str_MB_CouldnTReadThe", "Couldn't read the checksum file: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnBaseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("Str_Hash_MsgBaseFolderTitle", "Folder the checksum file refers to"), InitialDirectory = _baseFolder };
            if (dialog.ShowDialog() != true) return;
            _baseFolder = dialog.FolderName;
            TxtBaseFolder.Text = _baseFolder;
            TxtBaseFolder.ToolTip = _baseFolder;
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = Loc.T("Str_Hash_MsgCancelling", "Cancelling - finishing the file being hashed...");
                return;
            }

            var mode = CurrentMode;
            if (mode == HashVerifyMode.Expected && !HashVerifierManager.TryParseExpected(TxtExpected.Text, out _, out _))
            {
                Tell("Str_Hash_NeedExpected", "Paste a valid expected hash first.");
                return;
            }
            if (mode == HashVerifyMode.ChecksumList && _entries.Count == 0)
            {
                Tell("Str_Hash_NeedList", "Load a checksum file first.");
                return;
            }
            // A checksum list can be checked against its own folder without adding files first.
            if (_queuedFiles.Count == 0 && mode != HashVerifyMode.ChecksumList)
            {
                InputCard.PopDropArea();
                Tell("Str_Hash_NeedFiles", "Add some files or a folder first.");
                return;
            }

            var options = new HashRunOptions
            {
                Algorithms = SelectedAlgorithms().ToHashSet(),
                Mode = mode,
                Expected = TxtExpected.Text,
                Entries = _entries,
                BaseFolder = _baseFolder,
            };
            bool upper = ChkUpperCase.IsChecked == true;
            SaveSettings();

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetRunning(true);

            try
            {
                var files = _queuedFiles.ToList();
                var plan = await Task.Run(() => HashVerifierManager.BuildPlan(files, options, upper));
                SetRows(new ObservableCollection<HashFileResult>(plan.Rows));
                _lastRun = null;
                UpdateSummary();

                var progress = new Progress<HashProgress>(ReportProgress);
                var result = await _manager.RunAsync(plan, options, progress, _runCts.Token);
                _lastRun = result;
                _lastRunMode = mode;

                SetRunning(false);
                ProgressHash.Value = result.WasCancelled ? ProgressHash.Value : 100;
                TxtProgressPercent.Text = result.WasCancelled ? "" : "100%";
                TxtProgress.Text = HashVerifierManager.DescribeRun(result, mode);
                UpdateColumns(afterRun: true);

                try
                {
                    int bad = result.Mismatched + result.Missing + result.Extra + result.Unreadable;
                    ToastNotificationManager.ShowToast(Loc.T("Str_Hash_MsgToastTitle", "Hashing Complete"),
                        mode == HashVerifyMode.Compute ? Loc.F("Str_Hash_MsgToastHashed", "Hashed {0:N0} files.", result.Computed)
                        : bad == 0 ? Loc.F("Str_Hash_MsgToastAllMatch", "All {0:N0} files match.", result.Matched)
                        : Loc.F("Str_Hash_MsgToastSomeBad", "{0:N0} match - {1:N0} need a look.", result.Matched, bad));
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                SetRunning(false);
                TxtProgress.Text = Loc.T("Str_Hash_MsgStopped", "Stopped - something went wrong.");
                AppModal.Show(Loc.F("Str_MB_HashingFailed", "Hashing failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isRunning) SetRunning(false);
                UpdateSummary();
                UpdateEmptyState();
            }
        }

        private void Tell(string key, string fallback) =>
            AppModal.Show(TryFindResource(key) as string ?? fallback, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);

        private void ReportProgress(HashProgress p)
        {
            if (!_isRunning) return;
            _activity?.Update($"{p.Done:N0}/{p.Total:N0} hashed · {p.CurrentFile}");
            ProgressHash.IsIndeterminate = false;
            ProgressHash.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:0}%";
            TxtProgress.Text = $"{p.Done:N0} of {p.Total:N0} hashed" + (string.IsNullOrEmpty(p.CurrentFile) ? "" : $" · {p.CurrentFile}");
            UpdateSummary();
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("hash-verifier", $"{CurrentMode}, {_queuedFiles.Count} queued file(s), {string.Join('+', SelectedAlgorithms())}");
            else { _activity?.Dispose(); _activity = null; }

            IconRun.Kind = running ? MaterialDesignThemes.Wpf.PackIconKind.StopCircleOutline : MaterialDesignThemes.Wpf.PackIconKind.Pound;
            if (running) TxtRunLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else UpdateModePanels();
            UiMotion.Pop(BtnRun, 0.96);

            OptionsScrollViewer.IsEnabled = !running;
            UpdateActions();

            if (running)
            {
                ProgressHash.IsIndeterminate = false;
                ProgressHash.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = Loc.T("Str_DataQuality_MsgStarting", "Starting...");
            }
        }

        // =================================================================
        //  Results grid
        // =================================================================

        private void SetRows(ObservableCollection<HashFileResult> rows)
        {
            _rows = rows;
            _view = new ListCollectionView(_rows);
            _view.IsLiveFiltering = true;
            _view.LiveFilteringProperties.Add(nameof(HashFileResult.IsProblem));
            // Hashes and status arrive while a run is going: the search follows them.
            foreach (string p in new[] { nameof(HashFileResult.Md5), nameof(HashFileResult.Sha1), nameof(HashFileResult.Sha256), nameof(HashFileResult.Sha384), nameof(HashFileResult.Sha512), nameof(HashFileResult.Crc32), nameof(HashFileResult.Verdict), nameof(HashFileResult.Details) })
                _view.LiveFilteringProperties.Add(p);
            ApplyFilter();
            DataGridResults.ItemsSource = _view;
            UpdateColumns(afterRun: false);
            UpdateSummary();
            UpdateEmptyState();
        }

        private void ApplyFilter()
        {
            if (_view == null) return;
            bool onlyProblems = _onlyProblems;
            string term = _filter;
            _view.Filter = !onlyProblems && term.Length == 0
                ? null
                : o => o is HashFileResult r && (!onlyProblems || r.IsProblem) && r.Matches(term);
        }

        /// <summary>Filter results: finds the typed words in the name, path, size, status, details or any hash.</summary>
        private void TxtResultFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        /// <summary>The warning button: same job as the old "Only problems" tick box.</summary>
        private void BtnCheckProblems_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            _onlyProblems = !_onlyProblems;
            UpdateProblemsButton();
            UiMotion.Pop(BtnCheckProblems, 0.9);
            ApplyFilter();
            UpdateEmptyState();
            SaveSettings();
        }

        /// <summary>Lit (accent, filled icon) while only the problems are shown.</summary>
        private void UpdateProblemsButton()
        {
            ProblemsIcon.Kind = _onlyProblems ? MaterialDesignThemes.Wpf.PackIconKind.Warning : MaterialDesignThemes.Wpf.PackIconKind.WarningOutline;
            if (_onlyProblems)
            {
                ProblemsIcon.SetResourceReference(MaterialDesignThemes.Wpf.PackIcon.ForegroundProperty, "StatusWarningFg");
                BtnCheckProblems.SetResourceReference(Button.BackgroundProperty, "StatusWarningBg");
            }
            else
            {
                ProblemsIcon.ClearValue(MaterialDesignThemes.Wpf.PackIcon.ForegroundProperty);
                BtnCheckProblems.ClearValue(Button.BackgroundProperty);
            }
            BtnCheckProblems.ToolTip = TryFindResource(_onlyProblems ? "Str_Hash_ShowAllTooltip" : "Str_Hash_OnlyProblemsTooltip");
        }

        private void UpdateSummary()
        {
            bool any = _rows.Count > 0;
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            UpdateActions();
            if (!any) return;

            var r = HashVerifierManager.Summarize(_rows, TimeSpan.Zero);
            TxtSummaryFiles.Text = $"{r.Total:N0} file{(r.Total == 1 ? "" : "s")}";
            int problems = r.Mismatched + r.Missing + r.Extra + r.Unreadable + r.Cancelled;
            bool comparing = _rows.Any(x => x.Verdict is HashVerdict.Match or HashVerdict.Mismatch or HashVerdict.Missing or HashVerdict.Extra);
            PillMatch.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;
            PillProblems.Visibility = problems > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryMatch.Text = $"{r.Matched:N0} {(TryFindResource("Str_Hash_PillMatch") as string ?? "Match")}";
            TxtSummaryProblems.Text = $"{problems:N0} {(TryFindResource("Str_Hash_PillProblems") as string ?? "Problems")}";
        }

        /// <summary>Like the Duplicate and Validate views: actions wait for results; row actions need a selection.</summary>
        private void UpdateActions()
        {
            ActionsPanel.IsEnabled = _rows.Count > 0 && !_isRunning;
            BtnCopyHash.IsEnabled = BtnCopyPath.IsEnabled = BtnShowInFolder.IsEnabled = DataGridResults.SelectedItems.Count > 0;
        }

        private void DataGridResults_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

        private void UpdateEmptyState()
        {
            bool empty = _view == null || _view.IsEmpty;
            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            DataGridResults.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Copy and export
        // =================================================================

        private HashFileResult? _menuRow;

        private void DataGridResults_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? current = e.OriginalSource as DependencyObject;
            while (current != null && current is not DataGridRow)
                current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);

            _menuRow = (current as DataGridRow)?.Item as HashFileResult;
            if (_menuRow != null && !DataGridResults.SelectedItems.Contains(_menuRow))
                DataGridResults.SelectedItem = _menuRow;
        }

        private void DataGridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridResults.SelectedItem is HashFileResult row) ShowInFolder(row);
        }

        private List<HashFileResult> SelectedRows() => DataGridResults.SelectedItems.Cast<HashFileResult>().ToList();

        /// <summary>The hash of the strongest visible algorithm - what "Copy hash" copies.</summary>
        private HashAlgorithmKind PrimaryAlgorithm() =>
            HashVerifierManager.AllAlgorithms
                .Where(k => ColumnOf(k).Visibility == Visibility.Visible)
                .DefaultIfEmpty(HashAlgorithmKind.Sha256)
                .OrderByDescending(k => k == HashAlgorithmKind.Crc32 ? -1 : (int)k)
                .First();

        private void MenuCopyHash_Click(object sender, RoutedEventArgs e)
        {
            var kind = PrimaryAlgorithm();
            var lines = SelectedRows().Select(r => r.GetHash(kind)).Where(h => h.Length > 0).ToList();
            if (lines.Count > 0) CopyText(string.Join(Environment.NewLine, lines));
        }

        private void MenuCopyAll_Click(object sender, RoutedEventArgs e)
        {
            var lines = new List<string>();
            foreach (var row in SelectedRows())
                foreach (var kind in HashVerifierManager.AllAlgorithms.Where(k => row.TryGetHash(k, out _)))
                    lines.Add($"{row.FileName}\t{HashVerifierManager.DisplayName(kind)}\t{row.GetHash(kind)}");
            if (lines.Count > 0) CopyText(string.Join(Environment.NewLine, lines));
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var rows = SelectedRows();
            if (rows.Count > 0) CopyText(string.Join(Environment.NewLine, rows.Select(r => r.FullPath)));
        }

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((_menuRow ?? DataGridResults.SelectedItem as HashFileResult) is { } row) ShowInFolder(row);
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

        private void ShowInFolder(HashFileResult row)
        {
            string path = File.Exists(row.FullPath) ? row.FullPath : row.Folder;
            if (ShellExplorer.Show(path) != ShellExplorer.ShowResult.Shown)
                AppModal.Show(Loc.T("Str_MB_ThatFileOrFolder2", "That file or folder is no longer there."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Raised before the menu opens: one checksum-file item per algorithm that has hashes, plus CSV.</summary>
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            ExportMenu.Items.Clear();
            string label = TryFindResource("Str_Hash_ExportChecksumFile") as string ?? "Checksum file";

            foreach (var kind in HashVerifierManager.AllAlgorithms.Where(k => _rows.Any(r => r.TryGetHash(k, out _))))
            {
                var item = new MenuItem
                {
                    Header = $"{label} ({HashVerifierManager.DisplayName(kind)})",
                    Tag = kind,
                    Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.FileCheckOutline },
                };
                item.Click += MenuExportSums_Click;
                ExportMenu.Items.Add(item);
            }

            var csv = new MenuItem
            {
                Header = TryFindResource("Str_Common_ExportAsCsv") as string ?? "CSV",
                Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.FileDelimitedOutline },
            };
            csv.Click += MenuExportCsv_Click;
            ExportMenu.Items.Add(csv);
        }

        private List<HashFileResult> RowsToExport() => _view?.Cast<HashFileResult>().ToList() ?? _rows.ToList();

        private void MenuExportSums_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: HashAlgorithmKind kind }) return;
            var rows = RowsToExport();
            if (!rows.Any(r => r.TryGetHash(kind, out _)))
            {
                Tell("Str_Hash_NeedFiles", "Hash some files first - there is nothing to export yet.");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = Loc.T("Str_Hash_MsgSaveTitle", "Save checksum file"),
                Filter = Loc.T("Str_Hash_MsgSaveFilter", "Checksum file|*.*"),
                FileName = HashVerifierManager.DefaultChecksumFileName(kind),
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string? root = _lastRunMode == HashVerifyMode.ChecksumList && _baseFolder.Length > 0 ? _baseFolder : null;
                string saved = HashVerifierManager.SaveChecksumFile(rows, kind, dialog.FileName, root);
                ToastNotificationManager.ShowToast(Loc.T("Str_DataQuality_MsgExportComplete", "Export Complete"), Loc.F("Str_Generators_MsgSaved", "Saved {0}", Path.GetFileName(saved)));
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_MB_CouldNotSaveThe", "Could not save the file: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var rows = RowsToExport();
            if (rows.Count == 0)
            {
                Tell("Str_Hash_NeedFiles", "Hash some files first - there is nothing to export yet.");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = Loc.T("Str_Hash_MsgExportTitle", "Export results"),
                Filter = Loc.T("Str_DataQuality_MsgCsvFilter", "CSV file (*.csv)|*.csv"),
                FileName = $"Hash results {DateTime.Now:yyyy-MM-dd HH-mm}.csv",
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string saved = HashVerifierManager.SaveCsv(rows, dialog.FileName);
                ToastNotificationManager.ShowToast(Loc.T("Str_DataQuality_MsgExportComplete", "Export Complete"), Loc.F("Str_Generators_MsgSaved", "Saved {0}", Path.GetFileName(saved)));
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_MB_CouldNotSaveThe", "Could not save the file: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
