using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind PaletteExtractorView: finds the dominant colours of a picture
    /// (k-means or median cut), builds related palettes (light/dark, complementary,
    /// analogous, triadic) and writes hex / CSS / JSON text.
    ///
    /// No WPF types - the pixels come in as a BGRA byte array (the view or
    /// PaletteExtractorManager.Render.cs reads them from the picture), so all of this
    /// is unit-testable headlessly. Colour maths (HSL, contrast, text colour) is
    /// <see cref="ColorManager"/>'s; nothing is copied.
    /// </summary>
    public static partial class PaletteExtractorManager
    {
        /// <summary>Pixels with a channel at or above this on all three count as "near white".</summary>
        private const int NearWhiteMin = 240;

        /// <summary>Pixels with every channel at or below this count as "near black".</summary>
        private const int NearBlackMax = 25;

        /// <summary>Alpha below this is "transparent" (0-255).</summary>
        private const int TransparentBelow = 128;

        private const int MaxKMeansRounds = 24;

        // =================================================================
        //  Extraction
        // =================================================================

        /// <param name="bgra">w * h * 4 bytes, straight (non-premultiplied) alpha, B G R A order.</param>
        /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled.</exception>
        public static PaletteExtractResult Extract(byte[] bgra, int width, int height, PaletteExtractOptions options, CancellationToken ct = default)
        {
            options = new PaletteExtractOptions
            {
                ColorCount = options.ColorCount,
                Method = options.Method,
                IgnoreTransparent = options.IgnoreTransparent,
                IgnoreNearWhite = options.IgnoreNearWhite,
                IgnoreNearBlack = options.IgnoreNearBlack,
            }.Clamped();

            long pixelCount = (long)width * height;
            if (width <= 0 || height <= 0 || bgra.LongLength < pixelCount * 4)
                return new PaletteExtractResult { Colors = Array.Empty<PaletteColor>() };

            // 1. Histogram on 5 bits per channel: a photo's millions of pixels become
            //    at most 32,768 weighted points, so both methods run in milliseconds.
            var bins = new Dictionary<int, Bin>();
            int counted = 0, skipped = 0;

            for (long i = 0; i < pixelCount; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

                int o = (int)(i * 4);
                byte b = bgra[o], g = bgra[o + 1], r = bgra[o + 2], a = bgra[o + 3];

                if (options.IgnoreTransparent && a < TransparentBelow) { skipped++; continue; }
                if (options.IgnoreNearWhite && r >= NearWhiteMin && g >= NearWhiteMin && b >= NearWhiteMin) { skipped++; continue; }
                if (options.IgnoreNearBlack && r <= NearBlackMax && g <= NearBlackMax && b <= NearBlackMax) { skipped++; continue; }

                int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                ref Bin bin = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(bins, key, out _);
                bin.Count++;
                bin.SumR += r;
                bin.SumG += g;
                bin.SumB += b;
                counted++;
            }

            if (counted == 0)
                return new PaletteExtractResult { Colors = Array.Empty<PaletteColor>(), SkippedPixels = skipped };

            var points = new List<Point3>(bins.Count);
            foreach (var bin in bins.Values)
                points.Add(new Point3(bin.SumR / (double)bin.Count, bin.SumG / (double)bin.Count, bin.SumB / (double)bin.Count, bin.Count));

            // Keys of a Dictionary come out in insertion order, which follows the picture; sort so
            // the same colours always give the same answer whatever order the pixels came in.
            points.Sort(static (p, q) =>
            {
                int c = q.Weight.CompareTo(p.Weight);
                if (c != 0) return c;
                c = p.R.CompareTo(q.R);
                if (c != 0) return c;
                c = p.G.CompareTo(q.G);
                return c != 0 ? c : p.B.CompareTo(q.B);
            });

            ct.ThrowIfCancellationRequested();

            var clusters = options.Method == PaletteMethod.MedianCut
                ? MedianCut(points, options.ColorCount, ct)
                : KMeans(points, options.ColorCount, ct);

            // 2. Cluster -> palette colour, biggest first.
            var ordered = clusters
                .Where(c => c.Weight > 0)
                .OrderByDescending(c => c.Weight)
                .ThenBy(c => c.R).ThenBy(c => c.G).ThenBy(c => c.B)
                .ToList();

            var colors = new List<PaletteColor>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var rgb = new RgbColor(ToByte(ordered[i].R), ToByte(ordered[i].G), ToByte(ordered[i].B));
                var text = ColorManager.BestTextColor(rgb);
                colors.Add(new PaletteColor
                {
                    Index = i + 1,
                    Color = rgb,
                    Share = ordered[i].Weight / counted,
                    TextColor = text,
                    TextContrast = ColorManager.Contrast(rgb, text).Ratio,
                });
            }

            return new PaletteExtractResult { Colors = colors, CountedPixels = counted, SkippedPixels = skipped };
        }

        private struct Bin
        {
            public int Count;
            public long SumR, SumG, SumB;
        }

        private readonly record struct Point3(double R, double G, double B, double Weight);

        private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

        // ---- median cut ---------------------------------------------------

        private static List<Point3> MedianCut(List<Point3> points, int k, CancellationToken ct)
        {
            var boxes = new List<List<Point3>> { points };

            while (boxes.Count < k)
            {
                ct.ThrowIfCancellationRequested();

                // Split the box with the widest spread (weighted by how many pixels it holds).
                int pick = -1;
                double best = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Count < 2) continue;
                    double score = Spread(boxes[i], out _) * Math.Sqrt(boxes[i].Sum(p => p.Weight));
                    if (score > best) { best = score; pick = i; }
                }
                if (pick < 0) break; // every box is a single colour

                var box = boxes[pick];
                Spread(box, out int channel);
                box.Sort((p, q) => Channel(p, channel).CompareTo(Channel(q, channel)));

                // Cut where half of the pixels (not half of the distinct colours) are on each side.
                double half = box.Sum(p => p.Weight) / 2, run = 0;
                int cut = 1;
                for (int i = 0; i < box.Count - 1; i++)
                {
                    run += box[i].Weight;
                    cut = i + 1;
                    if (run >= half) break;
                }

                boxes[pick] = box.GetRange(0, cut);
                boxes.Add(box.GetRange(cut, box.Count - cut));
            }

            return boxes.Select(Mean).ToList();
        }

        private static double Channel(Point3 p, int c) => c switch { 0 => p.R, 1 => p.G, _ => p.B };

        /// <summary>Widest channel range in the box; <paramref name="channel"/> says which (0 R, 1 G, 2 B).</summary>
        private static double Spread(List<Point3> box, out int channel)
        {
            double minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            foreach (var p in box)
            {
                minR = Math.Min(minR, p.R); maxR = Math.Max(maxR, p.R);
                minG = Math.Min(minG, p.G); maxG = Math.Max(maxG, p.G);
                minB = Math.Min(minB, p.B); maxB = Math.Max(maxB, p.B);
            }
            double r = maxR - minR, g = maxG - minG, b = maxB - minB;
            if (g >= r && g >= b) { channel = 1; return g; }
            if (r >= b) { channel = 0; return r; }
            channel = 2;
            return b;
        }

        private static Point3 Mean(List<Point3> box)
        {
            double w = 0, r = 0, g = 0, b = 0;
            foreach (var p in box) { w += p.Weight; r += p.R * p.Weight; g += p.G * p.Weight; b += p.B * p.Weight; }
            return w <= 0 ? new Point3(0, 0, 0, 0) : new Point3(r / w, g / w, b / w, w);
        }

        // ---- k-means ------------------------------------------------------

        private static List<Point3> KMeans(List<Point3> points, int k, CancellationToken ct)
        {
            k = Math.Min(k, points.Count);

            // Deterministic k-means++ style start: the heaviest colour first, then each next
            // centre is the point far from every centre chosen so far, weighted by its pixels.
            var centers = new List<Point3> { points[0] };
            var nearest = new double[points.Count];
            for (int i = 0; i < points.Count; i++) nearest[i] = Distance(points[i], centers[0]);

            while (centers.Count < k)
            {
                int pick = -1;
                double best = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    double score = nearest[i] * Math.Sqrt(points[i].Weight);
                    if (score > best) { best = score; pick = i; }
                }
                if (pick < 0) break; // all remaining points sit on a centre

                centers.Add(points[pick]);
                for (int i = 0; i < points.Count; i++)
                    nearest[i] = Math.Min(nearest[i], Distance(points[i], points[pick]));
            }

            var assign = new int[points.Count];
            Array.Fill(assign, -1);

            for (int round = 0; round < MaxKMeansRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                bool changed = false;
                var sums = new double[centers.Count, 4];

                for (int i = 0; i < points.Count; i++)
                {
                    int bestC = 0;
                    double bestD = double.MaxValue;
                    for (int c = 0; c < centers.Count; c++)
                    {
                        double d = Distance(points[i], centers[c]);
                        if (d < bestD) { bestD = d; bestC = c; }
                    }
                    if (assign[i] != bestC) { assign[i] = bestC; changed = true; }

                    var p = points[i];
                    sums[bestC, 0] += p.R * p.Weight;
                    sums[bestC, 1] += p.G * p.Weight;
                    sums[bestC, 2] += p.B * p.Weight;
                    sums[bestC, 3] += p.Weight;
                }

                for (int c = 0; c < centers.Count; c++)
                {
                    double w = sums[c, 3];
                    if (w > 0) centers[c] = new Point3(sums[c, 0] / w, sums[c, 1] / w, sums[c, 2] / w, w);
                    else centers[c] = centers[c] with { Weight = 0 };
                }

                if (!changed) break;
            }

            return centers;
        }

        /// <summary>Squared distance with the "redmean"-style weights that follow the eye better than plain RGB.</summary>
        private static double Distance(Point3 a, Point3 b)
        {
            double rm = (a.R + b.R) / 2;
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
        }

        // =================================================================
        //  Related palettes
        // =================================================================

        /// <summary>Colours built from <paramref name="color"/>. The colour itself is always in the list.</summary>
        public static List<RgbColor> BuildVariant(RgbColor color, PaletteVariantKind kind)
        {
            var hsl = ColorManager.RgbToHsl(color);

            RgbColor Hue(double offset) => ColorManager.HslToRgb(hsl with { H = ColorManager.NormalizeHue(hsl.H + offset) });

            switch (kind)
            {
                case PaletteVariantKind.Complementary:
                    return new() { color, Hue(180) };

                case PaletteVariantKind.Analogous:
                    return new() { Hue(-30), color, Hue(30) };

                case PaletteVariantKind.Triadic:
                    return new() { color, Hue(120), Hue(240) };

                default: // light and dark: two darker steps, the colour, two lighter steps
                    RgbColor Light(double l) => ColorManager.HslToRgb(hsl with { L = Math.Clamp(l, 0, 100) });
                    double room = 100 - hsl.L;
                    return new()
                    {
                        Light(hsl.L * 0.45),
                        Light(hsl.L * 0.72),
                        color,
                        Light(hsl.L + room * 0.35),
                        Light(hsl.L + room * 0.7),
                    };
            }
        }

        // =================================================================
        //  Text output
        // =================================================================

        public static string ToHexList(IEnumerable<RgbColor> colors) =>
            string.Join(Environment.NewLine, colors.Select(c => c.Hex));

        /// <summary>CSS custom properties: <c>--palette-1: #AABBCC;</c> inside <c>:root</c>.</summary>
        public static string ToCss(IEnumerable<RgbColor> colors)
        {
            var sb = new StringBuilder(":root {\n");
            int i = 0;
            foreach (var c in colors)
            {
                i++;
                sb.Append("  --palette-").Append(i.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(c.Hex).Append(";\n");
            }
            return sb.Append("}\n").ToString();
        }

        public static string ToJson(IReadOnlyList<PaletteColor> colors)
        {
            var items = colors.Select(c =>
            {
                return new
                {
                    hex = c.Hex,
                    rgb = ColorManager.ToCssRgb(c.Color),
                    hsl = ColorManager.ToCssHsl(c.Color),
                    share = Math.Round(c.Share * 100, 2),
                    textColor = c.TextColor.Hex,
                    textContrast = c.TextContrast,
                };
            });

            return JsonSerializer.Serialize(items, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n";
        }

        public static string ExtensionFor(PaletteExportFormat format) => format switch
        {
            PaletteExportFormat.Css => ".css",
            PaletteExportFormat.Json => ".json",
            PaletteExportFormat.Png => ".png",
            _ => ".txt",
        };

        public static string FileFilterFor(PaletteExportFormat format) => format switch
        {
            PaletteExportFormat.Css => Loc.T("Str_PaletteExtractor_MsgCssFilter", "CSS file (*.css)|*.css"),
            PaletteExportFormat.Json => Loc.T("Str_PaletteExtractor_MsgJsonFilter", "JSON file (*.json)|*.json"),
            PaletteExportFormat.Png => Loc.T("Str_PaletteExtractor_MsgPngFilter", "PNG image (*.png)|*.png"),
            _ => Loc.T("Str_PaletteExtractor_MsgTxtFilter", "Text file (*.txt)|*.txt"),
        };

        /// <summary>Writes a text export (not PNG) via a hidden temp file, then moves it into place. The caller's Save dialog already asked about replacing.</summary>
        public static void WriteText(string path, string text)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path) ?? ".", $".{Path.GetFileName(path)}.creating-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }

        // =================================================================
        //  Settings (how the tool is set up - never the picture or its folder)
        // =================================================================

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "palette_extractor.json");

        private sealed class SettingsDto
        {
            public int? ColorCount { get; set; }
            public string? Method { get; set; }
            public bool? IgnoreTransparent { get; set; }
            public bool? IgnoreNearWhite { get; set; }
            public bool? IgnoreNearBlack { get; set; }
        }

        /// <summary>Never throws: a missing or unreadable file gives the defaults.</summary>
        public static PaletteExtractOptions LoadOptions(string? path = null)
        {
            var options = new PaletteExtractOptions();
            try
            {
                path ??= SettingsPath;
                if (!File.Exists(path)) return options;

                var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(path));
                if (dto == null) return options;

                if (dto.ColorCount is int n) options.ColorCount = n;
                if (Enum.TryParse(dto.Method, out PaletteMethod m)) options.Method = m;
                if (dto.IgnoreTransparent is bool t) options.IgnoreTransparent = t;
                if (dto.IgnoreNearWhite is bool w) options.IgnoreNearWhite = w;
                if (dto.IgnoreNearBlack is bool b) options.IgnoreNearBlack = b;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // defaults
            }
            return options.Clamped();
        }

        /// <summary>Best-effort; written to a temp file and moved into place.</summary>
        public static void SaveOptions(PaletteExtractOptions options, string? path = null)
        {
            try
            {
                path ??= SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string json = JsonSerializer.Serialize(new SettingsDto
                {
                    ColorCount = options.ColorCount,
                    Method = options.Method.ToString(),
                    IgnoreTransparent = options.IgnoreTransparent,
                    IgnoreNearWhite = options.IgnoreNearWhite,
                    IgnoreNearBlack = options.IgnoreNearBlack,
                }, new JsonSerializerOptions { WriteIndented = true });
                AtomicFile.WriteAllText(path, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the on-screen setup is unaffected
            }
        }
    }
}
