using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Video Frame Extractor - UI only. Choosing the moments and running ffmpeg are <see cref="FrameExtractorManager"/>'s.</summary>
    public partial class FrameExtractorView : UserControl
    {
        public sealed record FramePicture(string Path, string Label, BitmapSource? Picture);

        private static readonly string[] Extensions = { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg", ".ts", ".3gp" };

        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private bool _busy;
        private string? _video;
        private double _duration;
        private string? _lastFolder;
        private CancellationTokenSource? _cts;

        public FrameExtractorView()
        {
            InitializeComponent();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); DonePill.Visibility = Visibility.Collapsed; };
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
            (s.Mode switch { FrameMode.EverySeconds => RadioEvery, FrameMode.EvenlySpaced => RadioEven, _ => RadioOne }).IsChecked = true;
            (s.Format == FrameFormat.Jpg ? RadioJpg : RadioPng).IsChecked = true;
            TxtAt.Text = s.AtSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            TxtEvery.Text = s.EverySeconds.ToString("0.###", CultureInfo.InvariantCulture);
            TxtCount.Text = s.Count.ToString(CultureInfo.InvariantCulture);
            _applying = false;
            UpdatePanels();

            string? missing = FrameExtractorManager.MissingTools();
            if (missing != null) ShowStatus(missing, error: true);
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
                Mode = RadioEvery.IsChecked == true ? FrameMode.EverySeconds : RadioEven.IsChecked == true ? FrameMode.EvenlySpaced : FrameMode.OneFrame,
                Format = RadioJpg.IsChecked == true ? FrameFormat.Jpg : FrameFormat.Png,
                AtSeconds = At(TxtAt.Text, 0),
                EverySeconds = Num(TxtEvery.Text, 5),
                Count = (int)Num(TxtCount.Text, 10),
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

        private void UpdatePanels()
        {
            var s = ReadSettings();
            PanelAt.Visibility = s.Mode == FrameMode.OneFrame ? Visibility.Visible : Visibility.Collapsed;
            PanelEvery.Visibility = s.Mode == FrameMode.EverySeconds ? Visibility.Visible : Visibility.Collapsed;
            PanelCount.Visibility = s.Mode == FrameMode.EvenlySpaced ? Visibility.Visible : Visibility.Collapsed;

            if (_video == null || _duration <= 0)
            {
                TxtActionInfo.Text = "Choose or drop a video to start.";
                BtnRun.IsEnabled = false;
                return;
            }
            int count = FrameExtractorManager.Moments(s, _duration).Count;
            bool capped = s.Mode == FrameMode.EverySeconds && count >= FrameExtractorManager.MaxFrames;
            TxtActionInfo.Text = capped
                ? $"That would be more than {FrameExtractorManager.MaxFrames} frames, so only the first {FrameExtractorManager.MaxFrames} will be taken."
                : $"{count:N0} frame{(count == 1 ? "" : "s")} will be saved.";
            if (!_busy) BtnRun.IsEnabled = count > 0 && FrameExtractorManager.MissingTools() == null;
        }

        // =================================================================
        //  Video in
        // =================================================================

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a video",
                Filter = "Videos|" + string.Join(";", Extensions.Select(x => "*" + x)) + "|All files|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) LoadVideo(dialog.FileName);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            string? file = files.FirstOrDefault(f => File.Exists(f));
            if (file != null) LoadVideo(file);
        }

        private async void LoadVideo(string path)
        {
            TxtFileName.Text = Path.GetFileName(path);
            TxtVideoInfo.Text = "Reading the video...";
            string? problem = null;
            double? duration = await Task.Run(() => FrameExtractorManager.ProbeDuration(path, out problem));
            if (duration == null)
            {
                _video = null;
                _duration = 0;
                TxtVideoInfo.Text = "";
                ShowStatus(problem ?? "That video couldn't be read.", error: true);
                UpdatePanels();
                return;
            }
            _video = path;
            _duration = duration.Value;
            TxtVideoInfo.Text = "Length: " + TimerStopwatchManager.Format(TimeSpan.FromSeconds(_duration), false);
            HeroName.Text = Path.GetFileName(path) + "  ·  " + TimerStopwatchManager.Format(TimeSpan.FromSeconds(_duration), false);
            StatusBanner.Visibility = Visibility.Collapsed;
            ResultList.ItemsSource = null;
            ListEmpty.Visibility = Visibility.Visible;
            TxtEmptyTitle.Text = "Ready";
            BtnOpenFolder.Visibility = Visibility.Collapsed;
            UpdatePanels();
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
            if (_video == null) return;

            string? tools = FrameExtractorManager.MissingTools();
            if (tools != null) { ShowStatus(tools, error: true); return; }
            if (OutputCard.Status.IsError) { ShowStatus("Can't save there: " + OutputCard.Status.Text, error: true); return; }

            string? folder;
            try
            {
                folder = OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowStatus("Can't save there: " + ex.Message, error: true);
                return;
            }
            if (folder == null) { ShowStatus("Choose a folder to save the frames into first.", error: false); return; }

            var s = ReadSettings();
            var moments = FrameExtractorManager.Moments(s, _duration);
            if (moments.Count == 0) return;
            if (s.Mode == FrameMode.OneFrame && s.AtSeconds > _duration)
                ShowStatus($"The video is only {TimerStopwatchManager.Format(TimeSpan.FromSeconds(_duration), false)} long, so the last frame is used.", error: false);
            else StatusBanner.Visibility = Visibility.Collapsed;

            string video = _video;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            RunProgress.Maximum = moments.Count;
            RunProgress.Value = 0;
            var progress = new Progress<int>(n =>
            {
                RunProgress.Value = n;
                TxtActionInfo.Text = $"Taking frame {Math.Min(n + 1, moments.Count)} of {moments.Count}...";
            });
            var result = await Task.Run(() => FrameExtractorManager.Extract(video, folder, moments, s.Format, progress, token));
            SetBusy(false);

            if (result.Problem != null)
            {
                ShowStatus("Couldn't take the frames: " + result.Problem, error: true);
                UpdatePanels();
                return;
            }

            _lastFolder = folder;
            BtnOpenFolder.Visibility = result.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            await ShowResultsAsync(result.Files);
            TxtDonePill.Text = result.Cancelled ? $"Stopped - {result.Files.Count} saved" : $"Saved {result.Files.Count} frame{(result.Files.Count == 1 ? "" : "s")}";
            DonePill.Visibility = Visibility.Visible;
            _doneTimer.Stop();
            _doneTimer.Start();
            OutputCard.Refresh();
            UpdatePanels();
        }

        private async Task ShowResultsAsync(List<string> files)
        {
            var items = await Task.Run(() => files.Select(f => new FramePicture(f, Path.GetFileNameWithoutExtension(f), SpritePackerManager.LoadThumbnail(f, 300))).ToList());
            ResultList.ItemsSource = items;
            ListEmpty.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtEmptyTitle.Text = "No frames were saved";
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
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
                ShowStatus("Couldn't open the folder: " + ex.Message, error: true);
            }
        }

        private void ShowStatus(string text, bool error)
        {
            StatusBanner.SetResourceReference(Border.BackgroundProperty, error ? "StatusErrorBg" : "StatusWarningBg");
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "StatusWarningFg");
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        /// <summary>Called when the window closes: stops ffmpeg (frames already saved stay) and keeps the setup.</summary>
        public void CancelActiveRun()
        {
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
