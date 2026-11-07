using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Screen Recorder - remembered options (%AppData%\Smart Tools\screen-recorder.json). Written to a temp file then moved;
    /// a missing or damaged file gives defaults. Never a path, window title, or microphone name.
    /// </summary>
    public sealed partial class ScreenRecorderManager
    {
        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public static string SettingsPath =>
            Path.Combine(AppPaths.DataRoot, "screen-recorder.json");

        public static ScreenRecorderSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonSerializer.Deserialize<ScreenRecorderSettings>(File.ReadAllText(SettingsPath), SettingsJson) ?? new ScreenRecorderSettings();
                    if (Array.IndexOf(FpsChoices, s.Fps) < 0) s.Fps = 30;   // an edited file can't pick a frame rate the tool doesn't offer
                    return s;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A damaged file: start from defaults.
            }
            return new ScreenRecorderSettings();
        }

        public static void SaveSettings(ScreenRecorderSettings settings)
        {
            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never fail the tool over them.
            }
        }
    }
}
