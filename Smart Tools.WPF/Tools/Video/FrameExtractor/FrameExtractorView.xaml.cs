using MaterialDesignThemes.Wpf;
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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Video Frame Extractor - UI only. Videos (files or whole folders) come in through the Input card. The one you click
    /// is shown with a slider so you can stop at an exact moment; the frames can be one time, picked moments, a pattern,
    /// for every video in the list. Choosing the moments and running ffmpeg are <see cref="FrameExtractorManager"/>'s.
    /// </summary>
    public partial class FrameExtractorView : UserControl
    {
        public sealed record FramePicture(string Path, string Label, BitmapSource? Picture);

        private const int MaxThumbnails = 150;

        private readonly ObservableCollection<VideoSource> _sources = new();
        private readonly ObservableCollection<VideoItem> _videos = new();
        private readonly ObservableCollection<MomentChip> _picked = new();
        private readonly SemaphoreSlim _probeGate = new(3);
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly DispatcherTimer _previewTimer;
        private readonly bool _viewConstructed;

        private bool _loadedOnce;
        private bool _applying;
        private bool _busy;
        private bool _syncingTime;
        private int _syncRun;
        private int _previewRun;
        private double _time;
        private VideoItem? _current;
        private string? _lastFolder;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _previewCts;

        public FrameExtractorView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => "Videos|" + FrameExtractorManager.VideoFilter + "|All files|*.*";
            VideoList.ItemsSource = _videos;
            PickedList.ItemsSource = _picked;
            _sources.CollectionChanged += (_, _) => InputCard.SetEmpty(_sources.Count == 0);
            _videos.CollectionChanged += (_, _) => UpdateListState();
            _picked.CollectionChanged += (_, _) => { TxtPickedEmpty.Visibility = _picked.Count == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateInfo(); };

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); DonePill.Visibility = Visibility.Collapsed; };
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = ShowPreviewAsync(); };
            _viewConstructed = true;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = FrameExtractorManager.Clamp(JsonSettings.Load<FrameExtractorSettings>(FrameExtractorManager.SettingsName));
            _applying = true;
            (s.Mode switch { FrameMode.EverySeconds => RadioEvery, FrameMode.EvenlySpaced => RadioEven, FrameMode.Picked => RadioPicked, _ => RadioOne }).IsChecked = true;
            (s.Format == FrameFormat.Jpg ? RadioJpg : RadioPng).IsChecked = true;
            TxtAt.Text = s.AtSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            TxtEvery.Text = s.EverySeconds.ToString("0.###", CultureInfo.InvariantCulture);
            TxtCount.Text = s.Count.ToString(CultureInfo.InvariantCulture);
            InputCard.IncludeSubfolders = s.IncludeSubfolders;
            _time = s.AtSeconds;
            _applying = false;
            UpdatePanels();
            TxtPickedEmpty.Visibility = Visibility.Visible;

            string? missing = FrameExtractorManager.MissingTools();
            if (missing != null) ShowStatus(missing, StatusKind.Error);
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private FrameExtractorSettings ReadSettings()
        {
            double At(string t, double fallback) => FrameExtractorManager.TryParseTime(t, out double v) ? v : fallback;
            double Num(string t, double fallback) => double.TryParse(t.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) ? v : fallback;
            return FrameExtractorManager.Clamp(new FrameExtractorSettings
            {
                Mode = RadioPicked.IsChecked == true ? FrameMode.Picked : RadioEvery.IsChecked == true ? FrameMode.EverySeconds : RadioEven.IsChecked == true ? FrameMode.EvenlySpaced : FrameMode.OneFrame,
                Format = RadioJpg.IsChecked == true ? FrameFormat.Jpg : FrameFormat.Png,
                AtSeconds = At(TxtAt.Text, 0),
                EverySeconds = Num(TxtEvery.Text, 5),
                Count = (int)Num(TxtCount.Text, 10),
                IncludeSubfolders = InputCard.IncludeSubfolders,
            });
        }

        private void SaveSettings() => JsonSettings.Save(FrameExtractorManager.SettingsName, ReadSettings());

        private void Changed()
        {
            if (!_viewConstructed || _applying) return;
            UpdatePanels();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Mode_Click(object sender, RoutedEventArgs e) => Changed();
        private void Format_Click(object sender, RoutedEventArgs e) => Changed();
        private void Option_TextChanged(object sender, TextChangedEventArgs e) => Changed();

        /// <summary>Typing a time in the "At" box moves the slider too.</summary>
        private void At_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            if (!_syncingTime && FrameExtractorManager.TryParseTime(TxtAt.Text, out double t) && _current?.Duration != null) SetTime(t, fromAtBox: true);
            Changed();
        }

        private void UpdatePanels()
        {
            var s = ReadSettings();
            PanelAt.Visibility = s.Mode == FrameMode.OneFrame ? Visibility.Visible : Visibility.Collapsed;
            PanelEvery.Visibility = s.Mode == FrameMode.EverySeconds ? Visibility.Visible : Visibility.Collapsed;
            PanelCount.Visibility = s.Mode == FrameMode.EvenlySpaced ? Visibility.Visible : Visibility.Collapsed;
            PanelPicked.Visibility = s.Mode == FrameMode.Picked ? Visibility.Visible : Visibility.Collapsed;
            PickedArea.Visibility = s.Mode == FrameMode.Picked ? Visibility.Visible : Visibility.Collapsed;
            UpdateInfo();
        }

        // =================================================================
        //  Videos in (the Input card, or dropped on the page)
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => AddSources(e.Paths);

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            Changed();
            _ = ResyncAsync();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            _syncRun++;
            _sources.Clear();
            _videos.Clear();
            _picked.Clear();
            HideStatus();
            ClearCurrent();
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.Tag is not VideoSource source) return;
            _sources.Remove(source);
            _ = ResyncAsync();
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddSources(files);
        }

        private void AddSources(IEnumerable<string> paths)
        {
            if (_busy) return;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !(File.Exists(path) && FrameExtractorManager.IsVideo(path))) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new VideoSource { FullPath = path, IsFolder = isFolder });
            }
            _ = ResyncAsync();
        }

        private async Task ResyncAsync()
        {
            int run = ++_syncRun;
            var paths = _sources.Select(s => s.FullPath).ToList();
            bool include = InputCard.IncludeSubfolders;
            var (files, inaccessible) = await Task.Run(() => FrameExtractorManager.FindVideos(paths, include));
            if (run != _syncRun) return;

            bool capped = files.Count > FrameExtractorManager.MaxVideos;
            if (capped) files = files.Take(FrameExtractorManager.MaxVideos).ToList();
            var wanted = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

            for (int i = _videos.Count - 1; i >= 0; i--)
                if (!wanted.Contains(_videos[i].FullPath)) _videos.RemoveAt(i);

            var have = new HashSet<string>(_videos.Select(v => v.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<VideoItem>();
            foreach (string file in files)
            {
                if (have.Contains(file)) continue;
                var item = new VideoItem { FullPath = file };
                _videos.Add(item);
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

            if (inaccessible > 0) ShowStatus($"{inaccessible} folder(s) couldn't be read and were skipped.", StatusKind.Warning);
            else if (capped) ShowStatus(Loc.F("Str_FrameExtractor_MsgListFull", "The list is full ({0:N0} videos).", FrameExtractorManager.MaxVideos), StatusKind.Warning);
            else if (_sources.Count > 0 && files.Count == 0) ShowStatus(Loc.T("Str_FrameExtractor_MsgNoVideos", "No videos were found there."), StatusKind.Warning);
            else if (FrameExtractorManager.MissingTools() is { } missing) ShowStatus(missing, StatusKind.Error);
            else HideStatus();

            ProbeLengths(added);
            if (_videos.Count == 0) ClearCurrent();
            else if (VideoList.SelectedItem == null) VideoList.SelectedIndex = 0;
            UpdateInfo();
        }

        /// <summary>Reads each video's length (three at a time) so the list shows it and bad files are flagged early.</summary>
        private async void ProbeLengths(IReadOnlyList<VideoItem> items)
        {
            foreach (var item in items)
            {
                await _probeGate.WaitAsync();
                _ = Task.Run(() =>
                {
                    try
                    {
                        double? duration = FrameExtractorManager.ProbeDuration(item.FullPath, out string? problem);
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (!_videos.Contains(item)) return;
                            if (duration is { } d) item.Duration = d; else item.Problem = problem ?? "Couldn't be read";
                            if (ReferenceEquals(item, _current)) ApplyCurrentVideo();
                            UpdateInfo();
                        });
                    }
                    finally
                    {
                        _probeGate.Release();
                    }
                });
            }
        }

        private void UpdateListState()
        {
            VideosEmpty.Visibility = _videos.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtVideoCount.Text = _videos.Count == 0 ? "" : $"{_videos.Count:N0} video{(_videos.Count == 1 ? "" : "s")}";
        }

        // =================================================================
        //  The video you click, and choosing a moment in it
        // =================================================================

        private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoList.SelectedItem is not VideoItem item || ReferenceEquals(item, _current)) return;
            _current = item;
            _picked.Clear();
            _time = ReadSettings().Mode == FrameMode.OneFrame ? ReadSettings().AtSeconds : 0;
            ApplyCurrentVideo();
        }

        private void ClearCurrent()
        {
            _current = null;
            _previewCts?.Cancel();
            PreviewImage.Source = null;
            TxtPreviewNote.Text = Loc.T("Str_FrameExtractor_MsgClickVideo", "Click a video in the list to see it here.");
            TxtPreviewNote.Visibility = Visibility.Visible;
            SetTimeControls(false, 1);
            HeroName.Text = Loc.T("Str_FrameExtractor_MsgNoVideoYet", "No video yet");
            UpdateInfo();
        }

        /// <summary>The selected video's length is known (or its problem): set up the slider and show the first picture.</summary>
        private void ApplyCurrentVideo()
        {
            var item = _current;
            if (item == null) return;
            HeroName.Text = item.Name + (item.Duration is { } d ? "  ·  " + FrameExtractorManager.FormatTime(d) : "");

            if (item.Problem != null)
            {
                SetTimeControls(false, 1);
                PreviewImage.Source = null;
                TxtPreviewNote.Text = Loc.F("Str_FrameExtractor_MsgCantRead", "This video can't be read: {0}", item.Problem);
                TxtPreviewNote.Visibility = Visibility.Visible;
                return;
            }
            if (item.Duration is not { } duration)
            {
                SetTimeControls(false, 1);
                PreviewImage.Source = null;
                TxtPreviewNote.Text = Loc.T("Str_FrameExtractor_MsgReading", "Reading the video...");
                TxtPreviewNote.Visibility = Visibility.Visible;
                return;
            }

            SetTimeControls(true, duration);
            SetTime(Math.Min(_time, Math.Max(0, duration - 0.05)), fromAtBox: false);
        }

        private void SetTimeControls(bool enabled, double max)
        {
            _syncingTime = true;
            SliderTime.Maximum = Math.Max(0.1, max);
            SliderTime.IsEnabled = enabled;
            _syncingTime = false;
            foreach (var b in new[] { BtnBack1, BtnBackFine, BtnFwdFine, BtnFwd1, BtnAddMoment }) b.IsEnabled = enabled;
            TxtTime.IsEnabled = enabled;
            if (!enabled)
            {
                TxtTime.Text = "";
                TxtPreviewTime.Text = "-";
            }
        }

        /// <summary>Moves to a moment: the slider, the time box, the "At" box and the picture all follow.</summary>
        private void SetTime(double seconds, bool fromAtBox)
        {
            double max = SliderTime.Maximum;
            seconds = Math.Round(Math.Clamp(seconds, 0, max), 3);
            _time = seconds;
            _syncingTime = true;
            SliderTime.Value = seconds;
            TxtTime.Text = FrameExtractorManager.FormatTime(seconds);
            TxtPreviewTime.Text = TxtTime.Text;
            if (!fromAtBox && ReadSettings().Mode == FrameMode.OneFrame) TxtAt.Text = seconds.ToString("0.###", CultureInfo.InvariantCulture);
            _syncingTime = false;
            _previewTimer.Stop();
            _previewTimer.Start();
            UpdateInfo();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _syncingTime || _current?.Duration == null) return;
            SetTime(e.NewValue, fromAtBox: false);
        }

        private void Step_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
                SetTime(_time + delta, fromAtBox: false);
        }

        private void TxtTime_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ApplyTypedTime();
            e.Handled = true;
        }

        private void TxtTime_LostFocus(object sender, RoutedEventArgs e) => ApplyTypedTime();

        private void ApplyTypedTime()
        {
            if (_current?.Duration == null) return;
            if (FrameExtractorManager.TryParseTime(TxtTime.Text, out double t)) SetTime(t, fromAtBox: false);
            else TxtTime.Text = FrameExtractorManager.FormatTime(_time);   // not a time: put the real one back
        }

        private void BtnAddMoment_Click(object sender, RoutedEventArgs e)
        {
            if (_current?.Duration == null) return;
            if (_picked.Count >= FrameExtractorManager.MaxFrames) { ShowStatus(Loc.F("Str_FrameExtractor_MsgMostMoments", "That is the most moments I can take ({0}).", FrameExtractorManager.MaxFrames), StatusKind.Warning); return; }
            if (_picked.Any(c => Math.Abs(c.Seconds - _time) < 0.001)) return;
            var all = _picked.Select(c => c.Seconds).Append(_time).OrderBy(t => t).ToList();
            _picked.Clear();
            foreach (double t in all) _picked.Add(new MomentChip(t));
            if (ReadSettings().Mode != FrameMode.Picked)
            {
                RadioPicked.IsChecked = true;
                Changed();
            }
        }

        private void BtnRemoveMoment_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MomentChip chip) _picked.Remove(chip);
        }

        private void Chip_Go(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MomentChip chip) SetTime(chip.Seconds, fromAtBox: false);
        }

        /// <summary>The still for the current time, from ffmpeg (a newer request cancels an older one).</summary>
        private async Task ShowPreviewAsync()
        {
            var item = _current;
            if (item?.Duration == null) return;
            if (FrameExtractorManager.MissingTools() != null) { TxtPreviewNote.Text = "ffmpeg isn't available, so no picture can be shown."; TxtPreviewNote.Visibility = Visibility.Visible; return; }

            _previewCts?.Cancel();
            var cts = _previewCts = new CancellationTokenSource();
            int run = ++_previewRun;
            double t = _time;
            string path = item.FullPath;
            TxtPreviewNote.Text = PreviewImage.Source == null ? "Loading the picture..." : "";
            TxtPreviewNote.Visibility = PreviewImage.Source == null ? Visibility.Visible : Visibility.Collapsed;

            string? problem = null;
            var png = await Task.Run(() => FrameExtractorManager.PreviewFrame(path, t, 960, out problem, cts.Token));
            if (run != _previewRun || cts.IsCancellationRequested) return;

            if (png == null)
            {
                PreviewImage.Source = null;
                TxtPreviewNote.Text = problem ?? "No picture at that time.";
                TxtPreviewNote.Visibility = Visibility.Visible;
                return;
            }
            try
            {
                var image = new BitmapImage();
                using var ms = new MemoryStream(png);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
                PreviewImage.Source = image;
                TxtPreviewNote.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException or IOException)
            {
                PreviewImage.Source = null;
                TxtPreviewNote.Text = Loc.T("Str_FrameExtractor_MsgPictureFailed", "The picture couldn't be shown.");
                TxtPreviewNote.Visibility = Visibility.Visible;
            }
        }

        // =================================================================
        //  Telling the user what will happen
        // =================================================================

        private void UpdateInfo()
        {
            if (!_viewConstructed) return;
            var s = ReadSettings();
            var readable = _videos.Where(v => v.Duration != null && v.Problem == null).ToList();
            bool anyReading = _videos.Any(v => v.Duration == null && v.Problem == null);

            if (_videos.Count == 0)
            {
                TxtActionInfo.Text = Loc.T("Str_FrameExtractor_MsgAddToStart", "Add videos or folders on the left to start.");
                if (!_busy) BtnRun.IsEnabled = false;
                return;
            }

            int frames;
            string scope;
            if (s.Mode == FrameMode.Picked)
            {
                frames = _current?.Duration is { } d ? FrameExtractorManager.Moments(s, d, _picked.Select(c => c.Seconds).ToList()).Count : 0;
                scope = _current != null ? $"from {_current.Name}" : "";
            }
            else
            {
                frames = readable.Sum(v => FrameExtractorManager.Moments(s, v.Duration!.Value).Count);
                scope = readable.Count > 1 ? $"from {readable.Count:N0} videos (a folder for each)" : readable.Count == 1 ? $"from {readable[0].Name}" : "";
            }

            TxtActionInfo.Text = frames > 0
                ? $"{frames:N0} frame{(frames == 1 ? "" : "s")} will be saved {scope}.".Replace("  ", " ") + (anyReading ? " Still reading some videos..." : "")
                : s.Mode == FrameMode.Picked ? "Pick at least one moment with Add this moment." : anyReading ? "Reading the videos..." : "There is nothing to take from these videos.";
            if (!_busy) BtnRun.IsEnabled = frames > 0 && FrameExtractorManager.MissingTools() == null;
        }

        // =================================================================
        //  Taking the frames
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();
                return;
            }

            string? tools = FrameExtractorManager.MissingTools();
            if (tools != null) { ShowStatus(tools, StatusKind.Error); return; }
            if (OutputCard.Status.IsError) { ShowStatus(Loc.F("Str_FrameExtractor_MsgCantSave", "Can't save there: {0}", OutputCard.Status.Text), StatusKind.Error); return; }

            string? folder;
            try
            {
                folder = OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowStatus(Loc.F("Str_FrameExtractor_MsgCantSave", "Can't save there: {0}", ex.Message), StatusKind.Error);
                return;
            }
            if (folder == null) { ShowStatus(Loc.T("Str_FrameExtractor_MsgChooseFolder", "Choose a folder to save the frames into first."), StatusKind.Warning); return; }

            var s = ReadSettings();
            List<string> videos;
            IReadOnlyList<double>? picked = null;
            if (s.Mode == FrameMode.Picked)
            {
                if (_current == null || _picked.Count == 0) return;
                videos = new List<string> { _current.FullPath };
                picked = _picked.Select(c => c.Seconds).ToList();
            }
            else videos = _videos.Where(v => v.Problem == null).Select(v => v.FullPath).ToList();
            if (videos.Count == 0) return;

            HideStatus();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            RunProgress.IsIndeterminate = false;
            RunProgress.Maximum = 1;
            RunProgress.Value = 0;
            var progress = new Progress<FrameExtractorManager.FrameBatchProgress>(p =>
            {
                RunProgress.Value = (p.VideoIndex + (p.Total == 0 ? 0 : (double)p.Done / p.Total)) / Math.Max(1, p.VideoCount);
                TxtActionInfo.Text = Loc.F("Str_FrameExtractor_MsgProgress", "Video {0} of {1}: frame {2} of {3}...", p.VideoIndex + 1, p.VideoCount, Math.Min(p.Done + 1, p.Total), p.Total);
            });
            var result = await Task.Run(() => FrameExtractorManager.ExtractBatch(videos, folder, s, picked, progress, token));
            SetBusy(false);

            _lastFolder = folder;
            BtnOpenFolder.Visibility = result.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            await ShowResultsAsync(result.Files);
            TxtDonePill.Text = result.Cancelled ? $"Stopped - {result.Files.Count:N0} saved" : $"Saved {result.Files.Count:N0} frame{(result.Files.Count == 1 ? "" : "s")}";
            DonePill.Visibility = Visibility.Visible;
            _doneTimer.Stop();
            _doneTimer.Start();

            if (result.Problems.Count > 0)
            {
                string first = $"{Path.GetFileName(result.Problems[0].Video)}: {result.Problems[0].Problem}";
                ShowStatus(result.Problems.Count == 1 ? "One video had a problem - " + first : $"{result.Problems.Count} videos had a problem. The first: {first}", result.Files.Count == 0 ? StatusKind.Error : StatusKind.Warning);
            }
            OutputCard.Refresh();
            UpdateInfo();
        }

        private async Task ShowResultsAsync(List<string> files)
        {
            var shown = files.Take(MaxThumbnails).ToList();
            var items = await Task.Run(() => shown.Select(f => new FramePicture(f, Path.GetFileNameWithoutExtension(f), SpritePackerManager.LoadThumbnail(f, 300))).ToList());
            ResultList.ItemsSource = items;
            ListEmpty.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtEmptyTitle.Text = Loc.T("Str_FrameExtractor_MsgNoFramesSaved", "No frames were saved");
            if (files.Count > MaxThumbnails) ShowStatus(Loc.F("Str_FrameExtractor_MsgShowingFirst", "Showing the first {0} of {1:N0} frames - all of them are in the folder.", MaxThumbnails, files.Count), StatusKind.Warning);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            VideoList.IsEnabled = !busy;
            OutputCard.IsLocked = busy;
            RunProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            TxtRun.Text = busy ? "Stop" : "Take frames";
            IconRun.Kind = busy ? PackIconKind.Stop : PackIconKind.ImageMultipleOutline;
            if (busy) BtnRun.IsEnabled = true;
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFolder == null || !Directory.Exists(_lastFolder)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastFolder}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus(Loc.F("Str_FrameExtractor_MsgOpenFolder", "Couldn't open the folder: {0}", ex.Message), StatusKind.Error);
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private enum StatusKind { Error, Warning }

        private void ShowStatus(string text, StatusKind kind)
        {
            StatusBanner.SetResourceReference(Border.BackgroundProperty, kind == StatusKind.Error ? "StatusErrorBg" : "StatusWarningBg");
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, kind == StatusKind.Error ? "StatusErrorFg" : "StatusWarningFg");
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        /// <summary>Called when the window closes: stops ffmpeg (frames already saved stay) and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _syncRun++;
            _previewRun++;
            _previewTimer.Stop();
            _previewCts?.Cancel();
            _cts?.Cancel();
            _doneTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
