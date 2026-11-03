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
    //  mini player. UI only: the file's shape and load/save live in
    //  MediaViewerManager, the window is MediaViewerWidgetWindow.
    //
    //  The mini player is the real player: pressing W moves the Stage (the
    //  picture, video and controls) out of the tool into a small window, and
    //  closing the window moves it back. There is only ever one player, so
    //  nothing has to be kept in sync.
    // =====================================================================
    public partial class MediaViewerView
    {
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private MediaViewerWidgetWindow? _widget;
        private bool _applyingSettings;

        /// <summary>
        /// False until the remembered list has been read back. Until then nothing is saved - the list
        /// is still empty, and saving it would erase what the file remembers.
        /// </summary>
        private bool _settingsReady;

        /// <summary>The list came back from the last session: the remembered item is chosen but nothing is loaded yet.</summary>
        private bool _restoredIdle;

        /// <summary>True while the player is out in the mini window (the controls then show their small form).</summary>
        private bool StageInWidget => _widget != null;

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

        /// <summary>The Settings page changed something: "Reset position and size" puts an open mini player back in its corner.</summary>
        private void OnSettingsChangedElsewhere()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_widget == null) return;
                var s = MediaViewerManager.LoadSettings();
                if (s.WidgetLeft == null && s.WidgetTop == null && s.WidgetWidth == null && s.WidgetHeight == null)
                    _widget.ResetPlacement();
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
        //  The mini player: the same player, in a small window
        // =================================================================

        private void BtnWidget_Click(object sender, RoutedEventArgs e) => ToggleWidget();

        private void BtnBringBack_Click(object sender, RoutedEventArgs e) => HideWidget(openTool: false);

        private void ToggleWidget()
        {
            if (_widget == null) ShowWidget();
            else HideWidget(openTool: false);
        }

        /// <summary>Called by the main window at start-up when "show the mini player when the app starts" is on.</summary>
        public void RestoreWidget()
        {
            if (_widget == null) ShowWidget();
        }

        /// <summary>Moves the player out of the tool into the small window; the tool shows a placeholder meanwhile.</summary>
        private void ShowWidget()
        {
            if (_widget != null) return;
            var w = new MediaViewerWidgetWindow(MediaViewerManager.LoadSettings());
            w.MinimizeAppRequested += RequestMinimizeApp;
            w.BackRequested += () => HideWidget(openTool: true);
            w.CloseRequested += () => HideWidget(openTool: false);
            w.KeyPressed += e => Root_PreviewKeyDown(this, e);   // the same keys as in the tool
            w.PlacementChanged += (left, top, width, height) => MediaViewerManager.UpdateSettings(x =>
            {
                x.WidgetLeft = left;
                x.WidgetTop = top;
                x.WidgetWidth = width;
                x.WidgetHeight = height;
            }, notify: false);
            // Closed some other way (Alt+F4): the player must still go home.
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_widget, w)) ReturnStage(closeWindow: false);
            };

            var keep = CapturePlayback();
            PlayerCardGrid.Children.Remove(Stage);
            StagePlaceholder.Visibility = Visibility.Visible;
            w.Host(Stage, StageChrome, ChromeScrim);
            _widget = w;
            w.RefreshText();
            w.SetTitle(Current?.Name ?? "");
            w.Show();
            RestorePlayback(keep);
            UpdateUi();
        }

        /// <summary>Puts the player back in the tool and closes the small window. <paramref name="openTool"/> also brings the main window forward on the tool.</summary>
        private void HideWidget(bool openTool)
        {
            if (_widget == null) return;
            ReturnStage(closeWindow: true);
            if (openTool) RequestOpenTool();
        }

        private void ReturnStage(bool closeWindow)
        {
            var w = _widget;
            if (w == null) return;
            _widget = null;

            var keep = CapturePlayback();
            w.Release();
            if (!PlayerCardGrid.Children.Contains(Stage)) PlayerCardGrid.Children.Insert(0, Stage);
            StagePlaceholder.Visibility = Visibility.Collapsed;
            if (closeWindow) w.Close();
            RestorePlayback(keep);

            // Back in a tool page nobody is looking at, with no window to control it: it would play on unheard-of.
            if (!IsLoaded && _playing)
            {
                Player.Pause();
                _playing = false;
            }
            UpdateUi();
        }

        /// <summary>Closes the mini player when the app exits (the "open at start-up" choice stays as the user set it).</summary>
        private void CloseWidgetForExit() => ReturnStage(closeWindow: true);

        // Moving the MediaElement to another window makes it re-attach; remember where it was so a stall can't leave it frozen.
        private (TimeSpan Position, bool Playing, bool Open) CapturePlayback() =>
            (_mediaOpen ? Player.Position : TimeSpan.Zero, _playing, _mediaOpen);

        private void RestorePlayback((TimeSpan Position, bool Playing, bool Open) keep)
        {
            if (!keep.Open) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_mediaOpen) return;   // another item was chosen meanwhile
                if (Math.Abs((Player.Position - keep.Position).TotalSeconds) > 1.5) Player.Position = keep.Position;
                if (keep.Playing && _playing) Player.Play();
            });
        }

        /// <summary>Minimizes the main window so only the mini player is left on screen ("Back to the tool" brings the app back).</summary>
        private void RequestMinimizeApp()
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.MinimizeForWidget();
        }

        /// <summary>Brings the main window forward on this tool (the widget's "Back to the tool").</summary>
        private void RequestOpenTool()
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.ShowTool("media-viewer");
        }
    }
}
