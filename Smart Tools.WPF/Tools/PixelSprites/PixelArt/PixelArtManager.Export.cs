using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    // Colours, palettes, the PNG writer and the settings. Still no WPF types.
    public sealed partial class PixelArtManager
    {
        /// <summary>The longest side an exported picture may have.</summary>
        public const int MaxExportSide = 8192;

        // =================================================================
        //  Colours
        // =================================================================

        /// <summary>Any fully transparent colour is stored as 0, so "is empty" is one comparison.</summary>
        public static uint Normalize(uint argb) => (argb >> 24) == 0 ? 0u : argb;

        public static uint Pack(byte a, byte r, byte g, byte b) => Normalize((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b);

        /// <summary>Reads #RGB, #RRGGBB or #AARRGGBB (the # is optional). Returns null when it is not a colour.</summary>
        public static uint? TryParseColor(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim().TrimStart('#');
            if (t.Length == 3) t = string.Concat(t.Select(c => new string(c, 2)));
            if (t.Length is not (6 or 8) || !t.All(Uri.IsHexDigit)) return null;
            uint value = uint.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (t.Length == 6) value |= 0xFF000000;
            return Normalize(value);
        }

        /// <summary>#RRGGBB for opaque colours, #AARRGGBB when see-through.</summary>
        public static string ToHex(uint argb)
        {
            argb = Normalize(argb);
            return (argb >> 24) == 255
                ? "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture)
                : "#" + argb.ToString("X8", CultureInfo.InvariantCulture);
        }

        // =================================================================
        //  Palettes
        // =================================================================

        private static uint[] Hexes(params uint[] rgb) => rgb.Select(c => c | 0xFF000000).ToArray();

        public static IReadOnlyList<PixelArtPalette> Palettes { get; } = new[]
        {
            new PixelArtPalette("PICO-8", Hexes(0x000000, 0x1D2B53, 0x7E2553, 0x008751, 0xAB5236, 0x5F574F, 0xC2C3C7, 0xFFF1E8,
                                                0xFF004D, 0xFFA300, 0xFFEC27, 0x00E436, 0x29ADFF, 0x83769C, 0xFF77A8, 0xFFCCAA)),
            new PixelArtPalette("DawnBringer 16", Hexes(0x140C1C, 0x442434, 0x30346D, 0x4E4A4E, 0x854C30, 0x346524, 0xD04648, 0x757161,
                                                        0x597DCE, 0xD27D2C, 0x8595A1, 0x6DAA2C, 0xD2AA99, 0x6DC2CA, 0xDAD45E, 0xDEEED6)),
            new PixelArtPalette("Game Boy", Hexes(0x0F380F, 0x306230, 0x8BAC0F, 0x9BBC0F)),
            new PixelArtPalette("Grayscale", Enumerable.Range(0, 8).Select(i => Pack(255, (byte)(i * 255 / 7), (byte)(i * 255 / 7), (byte)(i * 255 / 7))).ToArray()),
        };

        public static PixelArtPalette PaletteByName(string? name) =>
            Palettes.FirstOrDefault(p => p.Name == name) ?? Palettes[0];

        public const int MaxCustomColors = 48;

        /// <summary>Adds a colour to the front of the list (once), keeping at most <see cref="MaxCustomColors"/>.</summary>
        public static List<uint> AddCustomColor(IEnumerable<uint> current, uint color)
        {
            color = Normalize(color);
            if (color == 0) return current.ToList();
            var list = current.Where(c => c != color).ToList();
            list.Insert(0, color);
            if (list.Count > MaxCustomColors) list.RemoveRange(MaxCustomColors, list.Count - MaxCustomColors);
            return list;
        }

        // =================================================================
        //  Pixels <-> RGBA bytes
        // =================================================================

        /// <summary>Straight-alpha RGBA bytes (what PNG stores), row by row.</summary>
        public static byte[] ToRgba(uint[] pixels)
        {
            var bytes = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i];
                bytes[i * 4] = (byte)(p >> 16);
                bytes[i * 4 + 1] = (byte)(p >> 8);
                bytes[i * 4 + 2] = (byte)p;
                bytes[i * 4 + 3] = (byte)(p >> 24);
            }
            return bytes;
        }

        public static uint[] FromRgba(int width, int height, byte[] rgba)
        {
            var pixels = new uint[width * height];
            for (int i = 0; i < pixels.Length && i * 4 + 3 < rgba.Length; i++)
                pixels[i] = Pack(rgba[i * 4 + 3], rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            return pixels;
        }

        // =================================================================
        //  Export
        // =================================================================

        /// <summary>
        /// A PNG of the current frame, or of every frame laid out in rows (a sprite sheet), enlarged by a
        /// whole number with nearest neighbour so the pixels stay sharp. Fails with a code instead of making a giant file.
        /// </summary>
        public static PixelArtExportResult Export(PixelArtDocument doc, int currentFrame, PixelArtExportOptions options)
        {
            int scale = Math.Clamp(options.Scale, 1, 64);
            int count = options.Sheet ? doc.Frames.Count : 1;
            int columns = options.Sheet ? Math.Clamp(options.Columns, 1, count) : 1;
            int rows = (count + columns - 1) / columns;
            long sheetW = (long)doc.Width * columns * scale, sheetH = (long)doc.Height * rows * scale;
            if (sheetW > MaxExportSide || sheetH > MaxExportSide) return new PixelArtExportResult { ProblemCode = "ExportTooBig" };

            int w = (int)sheetW, h = (int)sheetH;
            var rgba = new byte[w * h * 4];
            for (int f = 0; f < count; f++)
            {
                var frame = doc.Frames[options.Sheet ? f : Math.Clamp(currentFrame, 0, doc.Frames.Count - 1)];
                int ox = f % columns * doc.Width * scale, oy = f / columns * doc.Height * scale;
                for (int y = 0; y < doc.Height; y++)
                    for (int x = 0; x < doc.Width; x++)
                    {
                        uint p = frame[y * doc.Width + x];
                        if (p == 0) continue;
                        byte r = (byte)(p >> 16), g = (byte)(p >> 8), b = (byte)p, a = (byte)(p >> 24);
                        for (int sy = 0; sy < scale; sy++)
                        {
                            int row = ((oy + y * scale + sy) * w + ox + x * scale) * 4;
                            for (int sx = 0; sx < scale; sx++, row += 4)
                            {
                                rgba[row] = r; rgba[row + 1] = g; rgba[row + 2] = b; rgba[row + 3] = a;
                            }
                        }
                    }
            }
            return new PixelArtExportResult { Width = w, Height = h, Png = EncodePng(w, h, rgba) };
        }

        /// <summary>Straight-alpha RGBA bytes to a PNG file's bytes (see <see cref="PngWriter"/>).</summary>
        public static byte[] EncodePng(int width, int height, byte[] rgba) => PngWriter.Encode(width, height, rgba);

        /// <summary>Writes through a hidden temporary name and moves it into place; returns null on success, otherwise the reason.</summary>
        public static string? SaveBytes(string path, byte[] data) => SafeFile.WriteAllBytes(path, data);

        // =================================================================
        //  Settings (%AppData%\Smart Tools\pixel_art.json - the setup only, never the artwork or a path)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "pixel_art.json");

        public static PixelArtSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new PixelArtSettings();
                var s = JsonSerializer.Deserialize<PixelArtSettings>(File.ReadAllText(file), SettingsJson) ?? new PixelArtSettings();
                if (!Enum.IsDefined(s.Tool)) s.Tool = PixelArtTool.Pencil;
                if (!Enum.IsDefined(s.Symmetry)) s.Symmetry = PixelArtSymmetry.None;
                s.BrushSize = Math.Clamp(s.BrushSize, 1, 4);
                s.ExportScale = Math.Clamp(s.ExportScale, 1, 64);
                s.Fps = Math.Clamp(s.Fps, 1, 24);
                s.NewWidth = Math.Clamp(s.NewWidth, 1, PixelArtDocument.MaxSide);
                s.NewHeight = Math.Clamp(s.NewHeight, 1, PixelArtDocument.MaxSide);
                s.CustomColors = s.CustomColors?.Where(c => TryParseColor(c) != null).Take(MaxCustomColors).ToList();
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new PixelArtSettings();
            }
        }

        public static void SaveSettings(PixelArtSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
