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
            catch { /* damaged file: start from defaults */ }
            return new DbMigrateSettings();
        }

        public static void SaveSettings(DbMigrateSettings settings)
        {
            try
            {
                var path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* settings are a convenience; never fail the tool over them */ }
        }
    }
}
