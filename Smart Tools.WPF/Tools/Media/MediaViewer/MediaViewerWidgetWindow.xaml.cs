using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The Media Viewer's floating mini player. A frame only: the view puts its real player (the Stage)
    /// in with <see cref="Host"/> and takes it out again with <see cref="Release"/>, so there is only ever
    /// one player. Same shape as the Network Speed widget (borderless, always on top, not in the taskbar,
    /// no Owner so it outlives a minimized main window), but resizable.
    /// </summary>
    internal sealed partial class MediaViewerWidgetWindow : Window
    {
        public const double DefaultWidth = 420;
        public const double DefaultHeight = 300;

        // Position and size are written once the user stops moving or resizing, not on every step.
        private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

        // The title strip and the player's controls fade out while the window isn't focused or hovered,
        // so only the picture or video is left. They come back as soon as the pointer is over the window.
        private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private UIElement? _chrome;
        private UIElement? _scrim;
        private bool _controlsShown = true;

        /// <summary>"Minimize the app": the main window minimizes and only this mini player stays on screen.</summary>
        public event Action? MinimizeAppRequested;

        /// <summary>"Back to the tool": the player goes back and the main window comes forward.</summary>
        public event Action? BackRequested;

        /// <summary>The volume slider was moved (0..1).</summary>
        public event Action<double>? VolumeChanged;

        /// <summary>The speaker button was pressed.</summary>
        public event Action? MuteRequested;

        /// <summary>The close button: the player goes back quietly.</summary>
        public event Action? CloseRequested;

        /// <summary>Left, top, width, height after the user moved or resized the window.</summary>
        public event Action<double, double, double, double>? PlacementChanged;

        /// <summary>A key was pressed in the widget - the view handles it like in the tool (Space, arrows, N/P...).</summary>
        public event Action<KeyEventArgs>? KeyPressed;

        public MediaViewerWidgetWindow(MediaViewerSettings settings)
        {
            InitializeComponent();
            RefreshText();

            Width = Math.Max(MinWidth, settings.WidgetWidth ?? DefaultWidth);
            Height = Math.Max(MinHeight, settings.WidgetHeight ?? DefaultHeight);

            _placementTimer.Tick += (_, _) =>
            {
                _placementTimer.Stop();
                PlacementChanged?.Invoke(Left, Top, ActualWidth, ActualHeight);
            };
            SizeChanged += (_, _) => SchedulePlacement();
            LocationChanged += (_, _) => SchedulePlacement();
            PreviewKeyDown += (_, e) => KeyPressed?.Invoke(e);
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                if (!ControlsWanted) SetControlsShown(false);
            };
            Activated += (_, _) => UpdateControls();
            Deactivated += (_, _) => UpdateControls();
            Closed += (_, _) =>
            {
                _placementTimer.Stop();
                _hideTimer.Stop();
            };
            Loaded += (_, _) => PlaceOnScreen(settings);
        }

        private void SchedulePlacement()
        {
            if (!IsLoaded) return;
            _placementTimer.Stop();
            _placementTimer.Start();
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Re-reads the texts (the language can change while the widget is open).</summary>
        public void RefreshText()
        {
            BtnMinimizeApp.ToolTip = Text("Str_MediaViewer_Widget_MinimizeApp", "Minimize the app and the mini player");
            BtnBack.ToolTip = Text("Str_MediaViewer_Widget_Back", "Back to the tool");
            BtnClose.ToolTip = Text("Str_MediaViewer_Widget_Close", "Close the mini player");
            BtnMute.ToolTip = Text("Str_MediaViewer_Mute", "Mute (M)");
            VolumeSlider.ToolTip = Text("Str_MediaViewer_Volume", "Volume");
        }

        private bool _syncingVolume;

        /// <summary>Shows the player's volume and mute state in the strip, without raising <see cref="VolumeChanged"/>.</summary>
        public void SetVolume(double volume, bool muted)
        {
            _syncingVolume = true;
            VolumeSlider.Value = volume;
            _syncingVolume = false;
            MuteIcon.Kind = muted || volume <= 0 ? MaterialDesignThemes.Wpf.PackIconKind.VolumeOff : MaterialDesignThemes.Wpf.PackIconKind.VolumeHigh;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_syncingVolume) VolumeChanged?.Invoke(e.NewValue);
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e) => MuteRequested?.Invoke();

        /// <summary>The title in the strip: what is playing.</summary>
        public void SetTitle(string title)
        {
            TxtTitle.Text = string.IsNullOrEmpty(title) ? Text("Str_MediaViewer_Widget_Nothing", "Nothing playing") : title;
            TxtTitle.ToolTip = string.IsNullOrEmpty(title) ? null : title;
        }

        /// <summary>
        /// Puts the player in the window. <paramref name="chrome"/> (the player's controls) and
        /// <paramref name="scrim"/> (the darkening behind them) fade out together with the title strip.
        /// </summary>
        public void Host(FrameworkElement stage, UIElement? chrome = null, UIElement? scrim = null)
        {
            _chrome = chrome;
            _scrim = scrim;
            StageHost.Child = stage;
            SetControlsShown(true, animate: false);
        }

        /// <summary>Takes the player out again (so the view can put it back in the tool). Its controls are shown again.</summary>
        public FrameworkElement? Release()
        {
            var stage = StageHost.Child as FrameworkElement;
            SetControlsShown(true, animate: false);
            _hideTimer.Stop();
            _chrome = null;
            _scrim = null;
            StageHost.Child = null;
            return stage;
        }

        // ---------------- Controls that hide ----------------

        private bool ControlsWanted => IsActive || Card.IsMouseOver;

        /// <summary>Shows the controls at once; hides them a moment after the window loses focus and the pointer leaves.</summary>
        private void UpdateControls()
        {
            _hideTimer.Stop();
            if (ControlsWanted) SetControlsShown(true);
            else _hideTimer.Start();
        }

        private void SetControlsShown(bool show, bool animate = true)
        {
            if (animate && _controlsShown == show) return;
            _controlsShown = show;
            Fade(TitleStrip, show, animate, hitTest: true);
            if (_chrome != null) Fade(_chrome, show, animate, hitTest: true);
            if (_scrim != null) Fade(_scrim, show, animate, hitTest: false);
        }

        private static void Fade(UIElement element, bool show, bool animate, bool hitTest)
        {
            if (hitTest) element.IsHitTestVisible = show;
            if (!animate)
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);   // drop any held animation value
                element.Opacity = show ? 1 : 0;
                return;
            }
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(180)));
        }

        private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => UpdateControls();
        private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => UpdateControls();

        // ---------------- Position ----------------

        /// <summary>Bottom-right corner of the work area, at the default size.</summary>
        public void ResetPlacement()
        {
            Width = DefaultWidth;
            Height = DefaultHeight;
            SnapToCorner();
        }

        /// <summary>Bottom-right corner of the work area, keeping the current size.</summary>
        public void SnapToCorner()
        {
            var wa = SystemParameters.WorkArea;
            UpdateLayout();
            Left = wa.Right - ActualWidth - 16;
            Top = wa.Bottom - ActualHeight - 16;
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

        // ---------------- Buttons ----------------

        private void TitleStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { /* the button was released already */ }
        }

        private void BtnMinimizeApp_Click(object sender, RoutedEventArgs e) => MinimizeAppRequested?.Invoke();
        private void BtnBack_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
