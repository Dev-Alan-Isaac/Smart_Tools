using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
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
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Image Editor view - UI only. Rotate/flip maths, crop mapping, sizes
    /// and names are in <see cref="ImageEditingManager"/>; export in
    /// ImageEditingManager.Run.cs; pixels in ImageEditingManager.Imaging.cs.
    /// Edits are stored on each <see cref="ImageItem"/> and only applied when
    /// saving; originals are never changed.
    /// </summary>
    public partial class ImageEditingView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double QueueExpandedWidth = 400;
        private const int PreviewMaxSide = 2400;
        private const string DialogTitle = "Image Editor";

        private readonly ObservableCollection<ImageItem> _queue = new();
        private readonly ObservableCollection<ImageEditGroup> _groups = new();
        private int _adding;   // folder reads in flight (Files card)
        private readonly ImageEditingManager _manager = new();
        private ImageEditorSettings _settings = new();

        private ImageItem? _current;
        private int _previewVersion;
        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _loadCts;
        private string? _lastOutputFolder;

        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _queueCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        // Crop marquee (display space: fractions of the picture as shown).
        private bool _cropMode;
        private bool _dragging;
        private Point _dragStart;
        private CropRect? _draft;

        public ImageEditingView()
        {
            InitializeComponent();
            OutputCard.DefaultFolder = ImageEditingManager.DefaultOutputFolder();
            DataGridQueue.ItemsSource = _queue;
            _queue.CollectionChanged += (_, _) => UpdateQueueUi();

            // The Files card: one row per added folder (or set of loose files).
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = "Images|" + string.Join(";", ImageEditingManager.ImageExtensions.Select(x => "*" + x)) + "|All files (*.*)|*.*";
            _groups.CollectionChanged += (_, _) =>
                InputCard.SetEmpty(_groups.Count == 0);
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _loadCts?.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(ImageEditingManager.LoadSettings());
                _loadedOnce = true;
            }
            UpdateQueueUi();
            UpdatePreviewUi();
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        // =================================================================
        //  Sidebar and list panel
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void BtnToggleQueue_Click(object sender, RoutedEventArgs e)
        {
            SetQueueCollapsed(!_queueCollapsed);
            SaveSettings();
        }

        private void SetQueueCollapsed(bool collapsed)
        {
            _queueCollapsed = collapsed;
            QueueColumn.Width = new GridLength(collapsed ? 0 : QueueExpandedWidth);
            QueuePanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            IconToggleQueue.Kind = collapsed ? PackIconKind.ChevronLeft : PackIconKind.ChevronRight;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, DrawOverlay);
        }

        // =================================================================
        //  Adding images
        // =================================================================

        /// <summary>Drops anywhere on the page are accepted; the Files card lights up to show where they go.</summary>
        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) InputCard.SetDropHighlight(true);
            e.Handled = true;
        }

        private void Root_DragLeave(object sender, DragEventArgs e) =>
            InputCard.SetDropHighlight(false);

        private void Root_Drop(object sender, DragEventArgs e)
        {
            InputCard.SetDropHighlight(false);
            if (!_isRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        // ---- The Files card (same as Validate Data / Convert Files / Images to PDF) ----

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning && sender is Button { Tag: ImageEditGroup group }) RemoveItems(group.Items.ToList());
        }

        /// <summary>The Clear button on the Files card - same as Clear under the list.</summary>
        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_isRunning) _ = AddPathsAsync(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearList();

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearList();

        /// <summary>
        /// Saved as a preference, and also updates the folders already added
        /// (it used to apply only to the next folder). Images already listed
        /// keep their place, tick and edits.
        /// </summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            _ = RescanFoldersAsync(InputCard.IncludeSubfolders);
        }

        private async Task RescanFoldersAsync(bool includeSubfolders)
        {
            var folders = _groups.Where(g => g.IsFolder && g.FolderPath != null && g.IncludeSubfolders != includeSubfolders).ToList();
            if (_isRunning || folders.Count == 0) return;
            _adding++;
            UpdateQueueUi();
            TxtProgress.Text = includeSubfolders ? "Looking in subfolders..." : "Leaving out subfolders...";
            try
            {
                var added = new List<ImageItem>();
                int removed = 0, inaccessible = 0;
                foreach (var group in folders)
                {
                    if (!Directory.Exists(group.FolderPath)) continue;   // folder gone - leave its images alone
                    var found = await ImageEditingManager.ExpandAsync(new[] { group.FolderPath! }, includeSubfolders);
                    if (_isRunning) return;
                    inaccessible += found.InaccessibleFolders;
                    var gone = group.Items.Where(i => !found.Files.Contains(i.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (_current != null && gone.Contains(_current)) ShowItem(null);
                    var (a, r) = ImageEditingManager.ApplyRescan(_queue, _groups, group, found.Files, includeSubfolders);
                    added.AddRange(a);
                    removed += r.Count;
                }
                if (added.Count + removed > 0) InputCard.PopDropArea();
                string extra = inaccessible > 0 ? $" · {inaccessible:N0} folders couldn't be read" : "";
                TxtProgress.Text = (includeSubfolders
                    ? (added.Count > 0 ? $"Added {added.Count:N0} image{(added.Count == 1 ? "" : "s")} from subfolders, at the end of the list" : "No images in the subfolders")
                    : (removed > 0 ? $"Took {removed:N0} subfolder image{(removed == 1 ? "" : "s")} off the list" : "No subfolder images were in the list")) + extra + ".";
                if (_current == null && _queue.Count > 0) ShowItem(_queue[0]);
                if (added.Count > 0) await LoadThumbnailsAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtProgress.Text = $"Couldn't read the folders again: {ex.Message}";
            }
            finally
            {
                _adding--;
                UpdateQueueUi();
            }
        }

        /// <summary>
        /// Lists folders off the UI thread and adds new images - each folder
        /// as one Files card row, loose files sharing one - then loads
        /// thumbnails a few at a time.
        /// </summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isRunning || paths.Count == 0) return;
            bool subfolders = InputCard.IncludeSubfolders;
            _adding++;
            UpdateQueueUi();
            TxtProgress.Text = "Looking for images...";
            try
            {
                var added = new List<ImageItem>();
                int inaccessible = 0, foundCount = 0;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var found = await ImageEditingManager.ExpandAsync(new[] { folder }, subfolders);
                    if (_isRunning) return;
                    inaccessible += found.InaccessibleFolders;
                    foundCount += found.Files.Count;
                    added.AddRange(ImageEditingManager.AddToList(_queue, _groups, found.Files, folder, isFolder: true, subfolders));
                }

                var files = paths.Where(p => !Directory.Exists(p)).ToList();
                if (files.Count > 0)
                {
                    var found = await ImageEditingManager.ExpandAsync(files, false);
                    if (_isRunning) return;
                    foundCount += found.Files.Count;
                    added.AddRange(ImageEditingManager.AddToList(_queue, _groups, found.Files, null, isFolder: false, includeSubfolders: false));
                }

                string extra = inaccessible > 0 ? $" · {inaccessible:N0} folders couldn't be read" : "";
                TxtProgress.Text = added.Count == 0
                    ? (foundCount == 0 ? "No images found there." : "Those images are already in the list.") + extra
                    : $"Added {added.Count:N0} image{(added.Count == 1 ? "" : "s")}{extra}.";
                if (added.Count == 0) return;

                InputCard.PopDropArea();
                if (_current == null) ShowItem(added[0]);
                await LoadThumbnailsAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtProgress.Text = $"Couldn't read the folder: {ex.Message}";
            }
            finally
            {
                _adding--;
                UpdateQueueUi();
            }
        }

        private async Task LoadThumbnailsAsync()
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            try
            {
                await ImageEditingManager.LoadThumbnailsAsync(_queue.ToList(), _loadCts.Token);
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
            UpdateQueueUi();
            UpdatePreviewUi();
        }

        // =================================================================
        //  List
        // =================================================================

        private bool _reordering;

        private void UpdateQueueUi()
        {
            if (!_viewConstructed || _reordering) return;
            int ticked = _queue.Count(i => i.IsSelected);
            int unreadable = _queue.Count(i => i.Status == ImageItemStatus.Unreadable);
            TxtSummaryCount.Text = $"{_queue.Count:N0} image{(_queue.Count == 1 ? "" : "s")} · {ticked:N0} ticked" + (unreadable > 0 ? $" · {unreadable:N0} unreadable" : "");
            SummaryPill.Visibility = _queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UiMotion.FadeVisibility(EmptyStatePanel, _queue.Count == 0);
            UiMotion.FadeVisibility(DataGridQueue, _queue.Count > 0);

            bool idle = !_isRunning;
            BtnMoveUp.IsEnabled = BtnMoveDown.IsEnabled = idle && _queue.Count > 1 && HighlightedItem != null;
            BtnSortByName.IsEnabled = BtnSortByDate.IsEnabled = idle && _queue.Count > 1;
            BtnClearAll.IsEnabled = idle && _queue.Count > 0;
            BtnSelectAll.IsEnabled = idle && _queue.Count > 0;
            BtnDeselectAll.IsEnabled = idle && ticked > 0;
            BtnRemoveSelected.IsEnabled = idle && ticked > 0;
            BtnRunAction.IsEnabled = _isRunning || ticked > 0;
            BtnPrevious.IsEnabled = BtnNext.IsEnabled = _queue.Count > 1;
            UpdateNameExample();
        }

        private void ItemCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateQueueUi();

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _queue.Where(i => i.Status != ImageItemStatus.Unreadable)) item.IsSelected = true;
            UpdateQueueUi();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _queue) item.IsSelected = false;
            UpdateQueueUi();
        }

        // ---- Order: move up/down, sort by name or date ----

        /// <summary>The highlighted row, else the image on screen.</summary>
        private ImageItem? HighlightedItem => DataGridQueue.SelectedItem as ImageItem ?? _current;

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            if (_isRunning || HighlightedItem is not { } item) return;
            if (!ImageEditingManager.Move(_queue, new[] { item }, direction)) return;
            AfterReorder(item);
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_isRunning || _queue.Count < 2) return;
            var keep = HighlightedItem;
            var sorted = ImageEditingManager.Sorted(_queue, byDate);
            _reordering = true;   // Clear + re-add: don't flash the empty state or refresh the UI per row
            try
            {
                _queue.Clear();
                foreach (var item in sorted) _queue.Add(item);
            }
            finally
            {
                _reordering = false;
            }
            AfterReorder(keep);
        }

        /// <summary>Re-highlights the item (replacing rows drops the highlight) and scrolls it into view.</summary>
        private void AfterReorder(ImageItem? item)
        {
            if (item != null)
            {
                DataGridQueue.SelectedItem = item;
                DataGridQueue.ScrollIntoView(item);
            }
            UpdateQueueUi();
            UpdatePreviewUi();
        }

        // ---- Context menu ----

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (HighlightedItem is not { } item) return;
            if (ShellExplorer.Show(item.FullPath) == ShellExplorer.ShowResult.NotFound)
                MessageBox.Show($"\"{item.Name}\" is no longer there.", "Image Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void MenuDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || HighlightedItem is not { } item) return;
            bool bin = ImageViewerManager.HasRecycleBin(item.FullPath);
            string text = bin
                ? $"Move \"{item.Name}\" to the Recycle Bin?\n\nThis deletes the file from your computer, not just from the list."
                : $"Delete \"{item.Name}\" PERMANENTLY?\n\nIt's on a drive without a Recycle Bin, so it can't be undone.";
            if (MessageBox.Show(text, "Image Editor", MessageBoxButton.YesNo, bin ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                ImageEditingManager.DeleteImage(item.FullPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't delete \"{item.Name}\": {ex.Message}", "Image Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            RemoveItems(new[] { item });
        }

        private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning && sender is FrameworkElement { DataContext: ImageItem item }) RemoveItems(new[] { item });
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveItems(_queue.Where(i => i.IsSelected).ToList());

        private void ClearList()
        {
            if (_isRunning) return;
            _loadCts?.Cancel();
            ShowItem(null);
            _queue.Clear();
            _groups.Clear();
        }

        private void RemoveItems(IReadOnlyCollection<ImageItem> items)
        {
            if (_isRunning || items.Count == 0) return;
            ImageItem? next = null;
            if (_current != null && items.Contains(_current))
            {
                int index = _queue.IndexOf(_current);
                next = _queue.Skip(index + 1).Concat(_queue.Take(index).Reverse()).FirstOrDefault(i => !items.Contains(i));
            }
            ImageEditingManager.RemoveFromList(_queue, _groups, items.ToList());
            if (_current != null && !_queue.Contains(_current)) ShowItem(next);
        }

        // =================================================================
        //  Preview
        // =================================================================

        private void DataGridQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // One click shows the image (the old list needed a double-click).
            if (DataGridQueue.SelectedItem is ImageItem item && !ReferenceEquals(item, _current)) ShowItem(item);
            UpdateQueueUi();   // move buttons follow the highlight
        }

        private void BtnPrevious_Click(object sender, RoutedEventArgs e) => Step(-1);

        private void BtnNext_Click(object sender, RoutedEventArgs e) => Step(+1);

        private void Step(int delta)
        {
            if (_queue.Count == 0) return;
            int index = _current == null ? 0 : (_queue.IndexOf(_current) + delta + _queue.Count) % _queue.Count;
            ShowItem(_queue[index]);
        }

        private async void ShowItem(ImageItem? item)
        {
            ExitCropMode();
            if (_current != null) _current.IsNowShowing = false;
            _current = item;
            int version = ++_previewVersion;

            if (item == null)
            {
                PreviewImage.Source = null;
                UpdatePreviewUi();
                return;
            }

            item.IsNowShowing = true;
            if (!ReferenceEquals(DataGridQueue.SelectedItem, item))
            {
                DataGridQueue.SelectedItem = item;
                DataGridQueue.ScrollIntoView(item);
            }
            UpdatePreviewUi();

            var loaded = await Task.Run(() => ImageEditingManager.LoadOriented(item.FullPath, PreviewMaxSide));
            if (version != _previewVersion) return;   // another image was picked meanwhile

            if (loaded.Image == null)
            {
                PreviewImage.Source = null;
                item.Status = ImageItemStatus.Unreadable;
                item.Message = loaded.Error ?? "This file couldn't be read as an image.";
                item.IsSelected = false;
                UpdateQueueUi();
            }
            else
            {
                PreviewImage.Source = loaded.Image;
                if (item.PixelWidth <= 0)
                {
                    item.PixelWidth = loaded.Width;
                    item.PixelHeight = loaded.Height;
                }
                UiMotion.FadeIn(PreviewImage, 0);
            }
            UpdatePreviewUi();
        }

        private void UpdatePreviewUi()
        {
            if (!_viewConstructed) return;
            var item = _current;
            bool has = item != null && PreviewImage.Source != null;
            NoImagePanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

            TxtNowShowing.Text = item?.Name ?? (TryFindResource("Str_Common_NothingLoaded") as string ?? "Nothing loaded");
            TxtNowShowing.ToolTip = item?.FullPath;
            if (item == null)
            {
                TxtNowShowingMeta.Text = "Add Images on the left, Then start editing.";
            }
            else
            {
                var (w, h) = ImageEditingManager.PlannedSize(item.PixelWidth, item.PixelHeight, item.Orientation, item.Crop, BuildOptions());
                string planned = w > 0 && (w != item.PixelWidth || h != item.PixelHeight || item.Orientation.SwapsSides) ? $"  →  saves as {w} × {h}" : "";
                TxtNowShowingMeta.Text = item.Status == ImageItemStatus.Unreadable
                    ? item.Message
                    : $"{item.Dimensions} · {item.Size}{planned}";
            }

            TxtEditSummary.Text = item?.EditSummary ?? "";
            EditSummaryPill.Visibility = item?.HasEdits == true ? Visibility.Visible : Visibility.Collapsed;
            TxtPosition.Text = item == null ? "" : $"{_queue.IndexOf(item) + 1} / {_queue.Count}";

            // Rotate first, then mirror - the same order the export uses.
            if (item == null || item.Orientation.IsIdentity)
            {
                PreviewImage.LayoutTransform = Transform.Identity;
            }
            else
            {
                var group = new TransformGroup();
                group.Children.Add(new RotateTransform(item.Orientation.Rotation));
                if (item.Orientation.Mirror) group.Children.Add(new ScaleTransform(-1, 1));
                PreviewImage.LayoutTransform = group;
            }

            BtnClearCrop.IsEnabled = !_isRunning && (_cropMode ? _draft != null : item?.Crop != null);
            BtnApplyCrop.IsEnabled = _cropMode && _draft is { IsMeaningful: true };
            BtnToggleCrop.IsEnabled = !_isRunning && has;
            UpdateResizeHint();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, DrawOverlay);
        }

        private void PreviewStage_SizeChanged(object sender, SizeChangedEventArgs e) => DrawOverlay();

        // =================================================================
        //  Rotate / flip
        // =================================================================

        private List<ImageItem> Targets() =>
            ChkApplyToAllChecked.IsChecked == true
                ? _queue.Where(i => i.IsSelected && i.Status != ImageItemStatus.Unreadable).ToList()
                : _current != null ? new List<ImageItem> { _current } : new List<ImageItem>();

        private void ApplyToTargets(Func<ImageOrientation, ImageOrientation> change)
        {
            if (_isRunning) return;
            ExitCropMode();
            var targets = Targets();
            if (targets.Count == 0) return;
            foreach (var item in targets) item.Orientation = change(item.Orientation);
            UpdatePreviewUi();
        }

        private void BtnRotateLeft_Click(object sender, RoutedEventArgs e) => ApplyToTargets(ImageEditingManager.RotateCounterClockwise);

        private void BtnRotateRight_Click(object sender, RoutedEventArgs e) => ApplyToTargets(ImageEditingManager.RotateClockwise);

        private void BtnFlipHorizontal_Click(object sender, RoutedEventArgs e) => ApplyToTargets(ImageEditingManager.FlipHorizontal);

        private void BtnFlipVertical_Click(object sender, RoutedEventArgs e) => ApplyToTargets(ImageEditingManager.FlipVertical);

        /// <summary>Resets rotation, mirroring and crop (the old reset left the crop behind).</summary>
        private void BtnResetTransform_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            ExitCropMode();
            foreach (var item in Targets())
            {
                item.Orientation = ImageOrientation.Identity;
                item.Crop = null;
            }
            UpdatePreviewUi();
        }

        // =================================================================
        //  Crop
        // =================================================================

        private double SelectedAspect =>
            ImageEditingManager.AspectFor(CropAspectChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string);

        private void BtnToggleCrop_Click(object sender, RoutedEventArgs e) => SetCropMode(BtnToggleCrop.IsChecked == true);

        private void SetCropMode(bool on)
        {
            if (on && (_current == null || PreviewImage.Source == null))
            {
                BtnToggleCrop.IsChecked = false;
                return;
            }
            _cropMode = on;
            _dragging = false;
            BtnToggleCrop.IsChecked = on;
            OverlayCanvas.IsHitTestVisible = on;
            OverlayCanvas.Cursor = on ? System.Windows.Input.Cursors.Cross : null;
            // Start from the crop the image already has, as it looks now.
            _draft = on && _current?.Crop is CropRect c ? ImageEditingManager.SourceToDisplayRect(c, _current.Orientation) : null;
            UiMotion.FadeVisibility(CropBanner, on);
            if (on) Focus();
            UpdatePreviewUi();
        }

        private void ExitCropMode()
        {
            if (!_cropMode) return;
            _cropMode = false;
            _dragging = false;
            _draft = null;
            BtnToggleCrop.IsChecked = false;
            OverlayCanvas.IsHitTestVisible = false;
            OverlayCanvas.Cursor = null;
            OverlayCanvas.ReleaseMouseCapture();
            CropBanner.Visibility = Visibility.Collapsed;
        }

        private void CropAspect_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            // Re-shape the current selection to the new ratio, from its top-left corner.
            if (_cropMode && _draft is CropRect d)
            {
                var bounds = ImageBounds();
                if (!bounds.IsEmpty)
                {
                    var m = ImageEditingManager.Marquee(d.Left * bounds.Width, d.Top * bounds.Height, d.Right * bounds.Width, d.Bottom * bounds.Height, bounds.Width, bounds.Height, SelectedAspect);
                    _draft = new CropRect(m.Left / bounds.Width, m.Top / bounds.Height, m.Width / bounds.Width, m.Height / bounds.Height);
                }
                UpdatePreviewUi();
            }
        }

        private Rect ImageBounds()
        {
            if (PreviewImage.Source == null || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0) return Rect.Empty;
            try
            {
                return PreviewImage.TransformToVisual(OverlayCanvas).TransformBounds(new Rect(0, 0, PreviewImage.ActualWidth, PreviewImage.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return Rect.Empty;   // not in the visual tree yet
            }
        }

        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_cropMode) return;
            var bounds = ImageBounds();
            if (bounds.IsEmpty) return;
            var p = e.GetPosition(OverlayCanvas);
            _dragStart = new Point(Math.Clamp(p.X - bounds.X, 0, bounds.Width), Math.Clamp(p.Y - bounds.Y, 0, bounds.Height));
            _dragging = true;
            _draft = null;
            OverlayCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var bounds = ImageBounds();
            if (bounds.IsEmpty) return;
            var p = e.GetPosition(OverlayCanvas);
            var m = ImageEditingManager.Marquee(_dragStart.X, _dragStart.Y, p.X - bounds.X, p.Y - bounds.Y, bounds.Width, bounds.Height, SelectedAspect);
            _draft = new CropRect(m.Left / bounds.Width, m.Top / bounds.Height, m.Width / bounds.Width, m.Height / bounds.Height);
            DrawOverlay();
            BtnApplyCrop.IsEnabled = _draft is { IsMeaningful: true };
        }

        private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            OverlayCanvas.ReleaseMouseCapture();
            UpdatePreviewUi();
        }

        private void BtnApplyCrop_Click(object sender, RoutedEventArgs e) => ApplyCrop();

        /// <summary>Stores the selection per image, translated through that image's own rotation.</summary>
        private void ApplyCrop()
        {
            if (!_cropMode || _draft is not CropRect display || !display.IsMeaningful) return;
            foreach (var item in Targets()) item.Crop = ImageEditingManager.DisplayToSourceRect(display, item.Orientation);
            ExitCropMode();
            UpdatePreviewUi();
        }

        private void BtnClearCrop_Click(object sender, RoutedEventArgs e)
        {
            if (_cropMode)
            {
                _draft = null;
                UpdatePreviewUi();
                return;
            }
            foreach (var item in Targets()) item.Crop = null;
            UpdatePreviewUi();
        }

        /// <summary>
        /// Dims everything outside the crop - the one being drawn, or the
        /// one already applied (the old preview showed no sign of a crop).
        /// Drawn in screen pixels, so the outline stays crisp at any zoom.
        /// </summary>
        private void DrawOverlay()
        {
            var bounds = ImageBounds();
            CropRect? display = _cropMode
                ? _draft
                : _current?.Crop is CropRect c ? ImageEditingManager.SourceToDisplayRect(c, _current.Orientation) : null;

            if (bounds.IsEmpty || display is not CropRect d || _current == null)
            {
                CropMask.Visibility = CropRectVisual.Visibility = CropSizeBadge.Visibility = Visibility.Collapsed;
                if (_cropMode && !bounds.IsEmpty)
                {
                    // Nothing drawn yet: light dim so it's clear the picture is waiting for a drag.
                    CropMask.Data = new RectangleGeometry(bounds);
                    CropMask.Opacity = 0.2;
                    CropMask.Visibility = Visibility.Visible;
                }
                return;
            }

            var r = new Rect(bounds.X + d.Left * bounds.Width, bounds.Y + d.Top * bounds.Height, d.Width * bounds.Width, d.Height * bounds.Height);
            CropMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(r));
            CropMask.Opacity = _cropMode ? 0.55 : 0.4;
            CropMask.Visibility = Visibility.Visible;

            Canvas.SetLeft(CropRectVisual, r.X);
            Canvas.SetTop(CropRectVisual, r.Y);
            CropRectVisual.Width = Math.Max(0, r.Width);
            CropRectVisual.Height = Math.Max(0, r.Height);
            CropRectVisual.StrokeDashArray = _cropMode ? null : new DoubleCollection { 4, 3 };
            CropRectVisual.Visibility = Visibility.Visible;

            var o = _current.Orientation;
            int shownW = o.SwapsSides ? _current.PixelHeight : _current.PixelWidth;
            int shownH = o.SwapsSides ? _current.PixelWidth : _current.PixelHeight;
            if (shownW > 0 && shownH > 0)
            {
                TxtCropSize.Text = $"{Math.Round(d.Width * shownW):0} × {Math.Round(d.Height * shownH):0}";
                Canvas.SetLeft(CropSizeBadge, r.X + 6);
                Canvas.SetTop(CropSizeBadge, r.Y + 6);
                CropSizeBadge.Visibility = r.Width > 70 && r.Height > 26 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // =================================================================
        //  Keyboard
        // =================================================================

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox && e.Key != Key.Escape) return;
            if (_isRunning) return;

            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Escape when _cropMode:
                    ExitCropMode();
                    UpdatePreviewUi();
                    break;
                case Key.Enter when _cropMode:
                    ApplyCrop();
                    break;
                case Key.Left when !(Keyboard.FocusedElement is DataGridCell):
                    Step(-1);
                    break;
                case Key.Right when !(Keyboard.FocusedElement is DataGridCell):
                    Step(+1);
                    break;
                case Key.R:
                    ApplyToTargets(shift ? ImageEditingManager.RotateCounterClockwise : ImageEditingManager.RotateClockwise);
                    break;
                case Key.H:
                    ApplyToTargets(ImageEditingManager.FlipHorizontal);
                    break;
                case Key.V:
                    ApplyToTargets(ImageEditingManager.FlipVertical);
                    break;
                case Key.C:
                    SetCropMode(!_cropMode);
                    break;
                case Key.Delete when _current != null:
                    RemoveItems(new[] { _current });
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        // =================================================================
        //  Resize and output settings
        // =================================================================

        private static int? ParsePositive(string text) => int.TryParse(text.Trim(), out int v) && v > 0 ? v : null;

        private ImageOutputFormat SelectedFormat =>
            FormatChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string tag
            && Enum.TryParse(tag, out ImageOutputFormat f) ? f : ImageOutputFormat.Jpg;

        private string EffectiveOutputFolder =>
            OutputCard.EffectiveFolder ?? ImageEditingManager.DefaultOutputFolder();

        private ImageEditOptions BuildOptions() => new()
        {
            Format = SelectedFormat,
            Quality = (int)Math.Round(SliderQuality.Value),
            ResizeWidth = ParsePositive(TxtResizeWidth.Text),
            ResizeHeight = ParsePositive(TxtResizeHeight.Text),
            KeepAspect = ChkMaintainAspect.IsChecked == true,
            AllowEnlarge = ChkAllowEnlarge.IsChecked == true,
            OutputFolder = EffectiveOutputFolder,
            NameSuffix = ImageEditingManager.CleanSuffix(TxtNameSuffix.Text),
        };

        private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

        private void Resize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            UpdatePreviewUi();
            SaveSettings();
        }

        private void Resize_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdatePreviewUi();
            SaveSettings();
        }

        private void UpdateResizeHint()
        {
            var o = BuildOptions();
            if (o.ResizeWidth == null && o.ResizeHeight == null)
            {
                TxtResizeHint.Text = "Leave empty to keep the size. One side is enough - the other follows.";
                return;
            }
            if (_current is { PixelWidth: > 0 } item)
            {
                var (w, h) = ImageEditingManager.PlannedSize(item.PixelWidth, item.PixelHeight, item.Orientation, item.Crop, o);
                TxtResizeHint.Text = $"This image: {w} × {h}" + (o.KeepAspect ? " (fits inside, keeps its shape)" : " (stretched to fit exactly)");
            }
            else
            {
                TxtResizeHint.Text = o.KeepAspect ? "Each image fits inside this size and keeps its shape." : "Each image is stretched to exactly this size.";
            }
        }

        private void Format_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateFormatUi();
            SaveSettings();
        }

        private void UpdateFormatUi()
        {
            bool quality = ImageEditingManager.UsesQuality(SelectedFormat);
            PanelQuality.IsEnabled = quality;
            PanelQuality.Opacity = quality ? 1 : 0.45;
            PanelQuality.ToolTip = quality ? null : "PNG, BMP, GIF and TIFF are lossless - quality doesn't apply.";
            UpdateNameExample();
        }

        private void SliderQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || TxtQualityValue == null) return;
            TxtQualityValue.Text = $"{Math.Round(e.NewValue):0}";
            if (!_applyingSettings) SaveSettings();
        }

        private void NameSuffix_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            UpdateNameExample();
            SaveSettings();
        }

        private void UpdateNameExample()
        {
            if (!_viewConstructed) return;
            string stem = Path.GetFileNameWithoutExtension(_current?.Name ?? _queue.FirstOrDefault()?.Name ?? "holiday.jpg");
            TxtNameExample.Text = $"e.g. {stem}{ImageEditingManager.CleanSuffix(TxtNameSuffix.Text)}{ImageEditingManager.ExtensionFor(SelectedFormat)}";
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            string folder = _lastOutputFolder ?? EffectiveOutputFolder;
            if (ShellExplorer.Show(folder) != ShellExplorer.ShowResult.Shown)
                MessageBox.Show($"Nothing has been saved to {folder} yet.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------------- Settings ----------------

        private void ApplySettings(ImageEditorSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                foreach (var chip in FormatChips.Children.OfType<RadioButton>()) chip.IsChecked = (string)chip.Tag == s.Format.ToString();
                SliderQuality.Value = Math.Clamp(s.Quality, (int)SliderQuality.Minimum, 100);
                TxtQualityValue.Text = $"{SliderQuality.Value:0}";
                TxtResizeWidth.Text = s.ResizeWidth > 0 ? s.ResizeWidth.ToString() : "";
                TxtResizeHeight.Text = s.ResizeHeight > 0 ? s.ResizeHeight.ToString() : "";
                ChkMaintainAspect.IsChecked = s.KeepAspect;
                ChkAllowEnlarge.IsChecked = s.AllowEnlarge;
                TxtNameSuffix.Text = s.NameSuffix;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                ChkApplyToAllChecked.IsChecked = s.ApplyToAllChecked;
                foreach (var chip in CropAspectChips.Children.OfType<RadioButton>()) chip.IsChecked = (string)chip.Tag == s.CropAspect;
                SetQueueCollapsed(s.QueueCollapsed);
                UpdateFormatUi();
                UpdateNameExample();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            var o = BuildOptions();
            _settings.Format = o.Format;
            _settings.Quality = o.Quality;
            _settings.ResizeWidth = o.ResizeWidth ?? 0;
            _settings.ResizeHeight = o.ResizeHeight ?? 0;
            _settings.KeepAspect = o.KeepAspect;
            _settings.AllowEnlarge = o.AllowEnlarge;
            _settings.NameSuffix = o.NameSuffix;
            _settings.IncludeSubfolders = InputCard.IncludeSubfolders;
            _settings.ApplyToAllChecked = ChkApplyToAllChecked.IsChecked == true;
            _settings.CropAspect = CropAspectChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "Free";
            _settings.QueueCollapsed = _queueCollapsed;
            try
            {
                ImageEditingManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Image editor settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  Export
        // =================================================================

        private async void BtnRunAction_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Stopping after the images in progress...";
                return;
            }

            var targets = _queue.Where(i => i.IsSelected && i.Status != ImageItemStatus.Unreadable).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("Tick at least one image to save.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ExitCropMode();
            var options = BuildOptions();
            if (OutputCard.Status.IsError)
            {
                MessageBox.Show("Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Save to.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"Can't save to {options.OutputFolder}: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveSettings();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetRunning(true);

            try
            {
                var progress = new Progress<ImageEditProgress>(p =>
                {
                    double pct = p.Total == 0 ? 0 : p.Processed * 100.0 / p.Total;
                    ProgressEdit.Value = pct;
                    TxtProgressPercent.Text = $"{pct:0}%";
                    TxtProgress.Text = $"Saved {p.Processed:N0} of {p.Total:N0} · {p.CurrentFile}";
                });
                var result = await _manager.RunAsync(_queue.ToList(), options, progress, _runCts.Token);
                _lastOutputFolder = options.OutputFolder;

                SetRunning(false);
                ProgressEdit.Value = result.Cancelled > 0 ? ProgressEdit.Value : 100;
                TxtProgressPercent.Text = result.Cancelled > 0 ? "" : "100%";
                TxtProgress.Text = ImageEditingManager.DescribeResult(result);

                try
                {
                    ToastNotificationManager.ShowToast("Export Complete",
                        result.Failed > 0
                            ? $"Saved {result.Exported} image(s) - {result.Failed} failed."
                            : $"{result.Exported} image(s) saved to {options.OutputFolder}.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                SetRunning(false);
                TxtProgress.Text = "Export failed.";
                MessageBox.Show($"Export failed: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isRunning) SetRunning(false);
            }
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("image-editor");
            else { _activity?.Dispose(); _activity = null; }
            IconRun.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.ContentSaveAllOutline;
            if (running) TxtRunLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtRunLabel.SetResourceReference(TextBlock.TextProperty, "Str_ImgEdit_ExportButton");
            UiMotion.Pop(BtnRunAction, 0.96);

            OptionsScrollViewer.IsEnabled = !running;
            OutputCard.IsLocked = running;
            if (!running) OutputCard.Refresh();   // free space may have changed
            if (running)
            {
                ProgressEdit.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = "Starting...";
            }
            UpdateQueueUi();
            UpdatePreviewUi();
        }
    }
}
