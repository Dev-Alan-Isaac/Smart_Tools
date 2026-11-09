using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Drawing by hand on the character grid: set cells, lines, outlines, flood fill and typed text,
    /// with undo and redo. One mouse drag is one undo step (the same idea as the Pixel Art editor:
    /// the first time a stroke touches a cell it remembers what was there).
    /// </summary>
    public sealed class AsciiEditor
    {
        private sealed record Change(int[] Index, char[] OldChar, uint[] OldColor, char[] NewChar, uint[] NewColor);

        private readonly List<Change> _undo = new(), _redo = new();
        private Dictionary<int, (char Ch, uint Color)>? _stroke;
        private const int MaxUndo = 200;

        public AsciiGrid Grid { get; private set; } = new(80, 40);

        /// <summary>Mirror everything left to right while drawing.</summary>
        public bool Symmetry { get; set; }

        public bool IsDirty { get; private set; }
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>First and last row that changed (the preview redraws only those rows); (0, Rows - 1) after a load or resize.</summary>
        public event Action<int, int>? Changed;

        public void Load(AsciiGrid grid, bool dirty)
        {
            EndStroke();
            Grid = grid;
            _undo.Clear();
            _redo.Clear();
            IsDirty = dirty;
            Changed?.Invoke(0, Grid.Rows - 1);
        }

        public void Resize(int cols, int rows) => Load(Grid.Resized(cols, rows), IsDirty);

        // ---------------- Stroke ----------------

        public void BeginStroke()
        {
            EndStroke();
            _stroke = new();
        }

        public void EndStroke()
        {
            if (_stroke == null) return;
            var stroke = _stroke;
            _stroke = null;
            if (stroke.Count == 0) return;
            var index = stroke.Keys.ToArray();
            var change = new Change(index,
                index.Select(i => stroke[i].Ch).ToArray(), index.Select(i => stroke[i].Color).ToArray(),
                index.Select(i => Grid.Chars[i]).ToArray(), index.Select(i => Grid.Colors[i]).ToArray());
            // Nothing really changed (painted the same character again): no undo step.
            bool any = false;
            for (int k = 0; k < index.Length && !any; k++)
                any = change.OldChar[k] != change.NewChar[k] || change.OldColor[k] != change.NewColor[k];
            if (!any) return;
            _undo.Add(change);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
            IsDirty = true;
        }

        private int _dirtyMin = int.MaxValue, _dirtyMax = -1;

        private void SetOne(int x, int y, char ch, uint color)
        {
            if (!Grid.Contains(x, y)) return;
            int i = y * Grid.Cols + x;
            _stroke ??= new();
            if (!_stroke.ContainsKey(i)) _stroke[i] = (Grid.Chars[i], Grid.Colors[i]);
            Grid.Chars[i] = ch;
            Grid.Colors[i] = color;
            if (y < _dirtyMin) _dirtyMin = y;
            if (y > _dirtyMax) _dirtyMax = y;
        }

        /// <summary>Put a character (and colour) in a cell; mirrored too when symmetry is on. Call inside a stroke.</summary>
        public void Set(int x, int y, char ch, uint color)
        {
            SetOne(x, y, ch, color);
            if (Symmetry) SetOne(Grid.Cols - 1 - x, y, ch, color);
        }

        private void Flush()
        {
            if (_dirtyMax < 0) return;
            int a = _dirtyMin, b = _dirtyMax;
            _dirtyMin = int.MaxValue;
            _dirtyMax = -1;
            Changed?.Invoke(a, b);
        }

        /// <summary>Tell the preview about the cells touched since the last call (during a drag).</summary>
        public void Commit() => Flush();

        // ---------------- Shapes ----------------

        public void Line(int x0, int y0, int x1, int y1, char ch, uint color)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Set(x0, y0, ch, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        public void Rectangle(int x0, int y0, int x1, int y1, char ch, uint color)
        {
            Line(x0, y0, x1, y0, ch, color);
            Line(x1, y0, x1, y1, ch, color);
            Line(x1, y1, x0, y1, ch, color);
            Line(x0, y1, x0, y0, ch, color);
        }

        /// <summary>Replace the connected cells that look like the clicked one (same character and colour).</summary>
        public void Fill(int x, int y, char ch, uint color)
        {
            if (!Grid.Contains(x, y)) return;
            char target = Grid[x, y];
            uint targetColor = Grid.ColorAt(x, y);
            if (target == ch && targetColor == color) return;
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue((x, y));
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                if (!Grid.Contains(cx, cy) || Grid[cx, cy] != target || Grid.ColorAt(cx, cy) != targetColor) continue;
                Set(cx, cy, ch, color);
                queue.Enqueue((cx + 1, cy));
                queue.Enqueue((cx - 1, cy));
                queue.Enqueue((cx, cy + 1));
                queue.Enqueue((cx, cy - 1));
            }
        }

        /// <summary>Type a line of text starting at a cell (stops at the right edge).</summary>
        public void Text(int x, int y, string text, uint color)
        {
            foreach (char c in text)
            {
                if (char.IsControl(c)) continue;
                if (x >= Grid.Cols) break;
                SetOne(x++, y, c, color);
            }
        }

        // ---------------- Undo / redo ----------------

        public bool Undo()
        {
            EndStroke();
            if (_undo.Count == 0) return false;
            var c = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Apply(c.Index, c.OldChar, c.OldColor);
            _redo.Add(c);
            return true;
        }

        public bool Redo()
        {
            EndStroke();
            if (_redo.Count == 0) return false;
            var c = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Apply(c.Index, c.NewChar, c.NewColor);
            _undo.Add(c);
            return true;
        }

        private void Apply(int[] index, char[] chars, uint[] colors)
        {
            int min = int.MaxValue, max = -1;
            for (int k = 0; k < index.Length; k++)
            {
                Grid.Chars[index[k]] = chars[k];
                Grid.Colors[index[k]] = colors[k];
                int y = index[k] / Grid.Cols;
                if (y < min) min = y;
                if (y > max) max = y;
            }
            IsDirty = true;
            if (max >= 0) Changed?.Invoke(min, max);
        }

        public void Clear()
        {
            BeginStroke();
            for (int y = 0; y < Grid.Rows; y++)
                for (int x = 0; x < Grid.Cols; x++)
                    if (Grid[x, y] != ' ' || Grid.ColorAt(x, y) != 0) SetOne(x, y, ' ', 0);
            EndStroke();
            Flush();
        }
    }
}
