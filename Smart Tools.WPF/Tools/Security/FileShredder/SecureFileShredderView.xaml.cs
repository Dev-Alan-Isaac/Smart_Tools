using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Secure File Shredder - UI only. Overwriting, deleting, finding files
    /// (links are never followed), removing emptied folders and settings are
    /// in <see cref="ShredderManager"/>. Nothing is shredded until Shred is
    /// clicked (and confirmed, when that's on). Folders picked here are kept
    /// for this session only.
    /// </summary>
    public partial class FileShredderView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_file-shredder_Name", "Secure File Shredder");

        private readonly BulkObservableCollection<ShredItem> _queue = new();
        private readonly ObservableCollection<ShredQueueGroup> _groups = new();
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private CancellationTokenSource? _stopAfterFile;   // Stop: finish the current file, then stop
        private CancellationTokenSource? _stopNow;         // Stop now / app closing: cut the current file short (it's still deleted)
        private bool _isShredding;
        private int _adding;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        public FileShredderView()
        {
            InitializeComponent();

            DataGridQueue.ItemsSource = _queue;
            var queueMenu = new ContextMenu();
            BulkCheckMenu.Append(queueMenu, DataGridQueue, () => _queue, i => i.IsChecked, (i, v) => i.IsChecked = v,
                UpdateQueueState, eligible: i => i.Status != ShredStatus.Shredded, canRun: () => !IsBusy);
            DataGridQueue.ContextMenu = queueMenu;
            InputCard.ItemsSource = _groups;
            _queue.CollectionChanged += (_, _) => UpdateQueueState();
            _groups.CollectionChanged += (_, _) => UpdateQueueState();
            _viewConstructed = true;
            UpdateHints();
            UpdateQueueState();
        }

        /// <summary>
        /// Called from MainWindow.Window_Closing. The file being overwritten
        /// is cut short but still deleted - never left half-overwritten (the
        /// old view left it that way when the app closed mid-file).
        /// </summary>
        public void CancelActiveRun()
        {
            try
            {
                _stopAfterFile?.Cancel();
                _stopNow?.Cancel();
                _lifetimeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;
        private bool IsBusy => _isShredding || _adding > 0;

        // =================================================================
        //  Layout
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(ShredderManager.LoadSettings());
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
            ColSize.Visibility = e.NewSize.Width < 900 ? Visibility.Collapsed : Visibility.Visible;

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
        //  Settings (no paths)
        // =================================================================

        private void ApplySettings(ShredderSettings s)
        {
            _applyingSettings = true;
            try
            {
                (s.Method switch
                {
                    ShredMethod.QuickZero => RadioMethodQuick,
                    ShredMethod.Random7Pass => RadioMethodThorough,
                    _ => RadioMethodDod,
                }).IsChecked = true;
                ChkRenameBeforeDelete.IsChecked = s.ScrambleName;
                ChkRemoveEmptyFolders.IsChecked = s.RemoveEmptiedFolders;
                ChkConfirmBeforeShredding.IsChecked = s.ConfirmBeforeShredding;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateHints();
        }

        private ShredderSettings ReadSettings() => new()
        {
            Method = SelectedMethod,
            ScrambleName = ChkRenameBeforeDelete.IsChecked == true,
            RemoveEmptiedFolders = ChkRemoveEmptyFolders.IsChecked == true,
            ConfirmBeforeShredding = ChkConfirmBeforeShredding.IsChecked == true,
            IncludeSubfolders = InputCard.IncludeSubfolders,
        };

        private ShredMethod SelectedMethod =>
            Enum.TryParse(new[] { RadioMethodQuick, RadioMethodDod, RadioMethodThorough }.FirstOrDefault(r => r.IsChecked == true)?.Tag as string, out ShredMethod m)
                ? m : ShredMethod.Dod3Pass;

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateHints();
            ShredderManager.SaveSettings(ReadSettings());
        }

        /// <summary>For folders added from now on - each queue entry shows how it was added.</summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            ShredderManager.SaveSettings(ReadSettings());
        }

        private void UpdateHints()
        {
            if (TxtMethodHint == null) return;
            var method = SelectedMethod;
            TxtMethodHint.Text = ShredderManager.MethodHint(method);
            var ticked = _queue.Where(i => i.IsChecked && i.Status != ShredStatus.Shredded).ToList();
            long size = ticked.Sum(i => i.SizeBytes);
            TxtWriteEstimate.Text = ticked.Count == 0
                ? ""
                : $"Writes {ShredText.Bytes(ShredderManager.BytesToWrite(ticked, method))} in total ({ShredderManager.PassCount(method)} × {ShredText.Bytes(size)}).";
        }

        // =================================================================
        //  Adding files
        // =================================================================

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isShredding && e.Data.GetDataPresent(DataFormats.FileDrop);
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
            if (!_isShredding && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) _ = AddPathsAsync(paths);
        }

        /// <summary>Adds to the list only - nothing is shredded until Shred is clicked. Folders are read off the UI thread.</summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isShredding) return;
            var token = _lifetimeCts.Token;
            bool subfolders = InputCard.IncludeSubfolders;
            _adding++;
            UpdateQueueState();
            TxtProgress.Text = "Looking for files…";
            try
            {
                int added = 0, inaccessible = 0, links = 0;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    if (IsDangerousFolder(folder))
                    {
                        AppModal.Show(Loc.F("Str_MB_IsADriveOr", "\"{0}\" is a drive or a system folder. Shredding it would damage Windows or take everything on it, so it can't be added.\n\nAdd the specific folders or files you want to shred instead.", folder),
                            DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }
                    var scan = await ShredderManager.ScanFolderAsync(folder, subfolders, token);
                    inaccessible += scan.Inaccessible;
                    links += scan.LinksSkipped;
                    var (items, fileLinks) = await ShredderManager.CreateItemsAsync(scan.Files, token);
                    links += fileLinks;
                    added += ShredderManager.AddFolderToQueue(_queue, _known, _groups, folder, subfolders, items);
                }
                var loose = paths.Where(p => !Directory.Exists(p)).ToList();
                if (loose.Count > 0)
                {
                    var (items, fileLinks) = await ShredderManager.CreateItemsAsync(loose, token);
                    links += fileLinks;
                    added += ShredderManager.AddFilesToQueue(_queue, _known, _groups, items);
                }

                if (added > 0)
                {
                    InputCard.PopDropArea();
                    UiMotion.Pop(SummaryPill, 0.9);
                }
                var parts = new List<string>
                {
                    added == 0 ? "Nothing new added - the files may already be in the list." : $"Added {added:N0} {ShredText.Plural(added, "file", "files")}. Nothing is shredded until you click Shred.",
                };
                if (links > 0) parts.Add($"{links:N0} {ShredText.Plural(links, "link was", "links were")} skipped (they could point at other files).");
                if (inaccessible > 0) parts.Add($"{inaccessible:N0} {ShredText.Plural(inaccessible, "folder", "folders")} couldn't be opened.");
                if (!_isShredding) TxtProgress.Text = string.Join(" ", parts);
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            finally
            {
                _adding--;
                UpdateQueueState();
            }
        }

        /// <summary>Whole drives and Windows/program folders are refused outright - one mis-drop would be catastrophic.</summary>
        private static bool IsDangerousFolder(string folder) => ShredderManager.IsProtectedFolder(
            folder,
            new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            },
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // =================================================================
        //  The list
        // =================================================================

        private void UpdateQueueState()
        {
            int count = _queue.Count, ticked = 0, shredded = 0, failed = 0;
            long size = 0;
            foreach (var item in _queue)
            {
                if (item.IsChecked && item.Status != ShredStatus.Shredded)
                {
                    ticked++;
                    size += item.SizeBytes;
                }
                if (item.Status == ShredStatus.Shredded) shredded++;
                else if (item.Status is ShredStatus.Failed or ShredStatus.Skipped) failed++;
            }

            EmptyStatePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SummaryPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryCount.Text = $"{count:N0} {ShredText.Plural(count, "file", "files")} · {ticked:N0} ticked ({ShredText.Bytes(size)})";
            PillShredded.Visibility = shredded > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtShreddedCount.Text = $"{shredded:N0} shredded";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtFailedCount.Text = $"{failed:N0} not shredded";
            InputCard.SetEmpty(_groups.Count == 0);
            ChkIncludeAll.IsChecked = count > 0 && _queue.All(i => i.IsChecked) ? true : _queue.Any(i => i.IsChecked) ? null : false;

            bool idle = !IsBusy;
            BtnShredSelected.IsEnabled = _isShredding || (idle && ticked > 0);
            BtnSelectAll.IsEnabled = idle && count > 0;
            BtnDeselectAll.IsEnabled = idle && ticked > 0;
            BtnRemoveSelected.IsEnabled = idle && _queue.Any(i => i.IsChecked);
            UpdateHints();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_isShredding && sender is System.Windows.Controls.CheckBox { DataContext: ShredItem item })
            {
                item.IsChecked = !item.IsChecked;   // the run already has its list - undo the click
                return;
            }
            UpdateQueueState();
        }

        private void ChkIncludeAll_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) return;
            bool tick = ChkIncludeAll.IsChecked == true;
            foreach (var item in _queue) item.IsChecked = tick;
            UpdateQueueState();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) return;
            foreach (var item in _queue) item.IsChecked = true;
            UpdateQueueState();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) return;
            foreach (var item in _queue) item.IsChecked = false;
            UpdateQueueState();
        }

        /// <summary>Delete takes highlighted rows off the list - it never shreds.</summary>
        private void DataGridQueue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !IsBusy && DataGridQueue.SelectedItems.Count > 0)
            {
                RemoveRows(DataGridQueue.SelectedItems.Cast<ShredItem>().ToList());
                e.Handled = true;
            }
        }

        private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy && sender is Button { Tag: ShredItem item }) RemoveRows(new[] { item });
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy) RemoveRows(_queue.Where(i => i.IsChecked).ToList());
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not Button { Tag: ShredQueueGroup group }) return;
            int n = ShredderManager.RemoveGroup(_queue, _known, _groups, group);
            ResetProgress($"Took {group.Name} ({n:N0} {ShredText.Plural(n, "file", "files")}) off the list. Nothing was deleted.");
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!IsBusy) _ = AddPathsAsync(e.Paths);
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e) => InputCard_ClearRequested(sender, e);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (IsBusy || _queue.Count == 0) return;
            ShredderManager.ClearQueue(_queue, _known, _groups);
            ResetProgress("The list is empty. Nothing was deleted.");
        }

        private void RemoveRows(IReadOnlyCollection<ShredItem> rows)
        {
            if (rows.Count == 0) return;
            ShredderManager.RemoveFromQueue(_queue, _known, _groups, rows);
            ResetProgress($"Took {rows.Count:N0} {ShredText.Plural(rows.Count, "file", "files")} off the list. Nothing was deleted.");
        }

        private void ResetProgress(string message)
        {
            ProgressShred.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = message;
        }

        // =================================================================
        //  Shred / Stop / Stop now
        // =================================================================

        private async void BtnShredSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isShredding)
            {
                if (_stopAfterFile is { IsCancellationRequested: false })
                {
                    _stopAfterFile.Cancel();   // first click: finish this file, then stop
                    TxtShredLabel.Text = "Stop now";
                    BtnShredSelected.ToolTip = "Stop overwriting this file straight away (it's still deleted)";
                    TxtProgress.Text = "Finishing the current file, then stopping…";
                }
                else
                {
                    _stopNow?.Cancel();        // second click: cut the current file short
                    BtnShredSelected.IsEnabled = false;
                    TxtProgress.Text = "Stopping now…";
                }
                return;
            }

            var targets = _queue.Where(i => i.IsChecked && i.Status != ShredStatus.Shredded).ToList();
            if (targets.Count == 0)
            {
                TxtProgress.Text = "Tick at least one file to shred.";
                return;
            }

            var options = new ShredOptions
            {
                Method = SelectedMethod,
                ScrambleName = ChkRenameBeforeDelete.IsChecked == true,
                RemoveEmptiedFolders = ChkRemoveEmptyFolders.IsChecked == true,
            };

            if (ChkConfirmBeforeShredding.IsChecked == true && !Confirm(targets, options)) return;

            var roots = _groups.Where(g => g.IsFolder).Select(g => g.FolderPath).ToList();
            var stopAfter = new CancellationTokenSource();
            var stopNow = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _stopAfterFile = stopAfter;
            _stopNow = stopNow;
            SetShredding(true);

            var progress = new Progress<ShredProgress>(p =>
            {
                ProgressShred.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                if (stopNow.IsCancellationRequested) return;
                TxtProgress.Text = p.Finishing
                    ? $"Finishing {p.CurrentFile}, then stopping…"
                    : $"{p.FilesDone:N0} of {p.FilesTotal:N0} done · {p.CurrentFile} · {ShredText.Bytes(p.BytesWritten)} of {ShredText.Bytes(p.BytesTotal)} written";
                UpdateQueueState();
            });

            try
            {
                var summary = await ShredderManager.ShredAsync(targets, options, roots, progress, stopAfter.Token, stopNow.Token);
                TxtProgress.Text = ShredderManager.DescribeRun(summary);
                if (!summary.Stopped)
                {
                    ProgressShred.Value = 100;
                    TxtProgressPercent.Text = "100%";
                }

                // Shredded files are gone - take their rows off the list; failed and skipped rows stay with their reason.
                var gone = targets.Where(i => i.Status == ShredStatus.Shredded).ToList();
                if (gone.Count > 0) ShredderManager.RemoveFromQueue(_queue, _known, _groups, gone);

                Toast(summary.Stopped ? "Shredding stopped" : "Shredding complete",
                    $"{summary.Shredded:N0} {ShredText.Plural(summary.Shredded, "file", "files")} shredded ({ShredText.Bytes(summary.BytesFreed)})" +
                    (summary.Failed + summary.Skipped > 0 ? $", {summary.Failed + summary.Skipped:N0} not." : "."));
            }
            finally
            {
                _stopAfterFile = null;
                _stopNow = null;
                stopAfter.Dispose();
                stopNow.Dispose();
                SetShredding(false);
            }
        }

        private bool Confirm(IReadOnlyList<ShredItem> targets, ShredOptions options)
        {
            long size = targets.Sum(i => i.SizeBytes);
            var lines = new List<string>
            {
                $"Shred {targets.Count:N0} {ShredText.Plural(targets.Count, "file", "files")} ({ShredText.Bytes(size)})?",
                "",
                $"Each file is overwritten {ShredderManager.PassCount(options.Method)} {ShredText.Plural(ShredderManager.PassCount(options.Method), "time", "times")}, then deleted. They don't go to the Recycle Bin and can't be brought back.",
            };
            if (options.RemoveEmptiedFolders && _groups.Any(g => g.IsFolder)) lines.Add("Folders this leaves empty are removed too.");
            var first = targets.Take(5).Select(t => "  • " + t.Name).ToList();
            if (targets.Count > 5) first.Add($"  • …and {targets.Count - 5:N0} more");
            lines.Add("");
            lines.AddRange(first);
            return AppModal.Show(string.Join("\n", lines), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        private void SetShredding(bool shredding)
        {
            _isShredding = shredding;
            IconShred.Kind = shredding ? PackIconKind.Stop : PackIconKind.Shredder;
            if (shredding)
            {
                TxtShredLabel.Text = "Stop";
                BtnShredSelected.ToolTip = "Finish the current file, then stop - the rest aren't touched";
                ProgressShred.Value = 0;
                TxtProgressPercent.Text = "0%";
            }
            else
            {
                TxtShredLabel.SetResourceReference(TextBlock.TextProperty, "Str_Shredder_ShredFiles");
                BtnShredSelected.SetResourceReference(ToolTipProperty, "Str_Shredder_ShredFilesTooltip");
            }
            OptionsScrollViewer.IsEnabled = !shredding;   // the run uses what was set when it started
            UpdateQueueState();
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
