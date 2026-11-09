using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// ASCII Art Maker, the picture list: files and folders from the Pictures card (or dropped on the page), the list on the
    /// right, saving every picture in one go, and zooming the art with the mouse wheel.
    /// </summary>
    public partial class AsciiArtView
    {
        private const int MaxPictures = 2000;

        private readonly ObservableCollection<PixelSource> _sources = new();
        private readonly ObservableCollection<AsciiItem> _items = new();
        private readonly SemaphoreSlim _thumbGate = new(3);
        private int _syncRun;
        private AsciiItem? _current;
        private bool _restoring;
        private CancellationTokenSource? _batchCts;

        private bool BatchBusy => _batchCts != null;

        private void InitPictures()
        {
            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => Loc.F("Str_AsciiArt_FilterPictures", "Pictures|{0}|All files|*.*", ImageToPixelManager.PictureFilter);
            PictureList.ItemsSource = _items;
            _sources.CollectionChanged += (_, _) => InputCard.SetEmpty(_sources.Count == 0);
            _items.CollectionChanged += (_, _) => UpdateListState();
            StageScroll.PreviewMouseWheel += StageScroll_PreviewMouseWheel;
            Loaded += (_, _) => { if (CmbBatchFormat.SelectedIndex < 0) CmbBatchFormat.SelectedIndex = 0; };
        }

        // =================================================================
        //  Pictures in
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => AddSources(e.Paths);

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => _ = ResyncAsync();

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (BatchBusy) return;
            _syncRun++;
            _sources.Clear();
            _items.Clear();
            _current = null;
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (BatchBusy || (sender as FrameworkElement)?.Tag is not PixelSource source) return;
            _sources.Remove(source);
            _ = ResyncAsync();
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !BatchBusy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (!BatchBusy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddSources(files);
        }

        private void AddSources(IEnumerable<string> paths)
        {
            if (BatchBusy) return;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !(File.Exists(path) && ImageToPixelManager.IsPicture(path))) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new PixelSource { FullPath = path, IsFolder = isFolder });
            }
            _ = ResyncAsync();
        }

        /// <summary>Makes the list match the sources: new pictures are added, ones no longer wanted are removed.</summary>
        private async Task ResyncAsync()
        {
            int run = ++_syncRun;
            var paths = _sources.Select(s => s.FullPath).ToList();
            bool include = InputCard.IncludeSubfolders;
            var (files, inaccessible) = await Task.Run(() => ImageToPixelManager.FindPictures(paths, include));
            if (run != _syncRun) return;

            bool capped = files.Count > MaxPictures;
            if (capped) files = files.Take(MaxPictures).ToList();
            var wanted = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

            for (int i = _items.Count - 1; i >= 0; i--)
                if (!wanted.Contains(_items[i].FullPath)) _items.RemoveAt(i);

            var have = new HashSet<string>(_items.Select(t => t.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<AsciiItem>();
            foreach (string file in files)
            {
                if (have.Contains(file)) continue;
                var item = new AsciiItem { FullPath = file };
                _items.Add(item);
                added.Add(item);
            }

            foreach (var source in _sources)
            {
                if (!source.IsFolder) continue;
                string prefix = source.FullPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                source.Count = files.Count(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
            InputCard.ItemsSource = null;
            InputCard.ItemsSource = _sources;

            if (inaccessible > 0) ShowStatus(Loc.F("Str_AsciiArt_MsgInaccessible", "{0} folder(s) could not be read and were skipped.", inaccessible), StatusKind.Info);
            else if (capped) ShowStatus(Loc.F("Str_AsciiArt_MsgListFull", "The list is full ({0:N0} pictures).", MaxPictures), StatusKind.Info);
            else if (_sources.Count > 0 && files.Count == 0) ShowStatus(Loc.T("Str_AsciiArt_MsgNoneFound", "No pictures were found there."), StatusKind.Info);
            else HideStatus();

            LoadThumbnails(added);
            if (_items.Count > 0 && PictureList.SelectedItem == null) PictureList.SelectedIndex = 0;
        }

        private async void LoadThumbnails(IReadOnlyList<AsciiItem> items)
        {
            foreach (var item in items.Take(400))
            {
                await _thumbGate.WaitAsync();
                _ = Task.Run(() =>
                {
                    try
                    {
                        var thumb = SpritePackerManager.LoadThumbnail(item.FullPath, 76);
                        Dispatcher.InvokeAsync(() => item.Thumbnail = thumb);
                    }
                    finally
                    {
                        _thumbGate.Release();
                    }
                });
            }
        }

        private void UpdateListState()
        {
            bool many = _items.Count > 1;
            PictureListCard.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
            PanelBatch.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
            TxtListTitle.Text = Loc.N(_items.Count, "Str_AsciiArt_ListOne", "{0} picture", "Str_AsciiArt_ListMany", "{0} pictures", _items.Count);
            if (_items.Count == 0)
            {
                _current = null;
                _sourcePath = null;
            }
        }

        // =================================================================
        //  Picking one of them
        // =================================================================

        private void PictureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoring || PictureList.SelectedItem is not AsciiItem item || ReferenceEquals(item, _current)) return;
            var previous = _current;
            _current = item;
            _previousForRestore = previous;
            LoadPicture(item.FullPath);
        }

        private AsciiItem? _previousForRestore;

        /// <summary>The user kept the drawing: put the list selection back on the picture it belongs to.</summary>
        private void RestoreSelection()
        {
            _restoring = true;
            try
            {
                _current = _previousForRestore;
                PictureList.SelectedItem = _previousForRestore;
            }
            finally { _restoring = false; }
        }

        // =================================================================
        //  Save every picture
        // =================================================================

        private async void BtnSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (BatchBusy) { _batchCts?.Cancel(); return; }
            if (_items.Count == 0) return;
            if (OutputCard.Status.IsError)
            {
                ShowStatus(Loc.F("Str_AsciiArt_MsgCantSaveThere", "Can't save there: {0}", OutputCard.Status.Text), StatusKind.Error);
                return;
            }
            string folder;
            try { folder = OutputCard.EnsureFolder(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowStatus(Loc.F("Str_AsciiArt_MsgCantSaveThere", "Can't save there: {0}", ex.Message), StatusKind.Error);
                return;
            }

            var settings = Snapshot();
            string ext = TagOf(CmbBatchFormat);
            if (ext.Length == 0) ext = "txt";
            AsciiGlyphs glyphs;
            try { glyphs = AsciiGlyphs.Create(settings.FontName); }
            catch (InvalidOperationException ex)
            {
                ShowStatus(Loc.F("Str_AsciiArt_MsgNoFont", "No monospace font could be loaded: {0}", ex.Message), StatusKind.Error);
                return;
            }

            var cts = _batchCts = new CancellationTokenSource();
            SetBatchUi(true);
            foreach (var item in _items) { item.Status = AsciiItemStatus.Waiting; item.Message = ""; }
            HideStatus();
            int done = 0, failed = 0;
            try
            {
                foreach (var item in _items.ToList())
                {
                    cts.Token.ThrowIfCancellationRequested();
                    item.Status = AsciiItemStatus.Working;
                    var result = await AsciiArtManager.ConvertFileAsync(item.FullPath, settings, cts.Token);
                    if (result.Grid == null || result.Info == null)
                    {
                        item.Status = AsciiItemStatus.Failed;
                        item.Message = result.Problem == AsciiProblem.TooFewChars
                            ? Loc.T("Str_AsciiArt_MsgTooFewChars", "Pick at least two different characters (the font must be able to draw them).")
                            : Loc.T("Str_AsciiArt_ItemFailed", "Could not be made");
                        failed++;
                        if (result.Problem == AsciiProblem.TooFewChars) break;   // every picture would fail the same way
                        continue;
                    }
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(item.FullPath) + "." + ext;
                        var saved = await AsciiArtManager.SaveAsync(Path.Combine(folder, name), result.Grid, settings, glyphs, result.Info.SourceWidth);
                        item.Status = AsciiItemStatus.Done;
                        item.Message = Path.GetFileName(saved.Path);
                        done++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
                    {
                        Log.Warn($"ASCII art batch save failed for {item.FullPath}: {ex.Message}");
                        item.Status = AsciiItemStatus.Failed;
                        item.Message = ex.Message;
                        failed++;
                    }
                }
                ShowStatus(failed == 0
                    ? Loc.N(done, "Str_AsciiArt_MsgAllSavedOne", "Saved {0} picture.", "Str_AsciiArt_MsgAllSavedMany", "Saved {0} pictures.", done)
                    : Loc.F("Str_AsciiArt_MsgSavedSome", "Saved {0}, {1} could not be made.", done, failed),
                    failed == 0 ? StatusKind.Success : StatusKind.Info);
            }
            catch (OperationCanceledException)
            {
                foreach (var item in _items.Where(i => i.Status is AsciiItemStatus.Working or AsciiItemStatus.Waiting)) item.Status = AsciiItemStatus.Waiting;
                ShowStatus(Loc.F("Str_AsciiArt_MsgBatchStopped", "Stopped after {0}. Nothing already saved was removed.", done), StatusKind.Info);
            }
            finally
            {
                _batchCts = null;
                cts.Dispose();
                SetBatchUi(false);
                OutputCard.Refresh();
            }
        }

        private void SetBatchUi(bool busy)
        {
            TxtSaveAll.Text = busy ? Loc.T("Str_Common_Cancel", "Cancel") : Loc.T("Str_AsciiArt_SaveAll", "Save all");
            IconSaveAll.Kind = busy ? MaterialDesignThemes.Wpf.PackIconKind.Stop : MaterialDesignThemes.Wpf.PackIconKind.ContentSaveAll;
            RunProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            InputCard.IsLocked = busy;
            OutputCard.IsLocked = busy;
            CmbBatchFormat.IsEnabled = !busy;
            PictureList.IsEnabled = !busy;
        }

        // =================================================================
        //  Zoom with the mouse wheel
        // =================================================================

        /// <summary>
        /// The wheel zooms the art around the mouse pointer (the picture under the pointer stays under it). The scroll bars and
        /// the Fit button are still there for moving around. Shift + wheel scrolls sideways.
        /// </summary>
        private void StageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_hasGrid) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                StageScroll.ScrollToHorizontalOffset(StageScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }

            double old = ZoomScale.ScaleX;
            double next = Math.Clamp(old * Math.Pow(1.1, e.Delta / 120.0), SliderZoom.Minimum, SliderZoom.Maximum);
            if (Math.Abs(next - old) < 0.0001) { e.Handled = true; return; }

            var mouse = e.GetPosition(StageScroll);
            var margin = ZoomHost.Margin;
            double contentX = (StageScroll.HorizontalOffset + mouse.X - margin.Left) / old;
            double contentY = (StageScroll.VerticalOffset + mouse.Y - margin.Top) / old;

            _autoFit = false;
            SliderZoom.Value = next;   // its handler sets the scale
            StageScroll.UpdateLayout();
            StageScroll.ScrollToHorizontalOffset(Math.Max(0, contentX * next + margin.Left - mouse.X));
            StageScroll.ScrollToVerticalOffset(Math.Max(0, contentY * next + margin.Top - mouse.Y));
            e.Handled = true;
        }
    }
}
