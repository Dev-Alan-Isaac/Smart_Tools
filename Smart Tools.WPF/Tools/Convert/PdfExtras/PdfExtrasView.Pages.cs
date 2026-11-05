using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The page editor of PDF Extras (the Arrange action): the pages of one PDF as pictures that can be put in a new order,
    /// turned, copied, removed, mixed with pages from other PDFs, and checked for blank ones. The editing is a list of
    /// <see cref="PdfPageTile"/>; only Apply turns it into a page plan for <see cref="PdfExtrasManager"/>.
    /// </summary>
    public partial class PdfExtrasView
    {
        /// <summary>More pages than this are not drawn as pictures (memory); big PDFs use Pages (remove or keep) instead.</summary>
        private const int MaxEditorPages = 300;
        private const int ThumbnailSize = 220;
        private const string DragFormat = "SmartTools.PdfPageTiles";

        private readonly ObservableCollection<PdfPageTile> _tiles = new();
        private readonly List<PdfExtrasManager.PageRenderer> _renderers = new();   // index = source id
        private readonly List<string> _extraSources = new();
        private CancellationTokenSource? _editorCts;
        private PdfToolItem? _editorItem;
        private int _editorOriginalPages;
        private bool _thumbsDone;
        private bool _dragCandidate;
        private Point _dragStart;

        private bool Arranging => _operation == PdfExtraOperation.Arrange;

        // =================================================================
        //  Opening a PDF in the editor
        // =================================================================

        /// <summary>Called when the Arrange action is chosen or left: gives the list and the editor their share of the height.</summary>
        private void ShowArrangeMode(bool arrange)
        {
            ApplyRowHeights();
            if (arrange) _ = LoadEditorForSelectionAsync();
            else ReleaseEditor();
        }

        /// <summary>
        /// Splits the height between the file list and the page editor. With nothing added the list card alone shows its empty state;
        /// once PDFs are added in the Arrange action the list takes just the room its rows need and the editor gets the rest.
        /// </summary>
        private void ApplyRowHeights()
        {
            bool editor = Arranging && _items.Count > 0;
            double listHeight = Math.Clamp(150 + _items.Count * 46, 196, 330);
            RowFiles.Height = editor ? new GridLength(listHeight) : new GridLength(1, GridUnitType.Star);
            RowPages.Height = editor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            CardPages.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        }

        private PdfToolItem? EditorTarget() =>
            DataGridResults.SelectedItems.OfType<PdfToolItem>().FirstOrDefault() ?? (_items.Count == 1 ? _items[0] : null);

        private async Task LoadEditorForSelectionAsync(bool force = false)
        {
            var item = EditorTarget();
            if (!force && item != null && ReferenceEquals(item, _editorItem) && _tiles.Count > 0) return;
            await LoadEditorAsync(item);
        }

        private async Task LoadEditorAsync(PdfToolItem? item)
        {
            ReleaseEditor();
            if (item == null)
            {
                ShowEditorMessage(PackIconKind.ViewGrid, T("Str_PdfX_EditorSelectOne"), T("Str_PdfX_EditorSelectOneHint"));
                UpdateAll();
                return;
            }

            var cts = _editorCts = new CancellationTokenSource();
            var token = cts.Token;
            _editorItem = item;
            ShowEditorMessage(PackIconKind.Cached, T("Str_PdfX_EditorOpening"), item.Name);

            try
            {
                var renderer = await PdfExtrasManager.PageRenderer.OpenAsync(item.FullPath, PwdShared.Password);
                if (token.IsCancellationRequested) { renderer.Dispose(); return; }

                int count = renderer.PageCount;
                if (count > MaxEditorPages)
                {
                    renderer.Dispose();
                    ShowEditorMessage(PackIconKind.FileMultipleOutline, F("Str_PdfX_EditorTooMany", count), F("Str_PdfX_EditorTooManyHint", MaxEditorPages));
                    return;
                }
                if (count == 0)
                {
                    renderer.Dispose();
                    ShowEditorMessage(PackIconKind.FileAlertOutline, T("Str_PdfX_EditorNoPages"), item.Name);
                    return;
                }

                _renderers.Add(renderer);
                _editorOriginalPages = count;
                for (int i = 0; i < count; i++)
                {
                    var (w, h) = renderer.SizeOf(i);
                    _tiles.Add(new PdfPageTile
                    {
                        Source = 0, PageIndex = i, Width = w, Height = h,
                        Caption = F("Str_PdfX_TilePage", i + 1),
                        Tip = F("Str_PdfX_TileTip", i + 1, item.Name),
                    });
                }
                HideEditorMessage();
                UpdateEditorState();
                await RenderThumbsAsync(_tiles.ToList(), token);
            }
            catch (COMException)
            {
                if (!token.IsCancellationRequested)
                    ShowEditorMessage(PackIconKind.LockOutline, T("Str_PdfX_EditorNeedsPassword"), T("Str_PdfX_EditorNeedsPasswordHint"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                if (!token.IsCancellationRequested)
                    ShowEditorMessage(PackIconKind.FileAlertOutline, T("Str_PdfX_EditorCantOpen"), PdfToolkitManager.PlainError(ex));
            }
            finally
            {
                UpdateAll();
            }
        }

        /// <summary>Draws the pictures one after the other, so the tiles fill in from the first page and the window stays free.</summary>
        private async Task RenderThumbsAsync(List<PdfPageTile> tiles, CancellationToken token)
        {
            _thumbsDone = false;
            var cache = new Dictionary<(int, int), BitmapSource>();
            try
            {
                foreach (var tile in tiles)
                {
                    token.ThrowIfCancellationRequested();
                    if (tile.IsBlank || tile.Source >= _renderers.Count) continue;
                    if (!cache.TryGetValue((tile.Source, tile.PageIndex), out var picture))
                        cache[(tile.Source, tile.PageIndex)] = picture = await _renderers[tile.Source].RenderAsync(tile.PageIndex, ThumbnailSize, token);
                    tile.Thumb = picture;
                }
                _thumbsDone = true;
                if (_tiles.Any(t => t.IsMarkedBlank)) MarkBlankPages(select: false);
                UpdateEditorState();
            }
            catch (OperationCanceledException)
            {
                // another PDF was chosen, or the view closed
            }
            catch (COMException)
            {
                // a page Windows could not draw keeps its empty frame
            }
        }

        private void ReleaseEditor()
        {
            _editorCts?.Cancel();
            _editorCts = null;
            foreach (var renderer in _renderers) renderer.Dispose();
            _renderers.Clear();
            _extraSources.Clear();
            _tiles.Clear();
            _editorItem = null;
            _editorOriginalPages = 0;
            _thumbsDone = false;
        }

        private void ShowEditorMessage(PackIconKind icon, string title, string hint)
        {
            EditorMessage.Icon = icon;
            EditorMessage.Title = title;
            EditorMessage.Description = hint;
            EditorMessage.Visibility = Visibility.Visible;
            ListPages.Visibility = Visibility.Collapsed;
            EditorToolbar.IsEnabled = false;
        }

        private void HideEditorMessage()
        {
            EditorMessage.Visibility = Visibility.Collapsed;
            ListPages.Visibility = Visibility.Visible;
            EditorToolbar.IsEnabled = !_running;
        }

        // =================================================================
        //  What is selected, what is on screen
        // =================================================================

        private List<PdfPageTile> SelectedTiles() =>
            ListPages.SelectedItems.OfType<PdfPageTile>().OrderBy(t => _tiles.IndexOf(t)).ToList();

        private void ListPages_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEditorState();

        private void UpdateEditorState()
        {
            if (!_viewConstructed) return;
            for (int i = 0; i < _tiles.Count; i++) _tiles[i].Number = i + 1;

            int selected = ListPages.SelectedItems.Count, blank = _tiles.Count(t => t.IsMarkedBlank);
            TxtEditorCount.Text = Plural(_tiles.Count, "Str_PdfX_OnePage", "Str_PdfX_Pages_N");
            PillEditorSelected.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEditorSelected.Text = F("Str_PdfX_EditorSelected", selected);
            PillEditorBlank.Visibility = blank > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEditorBlank.Text = F("Str_PdfX_EditorBlankCount", blank);

            bool has = _tiles.Count > 0 && !_running;
            BtnEarlier.IsEnabled = BtnLater.IsEnabled = BtnTurnLeft.IsEnabled = BtnTurnRight.IsEnabled = BtnDuplicate.IsEnabled = BtnDeletePages.IsEnabled = has && selected > 0;
            BtnInsertBlank.IsEnabled = BtnInsertPdf.IsEnabled = BtnReset.IsEnabled = has;
            BtnFindBlank.IsEnabled = has && _thumbsDone;
            if (Arranging) UpdateAll();
        }

        /// <summary>Whether the new order is different from the PDF as it is.</summary>
        private bool EditorChanged() =>
            _tiles.Count > 0 && (_extraSources.Count > 0 || !PdfExtrasManager.IsUnchanged(_tiles.Select(t => t.ToPlan()).ToList(), _editorOriginalPages));

        /// <summary>The pages were changed: the last result and the last message (for example about blank pages) no longer describe what is on screen.</summary>
        private void EditorEdited()
        {
            ForgetResult();
            if (!_running) ShowStatus(T("Str_PdfX_StatusReady"), "Neutral", PackIconKind.InformationOutline);
            UpdateEditorState();
        }

        // =================================================================
        //  Editing
        // =================================================================

        private void BtnEarlier_Click(object sender, RoutedEventArgs e) => Move(-1);
        private void BtnLater_Click(object sender, RoutedEventArgs e) => Move(+1);

        /// <summary>Moves the selected pages one place; a block that is already at the edge stays put.</summary>
        private void Move(int direction)
        {
            var selected = SelectedTiles();
            if (selected.Count == 0) return;

            var order = direction < 0 ? selected : Enumerable.Reverse(selected).ToList();
            foreach (var tile in order)
            {
                int i = _tiles.IndexOf(tile), j = i + direction;
                if (j < 0 || j >= _tiles.Count || selected.Contains(_tiles[j])) continue;
                _tiles.Move(i, j);
            }
            ReselectInOrder(selected);
            EditorEdited();
        }

        private void BtnTurnLeft_Click(object sender, RoutedEventArgs e) => Turn(-90);
        private void BtnTurnRight_Click(object sender, RoutedEventArgs e) => Turn(+90);

        private void Turn(int degrees)
        {
            foreach (var tile in SelectedTiles()) tile.ExtraRotation = (((tile.ExtraRotation + degrees) % 360) + 360) % 360;
            EditorEdited();
        }

        private void BtnDuplicate_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedTiles();
            if (selected.Count == 0 || _tiles.Count + selected.Count > MaxEditorPages * 2) return;

            // Each copy goes right after its page.
            var copies = new List<PdfPageTile>();
            foreach (var tile in selected.AsEnumerable().Reverse())
            {
                var copy = tile.Clone();
                _tiles.Insert(_tiles.IndexOf(tile) + 1, copy);
                copies.Add(copy);
            }
            ReselectInOrder(copies);
            EditorEdited();
        }

        private void BtnDeletePages_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        private void DeleteSelected()
        {
            var selected = SelectedTiles();
            if (selected.Count == 0) return;
            int first = _tiles.IndexOf(selected[0]);
            foreach (var tile in selected) _tiles.Remove(tile);
            if (_tiles.Count > 0) ListPages.SelectedItem = _tiles[Math.Clamp(first, 0, _tiles.Count - 1)];
            EditorEdited();
        }

        private void BtnInsertBlank_Click(object sender, RoutedEventArgs e)
        {
            if (_tiles.Count == 0) return;
            var selected = SelectedTiles();
            int at = selected.Count > 0 ? _tiles.IndexOf(selected[^1]) + 1 : _tiles.Count;

            // The new page is as big as the page before it, as it is shown.
            var like = _tiles[Math.Clamp(at - 1, 0, _tiles.Count - 1)];
            var (w, h) = like.ShownSize;
            var blank = new PdfPageTile { Source = -1, PageIndex = 0, Width = w, Height = h, Caption = T("Str_PdfX_TileBlankPage"), Tip = T("Str_PdfX_TileBlankTip") };
            _tiles.Insert(at, blank);
            ReselectInOrder(new List<PdfPageTile> { blank });
            EditorEdited();
        }

        private async void BtnInsertPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_tiles.Count == 0 || _editorCts == null) return;
            var dialog = new OpenFileDialog { Filter = PdfToolkitManager.FileDialogFilter, Title = T("Str_PdfX_EditorInsertTitle") };
            if (dialog.ShowDialog() != true) return;

            var token = _editorCts.Token;
            try
            {
                var renderer = await PdfExtrasManager.PageRenderer.OpenAsync(dialog.FileName, PwdShared.Password);
                if (token.IsCancellationRequested) { renderer.Dispose(); return; }

                int count = renderer.PageCount;
                if (count == 0 || _tiles.Count + count > MaxEditorPages)
                {
                    renderer.Dispose();
                    ShowStatus(count == 0 ? T("Str_PdfX_EditorNoPages") : F("Str_PdfX_EditorTooManyAfter", MaxEditorPages), "Warning", PackIconKind.AlertOutline);
                    return;
                }

                _renderers.Add(renderer);
                _extraSources.Add(dialog.FileName);
                int source = _renderers.Count - 1;
                string name = Path.GetFileName(dialog.FileName);

                var selected = SelectedTiles();
                int at = selected.Count > 0 ? _tiles.IndexOf(selected[^1]) + 1 : _tiles.Count;
                var added = new List<PdfPageTile>();
                for (int i = 0; i < count; i++)
                {
                    var (w, h) = renderer.SizeOf(i);
                    var tile = new PdfPageTile
                    {
                        Source = source, PageIndex = i, Width = w, Height = h,
                        Caption = F("Str_PdfX_TileFrom", name, i + 1),
                        Tip = F("Str_PdfX_TileFromTip", i + 1, dialog.FileName),
                    };
                    _tiles.Insert(at + i, tile);
                    added.Add(tile);
                }
                ReselectInOrder(added);
                EditorEdited();
                ListPages.ScrollIntoView(added[0]);
                await RenderThumbsAsync(added, token);
            }
            catch (COMException)
            {
                ShowStatus(T("Str_PdfX_EditorInsertLocked"), "Warning", PackIconKind.LockOutline);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                ShowStatus(F("Str_PdfX_StatusCantRead", Path.GetFileName(dialog.FileName), PdfToolkitManager.PlainError(ex)), "Error", PackIconKind.AlertCircleOutline);
            }
        }

        private async void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (_editorItem != null) await LoadEditorAsync(_editorItem);
        }

        private void ReselectInOrder(List<PdfPageTile> tiles)
        {
            ListPages.SelectedItems.Clear();
            foreach (var tile in tiles) ListPages.SelectedItems.Add(tile);
        }

        private void ListPages_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !_running) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control) { Move(-1); e.Handled = true; }
            else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control) { Move(+1); e.Handled = true; }
        }

        // =================================================================
        //  Blank pages
        // =================================================================

        private void Blank_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            ReadSettings();
            if (_thumbsDone && _tiles.Any(t => t.IsMarkedBlank)) MarkBlankPages(select: false);
        }

        private void BtnFindBlank_Click(object sender, RoutedEventArgs e) => MarkBlankPages(select: true);

        /// <summary>Marks the pages that look empty (judged on the pictures already drawn) and, when asked, selects them so Delete removes them.</summary>
        private void MarkBlankPages(bool select)
        {
            if (!_thumbsDone) return;
            double threshold = PdfExtrasManager.BlankThreshold(_settings.Blank);
            var found = new List<PdfPageTile>();
            foreach (var tile in _tiles)
            {
                bool blank = !tile.IsBlank && tile.Thumb is BitmapSource picture && PdfExtrasManager.InkShare(picture) <= threshold;
                tile.IsMarkedBlank = blank;
                if (blank) found.Add(tile);
            }

            if (select)
            {
                ReselectInOrder(found);
                if (found.Count > 0) ListPages.ScrollIntoView(found[0]);
                ShowStatus(found.Count == 0 ? T("Str_PdfX_EditorNoBlank") : Plural(found.Count, "Str_PdfX_EditorFoundOne", "Str_PdfX_EditorFoundMany"),
                    found.Count == 0 ? "Neutral" : "Warning", found.Count == 0 ? PackIconKind.InformationOutline : PackIconKind.FileHidden);
            }
            UpdateEditorState();
        }

        // =================================================================
        //  Drag and drop reorder
        // =================================================================

        private void ListPages_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(ListPages);
            _dragCandidate = !_running && ItemsControl.ContainerFromElement(ListPages, e.OriginalSource as DependencyObject) is ListBoxItem;
        }

        private void ListPages_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(ListPages);
            if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _dragCandidate = false;
            var dragged = SelectedTiles();
            if (dragged.Count == 0) return;
            var data = new DataObject(DragFormat, dragged);
            DragDrop.DoDragDrop(ListPages, data, DragDropEffects.Move);
        }

        private void ListPages_DragOver(object sender, DragEventArgs e)
        {
            // Only page tiles are accepted here; files dropped from Explorer go to the list of PDFs.
            if (e.Data.GetDataPresent(DragFormat))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void ListPages_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DragFormat) || e.Data.GetData(DragFormat) is not List<PdfPageTile> dragged || dragged.Count == 0) return;
            e.Handled = true;

            // Where: before or after the tile under the pointer, else at the end.
            int target = _tiles.Count;
            if (ItemsControl.ContainerFromElement(ListPages, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: PdfPageTile over } container)
            {
                target = _tiles.IndexOf(over);
                if (e.GetPosition(container).X > container.ActualWidth / 2) target++;
            }

            int before = dragged.Count(t => _tiles.IndexOf(t) < target);
            foreach (var tile in dragged) _tiles.Remove(tile);
            target = Math.Clamp(target - before, 0, _tiles.Count);
            foreach (var tile in dragged) _tiles.Insert(target++, tile);

            ReselectInOrder(dragged);
            EditorEdited();
        }

        // =================================================================
        //  Saving
        // =================================================================

        /// <summary>The sentence under the list while the page editor is open.</summary>
        private string ArrangePlan()
        {
            if (_editorItem == null || _tiles.Count == 0) return T("Str_PdfX_EditorSelectOne");
            if (!EditorChanged()) return T("Str_PdfX_PlanArrangeNothing");
            string name = PdfExtrasManager.OutputFileName(PdfExtraOperation.Arrange, _settings.PagesMode, _editorItem.FullPath);
            string drops = _infos.TryGetValue(_editorItem.FullPath, out var info) && info.IsEncrypted ? " " + T("Str_PdfX_PlanDropsProtection") : "";
            return F("Str_PdfX_PlanArrange", _tiles.Count) + drops + " " + F("Str_PdfX_PlanSaved", name) + " " + T("Str_PdfX_ArrangeLoses");
        }

        private async Task ApplyArrangeAsync(string? sharedFolder)
        {
            if (_editorItem is not { } item || _tiles.Count == 0 || !EditorChanged() || IsBusy) return;

            var cts = _runCts = new CancellationTokenSource();
            _hasRun = true;
            foreach (var row in _items) { row.Status = PdfItemStatus.Ready; row.Message = ""; row.Result = ""; row.Outputs.Clear(); }
            BtnOpenOutput.Visibility = Visibility.Collapsed;
            SetRunning(true);
            ShowStatus(F("Str_PdfX_StatusWorkingOn", 1, 1), "Info", PackIconKind.Cached);
            item.Status = PdfItemStatus.Working;

            string folder = sharedFolder ?? Path.GetDirectoryName(item.FullPath) ?? PdfExtrasManager.DefaultFolder;
            var request = new PdfExtraRequest
            {
                Operation = PdfExtraOperation.Arrange,
                SourcePath = item.FullPath,
                OutputFolder = folder,
                OpenPassword = PwdShared.Password,
                Plan = _tiles.Select(t => t.ToPlan()).ToList(),
                ExtraSources = _extraSources.ToList(),
            };
            var result = await Task.Run(() => PdfExtrasManager.Apply(request, null, cts.Token));

            var kind = RecordResult(item, result);
            _lastOutputFolder = kind == PdfItemStatus.Done ? Path.GetDirectoryName(result.OutputPath) ?? "" : "";
            SetRunning(false);
            ShowRunSummary(kind == PdfItemStatus.Done ? 1 : 0, kind == PdfItemStatus.Skipped ? 1 : 0, kind == PdfItemStatus.Failed ? 1 : 0, kind == PdfItemStatus.Cancelled ? 1 : 0);
        }
    }
}
