using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Controls
{
    public sealed class AsciiCellEventArgs : EventArgs
    {
        public int X { get; }
        public int Y { get; }
        public bool IsRightButton { get; }
        public AsciiCellEventArgs(int x, int y, bool right) { X = x; Y = y; IsRightButton = right; }
    }

    /// <summary>
    /// Shows an <see cref="AsciiGrid"/> as text on its page colour (glyph runs, one cached drawing per
    /// row so a brush stroke only redraws the rows it touched) and, when interactive, reports the cell
    /// under the mouse. Zoom it with a LayoutTransform from outside.
    /// </summary>
    public sealed class AsciiGridControl : FrameworkElement
    {
        private const double BaseEm = 14;

        private AsciiGrid? _grid;
        private AsciiGlyphs? _glyphs;
        private bool _dark = true;
        private DrawingGroup?[] _rows = Array.Empty<DrawingGroup?>();
        private readonly AsciiBrushCache _brushes = new();
        private Brush _defaultBrush = Brushes.White;
        private Brush _pageBrush = Brushes.Black;
        private int _hoverX = -1, _hoverY = -1;
        private bool _dragging;
        private ScrollViewer? _scroller;
        private int _drawnFirst = -1, _drawnLast = -1;
        private const int RowMargin = 30;

        public AsciiGridControl()
        {
            Loaded += (_, _) =>
            {
                _scroller = FindScroller();
                if (_scroller != null) _scroller.ScrollChanged += Scroller_ScrollChanged;
            };
            Unloaded += (_, _) =>
            {
                if (_scroller != null) _scroller.ScrollChanged -= Scroller_ScrollChanged;
                _scroller = null;
            };
        }

        private ScrollViewer? FindScroller()
        {
            DependencyObject? d = this;
            while (d != null && d is not ScrollViewer) d = VisualTreeHelper.GetParent(d);
            return d as ScrollViewer;
        }

        /// <summary>Rows that can be seen right now (control coordinates), so a huge grid draws only what is on screen.</summary>
        private (int First, int Last) VisibleRows()
        {
            if (_grid == null) return (0, -1);
            if (_scroller == null || CellHeight <= 0) return (0, _grid.Rows - 1);
            try
            {
                var view = _scroller.TransformToDescendant(this).TransformBounds(new Rect(0, 0, _scroller.ViewportWidth, _scroller.ViewportHeight));
                int first = Math.Max(0, (int)Math.Floor(view.Top / CellHeight));
                int last = Math.Min(_grid.Rows - 1, (int)Math.Ceiling(view.Bottom / CellHeight));
                return (first, last);
            }
            catch (InvalidOperationException) { return (0, _grid.Rows - 1); }
        }

        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_grid == null || (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)) return;
            var (first, last) = VisibleRows();
            if (first < _drawnFirst || last > _drawnLast) InvalidateVisual();
        }

        /// <summary>When true the mouse reports cells (draw mode) and a cell outline follows it.</summary>
        public bool IsInteractive { get; set; }

        /// <summary>The cells a shape preview covers while dragging (a line or rectangle before it is committed).</summary>
        public List<(int X, int Y)>? Ghost { get; set; }

        public event EventHandler<AsciiCellEventArgs>? CellPressed;
        public event EventHandler<AsciiCellEventArgs>? CellMoved;
        public event EventHandler<AsciiCellEventArgs>? CellReleased;

        public double CellWidth => _glyphs == null ? 0 : _glyphs.CellWidthEm * BaseEm;
        public double CellHeight => _glyphs == null ? 0 : _glyphs.CellHeightEm * BaseEm;

        public void SetContent(AsciiGrid? grid, AsciiGlyphs? glyphs, bool dark)
        {
            _grid = grid;
            _glyphs = glyphs;
            _dark = dark;
            _pageBrush = new SolidColorBrush(AsciiRender.BackgroundColor(dark));
            _pageBrush.Freeze();
            _defaultBrush = new SolidColorBrush(AsciiRender.TextColor(dark));
            _defaultBrush.Freeze();
            _rows = new DrawingGroup?[grid?.Rows ?? 0];
            InvalidateMeasure();
            InvalidateVisual();
        }

        /// <summary>Redraw only these rows (after a stroke).</summary>
        public void InvalidateRows(int first, int last)
        {
            if (_grid == null) return;
            for (int y = Math.Max(0, first); y <= Math.Min(last, _rows.Length - 1); y++) _rows[y] = null;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) =>
            _grid == null || _glyphs == null ? new Size(0, 0) : new Size(_grid.Cols * CellWidth, _grid.Rows * CellHeight);

        protected override void OnRender(DrawingContext dc)
        {
            if (_grid == null || _glyphs == null) return;
            double w = _grid.Cols * CellWidth, h = _grid.Rows * CellHeight;
            dc.DrawRectangle(_pageBrush, null, new Rect(0, 0, w, h));
            var (vFirst, vLast) = VisibleRows();
            _drawnFirst = Math.Max(0, vFirst - RowMargin);
            _drawnLast = Math.Min(_grid.Rows - 1, vLast + RowMargin);
            for (int y = _drawnFirst; y <= _drawnLast && y < _rows.Length; y++)
            {
                var row = _rows[y] ??= AsciiRender.RowDrawing(_grid, _glyphs, y, BaseEm, _defaultBrush, _brushes);
                dc.DrawDrawing(row);
            }

            var outline = new Pen(_dark ? Brushes.White : Brushes.Black, 1);
            if (Ghost != null)
            {
                var ghostBrush = new SolidColorBrush(_dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0));
                ghostBrush.Freeze();
                foreach (var (gx, gy) in Ghost)
                    if (_grid.Contains(gx, gy)) dc.DrawRectangle(ghostBrush, null, new Rect(gx * CellWidth, gy * CellHeight, CellWidth, CellHeight));
            }
            if (IsInteractive && _grid.Contains(_hoverX, _hoverY))
                dc.DrawRectangle(null, outline, new Rect(_hoverX * CellWidth + 0.5, _hoverY * CellHeight + 0.5, CellWidth - 1, CellHeight - 1));
        }

        // ---------------- Mouse ----------------

        private bool CellAt(MouseEventArgs e, out int x, out int y)
        {
            x = y = -1;
            if (_grid == null || CellWidth <= 0) return false;
            var p = e.GetPosition(this);
            x = (int)Math.Floor(p.X / CellWidth);
            y = (int)Math.Floor(p.Y / CellHeight);
            return _grid.Contains(x, y);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (!IsInteractive || !CellAt(e, out int x, out int y)) return;
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            _dragging = true;
            CaptureMouse();
            Focus();
            CellPressed?.Invoke(this, new AsciiCellEventArgs(x, y, e.ChangedButton == MouseButton.Right));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!IsInteractive) return;
            bool inside = CellAt(e, out int x, out int y);
            if (!inside && !_dragging) { if (_hoverX != -1) { _hoverX = _hoverY = -1; InvalidateVisual(); } return; }
            if (_grid != null)
            {
                x = Math.Clamp(x < 0 ? (int)Math.Floor(e.GetPosition(this).X / CellWidth) : x, 0, _grid.Cols - 1);
                y = Math.Clamp(y < 0 ? (int)Math.Floor(e.GetPosition(this).Y / CellHeight) : y, 0, _grid.Rows - 1);
            }
            if (x != _hoverX || y != _hoverY)
            {
                _hoverX = x; _hoverY = y;
                InvalidateVisual();
                CellMoved?.Invoke(this, new AsciiCellEventArgs(x, y, e.RightButton == MouseButtonState.Pressed));
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            if (_grid == null) return;
            var p = e.GetPosition(this);
            int x = Math.Clamp((int)Math.Floor(p.X / CellWidth), 0, _grid.Cols - 1);
            int y = Math.Clamp((int)Math.Floor(p.Y / CellHeight), 0, _grid.Rows - 1);
            CellReleased?.Invoke(this, new AsciiCellEventArgs(x, y, e.ChangedButton == MouseButton.Right));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dragging || _hoverX == -1) return;
            _hoverX = _hoverY = -1;
            InvalidateVisual();
        }
    }
}
