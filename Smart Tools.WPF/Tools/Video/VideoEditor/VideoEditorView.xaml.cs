using MaterialDesignThemes.Wpf;
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
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using DataFormats = System.Windows.DataFormats;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Video Stitcher - UI only. Reading clips, deciding how to join them,
    /// ffmpeg, trims, names and settings are in <see cref="VideoEditorManager"/>.
    /// The player is the same pair Video Replay uses (VLC when Engines\VLC
    /// loads, else Windows' player with preview copies). Folders picked here
    /// are kept for this session only - never saved.
    /// </summary>
    public partial class VideoEditingView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double QueueExpandedWidth = 420;
        private static string DialogTitle => Loc.T("Str_Tool_video-editor_Name", "Video Stitcher");

        private readonly ObservableCollection<EditClip> _clips = new();
        private readonly ObservableCollection<EditClipGroup> _groups = new();
        private readonly DispatcherTimer _positionTimer;
        private readonly DispatcherTimer _saveTimer;   // the volume slider saves once it settles, not on every tick
        private readonly CancellationTokenSource _lifetimeCts = new();   // folder reading and lengths
        private readonly bool _viewConstructed;
        private EditorSettings _settings = new();

        // Player
        private readonly MediaElementBackend _windowsBackend;
        private VlcBackend? _vlcBackend;
        private Task? _vlcStart;
        private IReplayBackend _backend;
        private bool _vlcShutDown;
        private EditClip? _current;          // the clip in the player (null while a preview plays)
        private string? _currentSource;
        private bool _playingPreview;
        private bool _isPlaying;
        private bool _isUserSeeking;
        private bool _updatingSlider;
        private bool _muted;
        private int _consecutiveFailures;
        private TimeSpan? _seekWhenOpened;    // a trimmed clip starts at its start point
        private TimeSpan _lastPosition;       // to notice playback crossing a clip's end point
        // Where the last seek went, until the player gets there. Seeks in long or big files take
        // a second or more, and meanwhile the player still reports the old spot - the thumb jumped
        // back and repeated arrow presses all counted from the old spot.
        private TimeSpan? _seekTarget;
        private long _seekIssuedAt;
        private long _lastDragSeekAt;
        private CancellationTokenSource? _loadCts;

        // Runs
        private CancellationTokenSource? _runCts;
        private bool _isBusy;
        private int _adding;
        // Inside the Output card (built in BuildOutputOptions).
        private TextBox TxtOutputFileName = null!;
        private CheckBox AutoSaveCheckBox = null!;
        private List<string> _lastFiles = new();

        private bool _optionsCollapsed;
        private bool _queueCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _fileNameEdited;
        private bool _settingFileName;

        public VideoEditingView()
        {
            InitializeComponent();

            DataGridQueue.ItemsSource = _clips;
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = VideoEditorManager.FileDialogFilter;
            _clips.CollectionChanged += (_, _) => UpdateListState();
            _groups.CollectionChanged += (_, _) =>
                InputCard.SetEmpty(_groups.Count == 0);
            DataGridQueue.SelectionChanged += (_, _) => UpdateButtons();

            _positionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += PositionTimer_Tick;
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettingsQuietly();
            };
            _windowsBackend = new MediaElementBackend(Player);
            _backend = _windowsBackend;
            HookBackend(_windowsBackend);

            OutputCard.DefaultFolder = VideoEditorManager.DefaultFolder;
            BuildOutputOptions();
            SetFileName(VideoEditorManager.DefaultFileName);
            _viewConstructed = true;
        }

        /// <summary>
        /// Called from MainWindow.Window_Closing: stops a stitch or preview
        /// (ffmpeg is ended and nothing half-made is kept), stops playback,
        /// shuts VLC down and deletes previews.
        /// </summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _loadCts?.Cancel();
            _lifetimeCts.Cancel();
            StopPlayback();
            _positionTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettingsQuietly();   // a volume change still waiting to be saved
            }
            ShutDownVlc();
            VideoEditorManager.SweepPreviews();
            VideoReplayManager.SweepPreviews();
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        // =================================================================
        //  Lifetime, layout, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(VideoEditorManager.LoadSettings());
                _ = Task.Run(VideoEditorManager.SweepPreviews);   // leftovers from a crash
                _vlcStart = StartVlcAsync();
            }
            UpdateListState();
            UpdateNowPlaying();
        }

        /// <summary>Leaving the page: VLC's window goes with it, so stop; Windows' player just pauses.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_backend is VlcBackend) StopPlayback();
            else if (_isPlaying) Pause();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
            ColLength.Visibility = e.NewSize.Width < 900 ? Visibility.Collapsed : Visibility.Visible;

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void BtnToggleQueue_Click(object sender, RoutedEventArgs e)
        {
            _queueCollapsed = !_queueCollapsed;
            ApplyQueuePanel();
            SaveSettingsQuietly();
        }

        private void ApplyQueuePanel()
        {
            QueueColumn.Width = new GridLength(_queueCollapsed ? 0 : QueueExpandedWidth);
            QueuePanel.Visibility = _queueCollapsed ? Visibility.Collapsed : Visibility.Visible;
            // Flip the icon itself - the old code could replace the button's content and lose the icon.
            IconToggleQueue.Kind = _queueCollapsed ? PackIconKind.ChevronLeft : PackIconKind.ChevronRight;
        }

        private void ApplySettings(EditorSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                CheckByTag(s.FadeSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), RadioFadeNone, RadioFade05, RadioFade1, RadioFade2);
                CheckByTag(s.Format.ToString(), RadioFormatMp4, RadioFormatMkv, RadioFormatMov, RadioFormatWebm, RadioFormatAvi, RadioFormatFlv);
                CheckByTag(s.Size.ToString(), RadioSizeFirst, RadioSize1080, RadioSize720);
                CheckByTag(s.SplitMinutes.ToString(), RadioSplitOff, RadioSplit15, RadioSplit30, RadioSplit60);
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                AutoSaveCheckBox.IsChecked = s.SaveWithoutAsking;
                SliderVolume.Value = s.Volume;
                _muted = s.Muted;
                _queueCollapsed = s.QueueCollapsed;
            }
            finally
            {
                _applyingSettings = false;
            }
            ApplyAudioToBackend();
            UpdateMuteIcon();
            ApplyQueuePanel();
            UpdateOptionHints();
        }

        private EditorSettings ReadSettings() => new()
        {
            FadeSeconds = double.TryParse(CheckedTag(RadioFadeNone, RadioFade05, RadioFade1, RadioFade2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fade) ? fade : 0.5,
            Format = ParseTag(CheckedTag(RadioFormatMp4, RadioFormatMkv, RadioFormatMov, RadioFormatWebm, RadioFormatAvi, RadioFormatFlv), EditOutputFormat.Mp4),
            Size = ParseTag(CheckedTag(RadioSizeFirst, RadioSize1080, RadioSize720), EditOutputSize.FirstClip),
            SplitMinutes = int.TryParse(CheckedTag(RadioSplitOff, RadioSplit15, RadioSplit30, RadioSplit60), out int split) ? split : 15,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            SaveWithoutAsking = AutoSaveCheckBox.IsChecked == true,
            Volume = SliderVolume.Value,
            Muted = _muted,
            QueueCollapsed = _queueCollapsed,
        };

        private void SaveSettingsQuietly()
        {
            if (Ignore) return;
            _settings = ReadSettings();
            try
            {
                VideoEditorManager.SaveSettings(_settings);
            }
            catch (Exception)
            {
                // Settings are a convenience - a locked file mustn't stop the tool.
            }
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => OnOptionChanged();

        private void Option_Click(object sender, RoutedEventArgs e) => OnOptionChanged();

        private void OnOptionChanged()
        {
            if (Ignore) return;
            UpdateOptionHints();
            SaveSettingsQuietly();
        }

        private void UpdateOptionHints()
        {
            var s = ReadSettings();
            TxtFadeHint.Text = s.FadeSeconds > 0
                ? $"Each clip fades in from black and out to black over {s.FadeSeconds:0.#} s. Clips are re-encoded to add it."
                : "Straight cuts. Clips that match are joined as they are - in seconds, with no quality loss.";
            string format = s.Format switch
            {
                EditOutputFormat.Webm => "WebM uses VP9 video and Opus sound - encoding is slower.",
                EditOutputFormat.Mkv => "MKV holds any clip, so matching clips almost always join without re-encoding.",
                EditOutputFormat.Avi => "AVI gets H.264 video and MP3 sound.",
                _ => "H.264 video with AAC sound, using the graphics card when there is one.",
            };
            string split = s.SplitMinutes > 0 ? $" A new file starts before one passes {s.SplitMinutes} minutes." : " Everything goes in one file.";
            TxtOutputHint.Text = format + split;
        }

        // =================================================================
        //  Player engine (shared with Video Replay)
        // =================================================================

        /// <summary>Starts VLC in the background (its first start reads its plugins); until then - or without it - Windows' player is used.</summary>
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
                    // Leaving the page destroys VLC's window: stop and let VLC finish first.
                    host.Destroying += () =>
                    {
                        if (_vlcBackend is not { } vlc) return;
                        vlc.Stop();
                        vlc.WaitForIdle(TimeSpan.FromSeconds(3));
                    };
                    host.DoubleClicked += TogglePlay;
                    host.Clicked += () => Focus();
                    VlcSlot.Content = host;
                    var vlcBackend = new VlcBackend(host);
                    HookBackend(vlcBackend);
                    _vlcBackend = vlcBackend;
                    if (_current == null && !_playingPreview)
                    {
                        _backend = vlcBackend;
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
                ? "Playing with VLC from Engines\\VLC - every format plays directly. Stitching uses ffmpeg either way."
                : $"{error} WebM and FLV get a preview copy before they play. Stitching uses ffmpeg either way.";
            PillEngine.Visibility = Visibility.Visible;
        }

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

        private void HookBackend(IReplayBackend b)
        {
            b.Opened += () => { if (ReferenceEquals(b, _backend)) Backend_Opened(); };
            b.Ended += () => { if (ReferenceEquals(b, _backend)) Backend_Ended(); };
            b.Failed += m => { if (ReferenceEquals(b, _backend)) Backend_Failed(m); };
        }

        private void UseBestBackend()
        {
            var wanted = (IReplayBackend?)_vlcBackend ?? _windowsBackend;
            if (ReferenceEquals(wanted, _backend)) return;
            _backend.Stop();
            _backend = wanted;
            ApplyAudioToBackend();
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
        }

        // =================================================================
        //  Playback
        // =================================================================

        private void DataGridQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<ButtonBase>(source) != null) return;   // the remove button / tick box
            if (FindAncestor<DataGridRow>(source)?.Item is EditClip clip) _ = LoadAndPlayAsync(clip);
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (!_backend.HasMedia)
            {
                var first = (DataGridQueue.SelectedItem as EditClip) ?? _clips.FirstOrDefault(c => c.IsIncluded) ?? _clips.FirstOrDefault();
                if (first != null) _ = LoadAndPlayAsync(first);
                return;
            }
            if (_isPlaying) Pause();
            else
            {
                _backend.Play();
                _isPlaying = true;
                IconPlayPause.Kind = PackIconKind.Pause;
                _positionTimer.Start();
            }
        }

        private void Pause()
        {
            _backend.Pause();
            _isPlaying = false;
            IconPlayPause.Kind = PackIconKind.Play;
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayNeighbour(+1);

        private void BtnPrevious_Click(object sender, RoutedEventArgs e) => PlayNeighbour(-1);

        /// <summary>The next/previous ticked clip - the order the video will have.</summary>
        private void PlayNeighbour(int direction)
        {
            var order = _clips.Where(c => c.IsIncluded || ReferenceEquals(c, _current)).ToList();
            if (order.Count == 0) return;
            int index = _current != null ? order.IndexOf(_current) : -1;
            int next = index < 0 ? (direction > 0 ? 0 : order.Count - 1) : index + direction;
            if (next < 0 || next >= order.Count) return;
            _ = LoadAndPlayAsync(order[next]);
        }

        /// <summary>
        /// Loads a clip and plays its used part. Formats Windows' player can't
        /// open get a preview copy first (not needed with VLC); a clip that
        /// fails shows a banner (the old view showed a dialog for each one).
        /// </summary>
        private async Task LoadAndPlayAsync(EditClip clip, bool forcePreviewCopy = false)
        {
            _loadCts?.Cancel();
            var cts = _loadCts = new CancellationTokenSource();

            SetCurrent(clip);
            _playingPreview = false;
            HideNotice();
            NoClipPanel.Visibility = Visibility.Collapsed;
            DataGridQueue.ScrollIntoView(clip);
            UpdateNowPlaying();

            if (_vlcStart is { IsCompleted: false } starting)
            {
                _backend.Stop();
                ShowSurface(false);
                ShowConverting("Starting VLC...", "The first start takes a few seconds while it reads its plugins.", indeterminate: true);
                await starting;
                if (cts.IsCancellationRequested) return;
                ConvertingPanel.Visibility = Visibility.Collapsed;
            }

            UseBestBackend();
            string source = clip.FullPath;
            bool needsCopy = !_backend.PlaysEverything &&
                             (forcePreviewCopy || VideoReplayManager.NeedsPreviewCopy.Contains(Path.GetExtension(clip.FullPath)));
            if (needsCopy && VideoReplayManager.FfmpegExe != null)
            {
                _backend.Stop();
                ShowSurface(false);
                ShowConverting("Making a preview copy...", $"{Path.GetExtension(clip.FullPath).TrimStart('.').ToUpperInvariant()} can't be played by Windows directly.", indeterminate: true);
                try
                {
                    source = await VideoReplayManager.CreatePreviewAsync(clip.FullPath, p => Dispatcher.InvokeAsync(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        ProgressConvert.IsIndeterminate = false;
                        ProgressConvert.Value = p;
                        TxtConvert.Text = $"{p:0}% - only needed while VLC isn't in use. The stitch always uses the original.";
                    }), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;   // another clip was chosen
                }
                catch (Exception ex)
                {
                    ConvertingPanel.Visibility = Visibility.Collapsed;
                    FailCurrent(ex.Message);
                    return;
                }
                ConvertingPanel.Visibility = Visibility.Collapsed;
                if (cts.IsCancellationRequested) return;
            }

            _seekWhenOpened = clip.TrimStart;   // applied once the player has the file open
            OpenSource(source);
        }

        /// <summary>Plays the rendered preview (not tied to a row - its length mustn't be written back onto a clip).</summary>
        private async Task PlayPreviewFileAsync(string path)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            SetCurrent(null);
            _playingPreview = true;
            HideNotice();
            NoClipPanel.Visibility = Visibility.Collapsed;
            if (_vlcStart is { IsCompleted: false } starting) await starting;
            UseBestBackend();
            OpenSource(path);   // an H.264 MP4 - Windows' player can always open it
            UpdateNowPlaying();
        }

        private void OpenSource(string source)
        {
            _currentSource = source;
            _lastPosition = TimeSpan.Zero;
            _seekTarget = null;
            // The bar still shows the last clip: back to the start, so a shorter clip's length can't clamp it (and seek).
            _updatingSlider = true;
            SliderPosition.Value = 0;
            _updatingSlider = false;
            TxtCurrentTime.Text = "0:00";
            ShowSurface(true);
            try
            {
                _backend.Open(source);
            }
            catch (Exception ex)
            {
                FailCurrent(ex.Message);
                return;
            }
            _isPlaying = true;
            IconPlayPause.Kind = PackIconKind.Pause;
            _positionTimer.Start();
            UpdateNowPlaying();
        }

        private void SetCurrent(EditClip? clip)
        {
            if (_current != null && !ReferenceEquals(_current, clip)) _current.IsNowPlaying = false;
            _current = clip;
            if (clip != null) clip.IsNowPlaying = true;
        }

        private void StopPlayback()
        {
            _loadCts?.Cancel();
            _positionTimer.Stop();
            _backend.Stop();   // releases the file
            _currentSource = null;
            _seekTarget = null;
            ShowSurface(false);
            _isPlaying = false;
            SetCurrent(null);
            _playingPreview = false;
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
            if (_seekWhenOpened is TimeSpan start && !_playingPreview) SeekTo(start);
            _seekWhenOpened = null;
            if (_backend.Duration is not TimeSpan duration) return;
            SetSliderMaximum(duration);
            TxtTotalTime.Text = EditText.FormatTime(duration);
            if (_current != null && !_playingPreview && _current.Duration == null)
            {
                _current.Duration = duration;
                UpdateListState();
            }
            UpdateTrimBand();
        }

        /// <summary>A clip played to its end point (or the file's end) - go on to the next ticked clip, like the video will.</summary>
        private void Backend_Ended()
        {
            if (_playingPreview || _current == null)
            {
                _isPlaying = false;
                IconPlayPause.Kind = PackIconKind.Play;
                return;
            }
            var order = _clips.Where(c => c.IsIncluded || ReferenceEquals(c, _current)).ToList();
            int index = order.IndexOf(_current);
            if (index >= 0 && index + 1 < order.Count)
            {
                _ = LoadAndPlayAsync(order[index + 1]);
                return;
            }
            Pause();
        }

        private void Backend_Failed(string reason)
        {
            if (_playingPreview)
            {
                FailCurrent(reason);
                return;
            }
            var clip = _current;
            if (clip == null) return;
            bool alreadyCopy = _currentSource != null && _currentSource.StartsWith(VideoReplayManager.PreviewFolder, StringComparison.OrdinalIgnoreCase);
            // Windows' player: one retry through a preview copy for any format it refuses.
            if (!_backend.PlaysEverything && !alreadyCopy && VideoReplayManager.FfmpegExe != null)
            {
                _ = LoadAndPlayAsync(clip, forcePreviewCopy: true);
                return;
            }
            FailCurrent(reason);
        }

        /// <summary>Says why in a banner and moves on - until every clip has failed in a row.</summary>
        private void FailCurrent(string reason)
        {
            string name = _playingPreview ? "the preview" : _current?.Name ?? "this clip";
            _consecutiveFailures++;
            _positionTimer.Stop();
            _backend.Stop();
            _currentSource = null;
            ShowSurface(false);
            _isPlaying = false;
            IconPlayPause.Kind = PackIconKind.Play;
            ShowNotice($"Couldn't play {(_playingPreview ? name : $"\"{name}\"")}: {reason.TrimEnd('.')}.");

            var failed = _current;
            if (failed != null && _consecutiveFailures < _clips.Count)
            {
                var order = _clips.Where(c => c.IsIncluded || ReferenceEquals(c, failed)).ToList();
                int index = order.IndexOf(failed);
                if (index >= 0 && index + 1 < order.Count)
                {
                    _ = LoadAndPlayAsync(order[index + 1]);
                    return;
                }
            }
            _consecutiveFailures = 0;
            SetCurrent(null);
            _playingPreview = false;
            NoClipPanel.Visibility = Visibility.Visible;
            UpdateNowPlaying();
        }

        private void ShowConverting(string title, string text, bool indeterminate)
        {
            TxtConvertTitle.Text = title;
            TxtConvert.Text = text;
            ProgressConvert.IsIndeterminate = indeterminate;
            UiMotion.FadeVisibility(ConvertingPanel, true);
        }

        private void ShowNotice(string text)
        {
            TxtError.Text = text;
            if (ErrorBanner.Visibility != Visibility.Visible) UiMotion.FadeVisibility(ErrorBanner, true);
        }

        private void HideNotice() => ErrorBanner.Visibility = Visibility.Collapsed;

        private void BtnDismissError_Click(object sender, RoutedEventArgs e) => HideNotice();

        private void UpdateNowPlaying()
        {
            if (_playingPreview)
            {
                TxtNowPlaying.Text = "Preview of the stitched video";
                TxtTrimInfo.Text = "A small, quick version - the real one keeps the full picture size.";
            }
            else if (_current is { } clip)
            {
                TxtNowPlaying.Text = $"{clip.Number}. {clip.Name}";
                TxtTrimInfo.Text = clip.IsTrimmed
                    ? $"Uses {clip.TrimText} · press Start / End (or I / O) at another spot to change it"
                    : "Uses the whole clip · pause where it should start or end and press Start / End (I / O)";
            }
            else
            {
                TxtNowPlaying.SetResourceReference(TextBlock.TextProperty, "Str_Common_NothingLoaded");
                TxtTrimInfo.Text = "Add clips.";
            }
            bool canTrim = _current != null && !_playingPreview && !_isBusy;
            BtnSetStart.IsEnabled = canTrim;
            BtnSetEnd.IsEnabled = canTrim;
            BtnClearTrim.Visibility = canTrim && _current!.IsTrimmed ? Visibility.Visible : Visibility.Collapsed;
            UpdateTrimBand();
        }

        // ---------------- Seeking ----------------

        private void BtnSkipBack_Click(object sender, RoutedEventArgs e) => SeekBy(TimeSpan.FromSeconds(-10));

        private void BtnSkipForward_Click(object sender, RoutedEventArgs e) => SeekBy(TimeSpan.FromSeconds(10));

        private void SeekBy(TimeSpan delta)
        {
            if (!_backend.HasMedia) return;
            SeekTo(VideoReplayManager.Seek(CurrentPosition(), delta, _backend.Duration));   // from the last seek's spot, so quick presses add up
            UpdatePosition();
        }

        private void SeekTo(TimeSpan position)
        {
            _backend.Position = position;
            _seekTarget = position;
            _seekIssuedAt = Environment.TickCount64;
            _lastPosition = position;
        }

        /// <summary>The player's position - or, while a seek is still on its way, where it's going.</summary>
        private TimeSpan CurrentPosition()
        {
            var actual = _backend.Position;
            if (_seekTarget is not TimeSpan target) return actual;
            bool arrived = (actual - target).Duration() < TimeSpan.FromSeconds(1.5);
            if (arrived || Environment.TickCount64 - _seekIssuedAt > 5000)   // give up waiting after 5 s
            {
                _seekTarget = null;
                return actual;
            }
            return target;
        }

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            _backend.Poll();   // VLC reports opened / ended / failed this way
            // Playing through a trimmed clip's end point goes on to the next clip, like the finished
            // video. Only when playback crosses it - seeking past it (to move it) doesn't.
            if (_backend.HasMedia)
            {
                var now = CurrentPosition();
                bool crossed = _isPlaying && !_playingPreview && _current?.TrimEnd is TimeSpan end
                               && _lastPosition < end && now >= end && now - _lastPosition < TimeSpan.FromSeconds(2);
                _lastPosition = now;
                if (crossed)
                {
                    Backend_Ended();
                    return;
                }
            }
            UpdatePosition();
        }

        private void UpdatePosition()
        {
            if (_isUserSeeking || !_backend.HasMedia) return;
            var position = CurrentPosition();
            if (_backend.Duration is TimeSpan d)
            {
                if (Math.Abs(SliderPosition.Maximum - d.TotalSeconds) > 0.5)
                {
                    SetSliderMaximum(d);
                    TxtTotalTime.Text = EditText.FormatTime(d);
                    UpdateTrimBand();
                }
            }
            else
            {
                TxtTotalTime.Text = "--:--";   // no length stored in the file (common for browser-recorded WebM)
            }
            _updatingSlider = true;
            SliderPosition.Value = Math.Min(SliderPosition.Maximum, position.TotalSeconds);
            _updatingSlider = false;
            TxtCurrentTime.Text = EditText.FormatTime(position);
        }

        /// <summary>A shorter length clamps the thumb - that's not the user seeking, so it mustn't move the player.</summary>
        private void SetSliderMaximum(TimeSpan duration)
        {
            _updatingSlider = true;
            SliderPosition.Maximum = Math.Max(1, duration.TotalSeconds);
            _updatingSlider = false;
        }

        private void SliderPosition_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isUserSeeking = true;

        private void SliderPosition_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
            if (_backend.HasMedia) SeekTo(TimeSpan.FromSeconds(SliderPosition.Value));
        }

        /// <summary>While dragging, the time follows the thumb and the picture follows a few times a second.</summary>
        private void SliderPosition_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _updatingSlider) return;
            TxtCurrentTime.Text = EditText.FormatTime(TimeSpan.FromSeconds(e.NewValue));
            if (!_backend.HasMedia) return;
            if (!_isUserSeeking)
            {
                SeekTo(TimeSpan.FromSeconds(e.NewValue));
            }
            else if (Environment.TickCount64 - _lastDragSeekAt >= 200)   // not every pixel - each seek in a long file is slow
            {
                _lastDragSeekAt = Environment.TickCount64;
                SeekTo(TimeSpan.FromSeconds(e.NewValue));
            }
        }

        // ---------------- Volume ----------------

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
            if (Ignore) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e) => ToggleMute();

        private void ToggleMute()
        {
            _muted = !_muted;
            _backend.SetMute(_muted);
            UpdateMuteIcon();
            SaveSettingsQuietly();
        }

        private void UpdateMuteIcon() =>
            IconMute.Kind = _muted || SliderVolume.Value <= 0 ? PackIconKind.VolumeOff : PackIconKind.VolumeHigh;

        // =================================================================
        //  Trims
        // =================================================================

        private void BtnSetStart_Click(object sender, RoutedEventArgs e) => SetTrim(start: true);

        private void BtnSetEnd_Click(object sender, RoutedEventArgs e) => SetTrim(start: false);

        private void SetTrim(bool start)
        {
            if (_current == null || _playingPreview || _isBusy || !_backend.HasMedia) return;
            if (_current.Duration == null && _backend.Duration is TimeSpan d) _current.Duration = d;
            var position = CurrentPosition();   // I / O straight after a seek marks the spot sought to
            string? problem = start ? VideoEditorManager.SetTrimStart(_current, position) : VideoEditorManager.SetTrimEnd(_current, position);
            if (problem != null)
            {
                ShowNotice(problem);
                return;
            }
            HideNotice();
            UiMotion.Pop(start ? BtnSetStart : BtnSetEnd, 0.9);
            UpdateNowPlaying();
            UpdateListState();
        }

        private void BtnClearTrim_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _isBusy) return;
            VideoEditorManager.ClearTrim(_current);
            UpdateNowPlaying();
            UpdateListState();
        }

        private void ScrubberHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTrimBand();

        /// <summary>Shades the used part of the clip under the scrubber.</summary>
        private void UpdateTrimBand()
        {
            var clip = _current;
            double total = _backend.Duration?.TotalSeconds ?? clip?.Duration?.TotalSeconds ?? 0;
            if (clip == null || _playingPreview || !clip.IsTrimmed || total <= 0)
            {
                TrimBand.Visibility = Visibility.Collapsed;
                return;
            }
            const double thumbRadius = 6;   // the track runs from the thumb's centre at each end
            double track = Math.Max(0, ScrubberHost.ActualWidth - thumbRadius * 2);
            double start = Math.Clamp((clip.TrimStart?.TotalSeconds ?? 0) / total, 0, 1);
            double end = Math.Clamp((clip.TrimEnd?.TotalSeconds ?? total) / total, 0, 1);
            TrimBand.Margin = new Thickness(thumbRadius + start * track, 0, 0, 0);
            TrimBand.Width = Math.Max(2, (end - start) * track);
            TrimBand.Visibility = Visibility.Visible;
        }

        // =================================================================
        //  Adding clips
        // =================================================================

        // ---- The Files card (same as Validate Data / Convert Files / Images to PDF) ----

        /// <summary>The Clear button on the Files card - same as Clear all above the list.</summary>
        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_isBusy) _ = AddPathsAsync(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearList();

        /// <summary>
        /// Like the other file tools: changing "Include subfolders" also updates
        /// the folders already added (it used to apply only to the next folder).
        /// Clips already in order keep their place, tick and trim.
        /// </summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            OnOptionChanged();
            _ = RescanFoldersAsync(InputCard.IncludeSubfolders);
        }

        private async Task RescanFoldersAsync(bool includeSubfolders)
        {
            var folders = _groups.Where(g => g.IsFolder && g.FolderPath != null && g.IncludeSubfolders != includeSubfolders).ToList();
            if (_isBusy || folders.Count == 0) return;
            var token = _lifetimeCts.Token;
            _adding++;
            UpdateButtons();
            TxtProgress.Text = includeSubfolders ? "Looking in subfolders…" : "Leaving out subfolders…";
            try
            {
                var added = new List<EditClip>();
                int removed = 0, inaccessible = 0;
                foreach (var group in folders)
                {
                    var scan = await VideoEditorManager.ScanFolderAsync(group.FolderPath!, includeSubfolders, token);
                    if (scan.Missing) continue;   // folder gone - leave its clips alone
                    inaccessible += scan.InaccessibleFolders;
                    var (a, r) = VideoEditorManager.ApplyRescan(_clips, _groups, group, scan.Files, includeSubfolders);
                    if (_current != null && r.Contains(_current)) StopPlayback();
                    added.AddRange(a);
                    removed += r.Count;
                }
                UpdateListState();
                UpdateNowPlaying();
                if (added.Count + removed > 0) InputCard.PopDropArea();
                string text = includeSubfolders
                    ? (added.Count > 0 ? $"Added {added.Count:N0} {EditText.Plural(added.Count, "clip", "clips")} from subfolders, at the end of the list." : "No videos in the subfolders.")
                    : (removed > 0 ? $"Took {removed:N0} {EditText.Plural(removed, "subfolder clip", "subfolder clips")} off the list." : "No subfolder clips were in the list.");
                if (inaccessible > 0) text += $" {inaccessible:N0} {EditText.Plural(inaccessible, "folder", "folders")} couldn't be opened.";
                if (!_isBusy) TxtProgress.Text = text;
                if (added.Count > 0) _ = ReadLengthsAsync(added, token);
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex)
            {
                if (!_isBusy) TxtProgress.Text = "Couldn't read the folders again: " + ex.Message;
            }
            finally
            {
                _adding--;
                UpdateButtons();
            }
        }

        /// <summary>Drops land anywhere on the page; the sidebar's drop area lights up. (VLC's window can't have anything drawn over it, so there's no page overlay.)</summary>
        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isBusy && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) InputCard.SetDropHighlight(true);
            e.Handled = true;
        }

        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                InputCard.SetDropHighlight(false);
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            InputCard.SetDropHighlight(false);
            if (!_isBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        /// <summary>
        /// Adds clips to the end of the list, reading folders off the UI
        /// thread (the old view read them on the UI thread, and one locked
        /// subfolder dropped the whole folder with a dialog). Lengths are
        /// read in the background so the total shows straight away.
        /// </summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isBusy || paths.Count == 0) return;
            var token = _lifetimeCts.Token;
            bool subfolders = InputCard.IncludeSubfolders;
            _adding++;
            UpdateButtons();
            TxtProgress.Text = "Looking for videos…";
            try
            {
                int added = 0, inaccessible = 0, skipped = 0;
                string? firstName = null;
                var newClips = new List<EditClip>();
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var scan = await VideoEditorManager.ScanFolderAsync(folder, subfolders, token);
                    inaccessible += scan.InaccessibleFolders;
                    var group = VideoEditorManager.AddToList(_clips, _groups, scan.Files, folder, isFolder: true, subfolders);
                    if (group.Count > 0) firstName ??= Path.GetFileName(folder.TrimEnd('\\', '/'));
                    newClips.AddRange(group);
                }
                var files = paths.Where(p => !Directory.Exists(p)).ToList();
                if (files.Count > 0)
                {
                    var (found, notVideos) = await VideoEditorManager.ReadFilesAsync(files, token);
                    skipped += notVideos;
                    newClips.AddRange(VideoEditorManager.AddToList(_clips, _groups, found, null, isFolder: false, includeSubfolders: false));
                }
                added = newClips.Count;

                if (firstName != null) SuggestFileName(firstName);
                if (added > 0) InputCard.PopDropArea();
                var parts = new List<string>
                {
                    added == 0 ? "No new clips found - they may already be in the list." : $"Added {added:N0} {EditText.Plural(added, "clip", "clips")}.",
                };
                if (inaccessible > 0) parts.Add($"{inaccessible:N0} {EditText.Plural(inaccessible, "folder", "folders")} couldn't be opened.");
                if (skipped > 0) parts.Add($"{skipped:N0} {EditText.Plural(skipped, "file wasn't a video", "files weren't videos")} or couldn't be found.");
                if (!_isBusy) TxtProgress.Text = string.Join(" ", parts);
                _ = ReadLengthsAsync(newClips, token);
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex)
            {
                if (!_isBusy) TxtProgress.Text = "Couldn't add the clips: " + ex.Message;
            }
            finally
            {
                _adding--;
                UpdateButtons();
            }
        }

        /// <summary>Clip lengths from ffprobe, a few at a time (the old list only knew lengths of clips it had played).</summary>
        private async Task ReadLengthsAsync(IReadOnlyList<EditClip> clips, CancellationToken token)
        {
            var pending = clips.Where(c => c.Duration == null && !c.DurationRequested).ToList();
            foreach (var clip in pending) clip.DurationRequested = true;
            var tasks = pending.Select(async clip =>
            {
                try
                {
                    var length = await VideoReplayManager.ProbeDurationAsync(clip.FullPath, token);
                    if (length != null && clip.Duration == null) await Dispatcher.InvokeAsync(() => clip.Duration = length);
                }
                catch (OperationCanceledException)
                {
                    // closing
                }
                catch (Exception)
                {
                    // unknown length - the stitch reads it again and says why if it can't
                }
            });
            await Task.WhenAll(tasks);
            if (!token.IsCancellationRequested) UpdateListState();
        }

        /// <summary>Names the video after the first folder added, unless the user typed a name.</summary>
        private void SuggestFileName(string folderName)
        {
            if (_fileNameEdited || string.IsNullOrWhiteSpace(folderName)) return;
            SetFileName(VideoEditorManager.CleanFileName(folderName));
        }

        private void SetFileName(string name)
        {
            _settingFileName = true;
            TxtOutputFileName.Text = name;
            _settingFileName = false;
        }

        private void TxtOutputFileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_settingFileName) _fileNameEdited = !string.IsNullOrWhiteSpace(TxtOutputFileName.Text);
        }

        // =================================================================
        //  The list: tick, order, remove
        // =================================================================

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e) => UpdateListState();

        private void ChkIncludeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            bool include = ChkIncludeAll.IsChecked == true;
            foreach (var clip in _clips) clip.IsIncluded = include;
            UpdateListState();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => SetAllIncluded(true);

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e) => SetAllIncluded(false);

        private void SetAllIncluded(bool include)
        {
            if (_isBusy) return;
            foreach (var clip in _clips) clip.IsIncluded = include;
            UpdateListState();
        }

        private List<EditClip> HighlightedClips() => DataGridQueue.SelectedItems.OfType<EditClip>().ToList();

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            if (_isBusy) return;
            var chosen = HighlightedClips();
            if (!VideoEditorManager.Move(_clips, chosen, direction)) return;
            Reselect(chosen);
            UpdateNowPlaying();   // the clip number in the title may have changed
        }

        /// <summary>Puts the highlight back after rows moved (replacing a row drops it) and keeps them in view.</summary>
        private void Reselect(IReadOnlyList<EditClip> chosen)
        {
            DataGridQueue.SelectedItems.Clear();
            foreach (var clip in chosen) DataGridQueue.SelectedItems.Add(clip);
            var first = chosen.OrderBy(c => c.Number).FirstOrDefault();
            if (first != null)
            {
                DataGridQueue.ScrollIntoView(first);
                DataGridQueue.CurrentItem = first;
            }
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_isBusy || _clips.Count < 2) return;
            var chosen = HighlightedClips();
            var sorted = VideoEditorManager.Sorted(_clips, byDate);
            _clips.Clear();
            foreach (var clip in sorted) _clips.Add(clip);
            VideoEditorManager.Renumber(_clips);
            Reselect(chosen);
            UpdateNowPlaying();
            TxtProgress.Text = byDate ? "Clips sorted by date, oldest first." : "Clips sorted by name.";
        }

        private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy && sender is Button { Tag: EditClip clip }) RemoveClips(new[] { clip });
        }

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy && sender is Button { Tag: EditClipGroup group }) RemoveClips(group.Items.ToList());
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy) RemoveClips(HighlightedClips());
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearList();

        private void ClearList()
        {
            if (_isBusy || _clips.Count == 0) return;
            StopPlayback();
            _clips.Clear();
            _groups.Clear();
            ResetResult();
            TxtProgress.Text = "Add clips, put them in order, then stitch.";
        }

        private void RemoveClips(IReadOnlyCollection<EditClip> clips)
        {
            if (clips.Count == 0) return;
            if (_current != null && clips.Contains(_current)) StopPlayback();
            var gone = new HashSet<EditClip>(clips);
            int nextIndex = _clips.TakeWhile(c => !gone.Contains(c)).Count();
            VideoEditorManager.RemoveFromList(_clips, _groups, clips);
            if (_clips.Count > 0)
            {
                var next = _clips[Math.Clamp(nextIndex, 0, _clips.Count - 1)];
                DataGridQueue.SelectedItem = next;
                DataGridQueue.CurrentItem = next;
            }
            UpdateNowPlaying();
        }

        private void DataGridQueue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is ButtonBase) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Delete)
            {
                if (!_isBusy) RemoveClips(HighlightedClips());
                e.Handled = true;
            }
            else if (key == Key.Enter && DataGridQueue.SelectedItem is EditClip clip)
            {
                _ = LoadAndPlayAsync(clip);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
            {
                MoveHighlighted(key == Key.Up ? -1 : +1);
                e.Handled = true;
            }
        }

        private void UpdateListState()
        {
            int count = _clips.Count;
            UiMotion.FadeVisibility(EmptyStatePanel, count == 0);
            UiMotion.FadeVisibility(DataGridQueue, count > 0);
            if (count > 0 && SummaryPill.Visibility != Visibility.Visible) UiMotion.Pop(SummaryPill, 0.9);
            SummaryPill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryCount.Text = VideoEditorManager.DescribeList(_clips);
            int included = _clips.Count(c => c.IsIncluded);
            ChkIncludeAll.IsChecked = count == 0 || included == count ? true : included == 0 ? false : null;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool idle = !_isBusy;
            bool hasHighlight = DataGridQueue.SelectedItems.Count > 0;
            BtnMoveUp.IsEnabled = idle && hasHighlight;
            BtnMoveDown.IsEnabled = idle && hasHighlight;
            BtnRemoveSelected.IsEnabled = idle && hasHighlight;
            BtnSortByName.IsEnabled = idle && _clips.Count > 1;
            BtnSortByDate.IsEnabled = idle && _clips.Count > 1;
            BtnClearAll.IsEnabled = idle && _clips.Count > 0;
            int ticked = _clips.Count(c => c.IsIncluded);
            BtnSelectAll.IsEnabled = idle && _clips.Count > 0 && ticked < _clips.Count;
            BtnDeselectAll.IsEnabled = idle && ticked > 0;
            bool anyTicked = _clips.Any(c => c.IsIncluded);
            // One run at a time: while one runs, its own button is Cancel and the other waits.
            BtnRunAction.IsEnabled = _runKind == RunKind.Stitch || (idle && _adding == 0 && anyTicked);
            BtnPreviewStitch.IsEnabled = _runKind == RunKind.Preview || (idle && _adding == 0 && anyTicked);
        }

        // =================================================================
        //  Output folder (this session only)
        // =================================================================

        private string OutputFolder => OutputCard.EffectiveFolder ?? VideoEditorManager.DefaultFolder;

        private void BuildOutputOptions()
        {
            TxtOutputFileName = OutputCardOptions.TextBox("Stitched video",
                "No need for an extension - it comes from the format. A name that's taken gets \" (2)\".");
            TxtOutputFileName.TextChanged += TxtOutputFileName_TextChanged;
            AutoSaveCheckBox = OutputCardOptions.CheckBox("Save without asking", true,
                "Save straight into the folder above. Off: pick where to save each time.");
            AutoSaveCheckBox.Checked += Option_Changed;
            AutoSaveCheckBox.Unchecked += Option_Changed;
            OutputCard.Options = OutputCardOptions.Panel(
                OutputCardOptions.Label("File name"), TxtOutputFileName, AutoSaveCheckBox);
        }

        // =================================================================
        //  Stitch / Preview
        // =================================================================

        private enum RunKind { None, Stitch, Preview }

        private RunKind _runKind;

        private async void BtnRunAction_Click(object sender, RoutedEventArgs e)
        {
            if (_runKind == RunKind.Stitch)
            {
                CancelRun();
                return;
            }
            var snapshot = _clips.ToList();
            if (!snapshot.Any(c => c.IsIncluded))
            {
                TxtProgress.Text = "Tick at least one clip.";
                return;
            }
            _settings = ReadSettings();
            if (_settings.SaveWithoutAsking && OutputCard.Status.IsError)
            {
                TxtProgress.Text = "Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Save to.";
                return;
            }

            string folder = OutputFolder;
            string fileName = TxtOutputFileName.Text;
            bool overwriteAllowed = false;
            if (!_settings.SaveWithoutAsking)
            {
                string ext = VideoEditorManager.Extension(_settings.Format);
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save the video",
                    Filter = $"{ext.TrimStart('.').ToUpperInvariant()} video|*{ext}",
                    DefaultExt = ext,
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = VideoEditorManager.CleanFileName(fileName) + ext,
                };
                if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
                if (dialog.ShowDialog() != true) return;
                folder = Path.GetDirectoryName(dialog.FileName)!;
                fileName = Path.GetFileNameWithoutExtension(dialog.FileName);
                overwriteAllowed = true;
            }

            var options = new EditOptions
            {
                OutputFolder = folder,
                FileName = fileName,
                OverwriteAllowed = overwriteAllowed,
                Format = _settings.Format,
                Size = _settings.Size,
                FadeSeconds = _settings.FadeSeconds,
                SplitSeconds = _settings.SplitMinutes * 60.0,
            };

            var cts = BeginRun(RunKind.Stitch);
            ResetResult();
            try
            {
                var result = await VideoEditorManager.RunAsync(snapshot, options, RunProgress(cts), cts.Token);
                ShowResult(result);
            }
            catch (Exception ex)
            {
                ProgressEdit.Value = 0;
                TxtProgressPercent.Text = "";
                TxtProgress.Text = "The video couldn't be saved: " + ex.Message.Split('\n')[0];
                MessageBox.Show(Loc.F("Str_MB_TheVideoCouldnT", "The video couldn't be saved.\n\n{0}\n\nCheck that the folder exists, you can write to it, and there's enough free space.", ex.Message),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                EndRun(cts);
            }
        }

        private async void BtnPreviewStitch_Click(object sender, RoutedEventArgs e)
        {
            if (_runKind == RunKind.Preview)
            {
                CancelRun();
                return;
            }
            var snapshot = _clips.ToList();
            if (!snapshot.Any(c => c.IsIncluded))
            {
                TxtProgress.Text = "Tick at least one clip.";
                return;
            }
            StopPlayback();   // the old preview file may still be open in the player
            var cts = BeginRun(RunKind.Preview);
            try
            {
                string path = await VideoEditorManager.RenderPreviewAsync(snapshot, ReadSettings().FadeSeconds, RunProgress(cts), cts.Token);
                ProgressEdit.Value = 0;
                TxtProgressPercent.Text = "";
                TxtProgress.Text = "Playing the preview - it shows the order, trims and fades at a small size.";
                await PlayPreviewFileAsync(path);
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Preview cancelled.";
                ProgressEdit.Value = 0;
                TxtProgressPercent.Text = "";
            }
            catch (Exception ex)
            {
                ProgressEdit.Value = 0;
                TxtProgressPercent.Text = "";
                TxtProgress.Text = "No preview: " + ex.Message;
                ShowNotice("No preview could be made: " + ex.Message);
            }
            finally
            {
                EndRun(cts);
            }
        }

        private IProgress<EditProgress> RunProgress(CancellationTokenSource cts) => new Progress<EditProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            ProgressEdit.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:0}%";
            TxtProgress.Text = p.Text;
        });

        private CancellationTokenSource BeginRun(RunKind kind)
        {
            var cts = new CancellationTokenSource();
            _runCts = cts;
            _runKind = kind;
            _isBusy = true;
            OptionsScrollViewer.IsEnabled = false;
            OutputCard.IsLocked = true;
            ChkIncludeAll.IsEnabled = false;
            if (kind == RunKind.Stitch)
            {
                IconRun.Kind = PackIconKind.Close;
                TxtRunLabel.Text = "Cancel";
            }
            else
            {
                IconPreview.Kind = PackIconKind.Close;
                TxtPreviewLabel.Text = "Cancel preview";
            }
            ProgressEdit.Value = 0;
            TxtProgressPercent.Text = "0%";
            UpdateButtons();
            UpdateNowPlaying();
            return cts;
        }

        private void CancelRun()
        {
            _runCts?.Cancel();
            TxtProgress.Text = "Cancelling…";
            BtnRunAction.IsEnabled = BtnPreviewStitch.IsEnabled = false;
        }

        private void EndRun(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_runCts, cts)) _runCts = null;
            cts.Dispose();
            _runKind = RunKind.None;
            _isBusy = false;
            OptionsScrollViewer.IsEnabled = true;
            OutputCard.IsLocked = false;
            OutputCard.Refresh();   // free space, or "made on the first save" is now "ready"
            ChkIncludeAll.IsEnabled = true;
            IconRun.Kind = PackIconKind.MovieEditOutline;
            TxtRunLabel.Text = "Stitch videos";
            IconPreview.Kind = PackIconKind.MovieOpenPlayOutline;
            TxtPreviewLabel.Text = "Preview";
            UpdateListState();
            UpdateNowPlaying();
        }

        private void ShowResult(EditResult result)
        {
            TxtProgress.Text = VideoEditorManager.DescribeResult(result);
            _lastFiles = result.Files;
            bool any = result.Files.Count > 0;
            BtnOpenResult.IsEnabled = any;
            BtnShowInFolder.IsEnabled = any;
            BtnOpenResult.ToolTip = result.Files.Count > 1 ? "Open the first file" : "Open the video";
            ProgressEdit.Value = any ? 100 : 0;
            TxtProgressPercent.Text = any ? "100%" : "";
            if (any)
            {
                UiMotion.Pop(BtnOpenResult, 0.9);
                try
                {
                    ToastNotificationManager.ShowToast("Video ready", TxtProgress.Text);
                }
                catch (Exception)
                {
                    // a toast is a nicety
                }
            }
            else if (!result.Cancelled && result.Error != null)
            {
                MessageBox.Show(result.Error, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ResetResult()
        {
            _lastFiles = new List<string>();
            BtnOpenResult.IsEnabled = false;
            BtnShowInFolder.IsEnabled = false;
            foreach (var clip in _clips)
            {
                clip.Status = EditClipStatus.Ready;
                clip.Message = "";
            }
        }

        private void BtnOpenResult_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFiles.Count == 0) return;
            string file = _lastFiles[0];
            try
            {
                if (!File.Exists(file))
                {
                    TxtProgress.Text = "The video is no longer there.";
                    return;
                }
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Couldn't open the video: " + ex.Message;
            }
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFiles.Count == 0) return;
            string file = _lastFiles[0];
            try
            {
                if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
                else if (Directory.Exists(Path.GetDirectoryName(file))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(file)}\"");
                else TxtProgress.Text = "The video is no longer there.";
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Couldn't open the folder: " + ex.Message;
            }
        }

        // =================================================================
        //  Keyboard
        // =================================================================

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Space when e.OriginalSource is not ButtonBase:
                    TogglePlay();
                    break;
                case Key.Left when e.OriginalSource is not DataGridCell:
                    SeekBy(TimeSpan.FromSeconds(shift ? -1 : -5));
                    break;
                case Key.Right when e.OriginalSource is not DataGridCell:
                    SeekBy(TimeSpan.FromSeconds(shift ? 1 : 5));
                    break;
                case Key.I:
                    SetTrim(start: true);
                    break;
                case Key.O:
                    SetTrim(start: false);
                    break;
                case Key.N:
                    PlayNeighbour(+1);
                    break;
                case Key.P:
                    PlayNeighbour(-1);
                    break;
                case Key.M:
                    ToggleMute();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private static string? CheckedTag(params RadioButton[] buttons) => buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as string;

        private static void CheckByTag(string tag, params RadioButton[] buttons)
        {
            var match = buttons.FirstOrDefault(b => b.Tag as string == tag) ?? buttons.FirstOrDefault(b => b.IsChecked == true) ?? buttons[0];
            match.IsChecked = true;
        }

        private static T ParseTag<T>(string? tag, T fallback) where T : struct, Enum =>
            Enum.TryParse(tag, out T value) && Enum.IsDefined(value) ? value : fallback;

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
    }
}
