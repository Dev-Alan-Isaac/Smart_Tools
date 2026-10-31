using Smart_Tools.WPF.Controllers;
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
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The full-screen eyedropper. Covers every monitor with a frozen picture of the screen and
    /// follows the pointer with a magnifier (the pixels around it, enlarged, with the one that will
    /// be picked outlined) and a live colour read-out. Click picks and closes; Shift+click picks and
    /// keeps going; arrow keys nudge by one pixel; the wheel changes the magnification; Esc or
    /// right-click cancels. It is a fixed dark tool surface, so its colours don't follow the theme.
    /// </summary>
    internal sealed class ScreenColorPickerOverlay : Window
    {
        private static readonly int[] GridSizes = { 9, 15, 21 };
        private const double LoupeSize = 168;

        private readonly ScreenColorImage _image;
        private readonly ScreenColorSample _sample;
        private readonly Action<ScreenPick> _picked;
        private readonly Rect _virtualScreenPixels;

        private readonly Canvas _canvas = new();
        private readonly Border _loupe;
        private readonly Image _zoomImage;
        private readonly Border _readout;
        private readonly Border _swatch;
        private readonly TextBlock _hexText;
        private readonly TextBlock _posText;
        private readonly Border _hint;

        private int _gridIndex = 1;
        private WriteableBitmap _zoomBitmap;
        private int _px = -1, _py = -1;
        private bool _closing;

        public int PicksMade { get; private set; }

        public ScreenColorPickerOverlay(ScreenColorImage image, Rect virtualScreenDips, Rect virtualScreenPixels, ScreenColorSample sample, Action<ScreenPick> picked)
        {
            _image = image;
            _sample = sample;
            _picked = picked;
            _virtualScreenPixels = virtualScreenPixels;

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
            Title = "Pick a colour";

            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
            bitmap.Freeze();
            var picture = new Image { Source = bitmap, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);

            _zoomBitmap = NewZoomBitmap();
            _zoomImage = new Image { Source = _zoomBitmap, Stretch = Stretch.Fill, Width = LoupeSize, Height = LoupeSize };
            RenderOptions.SetBitmapScalingMode(_zoomImage, BitmapScalingMode.NearestNeighbor);

            // The pixel that will be picked: a box in the middle of the magnified grid.
            var centreBox = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
            var centreBoxInner = new Rectangle { Stroke = Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false };

            var loupeGrid = new Grid { Width = LoupeSize, Height = LoupeSize, ClipToBounds = true };
            loupeGrid.Children.Add(_zoomImage);
            loupeGrid.Children.Add(centreBox);
            loupeGrid.Children.Add(centreBoxInner);
            loupeGrid.SizeChanged += (_, _) => PlaceCentreBoxes(centreBox, centreBoxInner);
            Loaded += (_, _) => PlaceCentreBoxes(centreBox, centreBoxInner);

            _loupe = new Border
            {
                Width = LoupeSize + 6,
                Height = LoupeSize + 6,
                CornerRadius = new CornerRadius(14),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(3),
                Background = Brushes.Black,
                Child = loupeGrid,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            loupeGrid.Clip = new RectangleGeometry(new Rect(0, 0, LoupeSize, LoupeSize), 11, 11);

            _swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5) };
            _hexText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _posText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xBC, 0xCB)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var readoutRow = new StackPanel { Orientation = Orientation.Horizontal };
            readoutRow.Children.Add(_swatch);
            readoutRow.Children.Add(_hexText);
            readoutRow.Children.Add(_posText);
            _readout = new Border
            {
                Padding = new Thickness(9, 6, 11, 6),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x1B, 0x1E, 0x24)),
                Child = readoutRow,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

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
                    Text = "Click to pick  ·  Shift+click to keep picking  ·  Arrows nudge 1 px  ·  Wheel zooms  ·  Esc to finish",
                },
            };

            _canvas.Children.Add(_loupe);
            _canvas.Children.Add(_readout);

            var root = new Grid();
            root.Children.Add(picture);
            root.Children.Add(_canvas);
            root.Children.Add(_hint);
            Content = root;

            Loaded += (_, _) =>
            {
                Activate();
                Focus();
            };
            MouseMove += OnMove;
            MouseLeftButtonDown += OnClick;
            MouseRightButtonUp += (_, _) => Finish();
            MouseWheel += OnWheel;
            KeyDown += OnKey;
            Deactivated += (_, _) => { if (IsLoaded) Finish(); };   // Alt+Tab away = done
        }

        private void Finish()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }

        private WriteableBitmap NewZoomBitmap()
        {
            int n = GridSizes[_gridIndex];
            return new WriteableBitmap(n, n, 96, 96, PixelFormats.Bgra32, null);
        }

        private void PlaceCentreBoxes(Rectangle outer, Rectangle inner)
        {
            double cell = LoupeSize / GridSizes[_gridIndex];
            double offset = (LoupeSize - cell) / 2;
            foreach (var (r, grow) in new[] { (outer, 0.0), (inner, 2.0) })
            {
                r.Width = cell + grow * 2;
                r.Height = cell + grow * 2;
                r.HorizontalAlignment = HorizontalAlignment.Left;
                r.VerticalAlignment = VerticalAlignment.Top;
                r.Margin = new Thickness(offset - grow, offset - grow, 0, 0);
            }
        }

        // =================================================================
        //  Pointer -> pixel
        // =================================================================

        private (int X, int Y) ToPixel(Point p)
        {
            double sx = ActualWidth > 0 ? _image.Width / ActualWidth : 1;
            double sy = ActualHeight > 0 ? _image.Height / ActualHeight : 1;
            return (Math.Clamp((int)Math.Floor(p.X * sx), 0, _image.Width - 1), Math.Clamp((int)Math.Floor(p.Y * sy), 0, _image.Height - 1));
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            var (x, y) = ToPixel(e.GetPosition(this));
            ShowAt(x, y, e.GetPosition(this));
        }

        private void ShowAt(int x, int y, Point overlayPoint)
        {
            _px = x;
            _py = y;
            if (_hint.Visibility == Visibility.Visible) _hint.Visibility = Visibility.Collapsed;

            RenderZoom(x, y);
            var colour = ScreenColorPickerManager.Sample(_image, x, y, _sample) ?? new RgbColor(0, 0, 0);
            _swatch.Background = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
            _hexText.Text = colour.Hex;
            _posText.Text = $"{x}, {y}";

            _loupe.Visibility = _readout.Visibility = Visibility.Visible;
            _readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Sits down-right of the pointer, and flips to the other side near an edge so it is never cut off.
            double lw = _loupe.Width, lh = _loupe.Height + 6 + _readout.DesiredSize.Height;
            double left = overlayPoint.X + 26, top = overlayPoint.Y + 26;
            if (left + lw > ActualWidth) left = overlayPoint.X - 26 - lw;
            if (top + lh > ActualHeight) top = overlayPoint.Y - 26 - lh;
            left = Math.Max(0, left);
            top = Math.Max(0, top);
            Canvas.SetLeft(_loupe, left);
            Canvas.SetTop(_loupe, top);
            Canvas.SetLeft(_readout, left);
            Canvas.SetTop(_readout, top + _loupe.Height + 6);
        }

        /// <summary>Redraws the magnifier: the n x n pixels around (cx, cy), black beyond the screen's edge.</summary>
        private void RenderZoom(int cx, int cy)
        {
            int n = GridSizes[_gridIndex];
            int half = n / 2;
            var buffer = new byte[n * n * 4];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int o = (j * n + i) * 4;
                    if (ScreenColorPickerManager.PixelAt(_image, cx - half + i, cy - half + j) is RgbColor c)
                    {
                        buffer[o] = c.B;
                        buffer[o + 1] = c.G;
                        buffer[o + 2] = c.R;
                    }
                    buffer[o + 3] = 255;
                }
            _zoomBitmap.WritePixels(new Int32Rect(0, 0, n, n), buffer, n * 4, 0);
        }

        // =================================================================
        //  Picking and keys
        // =================================================================

        private void OnClick(object sender, MouseButtonEventArgs e)
        {
            var (x, y) = ToPixel(e.GetPosition(this));
            Pick(x, y, keepGoing: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        }

        private void Pick(int x, int y, bool keepGoing)
        {
            if (ScreenColorPickerManager.Sample(_image, x, y, _sample) is not RgbColor colour) return;
            PicksMade++;
            _picked(new ScreenPick(colour, x, y, DateTime.Now));
            if (!keepGoing) Finish();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Finish();
                    break;
                case Key.Left: Nudge(-1, 0); e.Handled = true; break;
                case Key.Right: Nudge(1, 0); e.Handled = true; break;
                case Key.Up: Nudge(0, -1); e.Handled = true; break;
                case Key.Down: Nudge(0, 1); e.Handled = true; break;
                case Key.Enter:
                case Key.Space:
                    if (_px >= 0) Pick(_px, _py, keepGoing: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Moves the real pointer one screen pixel, so the keyboard can reach the exact pixel wanted.</summary>
        private void Nudge(int dx, int dy)
        {
            if (_px < 0) return;
            int nx = Math.Clamp(_px + dx, 0, _image.Width - 1), ny = Math.Clamp(_py + dy, 0, _image.Height - 1);
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)_virtualScreenPixels.Left + nx, (int)_virtualScreenPixels.Top + ny);
            // Moving the cursor raises MouseMove, which redraws; do it here too in case the position did not change.
            double ox = nx * (ActualWidth / _image.Width), oy = ny * (ActualHeight / _image.Height);
            ShowAt(nx, ny, new Point(ox, oy));
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            int next = Math.Clamp(_gridIndex + (e.Delta > 0 ? -1 : 1), 0, GridSizes.Length - 1);   // fewer cells = bigger pixels
            if (next == _gridIndex) return;
            _gridIndex = next;
            _zoomBitmap = NewZoomBitmap();
            _zoomImage.Source = _zoomBitmap;
            if (_loupe.Child is Grid g && g.Children.Count >= 3 && g.Children[1] is Rectangle outer && g.Children[2] is Rectangle inner)
                PlaceCentreBoxes(outer, inner);
            if (_px >= 0) RenderZoom(_px, _py);
        }
    }
}
