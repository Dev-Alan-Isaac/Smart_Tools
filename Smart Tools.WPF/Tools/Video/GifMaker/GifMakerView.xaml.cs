using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// GIF Maker - UI only. Sizes, filter graphs, checks, the image list and
    /// settings are in <see cref="GifMakerManager"/>; ffmpeg/ffprobe in
    /// GifMakerManager.Run.cs; frame images and the animated preview
    /// (<see cref="GifAnimator"/>) in GifMakerManager.Render.cs.
    /// </summary>
    public partial class GifMakerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "GIF Maker";

        /// <summary>A new video starts with this much selected.</summary>
        private const double DefaultSelectionSeconds = 5;

        // Video mode
        private GifVideoInfo? _video;
        private CancellationTokenSource? _videoCts;
        private CancellationTokenSource? _frameCts;
        private double _frameAt;
        private bool _frameIsEnd;
        private bool _settingSliders;

        // Images mode
        private readonly List<GifImageSource> _sources = new();
        private readonly ObservableCollection<GifImageGroup> _queueDisplay = new();
        private readonly ObservableCollection<GifImageItem> _images = new();
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _expandCts;
        private CancellationTokenSource _probeCts = new();
        private int _probing;

        // Estimate, run, result
        private CancellationTokenSource? _estimateCts;
        private CancellationTokenSource? _runCts;
        private GifAnimator? _animator;
        private GifResult? _result;
        private bool _playing;

        private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
        private readonly DispatcherTimer _estimateTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private readonly DispatcherTimer _playTimer = new(DispatcherPriority.Render);

        // Video mode: the selected part loops in the stage as a small GIF
        private bool _loopEnabled = true;
        private GifAnimator? _loopAnimator;
        private CancellationTokenSource? _loopCts;      // set only while a loop is being made
        private string? _loopKey;                       // what the shown (or being made) loop depends on
        private System.Windows.Media.ImageSource? _stillFrame;   // the trim bar's last frame grab, shown when no loop plays
        private string _stillLabel = "";
        private readonly DispatcherTimer _loopRenderTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private readonly DispatcherTimer _loopTimer = new(DispatcherPriority.Render);

        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _fileNameEdited;

        /// <summary>The run bar shows a result ("Saved…", "Cancelled…"): keep it until the user changes something.</summary>
        private bool _holdStatus;
        private readonly bool _viewConstructed;

        public GifMakerView()
        {
            InitializeComponent();
            TrimTimeline.SelectionChanged += Timeline_SelectionChanged;
            TrimTimeline.ViewChanged += UpdateZoomText;
            InputCard.ItemsSource = _queueDisplay;
            InputCard.FileFilter = $"Images|{string.Join(";", GifMakerManager.ImageExtensions.Select(x => "*" + x))}|All files (*.*)|*.*";
            DataGridFrames.ItemsSource = _images;
            OutputCard.DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Smart Tools", "GIF Maker");
            OutputCard.FileNameEdited += (_, _) => _fileNameEdited = !string.IsNullOrWhiteSpace(OutputCard.FileName);
            _frameTimer.Tick += (_, _) =>
            {
                _frameTimer.Stop();
                _ = GrabFrameAsync();
            };
            _estimateTimer.Tick += (_, _) =>
            {
                _estimateTimer.Stop();
                _ = EstimateAsync();
            };
            _playTimer.Tick += PlayTimer_Tick;
            _loopRenderTimer.Tick += (_, _) =>
            {
                _loopRenderTimer.Stop();
                _ = RenderLoopAsync();
            };
            _loopTimer.Tick += LoopTimer_Tick;
            UpdateLoopButton();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops ffmpeg/ffprobe and the preview.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _videoCts?.Cancel();
            _frameCts?.Cancel();
            _estimateCts?.Cancel();
            _expandCts?.Cancel();
            _probeCts.Cancel();
            _frameTimer.Stop();
            _estimateTimer.Stop();
            _playTimer.Stop();
            _loopRenderTimer.Stop();
            _loopTimer.Stop();
            _loopCts?.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(GifMakerManager.LoadSettings());
                _ = Task.Run(GifMakerManager.SweepWorkFolders);   // leftovers from a crash
            }
            UpdateEngineWarning();
            ApplyMode(animate: false);
            if (_animator != null && _playing) _playTimer.Start();
            if (_loopAnimator != null) _loopTimer.Start();
        }

        /// <summary>The page was left: the previews stop ticking (they start again on return).</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            _playTimer.Stop();
            _loopTimer.Stop();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void UpdateEngineWarning()
        {
            string? missing = GifMakerManager.MissingEngine();
            TxtFfmpegWarning.Text = missing ?? "";
            FfmpegWarning.Visibility = missing == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Mode
        // =================================================================

        private GifSourceMode Mode => CheckedTag(ModeChips, GifSourceMode.Video);

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            ApplyMode(animate: true);
            SaveSettings();
        }

        private void SetMode(GifSourceMode mode)
        {
            if (Mode == mode) return;
            CheckTag(ModeChips, mode.ToString());
            ApplyMode(animate: true);
            SaveSettings();
        }

        private void ApplyMode(bool animate)
        {
            bool video = Mode == GifSourceMode.Video;
            VideoCard.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            InputCard.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
            PanelVideoSettings.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            PanelImageSettings.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
            PanelClip.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            PanelFrames.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
            SourceHeader.SetResourceReference(ContentProperty, video ? "Str_GifMaker_ClipHeader" : "Str_GifMaker_FramesHeader");
            SourceHeader.Tag = video ? PackIconKind.MovieOpenOutline : PackIconKind.ImageMultipleOutline;
            if (animate) UiMotion.FadeIn(video ? PanelClip : PanelFrames);

            UpdateClipUi();
            UpdateFramesUi();
            UpdateQualityNote();
            SuggestFileName();
            AfterPlanChanged();
        }

        // =================================================================
        //  Video mode: choosing the video
        // =================================================================

        private void BtnChooseVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            string pattern = string.Join(";", GifMakerManager.VideoExtensions.Select(x => "*" + x));
            var dialog = new OpenFileDialog { Title = "Choose a video", Filter = $"Videos|{pattern}|All files (*.*)|*.*" };
            if (dialog.ShowDialog() == true) _ = LoadVideoAsync(dialog.FileName);
        }

        private void BtnClearVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            _videoCts?.Cancel();
            _frameCts?.Cancel();
            _video = null;
            _stillFrame = null;
            StopLoop();
            ImgFrame.Source = null;
            UpdateClipUi();
            SuggestFileName();
            AfterPlanChanged();
        }

        private void VideoDropArea_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(File.Exists);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) VideoDropArea.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;
        }

        private void VideoDropArea_DragLeave(object sender, DragEventArgs e) =>
            VideoDropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");

        private void VideoDropArea_Drop(object sender, DragEventArgs e)
        {
            VideoDropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");
            e.Handled = true;   // the page overlay doesn't open as well
            if (_isRunning || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            string? file = paths.FirstOrDefault(p => File.Exists(p) && GifMakerManager.IsVideo(p)) ?? paths.FirstOrDefault(File.Exists);
            if (file != null) _ = LoadVideoAsync(file);
        }

        private async Task LoadVideoAsync(string path)
        {
            _videoCts?.Cancel();
            var cts = _videoCts = new CancellationTokenSource();
            SetMode(GifSourceMode.Video);
            TxtVideoHint.Text = "Reading " + Path.GetFileName(path) + "...";
            VideoRow.Visibility = Visibility.Collapsed;
            VideoEmptyHint.Visibility = Visibility.Visible;
            TxtProgress.Text = "Reading the video...";

            (GifVideoInfo? Info, string? Error) probe;
            try
            {
                probe = await GifMakerManager.ProbeVideoAsync(path, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                probe = (null, ex.Message);
            }
            if (cts.IsCancellationRequested) return;
            TxtVideoHint.SetResourceReference(TextBlock.TextProperty, "Str_GifMaker_DropVideoHint");

            if (probe.Info == null)
            {
                UpdateClipUi();
                TxtProgress.Text = $"{Path.GetFileName(path)}: {probe.Error}";
                UiMotion.Pop(VideoDropArea, 0.95);
                return;
            }

            _video = probe.Info;
            double length = _video.Duration.TotalSeconds;
            _settingSliders = true;
            TrimTimeline.Maximum = length;
            TrimTimeline.SetSelection(0, Math.Min(length, DefaultSelectionSeconds));
            double step = length > 600 ? 1 : 0.1;
            TrimTimeline.SmallStep = step;
            TrimTimeline.LargeStep = Math.Max(step, length / 20);
            _settingSliders = false;

            UpdateClipUi();
            UpdateTrimTexts();
            UiMotion.Pop(VideoDropArea, 0.97);
            UiMotion.FadeIn(ClipStage);
            SuggestFileName();
            ScheduleFrame(0, isEnd: false, immediate: true);
            AfterPlanChanged();
        }

        private void UpdateClipUi()
        {
            bool has = _video != null;
            VideoRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            VideoEmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (has)
            {
                TxtVideoName.Text = _video!.Name;
                TxtVideoDetails.Text = _video.Details;
                VideoRow.ToolTip = _video.FullPath;
            }
            ClipStage.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            ClipEmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            TrimPanel.IsEnabled = has && !_isRunning;
            TrimPanel.Opacity = has ? 1 : 0.5;
        }

        // =================================================================
        //  Video mode: the trim bar and frame previews
        // =================================================================

        /// <summary>The timeline keeps the handles at least a tenth of a second apart (pushing the other one), so this only reacts.</summary>
        private void Timeline_SelectionChanged(bool endMoved)
        {
            if (!_viewConstructed || _settingSliders || _video == null) return;
            UpdateTrimTexts();
            ScheduleFrame(endMoved ? TrimTimeline.End : TrimTimeline.Start, isEnd: endMoved);
            AfterPlanChanged();
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => TrimTimeline.ZoomIn();

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => TrimTimeline.ZoomOut();

        private void BtnZoomSelection_Click(object sender, RoutedEventArgs e) => TrimTimeline.ZoomToSelection();

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => TrimTimeline.ZoomReset();

        private void UpdateZoomText() =>
            TxtZoom.Text = string.Create(CultureInfo.InvariantCulture, $"{TrimTimeline.ZoomFactor:0.#}×");

        private void UpdateTrimTexts()
        {
            TxtStartTime.Text = GifText.FormatPrecise(TrimTimeline.Start);
            TxtEndTime.Text = GifText.FormatPrecise(TrimTimeline.End);
            TxtTrimLength.Text = string.Format(CultureInfo.CurrentCulture, TryFindResource("Str_GifMaker_TrimLength") as string ?? "Length {0}", GifText.FormatPrecise(TrimTimeline.End - TrimTimeline.Start));
        }

        /// <summary>Shows the frame under the slider that moved, ~140 ms after it stops (a newer move replaces an older grab).</summary>
        private void ScheduleFrame(double seconds, bool isEnd, bool immediate = false)
        {
            _frameAt = seconds;
            _frameIsEnd = isEnd;
            _frameTimer.Stop();
            if (immediate) _ = GrabFrameAsync();
            else _frameTimer.Start();
        }

        private async Task GrabFrameAsync()
        {
            if (_video == null) return;
            _frameCts?.Cancel();
            var cts = _frameCts = new CancellationTokenSource();
            var video = _video;
            // The very last frame of a file often can't be decoded: step back a little at the end.
            double at = _frameIsEnd ? Math.Max(0, _frameAt - 0.05) : _frameAt;
            bool isEnd = _frameIsEnd;
            FrameLoading.Visibility = Visibility.Visible;
            try
            {
                var png = await GifMakerManager.GrabFrameAsync(video.FullPath, at, 640, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(video, _video)) return;
                var image = GifMakerManager.ToBitmap(png);
                if (image != null) _stillFrame = image;
                _stillLabel = (isEnd ? "End · " : "Start · ") + GifText.FormatPrecise(_frameAt);
                if (_loopAnimator == null)   // a playing loop keeps the stage
                {
                    if (image != null) ImgFrame.Source = image;
                    TxtFrameLabel.Text = _stillLabel;
                }
            }
            catch (OperationCanceledException)
            {
                // a newer grab took over
            }
            catch (Exception ex)
            {
                Log.Info($"GIF Maker: frame grab failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_frameCts, cts) && _loopCts == null) FrameLoading.Visibility = Visibility.Collapsed;
            }
        }

        // ---- the selection, looping ----

        /// <summary>The loop depends on the video, the selection, the speed, the frame rate and the width - nothing else.</summary>
        private string LoopKey(GifOptions o)
        {
            var p = GifMakerManager.LoopPreviewOptions(o);
            return FormattableString.Invariant($"{_video?.FullPath}|{p.StartSeconds:0.###}|{p.EndSeconds:0.###}|{p.Fps}|{p.MaxWidth}|{p.Speed:0.###}");
        }

        /// <summary>
        /// Called whenever something the loop may depend on changes. The old loop gives way to the still frame at
        /// once (the sliders move that), and a new loop is made when things have been still for a moment.
        /// </summary>
        private void ScheduleLoop()
        {
            _loopRenderTimer.Stop();
            if (!_loopEnabled || _video == null || Mode != GifSourceMode.Video || GifMakerManager.MissingEngine() != null)
            {
                StopLoop();
                return;
            }
            if (_isRunning) return;

            var o = BuildOptions();
            if (GifMakerManager.Problem(o, _video, UsableImages) != null)
            {
                StopLoop();
                return;
            }
            if (LoopKey(o) == _loopKey && (_loopAnimator != null || _loopCts != null)) return;   // that loop is showing, or on its way

            StopLoop();
            _loopRenderTimer.Start();
        }

        private async Task RenderLoopAsync()
        {
            var video = _video;
            if (video == null || _isRunning || !_loopEnabled) return;
            var o = BuildOptions();
            _loopCts?.Cancel();
            var cts = _loopCts = new CancellationTokenSource();
            _loopKey = LoopKey(o);
            FrameLoading.Visibility = Visibility.Visible;
            try
            {
                byte[]? bytes = await GifMakerManager.RenderLoopPreviewAsync(video, o, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(video, _video)) return;
                var animator = bytes == null ? null : GifAnimator.FromBytes(bytes);
                if (animator == null)
                {
                    _loopKey = null;
                    return;
                }

                var p = GifMakerManager.LoopPreviewOptions(o);
                _loopAnimator = animator;
                ImgFrame.Source = animator.Bitmap;
                TxtFrameLabel.Text = $"Loop · {GifText.FormatPrecise(p.StartSeconds)} – {GifText.FormatPrecise(p.EndSeconds)}"
                                     + (p.EndSeconds < o.EndSeconds - 0.001 ? $" (first {GifMakerManager.LoopPreviewMaxSeconds:0} s)" : "");
                _loopTimer.Interval = TimeSpan.FromMilliseconds(animator.CurrentDelay);
                _loopTimer.Start();
            }
            catch (OperationCanceledException)
            {
                // a newer selection took over
            }
            catch (Exception ex)
            {
                Log.Info($"GIF Maker: loop preview failed: {ex.Message}");
                if (ReferenceEquals(_loopCts, cts)) _loopKey = null;
            }
            finally
            {
                if (ReferenceEquals(_loopCts, cts))
                {
                    _loopCts = null;
                    FrameLoading.Visibility = Visibility.Collapsed;
                }
                cts.Dispose();
            }
        }

        /// <summary>Stops the loop (and one being made) and gives the stage back to the still frame.</summary>
        private void StopLoop()
        {
            _loopRenderTimer.Stop();
            _loopTimer.Stop();
            bool wasShowing = _loopAnimator != null;
            if (_loopCts != null)
            {
                _loopCts.Cancel();
                _loopCts = null;
                FrameLoading.Visibility = Visibility.Collapsed;
            }
            _loopAnimator = null;
            _loopKey = null;
            if (wasShowing)
            {
                ImgFrame.Source = _stillFrame;
                TxtFrameLabel.Text = _stillLabel;
            }
        }

        private void LoopTimer_Tick(object? sender, EventArgs e)
        {
            if (_loopAnimator == null)
            {
                _loopTimer.Stop();
                return;
            }
            _loopTimer.Interval = TimeSpan.FromMilliseconds(_loopAnimator.Advance());
        }

        private void BtnLoop_Click(object sender, RoutedEventArgs e)
        {
            _loopEnabled = !_loopEnabled;
            UpdateLoopButton();
            if (_loopEnabled) ScheduleLoop();
            else StopLoop();
        }

        private void UpdateLoopButton()
        {
            IconLoop.Kind = _loopEnabled ? PackIconKind.Repeat : PackIconKind.RepeatOff;
            BtnLoop.ToolTip = _loopEnabled
                ? "Looping the selected part. Click to show just the start and end frames."
                : "Loop the selected part of the video, as it will play in the GIF.";
        }

        // =================================================================
        //  Images mode: the Files card
        // =================================================================

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>DragLeave also fires when moving between child elements - only hide once the pointer is really outside.</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>A video (alone, or the only one in the drop) opens in Video mode; images and folders go to Images mode.</summary>
        private void Root_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            if (_isRunning || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            var files = paths.Where(File.Exists).ToList();
            bool anyFolder = paths.Any(Directory.Exists);

            // One video file: open it. (A lone .gif counts as a video only while Video mode is on.)
            if (!anyFolder && files.Count == 1 && GifMakerManager.IsVideo(files[0])
                && (Mode == GifSourceMode.Video || !GifMakerManager.IsImage(files[0])))
            {
                _ = LoadVideoAsync(files[0]);
                return;
            }
            // Only videos (no images, no folders): open the first.
            var videos = files.Where(f => GifMakerManager.IsVideo(f) && !GifMakerManager.IsImage(f)).ToList();
            if (!anyFolder && videos.Count > 0 && !files.Any(GifMakerManager.IsImage))
            {
                _ = LoadVideoAsync(videos[0]);
                return;
            }
            SetMode(GifSourceMode.Images);
            AddSources(paths);
        }

        private void AddSources(IEnumerable<string> paths)
        {
            bool added = false;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) continue;
                if (!isFolder && !GifMakerManager.IsImage(path)) continue;   // "All files" lets anything through
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new GifImageSource { FullPath = path, IsFolder = isFolder });
                _removed.Remove(path);
                added = true;
            }
            if (!added)
            {
                InputCard.PopDropArea();
                return;
            }
            InputCard.PopDropArea();
            _ = RefreshQueueAsync();
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || sender is not Button { Tag: GifImageGroup group }) return;
            if (group.IsFolder)
                _sources.RemoveAll(s => s.IsFolder && string.Equals(s.FullPath, group.FolderPath, StringComparison.OrdinalIgnoreCase));
            else
                _sources.RemoveAll(s => !s.IsFolder && group.FilePaths.Contains(s.FullPath, StringComparer.OrdinalIgnoreCase));
            _ = RefreshQueueAsync();
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isRunning) AddSources(e.Paths);
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e) => InputCard_ClearRequested(sender, e);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isRunning) return;
            _expandCts?.Cancel();
            _probeCts.Cancel();
            _probeCts = new CancellationTokenSource();
            _sources.Clear();
            _removed.Clear();
            _images.Clear();
            RebuildQueueDisplay();
            UpdateFramesUi();
            SuggestFileName();
            AfterPlanChanged();
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            if (_sources.Any(s => s.IsFolder)) _ = RefreshQueueAsync();
            else RebuildQueueDisplay();
        }

        private void RebuildQueueDisplay()
        {
            _queueDisplay.Clear();
            foreach (var group in GifMakerManager.BuildQueueGroups(_sources, _images, InputCard.IncludeSubfolders))
                _queueDisplay.Add(group);
            InputCard.SetEmpty(_queueDisplay.Count == 0);
        }

        /// <summary>Reads the added folders off the UI thread and merges: frames still there keep their place, new ones go at the end.</summary>
        private async Task RefreshQueueAsync()
        {
            RebuildQueueDisplay();
            _expandCts?.Cancel();
            var cts = _expandCts = new CancellationTokenSource();
            var sources = _sources.ToList();
            bool subfolders = InputCard.IncludeSubfolders;
            if (sources.Any(s => s.IsFolder)) TxtProgress.Text = "Reading folders...";

            List<(string Path, long Size, DateTime Modified)> files;
            try
            {
                files = await Task.Run(() => GifMakerManager.ListImages(sources, subfolders, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Couldn't read the folders: " + ex.Message;
                return;
            }
            if (cts.IsCancellationRequested || _isRunning) return;

            var wanted = files.Where(f => !_removed.Contains(f.Path)).ToList();
            var keep = new HashSet<string>(wanted.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _images.Where(i => !keep.Contains(i.FullPath)).ToList()) _images.Remove(gone);

            var have = new HashSet<string>(_images.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var fresh = new List<GifImageItem>();
            foreach (var (path, size, modified) in wanted)
            {
                if (!have.Add(path)) continue;
                var item = new GifImageItem { FullPath = path, SizeBytes = size, Modified = modified };
                _images.Add(item);
                fresh.Add(item);
            }
            GifMakerManager.Renumber(_images);
            RebuildQueueDisplay();
            UpdateFramesUi();
            SuggestFileName();
            if (wanted.Count == 0 && sources.Count > 0) TxtProgress.Text = "No images found in what was added.";
            if (fresh.Count > 0) await ProbeImagesAsync(fresh);
            AfterPlanChanged();
        }

        /// <summary>Reads each new image's size (four at a time). Images that can't be read are marked and left out of the GIF.</summary>
        private async Task ProbeImagesAsync(IReadOnlyList<GifImageItem> items)
        {
            var token = _probeCts.Token;
            _probing++;
            TxtProgress.Text = $"Reading {GifText.Plural(items.Count, "image", "images")}...";
            using var gate = new SemaphoreSlim(4);
            try
            {
                await Task.WhenAll(items.Select(async item =>
                {
                    await gate.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        var (w, h, error) = await GifMakerManager.ProbeImageAsync(item.FullPath, token);
                        item.Width = w;
                        item.Height = h;
                        item.Problem = error;
                    }
                    catch (OperationCanceledException)
                    {
                        // cleared while reading
                    }
                    catch (Exception ex)
                    {
                        item.Problem = ex.Message;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
            finally
            {
                _probing--;
            }
        }

        // =================================================================
        //  Images mode: the frames list
        // =================================================================

        private void UpdateFramesUi()
        {
            bool any = _images.Count > 0;
            UiMotion.FadeVisibility(FramesEmptyState, !any);
            UiMotion.FadeVisibility(DataGridFrames, any);
            FrameTools.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            UpdateFrameButtons();
        }

        private void UpdateFrameButtons()
        {
            bool idle = !_isRunning;
            bool highlight = DataGridFrames.SelectedItems.Count > 0;
            BtnMoveUp.IsEnabled = idle && highlight;
            BtnMoveDown.IsEnabled = idle && highlight;
            BtnRemoveSelected.IsEnabled = idle && highlight;
            BtnSortByName.IsEnabled = idle && _images.Count > 1;
            BtnSortByDate.IsEnabled = idle && _images.Count > 1;
            BtnClearAll.IsEnabled = idle && _images.Count > 0;
        }

        private void DataGridFrames_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed) UpdateFrameButtons();
        }

        private List<GifImageItem> HighlightedFrames() => DataGridFrames.SelectedItems.OfType<GifImageItem>().ToList();

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            if (_isRunning) return;
            var chosen = HighlightedFrames();
            if (!GifMakerManager.Move(_images, chosen, direction)) return;
            Reselect(chosen);
            AfterPlanChanged();
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortFrames(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortFrames(byDate: true);

        private void SortFrames(bool byDate)
        {
            if (_isRunning || _images.Count < 2) return;
            var chosen = HighlightedFrames();
            var sorted = GifMakerManager.Sorted(_images, byDate);
            _images.Clear();
            foreach (var item in sorted) _images.Add(item);
            GifMakerManager.Renumber(_images);
            Reselect(chosen);
            TxtProgress.Text = byDate ? "Frames sorted by date, oldest first." : "Frames sorted by name.";
            AfterPlanChanged();
        }

        /// <summary>Puts the highlight back after rows moved (replacing a row drops it) and keeps them in view.</summary>
        private void Reselect(IReadOnlyList<GifImageItem> chosen)
        {
            DataGridFrames.SelectedItems.Clear();
            foreach (var item in chosen) DataGridFrames.SelectedItems.Add(item);
            var first = chosen.OrderBy(c => c.Number).FirstOrDefault();
            if (first != null)
            {
                DataGridFrames.ScrollIntoView(first);
                DataGridFrames.CurrentItem = first;
            }
        }

        private void BtnRemoveFrame_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning && sender is Button { Tag: GifImageItem item }) RemoveFrames(new[] { item });
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning) RemoveFrames(HighlightedFrames());
        }

        private void RemoveFrames(IReadOnlyCollection<GifImageItem> items)
        {
            if (items.Count == 0) return;
            int next = _images.TakeWhile(i => !items.Contains(i)).Count();
            foreach (var item in items)
            {
                _removed.Add(item.FullPath);
                _images.Remove(item);
                _sources.RemoveAll(s => !s.IsFolder && string.Equals(s.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
            }
            GifMakerManager.Renumber(_images);
            if (_images.Count > 0)
            {
                var select = _images[Math.Clamp(next, 0, _images.Count - 1)];
                DataGridFrames.SelectedItem = select;
                DataGridFrames.CurrentItem = select;
            }
            RebuildQueueDisplay();
            UpdateFramesUi();
            AfterPlanChanged();
        }

        private void DataGridFrames_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is Button) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Delete)
            {
                if (!_isRunning) RemoveFrames(HighlightedFrames());
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
            {
                MoveHighlighted(key == Key.Up ? -1 : +1);
                e.Handled = true;
            }
        }

        private void DataGridFrames_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<Button>(source) != null) return;
            if (FindAncestor<DataGridRow>(source)?.Item is GifImageItem item) OpenWithShell(item.FullPath);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static T CheckedTag<T>(Panel panel, T fallback) where T : struct, Enum =>
            panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out T v) ? v : fallback;

        private static double CheckedNumber(Panel panel, double fallback) =>
            panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        private static double ComboNumber(ComboBox combo, double fallback) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        private static void CheckTag(Panel panel, string tag)
        {
            var radio = panel.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag);
            if (radio != null) radio.IsChecked = true;
        }

        private static void CheckNumber(Panel panel, double value)
        {
            var radio = panel.Children.OfType<RadioButton>().FirstOrDefault(r =>
                double.TryParse((string)r.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && Math.Abs(v - value) < 0.001);
            if (radio != null) radio.IsChecked = true;
        }

        private static void SelectNumber(ComboBox combo, double value)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
                double.TryParse((string)i.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && Math.Abs(v - value) < 0.001);
            if (item != null) combo.SelectedItem = item;
        }

        private GifOptions BuildOptions() => new()
        {
            Mode = Mode,
            Fps = (int)CheckedNumber(FpsChips, 15),
            MaxWidth = (int)ComboNumber(CmbWidth, 480),
            Quality = CheckedTag(QualityChips, GifQuality.Balanced),
            Plays = (int)ComboNumber(CmbPlays, 0),
            Speed = CheckedNumber(SpeedChips, 1),
            StartSeconds = _video != null ? TrimTimeline.Start : 0,
            EndSeconds = _video != null ? TrimTimeline.End : 0,
            SecondsPerImage = ComboNumber(CmbImageDuration, 0.5),
            Fill = CheckedTag(FillChips, GifFill.Transparent),
        };

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void Setting_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateQualityNote();
            AfterPlanChanged();
            SaveSettings();
        }

        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore) return;
            AfterPlanChanged();
            SaveSettings();
        }

        private void UpdateQualityNote()
        {
            TxtQualityNote.Text = CheckedTag(QualityChips, GifQuality.Balanced) switch
            {
                GifQuality.Small => "64 colours: the smallest files. Fine for screen recordings and cartoons.",
                GifQuality.Best => "256 colours with smooth dithering: best for photos and video, but the biggest files.",
                _ => "128 colours: a good balance for most clips.",
            };
        }

        private void OutputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (!_isRunning) UpdatePlan();
        }

        private void OutputCard_AskEachTimeChanged(object? sender, EventArgs e) => SaveSettings();

        /// <summary>The Output card's file name follows the source until the user types their own.</summary>
        private void SuggestFileName()
        {
            if (_fileNameEdited) return;
            OutputCard.FileName = GifMakerManager.SuggestedName(Mode, _video, _images);
        }

        private void ApplySettings(GifSettings s)
        {
            _applyingSettings = true;
            try
            {
                CheckTag(ModeChips, s.Mode.ToString());
                CheckNumber(FpsChips, s.Fps);
                SelectNumber(CmbWidth, s.MaxWidth);
                CheckTag(QualityChips, s.Quality.ToString());
                SelectNumber(CmbPlays, s.Plays);
                CheckNumber(SpeedChips, s.Speed);
                SelectNumber(CmbImageDuration, s.SecondsPerImage);
                CheckTag(FillChips, s.Fill.ToString());
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                OutputCard.AskEachTime = s.AskEachTime;
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (!_loadedOnce || _applyingSettings) return;
            var o = BuildOptions();
            GifMakerManager.SaveSettings(new GifSettings
            {
                Mode = o.Mode,
                Fps = o.Fps,
                MaxWidth = o.MaxWidth,
                Quality = o.Quality,
                Plays = o.Plays,
                Speed = o.Speed,
                SecondsPerImage = o.SecondsPerImage,
                Fill = o.Fill,
                IncludeSubfolders = InputCard.IncludeSubfolders,
                AskEachTime = OutputCard.AskEachTime,
            });
        }

        // =================================================================
        //  The plan and the size estimate
        // =================================================================

        private int UsableImages => _images.Count(i => !i.HasProblem && i.Width > 0);

        /// <summary>The output size for the current source, or 0×0 when unknown.</summary>
        private (int Width, int Height) PlannedSize(GifOptions o)
        {
            if (o.Mode == GifSourceMode.Video)
                return _video == null ? (0, 0) : GifMakerManager.OutputSize(_video.Width, _video.Height, o.MaxWidth);
            var first = _images.FirstOrDefault(i => !i.HasProblem && i.Width > 0);
            return first == null ? (0, 0) : GifMakerManager.OutputSize(first.Width, first.Height, o.MaxWidth);
        }

        private void AfterPlanChanged()
        {
            _holdStatus = false;
            UpdatePlan();
            ScheduleLoop();
            _estimateCts?.Cancel();
            _estimateTimer.Stop();
            bool hasSource = Mode == GifSourceMode.Video ? _video != null : _images.Count > 0;
            if (!_isRunning && hasSource && GifMakerManager.MissingEngine() == null)
            {
                TxtEstimate.Text = "Estimating...";
                IconEstimate.Kind = PackIconKind.Harddisk;
                PillEstimate.Visibility = Visibility.Visible;
                _estimateTimer.Start();
            }
            else
            {
                PillEstimate.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>The plan pill ("4.3 s · 65 frames · 480×270") and, while idle, what's wrong or ready in the run bar.</summary>
        private void UpdatePlan()
        {
            var o = BuildOptions();
            var (w, h) = PlannedSize(o);
            bool hasSource = o.Mode == GifSourceMode.Video ? _video != null : _images.Count > 0;
            PillPlan.Visibility = hasSource ? Visibility.Visible : Visibility.Collapsed;
            if (hasSource) TxtPlan.Text = GifMakerManager.DescribePlan(o, w, h, UsableImages);

            if (_isRunning || _holdStatus) return;
            if (!hasSource)
            {
                TxtProgress.SetResourceReference(TextBlock.TextProperty, "Str_GifMaker_NoSourceHint");
                return;
            }
            if (_probing > 0) return;   // "Reading N images..." stays until they're read
            string? problem = GifMakerManager.Problem(o, _video, UsableImages);
            string? warning = problem == null ? GifMakerManager.Warning(o, w, h, UsableImages) : null;
            string where = OutputCard.AskEachTime
                ? "you'll choose where to save it"
                : "saves to " + (OutputCard.ResolvedFilePath ?? OutputCard.EffectiveFolder ?? "the output folder");
            TxtProgress.Text = problem ?? warning ?? $"Ready · {where}";
        }

        private async Task EstimateAsync()
        {
            if (_isRunning) return;
            var o = BuildOptions();
            if (GifMakerManager.Problem(o, _video, UsableImages) != null || _probing > 0)
            {
                PillEstimate.Visibility = Visibility.Collapsed;
                if (_probing > 0) _estimateTimer.Start();   // try again once the images are read
                return;
            }
            _estimateCts?.Cancel();
            var cts = _estimateCts = new CancellationTokenSource();
            try
            {
                long? bytes = await GifMakerManager.EstimateAsync(o, _video, _images.ToList(), cts.Token);
                if (cts.IsCancellationRequested) return;
                if (bytes is long b)
                {
                    TxtEstimate.Text = "≈ " + GifText.FormatBytes(b);
                    PillEstimate.Visibility = Visibility.Visible;
                    UiMotion.Pop(PillEstimate, 0.92);
                }
                else
                {
                    PillEstimate.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
                // a newer setting took over
            }
            catch (Exception ex)
            {
                Log.Info($"GIF Maker: estimate failed: {ex.Message}");
                if (ReferenceEquals(_estimateCts, cts)) PillEstimate.Visibility = Visibility.Collapsed;
            }
        }

        // =================================================================
        //  Create
        // =================================================================

        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Cancelling...";
                return;
            }
            await CreateAsync();
        }

        private async Task CreateAsync()
        {
            UpdateEngineWarning();
            if (GifMakerManager.MissingEngine() is string missing)
            {
                TxtProgress.Text = missing;
                UiMotion.Pop(FfmpegWarning, 0.9);
                return;
            }
            if (_probing > 0)
            {
                TxtProgress.Text = "Still reading the images - try again in a moment.";
                return;
            }
            var o = BuildOptions();
            if (GifMakerManager.Problem(o, _video, UsableImages) is string problem)
            {
                TxtProgress.Text = problem;
                if (o.Mode == GifSourceMode.Video) UiMotion.Pop(VideoDropArea, 0.95); else InputCard.PopDropArea();
                return;
            }

            string? target = OutputCard.GetSavePath("GIF image (*.gif)|*.gif", out bool overwrite, out string? saveProblem);
            if (target == null)
            {
                if (saveProblem != null) TxtProgress.Text = "Can't save there: " + saveProblem;
                return;
            }

            var cts = _runCts = new CancellationTokenSource();
            SetRunning(true);
            var progress = new Progress<GifProgress>(p =>
            {
                if (!_isRunning) return;
                ProgressRun.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                TxtProgress.Text = p.Stage + "...";
            });

            GifResult? result = null;
            try
            {
                result = o.Mode == GifSourceMode.Video
                    ? await GifMakerManager.CreateFromVideoAsync(_video!, o, target, overwrite, progress, cts.Token)
                    : await GifMakerManager.CreateFromImagesAsync(_images.ToList(), o, target, overwrite, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Cancelled - nothing was saved.";
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "The GIF couldn't be made: " + ex.Message;
            }
            finally
            {
                _holdStatus = true;   // the card's refresh after the run mustn't replace the message
                SetRunning(false);
                _runCts = null;
                cts.Dispose();
            }

            if (result == null)
            {
                ProgressRun.Value = 0;
                TxtProgressPercent.Text = "";
                return;
            }
            ProgressRun.Value = 100;
            TxtProgressPercent.Text = "100%";
            string renamed = !string.Equals(Path.GetFullPath(result.Path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
                ? " (the name was taken, so a number was added)"
                : "";
            TxtProgress.Text = $"Saved {Path.GetFileName(result.Path)} · {GifText.FormatBytes(result.Bytes)}{renamed}";
            ShowResult(result);
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("gif-maker");
            else { _activity?.Dispose(); _activity = null; }
            IconCreate.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.FileGifBox;
            if (running) TxtCreateLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtCreateLabel.SetResourceReference(TextBlock.TextProperty, "Str_GifMaker_CreateGif");
            UiMotion.Pop(BtnCreate, 0.96);

            OptionsScrollViewer.IsEnabled = !running;
            OutputCard.IsLocked = running;
            if (!running) OutputCard.Refresh();
            TrimPanel.IsEnabled = !running && _video != null;
            FrameTools.IsEnabled = !running;
            DataGridFrames.IsEnabled = !running;
            UpdateFrameButtons();

            if (!running) ScheduleLoop();   // the loop was put aside while the GIF was made
            if (running)
            {
                StopLoop();   // the real GIF gets the processor
                _estimateCts?.Cancel();
                _estimateTimer.Stop();
                ProgressRun.IsIndeterminate = false;
                ProgressRun.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = "Starting...";
            }
        }

        // =================================================================
        //  The result: animated preview
        // =================================================================

        private void ShowResult(GifResult result)
        {
            _result = result;
            _playTimer.Stop();
            _animator = GifAnimator.Load(result.Path);

            ResultEmptyState.Visibility = Visibility.Collapsed;
            ResultStage.Visibility = Visibility.Visible;
            ResultBar.Visibility = Visibility.Visible;
            TxtResultName.Text = Path.GetFileName(result.Path);
            TxtResultName.ToolTip = result.Path;
            TxtResultInfo.Text = $"{GifText.FormatBytes(result.Bytes)} · {result.Width}×{result.Height} · " +
                                 $"{GifText.Plural(result.Frames, "frame", "frames")} · {result.Length.TotalSeconds:0.0} s";

            if (_animator != null)
            {
                ImgResult.Source = _animator.Bitmap;
                ImgResult.MaxWidth = _animator.Width;
                ImgResult.MaxHeight = _animator.Height;
                SetPlaying(true);
            }
            else
            {
                ImgResult.Source = null;
                SetPlaying(false);
                TxtResultInfo.Text += " · the preview couldn't be shown";
            }
            UiMotion.FadeIn(ResultStage);
            UiMotion.Pop(ResultBar, 0.95);
        }

        private void SetPlaying(bool playing)
        {
            _playing = playing && _animator != null;
            IconPlayPause.Kind = _playing ? PackIconKind.Pause : PackIconKind.Play;
            if (_playing)
            {
                _playTimer.Interval = TimeSpan.FromMilliseconds(_animator!.CurrentDelay);
                _playTimer.Start();
            }
            else
            {
                _playTimer.Stop();
            }
        }

        private void PlayTimer_Tick(object? sender, EventArgs e)
        {
            if (_animator == null)
            {
                _playTimer.Stop();
                return;
            }
            int delay = _animator.Advance();
            _playTimer.Interval = TimeSpan.FromMilliseconds(delay);
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

        private void BtnOpenResult_Click(object sender, RoutedEventArgs e)
        {
            if (_result != null && File.Exists(_result.Path)) OpenWithShell(_result.Path);
            else TxtProgress.Text = "The GIF isn't there any more.";
        }

        private void BtnShowResult_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || ShellExplorer.Show(_result.Path) != ShellExplorer.ShowResult.Shown)
                TxtProgress.Text = "The GIF isn't there any more.";
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private static void OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open {Path.GetFileName(path)}: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
