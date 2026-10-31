using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
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
using Ellipse = System.Windows.Shapes.Ellipse;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The full-screen test window: one picture over the whole of a chosen screen. Left/Right change the picture, Space runs a
    /// slideshow, a click (or M) marks a suspect pixel, Shift+arrows move the pointer one pixel, Esc ends.
    /// The window is placed with exact pixel coordinates, so it covers the screen edge to edge whatever the Windows scaling.
    /// It is a fixed black tool surface, so its colours don't follow the theme.
    /// </summary>
    internal sealed class MonitorTestWindow : Window
    {
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpShowWindow = 0x0040;

        private readonly MonitorScreenInfo _screen;
        private readonly IReadOnlyList<MonitorPattern> _patterns;
        private readonly Func<string, string> _nameOf;
        private readonly Func<string, string> _descOf;
        private readonly Func<string, string> _text;
        private readonly Action<MonitorMark> _marked;

        private readonly MonitorPatternSurface _surface = new() { ShowLabels = true };
        private readonly Canvas _marksLayer = new() { IsHitTestVisible = false };
        private readonly Border _osd;
        private readonly Border _flashWarning;
        private readonly TextBlock _flashTitle, _flashText, _flashKeys;
        private readonly TextBlock _osdTitle, _osdDesc, _osdKeys;
        private readonly Border _cursorTag;
        private readonly TextBlock _cursorText;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _osdTimer;
        private readonly DispatcherTimer _idleTimer;
        private readonly DispatcherTimer _slideTimer;
        private readonly List<MonitorMark> _marks = new();

        private int _index;
        private double _patternStart;
        private bool _hints;
        private bool _slideshow;
        private int _slideSeconds;
        private bool _flashAccepted;        // the flashing picture was agreed to (on the page, or here)
        private int? _pendingIndex;         // a flashing picture waiting for an answer
        private int _pendingDirection = 1;

        public MonitorTestWindow(
            MonitorScreenInfo screen,
            IReadOnlyList<MonitorPattern> patterns,
            int startIndex,
            int slideshowSeconds,
            bool showHints,
            Func<string, string> nameOf,
            Func<string, string> descOf,
            Func<string, string> text,
            Action<MonitorMark> marked)
        {
            _screen = screen;
            _patterns = patterns;
            _nameOf = nameOf;
            _descOf = descOf;
            _text = text;
            _marked = marked;
            _hints = showHints;
            _slideSeconds = slideshowSeconds > 0 ? slideshowSeconds : 5;
            _slideshow = slideshowSeconds > 0;
            _index = Math.Clamp(startIndex, 0, patterns.Count - 1);
            // The page already asked when this picture is the first one or a slideshow was chosen.
            _flashAccepted = _patterns[_index].Flashing || slideshowSeconds > 0;

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
            Cursor = Cursors.Cross;
            Title = "Monitor test";
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _osdTitle = new TextBlock { Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold };
            _osdDesc = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xCB, 0xD8)), FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
            _osdKeys = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x8D, 0x97, 0xA8)), FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
            var osdStack = new StackPanel();
            osdStack.Children.Add(_osdTitle);
            osdStack.Children.Add(_osdDesc);
            osdStack.Children.Add(_osdKeys);
            _osd = new Border
            {
                Margin = new Thickness(24),
                Padding = new Thickness(16, 12, 18, 12),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x17, 0x1C)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = osdStack,
            };

            _cursorText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Consolas") };
            _cursorTag = new Border
            {
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x14, 0x17, 0x1C)),
                Child = _cursorText,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _flashTitle = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), FontSize = 20, FontWeight = FontWeights.Bold };
            _flashText = new TextBlock { Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            _flashKeys = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xCB, 0xD8)), FontSize = 12.5, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
            var warnStack = new StackPanel();
            warnStack.Children.Add(_flashTitle);
            warnStack.Children.Add(_flashText);
            warnStack.Children.Add(_flashKeys);
            _flashWarning = new Border
            {
                MaxWidth = 560,
                Padding = new Thickness(26, 22, 26, 22),
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x14, 0x17, 0x1C)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = warnStack,
            };

            var cursorCanvas = new Canvas { IsHitTestVisible = false };
            cursorCanvas.Children.Add(_cursorTag);

            var root = new Grid();
            root.Children.Add(_surface);
            root.Children.Add(_marksLayer);
            root.Children.Add(cursorCanvas);
            root.Children.Add(_osd);
            root.Children.Add(_flashWarning);
            Content = root;

            _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); FadeOsd(false); };
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _idleTimer.Tick += (_, _) =>
            {
                _idleTimer.Stop();
                Cursor = Cursors.None;
                _cursorTag.Visibility = Visibility.Collapsed;
            };
            _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_slideSeconds) };
            _slideTimer.Tick += (_, _) => Step(+1);

            SourceInitialized += (_, _) => PlaceOnScreen();
            Loaded += (_, _) =>
            {
                Activate();
                Focus();
                ShowPattern(_index);
                if (_slideshow) _slideTimer.Start();
                CompositionTarget.Rendering += OnRendering;
            };
            Closed += (_, _) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                _osdTimer.Stop();
                _idleTimer.Stop();
                _slideTimer.Stop();
            };
            KeyDown += OnKey;
            MouseMove += OnMouseMove;
            MouseLeftButtonDown += (_, e) =>
            {
                if (_pendingIndex == null) MarkAt(e.GetPosition(this));
            };
        }

        /// <summary>Exact pixels, on the chosen screen: no DPI guessing.</summary>
        private void PlaceOnScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HwndTopmost, _screen.X, _screen.Y, _screen.Width, _screen.Height, SwpShowWindow);
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            var pattern = _patterns[_index];
            if (pattern.Animated) _surface.Time = _clock.Elapsed.TotalSeconds - _patternStart;
        }

        // =================================================================
        //  Pictures
        // =================================================================

        private void ShowPattern(int index, int direction = 1)
        {
            int target = (index % _patterns.Count + _patterns.Count) % _patterns.Count;

            // Fast flashing colours: ask before the picture appears (a slideshow stops here until answered).
            if (_patterns[target].Flashing && !_flashAccepted && target != _index)
            {
                AskAboutFlashing(target, direction);
                return;
            }

            _index = target;
            var p = _patterns[_index];
            _patternStart = _clock.Elapsed.TotalSeconds;
            _surface.Time = 0;
            _surface.PatternId = p.Id;

            _osdTitle.Text = $"{_index + 1} / {_patterns.Count}   {_nameOf(p.Id)}";
            _osdDesc.Text = _descOf(p.Id);
            _osdKeys.Text = _slideshow ? string.Format(_text("Keys"), _slideSeconds) + "  ·  " + _text("SlideshowOn") : _text("Keys").Replace("{0}", "5");
            if (_hints) FadeOsd(true);
        }

        private void Step(int direction) => ShowPattern(_index + direction, direction);

        private void AskAboutFlashing(int target, int direction)
        {
            _pendingIndex = target;
            _pendingDirection = direction;
            _slideTimer.Stop();
            _flashTitle.Text = "⚠  " + _text("FlashTitle");
            _flashText.Text = _text("FlashNext").Replace("{0}", _nameOf(_patterns[target].Id));
            _flashKeys.Text = _text("FlashKeys");
            _flashWarning.Visibility = Visibility.Visible;
            Cursor = Cursors.Arrow;
        }

        /// <summary>The answer: Enter or Y shows the picture, anything else leaves it out and carries on the way it was going.</summary>
        private void AnswerFlashing(bool show)
        {
            if (_pendingIndex is not int target) return;
            _pendingIndex = null;
            _flashWarning.Visibility = Visibility.Collapsed;
            if (show)
            {
                _flashAccepted = true;
                ShowPattern(target, _pendingDirection);
            }
            else
            {
                // Skip over it; if it was the only way on, stay where we are.
                int next = (target + _pendingDirection + _patterns.Count) % _patterns.Count;
                if (!_patterns[next].Flashing) ShowPattern(next, _pendingDirection);
                else FadeOsd(_hints);
            }
            if (_slideshow) _slideTimer.Start();
        }

        private void FadeOsd(bool show)
        {
            _osd.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                _osdTimer.Stop();
                _osdTimer.Start();
            }
        }

        // =================================================================
        //  Keys and mouse
        // =================================================================

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_pendingIndex != null)
            {
                // While the warning is up, only the answer counts (and Esc still leaves the test only after answering).
                if (e.Key is Key.Enter or Key.Y) AnswerFlashing(true);
                else if (e.Key is Key.Escape or Key.N or Key.Left or Key.Right or Key.Space or Key.Back) AnswerFlashing(false);
                e.Handled = true;
                return;
            }

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    break;
                case Key.Right when !shift:
                case Key.PageDown:
                    Step(+1);
                    break;
                case Key.Left when !shift:
                case Key.PageUp:
                    Step(-1);
                    break;
                case Key.Home:
                    ShowPattern(0, +1);
                    break;
                case Key.End:
                    ShowPattern(_patterns.Count - 1, -1);
                    break;
                case Key.Space:
                    _slideshow = !_slideshow;
                    if (_slideshow) _slideTimer.Start();
                    else _slideTimer.Stop();
                    ShowPattern(_index);
                    FadeOsd(true);
                    break;
                case Key.H:
                    _hints = !_hints;
                    FadeOsd(_hints);
                    break;
                case Key.Delete:
                    _marks.Clear();
                    _marksLayer.Children.Clear();
                    break;
                case Key.M:
                    MarkAt(Mouse.GetPosition(this));
                    break;
                case Key.Left or Key.Right or Key.Up or Key.Down when shift:
                    NudgePointer(e.Key);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            Cursor = Cursors.Cross;
            _idleTimer.Stop();
            _idleTimer.Start();

            var p = e.GetPosition(this);
            var (x, y) = ToPixel(p);
            _cursorText.Text = $"{x}, {y}";
            _cursorTag.Visibility = _hints ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_cursorTag, Math.Min(p.X + 18, Math.Max(0, ActualWidth - 90)));
            Canvas.SetTop(_cursorTag, Math.Min(p.Y + 18, Math.Max(0, ActualHeight - 30)));
        }

        private (int X, int Y) ToPixel(Point p)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return (Math.Clamp((int)Math.Floor(p.X * dpi.DpiScaleX), 0, _screen.Width - 1), Math.Clamp((int)Math.Floor(p.Y * dpi.DpiScaleY), 0, _screen.Height - 1));
        }

        private void NudgePointer(Key key)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var p = Mouse.GetPosition(this);
            var (x, y) = ToPixel(p);
            int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0, dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
            x = Math.Clamp(x + dx, 0, _screen.Width - 1);
            y = Math.Clamp(y + dy, 0, _screen.Height - 1);
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(_screen.X + x, _screen.Y + y);
        }

        private void MarkAt(Point position)
        {
            var (x, y) = ToPixel(position);
            var mark = new MonitorMark(x, y, _patterns[_index].Id, DateTime.Now);
            if (!MonitorTestManager.AddMark(_marks, mark)) return;
            _marked(mark);
            DrawRing(x, y);
        }

        /// <summary>A ring around the pixel (not on it, so the pixel stays visible): white outside, black inside, readable on any colour.</summary>
        private void DrawRing(int px, int py)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double cx = (px + 0.5) / dpi.DpiScaleX, cy = (py + 0.5) / dpi.DpiScaleY;
            foreach (var (radius, thickness, brush) in new[] { (17.0, 4.0, Brushes.White), (17.0, 2.0, Brushes.Black) })
            {
                var ring = new Ellipse { Width = radius * 2, Height = radius * 2, Stroke = brush, StrokeThickness = thickness };
                Canvas.SetLeft(ring, cx - radius);
                Canvas.SetTop(ring, cy - radius);
                _marksLayer.Children.Add(ring);
            }
        }
    }
}
