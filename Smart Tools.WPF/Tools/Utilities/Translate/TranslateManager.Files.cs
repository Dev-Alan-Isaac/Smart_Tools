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
        public static readonly string[] FileExtensions = { ".txt", ".md", ".srt", ".vtt", ".json" };

        /// <summary>Bigger than this is refused (the model works a sentence at a time; 8 MB is already ~100,000 sentences).</summary>
        public const long MaxFileBytes = 8L * 1024 * 1024;

        private static readonly Regex OnlyDigits = new(@"^\d+$", RegexOptions.Compiled);

        /// <summary>Numbers, separators and only a date/time marker (T, Z, AM, PM, UTC, GMT): not words.</summary>
        private static readonly Regex DateOrTime = new(@"^[\d\s:\-/.,+]*\d[\d\s:\-/.,+]*(T|Z|AM|PM|UTC|GMT|[\d\s:\-/.,+])*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            // What to translate: a list of single lines (units) and a way to put the translated lines back.
            string extension = Path.GetExtension(path).ToLowerInvariant();
            List<string> units;
            Func<IReadOnlyList<string>, string> rebuild;
            if (extension == ".json")
            {
                if (!TryPrepareJson(text, out units, out rebuild)) return new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileBadJson");
            }
            else
            {
                string newline = text.Contains("\r\n") ? "\r\n" : "\n";
                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                bool[] words = WordLines(lines, extension);
                int[] picked = Enumerable.Range(0, lines.Length).Where(i => words[i]).ToArray();
                units = picked.Select(i => lines[i].Trim()).ToList();
                rebuild = translatedLines =>
                {
                    for (int k = 0; k < picked.Length; k++)
                    {
                        // Keep the indentation the line had (list items, nested subtitles).
                        string original = lines[picked[k]];
                        string indent = original[..(original.Length - original.TrimStart().Length)];
                        lines[picked[k]] = indent + translatedLines[k];
                    }
                    return string.Join(newline, lines);
                };
            }
            if (units.Count == 0) return new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileNothing");

            // Only the word lines go to the model, one per line; it hands back exactly one line for each.
            string current = string.Join("\n", units);
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
            if (translated.Length != units.Count) return new TranslateFileResult(null, false, failed, total, "Str_Translate_FileError");

            string output = rebuild(translated);

            string target = SafeFileOps.UniquePath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? "",
                $"{Path.GetFileNameWithoutExtension(path)}.{route[^1].ToCode}{Path.GetExtension(path)}"));
            try
            {
                AtomicFile.WriteAllText(target, output, FitEncoding(output, encoding));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TranslateFileResult(null, false, failed, total, "Str_Translate_FileCantSave");
            }

            progress?.Report(1);
            return new TranslateFileResult(target, false, failed, total);
        }

        /// <summary>
        /// JSON: only string VALUES that contain words are translated (keys, numbers, true/false, null, URLs, e-mail
        /// addresses and strings with no letters stay as they are). A value with line breaks is translated line by line.
        /// The structure and key order are kept; the file is written indented when the original had line breaks, compact otherwise.
        /// </summary>
        private static bool TryPrepareJson(string text, out List<string> units, out Func<IReadOnlyList<string>, string> rebuild)
        {
            units = new List<string>();
            rebuild = _ => text;

            System.Text.Json.Nodes.JsonNode? root;
            try
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(text, null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            {
                return false;   // not valid JSON (or a repeated key, which the tree can't hold)
            }
            if (root == null) return false;

            // Every translatable string, with the lines it will be sent as.
            // Each value remembers where it sits (parent + key or index) so putting it back is O(1), not a search.
            var values = new List<(System.Text.Json.Nodes.JsonNode Parent, string? Key, int Index, string[] Lines, int First)>();
            var found = new List<string>();
            void Walk(System.Text.Json.Nodes.JsonNode? node, System.Text.Json.Nodes.JsonNode? parent, string? key, int index)
            {
                switch (node)
                {
                    case System.Text.Json.Nodes.JsonObject obj:
                        foreach (var pair in obj) Walk(pair.Value, obj, pair.Key, -1);
                        break;
                    case System.Text.Json.Nodes.JsonArray array:
                        for (int n = 0; n < array.Count; n++) Walk(array[n], array, null, n);
                        break;
                    case System.Text.Json.Nodes.JsonValue value when parent != null && value.TryGetValue<string>(out string? s) && IsWords(s):
                        string[] parts = s.Replace("\r\n", "\n").Split('\n');
                        values.Add((parent, key, index, parts, found.Count));
                        foreach (string part in parts) if (part.Trim().Length > 0) found.Add(part.Trim());
                        break;
                }
            }
            try
            {
                Walk(root, null, null, -1);
            }
            catch (ArgumentException)
            {
                return false;   // a repeated key: the tree can't hold it, and saving would silently drop one value
            }
            units = found;

            bool indented = text.Contains('\n');
            rebuild = translatedLines =>
            {
                foreach (var (parent, key, index, parts, first) in values)
                {
                    int next = first;
                    var rebuilt = new string[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i].Trim().Length == 0) { rebuilt[i] = parts[i]; continue; }
                        string original = parts[i];
                        string indent = original[..(original.Length - original.TrimStart().Length)];
                        rebuilt[i] = indent + translatedLines[next++];
                    }
                    // Replace the value in its parent.
                    string joined = string.Join("\n", rebuilt);
                    if (parent is System.Text.Json.Nodes.JsonObject o && key != null) o[key] = joined;
                    else if (parent is System.Text.Json.Nodes.JsonArray a && index >= 0) a[index] = joined;
                }
                return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = indented,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep accents and quotes readable
                });
            };
            return true;
        }

        /// <summary>A string worth translating: has letters, and is not a URL, e-mail address or path-like token.</summary>
        private static bool IsWords(string s)
        {
            string t = s.Trim();
            if (t.Length == 0 || !t.Any(char.IsLetter)) return false;
            if (DateOrTime.IsMatch(t)) return false;   // 2026-11-15T10:30:00Z, 10:30 PM, 2026-11-15 10:30
            if (t.Contains("://") || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return false;
            if (!t.Contains(' ') && (t.Contains('@') || t.Contains('/') || t.Contains('\\') || t.Contains('_') || t.Contains('.'))) return false;   // identifiers, paths, e-mails, file names
            return true;
        }

        private static bool _codePagesRegistered;

        private static Encoding? Strict(int codePage)
        {
            try
            {
                if (!_codePagesRegistered) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); _codePagesRegistered = true; }
                return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static string? TryStrict(Encoding? encoding, byte[] bytes, int start = 0)
        {
            if (encoding == null) return null;
            try { return encoding.GetString(bytes, start, bytes.Length - start); }
            catch (DecoderFallbackException) { return null; }
        }

        /// <summary>
        /// Finds the file's encoding: a BOM (UTF-8, UTF-16), valid UTF-8, then the legacy East Asian code pages
        /// (Shift-JIS, Korean, GBK, Big5) when the high bytes come in pairs, else Windows-1252. The same encoding is
        /// used to save, unless the translated text no longer fits it (see <see cref="FitEncoding"/>).
        /// </summary>
        private static (string Text, Encoding Encoding) Decode(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (new UnicodeEncoding(false, true).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return (new UnicodeEncoding(true, true).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));

            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string? utf8 = TryStrict(new UTF8Encoding(false, throwOnInvalidBytes: true), bytes, bom ? 3 : 0);
            if (utf8 != null) return (utf8, new UTF8Encoding(bom));

            // Legacy code pages. Only when most high bytes sit next to another high byte (double-byte text);
            // Spanish/French text in Windows-1252 has isolated ones, and would be misread as kanji otherwise.
            if (HighBytesArePaired(bytes))
            {
                var sjis = Strict(932);
                string? t = TryStrict(sjis, bytes);
                if (t != null && Share(t, c => (c >= '぀' && c <= 'ヿ') || (c >= '｡' && c <= 'ﾟ')) >= 0.2) return (t, sjis!);

                var korean = Strict(949);
                t = TryStrict(korean, bytes);
                if (t != null && Share(t, c => c >= '가' && c <= '힯') >= 0.5) return (t, korean!);

                var gbk = Strict(936);
                if ((t = TryStrict(gbk, bytes)) != null) return (t, gbk!);

                var big5 = Strict(950);
                if ((t = TryStrict(big5, bytes)) != null) return (t, big5!);
            }

            var win = Strict(1252) ?? Encoding.Latin1;
            return (win.GetString(bytes), win);
        }

        private static bool HighBytesArePaired(byte[] bytes)
        {
            int high = 0, paired = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] < 0x80) continue;
                high++;
                if ((i > 0 && bytes[i - 1] >= 0x80) || (i + 1 < bytes.Length && bytes[i + 1] >= 0x80)) paired++;
            }
            return high > 0 && paired * 2 >= high;
        }

        /// <summary>Share of the non-ASCII characters in <paramref name="text"/> that satisfy <paramref name="test"/>.</summary>
        private static double Share(string text, Func<char, bool> test)
        {
            int other = 0, hit = 0;
            foreach (char c in text)
            {
                if (c < 0x80) continue;
                other++;
                if (test(c)) hit++;
            }
            return other == 0 ? 0 : (double)hit / other;
        }

        /// <summary>
        /// The encoding to save with: the file's own when every character fits (accents in Windows-1252, kanji in Shift-JIS),
        /// otherwise UTF-8 with a BOM. A character is never replaced by "?".
        /// </summary>
        private static Encoding FitEncoding(string text, Encoding original)
        {
            if (original is UTF8Encoding || original is UnicodeEncoding) return original;
            var strict = Strict(original.CodePage);
            if (strict != null)
            {
                try { strict.GetByteCount(text); return original; }
                catch (EncoderFallbackException) { /* does not fit: fall through */ }
            }
            return new UTF8Encoding(true);
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
