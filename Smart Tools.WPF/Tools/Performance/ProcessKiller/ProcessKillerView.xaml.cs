using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Process Killer - UI only. Reading, ending, port and locked-file lookups are in
    /// <see cref="ProcessKillerManager"/>. The list is synced in place each refresh so the
    /// selection and scroll stay; reading stops while the page isn't shown.
    /// </summary>
    public partial class ProcessKillerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int MaxNamesInConfirm = 8;

        private readonly ProcessKillerManager _manager = new();
        private readonly BulkObservableCollection<ProcessKillerRow> _rows = new();
        private readonly ListCollectionView _view;
        private readonly ProcessKillerSettings _settings;
        private readonly DispatcherTimer _searchTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly bool _viewConstructed;

        private HashSet<int>? _focusPids;
        private HashSet<int>? _searchPortPids;
        private int _searchVersion;
        private bool _ending;
        private int _detailsVersion;
        private bool _firstSnapshot = true;

        private enum StatusKind { Success, Error, Info }

        public ProcessKillerView()
        {
            InitializeComponent();

            _settings = ProcessKillerManager.LoadSettings();

            _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = o => o is ProcessKillerRow r && ProcessKillerManager.Matches(r, TxtSearch.Text, _settings.Filter, _focusPids, _searchPortPids);
            _view.IsLiveSorting = true;
            GridProcesses.ItemsSource = _view;
            // Sorted by a changing value (CPU), rows would jump away from under the pointer: the order is held while the mouse is over the list.
            GridProcesses.MouseEnter += (_, _) => _view.IsLiveSorting = false;
            GridProcesses.MouseLeave += (_, _) => _view.IsLiveSorting = true;
            ProcessIconConverter.IconsLoaded += OnIconsLoaded;

            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _searchTimer.Tick += async (_, _) =>
            {
                _searchTimer.Stop();
                await ApplySearchAsync();
            };

            _statusTimer = new DispatcherTimer();
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            _manager.SnapshotReady += Manager_SnapshotReady;
            _manager.RefreshSeconds = _settings.RefreshSeconds;

            RadioFilterAll.IsChecked = _settings.Filter == ProcessKillerFilter.All;
            RadioFilterApps.IsChecked = _settings.Filter == ProcessKillerFilter.Apps;
            RadioFilterBackground.IsChecked = _settings.Filter == ProcessKillerFilter.Background;
            RadioFilterWindows.IsChecked = _settings.Filter == ProcessKillerFilter.Windows;
            RadioRefresh0.IsChecked = _settings.RefreshSeconds == 0;
            RadioRefresh1.IsChecked = _settings.RefreshSeconds == 1;
            RadioRefresh2.IsChecked = _settings.RefreshSeconds == 2;
            RadioRefresh5.IsChecked = _settings.RefreshSeconds == 5;
            ChkAskSingle.IsChecked = _settings.ConfirmSingleEnd;
            _settingsApplied = true;
            if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);

            ApplySort(_settings.SortColumn, _settings.SortDescending);
            ElevationBanner.ShowIfNotElevated();
            UpdatePills(null);
            UpdateSelection();

            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _searchTimer.Stop();
            _statusTimer.Stop();
            SaveSettings();
            _manager.Dispose();
        }

        /// <summary>Read only while the page is on screen.</summary>
        private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) _manager.Start();
            else _manager.Stop();
        }

        private void Manager_SnapshotReady(ProcessKillerSnapshot snapshot)
        {
            ProcessKillerManager.Merge(_rows, snapshot.Processes);
            UpdatePills(snapshot);
            UpdateEmptyState();
            UpdateSelection(refreshDetails: false);

            if (_firstSnapshot)
            {
                _firstSnapshot = false;
                UiMotion.FadeIn(GridProcesses);
            }
        }

        // =================================================================
        //  Sidebar: search, filter, refresh
        // =================================================================

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            BtnClearSearch.Visibility = TxtSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }

        private void RadioFilter_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.Filter = RadioFilterApps.IsChecked == true ? ProcessKillerFilter.Apps
                : RadioFilterBackground.IsChecked == true ? ProcessKillerFilter.Background
                : RadioFilterWindows.IsChecked == true ? ProcessKillerFilter.Windows
                : ProcessKillerFilter.All;
            SaveSettings();
            RefreshFilter();
        }

        /// <summary>A number in the search box can also be a port: look up which processes own it, then filter.</summary>
        private async Task ApplySearchAsync()
        {
            int version = ++_searchVersion;
            HashSet<int>? portPids = null;
            int? port = ProcessKillerManager.ParsePort(TxtSearch.Text);
            if (port != null)
            {
                try
                {
                    portPids = (await ProcessKillerManager.FindPortUsesAsync(port.Value)).Select(u => u.Pid).ToHashSet();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    portPids = null;
                }
            }
            if (version != _searchVersion) return;
            _searchPortPids = portPids;
            RefreshFilter();
        }

        private void RefreshFilter()
        {
            _view.Refresh();
            UpdateEmptyState();
            UpdatePills(null);
        }

        private void RadioRefresh_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int seconds)) return;
            _settings.RefreshSeconds = seconds;
            _manager.RefreshSeconds = seconds;
            SaveSettings();
        }

        private void BtnRefreshNow_Click(object sender, RoutedEventArgs e) => _manager.RefreshNow();

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarCollapsed(!_settings.SidebarCollapsed);
            SaveSettings();
        }

        private void SetSidebarCollapsed(bool collapsed)
        {
            _settings.SidebarCollapsed = collapsed;
            var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Port and locked-file lookups ("show only these")
        // =================================================================

        private async void BtnFindPort_Click(object sender, RoutedEventArgs e) => await FindPortAsync();

        private async void TxtPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await FindPortAsync();
        }

        private async Task FindPortAsync()
        {
            int? port = ProcessKillerManager.ParsePort(TxtPort.Text);
            if (port == null)
            {
                ShowStatus(Str("Str_ProcessKiller_PortInvalid"), StatusKind.Error);
                TxtPort.Focus();
                TxtPort.SelectAll();
                return;
            }

            List<EndpointUse> uses;
            try
            {
                uses = await ProcessKillerManager.FindPortUsesAsync(port.Value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ShowStatus(ex.Message, StatusKind.Error);
                return;
            }

            if (uses.Count == 0)
            {
                ClearFocus();
                ShowStatus(string.Format(Str("Str_ProcessKiller_PortFree"), port), StatusKind.Info);
                return;
            }

            var first = uses[0];
            string endpoint = $"{first.Protocol} {first.LocalAddress}:{first.LocalPort}" + (first.State.Length > 0 ? $" · {first.State}" : string.Empty);
            string more = uses.Count > 1 ? " " + string.Format(Str("Str_ProcessKiller_AndMore"), uses.Count - 1) : string.Empty;
            SetFocus(uses.Select(u => u.Pid), string.Format(Str("Str_ProcessKiller_PortFocus"), port, endpoint + more), PackIconKind.LanConnect);
        }

        private async void DropArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = Str("Str_ProcessKiller_LockHeader"), CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) await FindLockersAsync(dialog.FileName);
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && File.Exists(paths[0]);
            e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
            if (ok) DropArea.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e) =>
            DropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");

        private async void DropArea_Drop(object sender, DragEventArgs e)
        {
            DropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && File.Exists(paths[0]))
                await FindLockersAsync(paths[0]);
            else
                ShowStatus(Str("Str_ProcessKiller_LockOneFile"), StatusKind.Error);
        }

        private async Task FindLockersAsync(string path)
        {
            string name = Path.GetFileName(path);
            List<ProcessKillerLocker> lockers;
            try
            {
                lockers = await ProcessKillerManager.FindLockersAsync(path);
            }
            catch (Win32Exception ex)
            {
                ShowStatus(string.Format(Str("Str_ProcessKiller_LockFailed"), name, ex.Message), StatusKind.Error);
                return;
            }

            if (lockers.Count == 0)
            {
                ClearFocus();
                ShowStatus(string.Format(Str("Str_ProcessKiller_LockFree"), name), StatusKind.Info);
                return;
            }

            string apps = string.Join(", ", lockers.Select(l => string.IsNullOrWhiteSpace(l.AppName) ? $"PID {l.Pid}" : l.AppName).Distinct());
            SetFocus(lockers.Select(l => l.Pid), string.Format(Str("Str_ProcessKiller_LockFocus"), name, apps), PackIconKind.FileLockOutline);
        }

        private void SetFocus(IEnumerable<int> pids, string message, PackIconKind icon)
        {
            _focusPids = pids.ToHashSet();
            TxtFocus.Text = message;
            IconFocus.Kind = icon;
            if (FocusBanner.Visibility != Visibility.Visible) UiMotion.FadeVisibility(FocusBanner, true);
            else UiMotion.Pop(FocusBanner, 0.97);
            RefreshFilter();
            _manager.RefreshNow();
        }

        private void BtnClearFocus_Click(object sender, RoutedEventArgs e) => ClearFocus();

        private void ClearFocus()
        {
            if (_focusPids == null) return;
            _focusPids = null;
            UiMotion.FadeVisibility(FocusBanner, false);
            RefreshFilter();
        }

        // =================================================================
        //  List: sorting, selection, details
        // =================================================================

        private void ApplySort(string member, bool descending)
        {
            var direction = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            foreach (var column in GridProcesses.Columns)
                column.SortDirection = column.SortMemberPath == member ? direction : null;

            using (_view.DeferRefresh())
            {
                _view.SortDescriptions.Clear();
                _view.SortDescriptions.Add(new SortDescription(member, direction));
                if (member != nameof(ProcessKillerRow.Pid))
                    _view.SortDescriptions.Add(new SortDescription(nameof(ProcessKillerRow.Pid), ListSortDirection.Ascending));
            }

            // Live sorting keeps CPU/memory order right as values change each refresh.
            _view.LiveSortingProperties.Clear();
            _view.LiveSortingProperties.Add(member);
        }

        private void GridProcesses_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            string member = e.Column.SortMemberPath;
            bool numeric = member is nameof(ProcessKillerRow.CpuPercent) or nameof(ProcessKillerRow.MemoryBytes) or nameof(ProcessKillerRow.Threads);
            bool descending = e.Column.SortDirection switch
            {
                ListSortDirection.Ascending => true,
                ListSortDirection.Descending => false,
                _ => numeric,   // biggest first for numbers, A→Z for text
            };

            _settings.SortColumn = member;
            _settings.SortDescending = descending;
            ApplySort(member, descending);
            SaveSettings();
        }

        private List<ProcessKillerRow> SelectedRows() => GridProcesses.SelectedItems.OfType<ProcessKillerRow>().ToList();

        private void GridProcesses_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

        /// <summary>Right-click selects the row under the pointer (unless it's already part of the selection).</summary>
        private void GridProcesses_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is { } row && !row.IsSelected)
            {
                GridProcesses.SelectedItems.Clear();
                row.IsSelected = true;
            }
        }

        private void GridProcesses_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is { Item: ProcessKillerRow row })
                OpenLocation(row);
        }

        private void UpdateSelection(bool refreshDetails = true)
        {
            var selected = SelectedRows();
            int endable = selected.Count(r => r.CanEnd);

            BtnEnd.IsEnabled = endable > 0 && !_ending;
            BtnEndTree.IsEnabled = selected.Count > 0 && !_ending;
            TxtEndLabel.Text = selected.Count > 1
                ? string.Format(Str("Str_ProcessKiller_EndCount"), endable)
                : Str("Str_ProcessKiller_EndProcess");

            if (selected.Count == 0)
            {
                TxtSelection.Text = Str("Str_ProcessKiller_SelectHint");
            }
            else if (selected.Count == 1)
            {
                var r = selected[0];
                TxtSelection.Text = r.Block switch
                {
                    ProcessKillerBlock.Self => Str("Str_ProcessKiller_BlockSelf"),
                    ProcessKillerBlock.None => $"{r.Name} · PID {r.Pid}",
                    _ => Str("Str_ProcessKiller_BlockCritical"),
                };
            }
            else
            {
                TxtSelection.Text = string.Format(Str("Str_ProcessKiller_SelectedCount"), selected.Count);
            }

            if (selected.Count == 1)
            {
                var r = selected[0];
                TxtDetailPath.Text = r.Path ?? Str("Str_ProcessKiller_NoAccess");
                BtnOpenLocation.IsEnabled = r.Path != null;
                TxtDetailParent.Text = r.ParentName != null ? $"{r.ParentName} · PID {r.ParentPid}" : r.ParentPid > 0 ? $"PID {r.ParentPid}" : "—";
                TxtDetailWindow.Text = r.WindowTitle ?? "—";
                TxtDetailStarted.Text = r.StartedLocal?.ToString("g", CultureInfo.CurrentCulture) ?? "—";
                if (refreshDetails) _ = LoadCommandLineAsync(r);
                if (DetailsPanel.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DetailsPanel, true);
            }
            else if (DetailsPanel.Visibility == Visibility.Visible)
            {
                _detailsVersion++;
                UiMotion.FadeVisibility(DetailsPanel, false);
            }
        }

        private async Task LoadCommandLineAsync(ProcessKillerRow row)
        {
            int version = ++_detailsVersion;
            TxtDetailCommand.Text = "…";
            string? command = await ProcessKillerManager.GetCommandLineAsync(row.Pid);
            if (version != _detailsVersion) return;
            TxtDetailCommand.Text = string.IsNullOrWhiteSpace(command) ? Str("Str_ProcessKiller_NoAccess") : command;
            TxtDetailCommand.ToolTip = command;
        }

        private void UpdatePills(ProcessKillerSnapshot? snapshot)
        {
            int shown = _view.Count;
            TxtCount.Text = shown == _rows.Count
                ? string.Format(Str("Str_ProcessKiller_CountAll"), _rows.Count)
                : string.Format(Str("Str_ProcessKiller_CountShown"), shown, _rows.Count);

            if (snapshot == null) return;
            TxtTotalCpu.Text = $"CPU {snapshot.TotalCpuPercent:0}%";
            TxtTotalMemory.Text = snapshot.MemoryTotalBytes > 0
                ? $"{ProcessKillerRow.FormatBytes(snapshot.MemoryUsedBytes)} / {ProcessKillerRow.FormatBytes(snapshot.MemoryTotalBytes)}"
                : "—";
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count > 0 && _view.Count == 0;
            if ((EmptyPanel.Visibility == Visibility.Visible) != empty) UiMotion.FadeVisibility(EmptyPanel, empty);
        }

        // =================================================================
        //  Ending processes
        // =================================================================

        private async void BtnEnd_Click(object sender, RoutedEventArgs e) => await EndSelectedAsync(tree: false);

        private async void BtnEndTree_Click(object sender, RoutedEventArgs e) => await EndSelectedAsync(tree: true);

        private async void MenuEnd_Click(object sender, RoutedEventArgs e) => await EndSelectedAsync(tree: false);

        private async void MenuEndTree_Click(object sender, RoutedEventArgs e) => await EndSelectedAsync(tree: true);

        private async Task EndSelectedAsync(bool tree)
        {
            var selected = SelectedRows();
            if (selected.Count == 0 || _ending) return;

            var targets = new List<ProcessKillerRow>();
            var seen = new HashSet<ProcessKillerRow>();
            foreach (var root in selected)
            {
                foreach (var r in tree ? ProcessKillerManager.TreeChildrenFirst(_rows, root) : [root])
                    if (seen.Add(r)) targets.Add(r);
            }

            var endable = targets.Where(r => r.CanEnd).ToList();
            int blocked = targets.Count - endable.Count;
            if (endable.Count == 0)
            {
                ShowStatus(Str(targets.Any(r => r.Block == ProcessKillerBlock.Self) && targets.Count == 1
                    ? "Str_ProcessKiller_BlockSelf"
                    : "Str_ProcessKiller_BlockCritical"), StatusKind.Error);
                return;
            }

            if (!Confirm(selected, endable, blocked, tree)) return;

            _ending = true;
            UpdateSelection(refreshDetails: false);
            try
            {
                var results = await ProcessKillerManager.EndAsync(endable);
                ReportResults(results, blocked);
            }
            finally
            {
                _ending = false;
                UpdateSelection(refreshDetails: false);
                _manager.RefreshNow();
            }
        }

        private bool Confirm(List<ProcessKillerRow> selected, List<ProcessKillerRow> endable, int blocked, bool tree)
        {
            // One ordinary program (not a Windows process, not a tree): the person may have chosen not to be asked.
            bool single = !tree && selected.Count == 1 && endable.Count == 1 && endable[0].Kind != ProcessKillerKind.Windows;
            if (single && !_settings.ConfirmSingleEnd) return true;

            var sb = new StringBuilder();
            string heading;
            if (tree && selected.Count == 1)
                heading = string.Format(Str("Str_ProcessKiller_ConfirmTree"), selected[0].Name, endable.Count - 1);
            else if (endable.Count == 1)
                heading = string.Format(Str("Str_ProcessKiller_ConfirmOne"), endable[0].Name, endable[0].Pid);
            else
                heading = string.Format(Str("Str_ProcessKiller_ConfirmMany"), endable.Count);
            sb.AppendLine(heading);

            if (endable.Count > 1)
            {
                sb.AppendLine();
                foreach (var r in endable.Take(MaxNamesInConfirm)) sb.AppendLine($"  • {r.Name} (PID {r.Pid})");
                if (endable.Count > MaxNamesInConfirm) sb.AppendLine("  • " + string.Format(Str("Str_ProcessKiller_AndMore"), endable.Count - MaxNamesInConfirm));
            }

            sb.AppendLine();
            sb.AppendLine(Str("Str_ProcessKiller_ConfirmUnsaved"));
            if (endable.Any(r => r.Kind == ProcessKillerKind.Windows)) sb.AppendLine(Str("Str_ProcessKiller_ConfirmWindows"));
            if (blocked > 0) sb.AppendLine(string.Format(Str("Str_ProcessKiller_ConfirmSkipped"), blocked));

            if (!single)
            {
                return MessageBox.Show(
                    Window.GetWindow(this)!,
                    sb.ToString().TrimEnd(),
                    Str("Str_ProcessKiller_ConfirmTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
            }

            // The native task dialog has a tick box a MessageBox cannot have.
            var page = new System.Windows.Forms.TaskDialogPage
            {
                Caption = Str("Str_ProcessKiller_ConfirmTitle"),
                Heading = heading,
                Text = sb.ToString().TrimEnd()[heading.Length..].Trim(),
                Icon = System.Windows.Forms.TaskDialogIcon.Warning,
                AllowCancel = true,
                Verification = new System.Windows.Forms.TaskDialogVerificationCheckBox(Str("Str_ProcessKiller_DontAskAgain")),
            };
            page.Buttons.Add(System.Windows.Forms.TaskDialogButton.Yes);
            page.Buttons.Add(System.Windows.Forms.TaskDialogButton.No);
            page.DefaultButton = System.Windows.Forms.TaskDialogButton.No;

            var owner = Window.GetWindow(this);
            var answer = owner != null
                ? System.Windows.Forms.TaskDialog.ShowDialog(new WindowInteropHelper(owner).Handle, page)
                : System.Windows.Forms.TaskDialog.ShowDialog(page);
            if (answer != System.Windows.Forms.TaskDialogButton.Yes) return false;
            if (page.Verification!.Checked) ChkAskSingle.IsChecked = false;      // the check box handler saves it
            return true;
        }

        private void ReportResults(List<ProcessKillerEndResult> results, int blocked)
        {
            int ended = results.Count(r => r.Outcome is ProcessKillerEndOutcome.Ended or ProcessKillerEndOutcome.AlreadyExited);
            int denied = results.Count(r => r.Outcome == ProcessKillerEndOutcome.AccessDenied);
            int failed = results.Count(r => r.Outcome is ProcessKillerEndOutcome.Failed or ProcessKillerEndOutcome.Blocked) + blocked;

            var parts = new List<string> { string.Format(Str("Str_ProcessKiller_ResultEnded"), ended) };
            if (denied > 0) parts.Add(string.Format(Str("Str_ProcessKiller_ResultDenied"), denied));
            if (failed > 0) parts.Add(string.Format(Str("Str_ProcessKiller_ResultFailed"), failed));

            string message = string.Join(" · ", parts);
            var firstError = results.FirstOrDefault(r => r.Outcome == ProcessKillerEndOutcome.Failed)?.Message;
            if (firstError != null) message += $" ({firstError})";

            ShowStatus(message, denied + failed > 0 ? StatusKind.Error : StatusKind.Success);
        }

        // =================================================================
        //  Location, clipboard, keys
        // =================================================================

        private void BtnOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRows() is [var row]) OpenLocation(row);
        }

        private void MenuOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRows().FirstOrDefault() is { } row) OpenLocation(row);
        }

        private void OpenLocation(ProcessKillerRow row)
        {
            if (row.Path == null || !File.Exists(row.Path))
            {
                ShowStatus(Str("Str_ProcessKiller_NoAccess"), StatusKind.Info);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("/select,");
                psi.ArgumentList.Add(row.Path);
                Process.Start(psi);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                ShowStatus(ex.Message, StatusKind.Error);
            }
        }

        private void RowMenu_Opened(object sender, RoutedEventArgs e)
        {
            var selected = SelectedRows();
            MenuEnd.IsEnabled = selected.Any(r => r.CanEnd);
            MenuEndTree.IsEnabled = selected.Count > 0;
            MenuOpenLocation.IsEnabled = selected.FirstOrDefault()?.Path != null;
            MenuCopyPath.IsEnabled = selected.Any(r => r.Path != null);
        }

        private void MenuCopyName_Click(object sender, RoutedEventArgs e) => CopyLines(r => r.Name);

        private void MenuCopyPid_Click(object sender, RoutedEventArgs e) => CopyLines(r => r.Pid.ToString());

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e) => CopyLines(r => r.Path);

        private void CopyLines(Func<ProcessKillerRow, string?> pick)
        {
            var lines = SelectedRows().Select(pick).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (lines.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                ShowStatus(Str("Str_ProcessKiller_Copied"), StatusKind.Success);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Clipboard busy (another app holds it) - nothing to do.
            }
        }

        private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                _manager.RefreshNow();
            }
            else if (e.Key == Key.F && mods == ModifierKeys.Control)
            {
                e.Handled = true;
                TxtSearch.Focus();
                TxtSearch.SelectAll();
            }
            else if (e.Key == Key.Escape && TxtSearch.IsKeyboardFocusWithin && TxtSearch.Text.Length > 0)
            {
                e.Handled = true;
                TxtSearch.Clear();
            }
            else if (e.Key == Key.Delete && GridProcesses.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                await EndSelectedAsync(tree: mods == ModifierKeys.Shift);
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private void ShowStatus(string message, StatusKind kind)
        {
            TxtStatus.Text = message;
            BorderStatus.ToolTip = message;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, kind switch
            {
                StatusKind.Success => "StatusSuccessFg",
                StatusKind.Error => "StatusErrorFg",
                _ => "TextSecondary",
            });
            IconStatus.Kind = kind switch
            {
                StatusKind.Success => PackIconKind.CheckCircleOutline,
                StatusKind.Error => PackIconKind.AlertCircleOutline,
                _ => PackIconKind.InformationOutline,
            };

            if (BorderStatus.Visibility != Visibility.Visible) UiMotion.FadeVisibility(BorderStatus, true);
            else UiMotion.Pop(BorderStatus);

            _statusTimer.Stop();
            _statusTimer.Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 9 : 5);
            _statusTimer.Start();
        }

        private void SaveSettings()
        {
            try
            {
                ProcessKillerManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Process Killer settings: {ex.Message}");
            }
        }

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null && child is not T)
                child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
            return child as T;
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;

        private bool _settingsApplied;

        private void ChkAskSingle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_settingsApplied) return;
            _settings.ConfirmSingleEnd = ChkAskSingle.IsChecked == true;
            SaveSettings();
        }

        /// <summary>Icons are extracted in the background; when some arrive, the rows with those programs read them again.</summary>
        private void OnIconsLoaded(IReadOnlyCollection<string> paths)
        {
            var loaded = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
                if (row.Path != null && loaded.Contains(row.Path)) row.RefreshIcon();
        }
    }

    /// <summary>
    /// Executable path → its 16 px icon. Icons are read from the disk in the background, a few at a time; the grid shows the cog until
    /// one arrives, then <see cref="IconsLoaded"/> tells the rows to read it again. Nothing here touches the disk on the UI thread.
    /// </summary>
    public sealed class ProcessIconConverter : IValueConverter
    {
        private const int MaxCached = 3000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Requested = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending = new();
        private static int _working;

        /// <summary>Raised on the UI thread with the paths whose icons just arrived.</summary>
        public static event Action<IReadOnlyCollection<string>>? IconsLoaded;

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || path.Length == 0) return null;
            if (Cache.TryGetValue(path, out var cached)) return cached;
            if (Requested.TryAdd(path, 0))
            {
                Pending.Enqueue(path);
                StartWorker();
            }
            return null;
        }

        private static void StartWorker()
        {
            if (Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            _ = Task.Run(() =>
            {
                var batch = new List<string>();
                void Flush()
                {
                    if (batch.Count == 0) return;
                    var done = batch.ToArray();
                    batch.Clear();
                    dispatcher?.BeginInvoke(() => IconsLoaded?.Invoke(done));
                }

                try
                {
                    if (Cache.Count > MaxCached)
                    {
                        Cache.Clear();      // a long session: start again, the rows on screen ask for theirs
                        Requested.Clear();
                    }
                    while (Pending.TryDequeue(out string? path))
                    {
                        Cache[path] = Extract(path);
                        batch.Add(path);
                        if (batch.Count >= 25) Flush();
                    }
                    Flush();
                }
                finally
                {
                    Volatile.Write(ref _working, 0);
                    if (!Pending.IsEmpty) StartWorker();
                }
            });
        }

        private static ImageSource? Extract(string path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                source.Freeze();
                return source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
