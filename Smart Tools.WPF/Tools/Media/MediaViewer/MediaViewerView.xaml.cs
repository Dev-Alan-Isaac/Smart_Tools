using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Media Viewer - UI only. Logic lives in MediaViewerManager.
    /// One list for pictures, video and sound: pictures show with zoom, pan
    /// and an optional slideshow; video and sound play in the same window.
    /// </summary>
    public partial class MediaViewerView : UserControl
    {
        private readonly ObservableCollection<MediaItem> _items = new();
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromSeconds(4) };
        private readonly bool _ready;

        private int _index = -1;
        private int _loadVersion;
        private bool _syncingList;
        private bool _updatingSlider;
        private bool _slideshow;
        private bool _playing;
        private bool _mediaOpen;

        // Picture zoom: 0 = fit to the window, otherwise a scale (1 = 100%).
        private double _zoom;
        private int _pixelWidth, _pixelHeight;
        private bool _panning;
        private Point _panStart;
        private double _panStartH, _panStartV;

        public MediaViewerView()
        {
            InitializeComponent();
            PlaylistBox.ItemsSource = _items;
            _tick.Tick += (_, _) => UpdateProgress();
            _slide.Tick += (_, _) => StepForSlideshow();
            _ready = true;
            Player.Volume = VolumeSlider.Value;
            UpdateUi();
        }

        private static string S(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        private MediaItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        // =================================================================
        //  Opening
        // =================================================================

        private void BtnOpenFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = S("Str_MediaViewer_OpenFiles", "Open files"), Multiselect = true, Filter = MediaViewerManager.FileDialogFilter };
            if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FileNames);
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = S("Str_MediaViewer_OpenFolder", "Open folder") };
            if (dialog.ShowDialog() == true) _ = AddPathsAsync(new[] { dialog.FolderName });
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
            Show(-1);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        private async Task AddPathsAsync(IEnumerable<string> paths)
        {
            var found = await Task.Run(() => MediaViewerManager.Expand(paths.ToList()));
            var known = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            int firstNew = _items.Count;
            foreach (var item in found)
                if (known.Add(item.Path)) _items.Add(item);

            if (_items.Count == firstNew)
            {
                if (_items.Count == 0) ShowError(S("Str_MediaViewer_NothingFound", "No pictures, videos or music found there."));
                return;
            }
            if (_index < 0 || Current == null) Show(firstNew);
            else UpdateUi();
        }

        // =================================================================
        //  Showing one item
        // =================================================================

        private void StopMedia()
        {
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
            TxtError.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string text)
        {
            HideStage();
            TxtError.Text = text;
            TxtError.Visibility = Visibility.Visible;
            UpdateUi();
        }

        private void Show(int index)
        {
            StopMedia();
            _slide.Stop();
            _loadVersion++;
            HideStage();
            ImageView.Source = null;
            PosSlider.Value = 0;
            TxtPosition.Text = TxtDuration.Text = "0:00";
            TxtInfo.Text = "";

            if (index < 0 || index >= _items.Count)
            {
                _index = -1;
                TxtNowPlaying.Text = "";
                EmptyPanel.Visibility = Visibility.Visible;
                UpdateUi();
                return;
            }

            _index = index;
            var item = _items[index];
            _syncingList = true;
            PlaylistBox.SelectedIndex = index;
            PlaylistBox.ScrollIntoView(item);
            _syncingList = false;
            TxtNowPlaying.Text = item.Name;

            if (item.Kind == ViewerMediaKind.Image) ShowImage(item);
            else ShowMedia(item);
            UpdateUi();
            Focus();
        }

        private void ShowImage(MediaItem item)
        {
            ImageScroll.Visibility = Visibility.Visible;
            _zoom = 0;
            _ = LoadImageAsync(item.Path, _loadVersion);
            if (_slideshow) _slide.Start();
        }

        private async Task LoadImageAsync(string path, int version)
        {
            var loaded = await Task.Run(() => ImageViewerManager.LoadForView(path));
            if (version != _loadVersion) return;   // another item was chosen meanwhile
            if (loaded is not { } result)
            {
                ShowError(S("Str_MediaViewer_ImageFailed", "This picture couldn't be opened."));
                return;
            }
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
            try
            {
                Player.Source = new Uri(item.Path);
                Player.SpeedRatio = CurrentSpeed;
                Player.Play();
                _playing = true;
                _tick.Start();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void PlaylistBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingList || PlaylistBox.SelectedIndex < 0) return;
            if (PlaylistBox.SelectedIndex != _index) Show(PlaylistBox.SelectedIndex);
        }

        // =================================================================
        //  Playback
        // =================================================================

        private double CurrentSpeed =>
            ComboSpeed.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 1;

        private void TogglePlay()
        {
            var item = Current;
            if (item == null)
            {
                BtnOpenFiles_Click(this, new RoutedEventArgs());
                return;
            }

            if (item.Kind == ViewerMediaKind.Image)
            {
                _slideshow = !_slideshow;
                if (_slideshow) _slide.Start();
                else _slide.Stop();
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

        private void StepForSlideshow()
        {
            if (_items.Count == 0 || Current?.Kind != ViewerMediaKind.Image) return;
            Show((_index + 1) % _items.Count);
        }

        private void GoNext()
        {
            if (_items.Count == 0) return;
            Show(_index + 1 < _items.Count ? _index + 1 : 0);
        }

        private void GoPrevious()
        {
            if (_items.Count == 0) return;
            Show(_index > 0 ? _index - 1 : _items.Count - 1);
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => GoNext();
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => GoPrevious();
        private void Player_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => TogglePlay();

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            _mediaOpen = true;
            Player.SpeedRatio = CurrentSpeed;   // MediaElement forgets the speed when the source changes
            if (Player.NaturalDuration.HasTimeSpan)
            {
                var length = Player.NaturalDuration.TimeSpan;
                PosSlider.Maximum = Math.Max(1, length.TotalSeconds);
                TxtDuration.Text = MediaViewerManager.FormatTime(length);
            }
            if (Player.NaturalVideoWidth > 0 && Current?.Kind == ViewerMediaKind.Video)
                TxtInfo.Text = $"{Player.NaturalVideoWidth} × {Player.NaturalVideoHeight} · {S("Str_MediaViewer_Video", "Video")}";
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (_index + 1 < _items.Count)
            {
                Show(_index + 1);   // plays on to the next one
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
            ShowError(string.Format(S("Str_MediaViewer_PlayFailed", "Windows can't play {0} files here. Try Video Replay, which has the VLC player.") + "\n\n{1}",
                ext, e.ErrorException?.Message));
        }

        private void UpdateProgress()
        {
            if (!_mediaOpen) return;
            var position = Player.Position;
            TxtPosition.Text = MediaViewerManager.FormatTime(position);
            if (PosSlider.IsMouseCaptureWithin) return;   // the user is dragging it
            _updatingSlider = true;
            PosSlider.Value = Math.Min(position.TotalSeconds, PosSlider.Maximum);
            _updatingSlider = false;
        }

        private void PosSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _updatingSlider || !_mediaOpen) return;
            Player.Position = TimeSpan.FromSeconds(e.NewValue);
            TxtPosition.Text = MediaViewerManager.FormatTime(Player.Position);
        }

        private void ComboSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && _mediaOpen) Player.SpeedRatio = CurrentSpeed;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            Player.Volume = e.NewValue;
            if (e.NewValue > 0 && Player.IsMuted) Player.IsMuted = false;
            UpdateUi();
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            Player.IsMuted = !Player.IsMuted;
            UpdateUi();
        }

        // =================================================================
        //  Pictures: zoom, pan
        // =================================================================

        private double FitScale
        {
            get
            {
                if (_pixelWidth == 0 || _pixelHeight == 0) return 1;
                return Math.Min(ImageScroll.ActualWidth / _pixelWidth, ImageScroll.ActualHeight / _pixelHeight);
            }
        }

        private void ApplyZoom()
        {
            if (ImageView.Source == null) return;
            if (_zoom <= 0)
            {
                ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ImageView.Stretch = System.Windows.Media.Stretch.Uniform;
                ImageView.Width = Math.Max(1, ImageScroll.ActualWidth);
                ImageView.Height = Math.Max(1, ImageScroll.ActualHeight);
            }
            else
            {
                ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ImageView.Stretch = System.Windows.Media.Stretch.Fill;
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

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
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
                case Key.Delete when Current != null:
                    RemoveCurrent();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        /// <summary>Takes the item off the list (the file itself is never deleted).</summary>
        private void RemoveCurrent()
        {
            int at = _index;
            _items.RemoveAt(at);
            Show(_items.Count == 0 ? -1 : Math.Min(at, _items.Count - 1));
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
            PlayIcon.Kind = running ? MaterialDesignThemes.Wpf.PackIconKind.Pause : MaterialDesignThemes.Wpf.PackIconKind.Play;
            BtnPrev.IsEnabled = BtnNext.IsEnabled = _items.Count > 1;
            BtnPlay.IsEnabled = has;

            SeekRow.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            ComboSpeed.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            BtnMute.Visibility = VolumeSlider.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            BtnFit.Visibility = image && _zoom > 0 ? Visibility.Visible : Visibility.Collapsed;

            MuteIcon.Kind = Player.IsMuted || VolumeSlider.Value <= 0
                ? MaterialDesignThemes.Wpf.PackIconKind.VolumeOff
                : MaterialDesignThemes.Wpf.PackIconKind.VolumeHigh;
            TxtCount.Text = has ? $"{_index + 1} / {_items.Count}" : (_items.Count > 0 ? _items.Count.ToString(CultureInfo.CurrentCulture) : "");
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            // Leaving the page: stop the sound and the slideshow, keep the list.
            _slide.Stop();
            _slideshow = false;
            if (_playing)
            {
                Player.Pause();
                _playing = false;
            }
            UpdateUi();
        }

        /// <summary>Called when the tool is closed: releases the file and stops the timers.</summary>
        public void CancelActiveRun()
        {
            _loadVersion++;
            _slide.Stop();
            _slideshow = false;
            StopMedia();
        }
    }
}
