using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Database Migrator - remembered options (%AppData%\Smart Tools\db-migrate.json): servers, database names, sign-in
    /// mode and user name. Never passwords. Written to a temp file then moved; a missing or damaged file gives defaults.
    /// </summary>
    public sealed partial class DbMigrateManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string SettingsPath =>
            Path.Combine(AppPaths.DataRoot, "db-migrate.json");

        public static DbMigrateSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonSerializer.Deserialize<DbMigrateSettings>(File.ReadAllText(SettingsPath)) ?? new DbMigrateSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                if (ex is JsonException or NotSupportedException) JsonSettings.KeepBadCopy(SettingsPath);   // damaged file: keep it as .bad, start from defaults
            }
            return new DbMigrateSettings();
        }

        public static void SaveSettings(DbMigrateSettings settings)
        {
            try
            {
                var path = SettingsPath;
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are a convenience; never fail the tool over them */ }
        }
    }
}
