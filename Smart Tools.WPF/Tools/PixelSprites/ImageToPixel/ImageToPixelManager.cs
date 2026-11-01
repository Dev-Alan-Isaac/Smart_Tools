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
                problem = $"That would be {w:N0} x {h:N0} pixels - pick a smaller enlargement (the limit is {PixelArtManager.MaxExportSide:N0}).";
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
