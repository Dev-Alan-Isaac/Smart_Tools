using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Media Viewer - UI only. Logic lives in MediaViewerManager.
    /// One list for pictures, video and sound: pictures show with zoom, pan
    /// and an optional slideshow; video and sound play in the same window.
    /// Laid out like Video Replay and Image Viewer.
    /// </summary>
    public partial class MediaViewerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double PlaylistExpandedWidth = 380;

        private readonly BulkObservableCollection<MediaItem> _items = new();   // a 10,000-file folder is one change notification, not 10,000
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromSeconds(4) };
        private const int SkipWaitSeconds = 5;
        private int _failStreak;                // unplayable files in a row, so a list of bad files can't loop forever
        private bool _skipImmediately;
        private MediaItem? _skipTarget;
        private readonly bool _ready;
        private readonly List<MediaSource> _sources = new();                  // what was added: files and folders
        private readonly ObservableCollection<MediaQueueGroup> _groups = new();   // the input card's rows
        private double _speed = 1;

        private int _index = -1;
        private int _loadVersion;
        private MediaItem? _nowPlaying;
        private bool _updatingSlider;
        private bool _isUserSeeking;
        private bool _resumeAfterSeek;
        private long _lastScrubTick;
        private bool _slideshow;
        private bool _playing;
        private bool _mediaOpen;
        private bool _optionsCollapsed;
        private bool _playlistCollapsed;
        private bool _theater;

        private readonly MediaViewerManager.ShuffleBag _bag = new();
        private readonly List<MediaItem> _history = new();   // shuffle: what played before, for Previous
        private double _angle;                               // picture rotation: 0, 90, 180, 270
        private bool _goingBack;

        // Picture zoom: 0 = fit to the window, otherwise a scale (1 = 100%).
        private double _zoom;
        private int _pixelWidth, _pixelHeight;
        private bool _panning;
        private Point _panStart;
        private double _panStartH, _panStartV;

        public MediaViewerView()
        {
            InitializeComponent();
            DataGridQueue.ItemsSource = _items;
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = MediaViewerManager.FileDialogFilter;
            _tick.Tick += (_, _) => UpdateProgress();
            _slide.Tick += (_, _) => StepForSlideshow();
            SkipRing.Completed += SkipRing_Completed;
            _ready = true;
            Player.Volume = VolumeSlider.Value;
            AudioBars.Source = FeedSpectrum;
            ApplyPanels();
            UpdateOrderUi();
            RebuildGroups();
            UpdateUi();
            InitializeSettings();
        }

        private static string S(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        private MediaItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        // =================================================================
        //  Panels
        // =================================================================

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
            ScheduleSave();
        }

        private void BtnTheater_Click(object sender, RoutedEventArgs e) => ToggleTheater();

        private void ToggleTheater()
        {
            _theater = !_theater;
            ApplyPanels();
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

        // =================================================================
        //  Opening
        // =================================================================

        /// <summary>Play with an empty list: ask for files (the input card in the sidebar does the same).</summary>
        private void OpenFilesDialog()
        {
            var dialog = new OpenFileDialog { Title = S("Str_MediaViewer_OpenFiles", "Open files"), Multiselect = true, Filter = MediaViewerManager.FileDialogFilter };
            if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FileNames);
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => _ = AddPathsAsync(e.Paths);

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            RebuildGroups();
            ScheduleSave();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e) => InputCard_ClearRequested(sender, EventArgs.Empty);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            _items.Clear();
            _sources.Clear();
            _bag.Reset();
            _history.Clear();
            RebuildGroups();
            Show(-1);
        }

        // Dragging over the page lights up the input card (like Video Replay).
        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) InputCard.SetDropHighlight(true);
            e.Handled = true;
        }

        private void Root_DragLeave(object sender, DragEventArgs e) => InputCard.SetDropHighlight(false);

        private void Root_Drop(object sender, DragEventArgs e)
        {
            InputCard.SetDropHighlight(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        /// <summary>Lists the new files/folders off the UI thread, then appends them (nothing already listed is touched).</summary>
        /// <summary>Adds files or folders to the queue, as if they had been dropped here (the Media Viewer hub forwards drops this way).</summary>
        public void AddPaths(IEnumerable<string> paths) => _ = AddPathsAsync(paths);

        private async Task AddPathsAsync(IEnumerable<string> paths)
        {
            var fresh = new List<MediaSource>();
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                fresh.Add(new MediaSource { FullPath = path, IsFolder = isFolder });
            }
            if (fresh.Count == 0) return;

            bool subfolders = InputCard.IncludeSubfolders;
            var found = await Task.Run(() => MediaViewerManager.Expand(fresh.Select(f => f.FullPath).ToList(), subfolders));
            var known = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            int firstNew = _items.Count;
            _items.AddRange(found.Where(item => known.Add(item.Path)).ToList());

            if (_items.Count == firstNew)
            {
                ShowError(S("Str_MediaViewer_NothingFound", "No pictures, videos or music found there."));
                return;
            }
            // Two drops of the same folder can overlap: the check above ran before the await, so it is repeated here.
            fresh.RemoveAll(f => _sources.Any(s => string.Equals(s.FullPath, f.FullPath, StringComparison.OrdinalIgnoreCase)));
            _sources.AddRange(fresh);
            RebuildGroups();
            InputCard.PopDropArea();
            if (_index < 0 || Current == null) Show(firstNew);
            else UpdateUi();
        }

        /// <summary>Rebuilds the input card's rows from what was added; sources with nothing left drop out.</summary>
        private void RebuildGroups()
        {
            var present = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            _sources.RemoveAll(s => s.IsFolder
                ? !_items.Any(i => i.Path.StartsWith(s.FullPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                : !present.Contains(s.FullPath));

            bool subfolders = InputCard.IncludeSubfolders;
            _groups.Clear();
            foreach (var g in MediaViewerManager.BuildGroups(_sources, _items))
            {
                g.Subtitle = g.IsFolder
                    ? string.Format(CultureInfo.CurrentCulture, S("Str_MediaViewer_ItemsCount", "{0:N0} items"), g.ItemCount)
                      + " · " + S(subfolders ? "Str_MediaViewer_WithSubfolders" : "Str_MediaViewer_FolderOnly", subfolders ? "with subfolders" : "this folder only")
                    : g.FilePaths.Count == 1
                        ? Path.GetFileName(g.FilePaths[0])
                        : string.Format(CultureInfo.CurrentCulture, S("Str_MediaViewer_FilesCount", "{0:N0} files"), g.FilePaths.Count);
                _groups.Add(g);
            }
            InputCard.SetEmpty(_groups.Count == 0);
            ScheduleSave();   // the list changed
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: MediaQueueGroup group }) return;
            RemoveItems(MediaViewerManager.ItemsOf(group, _items));
        }

        // =================================================================
        //  Showing one item
        // =================================================================

        private MediaSpectrumFeeder? _spectrum;

        /// <summary>Called by the music bars about 30 times a second: keeps the spectrum reader at the player's position and hands back the levels.</summary>
        private bool FeedSpectrum(float[] levels)
        {
            var spectrum = _spectrum;
            if (spectrum == null)
            {
                Array.Clear(levels);
                return false;
            }
            spectrum.Sync(Player.Position, _playing && _mediaOpen);
            return spectrum.GetLevels(levels);
        }

        private void StopSpectrum()
        {
            var spectrum = _spectrum;
            _spectrum = null;
            spectrum?.Dispose();
        }

        private void StopMedia()
        {
            StopSpectrum();
            _tick.Stop();
            _playing = false;
            _mediaOpen = false;
            try
            {
                Player.Stop();
                Player.Source = null;   // lets go of the file
            }
            catch (Exception)
            {
                // nothing was loaded
            }
        }

        private void HideStage()
        {
            ImageScroll.Visibility = Visibility.Collapsed;
            Player.Visibility = Visibility.Collapsed;
            AudioPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string text)
        {
            TxtError.Text = text;
            ErrorBanner.Visibility = Visibility.Visible;
            if (Current == null) EmptyPanel.Visibility = Visibility.Visible;
            UpdateUi();
        }

        // Dismissing the banner also cancels a pending skip, so the user can stay on the file.
        private void BtnDismissError_Click(object sender, RoutedEventArgs e)
        {
            CancelSkip();
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        private void CancelSkip()
        {
            SkipRing.Stop();
            SkipRingHost.Visibility = Visibility.Collapsed;
            _skipTarget = null;
        }

        /// <summary>
        /// The current file can't be played or shown: tell the user, then move on to the next one -
        /// after 5 seconds, or at once when the Settings page says so.
        /// </summary>
        private void HandleUnplayable(string message)
        {
            CancelSkip();
            ShowError(message);
            _failStreak++;

            // RepeatOne would hand back the same broken file, so it skips like Sequential.
            var next = ChooseNext(userAsked: Order == ViewerPlayOrder.RepeatOne);
            if (next == null || ReferenceEquals(next, Current) || _failStreak >= _items.Count)
            {
                TxtError.Text = message + "\n\n" + S("Str_MediaViewer_SkippedLast", "No more files to play.");
                _failStreak = 0;
                return;
            }

            _skipTarget = next;
            if (_skipImmediately)
            {
                SkipToTarget();
                return;
            }
            TxtError.Text = message + "\n\n" + S("Str_MediaViewer_SkippingIn", "Skipping to the next file…");
            SkipRingHost.Visibility = Visibility.Visible;
            SkipRing.Start(TimeSpan.FromSeconds(SkipWaitSeconds));
        }

        private void SkipRing_Completed(object? sender, EventArgs e) => SkipToTarget();

        private void SkipToTarget()
        {
            var target = _skipTarget;
            CancelSkip();
            int at = target == null ? -1 : _items.IndexOf(target);
            if (at >= 0) Show(at);
        }

        private void SetNowPlaying(MediaItem? item)
        {
            if (_nowPlaying != null) _nowPlaying.IsNowPlaying = false;
            _nowPlaying = item;
            if (item != null) item.IsNowPlaying = true;
        }

        private void Show(int index)
        {
            _restoredIdle = false;
            CancelSkip();
            StopMedia();
            _slide.Stop();
            SlideRing.Stop();
            _loadVersion++;
            HideStage();
            ImageView.Source = null;
            _angle = 0;
            ImageRotate.Angle = 0;
            PosSlider.Value = 0;
            TxtPosition.Text = TxtDuration.Text = "0:00";
            TxtInfo.Text = "";

            if (index < 0 || index >= _items.Count)
            {
                _index = -1;
                SetNowPlaying(null);
                TxtNowPlaying.Text = "";
                EmptyPanel.Visibility = Visibility.Visible;
                UpdateUi();
                return;
            }

            _index = index;
            var item = _items[index];
            if (!_goingBack && _nowPlaying != null && !ReferenceEquals(_nowPlaying, item) && _items.Contains(_nowPlaying))
            {
                _history.Add(_nowPlaying);   // for Previous while shuffling
                if (_history.Count > 200) _history.RemoveAt(0);
            }
            _goingBack = false;
            _bag.Played(item);
            SetNowPlaying(item);
            DataGridQueue.ScrollIntoView(item);
            TxtNowPlaying.Text = item.Name;

            if (item.Kind == ViewerMediaKind.Image) ShowImage(item);
            else ShowMedia(item);
            UpdateUi();
            Focus();
            ScheduleSave();   // remembers which item was on the stage
        }

        private void ShowImage(MediaItem item)
        {
            ImageScroll.Visibility = Visibility.Visible;
            _zoom = 0;
            _ = LoadImageAsync(item.Path, _loadVersion);
            if (_slideshow) StartSlideshowTimer();
        }

        private async Task LoadImageAsync(string path, int version)
        {
            var loaded = await Task.Run(() => ImageViewerManager.LoadForView(path));
            if (version != _loadVersion) return;   // another item was chosen meanwhile
            if (loaded is not { } result)
            {
                HandleUnplayable(S("Str_MediaViewer_ImageFailed", "This picture couldn't be opened."));
                return;
            }
            _failStreak = 0;
            ImageView.Source = result.Bitmap;
            _pixelWidth = result.Bitmap.PixelWidth;
            _pixelHeight = result.Bitmap.PixelHeight;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch (Exception) { /* shown as 0 B */ }
            TxtInfo.Text = $"{_pixelWidth} × {_pixelHeight} · {ImageViewerManager.FormatName(path)} · {MediaViewerManager.FormatSize(size)}";
            ApplyZoom();
        }

        private void ShowMedia(MediaItem item)
        {
            bool audio = item.Kind == ViewerMediaKind.Audio;
            Player.Visibility = Visibility.Visible;
            Player.Opacity = audio ? 0 : 1;
            if (audio)
            {
                TxtAudioName.Text = item.Name;
                AudioPanel.Visibility = Visibility.Visible;
            }
            TxtInfo.Text = item.Kind == ViewerMediaKind.Video ? S("Str_MediaViewer_Video", "Video") : S("Str_MediaViewer_Audio", "Music");
            _ = StartPlayerAsync(item, _loadVersion);
        }

        /// <summary>
        /// Opens the file in the player. A name with '#' or '%' (like "100% #1.mp4") is a valid Uri, but Windows' media
        /// engine still refuses to open it, so it is played through a safely named link/copy in %TEMP% instead.
        /// </summary>
        private async Task StartPlayerAsync(MediaItem item, int version)
        {
            string path = item.Path;
            if (MediaViewerManager.NeedsSafeName(path))
            {
                string? alias = await Task.Run(() => MediaViewerManager.MakeSafeAlias(path));
                if (version != _loadVersion) return;   // another item was chosen meanwhile
                if (alias != null) path = alias;
            }

            try
            {
                Player.Source = new Uri(path);
                Player.SpeedRatio = CurrentSpeed;
                Player.Play();
                _playing = true;
                _tick.Start();
            }
            catch (Exception ex)
            {
                HandleUnplayable(ex.Message);
            }
        }

        // =================================================================
        //  Playlist
        // =================================================================

        private void DataGridQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(DataGridQueue, source) is DataGridRow { Item: MediaItem item })
            {
                Show(_items.IndexOf(item));
                e.Handled = true;
            }
        }

        private void DataGridQueue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataGridQueue.SelectedItem is MediaItem item)
            {
                Show(_items.IndexOf(item));
                e.Handled = true;
            }
        }

        private void BtnRemoveFromList_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MediaItem item) RemoveItems(new[] { item });
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e) =>
            RemoveItems(DataGridQueue.SelectedItems.OfType<MediaItem>().ToList());

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => DataGridQueue.SelectAll();

        /// <summary>Takes items off the list (the files themselves are never deleted).</summary>
        private void RemoveItems(IReadOnlyCollection<MediaItem> victims)
        {
            if (victims.Count == 0) return;
            var current = Current;
            int oldIndex = _index;
            bool removingCurrent = current != null && victims.Contains(current);
            foreach (var v in victims.ToList()) _items.Remove(v);
            RebuildGroups();

            if (removingCurrent)
            {
                Show(_items.Count == 0 ? -1 : Math.Min(oldIndex, _items.Count - 1));
            }
            else
            {
                _index = current == null ? -1 : _items.IndexOf(current);
                UpdateUi();
            }
        }

        // =================================================================
        //  Playback
        // =================================================================

        private static string DialogTitle => S("Str_Tool_media-viewer_Name", "Media Viewer");

        /// <summary>The ticked rows; with none, the one on the stage.</summary>
        private List<MediaItem> Targets()
        {
            var selected = DataGridQueue.SelectedItems.OfType<MediaItem>().ToList();
            if (selected.Count == 0 && Current != null) selected.Add(Current);
            return selected;
        }

        /// <summary>A right-click picks the row under the pointer (unless it's already part of the selection).</summary>
        private void DataGridQueue_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(DataGridQueue, source) is DataGridRow { Item: MediaItem item } row && !row.IsSelected)
            {
                DataGridQueue.SelectedItems.Clear();
                row.IsSelected = true;
                DataGridQueue.SelectedItem = item;
            }
        }

        private void MenuPlay_Click(object sender, RoutedEventArgs e)
        {
            if (DataGridQueue.SelectedItem is MediaItem item) Show(_items.IndexOf(item));
        }

        private void MenuRemove_Click(object sender, RoutedEventArgs e) => RemoveItems(Targets());

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DataGridQueue.SelectedItem is not MediaItem item) return;
            if (ShellExplorer.Show(item.Path) == ShellExplorer.ShowResult.NotFound)
                System.Windows.MessageBox.Show(string.Format(S("Str_MediaViewer_NoLongerThere", "\"{0}\" is no longer there."), item.Name),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void MenuDeleteFile_Click(object sender, RoutedEventArgs e) => await DeleteFilesAsync(Targets());

        private async void BtnDeleteFiles_Click(object sender, RoutedEventArgs e) => await DeleteFilesAsync(Targets());

        private bool _deleting;

        /// <summary>Deletes the files from the computer (Recycle Bin where there is one) after asking, then takes them off the list. The deleting itself runs off the UI thread, so a big selection doesn't freeze the window.</summary>
        private async Task DeleteFilesAsync(List<MediaItem> targets)
        {
            if (targets.Count == 0 || _deleting) return;
            bool bin = targets.All(t => ImageViewerManager.HasRecycleBin(t.Path));
            string text = targets.Count == 1
                ? string.Format(S(bin ? "Str_MediaViewer_DeleteBinOne" : "Str_MediaViewer_DeletePermOne",
                    bin ? "Move \"{0}\" to the Recycle Bin?\n\nThis deletes the file from your computer, not just from the list."
                        : "Delete \"{0}\" PERMANENTLY?\n\nIt's on a drive without a Recycle Bin, so it can't be undone."), targets[0].Name)
                : string.Format(S(bin ? "Str_MediaViewer_DeleteBinMany" : "Str_MediaViewer_DeletePermMany",
                    bin ? "Move {0} files to the Recycle Bin?\n\nThis deletes them from your computer, not just from the list."
                        : "Delete {0} files PERMANENTLY?\n\nSome are on a drive without a Recycle Bin, so this can't be undone."), targets.Count);
            if (System.Windows.MessageBox.Show(text, DialogTitle, MessageBoxButton.YesNo,
                    bin ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            // A file that's playing is held open by the player - let go of it first.
            var current = Current;
            bool stoppedCurrent = current != null && targets.Contains(current) && current.Kind != ViewerMediaKind.Image;
            if (stoppedCurrent) StopMedia();

            var deleted = new List<MediaItem>();
            MediaItem? failedItem = null;
            string failedMessage = "";
            _deleting = true;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var item in targets)
                    {
                        try
                        {
                            ImageViewerManager.DeleteImage(item.Path);   // works for any file: Recycle Bin, else permanent
                            deleted.Add(item);
                        }
                        catch (Exception ex)
                        {
                            failedItem = item;
                            failedMessage = ex.Message;
                            break;
                        }
                    }
                });
            }
            finally
            {
                _deleting = false;
            }
            string? failure = failedItem == null ? null
                : string.Format(S("Str_MediaViewer_DeleteFailed", "Couldn't delete \"{0}\": {1}"), failedItem.Name, failedMessage);

            if (deleted.Count > 0) RemoveItems(deleted);
            else if (stoppedCurrent && current != null && _items.Contains(current)) Show(_items.IndexOf(current));   // nothing deleted - carry on playing
            if (failure != null)
                System.Windows.MessageBox.Show(failure, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

        /// <summary>Moves the selected rows one step; a block of rows moves together and stops at the edge.</summary>
        private void MoveSelected(int delta)
        {
            var selected = DataGridQueue.SelectedItems.OfType<MediaItem>().ToList();
            if (selected.Count == 0) return;
            var chosen = new HashSet<MediaItem>(selected);
            var current = Current;
            var ordered = selected.OrderBy(i => _items.IndexOf(i)).ToList();
            if (delta > 0) ordered.Reverse();

            foreach (var item in ordered)
            {
                int at = _items.IndexOf(item);
                int to = at + delta;
                if (to < 0 || to >= _items.Count || chosen.Contains(_items[to])) continue;
                _items.Move(at, to);
            }
            _index = current == null ? -1 : _items.IndexOf(current);
            _bag.Reset();
            UpdateUi();
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_items.Count < 2) return;
            var current = Current;
            var sorted = MediaViewerManager.Sort(_items, byDate);
            for (int i = 0; i < sorted.Count; i++)
            {
                int at = _items.IndexOf(sorted[i]);
                if (at != i) _items.Move(at, i);
            }
            _index = current == null ? -1 : _items.IndexOf(current);
            _bag.Reset();
            UpdateUi();
        }

        private double CurrentSpeed => _speed;

        private RadioButton[] SlideRadios => new[] { RadioSlide2, RadioSlide4, RadioSlide7, RadioSlide10 };

        // The speed pill on the player: click for faster, right-click (or Shift+S) for slower, like Video Replay.
        private void BtnSpeed_Click(object sender, RoutedEventArgs e) => StepSpeed(+1);

        private void BtnSpeed_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            StepSpeed(-1);
            e.Handled = true;
        }

        private void StepSpeed(int direction)
        {
            var speeds = MediaViewerManager.Speeds.ToList();
            int i = speeds.IndexOf(MediaViewerManager.ClampSpeed(_speed));
            i = (i + direction + speeds.Count) % speeds.Count;
            SetSpeed(speeds[i]);
            UiMotion.Pop(BtnSpeed, 0.9);
        }

        private void SetSpeed(double speed)
        {
            _speed = MediaViewerManager.ClampSpeed(speed);
            if (_mediaOpen) Player.SpeedRatio = _speed;
            BtnSpeed.Content = MediaViewerManager.SpeedLabel(_speed);
            ScheduleSave();
        }

        private void Slide_Click(object sender, RoutedEventArgs e)
        {
            int seconds = SlideRadios.FirstOrDefault(r => r.IsChecked == true)?.Tag is string tag && int.TryParse(tag, out int v) ? v : 4;
            _slide.Interval = TimeSpan.FromSeconds(seconds);
            if (_slideshow && _slide.IsEnabled) StartSlideshowTimer();   // the ring starts over at the new length
            ScheduleSave();
        }

        /// <summary>(Re)starts the picture timer and its ring together, so the ring always shows the time left.</summary>
        private void StartSlideshowTimer()
        {
            _slide.Stop();
            _slide.Start();
            SlideRing.Start(_slide.Interval);
        }

        private void StopSlideshowTimer()
        {
            _slide.Stop();
            SlideRing.Stop();
        }

        private void TogglePlay()
        {
            var item = Current;
            if (item == null)
            {
                if (_items.Count > 0) Show(0);   // a list is there but nothing chosen: start at the top
                else OpenFilesDialog();
                return;
            }
            if (_restoredIdle)
            {
                Show(_index);   // the item from last session: load it now
                return;
            }

            if (item.Kind == ViewerMediaKind.Image)
            {
                _slideshow = !_slideshow;
                if (_slideshow) StartSlideshowTimer();
                else StopSlideshowTimer();
            }
            else if (_playing)
            {
                Player.Pause();
                _playing = false;
            }
            else
            {
                Player.Play();
                _playing = true;
                _tick.Start();
            }
            UpdateUi();
        }

        private ViewerPlayOrder Order =>
            RadioOrderShuffle.IsChecked == true ? ViewerPlayOrder.Shuffle
            : RadioOrderRepeatOne.IsChecked == true ? ViewerPlayOrder.RepeatOne
            : ViewerPlayOrder.Sequential;

        private bool Repeat => ChkRepeatPlaylist.IsChecked == true && Order != ViewerPlayOrder.RepeatOne;

        private void Order_Click(object sender, RoutedEventArgs e)
        {
            _bag.Reset();
            _history.Clear();
            UpdateOrderUi();
            UpdateUi();
            ScheduleSave();
        }

        private void Repeat_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            UpdateOrderUi();
            ScheduleSave();
        }

        /// <summary>The line under the order tiles says what the choice does, and "repeat the playlist" only applies when a list plays through.</summary>
        private void UpdateOrderUi()
        {
            ChkRepeatPlaylist.IsEnabled = Order != ViewerPlayOrder.RepeatOne;
            string key = Order switch
            {
                ViewerPlayOrder.Shuffle => Repeat ? "Str_MediaViewer_Hint_ShuffleRepeat" : "Str_MediaViewer_Hint_Shuffle",
                ViewerPlayOrder.RepeatOne => "Str_MediaViewer_Hint_RepeatOne",
                _ => Repeat ? "Str_MediaViewer_Hint_SequentialRepeat" : "Str_MediaViewer_Hint_Sequential",
            };
            TxtOrderHint.Text = S(key, key);
        }

        /// <summary>
        /// What plays after the current item. Pressing Next (<paramref name="userAsked"/>)
        /// always moves on, even when "repeat one" is chosen, and wraps round to the start.
        /// Null = the end of the list.
        /// </summary>
        private MediaItem? ChooseNext(bool userAsked)
        {
            if (_items.Count == 0) return null;
            var order = userAsked && Order == ViewerPlayOrder.RepeatOne ? ViewerPlayOrder.Sequential : Order;
            if (order == ViewerPlayOrder.RepeatOne) return Current;
            if (order == ViewerPlayOrder.Shuffle) return _bag.Next(_items, Current, Repeat || userAsked);
            int next = _index + 1;
            if (next < _items.Count) return _items[next];
            return Repeat || userAsked ? _items[0] : null;
        }

        private void StepForSlideshow()
        {
            if (_items.Count == 0 || Current?.Kind != ViewerMediaKind.Image) return;
            if (ChooseNext(userAsked: true) is { } next) Show(_items.IndexOf(next));
        }

        private void GoNext()
        {
            if (ChooseNext(userAsked: true) is { } next) Show(_items.IndexOf(next));
        }

        private void GoPrevious()
        {
            if (_items.Count == 0) return;

            // More than 3 s into a video or song: Previous starts it again.
            if (Current is { Kind: not ViewerMediaKind.Image } && _mediaOpen && Player.Position > TimeSpan.FromSeconds(3))
            {
                Player.Position = TimeSpan.Zero;
                return;
            }

            if (Order == ViewerPlayOrder.Shuffle)
            {
                while (_history.Count > 0)
                {
                    var back = _history[^1];
                    _history.RemoveAt(_history.Count - 1);
                    int at = _items.IndexOf(back);
                    if (at < 0) continue;   // taken off the list since
                    _goingBack = true;   // going back shouldn't be recorded, or Previous would bounce between two items
                    Show(at);
                    return;
                }
            }
            Show(_index > 0 ? _index - 1 : _items.Count - 1);
        }

        private void BtnSkipBack_Click(object sender, RoutedEventArgs e) => SkipBy(TimeSpan.FromSeconds(-10));

        private void BtnSkipForward_Click(object sender, RoutedEventArgs e) => SkipBy(TimeSpan.FromSeconds(10));

        private void SkipBy(TimeSpan delta)
        {
            if (!_mediaOpen || Current is not { Kind: not ViewerMediaKind.Image }) return;
            var target = Player.Position + delta;
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (Player.NaturalDuration.HasTimeSpan && target > Player.NaturalDuration.TimeSpan) target = Player.NaturalDuration.TimeSpan;
            Player.Position = target;
            TxtPosition.Text = MediaViewerManager.FormatTime(target);
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => GoNext();
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => GoPrevious();
        private void Player_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => TogglePlay();

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            _mediaOpen = true;
            _failStreak = 0;
            // The music bars read the file a second time, so they start only once the player has opened it (never competing with it).
            if (Current is { Kind: ViewerMediaKind.Audio } playing && _spectrum == null)
            {
                try { _spectrum = new MediaSpectrumFeeder(playing.Path); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OutOfMemoryException) { _spectrum = null; }
            }
            Player.SpeedRatio = CurrentSpeed;   // MediaElement forgets the speed when the source changes
            if (Player.NaturalDuration.HasTimeSpan)
            {
                var length = Player.NaturalDuration.TimeSpan;
                _updatingSlider = true;
                PosSlider.Maximum = Math.Max(1, length.TotalSeconds);
                _updatingSlider = false;
                TxtDuration.Text = MediaViewerManager.FormatTime(length);
            }
            if (Player.NaturalVideoWidth > 0 && Current?.Kind == ViewerMediaKind.Video)
                TxtInfo.Text = $"{Player.NaturalVideoWidth} × {Player.NaturalVideoHeight} · {S("Str_MediaViewer_Video", "Video")}";
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            var next = ChooseNext(userAsked: false);
            if (next != null && ReferenceEquals(next, Current))
            {
                Player.Position = TimeSpan.Zero;   // repeat one
                Player.Play();
                return;
            }
            if (next != null)
            {
                Show(_items.IndexOf(next));   // plays on to the next one
                return;
            }
            Player.Stop();
            _playing = false;
            _tick.Stop();
            PosSlider.Value = 0;
            UpdateUi();
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _playing = false;
            string ext = Path.GetExtension(Current?.Path ?? "");
            HandleUnplayable(string.Format(S("Str_MediaViewer_PlayFailed", "Windows can't play {0} files here. Try Video Replay, which has the VLC player.") + "\n\n{1}",
                ext, e.ErrorException?.Message));
        }

        private void UpdateProgress()
        {
            if (!_mediaOpen) return;
            var position = Player.Position;
            TxtPosition.Text = MediaViewerManager.FormatTime(position);
            if (_isUserSeeking) return;   // the user is dragging it
            _updatingSlider = true;
            PosSlider.Value = Math.Min(position.TotalSeconds, PosSlider.Maximum);
            _updatingSlider = false;
        }

        private void PosSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _updatingSlider || !_mediaOpen) return;
            if (_isUserSeeking)
            {
                // dragging: the player is paused (no crackling audio); show a frame now and then, final seek on release
                TxtPosition.Text = MediaViewerManager.FormatTime(TimeSpan.FromSeconds(e.NewValue));
                if (Environment.TickCount64 - _lastScrubTick >= 100)
                {
                    _lastScrubTick = Environment.TickCount64;
                    Player.Position = TimeSpan.FromSeconds(e.NewValue);
                }
                return;
            }
            Player.Position = TimeSpan.FromSeconds(e.NewValue);
            TxtPosition.Text = MediaViewerManager.FormatTime(TimeSpan.FromSeconds(e.NewValue));
        }

        private void PosSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
            _resumeAfterSeek = _playing && _mediaOpen;
            if (_resumeAfterSeek) Player.Pause();   // seeking while playing is what garbles the picture and crackles the audio
        }

        private void PosSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndUserSeek();

        private void PosSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isUserSeeking && Mouse.LeftButton != MouseButtonState.Pressed) EndUserSeek();
        }

        private void EndUserSeek()
        {
            if (!_isUserSeeking) return;
            _isUserSeeking = false;
            bool resume = _resumeAfterSeek;
            _resumeAfterSeek = false;
            if (!_mediaOpen) return;
            var target = TimeSpan.FromSeconds(PosSlider.Value);
            Player.Position = target;
            TxtPosition.Text = MediaViewerManager.FormatTime(target);
            if (resume && _playing) Player.Play();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            Player.Volume = e.NewValue;
            if (e.NewValue > 0 && Player.IsMuted) Player.IsMuted = false;
            UpdateUi();
            ScheduleSave();
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            Player.IsMuted = !Player.IsMuted;
            UpdateUi();
            ScheduleSave();
        }

        // =================================================================
        //  Pictures: zoom, pan
        // =================================================================

        private double FitScale
        {
            get
            {
                if (_pixelWidth == 0 || _pixelHeight == 0) return 1;
                double w = Sideways ? _pixelHeight : _pixelWidth;   // a picture turned a quarter occupies the other way round
                double h = Sideways ? _pixelWidth : _pixelHeight;
                return Math.Min(ImageScroll.ActualWidth / w, ImageScroll.ActualHeight / h);
            }
        }

        private bool Sideways => _angle % 180 != 0;

        private void Rotate(double delta)
        {
            if (ImageView.Source == null) return;
            _angle = (_angle + delta + 360) % 360;
            ImageRotate.Angle = _angle;
            ApplyZoom();
        }

        private void BtnRotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);

        private void BtnRotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

        private void ApplyZoom()
        {
            if (ImageView.Source == null) return;
            if (_zoom <= 0)
            {
                ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ImageView.Stretch = Stretch.Uniform;
                // Before it's turned, the box is the window's the other way round when sideways.
                ImageView.Width = Math.Max(1, Sideways ? ImageScroll.ActualHeight : ImageScroll.ActualWidth);
                ImageView.Height = Math.Max(1, Sideways ? ImageScroll.ActualWidth : ImageScroll.ActualHeight);
            }
            else
            {
                ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ImageView.Stretch = Stretch.Fill;
                ImageView.Width = _pixelWidth * _zoom;
                ImageView.Height = _pixelHeight * _zoom;
            }
            UpdateUi();
        }

        private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_zoom <= 0) ApplyZoom();
        }

        private void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ImageView.Source == null) return;
            double current = _zoom > 0 ? _zoom : FitScale;
            _zoom = Math.Clamp(current * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.02, 20);
            ApplyZoom();
            e.Handled = true;
        }

        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            _zoom = 0;
            ApplyZoom();
        }

        private void ImageView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _zoom = _zoom > 0 ? 0 : 1;   // double-click: fit <-> 100%
                ApplyZoom();
                return;
            }
            if (_zoom <= 0) return;
            _panning = true;
            _panStart = e.GetPosition(ImageScroll);
            _panStartH = ImageScroll.HorizontalOffset;
            _panStartV = ImageScroll.VerticalOffset;
            ImageView.CaptureMouse();
            ImageView.Cursor = System.Windows.Input.Cursors.SizeAll;
        }

        private void ImageView_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_panning) return;
            var now = e.GetPosition(ImageScroll);
            ImageScroll.ScrollToHorizontalOffset(_panStartH - (now.X - _panStart.X));
            ImageScroll.ScrollToVerticalOffset(_panStartV - (now.Y - _panStart.Y));
        }

        private void ImageView_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_panning) return;
            _panning = false;
            ImageView.ReleaseMouseCapture();
            ImageView.Cursor = null;
        }

        // =================================================================
        //  Keyboard
        // =================================================================

        private bool IsInsideGrid(object? source)
        {
            for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, DataGridQueue)) return true;
            return false;
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
            // A focused button, tick box, slider or drop-down keeps its own Space and arrow keys (Space on "Repeat" must tick it, not pause the video).
            if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase && e.Key == Key.Space) return;
            if (e.OriginalSource is Slider or System.Windows.Controls.ComboBox or ComboBoxItem && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) return;
            bool inGrid = IsInsideGrid(e.OriginalSource);
            if (inGrid && e.Key is Key.Up or Key.Down or Key.Enter) return;   // the list keeps its own arrows and Enter

            bool media = Current is { Kind: not ViewerMediaKind.Image };
            switch (e.Key)
            {
                case Key.Space:
                    TogglePlay();
                    break;
                case Key.PageDown:
                    GoNext();
                    break;
                case Key.PageUp:
                    GoPrevious();
                    break;
                case Key.Right:
                    if (media && _mediaOpen) Player.Position += TimeSpan.FromSeconds(5);
                    else GoNext();
                    break;
                case Key.Left:
                    if (media && _mediaOpen) Player.Position = Player.Position > TimeSpan.FromSeconds(5) ? Player.Position - TimeSpan.FromSeconds(5) : TimeSpan.Zero;
                    else GoPrevious();
                    break;
                case Key.Up:
                    VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05);
                    break;
                case Key.Down:
                    VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05);
                    break;
                case Key.M:
                    BtnMute_Click(this, new RoutedEventArgs());
                    break;
                case Key.Delete when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    _ = DeleteFilesAsync(inGrid ? Targets() : Current != null ? new List<MediaItem> { Current } : new List<MediaItem>());
                    break;
                case Key.Delete:
                    if (inGrid && DataGridQueue.SelectedItems.Count > 0) RemoveItems(DataGridQueue.SelectedItems.OfType<MediaItem>().ToList());
                    else if (Current != null) RemoveItems(new[] { Current });
                    break;
                case Key.N:
                    GoNext();
                    break;
                case Key.P:
                    GoPrevious();
                    break;
                case Key.T:
                    ToggleTheater();
                    break;
                case Key.Escape when _widget != null:
                    HideWidget(openTool: false);
                    break;
                case Key.Escape when _theater:
                    ToggleTheater();
                    break;
                case Key.W:
                    ToggleWidget();
                    break;
                case Key.S:
                    StepSpeed(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
                    break;
                case Key.R:
                    Rotate(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -90 : 90);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        // =================================================================
        //  Buttons, state, leaving
        // =================================================================

        private void UpdateUi()
        {
            if (!_ready) return;
            var item = Current;
            bool has = item != null;
            bool image = item?.Kind == ViewerMediaKind.Image;
            bool media = has && !image;

            bool running = image ? _slideshow : _playing;
            PlayIcon.Kind = running ? PackIconKind.Pause : PackIconKind.Play;
            BtnPrev.IsEnabled = BtnNext.IsEnabled = _items.Count > 1;
            BtnPlay.IsEnabled = has;

            // In the mini window only the essentials stay (seek, previous / play / next, mute, speed, back to the tool);
            // the rest is too wide for a small window - their keys still work.
            bool mini = StageInWidget;
            SeekRow.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            BtnMute.Visibility = media && !mini ? Visibility.Visible : Visibility.Collapsed;   // in the mini window the speaker + slider in its top bar take over
            VolumeSlider.Visibility = media && !mini ? Visibility.Visible : Visibility.Collapsed;
            BtnSpeed.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            BtnSkipBack.Visibility = BtnSkipForward.Visibility = media && !mini ? Visibility.Visible : Visibility.Collapsed;
            BtnRotateLeft.Visibility = BtnRotateRight.Visibility = image && !mini ? Visibility.Visible : Visibility.Collapsed;
            BtnFit.Visibility = image && _zoom > 0 && !mini ? Visibility.Visible : Visibility.Collapsed;
            BtnTheater.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;

            // In the mini window the picture or video fills the whole stage and the controls float over its bottom edge,
            // so they can fade away without the picture jumping.
            Grid.SetRowSpan(StageContent, mini ? 2 : 1);
            ChromeScrim.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;

            // The same button sends the player out and brings it back.
            WidgetIcon.Kind = mini ? PackIconKind.WindowRestore : PackIconKind.PictureInPictureBottomRight;
            BtnWidget.ToolTip = S(mini ? "Str_MediaViewer_Widget_Back" : "Str_MediaViewer_Widget_Tooltip", "Floating mini player (W)");
            _widget?.SetTitle(item?.Name ?? "");

            MuteIcon.Kind = Player.IsMuted || VolumeSlider.Value <= 0 ? PackIconKind.VolumeOff : PackIconKind.VolumeHigh;
            _widget?.SetVolume(VolumeSlider.Value, Player.IsMuted);   // the mini player's own slider follows

            bool any = _items.Count > 0;
            EmptyStatePanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            DataGridQueue.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            BtnSelectAll.IsEnabled = BtnRemoveSelected.IsEnabled = BtnClearQueue.IsEnabled = BtnDeleteFiles.IsEnabled = any;
            BtnMoveUp.IsEnabled = BtnMoveDown.IsEnabled = BtnSortByName.IsEnabled = BtnSortByDate.IsEnabled = _items.Count > 1;
            SummaryPill.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TxtCount.Text = _items.Count.ToString(CultureInfo.CurrentCulture);
            PillPosition.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            TxtPositionPill.Text = has ? $"{_index + 1} / {_items.Count}" : "";
        }

        private void Root_Loaded(object sender, RoutedEventArgs e) => UpdateUi();   // back on the page: show what happened meanwhile

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            // Leaving the page: with the player out in the mini window it carries on there (it has its own
            // controls). Otherwise there'd be no way to stop it from another tool, so it pauses.
            if (_widget != null) return;
            StopSlideshowTimer();
            _slideshow = false;
            if (_playing)
            {
                Player.Pause();
                _playing = false;
            }
            UpdateUi();
            ScheduleSave();
        }

        /// <summary>Called when the tool is closed (or the app exits): releases the file, stops the timers, saves, closes the widget.</summary>
        public void CancelActiveRun()
        {
            _loadVersion++;
            StopSlideshowTimer();
            CancelSkip();
            _slideshow = false;
            StopMedia();
            MediaViewerManager.ClearPreviews();
            _saveTimer.Stop();
            SaveSettingsNow();
            CloseWidgetForExit();
        }
    }
}
