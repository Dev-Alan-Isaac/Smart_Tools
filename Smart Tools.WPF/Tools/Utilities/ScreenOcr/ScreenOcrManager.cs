using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// OCR Text Extractor (Screen to Text) backend, split in two:
    /// <list type="bullet">
    /// <item>ScreenOcrManager.cs (this file) - pure logic: cropping, the
    /// scale an image is read at, putting lines together, settings. No
    /// Windows Runtime, no WPF.</item>
    /// <item>ScreenOcrManager.Windows.cs - capturing the screen and the
    /// Windows OCR engine (Windows.Media.Ocr, built into Windows 10/11: no
    /// download, works offline, languages come from Windows' language packs).</item>
    /// </list>
    /// </summary>
    public static partial class ScreenOcrManager
    {
        /// <summary>Smaller than this (either side, in pixels) isn't worth reading - a stray click, not a selection.</summary>
        public const int MinSelectionPixels = 6;

        // =================================================================
        //  Pixels
        // =================================================================

        /// <summary>A rectangle out of a bigger image, clamped to its edges. Null when nothing is left.</summary>
        public static ScreenOcrImage? Crop(ScreenOcrImage source, int x, int y, int width, int height)
        {
            int left = Math.Clamp(x, 0, source.Width), top = Math.Clamp(y, 0, source.Height);
            int right = Math.Clamp(x + width, 0, source.Width), bottom = Math.Clamp(y + height, 0, source.Height);
            int w = right - left, h = bottom - top;
            if (w <= 0 || h <= 0) return null;

            var pixels = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(source.Pixels, ((top + row) * source.Width + left) * 4, pixels, row * w * 4, w * 4);
            return new ScreenOcrImage(pixels, w, h);
        }

        /// <summary>
        /// Puts see-through pixels on white (OCR reads transparency as black).
        /// When every pixel has alpha 0 the alpha is meaningless - most programs
        /// put screenshots on the clipboard that way - so the colours are kept as they are.
        /// </summary>
        public static void FlattenOnWhite(ScreenOcrImage image)
        {
            byte[] p = image.Pixels;
            bool anyAlpha = false;
            for (int i = 3; i < p.Length; i += 4)
                if (p[i] != 0) { anyAlpha = true; break; }

            for (int i = 0; i < p.Length; i += 4)
            {
                int a = anyAlpha ? p[i + 3] : 255;
                if (a < 255)
                {
                    int white = 255 - a;
                    p[i] = (byte)((p[i] * a + 255 * white) / 255);
                    p[i + 1] = (byte)((p[i + 1] * a + 255 * white) / 255);
                    p[i + 2] = (byte)((p[i + 2] * a + 255 * white) / 255);
                }
                p[i + 3] = 255;
            }
        }

        /// <summary>
        /// The scale to read an image at. Windows OCR misses small text (a
        /// single line captured from the screen is often under 20 px tall), so
        /// small captures are enlarged 2-3x; anything over the engine's limit
        /// is shrunk to fit. <paramref name="larger"/> is the retry for blurry pictures: one step bigger.
        /// </summary>
        public static double ReadScale(int width, int height, int maxDimension, bool larger = false)
        {
            if (width <= 0 || height <= 0) return 1;
            int shorter = Math.Min(width, height);
            double scale = larger
                ? (shorter < 120 ? 4 : shorter < 400 ? 3 : shorter < 900 ? 1.5 : 1)
                : (shorter < 120 ? 3 : shorter < 400 ? 2 : 1);
            int longer = Math.Max(width, height);
            if (longer * scale > maxDimension) scale = (double)maxDimension / longer;
            return scale;
        }

        // =================================================================
        //  Text
        // =================================================================

        /// <summary>The lines as the text box shows them.</summary>
        public static string FormatText(IReadOnlyList<string> lines, ScreenOcrLayout layout)
        {
            if (layout == ScreenOcrLayout.KeepLines) return string.Join(Environment.NewLine, lines);

            var sb = new StringBuilder();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (sb.Length == 0)
                {
                    sb.Append(line);
                    continue;
                }

                char last = sb[^1], next = line[0];
                if (last == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && char.IsLower(next))
                    sb.Length--;                  // "infor-" + "mation" -> "information"
                else if (!(IsCjk(last) && IsCjk(next)))
                    sb.Append(' ');               // Chinese / Japanese lines join without a space
                sb.Append(line);
            }
            return sb.ToString();
        }

        private static bool IsCjk(char c) =>
            c is >= '぀' and <= 'ヿ'      // Hiragana, Katakana
              or >= '㐀' and <= '䶿'      // CJK extension A
              or >= '一' and <= '鿿'      // CJK unified
              or >= '가' and <= '힯'      // Hangul
              or >= '＀' and <= '￯';     // full-width forms

        /// <summary>"1 word · 3 lines" style counts.</summary>
        public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

        /// <summary>A dated file name for saving ("screen-text 2026-10-09 15-46-51.txt").</summary>
        public static string SaveFileName(DateTime now) => $"screen-text {now:yyyy-MM-dd HH-mm-ss}.txt";

        // =================================================================
        //  Settings (%AppData%\Smart Tools\ocr_text_extractor.json)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "ocr_text_extractor.json");

        public static ScreenOcrSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ScreenOcrSettings();
                return Sanitize(JsonSerializer.Deserialize<ScreenOcrSettings>(File.ReadAllText(file), SettingsJson) ?? new ScreenOcrSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new ScreenOcrSettings();
            }
        }

        public static ScreenOcrSettings Sanitize(ScreenOcrSettings s)
        {
            s.Language ??= "";
            if (string.IsNullOrWhiteSpace(s.TesseractLanguage)) s.TesseractLanguage = "eng";
            if (!Enum.IsDefined(s.Engine)) s.Engine = ScreenOcrEngineKind.Windows;
            if (!Enum.IsDefined(s.Layout)) s.Layout = ScreenOcrLayout.KeepLines;
            return s;
        }

        /// <summary>Never throws - a settings file that can't be written isn't worth an error.</summary>
        public static void SaveSettings(ScreenOcrSettings s, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(Sanitize(s), SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
