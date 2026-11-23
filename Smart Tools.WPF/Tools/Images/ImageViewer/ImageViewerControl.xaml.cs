using Smart_Tools.WPF.Views;
using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Image Viewer - UI only. Finding images, the list, browse order and
    /// zoom maths are in <see cref="ImageViewerManager"/>; decoding (with
    /// photo orientation), thumbnails and saving in ImageViewerManager.Imaging.cs.
    /// The image on screen is tracked by item, never by row number.
    /// <see cref="LoadImage"/> / <see cref="LoadImages"/> stay for other
    /// code that opens images here.
    /// </summary>
    public partial class ImageViewerControl : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double PlaylistExpandedWidth = 380;
        private const int ThumbnailSide = 64;
        private static string DialogTitle => Loc.T("Str_Dlg_ImageViewer", "Image Viewer");

        private readonly BulkObservableCollection<ImageQueueItem> _queue = new();
        private readonly ObservableCollection<ImageQueueGroup> _groups = new();
        private readonly ImageBrowseOrder<ImageQueueItem> _order = new();
        private ImageViewerSettings _settings = new();

        private ImageQueueItem? _current;
        private BitmapSource? _bitmap;
        private ImageViewInfo? _info;
        private int _loadVersion;

        private double _zoom = 1.0;
        private bool _fit = true;
        private bool _isPanning;
        private Point _panStart;
        private double _panStartH, _panStartV;

        private readonly DispatcherTimer _slideshowTimer;
        private bool _slideshowPlaying;

        private CancellationTokenSource _addCts = new();
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource _thumbCts = new();
        private readonly SemaphoreSlim _thumbGate = new(3);   // a 10,000-image folder used to start 10,000 decodes at once

        private bool _optionsCollapsed;
        private bool _playlistCollapsed;
        private bool _theater;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        public ImageViewerControl()
        {
            InitializeComponent();
            DataGridQueue.ItemsSource = _queue;
            QueueList.ItemsSource = _groups;

            _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _slideshowTimer.Tick += SlideshowTimer_Tick;
            _viewConstructed = true;
        }

        // =================================================================
        //  Public API (kept for other code that opens images here)
        // =================================================================

        /// <summary>Shows one image (the list is replaced).</summary>
        public void LoadImage(string path) => LoadImages(new[] { path }, 0);

        /// <summary>Replaces the list with these files and shows the one at <paramref name="startIndex"/>.</summary>
        public async void LoadImages(IReadOnlyList<string> paths, int startIndex = 0)
        {
            ClearAll();
            var token = _addCts.Token;
            List<ImageFileFound> found;
            try
            {
                // Callers may pass any file type they know the viewer can show, so no extension filter here.
                found = await ImageViewerManager.ReadFilesAsync(paths ?? Array.Empty<string>(), token, imagesOnly: false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (token.IsCancellationRequested) return;
            var added = ImageViewerManager.AddToList(_queue, _groups, found, null, isFolder: false, includeSubfolders: false);
            UpdateListUi();
            if (added.Count > 0) await ShowAsync(added[Math.Clamp(startIndex, 0, added.Count - 1)]);
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            StopSlideshow();
            _addCts.Cancel();
            _loadCts?.Cancel();
            _thumbCts.Cancel();
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        // =================================================================
        //  Lifetime, panels, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(ImageViewerManager.LoadSettings());
                _loadedOnce = true;
            }
            UpdateModeUi();
            UpdateListUi();
            if (_current == null) ShowNothing();
            Focus();
        }

        /// <summary>Leaving the page stops the slideshow (the old one looked paused but its button still said Pause).</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            StopSlideshow();
            _isPanning = false;
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            ApplyPanels();
        }

        private void BtnTogglePlaylist_Click(object sender, RoutedEventArgs e)
        {
            _playlistCollapsed = !_playlistCollapsed;
            if (!_playlistCollapsed && _theater) _theater = false;
            ApplyPanels();
            SaveSettings();
        }

        private void BtnTheater_Click(object sender, RoutedEventArgs e) => ToggleTheater();

        private void ToggleTheater()
        {
            _theater = !_theater;
            ApplyPanels();
            UiMotion.Pop(Stage, 0.99);
        }

        private void ApplyPanels()
        {
            OptionsSidebar.Visibility = _theater ? Visibility.Collapsed : Visibility.Visible;
            OptionsColumn.Width = new GridLength(_theater ? 0 : _optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            OptionsGapColumn.Width = new GridLength(_theater ? 0 : 14);
            var v = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = v;
            SepHeader.Visibility = v;
            OptionsScrollViewer.Visibility = v;

            bool playlistHidden = _theater || _playlistCollapsed;
            PlaylistColumn.Width = new GridLength(playlistHidden ? 0 : PlaylistExpandedWidth);
            PlaylistPanel.Visibility = playlistHidden ? Visibility.Collapsed : Visibility.Visible;
            IconTogglePlaylist.Kind = playlistHidden ? PackIconKind.ChevronLeft : PackIconKind.ChevronRight;
            IconTheater.Kind = _theater ? PackIconKind.ArrowCollapse : PackIconKind.ArrowExpand;
        }

        private ImageBrowseMode SelectedMode => RadioBrowseShuffle.IsChecked == true ? ImageBrowseMode.Shuffle : ImageBrowseMode.Sequential;

        private RadioButton[] SpeedRadios => new[] { RadioSpeed2, RadioSpeed3, RadioSpeed5, RadioSpeed10 };

        private int SelectedSeconds =>
            SpeedRadios.FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && int.TryParse(s, out int v) ? v : 3;

        private void ApplySettings(ImageViewerSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                (s.BrowseMode == ImageBrowseMode.Shuffle ? RadioBrowseShuffle : RadioBrowseSequential).IsChecked = true;
                _order.Mode = s.BrowseMode;
                (SpeedRadios.FirstOrDefault(r => (string)r.Tag == s.SlideshowSeconds.ToString()) ?? RadioSpeed3).IsChecked = true;
                _slideshowTimer.Interval = TimeSpan.FromSeconds(SelectedSeconds);
                ChkIncludeSubfolders.IsChecked = s.IncludeSubfolders;
                _playlistCollapsed = s.PlaylistCollapsed;
                ApplyPanels();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            _settings.BrowseMode = SelectedMode;
            _settings.SlideshowSeconds = SelectedSeconds;
            _settings.IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true;
            _settings.PlaylistCollapsed = _playlistCollapsed;
            try
            {
                ImageViewerManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Image viewer settings not saved: {ex.Message}");
            }
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
        }

        /// <summary>Click, not Checked: Checked also fires when the settings are applied.</summary>
        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            _order.Mode = SelectedMode;
            _order.Reset();
            UpdateModeUi();
            SaveSettings();
        }

        private void Speed_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            _slideshowTimer.Interval = TimeSpan.FromSeconds(SelectedSeconds);
            if (_slideshowPlaying)
            {
                _slideshowTimer.Stop();
                _slideshowTimer.Start();
                SlideshowCountdownRing.Start(_slideshowTimer.Interval);
            }
            SaveSettings();
        }

        private void UpdateModeUi()
        {
            bool shuffle = SelectedMode == ImageBrowseMode.Shuffle;
            TxtModeHint.Text = shuffle
                ? "Every image once in a random order, then a new order."
                : "Goes through the list in order, back to the start after the last image.";
            IconModePill.Kind = shuffle ? PackIconKind.ShuffleVariant : PackIconKind.ImageMultipleOutline;
        }

        // =================================================================
        //  Adding images
        // =================================================================

        private void BtnAddImages_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = Loc.T("Str_ImageViewer_MsgChooseImages", "Choose images"), Multiselect = true, Filter = ImageViewerManager.FileDialogFilter };
            if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FileNames);
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Str_ImageViewer_MsgChooseFolders", "Choose folders of images"), Multiselect = true };
            if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FolderNames);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
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
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        /// <summary>
        /// Adds files and folders off the UI thread (the old viewer read
        /// folders on the UI thread and froze on big ones). The first new
        /// image shows straight away when nothing is on screen - before, you
        /// had to find it in the list and double-click it.
        /// </summary>
        /// <summary>Adds files or folders to the queue, as if they had been dropped here (the Media Viewer hub forwards drops this way).</summary>
        public void AddPaths(IEnumerable<string> paths) => _ = AddPathsAsync(paths.ToList());

        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            var token = _addCts.Token;
            var folders = paths.Where(Directory.Exists).ToList();
            var files = paths.Where(p => !Directory.Exists(p)).ToList();
            bool subfolders = ChkIncludeSubfolders.IsChecked == true;
            var notes = new List<string>();
            ImageQueueItem? first = null;
            int addedCount = 0;

            ProgressLoading.Visibility = Visibility.Visible;
            try
            {
                if (files.Count > 0)
                {
                    var found = await ImageViewerManager.ReadFilesAsync(files, token);
                    token.ThrowIfCancellationRequested();   // cleared while reading: don't refill the list
                    var added = ImageViewerManager.AddToList(_queue, _groups, found, null, isFolder: false, includeSubfolders: false);
                    first ??= added.FirstOrDefault();
                    addedCount += added.Count;
                    int skipped = files.Count - found.Count;
                    if (skipped > 0) notes.Add($"{skipped:N0} {ImageViewerText.Plural(skipped, "file isn't an image", "files aren't images")}");
                }
                foreach (string folder in folders)
                {
                    var scan = await ImageViewerManager.ScanFolderAsync(folder, subfolders, token);
                    token.ThrowIfCancellationRequested();
                    var added = ImageViewerManager.AddToList(_queue, _groups, scan.Files, folder, isFolder: true, includeSubfolders: subfolders);
                    first ??= added.FirstOrDefault();
                    addedCount += added.Count;
                    if (scan.InaccessibleFolders > 0) notes.Add($"{scan.InaccessibleFolders:N0} folder(s) in \"{Path.GetFileName(folder.TrimEnd('\\', '/'))}\" couldn't be read");
                    if (scan.Files.Count == 0) notes.Add($"no images in \"{Path.GetFileName(folder.TrimEnd('\\', '/'))}\"");
                }
            }
            catch (OperationCanceledException)
            {
                return;   // the list was cleared meanwhile
            }
            finally
            {
                if (_current == null || _bitmap != null) ProgressLoading.Visibility = Visibility.Collapsed;
            }

            UpdateListUi();
            if (addedCount > 0) UiMotion.Pop(DropArea, 0.97);
            SummaryPill.ToolTip = notes.Count > 0 ? "Last add: " + string.Join(" · ", notes) : "Images in the list";
            if (addedCount == 0 && notes.Count > 0)
                AppModal.Show(Loc.T("Str_MB_NothingWasAdded", "Nothing was added: ") + string.Join("; ", notes) + ".", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);

            if (_current == null && first != null) await ShowAsync(first);
        }

        // =================================================================
        //  The list
        // =================================================================

        private void UpdateListUi()
        {
            int count = _queue.Count;
            QueueEmptyHint.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool any = count > 0;
            UiMotion.FadeVisibility(EmptyStatePanel, !any);
            UiMotion.FadeVisibility(DataGridQueue, any);

            if (any && SummaryPill.Visibility != Visibility.Visible) UiMotion.Pop(SummaryPill, 0.9);
            SummaryPill.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryCount.Text = ImageViewerManager.DescribeList(_queue);

            UpdateCheckButtons();
            BtnClearQueue.IsEnabled = BtnClearSources.IsEnabled = any;
            BtnPrevious.IsEnabled = BtnNext.IsEnabled = count > 1;
            BtnSlideshowPlayPause.IsEnabled = count > 1 || _slideshowPlaying;
            if (count < 2) StopSlideshow();
            UpdatePosition();
        }

        private void UpdateCheckButtons()
        {
            int ticked = _queue.Count(i => i.IsSelected);
            BtnSelectAll.IsEnabled = _queue.Count > 0 && ticked < _queue.Count;
            BtnDeselectAll.IsEnabled = ticked > 0;
            BtnRemoveSelected.IsEnabled = _queue.Count > 0;
            BtnMoveUp.IsEnabled = BtnMoveDown.IsEnabled = DataGridQueue.SelectedItems.Count > 0;
            BtnSortByName.IsEnabled = BtnSortByDate.IsEnabled = _queue.Count > 1;
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e) => UpdateCheckButtons();

        // =================================================================
        //  Order: move up/down, sort by name or date
        // =================================================================

        private void DataGridQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed) UpdateCheckButtons();   // can fire during InitializeComponent
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            var chosen = DataGridQueue.SelectedItems.OfType<ImageQueueItem>().ToList();
            if (!ImageViewerManager.Move(_queue, chosen, direction)) return;
            AfterReorder(chosen);
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_queue.Count < 2) return;
            var chosen = DataGridQueue.SelectedItems.OfType<ImageQueueItem>().ToList();
            var sorted = ImageViewerManager.Sorted(_queue, byDate);
            _queue.Clear();
            _queue.AddRange(sorted);
            ImageViewerManager.Renumber(_queue);
            AfterReorder(chosen);
        }

        /// <summary>Re-applies the highlight (replacing a row drops it); next/previous follow the new order.</summary>
        private void AfterReorder(IReadOnlyList<ImageQueueItem> chosen)
        {
            DataGridQueue.SelectedItems.Clear();
            foreach (var item in chosen) DataGridQueue.SelectedItems.Add(item);
            var first = chosen.OrderBy(c => c.Number).FirstOrDefault();
            if (first != null)
            {
                DataGridQueue.ScrollIntoView(first);
                DataGridQueue.CurrentItem = first;
            }
            _order.Reset();
            UpdateListUi();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _queue) item.IsSelected = true;
            UpdateCheckButtons();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _queue) item.IsSelected = false;
            UpdateCheckButtons();
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ImageQueueGroup group }) RemoveItems(group.Items.ToList());
        }

        private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ImageQueueItem item }) RemoveItems(new[] { item });
        }

        /// <summary>The ticked images, or the highlighted rows when nothing is ticked.</summary>
        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var ticked = _queue.Where(i => i.IsSelected).ToList();
            var targets = ticked.Count > 0 ? ticked : DataGridQueue.SelectedItems.OfType<ImageQueueItem>().ToList();
            if (targets.Count == 0)
            {
                UiMotion.Pop(BtnRemoveSelected, 0.95);
                return;
            }
            RemoveItems(targets);
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e) => ClearAll();

        /// <summary>
        /// Removes images from the list (never from disk). When the image on
        /// screen goes, the one that took its place is shown.
        /// </summary>
        private void RemoveItems(IReadOnlyCollection<ImageQueueItem> items)
        {
            if (items.Count == 0) return;
            ImageQueueItem? replacement = null;
            bool currentGone = _current != null && items.Contains(_current);
            int currentIndex = _current == null ? -1 : _queue.IndexOf(_current);

            ImageViewerManager.RemoveFromList(_queue, _groups, items);

            if (currentGone)
            {
                // Removed items keep their old numbers: count those that were above the current image.
                int above = items.Count(i => i.Number - 1 < currentIndex);
                replacement = ImageViewerManager.NeighbourAfterRemoval(_queue, currentIndex - above);
                _current = null;
                if (replacement != null) _ = ShowAsync(replacement);
                else ShowNothing();
            }
            UpdateListUi();
        }

        private void ClearAll()
        {
            StopSlideshow();
            _addCts.Cancel();
            _addCts = new CancellationTokenSource();
            _thumbCts.Cancel();
            _thumbCts = new CancellationTokenSource();
            _queue.Clear();
            _groups.Clear();
            _current = null;
            ShowNothing();
            UpdateListUi();
        }

        /// <summary>Thumbnails load only for rows scrolled into view, a few at a time.</summary>
        private void DataGridQueue_LoadingRow(object? sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is ImageQueueItem { Thumbnail: null, ThumbnailRequested: false } item)
            {
                item.ThumbnailRequested = true;
                _ = LoadThumbnailAsync(item, _thumbCts.Token);
            }
        }

        private async Task LoadThumbnailAsync(ImageQueueItem item, CancellationToken token)
        {
            try
            {
                await _thumbGate.WaitAsync(token);
                try
                {
                    var thumb = await Task.Run(() => ImageViewerManager.LoadThumbnail(item.FullPath, ThumbnailSide), token);
                    if (!token.IsCancellationRequested) item.Thumbnail = thumb;
                }
                finally
                {
                    _thumbGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                item.ThumbnailRequested = false;
            }
        }

        private void DataGridQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<ButtonBase>(source) != null) return;   // the remove button / tick box
            if (FindAncestor<DataGridRow>(source)?.Item is ImageQueueItem item) _ = ShowAsync(item);
        }

        private void DataGridQueue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is ButtonBase) return;
            if (e.Key == Key.Enter && DataGridQueue.SelectedItem is ImageQueueItem item)
            {
                _ = ShowAsync(item);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                RemoveItems(DataGridQueue.SelectedItems.OfType<ImageQueueItem>().ToList());
                e.Handled = true;
            }
        }

        // =================================================================
        //  Context menu
        // =================================================================

        /// <summary>The highlighted row in the list, else the image on screen.</summary>
        private ImageQueueItem? MenuTarget() => DataGridQueue.SelectedItem as ImageQueueItem ?? _current;

        private void MenuPlay_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget() is { } item) _ = ShowAsync(item);
        }

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget() is not { } item) return;
            if (ShellExplorer.Show(item.FullPath) == ShellExplorer.ShowResult.NotFound)
                AppModal.Show(Loc.F("Str_MB_IsNoLongerThere", "\"{0}\" is no longer there.", item.Name), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void MenuDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget() is not { } item) return;
            bool bin = ImageViewerManager.HasRecycleBin(item.FullPath);
            string text = bin
                ? $"Move \"{item.Name}\" to the Recycle Bin?\n\nThis deletes the file from your computer, not just from the list."
                : $"Delete \"{item.Name}\" PERMANENTLY?\n\nIt's on a drive without a Recycle Bin, so it can't be undone.";
            if (AppModal.Show(text, DialogTitle, MessageBoxButton.YesNo, bin ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            try
            {
                ImageViewerManager.DeleteImage(item.FullPath);
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_MB_CouldnTDelete", "Couldn't delete \"{0}\": {1}", item.Name, ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            RemoveItems(new[] { item });
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

        // =================================================================
        //  Showing an image
        // =================================================================

        private void ShowNothing()
        {
            _loadCts?.Cancel();
            _loadVersion++;
            if (_current != null) _current.IsNowPlaying = false;
            _current = null;
            _bitmap = null;
            _info = null;
            MainImage.Source = null;
            TxtFileName.Text = Loc.T("Str_ImageViewer_MsgNothingLoaded", "Nothing loaded");
            TxtFileName.ToolTip = null;
            TxtImageInfo.Text = _queue.Count > 0 ? "Double-click an image in the list to view it." : "Add images on the left, then press Play.";
            TxtEmptyHint.Text = _queue.Count > 0 ? "Pick an image from the list." : "Add images on the left, or drop them here.";
            BtnSaveImageAs.IsEnabled = false;
            LoadErrorPanel.Visibility = Visibility.Collapsed;
            ProgressLoading.Visibility = Visibility.Collapsed;
            UiMotion.FadeVisibility(NoImagePanel, true);
            TxtZoomLevel.Text = "100%";
            UpdatePosition();
        }

        private async Task ShowAsync(ImageQueueItem item)
        {
            if (_current != null) _current.IsNowPlaying = false;
            _current = item;
            item.IsNowPlaying = true;
            int version = ++_loadVersion;

            TxtFileName.Text = item.Name;
            TxtFileName.ToolTip = item.FullPath;
            NoImagePanel.Visibility = Visibility.Collapsed;
            LoadErrorPanel.Visibility = Visibility.Collapsed;
            DataGridQueue.ScrollIntoView(item);
            UpdatePosition();

            _loadCts?.Cancel();
            var cts = _loadCts = new CancellationTokenSource();
            // The loading bar only appears when opening takes a moment (large files).
            _ = Task.Delay(150, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Dispatcher.InvokeAsync(() => { if (version == _loadVersion) ProgressLoading.Visibility = Visibility.Visible; });
            }, TaskScheduler.Default);

            (BitmapSource Bitmap, ImageViewInfo Info)? result;
            try
            {
                result = await Task.Run(() => ImageViewerManager.LoadForView(item.FullPath), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // A newer image was picked meanwhile: drop this one - including its error
            // (the old viewer showed a stale "couldn't preview" over the new image).
            if (version != _loadVersion) return;
            cts.Cancel();
            ProgressLoading.Visibility = Visibility.Collapsed;

            if (result is not { } loaded)
            {
                _bitmap = null;
                _info = null;
                MainImage.Source = null;
                BtnSaveImageAs.IsEnabled = false;
                TxtImageInfo.Text = $"{item.SizeDisplay} · {ImageViewerManager.FormatName(item.FullPath)}";
                TxtLoadError.Text = Loc.F("Str_ImageViewer_MsgCantOpen", "Couldn't open \"{0}\" - the file is damaged, or it's in a format this viewer can't read.", item.Name);
                UiMotion.FadeVisibility(LoadErrorPanel, true);
                return;
            }

            _bitmap = loaded.Bitmap;
            _info = loaded.Info;
            MainImage.Source = loaded.Bitmap;
            // One image pixel per screen unit at 100%. With Stretch="None" a 300-dpi scan showed at a third of its size.
            MainImage.Width = loaded.Bitmap.PixelWidth;
            MainImage.Height = loaded.Bitmap.PixelHeight;
            ImageRotate.Angle = 0;
            ApplyFit();
            BtnSaveImageAs.IsEnabled = true;
            UpdateInfo();
            UiMotion.FadeIn(MainImage, 0);
        }

        private void UpdateInfo()
        {
            if (_current == null || _info == null) return;
            var parts = new List<string>
            {
                $"{_info.OriginalWidth:N0} × {_info.OriginalHeight:N0}",
                ImageViewerText.FormatBytes(_info.SizeBytes),
                _info.Format,
            };
            if (_info.ShownSmaller) parts.Add("shown smaller to save memory");
            if (ImageRotate.Angle != 0) parts.Add($"turned {ImageRotate.Angle:0}° (Save keeps it)");
            TxtImageInfo.Text = string.Join(" · ", parts);
        }

        private void UpdatePosition()
        {
            if (_current == null || _queue.Count == 0)
            {
                PillPosition.Visibility = Visibility.Collapsed;
                return;
            }
            TxtPosition.Text = $"{_current.Number:N0} of {_queue.Count:N0}";
            PillPosition.Visibility = Visibility.Visible;
        }

        // =================================================================
        //  Browsing and the slideshow
        // =================================================================

        private void BtnPrevious_Click(object sender, RoutedEventArgs e) => Step(-1);

        private void BtnNext_Click(object sender, RoutedEventArgs e) => Step(+1);

        private void Step(int direction)
        {
            if (_order.Step(_queue, _current, direction) is ImageQueueItem next) _ = ShowAsync(next);
        }

        private void BtnSlideshowPlayPause_Click(object sender, RoutedEventArgs e) => ToggleSlideshow();

        private void ToggleSlideshow()
        {
            if (_slideshowPlaying)
            {
                StopSlideshow();
                return;
            }
            if (_queue.Count < 2)
            {
                UiMotion.Pop(BtnSlideshowPlayPause, 0.9);
                return;
            }
            _slideshowPlaying = true;
            IconSlideshowPlayPause.Kind = PackIconKind.Pause;
            _slideshowTimer.Interval = TimeSpan.FromSeconds(SelectedSeconds);
            _slideshowTimer.Start();
            // The ring shows in Shuffle too - it counts down to the next image either way.
            SlideshowCountdownRing.Start(_slideshowTimer.Interval);
            if (_current == null) Step(+1);
        }

        private void StopSlideshow()
        {
            _slideshowPlaying = false;
            _slideshowTimer.Stop();
            IconSlideshowPlayPause.Kind = PackIconKind.Play;
            SlideshowCountdownRing.Stop();
        }

        private void SlideshowTimer_Tick(object? sender, EventArgs e)
        {
            SlideshowCountdownRing.Restart();
            Step(+1);
        }


        // ---- The Files card (same as Validate Data / Convert Files / Images to PDF) ----

        private void DropArea_DragOver(object sender, DragEventArgs e) => Root_DragOver(sender, e);

        private void DropArea_DragLeave(object sender, DragEventArgs e) => Root_DragLeave(sender, e);

        private void DropArea_Drop(object sender, DragEventArgs e) => Root_Drop(sender, e);

        // =================================================================
        //  Zoom, turn, pan
        // =================================================================

        /// <summary>What "100%" means: one pixel of the original file per screen unit.</summary>
        private double ActualSizeZoom =>
            _bitmap != null && _info != null && _bitmap.PixelWidth > 0 ? (double)Math.Max(_info.OriginalWidth, _info.OriginalHeight) / Math.Max(_bitmap.PixelWidth, _bitmap.PixelHeight) : 1.0;

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(+1, null);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(-1, null);

        private void BtnZoomFit_Click(object sender, RoutedEventArgs e) => ApplyFit();

        private void BtnZoom100_Click(object sender, RoutedEventArgs e) => ZoomTo(ActualSizeZoom, null);

        private void ZoomStep(int direction, Point? anchor)
        {
            if (_bitmap == null) return;
            // Steps are in "percent of the original", so they land on round numbers.
            double actual = ActualSizeZoom;
            ZoomTo(ImageViewerManager.StepZoom(_zoom / actual, direction) * actual, anchor);
        }

        private void ZoomTo(double zoom, Point? anchor)
        {
            if (_bitmap == null) return;
            _fit = false;
            SetZoom(zoom, anchor ?? new Point(ImageScrollViewer.ViewportWidth / 2, ImageScrollViewer.ViewportHeight / 2));
        }

        private void SetZoom(double zoom, Point? anchor)
        {
            double old = _zoom;
            double h = ImageScrollViewer.HorizontalOffset, v = ImageScrollViewer.VerticalOffset;
            _zoom = ImageViewerManager.ClampZoom(zoom);
            ImageScale.ScaleX = _zoom;
            ImageScale.ScaleY = _zoom;
            TxtZoomLevel.Text = $"{_zoom / ActualSizeZoom * 100:0}%";
            if (anchor is Point p && old > 0)
            {
                ImageScrollViewer.UpdateLayout();
                ImageScrollViewer.ScrollToHorizontalOffset(ImageViewerManager.OffsetForZoomAt(h, p.X, old, _zoom));
                ImageScrollViewer.ScrollToVerticalOffset(ImageViewerManager.OffsetForZoomAt(v, p.Y, old, _zoom));
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePanCursor));
        }

        /// <summary>Fit keeps fitting when the window or the panels change size (the old fit was a one-off).</summary>
        private void ApplyFit()
        {
            if (_bitmap == null) return;
            _fit = true;
            SetZoom(ImageViewerManager.FitZoom(ImageScrollViewer.ActualWidth - 4, ImageScrollViewer.ActualHeight - 4,
                _bitmap.PixelWidth, _bitmap.PixelHeight, ImageRotate.Angle), null);
        }

        private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_fit) ApplyFit();
        }

        /// <summary>Ctrl + wheel zooms around the mouse; the wheel alone scrolls.</summary>
        private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_bitmap == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            ZoomStep(e.Delta > 0 ? +1 : -1, e.GetPosition(ImageScrollViewer));
            e.Handled = true;
        }

        private void BtnRotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);

        private void BtnRotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

        private void Rotate(double delta)
        {
            if (_bitmap == null) return;
            ImageRotate.Angle = ImageViewerManager.NormalizeAngle(ImageRotate.Angle, delta);
            if (_fit) ApplyFit();
            else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePanCursor));
            UpdateInfo();
        }

        private bool CanPan => ImageScrollViewer.ScrollableWidth > 0 || ImageScrollViewer.ScrollableHeight > 0;

        private void UpdatePanCursor() => MainImage.Cursor = CanPan ? Cursors.Hand : null;

        private void MainImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            if (e.ClickCount == 2)
            {
                // Double-click: fit ↔ actual size, around the mouse.
                if (_fit) ZoomTo(ActualSizeZoom, e.GetPosition(ImageScrollViewer));
                else ApplyFit();
                e.Handled = true;
                return;
            }
            if (!CanPan) return;   // nothing to drag when the whole image fits
            _isPanning = true;
            _panStart = e.GetPosition(ImageScrollViewer);
            _panStartH = ImageScrollViewer.HorizontalOffset;
            _panStartV = ImageScrollViewer.VerticalOffset;
            MainImage.Cursor = Cursors.SizeAll;
            MainImage.CaptureMouse();
            e.Handled = true;
        }

        private void MainImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var p = e.GetPosition(ImageScrollViewer);
            ImageScrollViewer.ScrollToHorizontalOffset(_panStartH - (p.X - _panStart.X));
            ImageScrollViewer.ScrollToVerticalOffset(_panStartV - (p.Y - _panStart.Y));
        }

        private void MainImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            _isPanning = false;
            MainImage.ReleaseMouseCapture();
            UpdatePanCursor();
        }

        // =================================================================
        //  Save
        // =================================================================

        private async void BtnSaveImageAs_Click(object sender, RoutedEventArgs e)
        {
            if (_current is not { } item || _bitmap == null) return;
            double angle = ImageRotate.Angle;
            string ext = Path.GetExtension(item.FullPath);
            var dialog = new SaveFileDialog
            {
                Title = angle == 0 ? "Save a copy" : $"Save a copy - turned {angle:0}°",
                FileName = Path.GetFileNameWithoutExtension(item.FullPath) + (angle == 0 ? " copy" : " turned") + ext,
                Filter = Loc.F("Str_ImageViewer_MsgSaveFilter", "Same type (*{0})|*{0}|JPEG (*.jpg)|*.jpg|PNG (*.png)|*.png|All files (*.*)|*.*", ext),
                InitialDirectory = item.Folder,
            };
            if (dialog.ShowDialog() != true) return;
            string destination = dialog.FileName;

            BtnSaveImageAs.IsEnabled = false;
            try
            {
                await Task.Run(() => ImageViewerManager.SaveCopy(item.FullPath, destination, angle));
                UiMotion.Pop(BtnSaveImageAs, 0.9);
                TxtImageInfo.Text = Loc.F("Str_ImageViewer_MsgSaved", "Saved {0}", Path.GetFileName(destination)) + (angle == 0 ? "" : " " + Loc.F("Str_ImageViewer_MsgTurned", "(turned {0:0}°)", angle));
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_Err_CouldntSave", "Couldn't save: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSaveImageAs.IsEnabled = _bitmap != null;
            }
        }

        // =================================================================
        //  Keyboard
        // =================================================================

        /// <summary>←/→ Home/End, Space, + − 0 1, R (Shift: left), T, Esc. Space is left to buttons and tick boxes that have focus.</summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Left:
                    Step(-1);
                    break;
                case Key.Right:
                    Step(+1);
                    break;
                case Key.Home when _queue.Count > 0:
                    _ = ShowAsync(_queue[0]);
                    break;
                case Key.End when _queue.Count > 0:
                    _ = ShowAsync(_queue[^1]);
                    break;
                case Key.Space when e.OriginalSource is not ButtonBase:
                    ToggleSlideshow();
                    break;
                case Key.Add or Key.OemPlus:
                    ZoomStep(+1, null);
                    break;
                case Key.Subtract or Key.OemMinus:
                    ZoomStep(-1, null);
                    break;
                case Key.D0 or Key.NumPad0:
                    ApplyFit();
                    break;
                case Key.D1 or Key.NumPad1:
                    ZoomTo(ActualSizeZoom, null);
                    break;
                case Key.R:
                    Rotate(shift ? -90 : 90);
                    break;
                case Key.T:
                    ToggleTheater();
                    break;
                case Key.Escape when _slideshowPlaying || _theater:
                    if (_slideshowPlaying) StopSlideshow();
                    else ToggleTheater();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }
    }
}
