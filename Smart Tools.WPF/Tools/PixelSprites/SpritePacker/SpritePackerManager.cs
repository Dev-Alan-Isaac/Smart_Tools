using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Image Sprite Sheet Packer logic. No WPF here, so it compiles in a plain net8.0 project.
    /// Pack: MaxRects (best short side) places the sprites, trying the smallest sheet first and
    /// starting another sheet when more than the biggest allowed sheet is needed. Slice: cuts a
    /// sheet into a grid of cells. Coordinate files (JSON, CSS, XML) say where everything is.
    /// Pixels are straight-alpha BGRA (what WPF decodes to); the PNG is written by <see cref="PngWriter"/>.
    /// </summary>
    public static partial class SpritePackerManager
    {
        public const int MaxSprites = 2000;
        public const int MaxSliceCells = 10_000;
        public const int MaxSide = 8192;

        // =================================================================
        //  Packing
        // =================================================================

        private readonly record struct Rect(int X, int Y, int W, int H);

        /// <summary>A free-space tracker: each insert takes the free rectangle it fits best (smallest short-side leftover).</summary>
        private sealed class MaxRectsBin
        {
            private readonly List<Rect> _free = new();

            public MaxRectsBin(int width, int height) => _free.Add(new Rect(0, 0, width, height));

            public Rect? Insert(int w, int h)
            {
                Rect? best = null;
                int bestShort = int.MaxValue, bestLong = int.MaxValue;
                foreach (var f in _free)
                {
                    if (f.W < w || f.H < h) continue;
                    int leftoverX = f.W - w, leftoverY = f.H - h;
                    int shortSide = Math.Min(leftoverX, leftoverY), longSide = Math.Max(leftoverX, leftoverY);
                    if (shortSide < bestShort || shortSide == bestShort && longSide < bestLong)
                    {
                        best = new Rect(f.X, f.Y, w, h);
                        bestShort = shortSide;
                        bestLong = longSide;
                    }
                }
                if (best is not { } placed) return null;

                for (int i = 0; i < _free.Count; i++)
                    if (Split(_free[i], placed))
                    {
                        _free.RemoveAt(i);
                        i--;
                    }
                Prune();
                return placed;
            }

            /// <summary>Cuts the part of a free rectangle that the new sprite covers, leaving up to four free strips.</summary>
            private bool Split(Rect f, Rect p)
            {
                if (p.X >= f.X + f.W || p.X + p.W <= f.X || p.Y >= f.Y + f.H || p.Y + p.H <= f.Y) return false;
                if (p.X > f.X) _free.Add(new Rect(f.X, f.Y, p.X - f.X, f.H));
                if (p.X + p.W < f.X + f.W) _free.Add(new Rect(p.X + p.W, f.Y, f.X + f.W - (p.X + p.W), f.H));
                if (p.Y > f.Y) _free.Add(new Rect(f.X, f.Y, f.W, p.Y - f.Y));
                if (p.Y + p.H < f.Y + f.H) _free.Add(new Rect(f.X, p.Y + p.H, f.W, f.Y + f.H - (p.Y + p.H)));
                return true;
            }

            private void Prune()
            {
                for (int i = 0; i < _free.Count; i++)
                    for (int j = i + 1; j < _free.Count; j++)
                    {
                        if (Contains(_free[j], _free[i])) { _free.RemoveAt(i); i--; break; }
                        if (Contains(_free[i], _free[j])) { _free.RemoveAt(j); j--; }
                    }
            }

            private static bool Contains(Rect outer, Rect inner) =>
                inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H;
        }

        private readonly record struct Item(SpritePackInput Input, int W, int H);

        /// <summary>
        /// Places the sprites. Big ones first; each sheet is as small as it can be (a power of two when asked),
        /// and a sprite bigger than the biggest allowed sheet is reported in <see cref="SpritePackResult.TooBig"/>.
        /// The same inputs always give the same sheets.
        /// </summary>
        public static SpritePackResult Pack(IReadOnlyList<SpritePackInput> inputs, SpritePackOptions options)
        {
            int pad = Math.Clamp(options.Padding, 0, 32);
            int max = Math.Clamp(options.MaxSide, 16, MaxSide);
            var tooBig = new List<int>();
            var pending = new List<Item>();
            foreach (var input in inputs)
            {
                int w = Math.Max(1, input.TrimWidth), h = Math.Max(1, input.TrimHeight);
                if (w > max || h > max) tooBig.Add(input.Index);
                else pending.Add(new Item(input, w, h));
            }
            pending = pending.OrderByDescending(i => Math.Max(i.W, i.H)).ThenByDescending(i => (long)i.W * i.H).ThenBy(i => i.Input.Index).ToList();

            var pages = new List<SpritePage>();
            while (pending.Count > 0)
            {
                // Which sprites share the next sheet: as many as fit in the biggest sheet.
                var bin = new MaxRectsBin(max + pad, max + pad);
                var onPage = new List<Item>();
                var left = new List<Item>();
                foreach (var item in pending)
                    if (bin.Insert(item.W + pad, item.H + pad) != null) onPage.Add(item);
                    else left.Add(item);

                pages.Add(SmallestPage(onPage, pages.Count, pad, options.PowerOfTwo, max));
                pending = left;
            }
            return new SpritePackResult { Pages = pages, TooBig = tooBig };
        }

        private static SpritePage SmallestPage(List<Item> items, int pageIndex, int pad, bool powerOfTwo, int max)
        {
            var sides = new List<int>();
            for (int s = 16; s < max; s *= 2) sides.Add(s);
            sides.Add(max);

            int maxW = items.Max(i => i.W), maxH = items.Max(i => i.H);
            long area = items.Sum(i => (long)i.W * i.H);
            var candidates = sides.SelectMany(w => sides.Select(h => (W: w, H: h)))
                .Where(c => c.W >= maxW && c.H >= maxH && (long)c.W * c.H >= area)
                .OrderBy(c => (long)c.W * c.H).ThenBy(c => Math.Abs(c.W - c.H)).ThenBy(c => c.W);

            foreach (var (bw, bh) in candidates)
            {
                var bin = new MaxRectsBin(bw + pad, bh + pad);
                var placed = new List<SpritePlacement>();
                bool all = true;
                foreach (var item in items)
                {
                    if (bin.Insert(item.W + pad, item.H + pad) is not { } r) { all = false; break; }
                    var i = item.Input;
                    placed.Add(new SpritePlacement(i.Index, i.Name, pageIndex, r.X, r.Y, item.W, item.H, i.TrimX, i.TrimY, i.SourceWidth, i.SourceHeight));
                }
                if (!all) continue;

                int width = powerOfTwo ? bw : placed.Max(p => p.X + p.Width);
                int height = powerOfTwo ? bh : placed.Max(p => p.Y + p.Height);
                return new SpritePage { Width = width, Height = height, Placements = placed.OrderBy(p => p.Index).ToList() };
            }

            // Cannot happen: the same sprites already fitted the biggest sheet. Still, never lose them.
            var fallback = new MaxRectsBin(max + pad, max + pad);
            var all2 = items.Select(item =>
            {
                var r = fallback.Insert(item.W + pad, item.H + pad) ?? new Rect(0, 0, item.W, item.H);
                var i = item.Input;
                return new SpritePlacement(i.Index, i.Name, pageIndex, r.X, r.Y, item.W, item.H, i.TrimX, i.TrimY, i.SourceWidth, i.SourceHeight);
            }).OrderBy(p => p.Index).ToList();
            return new SpritePage { Width = max, Height = max, Placements = all2 };
        }

        /// <summary>The smallest box around the pixels that are not fully see-through. A blank picture keeps one pixel.</summary>
        public static (int X, int Y, int Width, int Height) ComputeTrim(byte[] bgra, int width, int height)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    if (bgra[row + x * 4 + 3] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    maxY = y;
                }
            }
            return maxX < 0 ? (0, 0, 1, 1) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>Draws one sheet: each placement copies its kept part of the source picture. Straight-alpha BGRA.</summary>
        public static byte[] ComposeSheet(SpritePage page, Func<int, byte[]> getSourceBgra)
        {
            var sheet = new byte[page.Width * page.Height * 4];
            foreach (var p in page.Placements)
            {
                var src = getSourceBgra(p.Index);
                for (int r = 0; r < p.Height; r++)
                    Buffer.BlockCopy(src, ((p.TrimY + r) * p.SourceWidth + p.TrimX) * 4, sheet, ((p.Y + r) * page.Width + p.X) * 4, p.Width * 4);
            }
            return sheet;
        }

        public static byte[] BgraToRgba(byte[] bgra)
        {
            var rgba = new byte[bgra.Length];
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = bgra[i + 3];
            }
            return rgba;
        }

        // =================================================================
        //  Names
        // =================================================================

        /// <summary>Letters, digits, - and _ only (safe as a CSS class and in a file name).</summary>
        public static string SafeName(string? name, string fallback = "sprite")
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                bool ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
                if (ok) sb.Append(c);
                else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
            string result = sb.ToString().Trim('-');
            return result.Length > 0 ? result : fallback;
        }

        public static string SpriteNameFromFile(string fileName) => SafeName(Path.GetFileNameWithoutExtension(fileName));

        /// <summary>Same names get -2, -3 ... (case does not matter).</summary>
        public static List<string> UniqueNames(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string name in names)
            {
                string candidate = name;
                for (int n = 2; !seen.Add(candidate); n++) candidate = name + "-" + n.ToString(CultureInfo.InvariantCulture);
                result.Add(candidate);
            }
            return result;
        }

        // =================================================================
        //  Coordinate files
        // =================================================================

        public static string CoordinateExtension(SpriteCoordFormat format) => format switch
        {
            SpriteCoordFormat.Json => ".json",
            SpriteCoordFormat.Css => ".css",
            SpriteCoordFormat.Xml => ".xml",
            _ => "",
        };

        /// <summary>Text for the chosen coordinates file (null for None). <paramref name="imageFiles"/> holds one file name per sheet.</summary>
        public static string? BuildCoordinates(SpriteCoordFormat format, IReadOnlyList<SpritePage> pages, IReadOnlyList<string> imageFiles, int padding) => format switch
        {
            SpriteCoordFormat.Json => BuildJson(pages, imageFiles, padding),
            SpriteCoordFormat.Css => BuildCss(pages, imageFiles),
            SpriteCoordFormat.Xml => BuildXml(pages, imageFiles),
            _ => null,
        };

        private static IEnumerable<SpritePlacement> InOrder(IReadOnlyList<SpritePage> pages) =>
            pages.SelectMany(p => p.Placements).OrderBy(p => p.Index);

        private static string BuildJson(IReadOnlyList<SpritePage> pages, IReadOnlyList<string> imageFiles, int padding)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteStartObject("meta");
                w.WriteString("app", "Smart Tools - Sprite Sheet Packer");
                w.WriteNumber("padding", padding);
                w.WriteStartArray("images");
                for (int i = 0; i < pages.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("file", imageFiles[i]);
                    w.WriteNumber("width", pages[i].Width);
                    w.WriteNumber("height", pages[i].Height);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteStartArray("frames");
                foreach (var p in InOrder(pages))
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    w.WriteString("image", imageFiles[p.Page]);
                    w.WriteNumber("page", p.Page);
                    w.WriteNumber("x", p.X);
                    w.WriteNumber("y", p.Y);
                    w.WriteNumber("w", p.Width);
                    w.WriteNumber("h", p.Height);
                    w.WriteBoolean("trimmed", p.Trimmed);
                    w.WriteStartObject("spriteSourceSize");
                    w.WriteNumber("x", p.TrimX);
                    w.WriteNumber("y", p.TrimY);
                    w.WriteNumber("w", p.Width);
                    w.WriteNumber("h", p.Height);
                    w.WriteEndObject();
                    w.WriteStartObject("sourceSize");
                    w.WriteNumber("w", p.SourceWidth);
                    w.WriteNumber("h", p.SourceHeight);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string BuildCss(IReadOnlyList<SpritePage> pages, IReadOnlyList<string> imageFiles)
        {
            var sb = new StringBuilder();
            sb.Append("/* Sprite sheet styles made by Smart Tools. Put the picture(s) next to this file. */\n");
            sb.Append(".sprite { display: inline-block; background-repeat: no-repeat; }\n");
            foreach (var p in InOrder(pages))
            {
                string x = p.X == 0 ? "0" : "-" + p.X.ToString(CultureInfo.InvariantCulture) + "px";
                string y = p.Y == 0 ? "0" : "-" + p.Y.ToString(CultureInfo.InvariantCulture) + "px";
                sb.Append(CultureInfo.InvariantCulture, $".sprite-{p.Name} {{ background-image: url(\"{imageFiles[p.Page]}\"); background-position: {x} {y}; width: {p.Width}px; height: {p.Height}px; }}");
                if (p.Trimmed) sb.Append(CultureInfo.InvariantCulture, $" /* trimmed: kept {p.Width}x{p.Height} at {p.TrimX},{p.TrimY} of {p.SourceWidth}x{p.SourceHeight} */");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string BuildXml(IReadOnlyList<SpritePage> pages, IReadOnlyList<string> imageFiles)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
            using (var w = XmlWriter.Create(sb, settings))
            {
                w.WriteStartDocument();
                bool several = pages.Count > 1;
                if (several) w.WriteStartElement("TextureAtlases");
                for (int i = 0; i < pages.Count; i++)
                {
                    w.WriteStartElement("TextureAtlas");
                    w.WriteAttributeString("imagePath", imageFiles[i]);
                    w.WriteAttributeString("width", pages[i].Width.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("height", pages[i].Height.ToString(CultureInfo.InvariantCulture));
                    foreach (var p in pages[i].Placements)
                    {
                        w.WriteStartElement("SubTexture");
                        w.WriteAttributeString("name", p.Name);
                        w.WriteAttributeString("x", p.X.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("y", p.Y.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("width", p.Width.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("height", p.Height.ToString(CultureInfo.InvariantCulture));
                        if (p.Trimmed)
                        {
                            w.WriteAttributeString("frameX", (-p.TrimX).ToString(CultureInfo.InvariantCulture));
                            w.WriteAttributeString("frameY", (-p.TrimY).ToString(CultureInfo.InvariantCulture));
                            w.WriteAttributeString("frameWidth", p.SourceWidth.ToString(CultureInfo.InvariantCulture));
                            w.WriteAttributeString("frameHeight", p.SourceHeight.ToString(CultureInfo.InvariantCulture));
                        }
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                if (several) w.WriteEndElement();
                w.WriteEndDocument();
            }
            return sb.ToString();
        }

        // =================================================================
        //  Saving a packed sheet
        // =================================================================

        public sealed record SpriteSaveResult(string? Problem, List<string> Files);

        /// <summary>
        /// Writes the sheet PNG(s) and the coordinates file. The first sheet goes to <paramref name="pngPath"/>; more are named
        /// "name_2.png" ... and the coordinates file shares the name. Unless the user chose to replace (Save dialog), a name that is
        /// taken gets " (2)" for the whole set, so a set is never half old and half new. If anything fails, what this run wrote is removed.
        /// </summary>
        public static SpriteSaveResult SavePack(string pngPath, bool overwriteAllowed, IReadOnlyList<SpritePage> pages, Func<int, byte[]> getSourceBgra,
            SpriteCoordFormat format, int padding)
        {
            string folder = Path.GetDirectoryName(pngPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(pngPath);
            string coordExt = CoordinateExtension(format);
            if (!overwriteAllowed) stem = PickStem(folder, stem, pages.Count, coordExt);

            var imageFiles = Enumerable.Range(0, pages.Count).Select(i => PageFileName(stem, i)).ToList();
            var written = new List<string>();
            try
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    var rgba = BgraToRgba(ComposeSheet(pages[i], getSourceBgra));
                    string path = Path.Combine(folder, imageFiles[i]);
                    if (SafeFile.WriteAllBytes(path, PngWriter.Encode(pages[i].Width, pages[i].Height, rgba)) is { } error)
                        return Undo(written, error);
                    written.Add(path);
                }
                if (BuildCoordinates(format, pages, imageFiles, padding) is { } text)
                {
                    string path = Path.Combine(folder, stem + coordExt);
                    if (SafeFile.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text)) is { } error)
                        return Undo(written, error);
                    written.Add(path);
                }
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IndexOutOfRangeException)
            {
                return Undo(written, ex.Message);
            }
            return new SpriteSaveResult(null, written);
        }

        private static SpriteSaveResult Undo(List<string> written, string problem)
        {
            foreach (string path in written)
                try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new SpriteSaveResult(problem, new List<string>());
        }

        public static string PageFileName(string stem, int page) => page == 0 ? stem + ".png" : $"{stem}_{page + 1}.png";

        /// <summary>The first name (stem, "stem (2)", ...) for which none of the files of the set exist yet.</summary>
        public static string PickStem(string folder, string stem, int pageCount, string coordExtension)
        {
            for (int n = 1; ; n++)
            {
                string candidate = n == 1 ? stem : $"{stem} ({n})";
                bool clash = Enumerable.Range(0, pageCount).Any(i => File.Exists(Path.Combine(folder, PageFileName(candidate, i))))
                             || coordExtension.Length > 0 && File.Exists(Path.Combine(folder, candidate + coordExtension));
                if (!clash) return candidate;
            }
        }

        // =================================================================
        //  Slicing
        // =================================================================

        /// <summary>The grid of cells for a picture of the given size, or a problem code (CellTooSmall, CellTooBig, TooManyCells, BadNumbers).</summary>
        public static SpriteSliceResult Slice(int imageWidth, int imageHeight, SpriteSliceOptions o)
        {
            int margin = Math.Max(0, o.Margin), spacing = Math.Max(0, o.Spacing);
            int usableW = imageWidth - 2 * margin, usableH = imageHeight - 2 * margin;
            int columns, rows, cw, ch;

            if (o.Mode == SpriteSplitMode.ByCount)
            {
                columns = o.Columns;
                rows = o.Rows;
                if (columns < 1 || rows < 1) return Problem("BadNumbers");
                cw = (usableW - (columns - 1) * spacing) / columns;
                ch = (usableH - (rows - 1) * spacing) / rows;
                if (cw < 1 || ch < 1) return Problem("CellTooSmall");
            }
            else
            {
                cw = o.CellWidth;
                ch = o.CellHeight;
                if (cw < 1 || ch < 1) return Problem("BadNumbers");
                columns = (usableW + spacing) / (cw + spacing);
                rows = (usableH + spacing) / (ch + spacing);
                if (columns < 1 || rows < 1) return Problem("CellTooBig");
            }
            if ((long)columns * rows > MaxSliceCells) return Problem("TooManyCells");

            var cells = new List<SpriteCell>(columns * rows);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    cells.Add(new SpriteCell(r * columns + c, r, c, margin + c * (cw + spacing), margin + r * (ch + spacing), cw, ch));

            return new SpriteSliceResult
            {
                Columns = columns,
                Rows = rows,
                CellWidth = cw,
                CellHeight = ch,
                LeftoverX = usableW - (columns * cw + (columns - 1) * spacing),
                LeftoverY = usableH - (rows * ch + (rows - 1) * spacing),
                Cells = cells,
            };

            static SpriteSliceResult Problem(string code) => new() { ProblemCode = code };
        }

        /// <summary>"prefix_01" ... padded so the names sort in order.</summary>
        public static string SliceName(string? prefix, int index, int count)
        {
            int digits = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
            return SafeName(prefix) + "_" + (index + 1).ToString("D" + digits, CultureInfo.InvariantCulture);
        }

        /// <summary>True when every pixel of the cell is fully see-through (a picture without see-through pixels has no empty cells).</summary>
        public static bool IsCellEmpty(byte[] bgra, int imageWidth, SpriteCell cell)
        {
            for (int y = 0; y < cell.Height; y++)
            {
                int row = ((cell.Y + y) * imageWidth + cell.X) * 4;
                for (int x = 0; x < cell.Width; x++)
                    if (bgra[row + x * 4 + 3] != 0) return false;
            }
            return true;
        }

        public static byte[] CropRgba(byte[] bgra, int imageWidth, SpriteCell cell)
        {
            var rgba = new byte[cell.Width * cell.Height * 4];
            for (int y = 0; y < cell.Height; y++)
            {
                int src = ((cell.Y + y) * imageWidth + cell.X) * 4, dst = y * cell.Width * 4;
                for (int x = 0; x < cell.Width; x++)
                {
                    rgba[dst + x * 4] = bgra[src + x * 4 + 2];
                    rgba[dst + x * 4 + 1] = bgra[src + x * 4 + 1];
                    rgba[dst + x * 4 + 2] = bgra[src + x * 4];
                    rgba[dst + x * 4 + 3] = bgra[src + x * 4 + 3];
                }
            }
            return rgba;
        }

        public sealed record SpriteSliceSaveResult(string? Problem, string Folder, int Written, int SkippedEmpty, int Failed, bool Cancelled);

        /// <summary>
        /// Writes every cell as its own PNG into a new "name frames" folder (never into an existing one). A cell that fails is counted and
        /// the rest go on; cancelling keeps the files already written.
        /// </summary>
        public static SpriteSliceSaveResult SaveSlices(string parentFolder, string sheetName, byte[] bgra, int imageWidth, IReadOnlyList<SpriteCell> cells,
            string? prefix, bool skipEmpty, IProgress<int>? progress, CancellationToken token)
        {
            string folder = UniqueFolder(Path.Combine(parentFolder, SafeFileName(sheetName) + " frames"));
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new SpriteSliceSaveResult(ex.Message, folder, 0, 0, 0, false);
            }

            var keep = skipEmpty ? cells.Where(c => !IsCellEmpty(bgra, imageWidth, c)).ToList() : cells.ToList();
            int skipped = cells.Count - keep.Count;
            int written = 0, failed = 0, done = 0;
            foreach (var cell in keep)
            {
                if (token.IsCancellationRequested) return new SpriteSliceSaveResult(null, folder, written, skipped, failed, true);
                string name = SliceName(prefix, cell.Index, cells.Count) + ".png";
                var png = PngWriter.Encode(cell.Width, cell.Height, CropRgba(bgra, imageWidth, cell));
                if (SafeFile.WriteAllBytes(Path.Combine(folder, name), png) == null) written++;
                else failed++;
                progress?.Report(++done);
            }
            return new SpriteSliceSaveResult(null, folder, written, skipped, failed, false);
        }

        /// <summary>A file or folder name that Windows accepts.</summary>
        public static string SafeFileName(string? name)
        {
            string clean = string.Concat((name ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            return clean.Length > 0 ? clean : "spritesheet";
        }

        public static string UniqueFolder(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int n = 2; ; n++)
            {
                string candidate = $"{path} ({n})";
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\sprite_packer.json - the setup only, never an image or a path)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "sprite_packer.json");

        public static SpritePackerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new SpritePackerSettings();
                var s = JsonSerializer.Deserialize<SpritePackerSettings>(File.ReadAllText(file), SettingsJson) ?? new SpritePackerSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = SpriteMode.Pack;
                if (!Enum.IsDefined(s.Format)) s.Format = SpriteCoordFormat.Json;
                if (!Enum.IsDefined(s.SplitMode)) s.SplitMode = SpriteSplitMode.ByCount;
                s.Padding = Math.Clamp(s.Padding, 0, 32);
                s.MaxSide = Math.Clamp(s.MaxSide, 16, MaxSide);
                s.Columns = Math.Clamp(s.Columns, 1, 1000);
                s.Rows = Math.Clamp(s.Rows, 1, 1000);
                s.CellWidth = Math.Clamp(s.CellWidth, 1, MaxSide);
                s.CellHeight = Math.Clamp(s.CellHeight, 1, MaxSide);
                s.Margin = Math.Clamp(s.Margin, 0, 1000);
                s.Spacing = Math.Clamp(s.Spacing, 0, 1000);
                s.NamePrefix = SafeName(s.NamePrefix);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new SpritePackerSettings();
            }
        }

        public static void SaveSettings(SpritePackerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Settings not saved: {ex.Message}");
            }
        }
    }
}
