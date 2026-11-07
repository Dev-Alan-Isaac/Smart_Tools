using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The "drawn" exports of a folder tree: a PDF (PDFsharp, any size, split over pages) and a PNG picture (WPF drawing,
    /// limited to <see cref="MaxImageRows"/> items because one picture cannot be arbitrarily tall). Both draw the same
    /// rows: connector lines, a folder or file icon, the name, and the size/date next to it. No WPF types, so it can
    /// run on any thread.
    /// </summary>
    internal static class FolderTreeVisual
    {
        public const int MaxImageRows = 600;

        /// <summary>Phase timings of the last picture (for the log and tests).</summary>
        internal static string LastPictureTimings { get; private set; } = "";
        public const int MaxPdfRows = 60_000;

        private readonly record struct Rgb(string Hex);

        private static readonly Rgb TextColor = new("#1F2937");
        private static readonly Rgb MetaColor = new("#6B7280");
        private static readonly Rgb LineColor = new("#CBD5E1");
        private static readonly Rgb FolderColor = new("#F59E0B");
        private static readonly Rgb FolderTab = new("#D97706");
        private static readonly Rgb FileColor = new("#64748B");
        private static readonly Rgb Paper = new("#FFFFFF");

        /// <summary>One line of the picture: a tree item, or a plain label (the summary).</summary>
        private sealed record Row(FolderTreeNode? Node, int Depth, bool[] LastAt, string? Label = null);

        private interface ISurface
        {
            double Measure(string text, double size, bool bold);
            void Text(string text, double x, double top, double size, bool bold, Rgb color);
            void Line(double x1, double y1, double x2, double y2, double width, Rgb color);
            void Fill(double x, double y, double w, double h, Rgb color);
            void Outline(double x, double y, double w, double h, double width, Rgb color);
        }

        // =================================================================
        //  Rows
        // =================================================================

        private static int CountNodes(FolderTreeNode root)
        {
            int count = 0;
            var stack = new Stack<FolderTreeNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                count++;
                foreach (var c in n.Children) stack.Push(c);
            }
            return count;
        }

        private static List<Row> Flatten(FolderTreeResult r, int limit)
        {
            var rows = new List<Row> { new(r.Root, 0, Array.Empty<bool>()) };
            void Walk(FolderTreeNode parent, bool[] parentLast, int depth)
            {
                var kids = FolderTreeManager.Sorted(parent).ToList();
                for (int i = 0; i < kids.Count; i++)
                {
                    if (rows.Count >= limit) return;
                    var last = new bool[depth];
                    Array.Copy(parentLast, last, depth - 1);
                    last[depth - 1] = i == kids.Count - 1;
                    rows.Add(new Row(kids[i], depth, last));
                    if (kids[i].IsFolder) Walk(kids[i], last, depth + 1);
                }
            }
            Walk(r.Root, Array.Empty<bool>(), 1);
            return rows;
        }

        private static string MetaPlain(FolderTreeNode n, FolderTreeOptions o)
        {
            string meta = FolderTreeManager.Meta(n, o).Trim();
            return meta.Length >= 2 && meta[0] == '[' && meta[^1] == ']' ? meta[1..^1] : meta;
        }

        // =================================================================
        //  Drawing one row (shared by both outputs)
        // =================================================================

        private static string Fit(ISurface s, string text, double size, bool bold, double maxWidth)
        {
            if (maxWidth <= 0) return "";
            if (s.Measure(text, size, bold) <= maxWidth) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (s.Measure(text[..mid] + "…", size, bold) <= maxWidth) lo = mid; else hi = mid - 1;
            }
            return lo == 0 ? "" : text[..lo] + "…";
        }

        private static void DrawRow(ISurface s, Row row, FolderTreeOptions o, double x0, double y, double rowH, double indent, double size, double maxX)
        {
            double textTop = y + (rowH - size) / 2 - 1;
            if (row.Label != null)
            {
                s.Text(Fit(s, row.Label, size, false, maxX - x0), x0, textTop, size, false, MetaColor);
                return;
            }

            var node = row.Node!;
            if (row.Depth == 0)
            {
                string head = node.FullPath + (MetaPlain(node, o) is { Length: > 0 } m ? "   " + m : "");
                s.Text(Fit(s, head, size + 1, true, maxX - x0), x0, textTop, size + 1, true, TextColor);
                return;
            }

            double cy = y + rowH / 2;
            for (int j = 1; j < row.Depth; j++)
            {
                if (row.LastAt[j - 1]) continue;
                double xj = x0 + (j - 1) * indent + indent / 2;
                s.Line(xj, y, xj, y + rowH, 1, LineColor);
            }
            int k = row.Depth;
            double xk = x0 + (k - 1) * indent + indent / 2;
            s.Line(xk, y, xk, row.LastAt[k - 1] ? cy : y + rowH, 1, LineColor);
            s.Line(xk, cy, xk + indent * 0.45, cy, 1, LineColor);

            double iconH = rowH * 0.55, iconW = iconH * 1.2;
            double ix = xk + indent * 0.45 + 3, iy = cy - iconH / 2;
            if (node.IsFolder)
            {
                s.Fill(ix, iy - iconH * 0.18, iconW * 0.45, iconH * 0.25, FolderTab);
                s.Fill(ix, iy, iconW, iconH, FolderColor);
            }
            else
            {
                double fw = iconH * 0.85;
                s.Outline(ix + (iconW - fw) / 2, iy - iconH * 0.12, fw, iconH * 1.24, 1, FileColor);
                s.Fill(ix + (iconW - fw) / 2 + fw * 0.2, cy - 0.5, fw * 0.6, 1, FileColor);
            }

            double tx = ix + iconW + 5;
            string name = Fit(s, node.Name + (node.IsFolder ? "/" : ""), size, node.IsFolder, maxX - tx);
            s.Text(name, tx, textTop, size, node.IsFolder, TextColor);

            string meta = MetaPlain(node, o);
            if (meta.Length > 0)
            {
                double mx = tx + s.Measure(name, size, node.IsFolder) + 8;
                string fitted = Fit(s, meta, size - 1, false, maxX - mx);
                if (fitted.Length > 0) s.Text(fitted, mx, textTop + 0.5, size - 1, false, MetaColor);
            }
        }

        // =================================================================
        //  PDF (PDFsharp)
        // =================================================================

        private sealed class PdfSurface : ISurface
        {
            private readonly XGraphics _g;
            private readonly Dictionary<(double, bool), XFont> _fonts = new();

            public PdfSurface(XGraphics g) => _g = g;

            private XFont Font(double size, bool bold)
            {
                if (!_fonts.TryGetValue((size, bold), out var f))
                    _fonts[(size, bold)] = f = new XFont("Verdana", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                return f;
            }

            private static XColor C(Rgb c) => XColor.FromArgb(Convert.ToInt32(c.Hex[1..3], 16), Convert.ToInt32(c.Hex[3..5], 16), Convert.ToInt32(c.Hex[5..7], 16));

            public double Measure(string text, double size, bool bold) => _g.MeasureString(text, Font(size, bold)).Width;
            public void Text(string text, double x, double top, double size, bool bold, Rgb color) =>
                _ = text.Length == 0 ? 0 : Draw(text, x, top, size, bold, color);

            private int Draw(string text, double x, double top, double size, bool bold, Rgb color)
            {
                _g.DrawString(text, Font(size, bold), new XSolidBrush(C(color)), new XPoint(x, top), XStringFormats.TopLeft);
                return 0;
            }

            public void Line(double x1, double y1, double x2, double y2, double width, Rgb color) =>
                _g.DrawLine(new XPen(C(color), width * 0.6), x1, y1, x2, y2);
            public void Fill(double x, double y, double w, double h, Rgb color) => _g.DrawRectangle(new XSolidBrush(C(color)), x, y, w, h);
            public void Outline(double x, double y, double w, double h, double width, Rgb color) => _g.DrawRectangle(new XPen(C(color), width * 0.6), x, y, w, h);
        }

        /// <summary>The whole tree as a PDF (A4, as many pages as needed). Throws <see cref="IOException"/> if no font is available.</summary>
        public static byte[] RenderPdf(FolderTreeResult r, FolderTreeOptions o, out int drawn, out int total)
        {
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new WindowsFontResolver();

            total = CountNodes(r.Root);
            var rows = Flatten(r, MaxPdfRows);
            drawn = rows.Count;
            if (total > rows.Count) rows.Add(new Row(null, 0, Array.Empty<bool>(), Loc.F("Str_FolderTree_VisualCut", "… {0:N0} more items are not drawn.", total - rows.Count)));
            rows.Add(new Row(null, 0, Array.Empty<bool>(), ""));
            rows.Add(new Row(null, 0, Array.Empty<bool>(), FolderTreeManager.Footer(r)));

            const double margin = 36, rowH = 15, size = 8.5, indent = 14, footer = 22;
            using var doc = new PdfDocument();
            doc.Info.Title = r.Root.Name;
            doc.Info.Creator = "Smart Tools";
            var first = doc.AddPage();
            first.Size = PdfSharp.PageSize.A4;
            double pageW = first.Width.Point, pageH = first.Height.Point;
            int perPage = Math.Max(1, (int)((pageH - 2 * margin - footer) / rowH));
            int pages = (rows.Count + perPage - 1) / perPage;

            for (int p = 0; p < pages; p++)
            {
                var page = p == 0 ? first : doc.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                using var gfx = XGraphics.FromPdfPage(page);
                var surface = new PdfSurface(gfx);
                int from = p * perPage, to = Math.Min(rows.Count, from + perPage);
                for (int i = from; i < to; i++)
                    DrawRow(surface, rows[i], o, margin, margin + (i - from) * rowH, rowH, indent, size, pageW - margin);
                string pageText = $"{p + 1} / {pages}";
                surface.Text(pageText, pageW - margin - surface.Measure(pageText, 7.5, false), pageH - margin + 6, 7.5, false, MetaColor);
                surface.Text(Fit(surface, r.Root.Name, 7.5, false, pageW - 2 * margin - 60), margin, pageH - margin + 6, 7.5, false, MetaColor);
            }

            using var ms = new MemoryStream();
            doc.Save(ms, false);
            return ms.ToArray();
        }

        // =================================================================
        //  Picture (WPF drawing on an STA thread, PNG encoder)
        // =================================================================

        /// <summary>
        /// Draws onto a WPF <see cref="DrawingContext"/>, or only measures when the context is null (the first pass,
        /// which finds how wide the picture has to be). Magick can draw the same rows but needs ~5 ms per text item
        /// (about 8 s for 600 items); WPF does them in a fraction of a second.
        /// </summary>
        private sealed class WpfSurface : ISurface
        {
            private readonly DrawingContext? _dc;
            private readonly Dictionary<(string, double, bool), double> _widths = new();
            private readonly Dictionary<string, SolidColorBrush> _brushes = new();
            private static readonly Typeface Regular = new(new System.Windows.Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            private static readonly Typeface Bold = new(new System.Windows.Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);
            public double MaxRight;

            public WpfSurface(DrawingContext? dc) => _dc = dc;

            private SolidColorBrush Brush(Rgb c)
            {
                if (!_brushes.TryGetValue(c.Hex, out var b))
                {
                    b = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(c.Hex));
                    b.Freeze();
                    _brushes[c.Hex] = b;
                }
                return b;
            }

            private static FormattedText Format(string text, double size, bool bold, System.Windows.Media.Brush brush) =>
                new(text, System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, bold ? Bold : Regular, size, brush, 1.0);

            public double Measure(string text, double size, bool bold)
            {
                if (text.Length == 0) return 0;
                if (_widths.TryGetValue((text, size, bold), out double w)) return w;
                w = Format(text, size, bold, System.Windows.Media.Brushes.Black).WidthIncludingTrailingWhitespace;
                if (_widths.Count < 20_000) _widths[(text, size, bold)] = w;
                return w;
            }

            public void Text(string text, double x, double top, double size, bool bold, Rgb color)
            {
                if (text.Length == 0) return;
                MaxRight = Math.Max(MaxRight, x + Measure(text, size, bold));
                _dc?.DrawText(Format(text, size, bold, Brush(color)), new System.Windows.Point(x, top));
            }

            public void Line(double x1, double y1, double x2, double y2, double width, Rgb color)
            {
                if (_dc == null) return;
                var pen = new System.Windows.Media.Pen(Brush(color), width);
                pen.Freeze();
                _dc.DrawLine(pen, new System.Windows.Point(x1, y1), new System.Windows.Point(x2, y2));
            }

            public void Fill(double x, double y, double w, double h, Rgb color) =>
                _dc?.DrawRectangle(Brush(color), null, new System.Windows.Rect(x, y, w, h));

            public void Outline(double x, double y, double w, double h, double width, Rgb color)
            {
                if (_dc == null) return;
                var pen = new System.Windows.Media.Pen(Brush(color), width);
                pen.Freeze();
                _dc.DrawRectangle(null, pen, new System.Windows.Rect(x, y, w, h));
            }
        }

        /// <summary>The first <see cref="MaxImageRows"/> items as a PNG. <paramref name="drawn"/> is how many items fit.</summary>
        public static byte[] RenderPng(FolderTreeResult r, FolderTreeOptions o, CancellationToken token, out int drawn, out int total)
        {
            total = CountNodes(r.Root);
            var rows = Flatten(r, MaxImageRows);
            drawn = rows.Count;
            if (total > rows.Count) rows.Add(new Row(null, 0, Array.Empty<bool>(), Loc.F("Str_FolderTree_VisualCutPdf", "… {0:N0} more items are not drawn - use PDF for the whole tree.", total - rows.Count)));
            rows.Add(new Row(null, 0, Array.Empty<bool>(), ""));
            rows.Add(new Row(null, 0, Array.Empty<bool>(), FolderTreeManager.Footer(r)));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            const double margin = 24, rowH = 22, size = 12, indent = 22, maxWidth = 1800;
            var bytes = ZxingImageHelpers.RunOnStaThread(() =>
            {
                var measure = new WpfSurface(null);
                for (int i = 0; i < rows.Count; i++)
                    DrawRow(measure, rows[i], o, margin, margin + i * rowH, rowH, indent, size, maxWidth - margin);
                int width = (int)Math.Clamp(Math.Ceiling(measure.MaxRight + margin), 480, maxWidth);
                int height = (int)Math.Ceiling(margin * 2 + rows.Count * rowH);

                token.ThrowIfCancellationRequested();
                using var lease = MemoryBudget.Acquire(ImageLoad.EstimateBytes((ulong)width, (ulong)height), token);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Paper.Hex)), null, new System.Windows.Rect(0, 0, width, height));
                    var surface = new WpfSurface(dc);
                    for (int i = 0; i < rows.Count; i++)
                        DrawRow(surface, rows[i], o, margin, margin + i * rowH, rowH, indent, size, width - margin);
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                LastPictureTimings = $"{rows.Count} rows, {width}x{height}px, {ms.Length >> 10} KB";
                return ms.ToArray();
            }).GetAwaiter().GetResult();
            LastPictureTimings += $", {clock.ElapsedMilliseconds} ms";
            Log.Info("Folder tree picture: " + LastPictureTimings);
            return bytes;
        }
    }
}
