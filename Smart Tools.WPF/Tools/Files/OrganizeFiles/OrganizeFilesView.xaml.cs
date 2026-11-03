using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Organize Files view - UI only. Folder names, the plan and name
    /// clashes are worked out in <see cref="OrganizeManager"/>; scanning,
    /// moving/copying and undo in OrganizeManager.Run.cs. The preview
    /// rebuilds itself (debounced) whenever a setting changes, so what runs
    /// is always what's shown.
    /// </summary>
    public partial class OrganizeFilesView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "Organize Files";

        private readonly OrganizeManager _manager = new();
        private readonly ObservableCollection<OrganizeStepRow> _steps = new();
        private readonly DispatcherTimer _refreshTimer;

        private ObservableCollection<OrganizeFileItem> _rows = new();
        private OrganizeScanResult? _scan;
        private OrganizeRunResult? _lastRun;
        private OrganizeJournal? _journal;
        private OrganizeSettings _settings = new();

        private string _sourceFolder = "";
        private string _destinationFolder = "";

        private CancellationTokenSource? _refreshCts;
        private CancellationTokenSource? _runCts;
        private Task? _refreshTask;
        private bool _pendingRescan;
        private bool _isBusy;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        /// <summary>Rule list for the step combos (flat - each item shows its group as a label).</summary>
        public IReadOnlyList<OrganizeCriterionChoice> CriterionChoices { get; } = OrganizeManager.Choices;

        public OrganizeFilesView()
        {
            InitializeComponent();

            StepsList.ItemsSource = _steps;
            DataGridPreview.ItemsSource = CreateGroupedView(_rows);
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                bool rescan = _pendingRescan;
                _pendingRescan = false;
                _ = RefreshAsync(rescan);
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. Files already moved stay moved (each move is complete on its own) and can be undone next time.</summary>
        public void CancelActiveRun()
        {
            _refreshTimer.Stop();
            _refreshCts?.Cancel();
            _runCts?.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(OrganizeManager.LoadSettings());
                _journal = OrganizeManager.LoadJournal();
                _loadedOnce = true;
                UpdateUndoButton();
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateEmptyState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width) =>
            ColModified.Visibility = width < 1000 ? Visibility.Collapsed : Visibility.Visible;

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
        //  Folders
        // =================================================================

        /// <summary>The Input card changed (or re-checked) its source folder: the view follows it.</summary>
        private void InputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (!string.Equals(InputCard.FolderPath, _sourceFolder, StringComparison.OrdinalIgnoreCase)) SetSource(InputCard.FolderPath);
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => ScanOption_Changed(sender ?? this, new RoutedEventArgs());

        private void BtnBrowseDestination_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Choose where organized files go" };
            if (Directory.Exists(_destinationFolder)) dialog.InitialDirectory = _destinationFolder;
            else if (Directory.Exists(_sourceFolder)) dialog.InitialDirectory = _sourceFolder;
            if (dialog.ShowDialog() != true) return;
            _destinationFolder = dialog.FolderName;
            UpdateFolderUi();
            SaveSettings();
            ScheduleRefresh(rescan: true);   // a destination inside the source is left out of the scan
        }

        private void SetSource(string folder)
        {
            if (_isBusy) return;
            _sourceFolder = folder;
            if (InputCard.FolderPath != folder) InputCard.FolderPath = folder;
            _lastRun = null;
            UpdateFolderUi();
            SaveSettings();
            ScheduleRefresh(rescan: true);
        }

        private void RadioDestination_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateFolderUi(animate: true);
            SaveSettings();
            ScheduleRefresh(rescan: true);
        }

        private string EffectiveDestination =>
            RadioOtherFolder.IsChecked == true && !string.IsNullOrWhiteSpace(_destinationFolder) ? _destinationFolder : _sourceFolder;

        private void UpdateFolderUi(bool animate = false)
        {
            bool other = RadioOtherFolder.IsChecked == true;
            if (animate) UiMotion.FadeVisibility(PanelDestination, other);
            else PanelDestination.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
            TxtDestinationFolder.Text = string.IsNullOrWhiteSpace(_destinationFolder) ? "Choose a folder" : _destinationFolder;
            TxtDestinationFolder.ToolTip = _destinationFolder;
        }

        private void ScanOption_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            ScheduleRefresh(rescan: true);
        }

        // =================================================================
        //  Layout and rules
        // =================================================================

        private OrganizeLayout SelectedLayout =>
            RadioLayoutFlatten.IsChecked == true ? OrganizeLayout.Flatten :
            RadioLayoutKeep.IsChecked == true ? OrganizeLayout.KeepStructure : OrganizeLayout.Rules;

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void RadioLayout_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateLayoutUi(animate: true);
            SaveSettings();
            ScheduleRefresh(rescan: false);
        }

        private void PlanOption_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateLayoutUi(animate: false);
            SaveSettings();
            ScheduleRefresh(rescan: false);
        }

        private void RadioMove_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateMoveUi();
            SaveSettings();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
        }

        private void UpdateLayoutUi(bool animate)
        {
            var layout = SelectedLayout;
            bool rules = layout == OrganizeLayout.Rules;
            if (animate) UiMotion.FadeVisibility(PanelSteps, rules);
            else PanelSteps.Visibility = rules ? Visibility.Visible : Visibility.Collapsed;

            TxtLayoutHint.Text = layout switch
            {
                OrganizeLayout.Flatten => "Every file goes straight into the destination folder, out of any subfolders. Same names get ' (2)'.",
                OrganizeLayout.KeepStructure => "Files keep the subfolders they're in now - useful for moving or copying a whole tree somewhere else.",
                _ => "Each rule adds one folder level, top to bottom.",
            };
            TxtExample.Text = OrganizeManager.ExamplePath(layout, _steps.Select(s => s.ToStep()), ChkEmoji.IsChecked == true);

            int count = _steps.Count;
            TxtStepsHint.Text = $"{count} of {OrganizeManager.MaxSteps}";
            BtnAddStep.IsEnabled = count < OrganizeManager.MaxSteps;
            ChkEmoji.Visibility = _steps.Any(s => s.Criterion == OrganizeCriterion.Category) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateMoveUi()
        {
            bool move = RadioMove.IsChecked == true;
            ChkRemoveEmptyFolders.IsEnabled = move;
            if (_isBusy) return;
            IconOrganize.Kind = move ? PackIconKind.FolderMoveOutline : PackIconKind.ContentCopy;
            TxtOrganizeLabel.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, move ? "Str_Organize_ActionMove" : "Str_Organize_ActionCopy");
        }

        // ---------------- Rule cards ----------------

        private void BtnAddStep_Click(object sender, RoutedEventArgs e)
        {
            if (_steps.Count >= OrganizeManager.MaxSteps) return;
            // Suggest the next useful rule rather than another "Category".
            var used = _steps.Select(s => s.Criterion).ToHashSet();
            var next = new[] { OrganizeCriterion.Category, OrganizeCriterion.Year, OrganizeCriterion.Month, OrganizeCriterion.Extension, OrganizeCriterion.Size }
                .FirstOrDefault(c => !used.Contains(c), OrganizeCriterion.FirstLetter);
            AddStepRow(new OrganizeStep { Criterion = next });
            AfterStepsChanged();
        }

        private void AddStepRow(OrganizeStep step)
        {
            var row = new OrganizeStepRow { Criterion = step.Criterion, GroupUnknownAsOther = step.GroupUnknownAsOther };
            row.PropertyChanged += StepRow_PropertyChanged;
            _steps.Add(row);
        }

        private void StepRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (Ignore) return;
            if (e.PropertyName is nameof(OrganizeStepRow.Criterion) or nameof(OrganizeStepRow.GroupUnknownAsOther)) AfterStepsChanged();
        }

        private void BtnStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(sender, -1);

        private void BtnStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(sender, +1);

        private void MoveStep(object sender, int delta)
        {
            if (sender is not Button { Tag: OrganizeStepRow row }) return;
            int index = _steps.IndexOf(row);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= _steps.Count) return;
            _steps.Move(index, target);
            AfterStepsChanged();
        }

        private void BtnStepRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: OrganizeStepRow row } || _steps.Count <= 1) return;
            row.PropertyChanged -= StepRow_PropertyChanged;
            _steps.Remove(row);
            AfterStepsChanged();
        }

        private void AfterStepsChanged()
        {
            RenumberSteps();
            UpdateLayoutUi(animate: false);
            SaveSettings();
            ScheduleRefresh(rescan: false);
        }

        private void RenumberSteps()
        {
            for (int i = 0; i < _steps.Count; i++)
            {
                _steps[i].Number = i + 1;
                _steps[i].CanMoveUp = i > 0;
                _steps[i].CanMoveDown = i < _steps.Count - 1;
            }
        }

        // ---------------- Settings ----------------

        private void ApplySettings(OrganizeSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                // Folders aren't remembered between sessions - you pick them each time.
                (s.UseSourceAsDestination ? RadioSameFolder : RadioOtherFolder).IsChecked = true;
                (s.Layout switch
                {
                    OrganizeLayout.Flatten => RadioLayoutFlatten,
                    OrganizeLayout.KeepStructure => RadioLayoutKeep,
                    _ => RadioLayoutRules,
                }).IsChecked = true;

                foreach (var row in _steps) row.PropertyChanged -= StepRow_PropertyChanged;
                _steps.Clear();
                foreach (var step in s.Steps) AddStepRow(step);
                RenumberSteps();

                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                ChkSkipHidden.IsChecked = s.SkipHiddenAndSystem;
                (s.MoveFiles ? RadioMove : RadioCopy).IsChecked = true;
                ChkRemoveEmptyFolders.IsChecked = s.RemoveEmptyFolders;
                ChkEmoji.IsChecked = s.EmojiFolders;

                UpdateFolderUi();
                UpdateLayoutUi(animate: false);
                UpdateMoveUi();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            _settings.UseSourceAsDestination = RadioOtherFolder.IsChecked != true;
            _settings.Layout = SelectedLayout;
            _settings.Steps = _steps.Select(r => r.ToStep()).ToList();
            _settings.IncludeSubfolders = InputCard.IncludeSubfolders;
            _settings.SkipHiddenAndSystem = ChkSkipHidden.IsChecked == true;
            _settings.MoveFiles = RadioMove.IsChecked == true;
            _settings.RemoveEmptyFolders = ChkRemoveEmptyFolders.IsChecked == true;
            _settings.EmojiFolders = ChkEmoji.IsChecked == true;
            try
            {
                OrganizeManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Organize settings not saved: {ex.Message}");
            }
        }

        /// <summary>Categories from Settings, read fresh each time so edits there show up without a restart.</summary>
        private static List<OrganizeCategory> LoadCategories()
        {
            try
            {
                return AppSettingsStore.FileCategories
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => OrganizeCategory.Parse(c.Name, c.Extensions))
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Info($"Organize: categories not read: {ex.Message}");
                return new List<OrganizeCategory>();
            }
        }

        private OrganizeOptions BuildOptions() => new()
        {
            SourceFolder = _sourceFolder,
            DestinationFolder = EffectiveDestination,
            Layout = SelectedLayout,
            Steps = _steps.Select(r => r.ToStep()).ToList(),
            IncludeSubfolders = InputCard.IncludeSubfolders,
            SkipHiddenAndSystem = ChkSkipHidden.IsChecked == true,
            MoveFiles = RadioMove.IsChecked == true,
            RemoveEmptyFolders = ChkRemoveEmptyFolders.IsChecked == true,
            EmojiFolders = ChkEmoji.IsChecked == true,
            Categories = LoadCategories(),
        };

        // =================================================================
        //  Preview
        // =================================================================

        /// <summary>Debounced so dragging through the rule list doesn't start a scan per click.</summary>
        private void ScheduleRefresh(bool rescan)
        {
            if (!_viewConstructed || _isBusy) return;
            _pendingRescan |= rescan;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private Task RefreshAsync(bool rescan)
        {
            _refreshCts?.Cancel();
            var cts = _refreshCts = new CancellationTokenSource();
            _refreshTask = RefreshCoreAsync(rescan || _scan == null, cts);
            return _refreshTask;
        }

        private async Task RefreshCoreAsync(bool rescan, CancellationTokenSource cts)
        {
            var token = cts.Token;
            UiMotion.FadeVisibility(FfprobeWarning, false);

            if (!Directory.Exists(_sourceFolder))
            {
                _scan = null;
                ShowRows(new List<OrganizeFileItem>());
                TxtProgress.Text = string.IsNullOrWhiteSpace(_sourceFolder)
                    ? "Choose a folder to see how it would be organized."
                    : $"The folder {_sourceFolder} doesn't exist any more.";
                return;
            }
            if (RadioOtherFolder.IsChecked == true && string.IsNullOrWhiteSpace(_destinationFolder))
            {
                ShowRows(new List<OrganizeFileItem>());
                TxtProgress.Text = "Choose the destination folder (or switch to 'Same folder').";
                return;
            }

            var options = BuildOptions();
            ProgressOrganize.IsIndeterminate = true;
            TxtProgressPercent.Text = "";
            var progress = new Progress<OrganizeProgress>(p =>
            {
                if (!ReferenceEquals(_refreshCts, cts)) return;
                ReportProgress(p);
            });

            try
            {
                if (rescan)
                {
                    TxtProgress.Text = "Reading the folder...";
                    _scan = await OrganizeManager.ScanAsync(options, progress, token);
                }
                if (token.IsCancellationRequested) return;
                var items = await _manager.PlanAsync(_scan!.Files, options, progress, token);
                if (token.IsCancellationRequested) return;

                ShowRows(items);
                TxtProgress.Text = PlanText(items);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer refresh
            }
            catch (InvalidOperationException ex)
            {
                if (!ReferenceEquals(_refreshCts, cts)) return;
                ShowRows(new List<OrganizeFileItem>());
                TxtWarning.Text = ex.Message + " Pick a General or Image rule instead, or add ffprobe.exe.";
                UiMotion.FadeVisibility(FfprobeWarning, true);
                TxtProgress.Text = "Can't read video/audio details.";
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_refreshCts, cts)) return;
                TxtProgress.Text = $"Couldn't build the preview: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_refreshCts, cts))
                {
                    ProgressOrganize.IsIndeterminate = false;
                    ProgressOrganize.Value = 0;
                    TxtProgressPercent.Text = "";
                }
            }
        }

        private string PlanText(IReadOnlyCollection<OrganizeFileItem> items)
        {
            if (items.Count == 0) return "No files to organize in this folder.";
            var parts = new List<string> { OrganizeManager.DescribePlan(items) };
            if (_scan?.HiddenSkipped > 0) parts.Add($"{_scan.HiddenSkipped:N0} hidden left alone");
            if (_scan?.InaccessibleFolders > 0) parts.Add($"{_scan.InaccessibleFolders:N0} folders couldn't be read");
            if (_scan?.LinksSkipped > 0) parts.Add($"{_scan.LinksSkipped:N0} linked folders not followed");
            if (_scan?.InDestinationSkipped > 0) parts.Add($"{_scan.InDestinationSkipped:N0} already in the destination");
            return string.Join(" · ", parts);
        }

        private static ListCollectionView CreateGroupedView(ObservableCollection<OrganizeFileItem> rows)
        {
            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OrganizeFileItem.DestinationFolderDisplay)));
            view.SortDescriptions.Add(new SortDescription(nameof(OrganizeFileItem.DestinationFolderDisplay), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(OrganizeFileItem.Name), ListSortDirection.Ascending));
            return view;
        }

        /// <summary>A new collection and a new grouped view per result (never refill a grouped view in place).</summary>
        private void ShowRows(List<OrganizeFileItem> items)
        {
            _rows = new ObservableCollection<OrganizeFileItem>(items);
            DataGridPreview.ItemsSource = CreateGroupedView(_rows);
            if (_rows.Count > 0) UiMotion.FadeIn(DataGridPreview, 6);
            _lastRun = null;
            UpdateSummary();
            UpdateEmptyState();
        }

        private void UpdateSummary()
        {
            int ready = _rows.Count(OrganizeManager.IsPending);
            long bytes = _rows.Where(OrganizeManager.IsPending).Sum(i => i.SizeBytes);
            int folders = _rows.Where(OrganizeManager.IsPending).Select(i => i.DestinationFolderDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int inPlace = _rows.Count(i => i.Status == OrganizeStatus.InPlace);
            int renamed = _rows.Count(i => OrganizeManager.IsPending(i) && i.IsRenamed);

            TxtSummaryFiles.Text = $"{ready:N0} file{(ready == 1 ? "" : "s")} · {OrganizeText.FormatBytes(bytes)}";
            TxtSummaryFolders.Text = $"{folders:N0} folder{(folders == 1 ? "" : "s")}";
            TxtSummaryInPlace.Text = $"{inPlace:N0} in place";
            PillInPlace.Visibility = inPlace > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryRenamed.Text = $"{renamed:N0} renamed";
            PillRenamed.Visibility = renamed > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_lastRun is { } run)
            {
                int done = run.Moved + run.Copied;
                TxtSummaryDone.Text = $"{done:N0} {(run.Moved > 0 ? "moved" : "copied")}" + (run.Duplicates > 0 ? $" · {run.Duplicates:N0} already there" : "");
                TxtSummaryFailed.Text = $"{run.Failed:N0} failed";
                PillDone.Visibility = done + run.Duplicates > 0 ? Visibility.Visible : Visibility.Collapsed;
                PillFailed.Visibility = run.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                PillDone.Visibility = Visibility.Collapsed;
                PillFailed.Visibility = Visibility.Collapsed;
            }

            bool show = _rows.Count > 0;
            bool firstShow = show && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;
            EmptyStatePanel.Description = Directory.Exists(_sourceFolder)
                ? "Nothing to organize here. Try including subfolders, or showing hidden files."
                : "Choose a folder on the left. The preview shows where every file will go before anything moves.";
            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            UiMotion.FadeVisibility(DataGridPreview, !empty);
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _ = RefreshAsync(rescan: true);
        }

        private void BtnResetOrganize_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _refreshCts?.Cancel();
            _sourceFolder = "";
            InputCard.FolderPath = "";
            _destinationFolder = "";
            RadioSameFolder.IsChecked = true;   // reset clears the destination too, not only the source
            _scan = null;
            UpdateFolderUi();
            SaveSettings();
            ShowRows(new List<OrganizeFileItem>());
            TxtProgress.Text = "Choose a folder to see how it would be organized.";
        }

        private void BtnOpenDestination_Click(object sender, RoutedEventArgs e)
        {
            string folder = EffectiveDestination;
            if (ShellExplorer.Show(folder) != ShellExplorer.ShowResult.Shown)
                MessageBox.Show("Choose a folder first.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Double-click: the file where it is now.</summary>
        private void DataGridPreview_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridPreview.SelectedItem is not OrganizeFileItem item) return;
            string path = item.Status is OrganizeStatus.Moved or OrganizeStatus.Copied ? item.Destination : item.FullPath;
            if (ShellExplorer.Show(path) == ShellExplorer.ShowResult.NotFound)
                MessageBox.Show("That file is no longer there.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Organize and undo
        // =================================================================

        private async void BtnOrganizeFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Stopping after the files in progress...";
                return;
            }

            // Make sure the preview matches the current settings.
            if (_refreshTimer.IsEnabled)
            {
                _refreshTimer.Stop();
                bool rescan = _pendingRescan;
                _pendingRescan = false;
                await RefreshAsync(rescan);
            }
            else if (_refreshTask is { IsCompleted: false })
            {
                await _refreshTask;
            }

            var pending = _rows.Where(OrganizeManager.IsPending).ToList();
            if (pending.Count == 0)
            {
                InputCard.PopDropArea();
                MessageBox.Show(_rows.Count == 0
                        ? "Choose a folder with files in it first."
                        : "Nothing to do - every file is already where it belongs.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = BuildOptions();
            var shortfalls = OutputFolderManager.CheckSpace(pending.Select(i =>
                (i.Destination, i.SizeBytes, NeedsSpace: !(options.MoveFiles && SafeFileOps.SameVolume(i.FullPath, i.Destination)))));
            if (shortfalls.Count > 0)
            {
                string msg = OutputFolderManager.DescribeShortfalls(shortfalls);
                TxtProgress.Text = msg;
                MessageBox.Show(msg + "\n\nFree some space or choose another destination folder.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetBusy(true, undo: false);

            try
            {
                var progress = new Progress<OrganizeProgress>(ReportProgress);
                var (result, journal) = await _manager.RunAsync(_rows.ToList(), options, progress, _runCts.Token);
                _lastRun = result;

                if (journal.Entries.Count > 0)
                {
                    _journal = journal;
                    try
                    {
                        OrganizeManager.SaveJournal(journal);
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Undo journal not saved: {ex.Message}");
                    }
                }

                // The folder has changed: the next setting change re-reads it.
                _scan = null;
                SetBusy(false, undo: false);
                ProgressOrganize.Value = 100;
                TxtProgressPercent.Text = result.Cancelled > 0 ? "" : "100%";
                TxtProgress.Text = OrganizeManager.DescribeRun(result);
                UpdateSummary();

                try
                {
                    ToastNotificationManager.ShowToast("Organize Complete",
                        result.Failed > 0
                            ? $"Organized {result.Moved + result.Copied} files - {result.Failed} failed."
                            : $"Organized {result.Moved + result.Copied} file{(result.Moved + result.Copied == 1 ? "" : "s")}.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }

                // Say which files stayed behind and why, instead of leaving it to row tooltips.
                var failedRows = _rows.Where(i => i.Status == OrganizeStatus.Failed).ToList();
                if (failedRows.Count > 0)
                {
                    string list = string.Join("\n\n", failedRows.Take(8).Select(i => $"• {i.Message}"));
                    if (failedRows.Count > 8) list += $"\n\n…and {failedRows.Count - 8} more (see the Failed rows in the preview).";
                    MessageBox.Show($"{failedRows.Count} file{(failedRows.Count == 1 ? "" : "s")} couldn't be moved and stayed where {(failedRows.Count == 1 ? "it was" : "they were")}:\n\n{list}",
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, undo: false);
                TxtProgress.Text = "Organize failed.";
                MessageBox.Show($"Organize failed: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isBusy) SetBusy(false, undo: false);
            }
        }

        private async void BtnUndoOrganize_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _runCts?.Cancel();
                return;
            }

            var journal = _journal ?? OrganizeManager.LoadJournal();
            if (journal == null)
            {
                UpdateUndoButton();
                return;
            }

            string verb = journal.Moved ? "Move" : "Remove the copies of";
            var answer = MessageBox.Show(
                $"{verb} the {journal.Entries.Count:N0} file{(journal.Entries.Count == 1 ? "" : "s")} from the run on {journal.When:g} back?\n\n" +
                (journal.Moved ? "Files go back to where they were, and the folders that run created are removed if empty." : "Only copies that haven't been changed since are removed."),
                "Undo Organize", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetBusy(true, undo: true);

            try
            {
                var progress = new Progress<OrganizeProgress>(ReportProgress);
                var result = await OrganizeManager.UndoAsync(journal, progress, _runCts.Token);

                OrganizeManager.DeleteJournal();
                _journal = null;

                var parts = new List<string> { $"Undone: {result.Restored:N0} back in place" };
                if (result.Renamed > 0) parts.Add($"{result.Renamed:N0} restored with a new name (the old name was taken)");
                if (result.Missing > 0) parts.Add($"{result.Missing:N0} no longer at the organized location");
                if (result.Failed > 0) parts.Add($"{result.Failed:N0} couldn't be undone");
                SetBusy(false, undo: true);
                TxtProgress.Text = string.Join(" · ", parts);

                if (result.Errors.Count > 0)
                    MessageBox.Show("Some files couldn't be undone:\n\n" + string.Join("\n", result.Errors.Take(12)) + (result.Errors.Count > 12 ? $"\n...and {result.Errors.Count - 12} more" : ""),
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false, undo: true);
                TxtProgress.Text = "Undo stopped - files already put back stay back. Undo again to finish.";
            }
            catch (Exception ex)
            {
                SetBusy(false, undo: true);
                MessageBox.Show($"Undo failed: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isBusy) SetBusy(false, undo: true);
                UpdateUndoButton();
                _scan = null;
                ScheduleRefresh(rescan: true);
            }
        }

        private void UpdateUndoButton()
        {
            BtnUndoOrganize.IsEnabled = !_isBusy && _journal != null;
            BtnUndoOrganize.ToolTip = _journal != null
                ? (object)$"Undo the last run ({_journal.When:g} · {_journal.Entries.Count:N0} file{(_journal.Entries.Count == 1 ? "" : "s")} {(_journal.Moved ? "moved" : "copied")})"
                : TryFindResource("Str_Organize_UndoTooltip");
        }

        private void ReportProgress(OrganizeProgress p)
        {
            if (p.Percent is double pct)
            {
                ProgressOrganize.IsIndeterminate = false;
                ProgressOrganize.Value = pct;
                TxtProgressPercent.Text = $"{pct:0}%";
            }
            else
            {
                ProgressOrganize.IsIndeterminate = true;
                TxtProgressPercent.Text = "";
            }

            string phase = p.Phase switch
            {
                OrganizePhase.Scanning => $"Reading the folder · {p.Processed:N0} files so far",
                OrganizePhase.Planning => $"Reading video/audio/image details · {p.Processed:N0} of {p.Total:N0}",
                OrganizePhase.Undoing => $"Undoing · {p.Processed:N0} of {p.Total:N0}",
                _ => $"{(RadioMove.IsChecked == true ? "Moving" : "Copying")} · {p.Processed:N0} of {p.Total:N0}",
            };
            TxtProgress.Text = string.IsNullOrEmpty(p.CurrentFile) ? phase : $"{phase} · {p.CurrentFile}";
        }

        private void SetBusy(bool busy, bool undo)
        {
            _isBusy = busy;
            InputCard.IsLocked = busy;   // the source can't change mid-run

            if (busy)
            {
                _refreshTimer.Stop();
                _refreshCts?.Cancel();
                IconOrganize.Kind = PackIconKind.StopCircleOutline;
                TxtOrganizeLabel.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "Str_Common_Cancel");
                ProgressOrganize.IsIndeterminate = false;
                ProgressOrganize.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = undo ? "Undoing..." : "Starting...";
            }
            else
            {
                UpdateMoveUi();
            }
            UiMotion.Pop(BtnOrganizeFiles, 0.96);

            OptionsScrollViewer.IsEnabled = !busy;
            BtnRefresh.IsEnabled = !busy;
            BtnResetOrganize.IsEnabled = !busy;
            BtnOrganizeFiles.IsEnabled = !(busy && undo);   // Cancel for an undo lives on the undo button
            BtnUndoOrganize.IsEnabled = busy ? undo : _journal != null;
        }
    }
}