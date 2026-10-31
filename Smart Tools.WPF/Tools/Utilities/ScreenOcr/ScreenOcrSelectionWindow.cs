using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Full-screen "drag over the text" overlay for the OCR Text Extractor.
    /// Covers every monitor with a frozen, dimmed picture of the screen; the
    /// dragged rectangle shows through undimmed. Esc or right-click cancels.
    /// The selection is returned in the picture's pixels, so it maps 1:1 to
    /// what the user saw, whatever the display scaling.
    /// </summary>
    internal sealed class ScreenOcrSelectionWindow : Window
    {
        private readonly int _pixelWidth, _pixelHeight;
        private readonly Canvas _canvas = new();
        private readonly Path _dim;
        private readonly Rectangle _frame;
        private readonly Border _sizeBadge;
        private readonly TextBlock _sizeText;
        private readonly Border _hint;
        private Point? _start;

        /// <summary>The chosen area in the picture's pixels; null when cancelled.</summary>
        public Int32Rect? Selection { get; private set; }

        /// <param name="title">Window title; the OCR wording when null.</param>
        /// <param name="hint">The line shown at the top; the OCR wording when null. Other tools (Screenshot) pass their own.</param>
        public ScreenOcrSelectionWindow(BitmapSource screen, Rect virtualScreenDips, string? title = null, string? hint = null)
        {
            _pixelWidth = screen.PixelWidth;
            _pixelHeight = screen.PixelHeight;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = virtualScreenDips.Left;
            Top = virtualScreenDips.Top;
            Width = virtualScreenDips.Width;
            Height = virtualScreenDips.Height;
            Cursor = Cursors.Cross;
            Background = Brushes.Black;
            Title = title ?? TryFindResource("Str_Ocr_SelectionTitle") as string ?? "Select the text to read";

            var picture = new Image { Source = screen, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);

            // Dimmed everywhere except the selection: an even-odd geometry with the selection as a hole.
            _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), IsHitTestVisible = false };

            _frame = new Rectangle
            {
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _frame.SetResourceReference(Shape.StrokeProperty, "AccentColor");

            _sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            _sizeBadge = new Border
            {
                Padding = new Thickness(7, 2, 7, 3),
                CornerRadius = new CornerRadius(6),
                Child = _sizeText,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _sizeBadge.SetResourceReference(Border.BackgroundProperty, "AccentColor");

            _hint = new Border
            {
                Margin = new Thickness(0, 28, 0, 0),
                Padding = new Thickness(16, 9, 16, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1B, 0x1E, 0x24)),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 13,
                    Text = hint ?? TryFindResource("Str_Ocr_SelectionHint") as string ?? "Drag over the text you want to read  ·  Esc or right-click to cancel",
                },
            };

            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_sizeBadge);

            var root = new Grid();
            root.Children.Add(picture);
            root.Children.Add(_canvas);
            root.Children.Add(_hint);
            Content = root;

            Loaded += (_, _) =>
            {
                UpdateDim(null);
                Activate();
                Focus();
            };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += (_, _) => Close();
            KeyDown += OnKey;
            Deactivated += (_, _) => { if (IsLoaded && Selection == null) Close(); };   // Alt+Tab away = cancel
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(_canvas);
            CaptureMouse();
            _hint.Visibility = Visibility.Collapsed;
            UpdateSelection(_start.Value);
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_start != null && e.LeftButton == MouseButtonState.Pressed) UpdateSelection(e.GetPosition(_canvas));
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_start is not Point start) return;
            ReleaseMouseCapture();
            var rect = ToPixels(new Rect(start, e.GetPosition(_canvas)));
            _start = null;

            if (rect.Width < Controllers.ScreenOcrManager.MinSelectionPixels || rect.Height < Controllers.ScreenOcrManager.MinSelectionPixels)
            {
                // A click, not a drag: start again rather than read a few pixels.
                UpdateDim(null);
                _frame.Visibility = _sizeBadge.Visibility = Visibility.Collapsed;
                _hint.Visibility = Visibility.Visible;
                return;
            }
            Selection = rect;
            Close();
        }

        private void UpdateSelection(Point current)
        {
            var r = new Rect(_start!.Value, current);
            UpdateDim(r);

            Canvas.SetLeft(_frame, r.X);
            Canvas.SetTop(_frame, r.Y);
            _frame.Width = r.Width;
            _frame.Height = r.Height;
            _frame.Visibility = Visibility.Visible;

            var px = ToPixels(r);
            _sizeText.Text = $"{px.Width} × {px.Height}";
            _sizeBadge.Visibility = Visibility.Visible;
            _sizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double below = r.Bottom + 6, above = r.Top - _sizeBadge.DesiredSize.Height - 6;
            Canvas.SetLeft(_sizeBadge, Math.Min(r.X, Math.Max(0, ActualWidth - _sizeBadge.DesiredSize.Width)));
            Canvas.SetTop(_sizeBadge, below + _sizeBadge.DesiredSize.Height < ActualHeight ? below : Math.Max(0, above));
        }

        private void UpdateDim(Rect? hole)
        {
            var all = new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height)));
            if (hole is not Rect h)
            {
                _dim.Data = all;
                return;
            }
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(all);
            group.Children.Add(new RectangleGeometry(h));
            _dim.Data = group;
        }

        /// <summary>Overlay units to the frozen picture's pixels (the picture is stretched over the whole window).</summary>
        private Int32Rect ToPixels(Rect r)
        {
            double sx = ActualWidth > 0 ? _pixelWidth / ActualWidth : 1;
            double sy = ActualHeight > 0 ? _pixelHeight / ActualHeight : 1;
            int x = (int)Math.Floor(Math.Max(0, r.X) * sx), y = (int)Math.Floor(Math.Max(0, r.Y) * sy);
            int right = (int)Math.Ceiling(Math.Min(ActualWidth, r.Right) * sx), bottom = (int)Math.Ceiling(Math.Min(ActualHeight, r.Bottom) * sy);
            return new Int32Rect(x, y, Math.Max(0, Math.Min(_pixelWidth, right) - x), Math.Max(0, Math.Min(_pixelHeight, bottom) - y));
        }

        /// <summary>Wraps a captured screen for the overlay (frozen, so it can be shown right away).</summary>
        public static BitmapSource ToBitmapSource(ScreenOcrImage image)
        {
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
