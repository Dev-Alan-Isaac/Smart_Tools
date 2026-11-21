using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Junk File Cleaner - UI only. Where to look, scanning and deleting are
    /// in <see cref="JunkCleanerManager"/>. Results show one row per place
    /// (each browser separately); the chosen row's files are listed in a
    /// virtualised grid so a huge temp folder doesn't freeze the window.
    /// </summary>
    public partial class JunkFileCleanerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_junk-cleaner_Name", "Junk File Cleaner");

        private readonly ObservableCollection<JunkGroup> _groups = new();
        private readonly bool _viewConstructed;
        private JunkSettings _settings = new();
        private CancellationTokenSource? _runCts;
        private bool _isScanning, _isCleaning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        public JunkFileCleanerView()
        {
            InitializeComponent();
            CategoryResultsList.ItemsSource = _groups;

            var filesMenu = new ContextMenu();
            BulkCheckMenu.Append(filesMenu, DataGridFiles,
                () => SelectedGroup?.Items ?? Enumerable.Empty<JunkFile>(),
                f => f.IsSelected, (f, v) => f.IsSelected = v,
                () =>
                {
                    SelectedGroup?.Refresh();
                    UpdateResultsState();
                    ShowFiles();
                },
                eligible: f => f.Status != JunkFileStatus.Deleted, canRun: () => !Busy);
            DataGridFiles.ContextMenu = filesMenu;

            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. A clean stops after the file it's on - every file is deleted whole or not at all.</summary>
        public void CancelActiveRun() => _runCts?.Cancel();

        private bool Busy => _isScanning || _isCleaning;

        private CheckBox[] CategoryBoxes => new[] { ChkWindowsTemp, ChkBrowserCache, ChkThumbnailCache, ChkErrorReports, ChkRecycleBin, ChkWindowsUpdateCache, ChkLogFiles };

        // =================================================================
        //  Lifetime, layout, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(JunkCleanerManager.LoadSettings());
                ElevationBanner.ShowIfNotElevated();
            }
            UpdateResultsState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ColModified.Visibility = e.NewSize.Width < 1150 ? Visibility.Collapsed : Visibility.Visible;
            ResultsColumn.Width = new GridLength(e.NewSize.Width < 1000 ? 320 : 400);
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

        private void ApplySettings(JunkSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                foreach (var box in CategoryBoxes)
                    box.IsChecked = Enum.TryParse(box.Tag as string, out JunkCategory c) && s.Categories.Contains(c);
                ChkSkipRecent.IsChecked = s.SkipRecent;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateSelectAllLabel();
        }

        private List<JunkCategory> CheckedCategories() =>
            CategoryBoxes.Where(b => b.IsChecked == true)
                .Select(b => Enum.TryParse(b.Tag as string, out JunkCategory c) ? c : (JunkCategory?)null)
                .OfType<JunkCategory>().ToList();

        private void SaveSettingsQuietly()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _settings = new JunkSettings { Categories = CheckedCategories(), SkipRecent = ChkSkipRecent.IsChecked == true };
            try
            {
                JunkCleanerManager.SaveSettings(_settings);
            }
            catch (Exception)
            {
                // Settings are a convenience - a locked file mustn't stop the tool.
            }
        }

        private void Category_Click(object sender, RoutedEventArgs e)
        {
            UpdateSelectAllLabel();
            SaveSettingsQuietly();
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => SaveSettingsQuietly();

        /// <summary>"All" ticks every category; "None" when they're all ticked (the old toggle could get out of step with the boxes).</summary>
        private void BtnSelectAllCategories_Click(object sender, RoutedEventArgs e)
        {
            bool allTicked = CategoryBoxes.All(b => b.IsChecked == true);
            foreach (var box in CategoryBoxes) box.IsChecked = !allTicked;
            UpdateSelectAllLabel();
            SaveSettingsQuietly();
        }

        private void UpdateSelectAllLabel() =>
            TxtSelectAllCategories.Text = CategoryBoxes.All(b => b.IsChecked == true) ? "None" : "All";

        // =================================================================
        //  Scan
        // =================================================================

        private async void BtnScanForJunk_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Stopping…";
                return;
            }
            var categories = CheckedCategories();
            if (categories.Count == 0)
            {
                TxtProgress.Text = "Tick at least one kind of junk to look for.";
                UiMotion.Pop(OptionsScrollViewer, 0.98);
                return;
            }

            var cts = new CancellationTokenSource();
            _runCts = cts;
            _isScanning = true;
            SetBusyUi();
            var progress = new Progress<JunkProgress>(p =>
            {
                if (cts.IsCancellationRequested) return;
                ProgressScan.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                TxtProgress.Text = p.Text;
            });
            try
            {
                var options = new JunkScanOptions { Categories = categories, SkipRecent = ChkSkipRecent.IsChecked == true };
                var groups = await JunkCleanerManager.ScanAsync(options, progress: progress, token: cts.Token);
                ShowGroups(groups);
                ProgressScan.Value = 100;
                TxtProgressPercent.Text = "100%";
                TxtProgress.Text = JunkCleanerManager.DescribeScan(groups) + (groups.Count > 0 ? " Review the list, then press Clean." : "");
                if (groups.Count > 0) UiMotion.Pop(BtnCleanSelected, 0.92);
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Scan stopped.";
                ProgressScan.Value = 0;
                TxtProgressPercent.Text = "";
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "The scan failed: " + ex.Message;
                ProgressScan.Value = 0;
                TxtProgressPercent.Text = "";
            }
            finally
            {
                _isScanning = false;
                if (ReferenceEquals(_runCts, cts)) _runCts = null;
                cts.Dispose();
                SetBusyUi();
            }
        }

        private void ShowGroups(IEnumerable<JunkGroup> groups)
        {
            _groups.Clear();
            foreach (var g in groups) _groups.Add(g);
            CategoryResultsList.SelectedIndex = _groups.Count > 0 ? 0 : -1;
            if (_groups.Count == 0) TxtEmptyHint.Text = "Nothing to clean - all clear.";
            UpdateResultsState();
            ShowFiles();
        }

        // =================================================================
        //  Results
        // =================================================================

        private JunkGroup? SelectedGroup => CategoryResultsList.SelectedItem as JunkGroup;

        private void CategoryResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowFiles();

        /// <summary>Lists the chosen place's files (virtualised - one row per file on screen only).</summary>
        private void ShowFiles()
        {
            var g = SelectedGroup;
            DataGridFiles.ItemsSource = g is { IsRecycleBin: false } ? g.Items : null;
            bool files = g is { IsRecycleBin: false };
            DataGridFiles.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            RecycleBinPanel.Visibility = g is { IsRecycleBin: true } ? Visibility.Visible : Visibility.Collapsed;
            FilesEmptyPanel.Visibility = g == null ? Visibility.Visible : Visibility.Collapsed;
            BtnSelectAllItems.IsEnabled = BtnDeselectAllItems.IsEnabled = files && !Busy;
            BtnOpenFolder.IsEnabled = files;
            if (g == null)
            {
                FilesHeader.Content = "Files";
                TxtFilesInfo.Text = "";
                return;
            }
            FilesHeader.Content = g.Title;
            TxtFilesInfo.Text = g.IsRecycleBin
                ? g.Where
                : $"{g.SelectedCount:N0} of {g.Count:N0} ticked · {JunkText.FormatBytes(g.SelectedBytes)} of {g.SizeText}";
            if (g.IsRecycleBin)
                TxtRecycleBin.Text = g.RecycleBinItems > 0
                    ? $"Windows doesn't list the Recycle Bin's files here. Cleaning empties it on every drive: {g.RecycleBinItems:N0} items, {JunkText.FormatBytes(g.RecycleBinBytes)}. Untick the row to keep them."
                    : "The Recycle Bin is empty.";
        }

        /// <summary>The place's tick box: ticks or unticks all its files.</summary>
        private void GroupCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (Busy || sender is not CheckBox { Tag: JunkGroup group } box) return;
            group.IsSelected = box.IsChecked == true;   // the box follows through its binding
            UpdateResultsState();
            if (ReferenceEquals(group, SelectedGroup)) ShowFiles();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            SelectedGroup?.Refresh();
            UpdateResultsState();
            ShowFiles();
        }

        private void BtnSelectAllItems_Click(object sender, RoutedEventArgs e) => SetGroupTicked(true);

        private void BtnDeselectAllItems_Click(object sender, RoutedEventArgs e) => SetGroupTicked(false);

        private void SetGroupTicked(bool ticked)
        {
            if (Busy || SelectedGroup is not { } g) return;
            g.IsSelected = ticked;
            UpdateResultsState();
            ShowFiles();
        }

        /// <summary>The pills and the Clean button follow what's ticked (the old "reclaimable" pill counted unticked files too).</summary>
        private void UpdateResultsState()
        {
            bool any = _groups.Count > 0;
            EmptyStatePanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            SafetyNoteBanner.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            var t = JunkCleanerManager.Totals(_groups);
            TxtSummaryCount.Text = $"{t.Files:N0} {JunkText.Plural(t.Files, "item", "items")} · {JunkText.FormatBytes(t.Bytes)}";
            TxtSummaryReclaimable.Text = $"{JunkText.FormatBytes(t.SelectedBytes)} ticked";
            if (!_isCleaning)
            {
                TxtCleanLabel.Text = t.SelectedFiles > 0 ? $"Clean {JunkText.FormatBytes(t.SelectedBytes)}" : "Clean";
                BtnCleanSelected.IsEnabled = !_isScanning && t.SelectedFiles > 0;
            }
        }

        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is JunkFile file) ShowInFolder(file.FullPath, file.Root);
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DataGridFiles.SelectedItem is JunkFile file) ShowInFolder(file.FullPath, file.Root);
            else if (SelectedGroup?.Location.Roots.FirstOrDefault(Directory.Exists) is string root) ShowInFolder(null, root);
        }

        private void ShowInFolder(string? file, string folder)
        {
            try
            {
                if (file != null && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
                else if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
                else TxtProgress.Text = "That folder is no longer there.";
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Couldn't open the folder: " + ex.Message;
            }
        }

        // =================================================================
        //  Clean
        // =================================================================

        private async void BtnCleanSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Stopping after the current file…";
                return;
            }
            var t = JunkCleanerManager.Totals(_groups);
            if (t.SelectedFiles == 0)
            {
                TxtProgress.Text = "Nothing is ticked.";
                return;
            }

            // One question that says everything (the old view asked up to twice, and checked the wrong browser).
            var lines = new List<string> { $"Delete {t.SelectedFiles:N0} {JunkText.Plural(t.SelectedFiles, "item", "items")} ({JunkText.FormatBytes(t.SelectedBytes)}) for good? They don't go to the Recycle Bin." };
            if (_groups.Any(g => g.IsRecycleBin && g.SelectedCount > 0)) lines.Add("• The Recycle Bin is emptied on every drive.");
            foreach (var g in _groups.Where(g => g.SelectedCount > 0 && g.Location.ProcessName is string p && JunkCleanerManager.IsProcessRunning(p)))
                lines.Add($"• {g.Title} is open - files it's using will be skipped. Close it first for a full clean.");
            if (AppModal.Show(string.Join("\n\n", lines), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var cts = new CancellationTokenSource();
            _runCts = cts;
            _isCleaning = true;
            SetBusyUi();
            var progress = new Progress<JunkProgress>(p =>
            {
                if (cts.IsCancellationRequested) return;
                ProgressScan.IsIndeterminate = p.Indeterminate;
                ProgressScan.Value = p.Percent;
                TxtProgressPercent.Text = p.Indeterminate ? "" : $"{p.Percent:0}%";
                TxtProgress.Text = p.Text;
            });
            try
            {
                var result = await JunkCleanerManager.CleanAsync(_groups.ToList(), progress: progress, token: cts.Token);

                // Places with nothing left go; the rest keep files that couldn't be deleted, with the reason.
                foreach (var g in _groups.Where(g => g.Count == 0).ToList()) _groups.Remove(g);
                foreach (var g in _groups) g.Note = JunkCleanerManager.NoteFor(g);
                if (SelectedGroup == null && _groups.Count > 0) CategoryResultsList.SelectedIndex = 0;
                if (_groups.Count == 0) TxtEmptyHint.Text = "All clean.";

                ProgressScan.Value = 100;
                TxtProgressPercent.Text = "100%";
                TxtProgress.Text = JunkCleanerManager.DescribeClean(result);
                try
                {
                    ToastNotificationManager.ShowToast("Cleaning complete", TxtProgress.Text);
                }
                catch (Exception)
                {
                    // a toast is a nicety
                }
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Cleaning failed: " + ex.Message;
            }
            finally
            {
                _isCleaning = false;
                ProgressScan.IsIndeterminate = false;
                if (ReferenceEquals(_runCts, cts)) _runCts = null;
                cts.Dispose();
                SetBusyUi();
                ShowFiles();
            }
        }

        private void SetBusyUi()
        {
            OptionsScrollViewer.IsEnabled = !Busy;
            CategoryResultsList.IsHitTestVisible = !_isCleaning;
            DataGridFiles.IsHitTestVisible = !_isCleaning;

            IconScan.Kind = _isScanning ? PackIconKind.Close : PackIconKind.MagnifyScan;
            if (_isScanning) TxtScanLabel.Text = "Cancel";
            else TxtScanLabel.SetResourceReference(TextBlock.TextProperty, "Str_Junk_ScanButton");
            BtnScanForJunk.IsEnabled = !_isCleaning;

            IconClean.Kind = _isCleaning ? PackIconKind.Close : PackIconKind.DeleteSweepOutline;
            if (_isCleaning)
            {
                TxtCleanLabel.Text = "Stop";
                BtnCleanSelected.IsEnabled = true;
            }
            if (Busy)
            {
                ProgressScan.Value = 0;
                TxtProgressPercent.Text = "0%";
            }
            UpdateResultsState();
            BtnSelectAllItems.IsEnabled = BtnDeselectAllItems.IsEnabled = !Busy && SelectedGroup is { IsRecycleBin: false };
        }

        // =================================================================
        //  Helpers
        // =================================================================

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
    }
}
