using Smart_Tools.WPF.Models;
using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Pixel Art Editor logic. No WPF here, so it compiles in a plain net8.0 project.
    /// One instance holds the picture, the current frame and the undo history. Strokes are
    /// recorded as lists of changed pixels (cheap); whole-picture changes (resize, rotate,
    /// frames) are recorded as snapshots. Colours are packed ARGB (<c>0xAARRGGBB</c>), and a
    /// fully transparent pixel is always exactly 0.
    /// </summary>
    public sealed partial class PixelArtManager
    {
        private const int MaxHistory = 200;

        private abstract record Entry;
        private sealed record DiffEntry(int Frame, int[] Index, uint[] Old, uint[] New) : Entry;
        private sealed record SnapshotEntry(PixelArtDocument Before, int FrameBefore, PixelArtDocument After, int FrameAfter) : Entry;

        private readonly List<Entry> _undo = new();
        private readonly List<Entry> _redo = new();
        private Dictionary<int, uint>? _stroke;          // pixel index -> value before this stroke touched it

        public PixelArtDocument Document { get; private set; }
        public int CurrentFrame { get; private set; }

        /// <summary>True when the picture changed since it was created, loaded or exported.</summary>
        public bool IsDirty { get; private set; }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public uint[] Pixels => Document.Frames[CurrentFrame];

        /// <summary>Raised after anything that changes the picture, the frame list or the history.</summary>
        public event Action? Changed;

        public PixelArtManager(int width = 32, int height = 32) => Document = new PixelArtDocument(width, height);

        private void Raise() => Changed?.Invoke();

        // =================================================================
        //  New / load
        // =================================================================

        public void NewDocument(int width, int height)
        {
            Document = new PixelArtDocument(width, height);
            ResetState();
        }

        /// <summary>Replaces the picture with straight-alpha RGBA bytes (a PNG that was opened).</summary>
        public void LoadRgba(int width, int height, byte[] rgba)
        {
            var doc = new PixelArtDocument(width, height);
            doc.Frames[0] = FromRgba(doc.Width, doc.Height, rgba);
            Document = doc;
            ResetState();
        }

        private void ResetState()
        {
            _undo.Clear();
            _redo.Clear();
            _stroke = null;
            CurrentFrame = 0;
            IsDirty = false;
            Raise();
        }

        public void MarkSaved()
        {
            IsDirty = false;
            Raise();
        }

        // =================================================================
        //  Drawing
        // =================================================================

        /// <summary>Starts a stroke: everything stamped until <see cref="EndStroke"/> is one undo step.</summary>
        public void BeginStroke() => _stroke ??= new Dictionary<int, uint>();

        /// <summary>Puts one brush stamp (and its mirror images) on the picture. Needs <see cref="BeginStroke"/>.</summary>
        public void StrokePoint(int x, int y, uint color, int size, PixelArtSymmetry symmetry)
        {
            if (_stroke == null) return;
            color = Normalize(color);
            foreach (var (mx, my) in Mirrors(x, y, symmetry))
                Stamp(mx, my, color, size);
        }

        /// <summary>Joins two pointer positions with a line of stamps, so a fast drag leaves no gaps.</summary>
        public void StrokeLine(int x0, int y0, int x1, int y1, uint color, int size, PixelArtSymmetry symmetry)
        {
            foreach (var (x, y) in LinePoints(x0, y0, x1, y1))
                StrokePoint(x, y, color, size, symmetry);
        }

        /// <summary>Finishes the stroke and records it. Returns true when something changed.</summary>
        public bool EndStroke()
        {
            var stroke = _stroke;
            _stroke = null;
            if (stroke == null || stroke.Count == 0) return false;

            var pixels = Pixels;
            var index = new List<int>();
            var old = new List<uint>();
            var now = new List<uint>();
            foreach (var (i, before) in stroke)
            {
                if (pixels[i] == before) continue;              // painted over and back again
                index.Add(i);
                old.Add(before);
                now.Add(pixels[i]);
            }
            if (index.Count == 0) return false;
            Push(new DiffEntry(CurrentFrame, index.ToArray(), old.ToArray(), now.ToArray()));
            IsDirty = true;
            Raise();
            return true;
        }

        private void Stamp(int x, int y, uint color, int size)
        {
            size = Math.Clamp(size, 1, 16);
            int x0 = x - (size - 1) / 2, y0 = y - (size - 1) / 2;
            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                    SetPixel(x0 + dx, y0 + dy, color);
        }

        private void SetPixel(int x, int y, uint color)
        {
            if (_stroke == null || x < 0 || y < 0 || x >= Document.Width || y >= Document.Height) return;
            int i = y * Document.Width + x;
            var pixels = Pixels;
            if (!_stroke.ContainsKey(i)) _stroke[i] = pixels[i];
            pixels[i] = color;
        }

        private IEnumerable<(int X, int Y)> Mirrors(int x, int y, PixelArtSymmetry symmetry)
        {
            yield return (x, y);
            int mx = Document.Width - 1 - x, my = Document.Height - 1 - y;
            if (symmetry is PixelArtSymmetry.Horizontal or PixelArtSymmetry.Both) yield return (mx, y);
            if (symmetry is PixelArtSymmetry.Vertical or PixelArtSymmetry.Both) yield return (x, my);
            if (symmetry == PixelArtSymmetry.Both) yield return (mx, my);
        }

        /// <summary>Flood fill of the connected pixels of the same colour. One undo step. Returns the number of pixels changed.</summary>
        public int Fill(int x, int y, uint color)
        {
            int w = Document.Width, h = Document.Height;
            if (x < 0 || y < 0 || x >= w || y >= h) return 0;
            color = Normalize(color);
            var pixels = Pixels;
            uint target = pixels[y * w + x];
            if (target == color) return 0;

            BeginStroke();
            var stack = new Stack<(int X, int Y)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (sx, sy) = stack.Pop();
                if (pixels[sy * w + sx] != target) continue;
                int left = sx, right = sx;
                while (left > 0 && pixels[sy * w + left - 1] == target) left--;
                while (right < w - 1 && pixels[sy * w + right + 1] == target) right++;
                for (int i = left; i <= right; i++) SetPixel(i, sy, color);
                for (int i = left; i <= right; i++)
                {
                    if (sy > 0 && pixels[(sy - 1) * w + i] == target) stack.Push((i, sy - 1));
                    if (sy < h - 1 && pixels[(sy + 1) * w + i] == target) stack.Push((i, sy + 1));
                }
            }
            int changed = _stroke?.Count ?? 0;
            EndStroke();
            return changed;
        }

        /// <summary>Draws a line, rectangle or ellipse between two corners as one undo step.</summary>
        public void ApplyShape(PixelArtTool tool, int x0, int y0, int x1, int y1, uint color, bool filled, int size, PixelArtSymmetry symmetry)
        {
            BeginStroke();
            foreach (var (x, y) in ShapePoints(tool, x0, y0, x1, y1, filled))
                StrokePoint(x, y, color, size, symmetry);
            EndStroke();
        }

        // =================================================================
        //  Whole-picture changes (each is one snapshot undo step)
        // =================================================================

        /// <summary>Clears the current frame.</summary>
        public void ClearFrame()
        {
            BeginStroke();
            var pixels = Pixels;
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i] != 0) { _stroke![i] = pixels[i]; pixels[i] = 0; }
            EndStroke();
        }

        public void FlipHorizontal() => Transform(doc => Remap(doc, doc.Width, doc.Height, (x, y) => (doc.Width - 1 - x, y)));

        public void FlipVertical() => Transform(doc => Remap(doc, doc.Width, doc.Height, (x, y) => (x, doc.Height - 1 - y)));

        /// <summary>Turns every frame a quarter turn; width and height swap.</summary>
        public void Rotate(bool clockwise) =>
            Transform(doc => clockwise
                ? Remap(doc, doc.Height, doc.Width, (x, y) => (doc.Height - 1 - y, x))
                : Remap(doc, doc.Height, doc.Width, (x, y) => (y, doc.Width - 1 - x)));

        /// <summary>Gives the canvas a new size, keeping what fits. Cropped pixels can be brought back with Undo.</summary>
        public void Resize(int width, int height, PixelArtAnchor anchor)
        {
            width = Math.Clamp(width, 1, PixelArtDocument.MaxSide);
            height = Math.Clamp(height, 1, PixelArtDocument.MaxSide);
            if (width == Document.Width && height == Document.Height) return;
            Transform(doc =>
            {
                int ox = anchor == PixelArtAnchor.Center ? (width - doc.Width) / 2 : 0;
                int oy = anchor == PixelArtAnchor.Center ? (height - doc.Height) / 2 : 0;
                var frames = new List<uint[]>();
                foreach (var src in doc.Frames)
                {
                    var dst = new uint[width * height];
                    for (int y = 0; y < doc.Height; y++)
                    {
                        int ty = y + oy;
                        if (ty < 0 || ty >= height) continue;
                        for (int x = 0; x < doc.Width; x++)
                        {
                            int tx = x + ox;
                            if (tx < 0 || tx >= width) continue;
                            dst[ty * width + tx] = src[y * doc.Width + x];
                        }
                    }
                    frames.Add(dst);
                }
                return new PixelArtDocument(width, height, frames);
            });
        }

        private static PixelArtDocument Remap(PixelArtDocument doc, int newW, int newH, Func<int, int, (int X, int Y)> map)
        {
            var frames = new List<uint[]>();
            foreach (var src in doc.Frames)
            {
                var dst = new uint[newW * newH];
                for (int y = 0; y < doc.Height; y++)
                    for (int x = 0; x < doc.Width; x++)
                    {
                        var (nx, ny) = map(x, y);
                        dst[ny * newW + nx] = src[y * doc.Width + x];
                    }
                frames.Add(dst);
            }
            return new PixelArtDocument(newW, newH, frames);
        }

        private void Transform(Func<PixelArtDocument, PixelArtDocument> change)
        {
            EndStroke();
            var before = Document;
            int frameBefore = CurrentFrame;
            var after = change(before);
            Document = after;
            Push(new SnapshotEntry(before, frameBefore, after, Math.Min(CurrentFrame, after.Frames.Count - 1)));
            IsDirty = true;
            Raise();
        }

        // =================================================================
        //  Frames
        // =================================================================

        /// <summary>Adds a frame after the current one (a copy of it, or blank) and selects it. False at the frame limit.</summary>
        public bool AddFrame(bool duplicate)
        {
            if (Document.Frames.Count >= PixelArtDocument.MaxFrames) return false;
            FrameChange(doc =>
            {
                var frames = doc.Frames.Select(f => f).ToList();
                frames.Insert(CurrentFrame + 1, duplicate ? (uint[])Pixels.Clone() : new uint[doc.Width * doc.Height]);
                return (new PixelArtDocument(doc.Width, doc.Height, frames), CurrentFrame + 1);
            });
            return true;
        }

        /// <summary>Removes the current frame. The last frame can't be removed.</summary>
        public bool DeleteFrame()
        {
            if (Document.Frames.Count <= 1) return false;
            FrameChange(doc =>
            {
                var frames = doc.Frames.ToList();
                frames.RemoveAt(CurrentFrame);
                return (new PixelArtDocument(doc.Width, doc.Height, frames), Math.Min(CurrentFrame, frames.Count - 1));
            });
            return true;
        }

        /// <summary>Moves the current frame one place earlier (-1) or later (+1).</summary>
        public bool MoveFrame(int direction)
        {
            int to = CurrentFrame + Math.Sign(direction);
            if (to < 0 || to >= Document.Frames.Count || direction == 0) return false;
            FrameChange(doc =>
            {
                var frames = doc.Frames.ToList();
                (frames[CurrentFrame], frames[to]) = (frames[to], frames[CurrentFrame]);
                return (new PixelArtDocument(doc.Width, doc.Height, frames), to);
            });
            return true;
        }

        private void FrameChange(Func<PixelArtDocument, (PixelArtDocument Doc, int Frame)> change)
        {
            EndStroke();
            var before = Document;
            int frameBefore = CurrentFrame;
            var (after, frame) = change(before);
            Document = after;
            CurrentFrame = frame;
            Push(new SnapshotEntry(before, frameBefore, after, frame));
            IsDirty = true;
            Raise();
        }

        /// <summary>Selecting a frame is not an edit, so it is not in the history.</summary>
        public void SelectFrame(int index)
        {
            EndStroke();
            index = Math.Clamp(index, 0, Document.Frames.Count - 1);
            if (index == CurrentFrame) return;
            CurrentFrame = index;
            Raise();
        }

        // =================================================================
        //  Undo / redo
        // =================================================================

        private void Push(Entry entry)
        {
            _undo.Add(entry);
            if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public bool Undo()
        {
            EndStroke();
            if (_undo.Count == 0) return false;
            var entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
            Apply(entry, undo: true);
            return true;
        }

        public bool Redo()
        {
            EndStroke();
            if (_redo.Count == 0) return false;
            var entry = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
            Apply(entry, undo: false);
            return true;
        }

        private void Apply(Entry entry, bool undo)
        {
            switch (entry)
            {
                case DiffEntry d:
                    var pixels = Document.Frames[Math.Min(d.Frame, Document.Frames.Count - 1)];
                    var values = undo ? d.Old : d.New;
                    for (int i = 0; i < d.Index.Length; i++) pixels[d.Index[i]] = values[i];
                    CurrentFrame = Math.Min(d.Frame, Document.Frames.Count - 1);
                    break;
                case SnapshotEntry s:
                    Document = undo ? s.Before : s.After;
                    CurrentFrame = Math.Min(undo ? s.FrameBefore : s.FrameAfter, Document.Frames.Count - 1);
                    break;
            }
            IsDirty = true;
            Raise();
        }

        // =================================================================
        //  Shapes (pure)
        // =================================================================

        /// <summary>The pixels of a line, rectangle or ellipse whose corners are the two points. Also used for the live preview.</summary>
        public static List<(int X, int Y)> ShapePoints(PixelArtTool tool, int x0, int y0, int x1, int y1, bool filled) => tool switch
        {
            PixelArtTool.Line => LinePoints(x0, y0, x1, y1),
            PixelArtTool.Rectangle => RectanglePoints(x0, y0, x1, y1, filled),
            PixelArtTool.Ellipse => EllipsePoints(x0, y0, x1, y1, filled),
            _ => new List<(int, int)> { (x1, y1) },
        };

        /// <summary>Bresenham's line, both ends included.</summary>
        public static List<(int X, int Y)> LinePoints(int x0, int y0, int x1, int y1)
        {
            var points = new List<(int, int)>();
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                points.Add((x0, y0));
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
            return points;
        }

        public static List<(int X, int Y)> RectanglePoints(int x0, int y0, int x1, int y1, bool filled)
        {
            int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1), minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            var points = new List<(int, int)>();
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    if (filled || x == minX || x == maxX || y == minY || y == maxY) points.Add((x, y));
            return points;
        }

        /// <summary>
        /// An ellipse that fills the box between the two corners. A pixel is inside when its centre is inside
        /// the ellipse; the outline is the inside pixels that touch an outside pixel, so it is always closed and symmetric.
        /// </summary>
        public static List<(int X, int Y)> EllipsePoints(int x0, int y0, int x1, int y1, bool filled)
        {
            int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1), minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            int w = maxX - minX + 1, h = maxY - minY + 1;
            double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, rx = w / 2.0, ry = h / 2.0;

            var inside = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double nx = (x - cx) / rx, ny = (y - cy) / ry;
                    inside[y * w + x] = nx * nx + ny * ny <= 1.0;
                }

            // A very thin ellipse can miss the ends of its box (16 x 2). Every column and row must have a pixel,
            // so the missing ones get the pixel(s) nearest the middle - both middle ones when the size is even, to stay symmetric.
            int[] MidRows() => h % 2 == 1 ? new[] { h / 2 } : new[] { h / 2 - 1, h / 2 };
            int[] MidCols() => w % 2 == 1 ? new[] { w / 2 } : new[] { w / 2 - 1, w / 2 };
            for (int x = 0; x < w; x++)
                if (!Enumerable.Range(0, h).Any(y => inside[y * w + x]))
                    foreach (int y in MidRows()) inside[y * w + x] = true;
            for (int y = 0; y < h; y++)
                if (!Enumerable.Range(0, w).Any(x => inside[y * w + x]))
                    foreach (int x in MidCols()) inside[y * w + x] = true;

            bool At(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && inside[y * w + x];
            var points = new List<(int, int)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!inside[y * w + x]) continue;
                    if (filled || !At(x - 1, y) || !At(x + 1, y) || !At(x, y - 1) || !At(x, y + 1))
                        points.Add((minX + x, minY + y));
                }
            return points;
        }
    }
}
