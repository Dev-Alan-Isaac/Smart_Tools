using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    public sealed class ImageToPixelSettings
    {
        public int GridWidth { get; set; } = 48;
        /// <summary>0 = keep every colour; otherwise the number of colours in the picture.</summary>
        public int Colors { get; set; } = 16;
        /// <summary>"Auto" (colours picked from the picture) or the name of a palette from the Pixel Art Editor.</summary>
        public string Palette { get; set; } = "Auto";
        public bool Dither { get; set; }
        public int ExportScale { get; set; } = 8;
        public bool IncludeSubfolders { get; set; } = true;
        /// <summary>Added to each saved file's name when converting a list ("photo.jpg" becomes "photo-pixel.png").</summary>
        public string Suffix { get; set; } = "-pixel";
    }

    public sealed record PixelizeResult(uint[] Pixels, int Width, int Height, int ColorsUsed);

    /// <summary>
    /// Image to Pixel Art logic: shrink a picture to a small grid (area average), reduce it to a few colours (the
    /// picture's own via median cut, or one of the Pixel Art Editor's palettes), optionally dither, and write a sharp
    /// enlarged PNG. Pixels are 0xAARRGGBB like the Pixel Art Editor's. No WPF types.
    /// </summary>
    public static class ImageToPixelManager
    {
        public const string SettingsName = "image_to_pixel";
        public const int MaxGrid = 256;
        public static readonly int[] Scales = { 1, 2, 4, 8, 16, 32 };

        public static ImageToPixelSettings Clamp(ImageToPixelSettings s)
        {
            s.GridWidth = Math.Clamp(s.GridWidth, 4, MaxGrid);
            s.Colors = s.Colors == 0 ? 0 : Math.Clamp(s.Colors, 2, 64);
            s.Palette = PixelArtManager.Palettes.Any(p => p.Name == s.Palette) ? s.Palette : "Auto";
            s.ExportScale = Scales.Contains(s.ExportScale) ? s.ExportScale : 8;
            s.Suffix = string.Concat((s.Suffix ?? "-pixel").Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            if (s.Suffix.Length > 40) s.Suffix = s.Suffix[..40];
            return s;
        }

        /// <summary>BGRA (straight alpha) of any size to a pixel-art grid.</summary>
        public static PixelizeResult Pixelize(byte[] bgra, int width, int height, ImageToPixelSettings options)
        {
            var o = Clamp(new ImageToPixelSettings
            {
                GridWidth = options.GridWidth, Colors = options.Colors, Palette = options.Palette, Dither = options.Dither, ExportScale = options.ExportScale,
            });
            int gw = Math.Min(o.GridWidth, width);
            int gh = Math.Max(1, (int)Math.Round((double)height * gw / width));

            // Shrink with the icon maker's area average (premultiplied, no dark fringes).
            var small = IconMakerManager.Resample(bgra, width, height, gw, gh);

            var palette = o.Palette == "Auto"
                ? (o.Colors == 0 ? null : MedianCut(small, o.Colors))
                : PixelArtManager.PaletteByName(o.Palette).Colors.Where(c => (c >> 24) != 0).ToArray();

            var pixels = Quantize(small, gw, gh, palette, o.Dither);
            return new PixelizeResult(pixels, gw, gh, pixels.Where(p => p != 0).Distinct().Count());
        }

        /// <summary>Pixels to colours: transparent stays transparent (alpha under 128), the rest snaps to the palette.</summary>
        public static uint[] Quantize(byte[] bgra, int w, int h, uint[]? palette, bool dither)
        {
            var result = new uint[w * h];
            // Working copy in floating point so dithering can push error onto neighbours.
            var r = new float[w * h]; var g = new float[w * h]; var b = new float[w * h];
            var opaque = new bool[w * h];
            for (int i = 0; i < w * h; i++)
            {
                opaque[i] = bgra[i * 4 + 3] >= 128;
                b[i] = bgra[i * 4]; g[i] = bgra[i * 4 + 1]; r[i] = bgra[i * 4 + 2];
            }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!opaque[i]) continue;   // result stays 0 = transparent

                    uint chosen;
                    float cr = Math.Clamp(r[i], 0, 255), cg = Math.Clamp(g[i], 0, 255), cb = Math.Clamp(b[i], 0, 255);
                    if (palette == null || palette.Length == 0)
                        chosen = PixelArtManager.Pack(255, (byte)Math.Round(cr), (byte)Math.Round(cg), (byte)Math.Round(cb));
                    else
                        chosen = Nearest(palette, cr, cg, cb);

                    result[i] = chosen;
                    if (!dither || palette == null || palette.Length == 0) continue;

                    // Floyd-Steinberg: pass the leftover error to the next pixel and the row below.
                    float er = cr - (byte)(chosen >> 16), eg = cg - (byte)(chosen >> 8), eb = cb - (byte)chosen;
                    Spread(x + 1, y, 7 / 16f);
                    Spread(x - 1, y + 1, 3 / 16f);
                    Spread(x, y + 1, 5 / 16f);
                    Spread(x + 1, y + 1, 1 / 16f);

                    void Spread(int nx, int ny, float f)
                    {
                        if (nx < 0 || nx >= w || ny >= h) return;
                        int n = ny * w + nx;
                        if (!opaque[n]) return;
                        r[n] += er * f; g[n] += eg * f; b[n] += eb * f;
                    }
                }
            return result;
        }

        private static uint Nearest(uint[] palette, float r, float g, float b)
        {
            uint best = palette[0];
            float bestDistance = float.MaxValue;
            foreach (uint c in palette)
            {
                float dr = r - (byte)(c >> 16), dg = g - (byte)(c >> 8), db = b - (byte)c;
                // Eyes notice green most, blue least.
                float d = dr * dr * 0.30f + dg * dg * 0.59f + db * db * 0.11f;
                if (d < bestDistance) { bestDistance = d; best = c; }
            }
            return best;
        }

        // =================================================================
        //  Picking colours from the picture
        // =================================================================

        /// <summary>Median cut: split the colours into <paramref name="count"/> boxes and use each box's average.</summary>
        public static uint[] MedianCut(byte[] bgra, int count)
        {
            var colors = new List<(byte R, byte G, byte B)>();
            for (int i = 0; i + 3 < bgra.Length; i += 4)
                if (bgra[i + 3] >= 128) colors.Add((bgra[i + 2], bgra[i + 1], bgra[i]));
            if (colors.Count == 0) return Array.Empty<uint>();

            var boxes = new List<List<(byte R, byte G, byte B)>> { colors };
            while (boxes.Count < count)
            {
                // Split the box with the widest spread (most pixels x biggest range) that can still be split.
                var candidate = boxes
                    .Where(bx => bx.Select(c => (c.R, c.G, c.B)).Distinct().Count() > 1)
                    .OrderByDescending(bx => (long)bx.Count * Range(bx).Max)
                    .FirstOrDefault();
                if (candidate == null) break;

                int channel = Range(candidate).Channel;
                var sorted = candidate.OrderBy(c => channel == 0 ? c.R : channel == 1 ? c.G : c.B).ToList();
                int middle = sorted.Count / 2;
                boxes.Remove(candidate);
                boxes.Add(sorted.Take(middle).ToList());
                boxes.Add(sorted.Skip(middle).ToList());
                if (boxes.Any(bx => bx.Count == 0)) boxes.RemoveAll(bx => bx.Count == 0);
            }

            return boxes.Select(bx => PixelArtManager.Pack(255,
                    (byte)Math.Round(bx.Average(c => (double)c.R)),
                    (byte)Math.Round(bx.Average(c => (double)c.G)),
                    (byte)Math.Round(bx.Average(c => (double)c.B))))
                .Distinct().OrderBy(c => (c >> 16 & 0xFF) * 0.30 + (c >> 8 & 0xFF) * 0.59 + (c & 0xFF) * 0.11).ToArray();

            static (int Channel, int Max) Range(List<(byte R, byte G, byte B)> bx)
            {
                int rr = bx.Max(c => c.R) - bx.Min(c => c.R), gr = bx.Max(c => c.G) - bx.Min(c => c.G), br = bx.Max(c => c.B) - bx.Min(c => c.B);
                int max = Math.Max(rr, Math.Max(gr, br));
                return (max == rr ? 0 : max == gr ? 1 : 2, max);
            }
        }

        // =================================================================
        //  Finding pictures and converting a whole list
        // =================================================================

        public static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

        public const string PictureFilter = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp";
        public const int MaxPictures = 5000;

        public static bool IsPicture(string path) =>
            PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>Every picture under the paths (folders, or loose files), once each, A-Z. Links aren't followed.</summary>
        public static (List<string> Files, int Inaccessible) FindPictures(IEnumerable<string> paths, bool includeSubfolders)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int inaccessible = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = includeSubfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var here = Directory.EnumerateFiles(path, "*", options).Where(IsPicture).ToList();
                        here.Sort(StringComparer.OrdinalIgnoreCase);
                        foreach (string f in here) if (seen.Add(f)) files.Add(f);
                    }
                    else if (File.Exists(path) && IsPicture(path) && seen.Add(path)) files.Add(path);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    inaccessible++;
                }
            }
            return (files, inaccessible);
        }

        /// <summary>"photo.jpg" -> "photo-pixel.png" (the suffix is cleaned so it can't contain a path).</summary>
        public static string OutputName(string source, string suffix)
        {
            string clean = string.Concat((suffix ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
            return Path.GetFileNameWithoutExtension(source) + clean + ".png";
        }

        /// <summary>The path itself when free, otherwise "name (2).png", "name (3).png"... Never an existing file.</summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string folder = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(folder, $"{name} ({n}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        /// <summary>What happened to one picture: the file written, or why not.</summary>
        public sealed record PixelItemResult(string Source, string? Output, string? Error);

        public sealed record PixelBatchResult(List<PixelItemResult> Items, int Done, int Failed, bool Cancelled);

        /// <summary>
        /// Converts every picture with the same settings into PNGs in <paramref name="folder"/>, never over an existing file.
        /// <paramref name="load"/> turns a file into pixels (the view passes the WPF decoder; tests pass a fake).
        /// <paramref name="progress"/> gets each result as it is made, so rows can show Done or Failed at once.
        /// A picture that can't be read or saved is reported and skipped; the rest continue.
        /// </summary>
        public static PixelBatchResult ConvertAll(IReadOnlyList<string> sources, ImageToPixelSettings settings, string folder, string suffix,
            Func<string, (byte[]? Bgra, int Width, int Height, string Problem)> load, IProgress<(int Index, PixelItemResult Result)>? progress, CancellationToken token)
        {
            var results = new List<PixelItemResult>();
            int done = 0, failed = 0;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var all = sources.Select(s => new PixelItemResult(s, null, Loc.F("Str_ImageToPixel_MsgOutputFolderFailed", "Couldn't use the output folder: {0}", ex.Message))).ToList();
                return new PixelBatchResult(all, 0, all.Count, false);
            }

            for (int i = 0; i < sources.Count; i++)
            {
                if (token.IsCancellationRequested) return new PixelBatchResult(results, done, failed, true);
                string source = sources[i];
                PixelItemResult result;
                try
                {
                    var (bgra, w, h, problem) = load(source);
                    if (bgra == null) result = new PixelItemResult(source, null, problem == "ImageTooBig" ? Loc.T("Str_ImageToPixel_MsgTooBigShort", "Too big to open") : Loc.T("Str_ImageToPixel_MsgCantOpenShort", "Couldn't be opened"));
                    else
                    {
                        var pixels = Pixelize(bgra, w, h, settings);
                        var png = ToPng(pixels, settings.ExportScale, out string? tooBig);
                        if (png == null) result = new PixelItemResult(source, null, tooBig ?? Loc.T("Str_ImageToPixel_MsgNoPngShort", "Couldn't make the PNG"));
                        else
                        {
                            string target = UniquePath(Path.Combine(folder, OutputName(source, suffix)));
                            string? error = SafeFile.WriteAllBytes(target, png);
                            result = error == null ? new PixelItemResult(source, target, null) : new PixelItemResult(source, null, error);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
                {
                    result = new PixelItemResult(source, null, ex.Message);
                }

                results.Add(result);
                if (result.Error == null) done++; else failed++;
                progress?.Report((i, result));
            }
            return new PixelBatchResult(results, done, failed, false);
        }

        // =================================================================
        //  Picture out
        // =================================================================

        /// <summary>The grid as a PNG, every pixel enlarged to scale x scale (nearest neighbour, so edges stay sharp).</summary>
        public static byte[]? ToPng(PixelizeResult result, int scale, out string? problem)
        {
            problem = null;
            scale = Math.Clamp(scale, 1, 64);
            long w = (long)result.Width * scale, h = (long)result.Height * scale;
            if (w > PixelArtManager.MaxExportSide || h > PixelArtManager.MaxExportSide)
            {
                problem = Loc.F("Str_ImageToPixel_MsgEnlargeLimit", "That would be {0:N0} x {1:N0} pixels - pick a smaller enlargement (the limit is {2:N0}).", w, h, PixelArtManager.MaxExportSide);
                return null;
            }
            var rgba = PixelArtManager.ToRgba(result.Pixels);
            if (scale == 1) return PngWriter.Encode(result.Width, result.Height, rgba);

            int iw = (int)w, ih = (int)h;
            var big = new byte[iw * ih * 4];
            for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                    Buffer.BlockCopy(rgba, ((y / scale) * result.Width + x / scale) * 4, big, (y * iw + x) * 4, 4);
            return PngWriter.Encode(iw, ih, big);
        }

        /// <summary>BGRA pixels of the enlarged result, for showing on screen.</summary>
        public static byte[] ToBgra(PixelizeResult result)
        {
            var bgra = new byte[result.Pixels.Length * 4];
            for (int i = 0; i < result.Pixels.Length; i++)
            {
                uint p = result.Pixels[i];
                bgra[i * 4] = (byte)p; bgra[i * 4 + 1] = (byte)(p >> 8); bgra[i * 4 + 2] = (byte)(p >> 16); bgra[i * 4 + 3] = (byte)(p >> 24);
            }
            return bgra;
        }
    }
}
