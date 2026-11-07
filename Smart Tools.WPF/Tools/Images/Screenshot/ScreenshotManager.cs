using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Screenshot: the pure parts - delays, file names, picture formats, arrow maths and settings.
    /// Taking the picture (GDI, the window under the timer) is in ScreenshotManager.Windows.cs; the
    /// overlay and the editor are the view's. Nothing here touches WPF controls.
    /// </summary>
    public static partial class ScreenshotManager
    {
        /// <summary>Seconds to wait before taking the picture, so you can open a menu first.</summary>
        public static readonly int[] DelayChoices = { 0, 3, 5, 10 };

        public const int MinPenThickness = 2;
        public const int MaxPenThickness = 16;

        /// <summary>"Screenshot 2026-10-31 14-03-22.png"</summary>
        public static string SuggestedName(DateTime now) => $"Screenshot {now:yyyy-MM-dd HH-mm-ss}";

        /// <summary>The Save dialog filter, PNG first (lossless is the right default for screens).</summary>
        public const string SaveFilter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp";

        /// <summary>Which format a file name asks for. Anything unknown is PNG.</summary>
        public static string FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "jpg",
            ".bmp" => "bmp",
            _ => "png",
        };

        /// <summary>The two back-pointing ends of an arrow head, for a line from <c>(x1, y1)</c> to <c>(x2, y2)</c>.</summary>
        public static ((double X, double Y) Left, (double X, double Y) Right) ArrowHead(double x1, double y1, double x2, double y2, double size)
        {
            double angle = Math.Atan2(y2 - y1, x2 - x1);
            const double spread = Math.PI / 7;   // about 26 degrees either side of the line
            return ((x2 - size * Math.Cos(angle - spread), y2 - size * Math.Sin(angle - spread)),
                    (x2 - size * Math.Cos(angle + spread), y2 - size * Math.Sin(angle + spread)));
        }

        /// <summary>Head length for a pen thickness: big enough to see on a thin line.</summary>
        public static double ArrowHeadSize(double thickness) => 10 + thickness * 2.5;

        /// <summary>Corners of a drag in any direction as x, y, width, height.</summary>
        public static (double X, double Y, double Width, double Height) Normalize(double x1, double y1, double x2, double y2) =>
            (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

        /// <summary>How many pixels each block of a pixelated area covers, from the area's size, so a small area still hides its detail.</summary>
        public static int PixelBlock(double width, double height) =>
            Math.Clamp((int)(Math.Min(width, height) / 8), 6, 24);

        /// <summary>The clamped part of a rectangle that lies inside a picture, or null when none of it does.</summary>
        public static (int X, int Y, int Width, int Height)? ClampToImage(int x, int y, int width, int height, int imageWidth, int imageHeight)
        {
            int left = Math.Max(0, x), top = Math.Max(0, y);
            int right = Math.Min(imageWidth, x + width), bottom = Math.Min(imageHeight, y + height);
            return right - left < 1 || bottom - top < 1 ? null : (left, top, right - left, bottom - top);
        }

        // ---------------- Settings ----------------

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "screenshot.json");

        public static ScreenshotSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<ScreenshotSettings>(File.ReadAllText(file), JsonOptions) : null;
                return Sanitize(s ?? new ScreenshotSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new ScreenshotSettings();
            }
        }

        public static ScreenshotSettings Sanitize(ScreenshotSettings s)
        {
            if (!Enum.IsDefined(s.Mode)) s.Mode = ScreenshotMode.Region;
            if (!DelayChoices.Contains(s.DelaySeconds)) s.DelaySeconds = 0;
            s.Thickness = Math.Clamp(s.Thickness, MinPenThickness, MaxPenThickness);
            if (s.ColorIndex < 0) s.ColorIndex = 0;
            return s;
        }

        public static void SaveSettings(ScreenshotSettings settings, string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(Sanitize(settings), JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A setting that doesn't stick is not worth interrupting a capture for.
            }
        }
    }
}
