using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>A pointer position over the picture, in picture pixels (can be outside it while dragging).</summary>
    public sealed class PixelPointerEventArgs : EventArgs
    {
        public int X { get; init; }
        public int Y { get; init; }
        public bool IsRightButton { get; init; }
    }

    /// <summary>
    /// Draws the picture at a whole-number zoom with sharp pixels, a see-through checkerboard, an optional
    /// grid, onion skin, mirror guides and a ghost of the shape being dragged, and reports where the
    /// pointer is in picture pixels. It owns no editing logic: <see cref="PixelArtManager"/> does that.
    /// Colours come in as brush properties bound to theme tokens; nothing is read from resources while drawing.
    /// </summary>
    public sealed class PixelCanvasControl : FrameworkElement
    {
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
            nameof(Zoom), typeof(int), typeof(PixelCanvasControl),
            new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, null, (_, v) => Math.Clamp((int)v, 1, 64)));

        public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
            nameof(ShowGrid), typeof(bool), typeof(PixelCanvasControl), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowOnionProperty = DependencyProperty.Register(
            nameof(ShowOnion), typeof(bool), typeof(PixelCanvasControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CheckerLightProperty = BrushProperty(nameof(CheckerLight));
        public static readonly DependencyProperty CheckerDarkProperty = BrushProperty(nameof(CheckerDark));
        public static readonly DependencyProperty GridBrushProperty = BrushProperty(nameof(GridBrush));
        public static readonly DependencyProperty GuideBrushProperty = BrushProperty(nameof(GuideBrush));

        private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
            name, typeof(Brush), typeof(PixelCanvasControl), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public int Zoom { get => (int)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
        public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
        public bool ShowOnion { get => (bool)GetValue(ShowOnionProperty); set => SetValue(ShowOnionProperty, value); }
        public Brush CheckerLight { get => (Brush)GetValue(CheckerLightProperty); set => SetValue(CheckerLightProperty, value); }
        public Brush CheckerDark { get => (Brush)GetValue(CheckerDarkProperty); set => SetValue(CheckerDarkProperty, value); }
        public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush GuideBrush { get => (Brush)GetValue(GuideBrushProperty); set => SetValue(GuideBrushProperty, value); }

        public PixelArtManager? Manager { get; set; }
        public PixelArtSymmetry Symmetry { get; set; }

        /// <summary>Pixels of the shape being dragged, drawn faintly in <see cref="GhostColor"/> (0 = drawn as an outline).</summary>
        public IReadOnlyList<(int X, int Y)>? Ghost { get; set; }
        public uint GhostColor { get; set; }

        public event EventHandler<PixelPointerEventArgs>? PixelPressed;
        public event EventHandler<PixelPointerEventArgs>? PixelMoved;
        public event EventHandler<PixelPointerEventArgs>? PixelReleased;
        public event EventHandler? PointerLeft;

        /// <summary>Ctrl + wheel: +1 / -1 zoom steps asked for.</summary>
        public event EventHandler<int>? ZoomWheel;

        private WriteableBitmap? _bitmap, _onion;
        private (int X, int Y)? _hover;
        private bool _capturing;

        public PixelCanvasControl()
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            SnapsToDevicePixels = true;
            Cursor = Cursors.Cross;
            Focusable = true;
        }

        /// <summary>Copies the current frame (and the one before it, for onion skin) into the pictures and redraws.</summary>
        public void Refresh()
        {
            var manager = Manager;
            if (manager == null) return;
            var doc = manager.Document;
            if (_bitmap == null || _bitmap.PixelWidth != doc.Width || _bitmap.PixelHeight != doc.Height)
            {
                _bitmap = new WriteableBitmap(doc.Width, doc.Height, 96, 96, PixelFormats.Bgra32, null);
                _onion = new WriteableBitmap(doc.Width, doc.Height, 96, 96, PixelFormats.Bgra32, null);
                InvalidateMeasure();
            }
            var rect = new Int32Rect(0, 0, doc.Width, doc.Height);
            _bitmap.WritePixels(rect, manager.Pixels, doc.Width * 4, 0);
            if (manager.CurrentFrame > 0) _onion!.WritePixels(rect, doc.Frames[manager.CurrentFrame - 1], doc.Width * 4, 0);
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var doc = Manager?.Document;
            return doc == null ? new Size(0, 0) : new Size(doc.Width * Zoom, doc.Height * Zoom);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var manager = Manager;
            if (manager == null || _bitmap == null) return;
            var doc = manager.Document;
            int z = Zoom, w = doc.Width, h = doc.Height;
            var area = new Rect(0, 0, w * z, h * z);

            // Checkerboard: a tile of 2 x 2 squares of 8 px, repeated.
            var tile = new DrawingGroup();
            tile.Children.Add(new GeometryDrawing(CheckerLight, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            tile.Children.Add(new GeometryDrawing(CheckerDark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            tile.Children.Add(new GeometryDrawing(CheckerDark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
            var checker = new DrawingBrush(tile) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
            dc.DrawRectangle(checker, null, area);

            if (ShowOnion && manager.CurrentFrame > 0 && _onion != null)
            {
                dc.PushOpacity(0.35);
                dc.DrawImage(_onion, area);
                dc.Pop();
            }
            dc.DrawImage(_bitmap, area);

            if (ShowGrid && z >= 6)
            {
                var pen = new Pen(GridBrush, 1);
                pen.Freeze();
                dc.PushOpacity(0.35);
                for (int x = 1; x < w; x++) dc.DrawLine(pen, new Point(x * z + 0.5, 0), new Point(x * z + 0.5, h * z));
                for (int y = 1; y < h; y++) dc.DrawLine(pen, new Point(0, y * z + 0.5), new Point(w * z, y * z + 0.5));
                dc.Pop();
            }

            if (Symmetry != PixelArtSymmetry.None)
            {
                var guide = new Pen(GuideBrush, 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                guide.Freeze();
                if (Symmetry is PixelArtSymmetry.Horizontal or PixelArtSymmetry.Both)
                    dc.DrawLine(guide, new Point(w * z / 2.0, 0), new Point(w * z / 2.0, h * z));
                if (Symmetry is PixelArtSymmetry.Vertical or PixelArtSymmetry.Both)
                    dc.DrawLine(guide, new Point(0, h * z / 2.0), new Point(w * z, h * z / 2.0));
            }

            if (Ghost is { Count: > 0 } ghost)
            {
                byte a = (byte)(GhostColor >> 24), r = (byte)(GhostColor >> 16), g = (byte)(GhostColor >> 8), b = (byte)GhostColor;
                Brush fill = GhostColor == 0 ? GuideBrush : new SolidColorBrush(Color.FromArgb(a, r, g, b));
                dc.PushOpacity(0.7);
                foreach (var (x, y) in ghost)
                    if (x >= 0 && y >= 0 && x < w && y < h) dc.DrawRectangle(fill, null, new Rect(x * z, y * z, z, z));
                dc.Pop();
            }

            if (_hover is { } hv && hv.X >= 0 && hv.Y >= 0 && hv.X < w && hv.Y < h)
            {
                var outer = new Pen(Brushes.Black, 1);
                var inner = new Pen(Brushes.White, 1);
                dc.DrawRectangle(null, outer, new Rect(hv.X * z + 0.5, hv.Y * z + 0.5, Math.Max(1, z - 1), Math.Max(1, z - 1)));
                if (z >= 4) dc.DrawRectangle(null, inner, new Rect(hv.X * z + 1.5, hv.Y * z + 1.5, Math.Max(1, z - 3), Math.Max(1, z - 3)));
            }
        }

        // =================================================================
        //  Pointer
        // =================================================================

        private (int X, int Y) PixelAt(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            return ((int)Math.Floor(p.X / Zoom), (int)Math.Floor(p.Y / Zoom));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            Focus();
            CaptureMouse();
            _capturing = true;
            var (x, y) = PixelAt(e);
            PixelPressed?.Invoke(this, new PixelPointerEventArgs { X = x, Y = y, IsRightButton = e.ChangedButton == MouseButton.Right });
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var (x, y) = PixelAt(e);
            if (_hover != (x, y))
            {
                _hover = (x, y);
                InvalidateVisual();
            }
            PixelMoved?.Invoke(this, new PixelPointerEventArgs { X = x, Y = y, IsRightButton = e.RightButton == MouseButtonState.Pressed });
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            if (!_capturing || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            _capturing = false;
            ReleaseMouseCapture();
            var (x, y) = PixelAt(e);
            PixelReleased?.Invoke(this, new PixelPointerEventArgs { X = x, Y = y, IsRightButton = e.ChangedButton == MouseButton.Right });
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (_capturing) return;                       // keep following the pointer while it is held down
            _hover = null;
            InvalidateVisual();
            PointerLeft?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;     // plain wheel scrolls the page
            ZoomWheel?.Invoke(this, e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }
    }
}
