using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Translate's Files mode: reads a text, Markdown or subtitle file, translates only the lines that are words
    /// (not subtitle numbers, time codes or code blocks), and saves the result NEXT to the original with the target
    /// language added to the name (film.srt becomes film.es.srt). The original is never changed and an existing file
    /// is never overwritten.
    /// </summary>
    public sealed partial class TranslateManager
    {
        /// <summary>Plain text and subtitle formats: translating them can't damage a structure we don't understand.</summary>
        public static readonly string[] FileExtensions = { ".txt", ".md", ".srt", ".vtt" };

        /// <summary>Bigger than this is refused (the model works a sentence at a time; 8 MB is already ~100,000 sentences).</summary>
        public const long MaxFileBytes = 8L * 1024 * 1024;

        private static readonly Regex OnlyDigits = new(@"^\d+$", RegexOptions.Compiled);

        public static bool IsSupportedFile(string path) =>
            FileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Translates one file through <paramref name="route"/> (one pair, or two when it goes through English).
        /// <paramref name="progress"/> is 0..1 for this file. Cancelling saves nothing.
        /// </summary>
        public async Task<TranslateFileResult> TranslateFileAsync(string path, IReadOnlyList<TranslateLanguagePair> route, IProgress<double>? progress, CancellationToken token)
        {
            if (route.Count == 0) return new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileError");

            string text;
            Encoding encoding;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileUnreadable");
                if (info.Length > MaxFileBytes) return new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileTooBig");
                (text, encoding) = Decode(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                return new TranslateFileResult(null, true, 0, 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileUnreadable");
            }

            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool[] words = WordLines(lines, Path.GetExtension(path).ToLowerInvariant());
            int[] picked = Enumerable.Range(0, lines.Length).Where(i => words[i]).ToArray();
            if (picked.Length == 0) return new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileNothing");

            // Only the word lines go to the model, one per line; it hands back exactly one line for each.
            string current = string.Join("\n", picked.Select(i => lines[i].Trim()));
            int failed = 0, total = 0;
            for (int step = 0; step < route.Count; step++)
            {
                int done = step, steps = route.Count;
                var stepProgress = new Progress<double>(f => progress?.Report((done + f) / steps));
                var result = await TranslateAsync(current, route[step], stepProgress, token).ConfigureAwait(false);
                if (result.Canceled) return new TranslateFileResult(null, true, failed, total);
                current = result.Text;
                failed += result.FailedParts;
                total += result.TotalParts;
            }

            string[] translated = current.Replace("\r\n", "\n").Split('\n');
            if (translated.Length != picked.Length) return new TranslateFileResult(null, false, failed, total, "Str_Translate_FileError");

            for (int k = 0; k < picked.Length; k++)
            {
                // Keep the indentation the line had (list items, nested subtitles).
                string original = lines[picked[k]];
                string indent = original[..(original.Length - original.TrimStart().Length)];
                lines[picked[k]] = indent + translated[k];
            }

            string target = SafeFileOps.UniquePath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? "",
                $"{Path.GetFileNameWithoutExtension(path)}.{route[^1].ToCode}{Path.GetExtension(path)}"));
            try
            {
                AtomicFile.WriteAllText(target, string.Join(newline, lines), encoding);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TranslateFileResult(null, false, failed, total, "Str_Translate_FileCantSave");
            }

            progress?.Report(1);
            return new TranslateFileResult(target, false, failed, total);
        }

        /// <summary>UTF-8 (with or without a BOM) when the bytes are valid UTF-8, otherwise Latin-1; the same encoding is used to save.</summary>
        private static (string Text, Encoding Encoding) Decode(byte[] bytes)
        {
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            try
            {
                string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                return (text, new UTF8Encoding(bom));
            }
            catch (DecoderFallbackException)
            {
                return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
            }
        }

        /// <summary>Which lines are words to translate: not blank, and (subtitles) not an index, a time code or a header; (Markdown) not inside a code fence.</summary>
        private static bool[] WordLines(string[] lines, string extension)
        {
            var words = new bool[lines.Length];
            bool subtitles = extension is ".srt" or ".vtt";
            bool inFence = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                if (extension == ".md" && line.StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                if (subtitles)
                {
                    if (OnlyDigits.IsMatch(line) || line.Contains("-->") || line.StartsWith("WEBVTT", StringComparison.Ordinal)
                        || line.StartsWith("NOTE", StringComparison.Ordinal) || line.StartsWith("STYLE", StringComparison.Ordinal))
                        continue;
                }
                words[i] = true;
            }
            return words;
        }
    }
}
