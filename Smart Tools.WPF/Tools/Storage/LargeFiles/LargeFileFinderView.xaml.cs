using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Large File Finder - UI only. Scanning (a few workers, top-N, live
    /// snapshots), kinds, delete / move off the UI thread, CSV and settings
    /// are in <see cref="LargeFileFinderManager"/>. The chosen folder and
    /// drive are kept for this session only - never saved.
    /// </summary>
    public partial class LargeFileFinderView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_large-files_Name", "Large File Finder");

        private enum RunState
        {
            Idle,
            Scanning,
            Acting,   // deleting or moving
        }

        private static readonly Dictionary<LargeFileCategory, SolidColorBrush> KindBrushes = new();

        private readonly BulkObservableCollection<LargeFileItem> _results = new();
        private readonly DispatcherTimer _saveTimer;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private List<LargeFileItem> _allFiles = new();   // last scan (all kinds), largest first
        private LargeFileCategory? _filter;              // kind chip, null = all
        private LargeFileOptions? _lastOptions;          // for Refresh
        private int _hiddenByLimit;                      // matches past the result limit
        private int _skippedFolders;
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _actionCts;
        private RunState _state = RunState.Idle;
        private int _scanVersion;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        public LargeFileFinderView()
        {
            InitializeComponent();
            DataGridLargeFiles.ItemsSource = _results;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };

            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a scan or a move (a half-copied file is removed) and saves the options.</summary>
        public void CancelActiveRun()
        {
            try
            {
                _scanCts?.Cancel();
                _actionCts?.Cancel();
                _lifetimeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettings();
            }
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;
        private bool IsBusy => _state != RunState.Idle;
        private Window? Owner => Window.GetWindow(this);

        // =================================================================
        //  Layout
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(LargeFileFinderManager.LoadSettings());
            UpdateScopePanels(animate: false);
            _ = LoadDrivesAsync();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var visibility = e.NewSize.Width < 1000 ? Visibility.Collapsed : Visibility.Visible;
            ColType.Visibility = visibility;
            ColModified.Visibility = visibility;
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
        //  Where to look
        // =================================================================

        private bool DiskScope => RadioScopeDisk.IsChecked == true;

        private void Scope_Click(object sender, RoutedEventArgs e)
        {
            UpdateScopePanels(animate: true);
            QueueSave();
        }

        private void UpdateScopePanels(bool animate)
        {
            InputCard.Visibility = DiskScope ? Visibility.Collapsed : Visibility.Visible;
            DiskScopePanel.Visibility = DiskScope ? Visibility.Visible : Visibility.Collapsed;

            if (animate) UiMotion.FadeIn(DiskScope ? DiskScopePanel : InputCard, 4);
        }

        private async Task LoadDrivesAsync()
        {
            string? previous = (DriveList.SelectedItem as LargeFileDrive)?.RootPath;
            BtnRefreshDrives.IsEnabled = false;
            try
            {
                // A sleeping network drive can take seconds to answer - never on the UI thread.
                var drives = await Task.Run(() => LargeFileFinderManager.GetDrives(), _lifetimeCts.Token);
                DriveList.ItemsSource = drives;
                DriveList.SelectedItem = drives.FirstOrDefault(d => string.Equals(d.RootPath, previous, StringComparison.OrdinalIgnoreCase))
                                         ?? drives.FirstOrDefault();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DriveList.ItemsSource = Array.Empty<LargeFileDrive>();
            }
            finally
            {
                BtnRefreshDrives.IsEnabled = true;
            }
        }

        private void BtnRefreshDrives_Click(object sender, RoutedEventArgs e) => _ = LoadDrivesAsync();

        private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveList.SelectedItem != null) DriveList.ScrollIntoView(DriveList.SelectedItem);
        }

        /// <summary>The Input card's folder changed (or was re-checked).</summary>
        private void InputCard_FolderChanged(object? sender, EventArgs e) => QueueSave();

        /// <summary>The folder to search: the Input card's (session only, never saved).</summary>
        private string _folder => InputCard.FolderPath;

        // =================================================================
        //  Size, limit, options
        // =================================================================

        private void SizePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } button) return;
            var parts = tag.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int unit)) return;
            TxtMinSize.Text = parts[0];
            CmbMinSize.SelectedIndex = unit;
            UiMotion.Pop(button, 0.9);
        }

        private void Option_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            if (TxtSizeError.Visibility == Visibility.Visible && LargeFileFinderManager.ParseSize(TxtMinSize.Text, CmbMinSize.SelectedIndex) != null)
                TxtSizeError.Visibility = Visibility.Collapsed;
            QueueSave();
        }

        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) => QueueSave();

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton chip) UiMotion.Pop(chip, 0.9);
            QueueSave();
        }

        private RadioButton[] LimitChipList => new[] { RadioLimit25, RadioLimit50, RadioLimit100, RadioLimitAll };

        private int SelectedLimit =>
            LimitChipList.FirstOrDefault(c => c.IsChecked == true)?.Tag is string tag && int.TryParse(tag, out int n) ? n : 50;

        /// <summary>The options from the sidebar, or null (after telling the user what's missing).</summary>
        private LargeFileOptions? ReadOptions()
        {
            string root;
            if (DiskScope)
            {
                if (DriveList.SelectedItem is not LargeFileDrive drive)
                {
                    AppModal.Show(Owner!, "Choose a drive to search first.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                    return null;
                }
                root = drive.RootPath;
            }
            else
            {
                if (_folder.Length == 0)
                {
                    InputCard.Browse();   // nothing chosen yet: ask now
                    if (_folder.Length == 0) return null;
                }
                root = _folder;
            }

            if (!Directory.Exists(root))
            {
                AppModal.Show(Owner!, DiskScope ? "That drive isn't ready (unplugged, or no disc in it)." : "That folder isn't there any more. Choose it again.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            long? minBytes = LargeFileFinderManager.ParseSize(TxtMinSize.Text, CmbMinSize.SelectedIndex);
            if (minBytes == null)
            {
                TxtSizeError.Visibility = Visibility.Visible;
                UiMotion.Pop(TxtSizeError, 0.9);
                TxtMinSize.Focus();
                return null;
            }

            return new LargeFileOptions
            {
                RootPath = root,
                IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true,
                IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true,
                ShowHiddenFiles = ChkShowHidden.IsChecked == true,
                MinSizeBytes = minBytes.Value,
                ResultLimit = SelectedLimit,
            };
        }

        // =================================================================
        //  Run bar (Find / Stop)
        // =================================================================

        private async void BtnFindLargeFiles_Click(object sender, RoutedEventArgs e)
        {
            switch (_state)
            {
                case RunState.Scanning:
                    _scanCts?.Cancel();
                    TxtProgress.Text = "Stopping… (the files found so far stay in the list)";
                    return;
                case RunState.Acting:
                    _actionCts?.Cancel();
                    TxtProgress.Text = "Stopping after the current file… (a half-copied file is removed)";
                    return;
            }

            var options = ReadOptions();
            if (options != null) await RunScanAsync(options);
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || _lastOptions == null) return;
            if (!Directory.Exists(_lastOptions.RootPath))
            {
                AppModal.Show(Owner!, "That folder or drive isn't there any more.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await RunScanAsync(_lastOptions);
        }

        private async Task RunScanAsync(LargeFileOptions options)
        {
            int version = ++_scanVersion;
            _scanCts = new CancellationTokenSource();
            _filter = null;
            _allFiles = new List<LargeFileItem>();
            _results.Clear();
            BreakdownPanel.Visibility = Visibility.Collapsed;
            SummaryPanel.Visibility = Visibility.Collapsed;
            ProgressSearch.IsIndeterminate = true;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = "Looking…";
            SetEmpty("Looking for big files…", "The biggest ones appear here as they're found.", PackIconKind.FileFindOutline);
            ShowList(false);
            SetState(RunState.Scanning);

            var progress = new Progress<LargeFileProgress>(p =>
            {
                if (version != _scanVersion || _state != RunState.Scanning) return;
                TxtProgress.Text = $"{p.FilesScanned:N0} files in {p.FoldersScanned:N0} folders · {p.Matches:N0} large · {p.CurrentFolder}";
                if (p.Matches > 0) TxtProgressPercent.Text = LargeFileText.Bytes(p.MatchedBytes);
                if (p.Top is { Count: > 0 } top) ShowLive(top, options.ResultLimit);
            });

            LargeFileScanResult result;
            try
            {
                result = await LargeFileFinderManager.ScanAsync(options, progress, _scanCts.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                result = new LargeFileScanResult { Error = LargeFileFinderManager.PlainError(ex) };
            }
            finally
            {
                _scanCts.Dispose();
                _scanCts = null;
                _lastOptions = options;
                SetState(RunState.Idle);
            }

            ShowResult(result, options);
        }

        /// <summary>The biggest so far, while the scan runs.</summary>
        private void ShowLive(IReadOnlyList<LargeFileItem> top, int limit)
        {
            var list = (limit > 0 ? top.Take(limit) : top).ToList();
            LargeFileFinderManager.ScaleBars(list);
            _results.Clear();
            _results.AddRange(list);
            ShowList(true);
        }

        private void ShowResult(LargeFileScanResult result, LargeFileOptions options)
        {
            ProgressSearch.IsIndeterminate = false;
            ProgressSearch.Value = result.Cancelled ? 0 : 100;

            if (result.Error != null && result.Files.Count == 0)
            {
                TxtProgress.Text = result.Error;
                TxtProgressPercent.Text = "";
                SetEmpty("Couldn't search there", result.Error, PackIconKind.AlertCircleOutline);
                ShowList(false);
                return;
            }

            _allFiles = result.Files.ToList();
            _hiddenByLimit = Math.Max(0, result.TotalMatches - result.Files.Count);
            _skippedFolders = result.InaccessibleFolders;
            LargeFileFinderManager.ScaleBars(_allFiles);

            TxtProgress.Text = LargeFileFinderManager.Describe(result, result.Files.Count);
            TxtProgressPercent.Text = result.TotalMatches > 0 ? LargeFileText.Bytes(result.TotalMatchedBytes) : "";

            if (_allFiles.Count == 0)
            {
                string size = LargeFileText.Bytes(options.MinSizeBytes);
                SetEmpty(result.Cancelled ? "Stopped before anything was found" : $"Nothing of {size} or more",
                    options.ShowHiddenFiles ? "Try a smaller size." : "Try a smaller size, or turn on hidden files (AppData often holds big caches).",
                    PackIconKind.FileCheckOutline);
            }

            RefreshAll(animate: true);

            if (!result.Cancelled && result.Elapsed.TotalSeconds > 5)
                Toast("Large file search finished", $"{result.TotalMatches:N0} large {LargeFileText.Plural(result.TotalMatches, "file", "files")} ({LargeFileText.Bytes(result.TotalMatchedBytes)}).");
        }

        private void SetState(RunState state)
        {
            _state = state;
            bool busy = state != RunState.Idle;
            OptionsScrollViewer.IsEnabled = !busy;
            CategoryChips.IsEnabled = !busy;
            BtnRefresh.IsEnabled = !busy && _lastOptions != null;
            BtnExportCsv.IsEnabled = !busy && _results.Count > 0;

            if (busy)
            {
                IconFind.Kind = PackIconKind.Stop;
                TxtFindLabel.Text = state == RunState.Scanning ? "Stop search" : "Stop";
            }
            else
            {
                IconFind.Kind = PackIconKind.FileFindOutline;
                TxtFindLabel.SetResourceReference(TextBlock.TextProperty, "Str_LargeFile_SearchButton");
            }
            UiMotion.Pop(BtnFindLargeFiles, 0.96);
            UpdateActionButtons();
        }

        // =================================================================
        //  Results: list, pills, size bar by kind, filter chips
        // =================================================================

        /// <summary>After a scan, a delete or a move: list (with the kind filter), pills, size bar and chips.</summary>
        private void RefreshAll(bool animate)
        {
            if (_filter is { } f && _allFiles.All(i => i.Category != f)) _filter = null;
            LargeFileFinderManager.ScaleBars(_allFiles);
            ApplyFilter();
            UpdatePills(animate);
            BuildBreakdown(animate);
            BtnRefresh.IsEnabled = !IsBusy && _lastOptions != null;
        }

        private void ApplyFilter()
        {
            var list = _filter is { } f ? _allFiles.Where(i => i.Category == f).ToList() : _allFiles;
            _results.Clear();
            _results.AddRange(list);
            ShowList(_results.Count > 0);
            BtnExportCsv.IsEnabled = !IsBusy && _results.Count > 0;
            UpdateActionButtons();
        }

        private void UpdatePills(bool animate)
        {
            bool any = _allFiles.Count > 0 || _skippedFolders > 0;
            if (!any)
            {
                SummaryPanel.Visibility = Visibility.Collapsed;
                return;
            }

            int n = _allFiles.Count;
            TxtSummaryCount.Text = _hiddenByLimit > 0
                ? $"{n:N0} biggest of {n + _hiddenByLimit:N0}"
                : $"{n:N0} {LargeFileText.Plural(n, "file", "files")}";
            TxtSummaryTotalSize.Text = $"{LargeFileText.Bytes(_allFiles.Sum(i => i.SizeBytes))} total";
            PillSkipped.Visibility = _skippedFolders > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSkipped.Text = $"{_skippedFolders:N0} {LargeFileText.Plural(_skippedFolders, "folder", "folders")} skipped";

            bool wasHidden = SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = Visibility.Visible;
            if (animate && wasHidden) UiMotion.FadeIn(SummaryPanel, 4);
            else if (animate) UiMotion.Pop(PillTotal, 0.9);
        }

        private static SolidColorBrush KindBrush(LargeFileCategory category)
        {
            if (KindBrushes.TryGetValue(category, out var brush)) return brush;
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(LargeFileFinderManager.ColorFor(category));
            }
            catch (FormatException)
            {
                color = Colors.Gray;
            }
            brush = new SolidColorBrush(color);
            brush.Freeze();
            KindBrushes[category] = brush;
            return brush;
        }

        /// <summary>One bar split by kind (what is the space?) and a chip per kind that filters the list.</summary>
        private void BuildBreakdown(bool animate)
        {
            SizeBar.Children.Clear();
            SizeBar.ColumnDefinitions.Clear();
            CategoryChips.Children.Clear();

            var groups = LargeFileFinderManager.Breakdown(_allFiles);
            long total = groups.Sum(g => g.Bytes);
            if (groups.Count == 0 || total <= 0)
            {
                BreakdownPanel.Visibility = Visibility.Collapsed;
                return;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                var (category, count, bytes) = groups[i];
                string name = LargeFileFinderManager.CategoryName(category);
                string tip = $"{name}: {count:N0} {LargeFileText.Plural(count, "file", "files")}, {LargeFileText.Bytes(bytes)} ({bytes * 100.0 / total:0}%)";

                SizeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(bytes, total / 500.0), GridUnitType.Star) });
                var segment = new Border
                {
                    Background = KindBrush(category),
                    Margin = new Thickness(0, 0, i == groups.Count - 1 ? 0 : 1.5, 0),
                    ToolTip = tip,
                    Cursor = Cursors.Hand,
                    Opacity = _filter == null || _filter == category ? 1 : 0.35,
                };
                Grid.SetColumn(segment, i);
                var captured = category;
                segment.MouseLeftButtonUp += (_, _) => SetFilter(_filter == captured ? null : captured);
                SizeBar.Children.Add(segment);
            }

            CategoryChips.Children.Add(MakeChip(null, $"All · {LargeFileText.Bytes(total)}", null, $"{_allFiles.Count:N0} files"));
            foreach (var (category, count, bytes) in groups)
                CategoryChips.Children.Add(MakeChip(category, $"{LargeFileFinderManager.CategoryName(category)} · {LargeFileText.Bytes(bytes)}", KindBrush(category),
                    $"{count:N0} {LargeFileText.Plural(count, "file", "files")} - click to show only these"));

            bool wasHidden = BreakdownPanel.Visibility != Visibility.Visible;
            BreakdownPanel.Visibility = Visibility.Visible;
            if (animate && wasHidden) UiMotion.FadeIn(BreakdownPanel, 4);
        }

        private RadioButton MakeChip(LargeFileCategory? category, string text, Brush? dot, string tip)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (dot != null)
                content.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

            var chip = new RadioButton
            {
                Content = content,
                GroupName = "LargeFileKind",
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = tip,
                IsChecked = _filter == category,
            };
            chip.SetResourceReference(StyleProperty, "ModeToggleButtonStyle");
            chip.Click += (_, _) =>
            {
                UiMotion.Pop(chip, 0.9);
                SetFilter(category);
            };
            return chip;
        }

        private void SetFilter(LargeFileCategory? category)
        {
            if (IsBusy || _filter == category) return;
            _filter = category;
            ApplyFilter();
            BuildBreakdown(animate: false);
        }

        private void ShowList(bool any)
        {
            DataGridLargeFiles.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any == (EmptyStatePanel.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(EmptyStatePanel, !any);
        }

        private void SetEmpty(string title, string hint, PackIconKind icon)
        {
            TxtEmptyTitle.Text = title;
            TxtEmptyHint.Text = hint;
            EmptyBadge.Tag = icon;
        }

        // =================================================================
        //  Selection, keyboard, Explorer
        // =================================================================

        private List<LargeFileItem> SelectedFiles => DataGridLargeFiles.SelectedItems.OfType<LargeFileItem>().ToList();

        private void DataGridLargeFiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionButtons();

        private void UpdateActionButtons()
        {
            int count = DataGridLargeFiles.SelectedItems.Count;
            bool can = count > 0 && !IsBusy;
            BtnMoveSelected.IsEnabled = can;
            BtnDeleteSelected.IsEnabled = can;
        }

        private async void DataGridLargeFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            switch (e.Key)
            {
                case Key.Delete when !IsBusy && DataGridLargeFiles.SelectedItems.Count > 0:
                    e.Handled = true;
                    await DeleteFilesAsync(SelectedFiles);
                    break;
                case Key.Enter when DataGridLargeFiles.SelectedItem is LargeFileItem item:
                    e.Handled = true;
                    ShowInExplorer(item);
                    break;
                case Key.C when Keyboard.Modifiers == ModifierKeys.Control && DataGridLargeFiles.SelectedItems.Count > 0:
                    e.Handled = true;
                    try
                    {
                        Clipboard.SetText(string.Join(Environment.NewLine, SelectedFiles.Select(i => i.FullPath)));
                        TxtProgress.Text = $"Copied {DataGridLargeFiles.SelectedItems.Count:N0} {LargeFileText.Plural(DataGridLargeFiles.SelectedItems.Count, "path", "paths")}.";
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        // clipboard busy in another program - not worth an error box
                    }
                    break;
            }
        }

        private void DataGridLargeFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Only a double-click on a row (not the header or the scrollbar).
            if (e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(DataGridLargeFiles, source) is DataGridRow { Item: LargeFileItem item })
                ShowInExplorer(item);
        }

        private void BtnOpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: LargeFileItem item }) ShowInExplorer(item);
        }

        private void ShowInExplorer(LargeFileItem item)
        {
            try
            {
                if (File.Exists(item.FullPath)) Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
                else if (Directory.Exists(item.Folder)) Process.Start("explorer.exe", $"\"{item.Folder}\"");
                else TxtProgress.Text = $"\"{item.Name}\" and its folder aren't there any more.";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                TxtProgress.Text = "Couldn't open Explorer: " + ex.Message;
            }
        }

        // =================================================================
        //  Delete
        // =================================================================

        private async void BtnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: LargeFileItem item }) await DeleteFilesAsync(new List<LargeFileItem> { item });
        }

        private async void BtnDeleteSelected_Click(object sender, RoutedEventArgs e) => await DeleteFilesAsync(SelectedFiles);

        private async Task DeleteFilesAsync(List<LargeFileItem> items)
        {
            if (IsBusy || items.Count == 0) return;

            // Ask once per drive whether it has a Recycle Bin (a network drive doesn't - Windows deletes for good there).
            var roots = items.Select(i => System.IO.Path.GetPathRoot(i.FullPath) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var recycle = await Task.Run(() => roots.ToDictionary(r => r, r => r.Length > 0 && LargeFileFinderManager.HasRecycleBin(r), StringComparer.OrdinalIgnoreCase));
            int toBin = items.Count(i => recycle[System.IO.Path.GetPathRoot(i.FullPath) ?? ""]);
            int forGood = items.Count - toBin;

            string what = $"{items.Count:N0} {LargeFileText.Plural(items.Count, "file", "files")} ({LargeFileText.Bytes(items.Sum(i => i.SizeBytes))})";
            string question = forGood == 0
                ? $"Send {what} to the Recycle Bin?"
                : toBin == 0
                    ? $"Delete {what} permanently?\n\nThere's no Recycle Bin on that drive, so this can't be undone."
                    : $"Delete {what}?\n\n{toBin:N0} go to the Recycle Bin. {forGood:N0} {LargeFileText.Plural(forGood, "is", "are")} on a drive without one and will be deleted permanently.";
            string names = string.Join("\n", items.Take(5).Select(i => "• " + i.Name)) + (items.Count > 5 ? $"\n…and {items.Count - 5:N0} more" : "");

            if (AppModal.Show(Owner!, $"{question}\n\n{names}", DialogTitle, MessageBoxButton.YesNo,
                    forGood > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            _actionCts = new CancellationTokenSource();
            SetState(RunState.Acting);
            ProgressSearch.IsIndeterminate = true;
            TxtProgress.Text = $"Deleting {what}…";

            LargeFileActionResult result;
            try
            {
                result = await LargeFileFinderManager.DeleteAsync(items, _actionCts.Token);
            }
            finally
            {
                _actionCts.Dispose();
                _actionCts = null;
                ProgressSearch.IsIndeterminate = false;
                SetState(RunState.Idle);
            }

            RemoveFromList(result.Succeeded);
            int done = result.Succeeded.Count;
            TxtProgress.Text = done == 0
                ? "Nothing was deleted."
                : $"Deleted {done:N0} {LargeFileText.Plural(done, "file", "files")} ({LargeFileText.Bytes(result.Bytes)})"
                  + (result.Permanent ? (forGood == items.Count ? " permanently." : " - some permanently.") : " - they're in the Recycle Bin.")
                  + (result.Cancelled ? " Stopped before the rest." : "");
            ShowFailures("Some files couldn't be deleted:", result.Failed);
        }

        // =================================================================
        //  Move
        // =================================================================

        private async void BtnMoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var items = SelectedFiles;
            if (IsBusy || items.Count == 0) return;

            var dialog = new OpenFolderDialog { Title = $"Move {items.Count:N0} {LargeFileText.Plural(items.Count, "file", "files")} to…" };
            if (dialog.ShowDialog(Owner) != true) return;
            string destination = dialog.FolderName;

            string? problem = await Task.Run(() => LargeFileFinderManager.CheckMove(items, destination));
            if (problem != null)
            {
                AppModal.Show(Owner!, problem, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _actionCts = new CancellationTokenSource();
            SetState(RunState.Acting);
            ProgressSearch.IsIndeterminate = false;
            ProgressSearch.Value = 0;
            TxtProgressPercent.Text = "0%";

            var progress = new Progress<LargeFileMoveProgress>(p =>
            {
                if (_state != RunState.Acting) return;
                ProgressSearch.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                TxtProgress.Text = p.Done >= p.Total
                    ? "Finishing…"
                    : $"Moving {p.Done + 1:N0} of {p.Total:N0}: {p.CurrentFile}"
                      + (p.BytesTotal > 0 ? $" · {LargeFileText.Bytes(p.BytesDone)} of {LargeFileText.Bytes(p.BytesTotal)}" : "");
            });

            LargeFileActionResult result;
            try
            {
                result = await LargeFileFinderManager.MoveAsync(items, destination, progress, _actionCts.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                result = new LargeFileActionResult();
                foreach (var item in items) result.Failed.Add((item, LargeFileFinderManager.PlainError(ex)));
            }
            finally
            {
                _actionCts.Dispose();
                _actionCts = null;
                SetState(RunState.Idle);
            }

            RemoveFromList(result.Succeeded);
            int done = result.Succeeded.Count;
            ProgressSearch.Value = done > 0 ? 100 : 0;
            TxtProgressPercent.Text = _allFiles.Count > 0 ? LargeFileText.Bytes(_allFiles.Sum(i => i.SizeBytes)) : "";
            TxtProgress.Text = (done == 0
                    ? "Nothing was moved."
                    : $"Moved {done:N0} {LargeFileText.Plural(done, "file", "files")} ({LargeFileText.Bytes(result.Bytes)}) to {destination}.")
                + (result.Cancelled ? " Stopped - the rest weren't touched." : "");

            var problems = result.Failed.Concat(result.Warnings).ToList();
            ShowFailures(result.Warnings.Count > 0
                ? "Some files need a look (a copied file whose original couldn't be removed is now in both places):"
                : "Some files couldn't be moved:", problems);

            if (done > 0) Toast("Files moved", $"{done:N0} {LargeFileText.Plural(done, "file", "files")} ({LargeFileText.Bytes(result.Bytes)}) moved.");
        }

        private void RemoveFromList(IReadOnlyCollection<LargeFileItem> gone)
        {
            if (gone.Count == 0) return;
            var set = new HashSet<LargeFileItem>(gone);
            _allFiles.RemoveAll(set.Contains);
            if (_allFiles.Count == 0)
                SetEmpty("All cleared", "Nothing large is left in that list. Search again to look for more.", PackIconKind.CheckCircleOutline);
            RefreshAll(animate: true);
        }

        private void ShowFailures(string heading, IReadOnlyList<(LargeFileItem Item, string Error)> failures)
        {
            if (failures.Count == 0) return;
            string lines = string.Join("\n", failures.Take(8).Select(f => $"• {f.Item.Name}: {f.Error}"));
            if (failures.Count > 8) lines += $"\n…and {failures.Count - 8:N0} more";
            AppModal.Show(Owner!, $"{heading}\n\n{lines}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // =================================================================
        //  Export
        // =================================================================

        private async void MenuExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || _results.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save the list",
                Filter = "CSV file (*.csv)|*.csv",
                FileName = $"large-files-{DateTime.Now:yyyy-MM-dd}.csv",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(Owner) != true) return;

            var rows = _results.ToList();
            BtnExportCsv.IsEnabled = false;
            try
            {
                await LargeFileFinderManager.ExportCsvAsync(rows, dialog.FileName);
                TxtProgress.Text = $"Saved {rows.Count:N0} {LargeFileText.Plural(rows.Count, "row", "rows")} to {System.IO.Path.GetFileName(dialog.FileName)}.";
                UiMotion.Pop(BtnExportCsv, 0.9);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppModal.Show(Owner!, "Couldn't save the list: " + LargeFileFinderManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExportCsv.IsEnabled = !IsBusy && _results.Count > 0;
            }
        }

        // =================================================================
        //  Settings (how to search - never which folder or drive)
        // =================================================================

        private void ApplySettings(LargeFileSettings s)
        {
            _applyingSettings = true;
            try
            {
                RadioScopeFolder.IsChecked = s.Scope == LargeFileScope.Folder;
                RadioScopeDisk.IsChecked = s.Scope == LargeFileScope.Drive;
                ChkIncludeSubfolders.IsChecked = s.IncludeSubfolders;
                ChkIgnoreSystemFiles.IsChecked = s.IgnoreSystemFiles;
                ChkShowHidden.IsChecked = s.ShowHiddenFiles;
                TxtMinSize.Text = s.MinSize;
                CmbMinSize.SelectedIndex = Math.Clamp(s.MinUnit, 0, CmbMinSize.Items.Count - 1);
                var chip = LimitChipList.FirstOrDefault(c => c.Tag is string t && t == s.ResultLimit.ToString()) ?? RadioLimit50;
                chip.IsChecked = true;
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings() =>
            LargeFileFinderManager.SaveSettings(new LargeFileSettings
            {
                Scope = DiskScope ? LargeFileScope.Drive : LargeFileScope.Folder,
                IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true,
                IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true,
                ShowHiddenFiles = ChkShowHidden.IsChecked == true,
                MinSize = TxtMinSize.Text.Trim(),
                MinUnit = Math.Max(0, CmbMinSize.SelectedIndex),
                ResultLimit = SelectedLimit,
            });

        private void QueueSave()
        {
            if (Ignore) return;
            _saveTimer.Stop();
            _saveTimer.Start();
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
    }
}
