using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Screen Color Picker backend - the portable half (no WPF, no GDI): reading a colour
    /// out of a captured picture, averaging, formatting and settings.
    /// The screen capture itself is in <c>ScreenColorPickerManager.Windows.cs</c>.
    /// </summary>
    public static partial class ScreenColorPickerManager
    {
        public const int MaxSessionPicks = 24;

        // =================================================================
        //  Reading pixels
        // =================================================================

        /// <summary>The colour of one pixel; null when (x, y) is outside the picture.</summary>
        public static RgbColor? PixelAt(ScreenColorImage image, int x, int y)
        {
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
            int i = y * image.Stride + x * 4;
            return new RgbColor(image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i]);   // Bgra
        }

        /// <summary>
        /// The colour at (x, y), averaged over a square when asked. Pixels outside the picture
        /// are left out of the average (a pick at the screen edge still works).
        /// </summary>
        public static RgbColor? Sample(ScreenColorImage image, int x, int y, ScreenColorSample mode)
        {
            int radius = mode switch { ScreenColorSample.Average3 => 1, ScreenColorSample.Average5 => 2, _ => 0 };
            if (radius == 0) return PixelAt(image, x, y);
            if (PixelAt(image, x, y) == null) return null;

            long r = 0, g = 0, b = 0;
            int n = 0;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (PixelAt(image, x + dx, y + dy) is not RgbColor c) continue;
                    r += c.R;
                    g += c.G;
                    b += c.B;
                    n++;
                }
            return new RgbColor((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n));
        }

        // =================================================================
        //  Writing a colour as text
        // =================================================================

        public static string Format(RgbColor c, ScreenColorFormat format) => format switch
        {
            ScreenColorFormat.Rgb => ColorManager.ToRgbTriplet(c),
            ScreenColorFormat.Hsl => ColorManager.ToHslTriplet(c),
            ScreenColorFormat.CssRgb => ColorManager.ToCssRgb(c),
            _ => c.Hex,
        };

        /// <summary>"x, y" for the picked pixel.</summary>
        public static string Position(ScreenPick p) => string.Create(CultureInfo.InvariantCulture, $"{p.X}, {p.Y}");

        /// <summary>
        /// Adds a pick to the session list: newest first, the same colour moves to the front
        /// instead of repeating, and the list never grows past <see cref="MaxSessionPicks"/>.
        /// </summary>
        public static void AddPick(List<ScreenPick> list, ScreenPick pick)
        {
            list.RemoveAll(p => p.Color == pick.Color);
            list.Insert(0, pick);
            if (list.Count > MaxSessionPicks) list.RemoveRange(MaxSessionPicks, list.Count - MaxSessionPicks);
        }

        // =================================================================
        //  Settings (how the tool is set up - never colours or positions)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "screen_color_picker.json");

        public static ScreenColorPickerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ScreenColorPickerSettings();
                var s = JsonSerializer.Deserialize<ScreenColorPickerSettings>(File.ReadAllText(file), JsonOptions) ?? new ScreenColorPickerSettings();
                if (!Enum.IsDefined(s.CopyFormat)) s.CopyFormat = ScreenColorFormat.Hex;
                if (!Enum.IsDefined(s.Sample)) s.Sample = ScreenColorSample.Pixel;
                return s;
            }
            catch (Exception)
            {
                return new ScreenColorPickerSettings();
            }
        }

        public static void SaveSettings(ScreenColorPickerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
