using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The full-screen window of the HDR &amp; Color Depth Checker: one picture over the whole of a chosen screen.
    /// Left/Right change the picture, Space runs a slideshow, Up/Down change the white window size, H shows or hides the caption, Esc ends.
    /// Placed with exact pixel coordinates, so it covers the screen edge to edge whatever the Windows scaling. A fixed black test surface: no theme colours.
    /// </summary>
    internal sealed class MonitorHdrWindow : Window
    {
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpShowWindow = 0x0040;

        private readonly MonitorHdrInfo _screen;
        private readonly IReadOnlyList<MonitorHdrPattern> _patterns;
        private readonly Func<string, string> _nameOf;
        private readonly Func<string, string> _descOf;
        private readonly Func<string> _keys;
        private readonly MonitorHdrPatternSurface _surface = new() { ShowLabels = true };
        private readonly Border _caption;
        private readonly TextBlock _title, _desc, _keysText;
        private readonly DispatcherTimer _captionTimer;
        private readonly DispatcherTimer _slideTimer;

        private int _index;
        private int _sizeIndex = 2;
        private bool _hints;
        private bool _slideshow;
        private int _slideSeconds;

        public MonitorHdrWindow(MonitorHdrInfo screen, IReadOnlyList<MonitorHdrPattern> patterns, int startIndex, Func<string, string> nameOf, Func<string, string> descOf, Func<string> keys, int slideshowSeconds, bool showHints)
        {
            _screen = screen;
            _patterns = patterns;
            _nameOf = nameOf;
            _descOf = descOf;
            _keys = keys;
            _hints = showHints;
            _slideSeconds = slideshowSeconds > 0 ? slideshowSeconds : 5;
            _slideshow = slideshowSeconds > 0;
            _index = Math.Clamp(startIndex, 0, patterns.Count - 1);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = screen.X + 40;
            Top = screen.Y + 40;
            Width = 400;
            Height = 300;
            Cursor = Cursors.None;
            Title = "HDR & Color Depth";
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _title = new TextBlock { Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold };
            _desc = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xCB, 0xD8)), FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
            _keysText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x8D, 0x97, 0xA8)), FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
            var stack = new StackPanel();
            stack.Children.Add(_title);
            stack.Children.Add(_desc);
            stack.Children.Add(_keysText);
            _caption = new Border
            {
                Margin = new Thickness(24),
                Padding = new Thickness(16, 12, 18, 12),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x17, 0x1C)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = stack,
            };

            var root = new Grid();
            root.Children.Add(_surface);
            root.Children.Add(_caption);
            Content = root;

            _captionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _captionTimer.Tick += (_, _) =>
            {
                _captionTimer.Stop();
                _caption.Visibility = Visibility.Collapsed;
            };

            _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_slideSeconds) };
            _slideTimer.Tick += (_, _) => Step(1);

            SourceInitialized += (_, _) => PlaceOnScreen();
            Loaded += (_, _) => { Focus(); ShowPattern(); if (_slideshow) _slideTimer.Start(); };
            Closed += (_, _) => { _captionTimer.Stop(); _slideTimer.Stop(); };
            KeyDown += OnKey;
            MouseLeftButtonDown += (_, _) => Step(1);
            MouseRightButtonDown += (_, _) => Close();
        }

        private void PlaceOnScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HwndTopmost, _screen.X, _screen.Y, _screen.Width, _screen.Height, SwpShowWindow);
        }

        private void ShowPattern()
        {
            var p = _patterns[_index];
            _surface.PatternId = p.Id;
            _surface.Primaries = _screen.Primaries;
            _surface.WindowPercent = MonitorHdrManager.WhiteWindowPercents[_sizeIndex];
            _surface.ShowLabels = _hints;
            _title.Text = $"{_index + 1} / {_patterns.Count}   {_nameOf(p.Id)}";
            _desc.Text = _descOf(p.Id);
            _keysText.Text = _keys();
            if (_hints)
            {
                _caption.Visibility = Visibility.Visible;
                _captionTimer.Stop();
                _captionTimer.Start();
            }
        }

        private void Step(int direction)
        {
            _index = (_index + direction + _patterns.Count) % _patterns.Count;
            ShowPattern();
        }

        private void ToggleSlideshow()
        {
            _slideshow = !_slideshow;
            if (_slideshow) { _slideTimer.Interval = TimeSpan.FromSeconds(_slideSeconds); _slideTimer.Start(); }
            else _slideTimer.Stop();
            ShowPattern();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Space: ToggleSlideshow(); break;
                case Key.Right or Key.PageDown: Step(1); break;
                case Key.Left or Key.Back or Key.PageUp: Step(-1); break;
                case Key.Up:
                    _sizeIndex = Math.Min(_sizeIndex + 1, MonitorHdrManager.WhiteWindowPercents.Length - 1);
                    ShowPattern();
                    break;
                case Key.Down:
                    _sizeIndex = Math.Max(_sizeIndex - 1, 0);
                    ShowPattern();
                    break;
                case Key.H:
                    _hints = !_hints;
                    if (_hints) ShowPattern();
                    else { _surface.ShowLabels = false; _caption.Visibility = Visibility.Collapsed; }
                    break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
