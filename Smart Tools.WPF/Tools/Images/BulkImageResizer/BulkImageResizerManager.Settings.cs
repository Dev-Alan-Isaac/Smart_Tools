using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Bulk Image Resizer - remembered options (%AppData%\Smart Tools\bulk-image-resizer.json). Written to a temp file then
    /// moved; a missing or damaged file, or a value out of range, gives defaults.
    /// </summary>
    public sealed partial class BulkImageResizerManager
    {
        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public static string SettingsPath =>
            Path.Combine(AppPaths.DataRoot, "bulk-image-resizer.json");

        public static BulkResizeSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return Sanitize(JsonSerializer.Deserialize<BulkResizeSettings>(File.ReadAllText(SettingsPath), SettingsJson) ?? new BulkResizeSettings());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A damaged file: start from defaults.
            }
            return new BulkResizeSettings();
        }

        public static void SaveSettings(BulkResizeSettings settings)
        {
            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Sanitize(settings), SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never fail the tool over them.
            }
        }

        /// <summary>Keeps every number in the range the tool accepts, so an edited file can't break a run.</summary>
        public static BulkResizeSettings Sanitize(BulkResizeSettings s)
        {
            s.Percent = Math.Clamp(s.Percent, 1, 400);
            s.Width = Math.Clamp(s.Width, 0, 30000);
            s.Height = Math.Clamp(s.Height, 0, 30000);
            s.Quality = Math.Clamp(s.Quality, 1, 100);
            s.TargetKb = Math.Clamp(s.TargetKb, 1, 100000);
            s.Suffix = string.Concat((s.Suffix ?? "").Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0));
            return s;
        }
    }
}
