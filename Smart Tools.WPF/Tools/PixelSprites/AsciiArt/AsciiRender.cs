using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Frozen brushes by colour, so a picture with thousands of coloured cells does not make thousands of brushes.</summary>
    public sealed class AsciiBrushCache
    {
        private readonly Dictionary<uint, Brush> _brushes = new();
        public Brush Get(uint argb)
        {
            if (_brushes.TryGetValue(argb, out var b)) return b;
            b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            if (_brushes.Count > 20000) _brushes.Clear();   // a runaway palette cannot grow without limit
            _brushes[argb] = b;
            return b;
        }
    }

    /// <summary>Draws an <see cref="AsciiGrid"/> with WPF glyph runs: the preview control and the PNG export share this, so they look the same.</summary>
    public static class AsciiRender
    {
        /// <summary>The picture's background: near-black or near-white. It is data (what the art is drawn on), not a theme colour.</summary>
        public static Color BackgroundColor(bool dark) => dark ? Color.FromRgb(14, 15, 18) : Color.FromRgb(250, 250, 250);

        /// <summary>Text colour for cells without their own colour (black and white mode).</summary>
        public static Color TextColor(bool dark) => dark ? Color.FromRgb(233, 235, 239) : Color.FromRgb(24, 24, 27);

        public static void DrawRow(DrawingContext dc, AsciiGrid grid, AsciiGlyphs glyphs, int row, double em, double cellH, Brush defaultBrush, AsciiBrushCache brushes)
        {
            double cellW = glyphs.CellWidthEm * em;
            int x = 0;
            while (x < grid.Cols)
            {
                uint color = grid.ColorAt(x, row);
                int end = x + 1;
                while (end < grid.Cols && grid.ColorAt(end, row) == color) end++;
                bool blank = true;
                for (int i = x; i < end; i++) if (grid[i, row] != ' ') { blank = false; break; }
                if (!blank)
                {
                    var text = new ReadOnlySpan<char>(grid.Chars, row * grid.Cols + x, end - x);
                    dc.DrawGlyphRun(color == 0 ? defaultBrush : brushes.Get(color), glyphs.Run(text, em, x * cellW, row * cellH));
                }
                x = end;
            }
        }

        /// <summary>One row as a frozen drawing, so the preview only redraws the rows that changed.</summary>
        public static DrawingGroup RowDrawing(AsciiGrid grid, AsciiGlyphs glyphs, int row, double em, Brush defaultBrush, AsciiBrushCache brushes)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
                DrawRow(dc, grid, glyphs, row, em, glyphs.CellHeightEm * em, defaultBrush, brushes);
            group.Freeze();
            return group;
        }

        /// <summary>Largest picture the PNG export makes, in pixels (a 40 MP picture is about 160 MB of memory while it is drawn).</summary>
        public const long MaxPngPixels = 40_000_000;
        public const int MaxPngSide = 16000;

        /// <summary>
        /// The picture as PNG bytes. <c>cellHeightPx</c> is the height of one character row in pixels;
        /// it is reduced when the whole picture would be too large. Run on an STA thread.
        /// </summary>
        public static (byte[] Png, int Width, int Height, bool Reduced) RenderPng(AsciiGrid grid, AsciiGlyphs glyphs, double cellHeightPx, bool dark)
        {
            cellHeightPx = Math.Max(6, cellHeightPx);
            bool reduced = false;
            double Width(double ch) => grid.Cols * glyphs.CellWidthEm * (ch / glyphs.CellHeightEm);
            double Height(double ch) => grid.Rows * ch;
            while ((Width(cellHeightPx) > MaxPngSide || Height(cellHeightPx) > MaxPngSide
                    || Width(cellHeightPx) * Height(cellHeightPx) > MaxPngPixels) && cellHeightPx > 6)
            {
                cellHeightPx = Math.Max(6, cellHeightPx * 0.9);
                reduced = true;
            }

            double em = cellHeightPx / glyphs.CellHeightEm;
            int w = (int)Math.Ceiling(Width(cellHeightPx)), h = (int)Math.Ceiling(Height(cellHeightPx));
            var brushes = new AsciiBrushCache();
            var defaultBrush = brushes.Get(PackColor(TextColor(dark)));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(brushes.Get(PackColor(BackgroundColor(dark))), null, new Rect(0, 0, w, h));
                for (int y = 0; y < grid.Rows; y++)
                    DrawRow(dc, grid, glyphs, y, em, cellHeightPx, defaultBrush, brushes);
            }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (ms.ToArray(), w, h, reduced);
        }

        public static uint PackColor(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
    }
}
