using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Smart_Tools.WPF.Views
{
    // =====================================================================
    //  Media Viewer - what it remembers (media_viewer.json) and the floating
    //  widget. UI only: the file's shape and load/save live in
    //  MediaViewerManager, the widget window is MediaViewerWidgetWindow.
    // =====================================================================
    public partial class MediaViewerView
    {
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private MediaViewerWidgetWindow? _widget;
        private bool _applyingSettings;
        private bool _keepPlayingWithWidget = true;

        /// <summary>
        /// False until the remembered list has been read back. Until then nothing is saved - the list
        /// is still empty, and saving it would erase what the file remembers.
        /// </summary>
        private bool _settingsReady;

        /// <summary>The list came back from the last session: the remembered item is chosen but nothing is loaded yet.</summary>
        private bool _restoredIdle;

        private void InitializeSettings()
        {
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettingsNow();
            };
            MediaViewerManager.SettingsChanged += OnSettingsChangedElsewhere;
            LanguageManager.Changed += () => _widget?.RefreshText();

            var s = MediaViewerManager.LoadSettings();
            ApplySettings(s);
            if (s.RememberPlaylist && s.Sources.Count > 0) _ = RestoreThenReadyAsync(s);
            else _settingsReady = true;
        }

        private async Task RestoreThenReadyAsync(MediaViewerSettings s)
        {
            try
            {
                await RestorePlaylistAsync(s);
            }
            catch (Exception ex)
            {
                Log.Info($"Media Viewer couldn't bring back the list: {ex.Message}");
            }
            finally
            {
                _settingsReady = true;
            }
        }

        // =================================================================
        //  Remembered options
        // =================================================================

        private void ApplySettings(MediaViewerSettings s)
        {
            _applyingSettings = true;
            try
            {
                (s.Order switch
                {
                    ViewerPlayOrder.Shuffle => RadioOrderShuffle,
                    ViewerPlayOrder.RepeatOne => RadioOrderRepeatOne,
                    _ => RadioOrderSequential,
                }).IsChecked = true;
                ChkRepeatPlaylist.IsChecked = s.RepeatPlaylist;
                UpdateOrderUi();

                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                VolumeSlider.Value = s.Volume;
                Player.IsMuted = s.Muted;
                SetSpeed(s.Speed);

                (SlideRadios.FirstOrDefault(r => (string?)r.Tag == s.SlideshowSeconds.ToString(CultureInfo.InvariantCulture)) ?? RadioSlide4).IsChecked = true;
                _slide.Interval = TimeSpan.FromSeconds(s.SlideshowSeconds);

                _playlistCollapsed = s.PlaylistCollapsed;
                _keepPlayingWithWidget = s.KeepPlayingWithWidget;
                ApplyPanels();
                UpdateUi();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        /// <summary>Save soon: dragging the volume slider would otherwise write the file on every step.</summary>
        private void ScheduleSave()
        {
            if (_applyingSettings || !_ready || !_settingsReady) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Writes only this page's fields (the Settings page owns the rest).</summary>
        private void SaveSettingsNow()
        {
            if (_applyingSettings || !_ready || !_settingsReady) return;
            var order = Order;
            bool repeat = ChkRepeatPlaylist.IsChecked == true;
            bool subfolders = InputCard.IncludeSubfolders;
            double volume = VolumeSlider.Value;
            bool muted = Player.IsMuted;
            double speed = _speed;
            int slideshow = (int)Math.Round(_slide.Interval.TotalSeconds);
            bool collapsed = _playlistCollapsed;
            var sources = _sources.Select(x => new MediaSavedSource { Path = x.FullPath, IsFolder = x.IsFolder }).ToList();
            string last = Current?.Path ?? "";

            MediaViewerManager.UpdateSettings(s =>
            {
                s.Order = order;
                s.RepeatPlaylist = repeat;
                s.IncludeSubfolders = subfolders;
                s.Volume = volume;
                s.Muted = muted;
                s.Speed = speed;
                s.SlideshowSeconds = slideshow;
                s.PlaylistCollapsed = collapsed;
                if (s.RememberPlaylist)
                {
                    s.Sources = sources;
                    if (last.Length > 0 || sources.Count == 0) s.LastItem = last;
                }
            }, notify: false);
        }

        /// <summary>The Settings page changed something (or another saver did): take in the fields that page owns.</summary>
        private void OnSettingsChangedElsewhere()
        {
            Dispatcher.BeginInvoke(() =>
            {
                var s = MediaViewerManager.LoadSettings();
                _keepPlayingWithWidget = s.KeepPlayingWithWidget;
                _widget?.SetCompact(s.WidgetCompact);
                // "Reset position" on the Settings page: an open widget goes back to its corner straight away.
                if (_widget != null && s.WidgetLeft == null && s.WidgetTop == null) _widget.SnapToCorner();
            });
        }

        // =================================================================
        //  Remembered list
        // =================================================================

        /// <summary>Brings back last session's files and folders (missing ones are skipped). Nothing starts playing.</summary>
        private async Task RestorePlaylistAsync(MediaViewerSettings s)
        {
            var fresh = s.Sources
                .Where(x => x.IsFolder ? Directory.Exists(x.Path) : File.Exists(x.Path))
                .Select(x => new MediaSource { FullPath = x.Path, IsFolder = x.IsFolder })
                .ToList();
            if (fresh.Count == 0) return;

            bool subfolders = s.IncludeSubfolders;
            var found = await Task.Run(() => MediaViewerManager.Expand(fresh.Select(f => f.FullPath).ToList(), subfolders));

            var known = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var item in found)
                if (known.Add(item.Path)) _items.Add(item);
            foreach (var src in fresh)
                if (!_sources.Any(x => string.Equals(x.FullPath, src.FullPath, StringComparison.OrdinalIgnoreCase))) _sources.Add(src);
            RebuildGroups();

            // Pick the item that was on the stage last time, without loading it.
            if (Current == null && s.LastItem.Length > 0)
            {
                var last = _items.FirstOrDefault(i => string.Equals(i.Path, s.LastItem, StringComparison.OrdinalIgnoreCase));
                if (last != null)
                {
                    _index = _items.IndexOf(last);
                    SetNowPlaying(last);
                    TxtNowPlaying.Text = last.Name;
                    DataGridQueue.ScrollIntoView(last);
                    _restoredIdle = true;
                }
            }
            UpdateUi();
        }

        // =================================================================
        //  The floating widget
        // =================================================================

        private void BtnWidget_Click(object sender, RoutedEventArgs e) => ToggleWidget();

        private void ToggleWidget()
        {
            if (_widget == null) ShowWidget();
            else HideWidget();
        }

        /// <summary>Called by the main window at start-up when "show the widget when the app starts" is on.</summary>
        public void RestoreWidget()
        {
            if (_widget == null) ShowWidget();
        }

        private void ShowWidget()
        {
            if (_widget != null) return;
            var s = MediaViewerManager.LoadSettings();
            var w = new MediaViewerWidgetWindow(s);
            w.PlayPauseRequested += () =>
            {
                if (_items.Count == 0) RequestOpenTool();   // nothing to play yet: bring the tool forward so files can be added
                else TogglePlay();
            };
            w.PreviousRequested += GoPrevious;
            w.NextRequested += GoNext;
            w.SeekRequested += seconds =>
            {
                if (!_mediaOpen) return;
                Player.Position = TimeSpan.FromSeconds(seconds);
                TxtPosition.Text = MediaViewerManager.FormatTime(Player.Position);
            };
            w.VolumeChanged += v => VolumeSlider.Value = v;
            w.MuteToggled += () => BtnMute_Click(this, new RoutedEventArgs());
            w.SpeedStepRequested += StepSpeed;
            w.OrderCycleRequested += CycleOrder;
            w.OpenToolRequested += RequestOpenTool;
            w.CloseRequested += HideWidget;
            w.PositionChanged += (left, top) => MediaViewerManager.UpdateSettings(x => { x.WidgetLeft = left; x.WidgetTop = top; }, notify: false);
            w.CompactToggled += compact => MediaViewerManager.UpdateSettings(x => x.WidgetCompact = compact);
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_widget, w)) _widget = null;
                UpdateUi();
            };
            _widget = w;
            w.RefreshText();
            w.Show();
            PushWidget();
        }

        private void HideWidget()
        {
            var w = _widget;
            _widget = null;
            w?.Close();
            // Without the widget there's no way to stop music from another tool, so the page pauses it if it isn't showing.
            if (!IsLoaded && _playing)
            {
                Player.Pause();
                _playing = false;
            }
            UpdateUi();
        }

        /// <summary>Closes the widget when the app exits (the "open at start-up" choice stays as the user set it).</summary>
        private void CloseWidgetForExit()
        {
            var w = _widget;
            _widget = null;
            w?.Close();
        }

        private void CycleOrder()
        {
            var next = Order switch
            {
                ViewerPlayOrder.Sequential => RadioOrderShuffle,
                ViewerPlayOrder.Shuffle => RadioOrderRepeatOne,
                _ => RadioOrderSequential,
            };
            next.IsChecked = true;
            Order_Click(next, new RoutedEventArgs());
        }

        /// <summary>Brings the main window forward on this tool (the widget's "Open Media Viewer").</summary>
        private void RequestOpenTool()
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.ShowTool("media-viewer");
        }

        private MediaWidgetState BuildWidgetState()
        {
            var item = Current;
            if (item == null)
            {
                string waiting = _items.Count > 0
                    ? string.Format(CultureInfo.CurrentCulture, S("Str_MediaViewer_ItemsCount", "{0:N0} items"), _items.Count)
                    : "";
                return new MediaWidgetState(false, "", waiting, ViewerMediaKind.Audio, false, 0, 0, VolumeSlider.Value, Player.IsMuted,
                    MediaViewerManager.SpeedLabel(_speed), Order);
            }

            bool image = item.Kind == ViewerMediaKind.Image;
            double position = 0, duration = 0;
            if (!image && _mediaOpen)
            {
                position = Player.Position.TotalSeconds;
                if (Player.NaturalDuration.HasTimeSpan) duration = Player.NaturalDuration.TimeSpan.TotalSeconds;
            }
            string kind = item.Kind switch
            {
                ViewerMediaKind.Image => S("Str_MediaViewer_Picture", "Picture"),
                ViewerMediaKind.Video => S("Str_MediaViewer_Video", "Video"),
                _ => S("Str_MediaViewer_Audio", "Music"),
            };
            return new MediaWidgetState(true, item.Name, $"{kind} · {_index + 1} / {_items.Count}", item.Kind,
                image ? _slideshow : _playing, position, duration, VolumeSlider.Value, Player.IsMuted, MediaViewerManager.SpeedLabel(_speed), Order);
        }

        private void PushWidget() => _widget?.Update(BuildWidgetState());
    }
}
