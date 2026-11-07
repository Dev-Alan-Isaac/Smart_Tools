using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    // Files and settings for the Base64 & URL Encoder / Decoder. Still no WPF types.
    public static partial class EncodeToolManager
    {
        /// <summary>Files bigger than this are refused: Base64 grows them by a third and the result has to fit in memory and on screen.</summary>
        public const long MaxFileBytes = 30L * 1024 * 1024;

        /// <summary>Reads a whole file (other programs may keep it open). Returns a problem code instead of throwing.</summary>
        public static byte[]? ReadFile(string path, out string problem)
        {
            problem = "";
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { problem = "FileMissing"; return null; }
                if (info.Length > MaxFileBytes) { problem = "FileTooBig"; return null; }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                return data;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                problem = "FileUnreadable";
                return null;
            }
        }

        /// <summary>Reads a text file for the input box: byte-order mark first, UTF-8 otherwise. Binary files are refused.</summary>
        public static string? ReadTextFile(string path, out string problem)
        {
            var data = ReadFile(path, out problem);
            if (data == null) return null;
            if (data.Length > MaxInputChars) { problem = "TooLarge"; return null; }
            if (LooksBinary(data, EncodeToolTextEncoding.Utf8) && !HasBom(data)) { problem = "FileBinary"; return null; }
            using var reader = new StreamReader(new MemoryStream(data), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static bool HasBom(byte[] d) =>
            d.Length >= 2 && (d[0] == 0xFF && d[1] == 0xFE || d[0] == 0xFE && d[1] == 0xFF)
            || d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF;

        /// <summary>
        /// Writes through a hidden temporary name and moves it into place, so a half-written file never
        /// looks like a result. The path comes from a Save dialog, which already asked about replacing.
        /// Returns null on success, otherwise the reason.
        /// </summary>
        public static string? SaveBytes(string path, byte[] data)
        {
            string? folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return "No folder";
            string temp = Path.Combine(folder, "." + Path.GetFileName(path) + ".creating-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return ex.Message;
            }
        }

        /// <summary>UTF-8 without a byte-order mark, which is what other tools expect for pasted text.</summary>
        public static string? SaveText(string path, string text) => SaveBytes(path, new UTF8Encoding(false).GetBytes(text));

        /// <summary>A starting name for the Save dialog: "decoded" plus the extension the bytes suggest.</summary>
        public static string SuggestFileName(EncodeToolResult result, bool asBytes)
        {
            if (!asBytes) return "output.txt";
            string ext = ExtensionForKind(result.DetectedKind);
            if (ext.Length == 0) ext = result.IsText ? ".txt" : ".bin";
            return "decoded" + ext;
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\encode_tool.json - the setup only, never what was typed or a path)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "encode_tool.json");

        public static EncodeToolSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new EncodeToolSettings();
                var s = JsonSerializer.Deserialize<EncodeToolSettings>(File.ReadAllText(file), SettingsJson) ?? new EncodeToolSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = EncodeToolMode.Base64;
                if (!Enum.IsDefined(s.Direction)) s.Direction = EncodeToolDirection.Encode;
                if (!Enum.IsDefined(s.TextEncoding)) s.TextEncoding = EncodeToolTextEncoding.Utf8;
                if (s.UrlScope is { } scope && !Enum.IsDefined(scope)) s.UrlScope = null;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new EncodeToolSettings();
            }
        }

        public static void SaveSettings(EncodeToolSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort: AtomicFile already removed its temp file
            }
        }
    }
}
