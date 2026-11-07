using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The small "remember how the tool was set up" file every newer tool keeps: %AppData%\Smart Tools\&lt;name&gt;.json.
    /// A missing or broken file gives the defaults, and a save goes through a temp file so it is never half-written.
    /// Never put pictures, secrets or paths in here. (Only <see cref="TryCopy"/> touches WPF.)
    /// </summary>
    public static class JsonSettings
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public static string PathFor(string name) => Path.Combine(AppPaths.DataRoot, name + ".json");

        public static T Load<T>(string name, string? path = null) where T : new()
        {
            try
            {
                string file = path ?? PathFor(name);
                if (!File.Exists(file)) return new T();
                return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options) ?? new T();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // A file that cannot be parsed would be overwritten by the next save: keep a copy first.
                if (ex is JsonException or NotSupportedException) KeepBadCopy(path ?? PathFor(name));
                return new T();
            }
        }

        /// <summary>Copies a file that could not be parsed to "name.bad" before defaults (and the next save) replace it.</summary>
        public static void KeepBadCopy(string file)
        {
            try
            {
                if (File.Exists(file)) File.Copy(file, file + ".bad", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info("Could not keep a copy of the unreadable file " + file + ": " + ex.Message);
            }
        }

        public static void Save<T>(string name, T settings, string? path = null)
        {
            string file = path ?? PathFor(name);
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>Copies to the clipboard; false when another program has it open.</summary>
        public static bool TryCopy(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }
    }
}
