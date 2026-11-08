using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Empty Folder Cleaner - UI only. Finding the folders and the Recycle Bin delete are in <see cref="EmptyFolderManager"/>.
    /// Nothing is deleted without a confirmation, and each folder is checked again right before it goes.
    /// </summary>
    public partial class EmptyFoldersView : UserControl
    {
        public sealed class FoundFolder : INotifyPropertyChanged
        {
            private bool _isChecked = true;
            public required EmptyFolder Folder { get; init; }
            public bool IsChecked
            {
                get => _isChecked;
                set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly ObservableCollection<EmptyFolder> _roots = new();
        private readonly Smart_Tools.WPF.Models.BulkObservableCollection<FoundFolder> _found = new();
        private readonly DispatcherTimer _doneTimer;
        private CancellationTokenSource? _cts;
        private bool _busy;

        public EmptyFoldersView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _roots;
            FoundList.ItemsSource = _found;
            _roots.CollectionChanged += (_, _) => InputCard.SetEmpty(_roots.Count == 0);
            _found.CollectionChanged += (_, _) => UpdateState();
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); DonePill.Visibility = Visibility.Collapsed; };
        }

        private void Root_Loaded(object sender, RoutedEventArgs e) => UpdateState();
        private void Root_Unloaded(object sender, RoutedEventArgs e) { }

        // =================================================================
        //  Folders to check
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_busy) return;
            foreach (string path in e.Paths)
            {
                if (!Directory.Exists(path) || _roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                _roots.Add(new EmptyFolder(path, 0));
            }
            _found.Clear();
            HideStatus();
            UpdateState();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            _roots.Clear();
            _found.Clear();
            HideStatus();
            UpdateState();
        }

        private void BtnRemoveRoot_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.Tag is not EmptyFolder root) return;
            _roots.Remove(root);
            _found.Clear();
            UpdateState();
        }

        // =================================================================
        //  Finding
        // =================================================================

        private async void BtnFind_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();   // the same button stops a running search
                return;
            }
            if (_roots.Count == 0)
            {
                ShowStatus("Add at least one folder to check.", StatusKind.Warning);
                InputCard.PopDropArea();
                return;
            }

            var roots = _roots.Select(r => r.Path).ToList();
            _found.Clear();
            HideStatus();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);

            EmptyFolderScan? scan = null;
            try
            {
                scan = await Task.Run(() => EmptyFolderManager.Find(roots, token));
            }
            catch (OperationCanceledException)
            {
            }
            SetBusy(false);

            if (scan == null)
            {
                ShowStatus("Stopped.", StatusKind.Warning);
                return;
            }
            _found.AddRange(scan.Folders.Select(f => new FoundFolder { Folder = f }).ToList());   // one change, one UpdateState

            TxtEmptyTitle.Text = "No empty folders found";
            TxtEmptyHint.Text = $"Looked through {scan.FoldersLooked:N0} folder{(scan.FoldersLooked == 1 ? "" : "s")}.";
            if (scan.Inaccessible > 0)
                ShowStatus($"{scan.Inaccessible:N0} folder{(scan.Inaccessible == 1 ? "" : "s")} couldn't be opened and {(scan.Inaccessible == 1 ? "was" : "were")} left out.", StatusKind.Warning);
            UpdateState();
        }

        // =================================================================
        //  Selecting, deleting
        // =================================================================

        private void FoundCheck_Click(object sender, RoutedEventArgs e) => UpdateState();

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _found) f.IsChecked = true;
            UpdateState();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _found) f.IsChecked = false;
            UpdateState();
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var chosen = _found.Where(f => f.IsChecked).ToList();
            if (chosen.Count == 0 || _busy) return;

            int nested = chosen.Sum(f => f.Folder.Nested);
            string message = $"Move {chosen.Count:N0} empty folder{(chosen.Count == 1 ? "" : "s")}" +
                             (nested > 0 ? $" (and the {nested:N0} empty folder{(nested == 1 ? "" : "s")} inside)" : "") +
                             " to the Recycle Bin?\n\nYou can restore them from the Recycle Bin.";
            if (System.Windows.MessageBox.Show(message, Loc.T("Str_Tool_empty-folders_Name", "Empty Folder Cleaner"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var paths = chosen.Select(f => f.Folder.Path).ToList();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            EmptyFolderDelete result;
            try
            {
                result = await Task.Run(() => EmptyFolderManager.Delete(paths, token));
            }
            catch (OperationCanceledException)
            {
                result = new EmptyFolderDelete(0, new List<string>(), 0);
            }
            SetBusy(false);

            // Take away what is gone; keep what could not be deleted so it can be tried again.
            var keep = new HashSet<string>(result.Failed, StringComparer.OrdinalIgnoreCase);
            _found.RemoveWhere(f => f.IsChecked && !keep.Contains(f.Folder.Path) && !Directory.Exists(f.Folder.Path));

            TxtDonePill.Text = $"Moved {result.Deleted:N0} to the Recycle Bin";
            DonePill.Visibility = Visibility.Visible;
            _doneTimer.Stop();
            _doneTimer.Start();

            var notes = new List<string>();
            if (result.NoLongerEmpty > 0) notes.Add($"{result.NoLongerEmpty:N0} {(result.NoLongerEmpty == 1 ? "folder" : "folders")} got a file since the search, so {(result.NoLongerEmpty == 1 ? "it was" : "they were")} left alone.");
            if (result.Failed.Count > 0) notes.Add($"{result.Failed.Count:N0} couldn't be deleted (in use or no permission).");
            if (notes.Count > 0) ShowStatus(string.Join(" ", notes), StatusKind.Warning);
            else HideStatus();
            UpdateState();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            FoundList.IsEnabled = !busy;
            BtnDelete.IsEnabled = !busy && _found.Any(f => f.IsChecked);
            BtnSelectAll.IsEnabled = BtnSelectNone.IsEnabled = !busy && _found.Count > 0;
            TxtFind.Text = busy ? "Stop" : "Find empty folders";
            IconFind.Kind = busy ? PackIconKind.Stop : PackIconKind.Magnify;
        }

        private void UpdateState()
        {
            ListEmpty.Visibility = _found.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_found.Count == 0 && TxtEmptyTitle.Text != "No empty folders found")
            {
                TxtEmptyTitle.Text = _roots.Count == 0 ? "Add a folder to check" : "Ready to search";
                TxtEmptyHint.Text = _roots.Count == 0 ? "Drop folders on the left, then press Find empty folders." : "Press Find empty folders.";
            }
            if (_found.Count > 0)
            {
                int selected = _found.Count(f => f.IsChecked);
                PillCount.Visibility = Visibility.Visible;
                TxtPillCount.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} found · {1:N0} selected", _found.Count, selected);
                TxtDelete.Text = selected > 0 ? $"Move {selected:N0} to Recycle Bin" : "Move to Recycle Bin";
            }
            else
            {
                PillCount.Visibility = Visibility.Collapsed;
                TxtDelete.Text = "Move to Recycle Bin";
            }
            if (!_busy)
            {
                BtnDelete.IsEnabled = _found.Any(f => f.IsChecked);
                BtnSelectAll.IsEnabled = BtnSelectNone.IsEnabled = _found.Count > 0;
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private enum StatusKind { Warning, Error }

        private void ShowStatus(string text, StatusKind kind)
        {
            string bg = kind == StatusKind.Error ? "StatusErrorBg" : "StatusWarningBg";
            string fg = kind == StatusKind.Error ? "StatusErrorFg" : "StatusWarningFg";
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        /// <summary>Called when the window closes: stops a running search or delete.</summary>
        public void CancelActiveRun()
        {
            _cts?.Cancel();
            _doneTimer.Stop();
        }
    }
}
