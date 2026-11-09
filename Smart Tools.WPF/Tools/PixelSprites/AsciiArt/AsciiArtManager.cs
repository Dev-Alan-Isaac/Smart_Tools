using ImageMagick;
using Orientation = ImageMagick.Orientation;
using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    public enum AsciiProblem
    {
        None,
        CantOpen,
        TooFewChars,
        TooBig
    }

    /// <summary>The picture, how it was made, or why it could not be made.</summary>
    public sealed record AsciiConvertResult(AsciiGrid? Grid, AsciiConvertInfo? Info, AsciiProblem Problem, string Detail = "");

    /// <summary>
    /// ASCII Art Maker, picture to characters. The result is sized in characters, not in
    /// pixels, so the same photo at 640 px or 6000 px gives the same layout; a big picture
    /// is shrunk with a proper filter (not by dropping pixels), sampled four times per
    /// character cell, and each character is the one whose real shape in the chosen font
    /// best matches those four samples. That is what keeps edges and fine detail.
    /// Drawing by hand is in AsciiEditor; saving and copying in AsciiArtManager.Export.cs.
    /// </summary>
    public static partial class AsciiArtManager
    {
        public const int MinColumns = 20;
        public const int MaxColumns = 600;
        /// <summary>No picture is made with more cells than this (a 600 x 400 grid is 240,000).</summary>
        public const int MaxCells = 250_000;

        // ---------------- Character sets ----------------

        private const string ClassicShort = "@%#*+=-:.";
        private const string ClassicLong = "$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\\|()1{}[]?-_+~<>i!lI;:,\"^`'.";
        private const string SpecialChars = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        private const string LetterChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private const string DigitChars = "0123456789";

        /// <summary>The characters a set uses, without duplicates and without anything the font cannot draw (<c>ignored</c> counts those).</summary>
        public static List<char> BuildCharList(AsciiSettings s, AsciiGlyphs glyphs, out int ignored)
        {
            string source = s.CharSet switch
            {
                AsciiCharSet.Classic => ClassicShort,
                AsciiCharSet.ClassicDetailed => ClassicLong,
                AsciiCharSet.Letters => LetterChars,
                AsciiCharSet.Special => SpecialChars,
                AsciiCharSet.Mixed => LetterChars + DigitChars + SpecialChars,
                _ => s.CustomChars ?? "",
            };
            if (s.UseSpace) source += " ";
            var seen = new HashSet<char>();
            var list = new List<char>();
            ignored = 0;
            foreach (char c in source)
            {
                if (char.IsControl(c) || c == ' ' || !seen.Add(c)) continue;
                if (!glyphs.Has(c)) { ignored++; continue; }
                list.Add(c);
            }
            return list;
        }

        private static readonly Dictionary<string, AsciiCharStat[]> StatCache = new();

        /// <summary>The ink coverage of each character in this font (drawn once on an STA thread, then remembered).</summary>
        public static async Task<AsciiCharStat[]> MeasureAsync(AsciiGlyphs glyphs, List<char> chars)
        {
            string key = glyphs.FontName + "|" + new string(chars.ToArray());
            lock (StatCache)
            {
                if (StatCache.TryGetValue(key, out var hit)) return hit;
            }
            var stats = await ZxingImageHelpers.RunOnStaThread(() => glyphs.Measure(chars));
            lock (StatCache)
            {
                if (StatCache.Count > 40) StatCache.Clear();
                StatCache[key] = stats;
            }
            return stats;
        }

        // ---------------- Size ----------------

        /// <summary>Columns and rows for a picture: rows follow the picture's shape and the font's cell shape so nothing looks stretched.</summary>
        public static (int Cols, int Rows) PlanSize(AsciiSettings s, int sourceWidth, int sourceHeight, double cellAspect)
        {
            int cols = s.MatchOriginal ? sourceWidth / 4 : s.Columns;
            cols = Math.Clamp(cols, MinColumns, MaxColumns);
            double ratio = sourceWidth <= 0 ? 1 : (double)sourceHeight / sourceWidth;
            int rows = Math.Max(1, (int)Math.Round(cols * ratio * cellAspect));
            if ((long)cols * rows > MaxCells)
            {
                double k = Math.Sqrt((double)MaxCells / ((long)cols * rows));
                cols = Math.Max(MinColumns, (int)(cols * k));
                rows = Math.Max(1, (int)(rows * k));
            }
            return (cols, rows);
        }

        // ---------------- Convert ----------------

        public static async Task<AsciiConvertResult> ConvertFileAsync(string path, AsciiSettings s, CancellationToken ct)
        {
            AsciiGlyphs glyphs;
            List<char> chars;
            int ignored;
            try
            {
                glyphs = AsciiGlyphs.Create(s.FontName);
                chars = BuildCharList(s, glyphs, out ignored);
            }
            catch (InvalidOperationException ex)
            {
                return new AsciiConvertResult(null, null, AsciiProblem.CantOpen, ex.Message);
            }
            if (chars.Count < 2) return new AsciiConvertResult(null, null, AsciiProblem.TooFewChars);

            var stats = await MeasureAsync(glyphs, chars);
            ct.ThrowIfCancellationRequested();

            try
            {
                return await Task.Run(() => ConvertCore(path, s, glyphs, stats, ignored, ct), ct);
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or OutOfMemoryException or InvalidOperationException)
            {
                Log.Warn($"ASCII art: could not read {path}: {ex.Message}");
                return new AsciiConvertResult(null, null, AsciiProblem.CantOpen, ex.Message);
            }
        }

        private static AsciiConvertResult ConvertCore(string path, AsciiSettings s, AsciiGlyphs glyphs, AsciiCharStat[] stats, int ignored, CancellationToken ct)
        {
            // Size first (header only), so a big JPEG can be read already reduced to what is needed.
            var info = new MagickImageInfo(path);
            int srcW = (int)info.Width, srcH = (int)info.Height;
            if (info.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom)
                (srcW, srcH) = (srcH, srcW);
            if (srcW <= 0 || srcH <= 0) return new AsciiConvertResult(null, null, AsciiProblem.CantOpen);

            var (cols, rows) = PlanSize(s, srcW, srcH, glyphs.CellAspect);
            // Read at least 4 pixels per character across: enough to sample each cell four times with room to filter.
            uint want = (uint)Math.Max(cols * 4, rows * 4);
            using var loaded = ImageLoad.Open(path, ct, want);
            var img = loaded.Image;
            ct.ThrowIfCancellationRequested();

            img.AutoOrient();
            img.BackgroundColor = s.DarkBackground ? MagickColors.Black : MagickColors.White;
            img.Alpha(AlphaAction.Remove);
            img.FilterType = FilterType.Lanczos;
            img.Resize(new MagickGeometry((uint)(cols * 2), (uint)(rows * 2)) { IgnoreAspectRatio = true });
            if (s.Sharpen) img.UnsharpMask(0, 0.8, 0.9, 0.02);
            ct.ThrowIfCancellationRequested();

            int sw = cols * 2, sh = rows * 2;
            byte[] rgba = img.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidOperationException("No pixel data.");
            if (rgba.Length < sw * sh * 4) return new AsciiConvertResult(null, null, AsciiProblem.CantOpen);

            var grid = Build(rgba, sw, sh, cols, rows, s, stats, ct);
            return new AsciiConvertResult(grid, new AsciiConvertInfo(cols, rows, srcW, srcH, stats.Length, ignored), AsciiProblem.None);
        }

        /// <summary>
        /// Pure core: RGBA pixels at two samples per character in each direction (sw = cols * 2) in,
        /// characters and colours out. Tested without any picture file.
        /// </summary>
        public static AsciiGrid Build(byte[] rgba, int sw, int sh, int cols, int rows, AsciiSettings s, AsciiCharStat[] stats, CancellationToken ct = default)
        {
            // Tone: luminance 0..1, then auto levels, contrast and brightness.
            var lum = new float[sw * sh];
            for (int i = 0; i < lum.Length; i++)
                lum[i] = (0.2126f * rgba[i * 4] + 0.7152f * rgba[i * 4 + 1] + 0.0722f * rgba[i * 4 + 2]) / 255f;

            if (s.AutoLevels)
            {
                var hist = new int[256];
                foreach (float v in lum) hist[(int)Math.Clamp(v * 255f, 0, 255)]++;
                long cut = (long)(lum.Length * 0.01);
                int lo = 0, hi = 255;
                long acc = 0;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc > cut) { lo = i; break; } }
                acc = 0;
                for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc > cut) { hi = i; break; } }
                if (hi - lo >= 8)
                {
                    float l = lo / 255f, span = (hi - lo) / 255f;
                    for (int i = 0; i < lum.Length; i++) lum[i] = Math.Clamp((lum[i] - l) / span, 0f, 1f);
                }
            }
            float factor = (float)Math.Pow(4.0, Math.Clamp(s.Contrast, -100, 100) / 100.0);
            float shift = Math.Clamp(s.Brightness, -100, 100) / 200f;
            bool inkIsLight = s.DarkBackground ^ s.Invert;   // light ink on a dark page, or dark ink on a light page
            for (int i = 0; i < lum.Length; i++)
            {
                float v = Math.Clamp((lum[i] - 0.5f) * factor + 0.5f + shift, 0f, 1f);
                lum[i] = inkIsLight ? v : 1f - v;   // now: how much ink this sample wants
            }

            // Characters ordered by how much ink they carry, each coverage scaled so the heaviest = 1.
            float max = stats.Max(c => c.Total);
            if (max <= 0) max = 1;
            int n = stats.Length;
            var chars = new char[n];
            var dens = new float[n];
            var q = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                chars[i] = stats[i].Ch;
                dens[i] = Math.Min(1f, stats[i].Total / max);
                q[i * 4] = Math.Min(1f, stats[i].Q0 / max);
                q[i * 4 + 1] = Math.Min(1f, stats[i].Q1 / max);
                q[i * 4 + 2] = Math.Min(1f, stats[i].Q2 / max);
                q[i * 4 + 3] = Math.Min(1f, stats[i].Q3 / max);
            }
            int[] order = Enumerable.Range(0, n).OrderBy(i => dens[i]).ToArray();
            float[] sortedDens = order.Select(i => dens[i]).ToArray();

            var grid = new AsciiGrid(cols, rows);
            bool colored = s.Color == AsciiColorMode.Colored;
            bool shapes = s.MatchShapes;

            Parallel.For(0, rows, new ParallelOptions { CancellationToken = ct }, y =>
            {
                for (int x = 0; x < cols; x++)
                {
                    int p0 = (y * 2) * sw + x * 2, p1 = p0 + sw;
                    float t0 = lum[p0], t1 = lum[p0 + 1], t2 = lum[p1], t3 = lum[p1 + 1];
                    float mean = (t0 + t1 + t2 + t3) * 0.25f;
                    float spread = Math.Max(Math.Max(t0, t1), Math.Max(t2, t3)) - Math.Min(Math.Min(t0, t1), Math.Min(t2, t3));

                    int pick;
                    if (!shapes || spread < 0.08f)
                    {
                        pick = order[Nearest(sortedDens, mean)];
                    }
                    else
                    {
                        // Shape match: the character whose four quarters best follow the four samples,
                        // with a small push toward the right overall darkness.
                        pick = 0;
                        float best = float.MaxValue;
                        for (int i = 0; i < n; i++)
                        {
                            float d0 = q[i * 4] - t0, d1 = q[i * 4 + 1] - t1, d2 = q[i * 4 + 2] - t2, d3 = q[i * 4 + 3] - t3;
                            float dm = dens[i] - mean;
                            float err = d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3 + 0.6f * dm * dm;
                            if (err < best) { best = err; pick = i; }
                        }
                    }
                    int cell = y * cols + x;
                    grid.Chars[cell] = chars[pick];

                    if (colored)
                    {
                        int r = rgba[p0 * 4] + rgba[(p0 + 1) * 4] + rgba[p1 * 4] + rgba[(p1 + 1) * 4];
                        int g = rgba[p0 * 4 + 1] + rgba[(p0 + 1) * 4 + 1] + rgba[p1 * 4 + 1] + rgba[(p1 + 1) * 4 + 1];
                        int b = rgba[p0 * 4 + 2] + rgba[(p0 + 1) * 4 + 2] + rgba[p1 * 4 + 2] + rgba[(p1 + 1) * 4 + 2];
                        grid.Colors[cell] = 0xFF000000u | (uint)(r / 4) << 16 | (uint)(g / 4) << 8 | (uint)(b / 4);
                    }
                }
            });
            return grid;
        }

        /// <summary>Index of the value closest to <c>v</c> in an ascending array.</summary>
        private static int Nearest(float[] sorted, float v)
        {
            int lo = 0, hi = sorted.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] < v) lo = mid + 1; else hi = mid;
            }
            if (lo > 0 && Math.Abs(sorted[lo - 1] - v) <= Math.Abs(sorted[lo] - v)) return lo - 1;
            return lo;
        }
    }
}
