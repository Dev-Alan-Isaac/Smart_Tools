using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Text Diff - the logic. The comparison is File Differences' (<see cref="FileDifferencesManager.DiffLines"/>,
    /// rows, word highlights, unified diff), run on text that is pasted or typed instead of read from disk.
    /// Keep those signatures stable: both tools depend on them.
    /// </summary>
    public static class TextDiffManager
    {
        /// <summary>A text file bigger than this isn't loaded into a box (it would freeze typing); File Differences handles big files.</summary>
        public const long MaxFileBytes = 8 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "text_diff.json");

        /// <summary>The lines of a text, without line breaks. A final line break doesn't add an empty line.</summary>
        public static string[] Lines(string? text) => FileDifferencesManager.SplitLines(text ?? "").Lines;

        public static FileDiffOptions OptionsFor(TextDiffSettings settings) =>
            new() { IgnoreWhitespace = settings.IgnoreWhitespace, IgnoreCase = settings.IgnoreCase };

        /// <summary>Compares two texts. Runs on the calling thread: call it from Task.Run for big texts.</summary>
        public static (FileDiffResult Result, TextDiffVerdict Verdict) Compare(string? original, string? changed, TextDiffSettings settings, CancellationToken token = default)
        {
            string[] a = Lines(original), b = Lines(changed);
            var result = FileDifferencesManager.DiffLines(a, b, OptionsFor(settings), token);

            if (a.Length == 0 && b.Length == 0) return (result, TextDiffVerdict.Nothing);
            if (result.LinesAdded + result.LinesRemoved > 0) return (result, TextDiffVerdict.Different);

            // No line counts as different: the same text, or only what the ignore options skip.
            bool exact = a.AsSpan().SequenceEqual(b);
            return (result, exact ? TextDiffVerdict.Identical : TextDiffVerdict.SameWithOptions);
        }

        /// <summary>The differences as a unified diff, the text <c>git diff</c> prints.</summary>
        public static string BuildPatch(FileDiffResult result, string nameA, string nameB) =>
            FileDifferencesManager.BuildUnifiedDiff(result, nameA, nameB);

        /// <summary>Reads a text file for one of the boxes. Throws <see cref="InvalidDataException"/> when it isn't text or is too big.</summary>
        public static string ReadTextFile(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) throw new InvalidDataException("TooBig");

            byte[] bytes = File.ReadAllBytes(path);
            // A zero byte means the file isn't text (UTF-16 files have them too, but they start with a BOM).
            bool utf16 = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
            if (!utf16 && Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0) throw new InvalidDataException("Binary");

            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        public static TextDiffSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new TextDiffSettings();
                var s = JsonSerializer.Deserialize<TextDiffSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new TextDiffSettings();
                if (!Enum.IsDefined(s.ViewMode)) s.ViewMode = FileDiffViewMode.SideBySide;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new TextDiffSettings();
            }
        }

        public static void SaveSettings(TextDiffSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string temp = SettingsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, SettingsPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Remembering the setup is a convenience; losing it is not worth an error.
            }
        }
    }
}
