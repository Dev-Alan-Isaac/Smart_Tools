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
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Video Replay view - UI only. The play order is <see cref="PlayOrder{T}"/>;
    /// file lists, durations, preview copies and settings are in
    /// <see cref="VideoReplayManager"/>. Playback goes through
    /// <see cref="IReplayBackend"/>: VLC from Engines\VLC when it's there
    /// (plays WebM and everything else directly), otherwise Windows'
    /// MediaElement with preview copies (VideoReplayView.Players.cs).
    /// The current clip is tracked by object, never by row number.
    /// </summary>
    public partial class VideoReplayView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double PlaylistExpandedWidth = 400;
        private const string DialogTitle = "Video Replay";

        private readonly ObservableCollection<PlaylistItem> _queue = new();
        private readonly ObservableCollection<ReplayQueueGroup> _queueGroups = new();
        private readonly List<ReplaySource> _sources = new();
        private readonly PlayOrder<PlaylistItem> _order = new();
        private readonly DispatcherTimer _positionTimer;
        private ReplaySettings _settings = new();

        private PlaylistItem? _current;
        private bool _isPlaying;
        private bool _isUserSeeking;
        private bool _updatingSlider;
        private int _consecutiveFailures;
        private bool _keepNotice;                 // a skip reason is showing - the next clip's load must not wipe it
        private bool _retriedEarlyEnd;            // the current clip already got its one retry after ending too early
        private TimeSpan _lastPosition;          // where the clip was at the last timer tick (VLC resets it when a clip ends)
        private TimeSpan? _lastDuration;
        private double _speed = 1.0;
        private bool _muted;

        // Playback engines
        private readonly MediaElementBackend _windowsBackend;
        private VlcBackend? _vlcBackend;
        private Task? _vlcStart;   // VLC's first start (plugin scan) - clips wait for it
        private IReplayBackend _backend;
        private string? _currentSource;

        /// <summary>VLC's window is destroyed when you leave the page - where to pick up again on return.</summary>
        private (PlaylistItem Item, TimeSpan Position, bool WasPlaying)? _resume;
        private bool _pauseWhenOpened;

        private CancellationTokenSource? _loadCts;      // preview copy for the current clip
        private CancellationTokenSource? _expandCts;    // reading folders
        private CancellationTokenSource? _probeCts;     // background durations

        private bool _optionsCollapsed;
        private bool _playlistCollapsed;
        private bool _theater;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        public VideoReplayView()
        {
            InitializeComponent();
            DataGridQueue.ItemsSource = _queue;
            InputCard.ItemsSource = _queueGroups;
            InputCard.FileFilter = VideoReplayManager.FileDialogFilter;
            _positionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += PositionTimer_Tick;
            _windowsBackend = new MediaElementBackend(Player);
            _backend = _windowsBackend;
            HookBackend(_windowsBackend);
            _viewConstructed = true;
        }

        /// <summary>
        /// Called from MainWindow.Window_Closing: stops playback, conversions
        /// and probes, shuts VLC down and deletes preview copies. VLC gets a
        /// few seconds to let go; if it's stuck, the app still exits (it used
        /// to keep running in the background after closing mid-clip).
        /// </summary>
        public void CancelActiveRun()
        {
            _loadCts?.Cancel();
            _expandCts?.Cancel();
            _probeCts?.Cancel();
            StopPlayback();
            _positionTimer.Stop();
            ShutDownVlc();
            VideoReplayManager.SweepPreviews();
        }

        private bool _vlcShutDown;

        private void ShutDownVlc()
        {
            if (_vlcShutDown) return;
            _vlcShutDown = true;
            bool clean = true;
            if (_vlcBackend is { } vlc)
            {
                _vlcBackend = null;
                _backend = _windowsBackend;   // nothing may call into VLC after this
                clean = vlc.Close(TimeSpan.FromSeconds(3));
            }
            clean &= VlcPlayer.Shutdown(TimeSpan.FromSeconds(2));
            VlcShutdown.Finish(clean);   // a stuck VLC can't hold the app open
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private ReplayMode Mode =>
            ModeTiles.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out ReplayMode m) ? m : ReplayMode.Sequential;

        private bool Repeat => ChkRepeatPlaylist.IsChecked == true;

        // =================================================================
        //  Lifetime, layout, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(VideoReplayManager.LoadSettings());
                _ = Task.Run(() => VideoReplayManager.SweepPreviews());   // leftovers from a crash
                _vlcStart = StartVlcAsync();
            }
            else if (_resume is { } r)
            {
                // Back on the page with VLC: reload where you were.
                _resume = null;
                if (_queue.Contains(r.Item))
                {
                    _pauseWhenOpened = !r.WasPlaying;
                    // StopPlayback (on leaving the page) forgot the place in the play order - restore it, or Next starts over at clip 1.
                    if (_order.Count != _queue.Count) _order.Rebuild(_queue, Mode, r.Item);
                    else _order.JumpTo(r.Item);
                    _ = LoadAndPlayAsync(r.Item, startAt: r.Position);
                }
            }
            UpdateModeUi();
            UpdateQueueUi();
            UpdateNowPlaying();
            Focus();
        }

        /// <summary>
        /// Pauses when you leave the page - the old view kept playing (sound
        /// and all) on other pages, and deleted preview copies still in use.
        /// </summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_backend is VlcBackend) RememberAndStopVlc();
            else if (_isPlaying) Pause();
        }

        /// <summary>
        /// VLC draws into a native window that WPF destroys with the page -
        /// stop before that happens, and pick up at the same spot on return.
        /// </summary>
        private void RememberAndStopVlc()
        {
            if (_backend is not VlcBackend || _current == null || !_backend.HasMedia) return;
            _resume = (_current, _backend.Position, _isPlaying);
            StopPlayback();
        }

        // =================================================================
        //  Playback engine
        // =================================================================

        /// <summary>
        /// Starts VLC from Engines\VLC in the background (the first start
        /// scans its plugins). Until it's ready - or if it isn't there -
        /// Windows' player is used. Never throws.
        /// </summary>
        private async Task StartVlcAsync()
        {
            string? error;
            try
            {
                error = await Task.Run(VlcPlayer.EnsureLoaded);
            }
            catch (Exception ex)
            {
                error = "VLC couldn't start: " + ex.Message;
            }
            if (_vlcShutDown) return;   // the app closed while VLC was still starting
            if (error == null)
            {
                try
                {
                    var host = new VlcVideoHost();
                    // Stop VLC and let it finish before its window goes (leaving the page or closing).
                    host.Destroying += () =>
                    {
                        RememberAndStopVlc();
                        _vlcBackend?.WaitForIdle(TimeSpan.FromSeconds(3));
                    };
                    host.DoubleClicked += ToggleTheater;
                    host.Clicked += () => Focus();
                    host.RightClicked += () =>
                    {
                        if (Stage.ContextMenu is { } menu)
                        {
                            menu.PlacementTarget = Stage;
                            menu.IsOpen = true;
                        }
                    };
                    VlcSlot.Content = host;
                    var vlc = new VlcBackend(host);
                    HookBackend(vlc);
                    _vlcBackend = vlc;
                    if (_current == null)
                    {
                        _backend = vlc;
                        ApplyAudioToBackend();
                    }
                }
                catch (Exception ex)
                {
                    error = "VLC couldn't start: " + ex.Message;
                    VlcSlot.Content = null;
                    _vlcBackend = null;
                }
            }

            TxtEngine.Text = _vlcBackend != null ? "VLC" : "Windows player";
            PillEngine.ToolTip = _vlcBackend != null
                ? $"Playing with VLC {VlcPlayer.Version} from {VlcPlayer.VlcFolder} - WebM, MKV, FLV and more play directly."
                : $"{error} Until VLC works, WebM and FLV get a preview copy first. Details: {VlcPlayer.LogPath}";
            PillEngine.Visibility = Visibility.Visible;

            // Say why out loud when VLC was clearly meant to be there (some of
            // its files exist, or they're in the project but not the build output).
            bool vlcIntended = Directory.Exists(VlcPlayer.VlcFolder) || VlcPlayer.FindProjectCopy() != null;
            if (error != null && vlcIntended) ShowNotice(error, error: true);
        }

        private void HookBackend(IReplayBackend b)
        {
            b.Opened += () => { if (ReferenceEquals(b, _backend)) Backend_Opened(); };
            b.Ended += () => { if (ReferenceEquals(b, _backend)) Backend_Ended(); };
            b.Failed += m => { if (ReferenceEquals(b, _backend)) Backend_Failed(m); };
        }

        /// <summary>Picks VLC when it's running, else Windows' player; hides the other one's picture.</summary>
        private void UseBestBackend()
        {
            var wanted = (IReplayBackend?)_vlcBackend ?? _windowsBackend;
            if (!ReferenceEquals(wanted, _backend))
            {
                _backend.Stop();
                _backend = wanted;
                ApplyAudioToBackend();
            }
        }

        private void ShowSurface(bool visible)
        {
            bool vlc = _backend is VlcBackend;
            VlcSlot.Visibility = visible && vlc ? Visibility.Visible : Visibility.Collapsed;
            Player.Visibility = visible && !vlc ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyAudioToBackend()
        {
            _backend.SetVolume(SliderVolume.Value);
            _backend.SetMute(_muted);
            _backend.SetSpeed(_speed);
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
            ColTime.Visibility = e.NewSize.Width < 1000 ? Visibility.Collapsed : Visibility.Visible;

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

        /// <summary>Theater hides both side panels; toggling again brings back what was open.</summary>
        private void ToggleTheater()
        {
            _theater = !_theater;
            ApplyPanels();
            UiMotion.Pop(Stage, 0.99);
        }

        private void ApplyPanels()
        {
            bool optionsHidden = _theater;
            OptionsSidebar.Visibility = optionsHidden ? Visibility.Collapsed : Visibility.Visible;
            OptionsColumn.Width = new GridLength(optionsHidden ? 0 : _optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            OptionsGapColumn.Width = new GridLength(optionsHidden ? 0 : 14);
            var v = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = v;
            SepHeader.Visibility = v;
            OptionsScrollViewer.Visibility = v;

            bool playlistHidden = _theater || _playlistCollapsed;
            PlaylistColumn.Width = new GridLength(playlistHidden ? 0 : PlaylistExpandedWidth);
            PlaylistPanel.Visibility = playlistHidden ? Visibility.Collapsed : Visibility.Visible;
            // Flip the icon itself - the old code replaced button Content with text and lost the icon.
            IconTogglePlaylist.Kind = playlistHidden ? PackIconKind.ChevronLeft : PackIconKind.ChevronRight;
            IconTheater.Kind = _theater ? PackIconKind.ArrowCollapse : PackIconKind.ArrowExpand;
        }

        private void ApplySettings(ReplaySettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                foreach (var r in ModeTiles.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == s.Mode.ToString();
                ChkRepeatPlaylist.IsChecked = s.RepeatPlaylist;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                SliderVolume.Value = s.Volume;
                _muted = s.Muted;
                ApplyAudioToBackend();
                SetSpeed(s.Speed);
                _playlistCollapsed = s.PlaylistCollapsed;
                ApplyPanels();
                UpdateMuteIcon();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            _settings.Mode = Mode;
            _settings.RepeatPlaylist = Repeat;
            _settings.IncludeSubfolders = InputCard.IncludeSubfolders;
            _settings.Volume = SliderVolume.Value;
            _settings.Muted = _muted;
            _settings.Speed = _speed;
            _settings.PlaylistCollapsed = _playlistCollapsed;
            try
            {
                VideoReplayManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Replay settings not saved: {ex.Message}");
            }
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateModeUi();
            UpdateNowPlaying();
            SaveSettings();
        }

        // =================================================================
        //  Mode
        // =================================================================

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            // Re-plan around the current clip (shuffle keeps it and shuffles the rest).
            _order.Rebuild(_queue, Mode, _current);
            UpdateModeUi();
            UpdateNowPlaying();
            SaveSettings();
        }

        private void UpdateModeUi()
        {
            TxtModeHint.Text = VideoReplayManager.ModeHint(Mode, Repeat);
            ChkRepeatPlaylist.IsEnabled = Mode != ReplayMode.Loop;
            IconModePill.Kind = Mode switch
            {
                ReplayMode.Shuffle => PackIconKind.ShuffleVariant,
                ReplayMode.Loop => PackIconKind.RepeatOnce,
                _ => PackIconKind.PlaylistPlay,
            };
        }

        // =================================================================
        //  Adding clips
        // =================================================================

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) InputCard.SetDropHighlight(true);
            e.Handled = true;
        }

        private void Root_DragLeave(object sender, DragEventArgs e) =>
            InputCard.SetDropHighlight(false);

        private void Root_Drop(object sender, DragEventArgs e)
        {
            InputCard.SetDropHighlight(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddSourcesAsync(paths);
            e.Handled = true;
        }

        /// <summary>Lists the new files/folders off the UI thread, then appends them (nothing already queued is touched).</summary>
        private async Task AddSourcesAsync(IEnumerable<string> paths)
        {
            var fresh = new List<ReplaySource>();
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                fresh.Add(new ReplaySource { FullPath = path, IsFolder = isFolder });
            }
            if (fresh.Count == 0) return;

            _expandCts?.Cancel();
            var cts = _expandCts = new CancellationTokenSource();
            bool subfolders = InputCard.IncludeSubfolders;
            TxtUpNext.Text = "Reading folders...";
            try
            {
                var result = await VideoReplayManager.ExpandAsync(fresh, subfolders, cts.Token);
                var known = new HashSet<string>(_queue.Select(q => q.FullPath), StringComparer.OrdinalIgnoreCase);
                var added = result.Files.Where(f => known.Add(f.Path)).Select(f => new PlaylistItem { FullPath = f.Path, SizeBytes = f.Size }).ToList();

                _sources.AddRange(fresh);
                foreach (var item in added) _queue.Add(item);
                _order.Add(added, Mode);
                Renumber();
                RebuildQueueGroups();
                UpdateQueueUi();
                InputCard.PopDropArea();

                var notes = new List<string>();
                if (added.Count == 0) notes.Add("No new videos found");
                if (result.Inaccessible > 0) notes.Add($"{result.Inaccessible} folder(s) couldn't be read");
                if (result.Links > 0) notes.Add($"{result.Links} linked folder(s) not followed");
                if (notes.Count > 0) ShowNotice(string.Join(" · ", notes), error: added.Count == 0);

                _ = ProbeDurationsAsync(added);
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
            finally
            {
                if (ReferenceEquals(_expandCts, cts)) _expandCts = null;
                UpdateNowPlaying();
            }
        }

        /// <summary>Fills in every clip's length (and the playlist total) in the background.</summary>
        private async Task ProbeDurationsAsync(IReadOnlyList<PlaylistItem> items)
        {
            if (items.Count == 0 || VideoReplayManager.FfprobeExe == null) return;
            _probeCts ??= new CancellationTokenSource();
            var token = _probeCts.Token;
            var tasks = items.Select(async item =>
            {
                try
                {
                    var d = await VideoReplayManager.ProbeDurationAsync(item.FullPath, token);
                    if (d != null && item.Duration == null) item.Duration = d;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Info($"Probe failed for {item.Name}: {ex.Message}");
                }
            });
            await Task.WhenAll(tasks);
            UpdateQueueUi();
        }

        private void Renumber()
        {
            for (int i = 0; i < _queue.Count; i++) _queue[i].Number = i + 1;
        }

        private void RebuildQueueGroups()
        {
            // Sources with nothing left in the playlist drop out.
            _sources.RemoveAll(s => s.IsFolder
                ? !_queue.Any(q => q.FullPath.StartsWith(s.FullPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                : !_queue.Any(q => string.Equals(q.FullPath, s.FullPath, StringComparison.OrdinalIgnoreCase)));
            _queueGroups.Clear();
            foreach (var g in VideoReplayManager.BuildQueueGroups(_sources, _queue, InputCard.IncludeSubfolders))
                _queueGroups.Add(g);
            InputCard.SetEmpty(_queueGroups.Count == 0);
        }

        private void UpdateQueueUi()
        {
            TxtSummaryCount.Text = VideoReplayManager.DescribePlaylist(_queue);
            bool any = _queue.Count > 0;
            if (any && SummaryPill.Visibility != Visibility.Visible) UiMotion.Pop(SummaryPill, 0.9);
            SummaryPill.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            UiMotion.FadeVisibility(EmptyStatePanel, !any);
            UiMotion.FadeVisibility(DataGridQueue, any);
            UpdateCheckButtons();
        }

        private void UpdateCheckButtons()
        {
            int ticked = _queue.Count(i => i.IsSelected);
            BtnSelectAll.IsEnabled = _queue.Count > 0 && ticked < _queue.Count;
            BtnDeselectAll.IsEnabled = ticked > 0;
            BtnRemoveSelected.IsEnabled = ticked > 0 || DataGridQueue.SelectedItems.Count > 0;
            BtnMoveUp.IsEnabled = DataGridQueue.SelectedItems.Count > 0;
            BtnMoveDown.IsEnabled = DataGridQueue.SelectedItems.Count > 0;
            BtnSortByName.IsEnabled = _queue.Count > 1;
            BtnSortByDate.IsEnabled = _queue.Count > 1;
        }

        // =================================================================
        //  Removing clips
        // =================================================================

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ReplayQueueGroup group }) return;
            string root = group.FolderPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var items = group.IsFolder
                ? _queue.Where(q => q.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList()
                : _queue.Where(q => group.FilePaths.Contains(q.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
            _sources.RemoveAll(s => string.Equals(s.FullPath, group.FolderPath, StringComparison.OrdinalIgnoreCase)
                                    || group.FilePaths.Contains(s.FullPath, StringComparer.OrdinalIgnoreCase));
            RemoveItems(items);
        }

        private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: PlaylistItem item }) RemoveItems(new[] { item });
        }

        /// <summary>Ticked rows, or the highlighted rows when nothing is ticked.</summary>
        private List<PlaylistItem> TargetItems()
        {
            var ticked = _queue.Where(i => i.IsSelected).ToList();
            return ticked.Count > 0 ? ticked : DataGridQueue.SelectedItems.Cast<PlaylistItem>().ToList();
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveItems(TargetItems());

        private void RemoveItems(IReadOnlyCollection<PlaylistItem> items)
        {
            if (items.Count == 0) return;
            if (_current != null && items.Contains(_current)) StopPlayback();
            foreach (var item in items)
            {
                _order.Remove(item);
                _queue.Remove(item);
            }
            Renumber();
            RebuildQueueGroups();
            UpdateQueueUi();
            UpdateNowPlaying();
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            _ = AddSourcesAsync(e.Paths);
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => Option_Changed(sender ?? this, new RoutedEventArgs());

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e) => InputCard_ClearRequested(sender, e);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            _expandCts?.Cancel();
            StopPlayback();
            _queue.Clear();
            _sources.Clear();
            _order.Clear();
            RebuildQueueGroups();
            UpdateQueueUi();
            UpdateNowPlaying();
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

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e) => UpdateCheckButtons();

        // =================================================================
        //  Order: move up/down, sort by name or date
        // =================================================================

        private void DataGridQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BtnSortByDate != null) UpdateCheckButtons();   // can fire during InitializeComponent
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            var chosen = DataGridQueue.SelectedItems.OfType<PlaylistItem>().ToList();
            if (!VideoReplayManager.Move(_queue, chosen, direction)) return;
            AfterReorder(chosen);
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_queue.Count < 2) return;
            var chosen = DataGridQueue.SelectedItems.OfType<PlaylistItem>().ToList();
            var sorted = VideoReplayManager.Sorted(_queue, byDate);
            _queue.Clear();
            foreach (var item in sorted) _queue.Add(item);
            VideoReplayManager.Renumber(_queue);
            AfterReorder(chosen);
        }

        /// <summary>Re-applies the highlight (replacing a row drops it) and re-syncs the play order with the new list order.</summary>
        private void AfterReorder(IReadOnlyList<PlaylistItem> chosen)
        {
            DataGridQueue.SelectedItems.Clear();
            foreach (var item in chosen) DataGridQueue.SelectedItems.Add(item);
            var first = chosen.OrderBy(c => c.Number).FirstOrDefault();
            if (first != null)
            {
                DataGridQueue.ScrollIntoView(first);
                DataGridQueue.CurrentItem = first;
            }
            _order.Rebuild(_queue, Mode, _current);
            RebuildQueueGroups();
            UpdateQueueUi();
            UpdateNowPlaying();
        }

        private void DataGridQueue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is CheckBox) return;
            if (e.Key == Key.Delete)
            {
                RemoveItems(DataGridQueue.SelectedItems.Cast<PlaylistItem>().ToList());
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && DataGridQueue.SelectedItem is PlaylistItem item)
            {
                PlayItem(item);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey is Key.Up or Key.Down))
            {
                MoveHighlighted(e.SystemKey == Key.Up ? -1 : +1);
                e.Handled = true;
            }
        }

        // =================================================================
        //  Context menus
        // =================================================================

        /// <summary>The row the menu is for: the highlighted row in the playlist, else the clip playing.</summary>
        private PlaylistItem? MenuTarget(object sender)
        {
            bool fromGrid = sender is MenuItem { Parent: ContextMenu { PlacementTarget: DataGrid } };
            return fromGrid ? DataGridQueue.SelectedItem as PlaylistItem ?? _current : _current;
        }

        private void MenuPlay_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget(sender) is PlaylistItem item) PlayItem(item);
        }

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget(sender) is not PlaylistItem item) return;
            // The app's shared helper (replaces this view's own Explorer COM and window-focus code).
            if (ShellExplorer.Show(item.FullPath) == ShellExplorer.ShowResult.NotFound)
                ShowNotice($"\"{item.Name}\" is no longer there.", error: true);
        }

        private void MenuDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget(sender) is PlaylistItem item) DeleteWithConfirm(item);
        }

        /// <summary>Asks, then moves the clip to the Recycle Bin (permanent only on drives without one) and drops it from the playlist.</summary>
        private void DeleteWithConfirm(PlaylistItem item)
        {
            bool wasCurrent = ReferenceEquals(item, _current);
            bool bin = VideoReplayManager.HasRecycleBin(item.FullPath);
            string text = bin
                ? $"Move \"{item.Name}\" to the Recycle Bin?\n\nThis deletes the file from your computer, not just from the playlist."
                : $"Delete \"{item.Name}\" PERMANENTLY?\n\nIt's on a drive without a Recycle Bin, so it can't be undone.";
            if (MessageBox.Show(text, DialogTitle, MessageBoxButton.YesNo, bin ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            // Work out where to carry on before StopPlayback loses the position: the clip before the deleted one
            // (or the one after it when the deleted clip was first).
            PlaylistItem? resume = null;
            if (wasCurrent)
            {
                if (_order.Count != _queue.Count) _order.Rebuild(_queue, Mode, _current);
                var order = _order.Order;
                int idx = -1;
                for (int k = 0; k < order.Count; k++)
                    if (ReferenceEquals(order[k], item)) { idx = k; break; }
                if (idx > 0) resume = order[idx - 1];
                else if (idx == 0 && order.Count > 1) resume = order[1];
            }

            // MediaElement locks the file it plays - release it first.
            if (ReferenceEquals(item, _current)) StopPlayback();
            try
            {
                VideoReplayManager.DeleteClip(item.FullPath);
            }
            catch (Exception ex)
            {
                ShowNotice($"Couldn't delete \"{item.Name}\": {ex.Message}", error: true);
                return;
            }
            RemoveItems(new[] { item });
            if (wasCurrent && resume != null && _queue.Contains(resume))
            {
                _order.JumpTo(resume);   // keep watching from the clip before the one just binned
                _ = LoadAndPlayAsync(resume);
            }
            else if (wasCurrent && _queue.Count > 0) PlayNext(userAsked: false);
        }

        // =================================================================
        //  Playback
        // =================================================================

        private void DataGridQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null) return;   // the remove button / checkbox
            if (DataGridQueue.SelectedItem is PlaylistItem item) PlayItem(item);
        }

        private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private void PlayItem(PlaylistItem item)
        {
            if (_order.Count != _queue.Count) _order.Rebuild(_queue, Mode, item);
            else _order.JumpTo(item);
            _consecutiveFailures = 0;
            _ = LoadAndPlayAsync(item);
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (_current == null)
            {
                if (_queue.Count == 0)
                {
                    InputCard.PopDropArea();
                    return;
                }
                if (_order.Count != _queue.Count) _order.Rebuild(_queue, Mode);
                var first = DataGridQueue.SelectedItem as PlaylistItem ?? _order.Next(Mode, Repeat) ?? _order.Order.FirstOrDefault();
                if (first != null)
                {
                    _order.JumpTo(first);
                    _consecutiveFailures = 0;
                    _ = LoadAndPlayAsync(first);
                }
                return;
            }
            if (_isPlaying) Pause();
            else Resume();
        }

        private void Pause()
        {
            _backend.Pause();
            _isPlaying = false;
            _positionTimer.Stop();
            IconPlayPause.Kind = PackIconKind.Play;   // the old code replaced the icon with a "▶" text character
        }

        private void Resume()
        {
            if (!_backend.HasMedia) return;
            _backend.Play();
            _isPlaying = true;
            _positionTimer.Start();
            IconPlayPause.Kind = PackIconKind.Pause;
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayNext(userAsked: true);

        private void BtnPrevious_Click(object sender, RoutedEventArgs e) => PlayPrevious();

        /// <summary>Next clip (for Next, a finished clip, or a failed one). Loop only repeats on its own, not when you press Next.</summary>
        private void PlayNext(bool userAsked)
        {
            if (_queue.Count == 0) return;
            if (_order.Count != _queue.Count) _order.Rebuild(_queue, Mode, _current);
            var mode = userAsked && Mode == ReplayMode.Loop ? ReplayMode.Sequential : Mode;
            var next = _order.Next(mode, Repeat || userAsked && Mode == ReplayMode.Loop);
            if (next == null)
            {
                // End of the playlist: stop (the old one wrapped around forever), ready to start again.
                StopPlayback();
                TxtUpNext.Text = "End of the playlist - press Play to start again, or tick \"Repeat the playlist\".";
                return;
            }
            _ = LoadAndPlayAsync(next);
        }

        /// <summary>More than 3 s in: back to the start of this clip. Otherwise the previous clip.</summary>
        private void PlayPrevious()
        {
            if (_queue.Count == 0) return;
            if (_current != null && VideoReplayManager.PreviousRestarts(_backend.Position))
            {
                _backend.Position = TimeSpan.Zero;
                UpdatePosition();
                return;
            }
            if (_order.Count != _queue.Count) _order.Rebuild(_queue, Mode, _current);
            var mode = Mode == ReplayMode.Loop ? ReplayMode.Sequential : Mode;
            if (_order.Previous(mode, Repeat || Mode == ReplayMode.Loop) is PlaylistItem prev) _ = LoadAndPlayAsync(prev);
        }

        /// <summary>
        /// Loads a clip. Formats Windows can't play (WEBM, FLV) get a preview
        /// copy first, with progress; anything else that fails to open gets
        /// one try with a preview copy before it's marked failed.
        /// </summary>
        private async Task LoadAndPlayAsync(PlaylistItem item, bool forcePreview = false, TimeSpan? startAt = null)
        {
            _loadCts?.Cancel();
            var cts = _loadCts = new CancellationTokenSource();

            if (_current != null && !ReferenceEquals(_current, item) && _current.Status != ClipStatus.Failed) _current.Status = ClipStatus.Ready;
            if (!ReferenceEquals(_current, item)) _retriedEarlyEnd = false;
            _current = item;
            _lastPosition = TimeSpan.Zero;
            _lastDuration = null;
            if (_keepNotice) _keepNotice = false;   // the skip reason stays up until the next clip is skipped or you dismiss it
            else HideNotice();
            NoClipPanel.Visibility = Visibility.Collapsed;
            DataGridQueue.ScrollIntoView(item);
            UpdateNowPlaying();

            // VLC's first start reads its plugins (a few seconds). Wait for it
            // rather than making a preview copy that VLC wouldn't need.
            if (_vlcStart is { IsCompleted: false } starting)
            {
                _backend.Stop();
                ShowSurface(false);
                ProgressConvert.IsIndeterminate = true;
                TxtConvert.Text = "Starting VLC - the first start takes a few seconds while it reads its plugins.";
                UiMotion.FadeVisibility(ConvertingPanel, true);
                await starting;
                if (cts.IsCancellationRequested) return;   // another clip was chosen meanwhile
                ConvertingPanel.Visibility = Visibility.Collapsed;
            }

            UseBestBackend();
            string source = item.FullPath;
            // VLC plays everything directly; only Windows' player needs preview copies.
            bool needsCopy = !_backend.PlaysEverything &&
                             (forcePreview || VideoReplayManager.NeedsPreviewCopy.Contains(Path.GetExtension(item.FullPath)));
            // Without ffmpeg, try the original anyway - some PCs have the codec.
            if (needsCopy && VideoReplayManager.FfmpegExe != null)
            {
                _backend.Stop();
                ShowSurface(false);
                item.Status = ClipStatus.Converting;
                ProgressConvert.IsIndeterminate = true;
                TxtConvert.Text = $"{Path.GetExtension(item.FullPath).ToUpperInvariant().TrimStart('.')} can't be played by Windows directly.";
                UiMotion.FadeVisibility(ConvertingPanel, true);
                string encoderLabel = "graphics card";
                try
                {
                    source = await VideoReplayManager.CreatePreviewAsync(item.FullPath, p => Dispatcher.InvokeAsync(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        ProgressConvert.IsIndeterminate = false;
                        ProgressConvert.Value = p;
                        TxtConvert.Text = $"{p:0}% with the {encoderLabel} - VLC isn't in use, so WebM needs this step (hover the engine pill for why).";
                    }), cts.Token, onEncoder: enc => encoderLabel = VideoReplayManager.EncoderLabel(enc));
                }
                catch (OperationCanceledException)
                {
                    if (item.Status == ClipStatus.Converting) item.Status = ClipStatus.Ready;
                    return;   // another clip was chosen
                }
                catch (Exception ex)
                {
                    ConvertingPanel.Visibility = Visibility.Collapsed;
                    FailCurrent(item, ex.Message);
                    return;
                }
                ConvertingPanel.Visibility = Visibility.Collapsed;
                if (cts.IsCancellationRequested) return;
            }

            item.Status = ClipStatus.Playing;
            _currentSource = source;
            ShowSurface(true);
            try
            {
                _backend.Open(source);
                if (startAt is TimeSpan at && at > TimeSpan.Zero) _backend.Position = at;
            }
            catch (Exception ex)
            {
                FailCurrent(item, ex.Message);
                return;
            }
            _isPlaying = true;
            IconPlayPause.Kind = PackIconKind.Pause;
            _positionTimer.Start();
            UpdateNowPlaying();
        }

        private void StopPlayback()
        {
            _loadCts?.Cancel();
            _positionTimer.Stop();
            _backend.Stop();   // releases the file
            _currentSource = null;
            ShowSurface(false);
            _isPlaying = false;
            if (_current is { Status: ClipStatus.Playing or ClipStatus.Converting }) _current.Status = ClipStatus.Ready;
            _current = null;
            _order.ResetPosition();
            IconPlayPause.Kind = PackIconKind.Play;
            ConvertingPanel.Visibility = Visibility.Collapsed;
            NoClipPanel.Visibility = Visibility.Visible;
            _updatingSlider = true;
            SliderPosition.Value = 0;
            _updatingSlider = false;
            TxtCurrentTime.Text = "0:00";
            TxtTotalTime.Text = "0:00";
            UpdateNowPlaying();
        }

        private void Backend_Opened()
        {
            _consecutiveFailures = 0;
            if (_pauseWhenOpened)
            {
                _pauseWhenOpened = false;
                Pause();
            }
            _seekTarget = null;
            if (EffectiveDuration is not TimeSpan duration) return;
            SliderPosition.Maximum = Math.Max(1, duration.TotalSeconds);
            TxtTotalTime.Text = ReplayText.FormatTime(duration);
            if (_current != null && _current.Duration == null)
            {
                _current.Duration = duration;
                UpdateQueueUi();
            }
        }

        private void Backend_Ended()
        {
            if (Mode == ReplayMode.Loop)
            {
                _backend.Position = TimeSpan.Zero;
                _backend.Play();
                return;
            }

            // The engine says "finished" - but if it was nowhere near the end, the clip is being skipped, not watched.
            if (_current != null)
            {
                bool early = _lastDuration is TimeSpan d
                    ? _lastPosition < d - TimeSpan.FromSeconds(3)
                    : _lastPosition < TimeSpan.FromSeconds(10);
                if (early && !_retriedEarlyEnd)
                {
                    // One more try before skipping: a good clip shouldn't be lost to a glitch in the engine.
                    _retriedEarlyEnd = true;
                    VideoReplayManager.LogSkip(_current.FullPath, _backend.Name, $"Ended at {ReplayText.FormatTime(_lastPosition)} - trying it once more.");
                    _ = LoadAndPlayAsync(_current);
                    return;
                }
                if (early)
                {
                    string where = _lastDuration is TimeSpan len
                        ? $"{ReplayText.FormatTime(_lastPosition)} of {ReplayText.FormatTime(len)}"
                        : $"{ReplayText.FormatTime(_lastPosition)} (length unknown)";
                    string why = $"The {_backend.Name} player stopped at {where} and reported the end of the clip " +
                                 "(damaged or cut-off file, or a stream it can't decode past that point).";
                    if (_backend.PlaysEverything && VlcPlayer.ErrorMessage is { Length: > 0 } vlcSays) why += $" VLC said: {vlcSays}";
                    ReportSkip(_current, why);
                }
            }
            PlayNext(userAsked: false);
        }

        /// <summary>Logs why a clip was skipped and keeps a banner up through the next clip's load.</summary>
        private void ReportSkip(PlaylistItem item, string reason)
        {
            VideoReplayManager.LogSkip(item.FullPath, _backend.Name, reason);
            ShowNotice($"Skipped \"{item.Name}\": {reason}  (log: {VideoReplayManager.SkipLogPath})", error: true);
            _keepNotice = true;
        }

        private void Backend_Failed(string reason)
        {
            var item = _current;
            if (item == null) return;
            bool alreadyCopy = _currentSource != null && _currentSource.StartsWith(VideoReplayManager.PreviewFolder, StringComparison.OrdinalIgnoreCase);

            // Windows' player: one retry through a preview copy for any format it refuses (not just WEBM).
            if (!_backend.PlaysEverything && !alreadyCopy && VideoReplayManager.FfmpegExe != null)
            {
                VideoReplayManager.LogSkip(item.FullPath, _backend.Name, "Windows' player refused it (" + reason + ") - retrying through a preview copy.");
                _ = LoadAndPlayAsync(item, forcePreview: true);
                return;
            }
            FailCurrent(item, reason);
        }

        /// <summary>
        /// Marks a clip failed, says so in a banner (the old view showed a
        /// dialog per clip) and moves on - until every clip has failed in a row.
        /// </summary>
        private void FailCurrent(PlaylistItem item, string reason)
        {
            item.Status = ClipStatus.Failed;
            item.Message = reason.TrimEnd('.');
            _consecutiveFailures++;
            _positionTimer.Stop();
            _backend.Stop();
            _currentSource = null;
            ShowSurface(false);
            _isPlaying = false;
            IconPlayPause.Kind = PackIconKind.Play;
            string played = _lastPosition > TimeSpan.Zero ? $" after {ReplayText.FormatTime(_lastPosition)}" : " before it started";
            VideoReplayManager.LogSkip(item.FullPath, _backend.Name, $"Failed{played}: {item.Message}");
            ShowNotice($"Couldn't play \"{item.Name}\"{played}: {item.Message}.  (log: {VideoReplayManager.SkipLogPath})", error: true);
            _keepNotice = true;

            if (Mode != ReplayMode.Loop && _consecutiveFailures < _queue.Count)
            {
                PlayNext(userAsked: false);
                return;
            }
            _consecutiveFailures = 0;
            _keepNotice = false;
            _current = null;
            NoClipPanel.Visibility = Visibility.Visible;
            UpdateNowPlaying();
        }

        // ---------------- Seeking ----------------

        private void BtnSkipBack_Click(object sender, RoutedEventArgs e) => SeekBy(TimeSpan.FromSeconds(-10));
        private void BtnSkipForward_Click(object sender, RoutedEventArgs e) => SeekBy(TimeSpan.FromSeconds(10));

        private void SeekBy(TimeSpan delta)
        {
            if (!_backend.HasMedia) return;
            var target = VideoReplayManager.Seek(_backend.Position, delta, EffectiveDuration);
            BeginSeek(target);
            UpdatePosition();
        }

        // A seek can take a while (sparse keyframes / no index). Until the engine catches up the slider keeps showing
        // the target instead of snapping back to the old position on the next timer tick.
        private TimeSpan? _seekTarget;
        private DateTime _seekDeadline;

        /// <summary>Engine length, or the ffprobe length when the file doesn't store one (browser-recorded WebM).</summary>
        private TimeSpan? EffectiveDuration => _backend.Duration ?? _current?.Duration;

        private void BeginSeek(TimeSpan target)
        {
            _seekTarget = target;
            _seekDeadline = DateTime.UtcNow.AddSeconds(45);   // big files without an index can take a while; released as soon as the engine arrives
            _backend.Position = target;
        }

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            _backend.Poll();   // VLC reports opened / ended / failed this way
            UpdatePosition();
        }

        private void UpdatePosition()
        {
            if (_isUserSeeking || !_backend.HasMedia) return;
            var position = _backend.Position;
            if (_seekTarget is TimeSpan pending)
            {
                if (DateTime.UtcNow < _seekDeadline && Math.Abs((position - pending).TotalSeconds) > 2)
                    position = pending;   // engine still seeking - don't let the bar jump back
                else
                    _seekTarget = null;
            }
            if (position > TimeSpan.Zero) _lastPosition = position;
            var duration = EffectiveDuration;
            _lastDuration = duration;
            if (duration is TimeSpan d)
            {
                if (Math.Abs(SliderPosition.Maximum - d.TotalSeconds) > 0.5)
                {
                    SliderPosition.Maximum = Math.Max(1, d.TotalSeconds);
                    TxtTotalTime.Text = ReplayText.FormatTime(d);
                }
            }
            else
            {
                TxtTotalTime.Text = "--:--";   // no length anywhere (engine and ffprobe both unknown)
                TxtCurrentTime.Text = ReplayText.FormatTime(position);
                return;
            }
            _updatingSlider = true;
            SliderPosition.Value = position.TotalSeconds;
            _updatingSlider = false;
            TxtCurrentTime.Text = ReplayText.FormatTime(position);
        }

        private void SliderPosition_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isUserSeeking = true;

        private void SliderPosition_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
            // Without any known length the slider range is meaningless (max 1 s) - seeking would just restart the clip.
            if (_backend.HasMedia && EffectiveDuration != null) BeginSeek(TimeSpan.FromSeconds(SliderPosition.Value));
        }

        /// <summary>While dragging, the time follows the thumb (the old one stayed frozen until release).</summary>
        private void SliderPosition_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _updatingSlider) return;
            TxtCurrentTime.Text = ReplayText.FormatTime(TimeSpan.FromSeconds(e.NewValue));
            if (!_isUserSeeking && _backend.HasMedia && EffectiveDuration != null) BeginSeek(TimeSpan.FromSeconds(e.NewValue));
        }

        // ---------------- Volume and speed ----------------

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            _backend.SetVolume(e.NewValue);
            if (e.NewValue > 0 && _muted)
            {
                _muted = false;
                _backend.SetMute(false);
            }
            UpdateMuteIcon();
            if (!_applyingSettings) SaveSettings();
        }

        /// <summary>Mute keeps the volume level (the old one set the slider to 0 and swapped the icon for an emoji).</summary>
        private void BtnMute_Click(object sender, RoutedEventArgs e) => ToggleMute();

        private void ToggleMute()
        {
            _muted = !_muted;
            _backend.SetMute(_muted);
            UpdateMuteIcon();
            SaveSettings();
        }

        private void UpdateMuteIcon() =>
            IconMute.Kind = _muted || SliderVolume.Value <= 0 ? PackIconKind.VolumeOff
                : SliderVolume.Value < 0.5 ? PackIconKind.VolumeMedium : PackIconKind.VolumeHigh;

        private void BtnSpeed_Click(object sender, RoutedEventArgs e) => StepSpeed(+1);

        private void BtnSpeed_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            StepSpeed(-1);
            e.Handled = true;
        }

        private void StepSpeed(int direction)
        {
            var speeds = VideoReplayManager.Speeds;
            int i = speeds.ToList().IndexOf(VideoReplayManager.ClampSpeed(_speed));
            i = (i + direction + speeds.Count) % speeds.Count;
            SetSpeed(speeds[i]);
            UiMotion.Pop(BtnSpeed, 0.9);
            SaveSettings();
        }

        private void SetSpeed(double speed)
        {
            _speed = VideoReplayManager.ClampSpeed(speed);
            _backend.SetSpeed(_speed);
            BtnSpeed.Content = VideoReplayManager.SpeedLabel(_speed);
        }

        // =================================================================
        //  Now playing, notices, keyboard
        // =================================================================

        private void UpdateNowPlaying()
        {
            if (_current == null)
            {
                TxtNowPlaying.SetResourceReference(TextBlock.TextProperty, "Str_Common_NothingLoaded");
                TxtUpNext.Text = _queue.Count == 0
                    ? "Add clips on the left, then press Play."
                    : $"{_queue.Count:N0} clip{(_queue.Count == 1 ? "" : "s")} ready - press Play or double-click one.";
                PillPosition.Visibility = Visibility.Collapsed;
                return;
            }
            TxtNowPlaying.Text = _current.Name;
            TxtNowPlaying.ToolTip = _current.FullPath;
            var next = _order.PeekNext(Mode, Repeat);
            TxtUpNext.Text = Mode == ReplayMode.Loop ? "Repeating this clip"
                : next != null ? $"Up next: {next.Name}"
                : "Last clip in the playlist";
            TxtPosition.Text = $"{Math.Max(1, _order.CurrentNumber)} of {_order.Count}";
            PillPosition.Visibility = Visibility.Visible;
        }

        private void ShowNotice(string text, bool error)
        {
            TxtError.Text = text;
            ErrorBanner.SetResourceReference(Border.BackgroundProperty, error ? "StatusErrorBg" : "StatusInfoBg");
            TxtError.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "StatusInfoFg");
            UiMotion.FadeVisibility(ErrorBanner, true);
        }

        private void HideNotice() => ErrorBanner.Visibility = Visibility.Collapsed;

        private void BtnDismissError_Click(object sender, RoutedEventArgs e) => UiMotion.FadeVisibility(ErrorBanner, false);

        private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Double-click the picture (not the controls) for theater.
            if (e.ClickCount == 2 && e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) == null && FindParent<Slider>(d) == null)
            {
                ToggleTheater();
                e.Handled = true;
            }
            Focus();
        }

        /// <summary>Space, arrows, N/P, M, T. Ignored while typing or when a button already handles Space.</summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                // Ctrl shortcuts for the playlist / sidebar buttons (plain letters are for playback).
                var none = new RoutedEventArgs();
                switch (e.Key)
                {
                    case Key.O when shift: InputCard.BrowseFolders(); break;
                    case Key.O: InputCard.BrowseFiles(); break;
                    case Key.B: BtnCollapseOptions_Click(this, none); break;
                    case Key.D1: BtnSortByName_Click(this, none); break;
                    case Key.D2: BtnSortByDate_Click(this, none); break;
                    case Key.R: BtnRemoveSelected_Click(this, none); break;
                    case Key.A when shift: BtnSelectAll_Click(this, none); break;
                    case Key.D when shift: BtnDeselectAll_Click(this, none); break;
                    case Key.Delete when shift:
                        if (_queue.Count > 0 && MessageBox.Show("Remove every clip from the playlist?", DialogTitle,
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                            InputCard_ClearRequested(this, EventArgs.Empty);
                        break;
                    case Key.Up: MoveHighlighted(-1); break;
                    case Key.Down: MoveHighlighted(+1); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.S:
                    StepSpeed(shift ? -1 : +1);
                    break;
                case Key.L:
                    BtnTogglePlaylist_Click(this, new RoutedEventArgs());
                    break;
                case Key.Escape when ErrorBanner.Visibility == Visibility.Visible && !_theater:
                    UiMotion.FadeVisibility(ErrorBanner, false);
                    break;
                case Key.Space when e.OriginalSource is not ButtonBase:
                    TogglePlay();
                    break;
                case Key.Left when e.OriginalSource is not DataGridCell:
                    SeekBy(TimeSpan.FromSeconds(shift ? -30 : -10));
                    break;
                case Key.Right when e.OriginalSource is not DataGridCell:
                    SeekBy(TimeSpan.FromSeconds(shift ? 30 : 10));
                    break;
                case Key.N:
                    PlayNext(userAsked: true);
                    break;
                case Key.P:
                    PlayPrevious();
                    break;
                case Key.M:
                    ToggleMute();
                    break;
                case Key.T:
                    ToggleTheater();
                    break;
                case Key.Delete when _current != null && e.OriginalSource is not DataGridCell:
                    DeleteWithConfirm(_current);   // the playlist grid keeps Delete for "remove from playlist"
                    break;
                case Key.Escape when _theater:
                    ToggleTheater();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }
    }
}