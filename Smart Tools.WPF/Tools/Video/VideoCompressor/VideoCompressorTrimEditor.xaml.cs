using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The body of the Video Compressor's trim window - UI only. Shows the part of <see cref="Item"/> that is kept: a timeline with
    /// two handles, the start and end times, and the frame under the handle that moved. Every change is written to the item at once
    /// (null start/end = the whole video) and reported with <see cref="TrimChanged"/>.
    /// </summary>
    public partial class VideoCompressorTrimEditor : UserControl
    {
        private const string DialogTitle = "Video Compressor";

        private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
        private VcFileItem? _item;
        private CancellationTokenSource? _frameCts;
        private double _frameAt;
        private bool _frameIsEnd, _loading;

        /// <summary>The kept part of <see cref="Item"/> changed (a handle moved, a time was typed, or it was reset).</summary>
        public event Action? TrimChanged;

        private string T(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        public VideoCompressorTrimEditor()
        {
            InitializeComponent();
            Timeline.SelectionChanged += Timeline_SelectionChanged;
            _frameTimer.Tick += async (_, _) =>
            {
                _frameTimer.Stop();
                await ShowFrameAsync();
            };
        }

        // =================================================================
        //  What it shows
        // =================================================================

        /// <summary>The video being trimmed. It must be probed (<see cref="VcFileItem.Duration"/> known).</summary>
        public VcFileItem? Item
        {
            get => _item;
            set
            {
                if (ReferenceEquals(_item, value)) return;
                CancelFrame();
                _item = value;
                if (_item != null) LoadTimeline(_item);
                ScheduleFrame(Timeline.Start, isEnd: false, immediate: true);
            }
        }

        private void LoadTimeline(VcFileItem item)
        {
            double duration = Math.Max(0.2, item.Duration ?? 0.2);
            var (start, end) = VideoCompressorManager.TrimRange(item);
            if (end - start < VideoCompressorManager.MinKeepSeconds) (start, end) = (0, duration);

            _loading = true;
            try
            {
                Timeline.Maximum = duration;
                Timeline.SmallStep = Math.Max(0.05, Math.Min(1, duration / 600));
                Timeline.LargeStep = Math.Max(0.5, duration / 60);
                Timeline.SetSelection(start, end);
                UpdateTexts();
            }
            finally
            {
                _loading = false;
            }
        }

        // =================================================================
        //  Times
        // =================================================================

        private void Timeline_SelectionChanged(bool endMoved)
        {
            if (_loading || _item == null) return;
            UpdateTexts();
            WriteBack();
            ScheduleFrame(endMoved ? Timeline.End : Timeline.Start, endMoved);
        }

        private void UpdateTexts()
        {
            double duration = Timeline.Maximum;
            TxtStart.Text = VcText.FormatPrecise(Timeline.Start);
            TxtEnd.Text = VcText.FormatPrecise(Timeline.End);
            TxtLength.Text = string.Format(CultureInfo.CurrentCulture, T("Str_Vc_TrimLength", "Keeps {0} of {1}"),
                VcText.FormatPrecise(Timeline.End - Timeline.Start), VcText.FormatPrecise(duration));
        }

        /// <summary>Writes the handles to the item. The whole video is stored as nulls, so "nothing trimmed" stays "nothing trimmed".</summary>
        private void WriteBack()
        {
            if (_item == null) return;
            bool whole = Timeline.Start <= 0.05 && Timeline.End >= Timeline.Maximum - 0.05;
            _item.TrimStart = whole ? null : Timeline.Start;
            _item.TrimEnd = whole ? null : Timeline.End;
            TrimChanged?.Invoke();
        }

        /// <summary>What typing in the Start and End boxes does: parse, clamp so at least a moment is kept, and apply.</summary>
        public void ApplyTimes(string startText, string endText)
        {
            if (_item == null) return;
            double? typedStart = VideoCompressorManager.ParseTime(startText);
            double? typedEnd = VideoCompressorManager.ParseTime(endText);
            bool startEdited = typedStart is double s && Math.Abs(s - Timeline.Start) > 1e-6;
            bool endEdited = typedEnd is double e && Math.Abs(e - Timeline.End) > 1e-6;

            var (start, end) = VideoCompressorManager.ClampKeep(typedStart ?? Timeline.Start, typedEnd ?? Timeline.End, Timeline.Maximum, startEdited || !endEdited);
            bool endMoved = Math.Abs(end - Timeline.End) > 1e-6;

            _loading = true;
            try
            {
                Timeline.SetSelection(start, end);
                UpdateTexts();
            }
            finally
            {
                _loading = false;
            }
            WriteBack();
            ScheduleFrame(endMoved ? end : start, endMoved);
        }

        private void TimeBox_LostFocus(object sender, RoutedEventArgs e) => ApplyTimes(TxtStart.Text, TxtEnd.Text);

        private void TimeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ApplyTimes(TxtStart.Text, TxtEnd.Text);
            e.Handled = true;
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null) return;
            _loading = true;
            try
            {
                Timeline.SetSelection(0, Timeline.Maximum);
                UpdateTexts();
            }
            finally
            {
                _loading = false;
            }
            WriteBack();
            ScheduleFrame(0, isEnd: false);
        }

        // =================================================================
        //  The frame
        // =================================================================

        private void ScheduleFrame(double seconds, bool isEnd, bool immediate = false)
        {
            if (_item == null) return;
            _frameAt = seconds;
            _frameIsEnd = isEnd;
            _frameTimer.Stop();
            if (immediate) _ = ShowFrameAsync();
            else _frameTimer.Start();
        }

        private void CancelFrame()
        {
            _frameTimer.Stop();
            _frameCts?.Cancel();
        }

        private async Task ShowFrameAsync()
        {
            var item = _item;
            if (item == null) return;
            _frameCts?.Cancel();
            var cts = _frameCts = new CancellationTokenSource();
            // The very last frame of a file often can't be decoded: step back a little at the end.
            double at = _frameIsEnd ? Math.Max(0, _frameAt - 0.05) : _frameAt;
            try
            {
                var png = await GifMakerManager.GrabFrameAsync(item.FullPath, at, 1280, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(item, _item)) return;
                if (GifMakerManager.ToBitmap(png) is { } bitmap)
                {
                    ImgFrame.Source = bitmap;
                    TxtFrameLabel.Text = (_frameIsEnd ? T("Str_Vc_TrimEndLabel", "End") : T("Str_Vc_TrimStartLabel", "Start")) + " · " + VcText.FormatPrecise(_frameAt);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer frame was asked for.
            }
        }

        private async void BtnSaveFrame_Click(object sender, RoutedEventArgs e)
        {
            var item = _item;
            if (item == null) return;
            double at = _frameIsEnd ? Math.Max(0, _frameAt - 0.05) : _frameAt;
            var dialog = new SaveFileDialog
            {
                Title = DialogTitle,
                FileName = $"{Path.GetFileNameWithoutExtension(item.FullPath)} {VcText.FormatPrecise(_frameAt).Replace(':', '.')}.png",
                Filter = "PNG picture (*.png)|*.png|JPEG picture (*.jpg)|*.jpg",
                InitialDirectory = item.Folder,
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var png = await GifMakerManager.GrabFrameAsync(item.FullPath, at, 100_000, CancellationToken.None);
                if (png == null)
                {
                    MessageBox.Show(T("Str_Vc_FrameFailed", "There is no frame at that moment."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string ext = Path.GetExtension(dialog.FileName);
                if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                    encoder.Frames.Add(BitmapFrame.Create(GifMakerManager.ToBitmap(png)!));
                    await using var stream = File.Create(dialog.FileName);
                    encoder.Save(stream);
                }
                else
                {
                    await File.WriteAllBytesAsync(dialog.FileName, png);
                }
                TxtFrameLabel.Text = T("Str_Vc_FrameSaved", "Saved ") + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => CancelFrame();
    }
}
