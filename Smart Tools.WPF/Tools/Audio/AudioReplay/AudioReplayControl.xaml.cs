using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Views;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Audio Replay UI. Everything that isn't drawing or reading a control -
    /// the playlist, play order, NAudio playback, duration probing, FFT -
    /// lives in <see cref="AudioReplayManager"/>; data types live in
    /// AudioReplayModel.cs. This class only wires controls to the manager
    /// and renders what it reports.
    /// </summary>
    public partial class AudioReplayControl : UserControl, IHubPage
    {
        /// <summary>Hub tab strip slot (see <see cref="IHubPage"/>).</summary>
        public void SetHubTabs(UIElement? strip) => HubTabsHost.Content = strip;

        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private const double PlaylistWidth = 380;
        private const double NarrowLayoutWidth = 760;

        private const int LevelBarCount = 56;
        private const double MinBarHeight = 3;
        private const double PeakCapHeight = 2;
        private const double PeakFallPerTick = 1.6;  // px per 30ms tick
        private const double PeakHoldTicks = 12;     // ~360ms before a cap starts falling

        private static readonly double[] SpeedOptions = { 0.5, 1.0, 1.5, 2.0 };

        private readonly AudioReplayManager _audio = new();
        private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
        private readonly bool _viewConstructed;

        private bool _isDraggingSeek;
        private bool _optionsCollapsed;
        private bool _playlistCollapsed;

        // Spectrum drawing state (smoothing + peak caps are presentation, so they stay here)
        private readonly Rectangle[] _bars = new Rectangle[LevelBarCount];
        private readonly TranslateTransform[] _caps = new TranslateTransform[LevelBarCount];
        private readonly float[] _levels = new float[LevelBarCount];
        private readonly double[] _barHeights = new double[LevelBarCount];
        private readonly double[] _peakHeights = new double[LevelBarCount];
        private readonly int[] _peakHold = new int[LevelBarCount];
        private double _tilePulse;   // smoothed bass level driving the album tile pulse

        public AudioReplayControl()
        {
            InitializeComponent();
            _viewConstructed = true;

            DataGridQueue.ItemsSource = _audio.Playlist;
            QueueList.ItemsSource = _audio.QueueGroups;
            _audio.QueueGroups.CollectionChanged += (_, _) =>
                QueueEmptyHint.Visibility = _audio.QueueGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _audio.StateChanged += Audio_StateChanged;
            _audio.NowPlayingChanged += Audio_NowPlayingChanged;
            _audio.PlaybackFailed += Audio_PlaybackFailed;
            _audio.QueueChanged += UpdateSummary;

            _positionTimer.Tick += PositionTimer_Tick;
            _meterTimer.Tick += MeterTimer_Tick;

            BuildLevelBars();
            UpdateModeHint();
            UpdateSummary();
            Audio_NowPlayingChanged(null);
            Audio_StateChanged(AudioPlaybackState.Idle);
        }

        /// <summary>Called from MainWindow.Window_Closing to release the audio device and stop probing.</summary>
        public void ReleaseAudio() => _audio.Dispose();

        // ===================== LIFECYCLE =====================

        /// <summary>
        /// MainWindow unloads this control whenever another tool is opened.
        /// It used to tear down the whole player here while leaving the UI
        /// saying "playing" - coming back showed a pause button that did
        /// nothing. Now it just pauses, and resumes normally from Play.
        /// </summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            _audio.Pause();
        }

        // ===================== SIDEBAR COLLAPSE =====================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;

            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;

            if (BtnCollapseOptions.Content is StackPanel headerStack)
                headerStack.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        // ===================== ADDING FILES =====================

        // The Files card is the same as in Validate Data / Convert Files / Images to PDF:
        // Add files and Add folder side by side (the old Files/Folder switch hid one of them).

        /// <summary>
        /// Changing "Include subfolders" now updates the folders already in the
        /// playlist (before, the box did nothing until the next Add folder).
        /// Tracks keep their place; subfolder tracks are added at the end or removed.
        /// </summary>
        private async void ChkIncludeSubfolders_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool include = ChkIncludeSubfolders.IsChecked == true;
            var folders = _audio.QueueGroups.Where(g => g.IsFolder && g.IncludesSubfolders != include).ToList();
            if (folders.Count == 0) return;

            ChkIncludeSubfolders.IsEnabled = false;
            BtnAddFolder.IsEnabled = false;
            try
            {
                int added = 0, removed = 0;
                var problems = new List<string>();
                foreach (var group in folders)
                {
                    var (a, r, error) = await _audio.RescanFolderAsync(group, include);
                    added += a;
                    removed += r;
                    if (error != null) problems.Add(error);
                }
                if (added + removed > 0) UiMotion.Pop(DropArea, 0.97);
                TxtNowPlayingDetail.Text = include
                    ? (added > 0 ? $"Added {added:N0} track(s) from subfolders, at the end of the playlist." : "No audio files in the subfolders.")
                    : (removed > 0 ? $"Took {removed:N0} subfolder track(s) off the playlist." : "No subfolder tracks were in the playlist.");
                if (problems.Count > 0)
                    ShowMessage("Some folders couldn't be read again:\n" + string.Join("\n", problems), "Include subfolders", MessageBoxImage.Warning);
            }
            finally
            {
                ChkIncludeSubfolders.IsEnabled = true;
                BtnAddFolder.IsEnabled = true;
            }
        }

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Add Audio Files",
                Filter = AudioReplayManager.OpenFileFilter,
            };

            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
                _audio.AddFiles(dlg.FileNames);
        }

        private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Add Audio Folder" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true || string.IsNullOrEmpty(dlg.FolderName))
                return;

            await AddFolderAsync(dlg.FolderName, ChkIncludeSubfolders.IsChecked == true, reportEmpty: true);
        }

        private async Task AddFolderAsync(string folder, bool includeSubfolders, bool reportEmpty)
        {
            BtnAddFolder.IsEnabled = false;
            try
            {
                var result = await _audio.AddFolderAsync(folder, includeSubfolders);

                if (!result.Succeeded)
                    ShowMessage($"Couldn't read that folder:\n{result.Error}", "Add Folder", MessageBoxImage.Warning);
                else if (result.Added == 0 && reportEmpty)
                    ShowMessage(result.Duplicates > 0
                            ? "Every audio file in that folder is already in the playlist."
                            : "No supported audio files were found in that folder.",
                        "Add Folder", MessageBoxImage.Information);
            }
            finally
            {
                BtnAddFolder.IsEnabled = true;
            }
        }

        /// <summary>Dropping on the Files card lights its border; elsewhere on the page the big overlay shows.</summary>
        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) DropArea.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;   // don't also open the page overlay
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e) =>
            DropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");

        private async void DropArea_Drop(object sender, DragEventArgs e)
        {
            DropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddDroppedAsync(paths);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>
        /// DragLeave also fires when the pointer merely crosses from one child
        /// element to another, so only hide once it has really left the view.
        /// </summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                UiMotion.FadeVisibility(DropOverlay, false);
        }

        private async void Root_Drop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(DropOverlay, false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddDroppedAsync(paths);
        }

        /// <summary>Dropped folders follow "Include subfolders" (they always included subfolders before, whatever the box said).</summary>
        /// <summary>Adds files or folders to the queue, as if they had been dropped here (the Media Viewer hub forwards drops this way).</summary>
        public void AddPaths(IEnumerable<string> paths) => _ = AddDroppedAsync(paths.ToArray());

        private async Task AddDroppedAsync(string[] paths)
        {
            var looseFiles = paths.Where(p => File.Exists(p) && AudioReplayManager.IsAudioFile(p)).ToList();
            if (looseFiles.Count > 0)
                _audio.AddFiles(looseFiles);

            bool subfolders = ChkIncludeSubfolders.IsChecked == true;
            foreach (var folder in paths.Where(Directory.Exists))
                await AddFolderAsync(folder, subfolders, reportEmpty: false);
        }

        // ===================== QUEUE / PLAYLIST =====================

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: AudioQueueGroup group })
                _audio.RemoveGroup(group);
        }

        private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: AudioPlaylistItem item })
                _audio.RemoveItems(new[] { item });
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e) => _audio.RemoveSelected();

        /// <summary>Clear on the Files card and Clear All under the playlist do the same.</summary>
        private void BtnClearFiles_Click(object sender, RoutedEventArgs e) => _audio.Clear();

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => _audio.Clear();

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            _audio.SetAllSelected(true);
            UpdateActionButtonState();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            _audio.SetAllSelected(false);
            UpdateActionButtonState();
        }

        private void ItemCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateActionButtonState();

        private void DataGridQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not DataGridRow)
                dep = VisualTreeHelper.GetParent(dep);

            if (dep is DataGridRow { Item: AudioPlaylistItem item })
                _audio.Play(item);
        }

        // ----- Order: move up/down, sort by name or date -----

        private void DataGridQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BtnClearAll != null) UpdateActionButtonState();   // can fire during InitializeComponent
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            var chosen = DataGridQueue.SelectedItems.OfType<AudioPlaylistItem>().ToList();
            if (_audio.Move(chosen, direction)) AfterReorder(chosen);
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            var chosen = DataGridQueue.SelectedItems.OfType<AudioPlaylistItem>().ToList();
            _audio.Sort(byDate);
            AfterReorder(chosen);
        }

        /// <summary>Re-applies the highlight (rebuilding the list drops it) and keeps the first highlighted row in view.</summary>
        private void AfterReorder(IReadOnlyList<AudioPlaylistItem> chosen)
        {
            DataGridQueue.SelectedItems.Clear();
            foreach (var item in chosen) DataGridQueue.SelectedItems.Add(item);
            var first = chosen.OrderBy(c => _audio.Playlist.IndexOf(c)).FirstOrDefault();
            if (first != null)
            {
                DataGridQueue.ScrollIntoView(first);
                DataGridQueue.CurrentItem = first;
            }
        }

        private void DataGridQueue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is CheckBox) return;
            if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
            {
                MoveHighlighted(e.SystemKey == Key.Up ? -1 : +1);
                e.Handled = true;
            }
        }

        private void UpdateSummary()
        {
            int count = _audio.Playlist.Count;
            string summary = $"{count:N0} file(s) queued";
            bool changed = TxtSummaryCount.Text != summary;
            TxtSummaryCount.Text = summary;
            SummaryPill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (changed && count > 0) UiMotion.Pop(SummaryPill);
            DataGridQueue.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStatePanel.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateActionButtonState();
            UpdateNowPlayingDetail();
        }

        private void UpdateActionButtonState()
        {
            bool hasItems = _audio.Playlist.Count > 0;
            BtnRemoveSelected.IsEnabled = hasItems && _audio.AnySelected;
            BtnClearQueue.IsEnabled = hasItems;
            BtnClearAll.IsEnabled = hasItems;
            BtnSelectAll.IsEnabled = hasItems;
            BtnDeselectAll.IsEnabled = hasItems;
            BtnMoveUp.IsEnabled = DataGridQueue.SelectedItems.Count > 0;
            BtnMoveDown.IsEnabled = DataGridQueue.SelectedItems.Count > 0;
            BtnSortByName.IsEnabled = _audio.Playlist.Count > 1;
            BtnSortByDate.IsEnabled = _audio.Playlist.Count > 1;
            BtnPlayPause.IsEnabled = hasItems || _audio.HasTrackLoaded;
            BtnNext.IsEnabled = hasItems;
            BtnPrevious.IsEnabled = hasItems;
        }

        // ===================== REPLAY MODE =====================

        private void RadioReplayMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            _audio.Mode = RadioModeShuffle.IsChecked == true ? AudioReplayMode.Shuffle
                : RadioModeLoop.IsChecked == true ? AudioReplayMode.Loop
                : AudioReplayMode.Sequential;

            UpdateModeHint();
            UpdateNowPlayingDetail();
            UiMotion.FadeIn(TxtModeHint, risePx: 3);
        }

        private void UpdateModeHint()
        {
            TxtModeHint.Text = _audio.Mode switch
            {
                AudioReplayMode.Shuffle => "Shuffle plays every queued file once, in a random order.",
                AudioReplayMode.Loop => "Loop repeats the current file until you skip or stop it.",
                _ => "Sequential plays every queued file once, in the order shown.",
            };
        }

        // ===================== TRANSPORT =====================

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => _audio.TogglePlayPause();
        private void BtnStop_Click(object sender, RoutedEventArgs e) => _audio.Stop();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => _audio.Next();
        private void BtnPrevious_Click(object sender, RoutedEventArgs e) => _audio.Previous();
        private void BtnSkipBack_Click(object sender, RoutedEventArgs e) => _audio.SkipBy(TimeSpan.FromSeconds(-10));
        private void BtnSkipForward_Click(object sender, RoutedEventArgs e) => _audio.SkipBy(TimeSpan.FromSeconds(10));

        private void Audio_StateChanged(AudioPlaybackState state)
        {
            bool playing = state == AudioPlaybackState.Playing;
            IconPlayPause.Kind = playing ? PackIconKind.Pause : PackIconKind.Play;
            BtnPlayPause.ToolTip = playing ? "Pause" : "Play";

            if (playing)
            {
                _positionTimer.Start();
                _meterTimer.Start();
            }
            else
            {
                _positionTimer.Stop();
                // Meter keeps ticking briefly so the bars fall smoothly to rest
                // instead of freezing mid-height; MeterTimer_Tick stops it.
            }

            UpdatePositionDisplay();
            UpdateActionButtonState();
        }

        private void Audio_NowPlayingChanged(AudioPlaylistItem? item)
        {
            TxtNowPlaying.Text = item?.Name ?? "Nothing loaded";
            TxtNowPlaying.ToolTip = item?.FilePath;
            UiMotion.FadeVisibility(NoClipPanel, item == null);
            UpdateNowPlayingDetail();

            if (item != null)
            {
                UiMotion.FadeIn(TxtNowPlaying);
                UiMotion.Pop(AlbumTile, fromScale: 0.8);
            }

            SliderPosition.Maximum = Math.Max(1, _audio.Duration.TotalSeconds);
            TxtTotalTime.Text = AudioReplayManager.FormatTime(_audio.Duration);
            UpdatePositionDisplay();

            if (item != null)
                DataGridQueue.ScrollIntoView(item);
        }

        /// <summary>"Track 3 of 42 · Shuffle · Music\Albums" under the title.</summary>
        private void UpdateNowPlayingDetail()
        {
            var item = _audio.CurrentItem;
            if (item == null)
            {
                TxtNowPlayingDetail.Text = _audio.Playlist.Count > 0
                    ? $"{_audio.Playlist.Count:N0} track(s) ready - press Play or double-click one"
                    : "Pick a track to start";
                return;
            }

            string mode = _audio.Mode switch
            {
                AudioReplayMode.Shuffle => "Shuffle",
                AudioReplayMode.Loop => "Loop",
                _ => "In order",
            };
            int position = _audio.Playlist.IndexOf(item) + 1;
            string folder = Path.GetFileName(Path.GetDirectoryName(item.FilePath) ?? "");

            TxtNowPlayingDetail.Text = $"Track {position:N0} of {_audio.Playlist.Count:N0}  ·  {mode}"
                                       + (folder.Length > 0 ? $"  ·  {folder}" : "");
        }

        private void Audio_PlaybackFailed(AudioPlaylistItem? item, string message)
        {
            TxtNowPlaying.Text = item != null ? $"Couldn't play \"{item.Name}\"" : "Playback error";
            ShowMessage($"That file couldn't be played:\n{message}", "Playback Error", MessageBoxImage.Warning);
        }

        // ===================== SEEK / VOLUME / SPEED =====================

        private void PositionTimer_Tick(object? sender, EventArgs e) => UpdatePositionDisplay();

        private void UpdatePositionDisplay()
        {
            if (_isDraggingSeek) return;
            TxtCurrentTime.Text = AudioReplayManager.FormatTime(_audio.Position);
            SliderPosition.Value = _audio.Position.TotalSeconds;
        }

        private void SliderPosition_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isDraggingSeek = true;

        private void SliderPosition_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSeek = false;
            _audio.Seek(TimeSpan.FromSeconds(SliderPosition.Value));
            UpdatePositionDisplay();
        }

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            _audio.Volume = (float)e.NewValue;
            UpdateMuteIcon();
        }

        /// <summary>Mute keeps the slider where it is, so un-muting returns to the same level.</summary>
        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            if (_audio.IsMuted && SliderVolume.Value <= 0.0001)
                SliderVolume.Value = 1.0; // muted by dragging to 0 - bring it back up
            else
                _audio.IsMuted = !_audio.IsMuted;

            UpdateMuteIcon();
        }

        private void UpdateMuteIcon()
        {
            // Swap the PackIcon's Kind - the old code replaced Button.Content with
            // an emoji string, which wiped out the icon.
            IconMute.Kind = _audio.IsMuted ? PackIconKind.VolumeOff
                : SliderVolume.Value < 0.5 ? PackIconKind.VolumeMedium
                : PackIconKind.VolumeHigh;
            SliderVolume.Opacity = _audio.IsMuted ? 0.5 : 1.0;
        }

        private void CmbSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            int i = CmbSpeed.SelectedIndex;
            _audio.Speed = i >= 0 && i < SpeedOptions.Length ? SpeedOptions[i] : 1.0;
        }

        // ===================== LAYOUT =====================

        private void BtnTogglePlaylist_Click(object sender, RoutedEventArgs e)
        {
            _playlistCollapsed = !_playlistCollapsed;
            PlaylistPanel.Visibility = _playlistCollapsed ? Visibility.Collapsed : Visibility.Visible;
            PlaylistColumn.Width = new GridLength(_playlistCollapsed ? 0 : PlaylistWidth);
            IconTogglePlaylist.Kind = _playlistCollapsed ? PackIconKind.ChevronLeft : PackIconKind.ChevronRight;
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double width = e.NewSize.Width;
            if (width <= 0) return;

            if (width < NarrowLayoutWidth)
            {
                PlaylistPanel.Visibility = Visibility.Collapsed;
                PlaylistColumn.Width = new GridLength(0);
            }
            else if (!_playlistCollapsed)
            {
                PlaylistPanel.Visibility = Visibility.Visible;
                PlaylistColumn.Width = new GridLength(PlaylistWidth);
            }
        }

        // ===================== SPECTRUM VISUALIZER =====================

        /// <summary>
        /// One cell per band: a gradient bar (bass = red ... treble = violet)
        /// plus a small "peak cap" that jumps to each new high and then
        /// drifts back down - the classic analyser look.
        /// </summary>
        private void BuildLevelBars()
        {
            LevelBarsPanel.Columns = LevelBarCount;
            LevelBarsPanel.Rows = 1;
            LevelBarsPanel.Children.Clear();

            for (int i = 0; i < LevelBarCount; i++)
            {
                double hue = 270.0 * i / Math.Max(1, LevelBarCount - 1);
                Color top = HsvToRgb(hue, 0.75, 1.0);
                Color bottom = HsvToRgb(hue, 0.95, 0.55);

                var barBrush = new LinearGradientBrush(bottom, top, new System.Windows.Point(0, 1), new System.Windows.Point(0, 0));
                barBrush.Freeze();
                var capBrush = new SolidColorBrush(top);
                capBrush.Freeze();

                var bar = new Rectangle
                {
                    Height = MinBarHeight,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = barBrush,
                    VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                };

                var capShift = new TranslateTransform();
                var cap = new Rectangle
                {
                    Height = PeakCapHeight,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = capBrush,
                    Opacity = 0.9,
                    VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                    RenderTransform = capShift,
                };

                var cell = new Grid { Margin = new Thickness(1.5, 0, 1.5, 0) };
                cell.Children.Add(bar);
                cell.Children.Add(cap);
                LevelBarsPanel.Children.Add(cell);

                _bars[i] = bar;
                _caps[i] = capShift;
            }
        }

        private void MeterTimer_Tick(object? sender, EventArgs e)
        {
            bool live = _audio.GetSpectrumLevels(_levels);

            // Bars fill the visualizer's real height instead of a fixed 220px.
            double maxHeight = Math.Max(40, LevelBarsPanel.ActualHeight - PeakCapHeight - 4);
            bool anyMoving = false;

            for (int i = 0; i < LevelBarCount; i++)
            {
                double target = _levels[i] * maxHeight;
                double current = _barHeights[i];

                // Fast attack, slow release - real analyser ballistics.
                double next = current + (target - current) * (target > current ? 0.35 : 0.12);
                _barHeights[i] = next;
                _bars[i].Height = Math.Max(MinBarHeight, next);

                // Peak cap: jump up, hold briefly, then fall at a steady rate.
                if (next >= _peakHeights[i])
                {
                    _peakHeights[i] = next;
                    _peakHold[i] = (int)PeakHoldTicks;
                }
                else if (_peakHold[i] > 0)
                {
                    _peakHold[i]--;
                }
                else
                {
                    _peakHeights[i] = Math.Max(0, _peakHeights[i] - PeakFallPerTick);
                }

                _caps[i].Y = -Math.Max(MinBarHeight, _peakHeights[i]) - 1;

                if (next > MinBarHeight + 0.5 || _peakHeights[i] > MinBarHeight + 0.5)
                    anyMoving = true;
            }

            // Album tile "breathes" with the low end: average of the first bands,
            // smoothed so it pulses rather than jitters.
            const int bassBands = 6;
            double bass = 0;
            for (int i = 0; i < bassBands; i++) bass += _levels[i];
            bass /= bassBands;
            _tilePulse += (bass - _tilePulse) * (bass > _tilePulse ? 0.45 : 0.15);

            double tileScale = 1 + 0.07 * _tilePulse;
            AlbumTileScale.ScaleX = tileScale;
            AlbumTileScale.ScaleY = tileScale;
            AlbumTileGlow.Opacity = 0.25 + 0.45 * _tilePulse;
            if (_tilePulse > 0.01) anyMoving = true;

            // Once paused/stopped and everything has settled, stop ticking.
            if (!live && !anyMoving)
            {
                _meterTimer.Stop();
                _tilePulse = 0;
                AlbumTileScale.ScaleX = AlbumTileScale.ScaleY = 1;
                AlbumTileGlow.Opacity = 0.25;
            }
        }

        private static Color HsvToRgb(double hue, double saturation, double value)
        {
            hue %= 360.0;
            if (hue < 0) hue += 360.0;

            double c = value * saturation;
            double x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
            double m = value - c;

            (double r, double g, double b) = hue switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };

            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        // ===================== HELPERS =====================

        private void ShowMessage(string text, string title, MessageBoxImage icon)
        {
            var owner = Window.GetWindow(this);
            if (owner != null)
                AppModal.Show(owner, text, title, MessageBoxButton.OK, icon);
            else
                AppModal.Show(text, title, MessageBoxButton.OK, icon);
        }
    }
}
