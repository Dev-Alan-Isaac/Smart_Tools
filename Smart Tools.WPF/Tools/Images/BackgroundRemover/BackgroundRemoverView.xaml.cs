using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using DragEventArgs = System.Windows.DragEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using Path = System.IO.Path;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Background Remover UI. Reads the controls into
    /// <see cref="BackgroundRemoverOptions"/>, hands work to
    /// <see cref="BackgroundRemoverManager"/>, and renders progress and the
    /// live before/after preview. No image processing happens here.
    /// </summary>
    public partial class BackgroundRemoverView : UserControl
    {
        private const double QueueExpandedWidth = 450;
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int PreviewMaxSize = 700;

        private readonly BackgroundRemoverManager _manager = new();
        private readonly BulkObservableCollection<BackgroundRemoverItem> _files = new();
        private readonly ObservableCollection<BackgroundRemoverGroup> _groups = new();
        private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly bool _viewConstructed;

        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _previewCts;
        private BackgroundRemoverItem? _previewItem;
        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _queueCollapsed;
        private int _adding;   // folder reads in flight (Files card)

        private string? _replacementImagePath;

        public BackgroundRemoverView()
        {
            InitializeComponent();
            OutputCard.DefaultFolder = BackgroundRemoverManager.DefaultFolder;
            _viewConstructed = true;

            DataGridQueue.ItemsSource = _files;
            _files.CollectionChanged += (_, _) => OnQueueChanged();

            // The Files card: one row per added folder (or set of loose files).
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = BackgroundRemoverManager.ImageFileFilter;
            _groups.CollectionChanged += (_, _) =>
                InputCard.SetEmpty(_groups.Count == 0);

            _previewDebounce.Tick += (_, _) =>
            {
                _previewDebounce.Stop();
                _ = RefreshPreviewAsync();
            };

            UpdateNamingExample();
            UpdateFormatHint();
        }

        /// <summary>Called from MainWindow.Window_Closing so a running batch stops cleanly.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _previewCts?.Cancel();
            _thumbCts.Cancel();
        }

        // ================================================================
        //  Thumbnails (only rows scrolled into view ask for one, a few at a time)
        // ================================================================

        private const int ThumbnailSide = 64;
        private readonly CancellationTokenSource _thumbCts = new();
        private readonly SemaphoreSlim _thumbGate = new(3);

        private void DataGridQueue_LoadingRow(object? sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is BackgroundRemoverItem { Thumbnail: null, ThumbnailRequested: false } item)
            {
                item.ThumbnailRequested = true;
                _ = LoadThumbnailAsync(item, _thumbCts.Token);
            }
        }

        private async Task LoadThumbnailAsync(BackgroundRemoverItem item, CancellationToken token)
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

        private void Root_Loaded(object sender, RoutedEventArgs e) => OnQueueChanged();

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) { }

        // ================================================================
        //  Drag and drop
        // ================================================================

        /// <summary>Page-wide overlay for drops outside the Files card (the card lights its own border).</summary>
        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
            e.Effects = ok ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            if (ok && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>Only hide once the pointer has really left (DragLeave also fires between child elements).</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                UiMotion.FadeVisibility(DropOverlay, false);
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(DropOverlay, false);
            if (!_isRunning && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] dropped)
                _ = AddPathsAsync(dropped);
            e.Handled = true;
        }

        // ---- The Files card (same as Validate Data / Convert Files / Images to PDF) ----

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isRunning) _ = AddPathsAsync(e.Paths);
        }

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning && sender is Button { Tag: BackgroundRemoverGroup group })
                BackgroundRemoverManager.RemoveFromList(_files, _groups, group.Items.ToList());
        }

        /// <summary>The Clear button on the Files card - same as Clear under the queue.</summary>
        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearList();

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearList();

        private void ClearList()
        {
            if (_isRunning) return;
            _files.Clear();
            _groups.Clear();
            ResetProgressUi();
        }

        /// <summary>
        /// Changing "Include subfolders" also updates the folders already
        /// added (it used to do nothing until the next folder was added).
        /// Images already queued keep their place, tick and status.
        /// </summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (_viewConstructed) _ = RescanFoldersAsync(InputCard.IncludeSubfolders);
        }

        private async Task RescanFoldersAsync(bool includeSubfolders)
        {
            var folders = _groups.Where(g => g.IsFolder && g.FolderPath != null && g.IncludeSubfolders != includeSubfolders).ToList();
            if (_isRunning || folders.Count == 0) return;
            _adding++;
            UpdateFilesButtons();
            try
            {
                int added = 0, removed = 0;
                foreach (var group in folders)
                {
                    if (!Directory.Exists(group.FolderPath)) continue;   // folder gone - leave its images alone
                    var found = await _manager.FindImagesAsync(group.FolderPath!, includeSubfolders);
                    var queued = new HashSet<string>(_files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
                    var newItems = await _manager.CreateItemsAsync(found.Where(p => !queued.Contains(p)));
                    if (_isRunning) return;
                    var (a, r) = BackgroundRemoverManager.ApplyRescan(_files, _groups, group, found, newItems, includeSubfolders);
                    added += a.Count;
                    removed += r.Count;
                }
                if (added + removed > 0) InputCard.PopDropArea();
                TxtProgress.Text = includeSubfolders
                    ? (added > 0 ? $"Added {added:N0} {Plural(added, "image", "images")} from subfolders." : "No images in the subfolders.")
                    : (removed > 0 ? $"Took {removed:N0} subfolder {Plural(removed, "image", "images")} off the queue." : "No subfolder images were in the queue.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtProgress.Text = "Couldn't read the folders again: " + ex.Message;
            }
            finally
            {
                _adding--;
                UpdateFilesButtons();
            }
        }

        private void UpdateFilesButtons()
        {
            bool idle = !_isRunning;
            InputCard.IsLocked = !idle;
            BtnClearAll.IsEnabled = idle && _files.Count > 0;
            int ticked = _files.Count(f => f.IsSelected);
            BtnRemoveFiles.IsEnabled = idle && _files.Count > 0 && ticked < _files.Count;   // "select all"
            BtnDeselectAll.IsEnabled = idle && ticked > 0;
            BtnRemoveSelected.IsEnabled = idle && ticked > 0;
            BtnMoveUp.IsEnabled = BtnMoveDown.IsEnabled = idle && _files.Count > 1 && HighlightedItem != null;
            BtnSortByName.IsEnabled = BtnSortByDate.IsEnabled = idle && _files.Count > 1;
        }

        private static string Plural(int n, string one, string many) => n == 1 ? one : many;

        // ================================================================
        //  Options sidebar
        // ================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);

            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;

            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;

            if (BtnCollapseOptions.Content is StackPanel headerStack)
                headerStack.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        private void BtnToggleQueue_Click(object sender, RoutedEventArgs e) => SetQueueCollapsed(!_queueCollapsed);

        private void SetQueueCollapsed(bool collapsed)
        {
            _queueCollapsed = collapsed;
            QueueColumn.Width = new GridLength(collapsed ? 0 : QueueExpandedWidth);
            QueuePanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            IconTogglePlaylist.Kind = collapsed ? PackIconKind.ChevronLeft : PackIconKind.ChevronRight;
        }

        // ---- background mode / output format (icon pills) ----

        private BackgroundRemoverMode SelectedMode =>
            RadioBgSolid.IsChecked == true ? BackgroundRemoverMode.SolidColor
            : RadioBgImage.IsChecked == true ? BackgroundRemoverMode.ImageReplacement
            : BackgroundRemoverMode.Transparent;

        private BackgroundRemoverOutputFormat SelectedFormat =>
            RadioFormatJpg.IsChecked == true ? BackgroundRemoverOutputFormat.Jpg
            : RadioFormatWebp.IsChecked == true ? BackgroundRemoverOutputFormat.WebP
            : BackgroundRemoverOutputFormat.Png;

        private void RadioBackgroundMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            var mode = SelectedMode;
            UiMotion.FadeVisibility(PanelSolidColorOptions, mode == BackgroundRemoverMode.SolidColor);
            UiMotion.FadeVisibility(PanelImageReplaceOptions, mode == BackgroundRemoverMode.ImageReplacement);
            UpdateFormatHint();
            SchedulePreview();
        }

        private void RadioOutputFormat_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            UiMotion.FadeVisibility(PanelQuality, SelectedFormat != BackgroundRemoverOutputFormat.Png);
            UpdateFormatHint();
            UpdateNamingExample();
            SchedulePreview(); // JPG preview shows the white flattening
        }

        private void UpdateFormatHint()
        {
            bool transparent = SelectedMode == BackgroundRemoverMode.Transparent;
            TxtFormatHint.Text = SelectedFormat switch
            {
                BackgroundRemoverOutputFormat.Jpg when transparent =>
                    "JPG can't store transparency - removed areas become white.",
                BackgroundRemoverOutputFormat.Jpg => "Smallest files for photos; no transparency.",
                BackgroundRemoverOutputFormat.WebP => "Small files and keeps transparency.",
                _ => "PNG keeps transparency - best for cut-outs.",
            };
        }

        private void SldTolerance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtToleranceValue.Text = $"{(int)e.NewValue}%";
            SchedulePreview();
        }

        private void SldEdgeSoftness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtEdgeSoftnessValue.Text = $"{(int)e.NewValue}%";
            SchedulePreview();
        }

        private void SldEdgeChoke_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtEdgeChokeValue.Text = $"{e.NewValue:0.#} px";
            SchedulePreview();
        }

        private void ChkCleanEdges_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            EdgeOptionsPanel.IsEnabled = ChkCleanEdges.IsChecked == true;
            SchedulePreview();
        }

        private void SldQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtQualityValue.Text = ((int)e.NewValue).ToString();
        }

        private void PreviewOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) SchedulePreview();
        }

        // ---- colours ----

        private void TxtTargetColor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool valid = UpdateSwatch(SwatchTargetColor, TxtTargetColor.Text);
            SetHexFieldValidity(TargetColorBar, TxtTargetColorError, valid);
            if (valid) SchedulePreview();
        }

        private void TxtReplacementColor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool valid = UpdateSwatch(SwatchReplacementColor, TxtReplacementColor.Text);
            SetHexFieldValidity(ReplacementColorBar, TxtReplacementColorError, valid);
            if (valid) SchedulePreview();
        }

        // ---- eyedropper: click the Before preview to take the colour to remove ----

        private bool _pickingColor;

        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) { SetPickingColor(false); return; }

            if (ImgBefore.Source is not BitmapSource)
            {
                ShowMessage((string)FindResource("Str_BgRemove_PickNoImage"), "Background Remover", MessageBoxImage.Information);
                return;
            }
            SetPickingColor(true);
        }

        private void SetPickingColor(bool on)
        {
            _pickingColor = on;
            ImgBefore.Cursor = on ? System.Windows.Input.Cursors.Cross : null;
            IconPickColor.SetResourceReference(PackIcon.ForegroundProperty, on ? "AccentColor" : "TextSecondary");
            UiMotion.FadeVisibility(TxtPickHint, on);
        }

        private void Root_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_pickingColor && e.Key == System.Windows.Input.Key.Escape)
            {
                SetPickingColor(false);
                e.Handled = true;
            }
        }

        private void ImgBefore_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_pickingColor || ImgBefore.Source is not BitmapSource source) return;

            // The image is letter-boxed (Stretch=Uniform): find where it really sits inside the control.
            double boxW = ImgBefore.ActualWidth, boxH = ImgBefore.ActualHeight;
            double scale = Math.Min(boxW / source.Width, boxH / source.Height);
            double drawnW = source.Width * scale, drawnH = source.Height * scale;
            var p = e.GetPosition(ImgBefore);
            double fx = (p.X - (boxW - drawnW) / 2) / drawnW, fy = (p.Y - (boxH - drawnH) / 2) / drawnH;
            if (fx is < 0 or >= 1 || fy is < 0 or >= 1) return;   // clicked the empty margin

            int px = Math.Min(source.PixelWidth - 1, (int)(fx * source.PixelWidth));
            int py = Math.Min(source.PixelHeight - 1, (int)(fy * source.PixelHeight));

            var pixel = new byte[4];
            new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
            if (pixel[3] == 0) return;   // a see-through pixel has no colour to pick

            TxtTargetColor.Text = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";
            UiMotion.Pop(SwatchTargetColor, fromScale: 0.6);
            SetPickingColor(false);
            e.Handled = true;
        }

        /// <summary>Preset swatches. Tag = "target|#RRGGBB" or "replacement|#RRGGBB".</summary>
        private void BtnColorPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;
            var parts = tag.Split('|');
            if (parts.Length != 2) return;

            var box = parts[0] == "replacement" ? TxtReplacementColor : TxtTargetColor;
            box.Text = parts[1];
            UiMotion.Pop(parts[0] == "replacement" ? SwatchReplacementColor : SwatchTargetColor, fromScale: 0.6);
        }

        /// <summary>Red border on the rounded colour field + inline message (no dialog).</summary>
        private void SetHexFieldValidity(Border bar, TextBlock errorMessage, bool isValid)
        {
            if (isValid)
                bar.ClearValue(Border.BorderBrushProperty);
            else
                bar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");

            UiMotion.FadeVisibility(errorMessage, !isValid);
        }

        private static bool UpdateSwatch(Border swatch, string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                swatch.Background = new SolidColorBrush(color);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentNullException)
            {
                return false; // incomplete while typing - keep the last valid colour
            }
        }

        private static string NormalizeHex(string input, string fallback)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(input);
                return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentNullException)
            {
                return fallback;
            }
        }

        // ---- replacement image / output folder / naming ----

        private void BtnBrowseReplacementImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose a replacement background image",
                Filter = BackgroundRemoverManager.ImageFileFilter,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            _replacementImagePath = dlg.FileName;
            TxtReplacementImagePath.Text = Path.GetFileName(dlg.FileName);
            TxtReplacementImagePath.ToolTip = dlg.FileName;
            UiMotion.FadeIn(TxtReplacementImagePath, risePx: 3);
            SchedulePreview();
        }

        private void OutputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (_viewConstructed) UpdateNamingExample();
        }

        private void TxtNamingSuffix_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) UpdateNamingExample();
        }

        /// <summary>Live "photo_nobg.png" example using the first queued file (or a sample name).</summary>
        private void UpdateNamingExample()
        {
            string sample = _files.Count > 0 ? _files[0].FullPath : "photo.jpg";
            TxtNamingExample.Text = BackgroundRemoverManager.ExampleOutputName(sample, BuildOptions());
        }

        // ================================================================
        //  Queue
        // ================================================================

        /// <summary>
        /// Adds picked or dropped files and folders. Each folder becomes one
        /// Files card row (subfolders follow the checkbox); loose files share
        /// one. Reports what happened instead of failing silently.
        /// </summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isRunning || paths.Count == 0) return;
            bool subfolders = InputCard.IncludeSubfolders;
            _adding++;
            UpdateFilesButtons();
            try
            {
                int added = 0, unreadable = 0;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    try
                    {
                        var found = await _manager.FindImagesAsync(folder, subfolders);
                        var items = await _manager.CreateItemsAsync(found);
                        if (_isRunning) return;
                        added += BackgroundRemoverManager.AddToList(_files, _groups, items, folder, isFolder: true, subfolders).Count;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        unreadable++;
                    }
                }

                var files = paths.Where(p => !Directory.Exists(p)).ToList();
                var supported = BackgroundRemoverManager.SupportedFiles(files);
                if (supported.Count > 0)
                {
                    var items = await _manager.CreateItemsAsync(supported);
                    if (_isRunning) return;
                    added += BackgroundRemoverManager.AddToList(_files, _groups, items, null, isFolder: false, includeSubfolders: false).Count;
                }

                if (added > 0) InputCard.PopDropArea();
                var parts = new List<string>
                {
                    added == 0 ? "No new images found - they may already be queued." : $"Added {added:N0} {Plural(added, "image", "images")}.",
                };
                int skipped = files.Count - supported.Count;
                if (skipped > 0) parts.Add($"{skipped:N0} {Plural(skipped, "file wasn't a supported image", "files weren't supported images")}.");
                if (unreadable > 0) parts.Add($"{unreadable:N0} {Plural(unreadable, "folder", "folders")} couldn't be read.");
                TxtProgress.Text = string.Join(" ", parts);

                // First files in: preview the first one straight away.
                if (_previewItem == null && _files.Count > 0)
                    SelectForPreview(_files[0]);
            }
            finally
            {
                _adding--;
                UpdateFilesButtons();
            }
        }

        private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning && sender is FrameworkElement { DataContext: BackgroundRemoverItem item })
                BackgroundRemoverManager.RemoveFromList(_files, _groups, new[] { item });
        }

        private void BtnRemoveFiles_Click(object sender, RoutedEventArgs e)
        {
            int selected = _files.Count(f => f.IsSelected);
            if (selected == 0) return;

            // Every row starts checked (checked = "include in the run"), so on a
            // fresh queue Remove would silently wipe everything - confirm that case.
            if (selected == _files.Count &&
                MessageBox.Show(Window.GetWindow(this)!,
                    $"All {selected:N0} files are checked. Remove them all from the queue?",
                    "Remove all files", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            BackgroundRemoverManager.RemoveFromList(_files, _groups, _files.Where(f => f.IsSelected).ToList());
        }

        // ---- Ticks ("include in the run") ----

        private void ItemCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateFilesButtons();

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _files) item.IsSelected = true;
            UpdateFilesButtons();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _files) item.IsSelected = false;
            UpdateFilesButtons();
        }

        // ---- Order: move up/down, sort by name or date ----

        /// <summary>The highlighted row in the list, else the image being previewed.</summary>
        private BackgroundRemoverItem? HighlightedItem => DataGridQueue.SelectedItem as BackgroundRemoverItem ?? _previewItem;

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            if (_isRunning || HighlightedItem is not { } item) return;
            if (!BackgroundRemoverManager.Move(_files, new[] { item }, direction)) return;
            AfterReorder(item);
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_isRunning || _files.Count < 2) return;
            var keep = HighlightedItem;
            var sorted = BackgroundRemoverManager.Sorted(_files, byDate);
            _reordering = true;   // Clear + re-add: don't flash the empty state or drop the preview
            try
            {
                _files.Clear();
                _files.AddRange(sorted);
            }
            finally
            {
                _reordering = false;
            }
            AfterReorder(keep);
        }

        /// <summary>Re-highlights the item (a reorder drops the highlight) in both lists and scrolls it into view.</summary>
        private void AfterReorder(BackgroundRemoverItem? item)
        {
            if (item != null)
            {
                DataGridQueue.SelectedItem = item;
                DataGridQueue.ScrollIntoView(item);
            }
            OnQueueChanged();
        }

        // ---- Context menu ----

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (HighlightedItem is not { } item) return;
            if (ShellExplorer.Show(item.FullPath) == ShellExplorer.ShowResult.NotFound)
                MessageBox.Show($"\"{item.DisplayName}\" is no longer there.", "Background Remover", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void MenuDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || HighlightedItem is not { } item) return;
            bool bin = ImageViewerManager.HasRecycleBin(item.FullPath);
            string text = bin
                ? $"Move \"{item.DisplayName}\" to the Recycle Bin?\n\nThis deletes the file from your computer, not just from the queue."
                : $"Delete \"{item.DisplayName}\" PERMANENTLY?\n\nIt's on a drive without a Recycle Bin, so it can't be undone.";
            if (MessageBox.Show(text, "Background Remover", MessageBoxButton.YesNo, bin ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                BackgroundRemoverManager.DeleteImage(item.FullPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't delete \"{item.DisplayName}\": {ex.Message}", "Background Remover", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            BackgroundRemoverManager.RemoveFromList(_files, _groups, new[] { item });
        }

        private bool _reordering;

        private void OnQueueChanged()
        {
            if (_reordering) return;
            int count = _files.Count;
            bool empty = count == 0;

            string summary = count == 1 ? "1 file queued" : $"{count:N0} files queued";
            bool changed = TxtSummaryCount.Text != summary;
            TxtSummaryCount.Text = summary;
            SummaryPill.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (changed && !empty) UiMotion.Pop(SummaryPill);

            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            UiMotion.FadeVisibility(QueueEmptyStatePanel, empty);
            UiMotion.FadeVisibility(DataGridQueue, !empty);

            if (_previewItem != null && !_files.Contains(_previewItem))
            {
                _previewItem = null;
                if (_files.Count > 0) SelectForPreview(_files[0]);
                else ClearPreview();
            }

            UpdateNamingExample();
            UpdateFilesButtons();
        }

        // ================================================================
        //  Live preview
        // ================================================================

        private void DataGridQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataGridQueue.SelectedItem is BackgroundRemoverItem item) SelectForPreview(item);
            UpdateFilesButtons();   // move buttons follow the highlight
        }

        private void SelectForPreview(BackgroundRemoverItem item)
        {
            if (ReferenceEquals(_previewItem, item)) return;
            _previewItem = item;
            _ = RefreshPreviewAsync();
        }

        /// <summary>Settings changed - re-render after a short pause (sliders fire many events).</summary>
        private void SchedulePreview()
        {
            if (_previewItem == null) return;
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Count == 0)
            {
                ShowMessage("Add at least one image first.", "Nothing to preview", MessageBoxImage.Information);
                return;
            }
            _previewItem ??= _files[0];
            _ = RefreshPreviewAsync();
        }

        private async Task RefreshPreviewAsync()
        {
            var item = _previewItem;
            if (item == null) return;

            _previewCts?.Cancel();
            var cts = new CancellationTokenSource();
            _previewCts = cts;

            PreviewBusy.Visibility = Visibility.Visible;
            PreviewBusy.IsIndeterminate = true;
            PreviewErrorPanel.Visibility = Visibility.Collapsed;
            TxtPreviewInfo.Text = $"{item.DisplayName}  ·  rendering…";

            try
            {
                var preview = await _manager.RenderPreviewAsync(item.FullPath, BuildOptions(), PreviewMaxSize, cts.Token);
                if (cts.IsCancellationRequested) return;

                bool firstImage = ImgAfter.Source == null;
                ImgBefore.Source = ToBitmap(preview.OriginalPng);
                ImgAfter.Source = ToBitmap(preview.ResultPng);
                if (firstImage) { UiMotion.FadeIn(ImgBefore); UiMotion.FadeIn(ImgAfter); }

                TxtPreviewInfo.Text = $"{item.DisplayName}  ·  {preview.SourceWidth:N0} × {preview.SourceHeight:N0}  ·  {item.SizeDisplay}";
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer preview request
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                ImgAfter.Source = null;
                TxtPreviewError.Text = ex.Message;
                UiMotion.FadeVisibility(PreviewErrorPanel, true);
                TxtPreviewInfo.Text = item.DisplayName;
            }
            finally
            {
                if (ReferenceEquals(_previewCts, cts))
                {
                    PreviewBusy.IsIndeterminate = false;
                    PreviewBusy.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ClearPreview()
        {
            _previewCts?.Cancel();
            ImgBefore.Source = null;
            ImgAfter.Source = null;
            PreviewErrorPanel.Visibility = Visibility.Collapsed;
            TxtPreviewInfo.Text = "Click a file to preview it";
        }

        private static BitmapImage ToBitmap(byte[] png)
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(png);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // decode now so the stream can close
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        // ================================================================
        //  Run
        // ================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Cancelling - finishing the images already in progress…";
                return;
            }

            if (_files.Count == 0)
            {
                ShowMessage("Add at least one image first.", "Nothing to process", MessageBoxImage.Information);
                return;
            }

            var toProcess = _files.Where(f => f.IsSelected).ToList();
            if (toProcess.Count == 0)
            {
                ShowMessage("Nothing is checked in the queue - check at least one file to process it.", "Nothing to process", MessageBoxImage.Information);
                return;
            }

            var options = BuildOptions();
            if (options.Mode == BackgroundRemoverMode.ImageReplacement && string.IsNullOrWhiteSpace(options.ReplacementImagePath))
            {
                ShowMessage("Choose a replacement image first.", "Missing replacement image", MessageBoxImage.Warning);
                return;
            }

            if (!OutputCard.IsSameAsSource)
            {
                if (OutputCard.Status.IsError)
                {
                    ShowMessage("Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Save to.", "Can't save", MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    OutputCard.EnsureFolder();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ShowMessage("The folder couldn't be created: " + ex.Message, "Can't save", MessageBoxImage.Warning);
                    return;
                }
            }

            foreach (var item in toProcess) item.ResetStatus();

            _runCts = new CancellationTokenSource();
            _isRunning = true;
            SetRunningUiState(true);

            ProgressRun.Value = 0;
            TxtProgressPercent.Text = "0%";
            SetStats(0, 0, 0);
            TxtEta.Text = "estimating…";
            UiMotion.FadeVisibility(StatsPanel, true);

            var clock = Stopwatch.StartNew();
            var progress = new Progress<BackgroundRemoverProgress>(p =>
            {
                p.Item.SetStatus(p.Status, p.StatusText, p.OutputPath);
                if (p.Status == BackgroundRemoverStatus.Processing) return;

                ProgressRun.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                TxtProgress.Text = $"{p.Processed:N0} of {p.Total:N0} images processed";
                SetStats(p.Succeeded, p.Skipped, p.Failed);
                TxtEta.Text = EstimateRemaining(clock.Elapsed, p.Processed, p.Total);
            });

            try
            {
                var result = await _manager.RunAsync(toProcess, options, progress, _runCts.Token);
                SetStats(result.SucceededCount, result.SkippedCount, result.FailedCount);
                TxtEta.Text = $"took {FormatDuration(result.Elapsed)}";
                TxtProgress.Text = $"Done - {result.SucceededCount:N0} removed, {result.SkippedCount:N0} skipped, {result.FailedCount:N0} failed.";
                ProgressRun.Value = 100;
                TxtProgressPercent.Text = "100%";
                UiMotion.Pop(TxtProgressPercent);
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Cancelled - files not started yet were left as Pending.";
                TxtEta.Text = $"stopped after {FormatDuration(clock.Elapsed)}";
                foreach (var item in toProcess.Where(i => i.Status == BackgroundRemoverStatus.Processing))
                    item.ResetStatus();
            }
            catch (Exception ex)
            {
                ShowMessage($"Batch failed: {ex.Message}", "Error", MessageBoxImage.Error);
            }
            finally
            {
                _isRunning = false;
                SetRunningUiState(false);
                _runCts?.Dispose();
                _runCts = null;
            }
        }

        private void SetStats(int removed, int skipped, int failed)
        {
            TxtStatRemoved.Text = $"{removed:N0} removed";
            TxtStatSkipped.Text = $"{skipped:N0} skipped";
            TxtStatFailed.Text = $"{failed:N0} failed";
        }

        private static string EstimateRemaining(TimeSpan elapsed, int done, int total)
        {
            if (done == 0) return "estimating…";
            if (done >= total) return "finishing…";
            var remaining = TimeSpan.FromTicks(elapsed.Ticks / done * (total - done));
            return $"~{FormatDuration(remaining)} left";
        }

        private static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
            : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
            : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))}s";

        private void SetRunningUiState(bool running)
        {
            // Swap icon + label instead of replacing Button.Content (which would wipe the icon).
            IconRun.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.AutoFix;
            if (running)
                TxtRunLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else
                TxtRunLabel.SetResourceReference(TextBlock.TextProperty, "Str_BgRemove_RunButton");

            UpdateFilesButtons();
            BtnRemoveFiles.IsEnabled = !running;
            BtnPreview.IsEnabled = !running;
            BtnExportReport.IsEnabled = !running;
            OptionsScrollViewer.IsEnabled = !running;
            OutputCard.IsLocked = running;
            if (!running) OutputCard.Refresh();   // free space may have changed
            DataGridQueue.IsEnabled = !running;
        }

        private void ResetProgressUi()
        {
            ProgressRun.Value = 0;
            TxtProgressPercent.Text = "0%";
            TxtProgress.SetResourceReference(TextBlock.TextProperty, "Str_BgRemove_ProgressInitial");
            UiMotion.FadeVisibility(StatsPanel, false);
        }

        // ================================================================
        //  Export report
        // ================================================================

        private void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string ext }) return;
            bool asJson = ext == "json";
            if (_files.Count == 0)
            {
                ShowMessage("There's nothing to export yet - add and process some images first.", "Nothing to export", MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = asJson ? "JSON file (*.json)|*.json" : "CSV file (*.csv)|*.csv",
                FileName = $"background-removal-report-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}",
                DefaultExt = "." + ext,
                AddExtension = true,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                _manager.ExportReport(_files, dlg.FileName, asJson);
                ShowMessage("Report exported.", "Export complete", MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowMessage($"Export failed: {ex.Message}", "Error", MessageBoxImage.Error);
            }
        }

        // ================================================================
        //  Shared
        // ================================================================

        private BackgroundRemoverOptions BuildOptions() => new()
        {
            Mode = SelectedMode,
            TargetColorHex = NormalizeHex(TxtTargetColor.Text, "#FFFFFF"),
            Tolerance = SldTolerance.Value,
            CleanEdges = ChkCleanEdges.IsChecked == true,
            EdgeSoftness = SldEdgeSoftness.Value,
            EdgeChoke = SldEdgeChoke.Value,
            TrimEdges = ChkTrimEdges.IsChecked == true,
            ReplacementColorHex = NormalizeHex(TxtReplacementColor.Text, "#FFFFFF"),
            ReplacementImagePath = _replacementImagePath,
            OutputFormat = SelectedFormat,
            Quality = (int)SldQuality.Value,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            OverwriteExisting = ChkOverwriteExisting.IsChecked == true,
            NamingSuffix = TxtNamingSuffix.Text ?? "",
            OutputFolder = OutputCard.IsSameAsSource ? "" : OutputCard.EffectiveFolder ?? "",
        };

        private void ShowMessage(string text, string title, MessageBoxImage icon)
        {
            var owner = Window.GetWindow(this);
            if (owner != null) MessageBox.Show(owner, text, title, MessageBoxButton.OK, icon);
            else MessageBox.Show(text, title, MessageBoxButton.OK, icon);
        }
    }
}
