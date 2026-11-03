using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The Media Viewer's floating mini player. It holds no player logic: the view pushes a
    /// <see cref="MediaWidgetState"/> in through <see cref="Update"/> and the buttons raise events
    /// back. Same shape as the Network Speed widget (borderless, always on top, not in the
    /// taskbar, no Owner so it outlives a minimized main window).
    /// </summary>
    internal sealed partial class MediaViewerWidgetWindow : Window
    {
        private bool _updating;       // a value is being pushed in - not the user moving a slider
        private bool _compact;
        private bool _imageMode;      // a picture is on the stage: no seek bar, volume or speed
        private bool _hasItem;
        private ViewerPlayOrder _order;

        public event Action? PlayPauseRequested;
        public event Action? PreviousRequested;
        public event Action? NextRequested;
        public event Action<double>? SeekRequested;
        public event Action<double>? VolumeChanged;
        public event Action? MuteToggled;
        public event Action<int>? SpeedStepRequested;
        public event Action? OrderCycleRequested;
        public event Action? OpenToolRequested;
        public event Action? CloseRequested;

        /// <summary>The user dragged or snapped the widget (Left, Top).</summary>
        public event Action<double, double>? PositionChanged;

        /// <summary>The user flipped compact mode from the menu.</summary>
        public event Action<bool>? CompactToggled;

        public MediaViewerWidgetWindow(MediaViewerSettings settings)
        {
            InitializeComponent();
            RefreshText();
            SetCompact(settings.WidgetCompact);
            Update(new MediaWidgetState(false, "", "", ViewerMediaKind.Audio, false, 0, 0, settings.Volume, settings.Muted,
                MediaViewerManager.SpeedLabel(settings.Speed), settings.Order));
            Loaded += (_, _) => PlaceOnScreen(settings);
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Re-reads the texts (the language can change while the widget is open).</summary>
        public void RefreshText()
        {
            MenuCompact.Header = Text("Str_MediaViewer_Widget_Compact", "Compact mode");
            MenuCorner.Header = Text("Str_MediaViewer_Widget_Corner", "Snap to corner");
            MenuOpen.Header = Text("Str_MediaViewer_Widget_Open", "Open Media Viewer");
            MenuClose.Header = Text("Str_MediaViewer_Widget_Close", "Close widget");
            BtnClose.ToolTip = Text("Str_MediaViewer_Widget_Close", "Close widget");
            BtnPrev.ToolTip = Text("Str_MediaViewer_Previous", "Previous");
            BtnNext.ToolTip = Text("Str_MediaViewer_Next", "Next");
            BtnPlay.ToolTip = Text("Str_MediaViewer_PlayPause", "Play / pause");
            BtnMute.ToolTip = Text("Str_MediaViewer_Mute", "Mute");
            BtnSpeed.ToolTip = Text("Str_VideoReplay_SpeedClickForFaster", "Speed - click for faster, right-click for slower");
            ApplyOrderTip();
        }

        private void ApplyOrderTip()
        {
            string name = _order switch
            {
                ViewerPlayOrder.Shuffle => Text("Str_VideoReplay_ModeShuffle", "Shuffle"),
                ViewerPlayOrder.RepeatOne => Text("Str_VideoReplay_ModeLoop", "Loop"),
                _ => Text("Str_VideoReplay_ModeSequential", "Sequential"),
            };
            BtnOrder.ToolTip = Text("Str_MediaViewer_Widget_Order", "Play order") + ": " + name;
        }

        /// <summary>Compact mode: just the title and the three transport buttons.</summary>
        public void SetCompact(bool compact)
        {
            _compact = compact;
            MenuCompact.IsChecked = compact;
            Card.Width = compact ? 230 : 300;
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            bool full = !_compact && !_imageMode && _hasItem;
            SeekRow.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
            ExtrasRow.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
            // Pictures have no sound or speed; the play order still matters (it steps the slideshow).
            BtnMute.Visibility = VolumeSlider.Visibility = BtnSpeed.Visibility = _imageMode ? Visibility.Collapsed : Visibility.Visible;
            TxtSubtitle.Visibility = string.IsNullOrEmpty(TxtSubtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Shows what the view is doing. Values the user is dragging right now are left alone.</summary>
        public void Update(MediaWidgetState s)
        {
            _updating = true;
            try
            {
                _hasItem = s.HasItem;
                _imageMode = s.HasItem && s.Kind == ViewerMediaKind.Image;
                _order = s.Order;

                TxtTitle.Text = s.HasItem ? s.Title : Text("Str_MediaViewer_Widget_Nothing", "Nothing playing");
                TxtTitle.ToolTip = s.HasItem ? s.Title : null;
                TxtSubtitle.Text = s.Subtitle;
                KindIcon.Kind = !s.HasItem ? PackIconKind.PlayBoxMultipleOutline
                    : s.Kind == ViewerMediaKind.Image ? PackIconKind.ImageOutline
                    : s.Kind == ViewerMediaKind.Video ? PackIconKind.MovieOpenOutline
                    : PackIconKind.MusicNote;
                PlayIcon.Kind = s.IsRunning ? PackIconKind.Pause : PackIconKind.Play;

                if (s.DurationSeconds > 0) SeekSlider.Maximum = s.DurationSeconds;
                TxtDuration.Text = MediaViewerManager.FormatTime(TimeSpan.FromSeconds(Math.Max(0, s.DurationSeconds)));
                if (!SeekSlider.IsMouseCaptureWithin)
                {
                    SeekSlider.Value = Math.Clamp(s.PositionSeconds, 0, SeekSlider.Maximum);
                    TxtPosition.Text = MediaViewerManager.FormatTime(TimeSpan.FromSeconds(Math.Max(0, s.PositionSeconds)));
                }
                if (!VolumeSlider.IsMouseCaptureWithin) VolumeSlider.Value = s.Volume;
                MuteIcon.Kind = s.Muted || s.Volume <= 0 ? PackIconKind.VolumeOff : PackIconKind.VolumeHigh;
                BtnSpeed.Content = s.SpeedLabel;
                OrderIcon.Kind = s.Order switch
                {
                    ViewerPlayOrder.Shuffle => PackIconKind.ShuffleVariant,
                    ViewerPlayOrder.RepeatOne => PackIconKind.RepeatOnce,
                    _ => PackIconKind.PlaylistPlay,
                };
                ApplyOrderTip();
                ApplyVisibility();
            }
            finally
            {
                _updating = false;
            }
        }

        // ---------------- Position ----------------

        /// <summary>Bottom-right corner of the work area.</summary>
        public void SnapToCorner()
        {
            var wa = SystemParameters.WorkArea;
            UpdateLayout();
            Left = wa.Right - ActualWidth - 16;
            Top = wa.Bottom - ActualHeight - 16;
            PositionChanged?.Invoke(Left, Top);
        }

        private void PlaceOnScreen(MediaViewerSettings s)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (s.WidgetLeft is double l && s.WidgetTop is double t && screen.Contains(new Point(l + 20, t + 20)))
            {
                Left = l;
                Top = t;
            }
            else
            {
                SnapToCorner();
            }
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { return; }
            PositionChanged?.Invoke(Left, Top);
        }

        // ---------------- Buttons ----------------

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
        private void BtnMute_Click(object sender, RoutedEventArgs e) => MuteToggled?.Invoke();
        private void BtnOrder_Click(object sender, RoutedEventArgs e) => OrderCycleRequested?.Invoke();
        private void BtnSpeed_Click(object sender, RoutedEventArgs e) => SpeedStepRequested?.Invoke(+1);

        private void BtnSpeed_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            SpeedStepRequested?.Invoke(-1);
            e.Handled = true;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating) return;
            TxtPosition.Text = MediaViewerManager.FormatTime(TimeSpan.FromSeconds(e.NewValue));
            SeekRequested?.Invoke(e.NewValue);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_updating) VolumeChanged?.Invoke(e.NewValue);
        }

        // ---------------- Menu ----------------

        private void MenuCompact_Click(object sender, RoutedEventArgs e)
        {
            SetCompact(MenuCompact.IsChecked);
            CompactToggled?.Invoke(_compact);
        }

        private void MenuCorner_Click(object sender, RoutedEventArgs e) => SnapToCorner();
        private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenToolRequested?.Invoke();
        private void MenuClose_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
