using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    public enum IconFit { Fit, Fill }

    public sealed class IconMakerSettings
    {
        public IconFit Fit { get; set; } = IconFit.Fit;
        public List<int> Sizes { get; set; } = new() { 16, 32, 48, 64, 128, 256 };
    }

    /// <summary>
    /// Icon Maker logic: resize any picture to square icon sizes (area-averaged, so small icons stay crisp and
    /// semi-transparent edges don't turn dark) and pack them into a .ico file. Pure pixel maths, no WPF types.
    /// </summary>
    public static class IconMakerManager
    {
        public const string SettingsName = "icon_maker";
        public static readonly int[] AllSizes = { 16, 24, 32, 48, 64, 128, 256 };

        /// <summary>The files a website favicon set needs: name and pixel size.</summary>
        public static readonly (string Name, int Size)[] FaviconPngs =
        {
            ("favicon-16x16.png", 16),
            ("favicon-32x32.png", 32),
            ("apple-touch-icon.png", 180),
            ("android-chrome-192x192.png", 192),
            ("android-chrome-512x512.png", 512),
        };

        public static readonly int[] FaviconIcoSizes = { 16, 32, 48 };

        public static IconMakerSettings Clamp(IconMakerSettings s)
        {
            if (!Enum.IsDefined(s.Fit)) s.Fit = IconFit.Fit;
            s.Sizes = (s.Sizes ?? new List<int>()).Where(v => AllSizes.Contains(v)).Distinct().OrderBy(v => v).ToList();
            if (s.Sizes.Count == 0) s.Sizes = new List<int> { 16, 32, 48, 256 };
            return s;
        }

        // =================================================================
        //  Resizing
        // =================================================================

        /// <summary>
        /// Straight-alpha BGRA of any size to a <paramref name="size"/> x <paramref name="size"/> icon.
        /// Fit keeps the whole picture (see-through padding on the short side); Fill covers the square and crops the middle.
        /// </summary>
        public static byte[] ToSquare(byte[] bgra, int width, int height, int size, IconFit fit)
        {
            double scale = fit == IconFit.Fit ? Math.Min((double)size / width, (double)size / height) : Math.Max((double)size / width, (double)size / height);
            int dw = Math.Max(1, (int)Math.Round(width * scale));
            int dh = Math.Max(1, (int)Math.Round(height * scale));
            if (fit == IconFit.Fit) { dw = Math.Min(dw, size); dh = Math.Min(dh, size); }

            var scaled = Resample(bgra, width, height, dw, dh);
            var canvas = new byte[size * size * 4];
            int offsetX = (size - dw) / 2, offsetY = (size - dh) / 2;   // negative for Fill: crops
            for (int y = 0; y < size; y++)
            {
                int sy = y - offsetY;
                if (sy < 0 || sy >= dh) continue;
                for (int x = 0; x < size; x++)
                {
                    int sx = x - offsetX;
                    if (sx < 0 || sx >= dw) continue;
                    Buffer.BlockCopy(scaled, (sy * dw + sx) * 4, canvas, (y * size + x) * 4, 4);
                }
            }
            return canvas;
        }

        /// <summary>
        /// Area average when shrinking, bilinear when enlarging; colours are averaged weighted by alpha
        /// (premultiplied), so a see-through edge doesn't pick up dark fringes.
        /// </summary>
        public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[dw * dh * 4];
            double fx = (double)sw / dw, fy = (double)sh / dh;
            bool shrink = fx > 1 || fy > 1;

            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    double b = 0, g = 0, r = 0, a = 0, weight = 0;
                    if (shrink)
                    {
                        double x0 = x * fx, x1 = (x + 1) * fx, y0 = y * fy, y1 = (y + 1) * fy;
                        for (int sy = (int)y0; sy < Math.Ceiling(y1) && sy < sh; sy++)
                        {
                            double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                            for (int sx = (int)x0; sx < Math.Ceiling(x1) && sx < sw; sx++)
                            {
                                double w = wy * (Math.Min(sx + 1, x1) - Math.Max(sx, x0));
                                Add(src, (sy * sw + sx) * 4, w, ref b, ref g, ref r, ref a);
                                weight += w;
                            }
                        }
                    }
                    else
                    {
                        double cx = (x + 0.5) * fx - 0.5, cy = (y + 0.5) * fy - 0.5;
                        int ix = (int)Math.Floor(cx), iy = (int)Math.Floor(cy);
                        double tx = cx - ix, ty = cy - iy;
                        for (int j = 0; j <= 1; j++)
                            for (int i = 0; i <= 1; i++)
                            {
                                int sx = Math.Clamp(ix + i, 0, sw - 1), sy = Math.Clamp(iy + j, 0, sh - 1);
                                double w = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - ty : ty);
                                Add(src, (sy * sw + sx) * 4, w, ref b, ref g, ref r, ref a);
                                weight += w;
                            }
                    }

                    int o = (y * dw + x) * 4;
                    if (weight <= 0 || a <= 0) continue;   // fully see-through
                    dst[o] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
                    dst[o + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
                    dst[o + 2] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
                    dst[o + 3] = (byte)Math.Clamp(Math.Round(a / weight * 255), 0, 255);
                }
            return dst;

            // 'a' collects opacity x coverage, which is also the weight of each colour: so a see-through pixel adds
            // almost nothing to the colour, and the colour is divided by 'a' (not by the plain coverage).
            static void Add(byte[] s, int i, double w, ref double b, ref double g, ref double r, ref double a)
            {
                double aw = s[i + 3] / 255.0 * w;
                b += s[i] * aw;
                g += s[i + 1] * aw;
                r += s[i + 2] * aw;
                a += aw;
            }
        }

        // =================================================================
        //  The .ico file
        // =================================================================

        /// <summary>
        /// A Windows icon holding one PNG per size (supported since Windows Vista). Sizes up to 256; the pictures are
        /// whatever PNG bytes the caller made. Smallest first.
        /// </summary>
        public static byte[] BuildIco(IReadOnlyList<(int Size, byte[] Png)> images)
        {
            if (images.Count == 0) throw new ArgumentException("An icon needs at least one picture.", nameof(images));
            var sorted = images.OrderBy(i => i.Size).ToList();
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
            w.Write((ushort)0);                       // reserved
            w.Write((ushort)1);                       // type: icon
            w.Write((ushort)sorted.Count);
            int offset = 6 + 16 * sorted.Count;
            foreach (var (size, png) in sorted)
            {
                w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0);                     // palette colours
                w.Write((byte)0);                     // reserved
                w.Write((ushort)1);                   // colour planes
                w.Write((ushort)32);                  // bits per pixel
                w.Write(png.Length);
                w.Write(offset);
                offset += png.Length;
            }
            foreach (var (_, png) in sorted) w.Write(png);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>A square BGRA picture as PNG bytes.</summary>
        public static byte[] ToPng(byte[] bgraSquare, int size)
        {
            var rgba = new byte[bgraSquare.Length];
            for (int i = 0; i + 3 < bgraSquare.Length; i += 4)
            {
                rgba[i] = bgraSquare[i + 2];
                rgba[i + 1] = bgraSquare[i + 1];
                rgba[i + 2] = bgraSquare[i];
                rgba[i + 3] = bgraSquare[i + 3];
            }
            return PngWriter.Encode(size, size, rgba);
        }

        /// <summary>The .ico for a picture: one PNG per chosen size.</summary>
        public static byte[] BuildIcoFrom(byte[] bgra, int width, int height, IEnumerable<int> sizes, IconFit fit) =>
            BuildIco(sizes.Distinct().OrderBy(s => s).Select(s => (s, ToPng(ToSquare(bgra, width, height, s, fit), s))).ToList());

        /// <summary>Every file of a website favicon set (name and bytes), including favicon.ico, the PNGs, the manifest and the HTML lines.</summary>
        public static List<(string Name, byte[] Data)> BuildFaviconSet(byte[] bgra, int width, int height, IconFit fit, string siteName)
        {
            var files = new List<(string, byte[])>
            {
                ("favicon.ico", BuildIcoFrom(bgra, width, height, FaviconIcoSizes, fit)),
            };
            foreach (var (name, size) in FaviconPngs)
                files.Add((name, ToPng(ToSquare(bgra, width, height, size, fit), size)));
            files.Add(("site.webmanifest", Encoding.UTF8.GetBytes(WebManifest(siteName))));
            files.Add(("favicon-html.txt", Encoding.UTF8.GetBytes(FaviconHtml())));
            return files;
        }

        /// <summary>The lines to paste into a web page's head for the favicon set.</summary>
        public static string FaviconHtml() =>
            "<link rel=\"icon\" href=\"/favicon.ico\" sizes=\"any\">\r\n" +
            "<link rel=\"icon\" type=\"image/png\" sizes=\"32x32\" href=\"/favicon-32x32.png\">\r\n" +
            "<link rel=\"icon\" type=\"image/png\" sizes=\"16x16\" href=\"/favicon-16x16.png\">\r\n" +
            "<link rel=\"apple-touch-icon\" href=\"/apple-touch-icon.png\">\r\n" +
            "<link rel=\"manifest\" href=\"/site.webmanifest\">\r\n";

        public static string WebManifest(string name)
        {
            string safe = System.Text.Json.JsonSerializer.Serialize(string.IsNullOrWhiteSpace(name) ? "My site" : name.Trim());
            return "{\r\n" +
                   $"  \"name\": {safe},\r\n" +
                   $"  \"short_name\": {safe},\r\n" +
                   "  \"icons\": [\r\n" +
                   "    { \"src\": \"/android-chrome-192x192.png\", \"sizes\": \"192x192\", \"type\": \"image/png\" },\r\n" +
                   "    { \"src\": \"/android-chrome-512x512.png\", \"sizes\": \"512x512\", \"type\": \"image/png\" }\r\n" +
                   "  ],\r\n" +
                   "  \"theme_color\": \"#ffffff\",\r\n" +
                   "  \"background_color\": \"#ffffff\",\r\n" +
                   "  \"display\": \"standalone\"\r\n" +
                   "}\r\n";
        }
    }
}
