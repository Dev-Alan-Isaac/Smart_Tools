using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using MenuItem = System.Windows.Controls.MenuItem;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The small always-on-top bar shown while recording: a red dot, the time, and Stop. It is the way to stop when the app
    /// itself is minimized. Windows is asked to leave it out of the recording (it works on Windows 10 2004 and later); on older
    /// versions it would show up in the video, so it can be dragged out of the way. Code only, no XAML.
    /// </summary>
    internal sealed class ScreenRecorderControlWindow : Window
    {
        private const uint WdaExcludeFromCapture = 0x11;

        private readonly TextBlock _time = new();
        private readonly TextBlock _pausedText = new();
        private readonly Ellipse _dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        private readonly IReadOnlyList<RecordScreenInfo> _screens;
        private RecordRegion? _currentRegion;

        /// <summary>The user pressed Stop.</summary>
        public event Action? StopRequested;

        /// <summary>The user picked another screen (or "All screens") from the bar's screen menu.</summary>
        public event Action<RecordScreenInfo>? ScreenChosen;

        /// <param name="screens">The choices from <c>ListScreens</c>. The screen button only shows with two or more monitors (all + each one).</param>
        /// <param name="currentRegion">The screen being recorded now (null = all screens).</param>
        public ScreenRecorderControlWindow(IReadOnlyList<RecordScreenInfo>? screens = null, RecordRegion? currentRegion = null)
        {
            _screens = screens ?? Array.Empty<RecordScreenInfo>();
            _currentRegion = currentRegion;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = Application.Current?.TryFindResource("Str_Recorder_BarTitle") as string ?? "Screen recording";
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, "CardBg");

            _dot.SetResourceReference(Shape.FillProperty, "StatusErrorFg");

            _pausedText.Margin = new Thickness(0, 0, 14, 0);
            _pausedText.VerticalAlignment = VerticalAlignment.Center;
            _pausedText.FontSize = 12;
            _pausedText.FontWeight = FontWeights.SemiBold;
            _pausedText.Text = Application.Current?.TryFindResource("Str_Recorder_PausedSecurity") as string ?? "Paused for a Windows security prompt";
            _pausedText.Visibility = Visibility.Collapsed;
            _pausedText.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningFg");

            _time.Margin = new Thickness(8, 0, 14, 0);
            _time.VerticalAlignment = VerticalAlignment.Center;
            _time.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            _time.FontSize = 15;
            _time.FontWeight = FontWeights.SemiBold;
            _time.Text = "00:00";
            _time.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            var stopIcon = new PackIcon { Kind = PackIconKind.Stop, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            var stopLabel = new TextBlock
            {
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Text = Application.Current?.TryFindResource("Str_Recorder_Stop") as string ?? "Stop",
            };
            var stopContent = new StackPanel { Orientation = Orientation.Horizontal };
            stopContent.Children.Add(stopIcon);
            stopContent.Children.Add(stopLabel);
            var stop = new Button { Content = stopContent, Cursor = Cursors.Hand };
            if (Application.Current?.TryFindResource("DangerButtonStyle") is Style danger) stop.Style = danger;
            stop.Click += (_, _) => StopRequested?.Invoke();

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(_dot);
            row.Children.Add(_time);
            row.Children.Add(_pausedText);
            if (_screens.Count >= 3) row.Children.Add(BuildScreenButton());
            row.Children.Add(stop);

            var card = new Border
            {
                Padding = new Thickness(14, 10, 12, 10),
                BorderThickness = new Thickness(1),
                Child = row,
                Cursor = Cursors.SizeAll,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBg");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null) return;
                try { DragMove(); }
                catch (InvalidOperationException) { }
            };
            Content = card;

            SourceInitialized += (_, _) => ExcludeFromCapture();
            Loaded += (_, _) => PlaceAtBottomCenter();
        }

        public void SetElapsed(string text) => _time.Text = text;

        /// <summary>Recording is waiting for a Windows security prompt to close: amber dot, an explanation, and a still clock.</summary>
        public void SetPaused(bool paused)
        {
            _dot.SetResourceReference(Shape.FillProperty, paused ? "StatusWarningFg" : "StatusErrorFg");
            _pausedText.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Which screen is being recorded now, so the menu can tick it.</summary>
        public void SetCurrentRegion(RecordRegion? region) => _currentRegion = region;

        private Button BuildScreenButton()
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(10, 4, 10, 4),
                Content = new PackIcon { Kind = PackIconKind.MonitorMultiple, Width = 18, Height = 18 },
                ToolTip = Application.Current?.TryFindResource("Str_Recorder_SwitchScreen") as string ?? "Switch the screen being recorded",
            };
            if (Application.Current?.TryFindResource("NeutralIconButtonStyle") is Style neutral) button.Style = neutral;

            button.Click += (_, _) =>
            {
                var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
                foreach (var screen in _screens)
                {
                    var item = new MenuItem { Header = screen.Name, IsCheckable = false, IsChecked = Equals(screen.Region, _currentRegion) };
                    item.Click += (_, _) =>
                    {
                        _currentRegion = screen.Region;
                        ScreenChosen?.Invoke(screen);
                    };
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            };
            return button;
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            for (var current = child; current != null; current = VisualTreeHelper.GetParent(current))
                if (current is T match) return match;
            return null;
        }

        private void PlaceAtBottomCenter()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 24;
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private void ExcludeFromCapture()
        {
            try
            {
                SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WdaExcludeFromCapture);
            }
            catch (EntryPointNotFoundException)
            {
                // Not available on this Windows version: the bar will be in the video.
            }
        }
    }
}
