using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Regex Tester engine: matching (with groups, line:column and
    /// highlights), replace preview, searching a folder, CSV export, the
    /// pattern breakdown (RegexManager.Breakdown.cs) and settings. No WPF,
    /// so it's tested in a plain test project.
    ///
    /// Fixed against the old RegexManager / view:
    ///  - A slow pattern (e.g. (a+)+$) crashed the app: live matching only
    ///    caught ArgumentException, so the RegexMatchTimeoutException escaped
    ///    a TextChanged handler. It also ran on the UI thread on every key,
    ///    freezing the window for up to 2 s each time.
    ///  - Empty matches (\b, x*) were drawn as an extra space, so the
    ///    highlighted text wasn't the real text.
    ///  - No limits: "." on a big file built a million highlights and rows on
    ///    the UI thread. Lists and highlights are now capped (with a note).
    ///  - UTF-16 text files (PowerShell output, .reg, many Windows logs) were
    ///    skipped as "binary" - the null-byte check ran before the BOM.
    ///  - One unreadable subfolder (or a folder link loop) made the whole
    ///    folder search fail; links were followed; any file size was read
    ///    whole (out of memory on a multi-GB file).
    ///  - CSV export let matched text like "=HYPERLINK(...)" run as an Excel
    ///    formula, and wrote straight over the target.
    ///  - Positions were only character offsets; now line:column too, and
    ///    named groups show their names.
    /// </summary>
    public static partial class RegexManager
    {
        public static TimeSpan LiveTimeout { get; set; } = TimeSpan.FromSeconds(1);
        public static TimeSpan FileTimeout { get; set; } = TimeSpan.FromSeconds(2);

        public const int MaxRows = 10_000;          // groups grid / CSV for one text
        public const int MaxHighlights = 2_000;     // highlighted spans drawn
        public const int MaxMatches = 100_000;      // counting stops here ("100,000+")
        public const int MaxFolderRows = 100_000;
        public const long MaxLoadBytes = 20L * 1024 * 1024;         // File mode: loaded into the editor
        public static long MaxFolderFileBytes { get; set; } = 100L * 1024 * 1024;

        public static readonly IReadOnlyList<RegexPreset> Presets = new[]
        {
            new RegexPreset("Email address", @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", "Contact us at support@example.com or sales@example.co.uk for help."),
            new RegexPreset("Website URL", @"https?://[^\s""'<>]+", "Visit https://www.example.com or http://test.org/path?query=1 today."),
            new RegexPreset("IPv4 address", @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "Servers: 192.168.1.1, 10.0.0.255, and 8.8.8.8 are online."),
            new RegexPreset("US phone number (groups)", @"\((\d{3})\)\s(\d{3})-(\d{4})", "Call (555) 123-4567 or (800) 555-0199 for support."),
            new RegexPreset("Date YYYY-MM-DD (named groups)", @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})", "Order placed on 2024-03-15 and shipped 2024-03-18."),
            new RegexPreset("Hex colour code", @"#[0-9A-Fa-f]{6}\b", "Brand palette: #1A2B3C, #FFFFFF, and #ff5733."),
            new RegexPreset("Whole numbers and decimals", @"-?\d+(?:\.\d+)?", "Totals: 42, -17, 3.14, and 100.0 units."),
            new RegexPreset("Capitalised words", @"\b[A-Z][a-z]*\b", "The Quick Brown Fox jumps over the Lazy Dog."),
            new RegexPreset("Log lines with ERROR (multiline)", @"^.*\bERROR\b.*$", "10:01 INFO start\n10:02 ERROR disk full\n10:03 INFO retry\n10:04 ERROR timeout"),
            new RegexPreset("Repeated word", @"\b(\w+)\s+\1\b", "This is is a test of the the repeated words."),
        };

        // =================================================================
        //  Building the regex
        // =================================================================

        public static RegexOptions BuildFlags(RegexTestOptions o)
        {
            var flags = RegexOptions.CultureInvariant;
            if (o.IgnoreCase) flags |= RegexOptions.IgnoreCase;
            if (o.Multiline) flags |= RegexOptions.Multiline;
            if (o.Singleline) flags |= RegexOptions.Singleline;
            if (o.IgnoreWhitespace) flags |= RegexOptions.IgnorePatternWhitespace;
            return flags;
        }

        /// <summary>The regex, or a plain reason with where in the pattern it went wrong.</summary>
        public static Regex? TryCreate(RegexTestOptions o, TimeSpan timeout, out string? error, out int errorOffset)
        {
            error = null;
            errorOffset = -1;
            if (string.IsNullOrEmpty(o.Pattern))
            {
                error = "Type a pattern.";
                return null;
            }
            try
            {
                return new Regex(o.Pattern, BuildFlags(o), timeout);
            }
            catch (RegexParseException ex)
            {
                errorOffset = ex.Offset;
                error = $"{ParseErrorText(ex.Error)} (at character {ex.Offset}).";
                return null;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static string ParseErrorText(RegexParseError e) => e switch
        {
            RegexParseError.InsufficientClosingParentheses => "A ( has no matching )",
            RegexParseError.InsufficientOpeningParentheses => "A ) has no matching (",
            RegexParseError.UnterminatedBracket => "A [ has no closing ]",
            RegexParseError.QuantifierAfterNothing => "*, + or ? has nothing before it to repeat",
            RegexParseError.NestedQuantifiersNotParenthesized => "Two repeats in a row (like a**) - put one in ( ) first",
            RegexParseError.UnescapedEndingBackslash => "The pattern ends with a lone \\ - use \\\\ for a backslash",
            RegexParseError.ReversedCharacterRange => "A range like [z-a] is backwards - put the smaller character first",
            RegexParseError.ReversedQuantifierRange => "In {m,n} the first number must not be bigger than the second",
            RegexParseError.UnrecognizedEscape => "Unknown escape - put \\ only before special characters or known codes like \\d",
            RegexParseError.UndefinedNamedReference or RegexParseError.UndefinedNumberedReference => "It refers to a group that doesn't exist",
            RegexParseError.InvalidGroupingConstruct => "Something after (? isn't a known group type",
            RegexParseError.InvalidUnicodePropertyEscape or RegexParseError.UnrecognizedUnicodeProperty => "Unknown \\p{…} category",
            RegexParseError.ShorthandClassInCharacterRange => "A range can't start or end with \\d, \\w or \\s",
            RegexParseError.InsufficientOrInvalidHexDigits => "\\x needs 2 hex digits and \\u needs 4 (like \\x41, \\u00E9)",
            RegexParseError.MalformedNamedReference => "\\k needs a group name, like \\k<year>",
            RegexParseError.UnterminatedComment => "A (?# comment has no closing )",
            RegexParseError.CaptureGroupNameInvalid => "A group name must start with a letter or _ (like (?<year>...))",
            RegexParseError.ExclusionGroupNotLast => "In [a-z-[aeiou]] the excluded part must come last",
            RegexParseError.MissingControlCharacter or RegexParseError.UnrecognizedControlCharacter => "\\c must be followed by a letter, like \\cC",
            _ => "The pattern isn't valid: " + e,
        };

        // =================================================================
        //  Matching one text
        // =================================================================

        /// <summary>
        /// Every match with its groups, line:column and highlight spans,
        /// capped (<see cref="MaxRows"/>, <see cref="MaxHighlights"/>,
        /// <see cref="MaxMatches"/>) so a pattern like "." on a big file stays
        /// quick. A timeout keeps what was found and says so. Optional
        /// replace preview.
        /// </summary>
        public static RegexTextResult MatchText(string text, Regex regex, string fileName, string fullPath, string? replacement, CancellationToken token,
            int maxRows = MaxRows, int maxHighlights = MaxHighlights)
        {
            var sw = Stopwatch.StartNew();
            var rows = new List<RegexMatchRow>();
            var spans = new List<RegexSpan>();
            int count = 0;
            bool truncated = false, timedOut = false;
            var lines = new LineIndex(text);
            var names = regex.GetGroupNumbers().ToDictionary(n => n, n => regex.GroupNameFromNumber(n));

            try
            {
                for (var m = regex.Match(text); m.Success; m = m.NextMatch())
                {
                    if ((count & 63) == 0) token.ThrowIfCancellationRequested();
                    count++;
                    if (spans.Count < maxHighlights) spans.Add(new RegexSpan(m.Index, m.Length));
                    if (rows.Count < maxRows)
                    {
                        for (int g = 0; g < m.Groups.Count; g++)
                        {
                            var group = m.Groups[g];
                            if (!group.Success) continue;
                            int number = regex.GroupNumberFromName(group.Name);
                            var (line, col) = lines.Locate(group.Index);
                            rows.Add(new RegexMatchRow
                            {
                                FileName = fileName,
                                FullPath = fullPath,
                                MatchNumber = count,
                                GroupNumber = number,
                                GroupName = number == 0 ? "Whole match" : names.TryGetValue(number, out var n) && n != number.ToString(CultureInfo.InvariantCulture) ? n : number.ToString(CultureInfo.InvariantCulture),
                                Match = m.Value,
                                GroupValue = group.Value,
                                Position = group.Index,
                                Line = line,
                                Column = col,
                                Length = group.Length,
                            });
                        }
                    }
                    else truncated = true;
                    if (count >= MaxMatches)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut = true;
            }
            if (spans.Count < count) truncated = true;

            string? replaced = null;
            if (replacement != null && !timedOut)
            {
                try { replaced = regex.Replace(text, replacement); }
                catch (RegexMatchTimeoutException) { timedOut = true; }
            }

            return new RegexTextResult
            {
                Rows = rows,
                Highlights = spans,
                MatchCount = count,
                Truncated = truncated,
                TimedOut = timedOut,
                Replaced = replaced,
                Elapsed = sw.Elapsed,
            };
        }

        /// <summary>Live matching off the UI thread; a newer keystroke cancels this one through the token.</summary>
        public static Task<RegexTextResult> MatchTextAsync(string text, RegexTestOptions options, CancellationToken token, string fileName = "(text)", string fullPath = "") =>
            Task.Run(() =>
            {
                var regex = TryCreate(options, LiveTimeout, out string? error, out int offset);
                if (regex == null) return new RegexTextResult { Error = error, ErrorOffset = offset };
                return MatchText(text, regex, fileName, fullPath, string.IsNullOrEmpty(options.Replacement) ? null : options.Replacement, token);
            }, token);

        /// <summary>Finds line:column for an offset - line starts are found once, then a binary search per match.</summary>
        public sealed class LineIndex
        {
            private readonly List<int> _starts = new() { 0 };

            public LineIndex(string text)
            {
                for (int i = 0; i < text.Length; i++)
                    if (text[i] == '\n') _starts.Add(i + 1);
            }

            public (int Line, int Column) Locate(int offset)
            {
                int i = _starts.BinarySearch(offset);
                if (i < 0) i = ~i - 1;
                return (i + 1, offset - _starts[i] + 1);
            }
        }

        public static string DescribeResult(RegexTextResult r)
        {
            if (r.Error != null) return r.Error;
            string count = r.MatchCount >= MaxMatches ? $"{MaxMatches:N0}+ matches" : $"{r.MatchCount:N0} {RegexText.Plural(r.MatchCount, "match", "matches")}";
            if (r.TimedOut) return $"The pattern is too slow on this text and was stopped after {count} - it likely backtracks a lot (e.g. (a+)+). Try making it more specific.";
            string time = r.Elapsed.TotalMilliseconds < 1 ? "<1 ms" : $"{r.Elapsed.TotalMilliseconds:0} ms";
            string note = r.Truncated ? $" · showing the first {r.Highlights.Count:N0} highlights and {r.Rows.Count:N0} group rows" : "";
            return $"{count} · {time}{note}";
        }

        // =================================================================
        //  Reading text files (BOM first, then the binary check)
        // =================================================================

        /// <summary>
        /// The file as text, or a reason to skip it. A BOM decides the
        /// encoding first (UTF-8/16/32); only then is a NUL byte taken as
        /// "binary" - the old check ran first and skipped every UTF-16 file.
        /// </summary>
        public static (string? Text, string? SkipReason) ReadTextFile(string path, long maxBytes)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return (null, "It isn't there any more.");
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (null, "It's a link or cloud placeholder - skipped.");
                if (info.Length > maxBytes) return (null, $"Too big ({RegexText.Bytes(info.Length)}; the limit is {RegexText.Bytes(maxBytes)}).");
                byte[] bytes = File.ReadAllBytes(path);
                var (encoding, bomLength) = DetectBom(bytes);
                if (encoding == null)
                {
                    int sniff = Math.Min(bytes.Length, 8192);
                    if (Array.IndexOf(bytes, (byte)0, 0, sniff) >= 0) return (null, "It looks like a binary file, not text.");
                    encoding = new UTF8Encoding(false, false);
                    string utf8 = encoding.GetString(bytes);
                    if (utf8.Count(c => c == '�') > Math.Max(8, utf8.Length / 200)) return (Encoding.Latin1.GetString(bytes), null);   // old 8-bit text (Windows-1252 and friends)
                    return (utf8, null);
                }
                return (encoding.GetString(bytes, bomLength, bytes.Length - bomLength), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, ex is UnauthorizedAccessException ? "Windows didn't allow reading it." : "It couldn't be read: " + ex.Message);
            }
        }

        public static (Encoding? Encoding, int BomLength) DetectBom(byte[] b)
        {
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) return (new UTF32Encoding(false, true), 4);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false), 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, true), 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, true), 2);
            return (null, 0);
        }

        /// <summary>File mode: the text to put in the editor, off the UI thread, with a size limit.</summary>
        public static Task<(string? Text, string? Error)> LoadFileAsync(string path) =>
            Task.Run(() =>
            {
                var (text, reason) = ReadTextFile(path, MaxLoadBytes);
                return text != null
                    ? (text, (string?)null)
                    : (null, reason != null && reason.StartsWith("Too big") ? reason + " Use Folder mode to search big files." : reason);
            });

        // =================================================================
        //  Searching a folder
        // =================================================================

        /// <summary>"*.txt;*.log" → patterns; blank or "*" = every file.</summary>
        public static IReadOnlyList<string> ParseFilter(string? filter)
        {
            var parts = (filter ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(p => p.Contains('*') || p.Contains('?') ? p : p.StartsWith('.') ? "*" + p : "*." + p)
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .ToList();
            return parts.Count == 0 ? new[] { "*" } : parts;
        }

        public static bool MatchesFilter(string fileName, IReadOnlyList<string> patterns) =>
            patterns.Any(p => p == "*" || FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));

        /// <summary>
        /// Files to search, natural order. Folder links aren't followed and an
        /// unreadable folder is counted, not fatal - the old search failed as
        /// a whole when one subfolder couldn't be listed.
        /// </summary>
        public static (List<string> Files, int Inaccessible) ListFiles(string folder, bool includeSubfolders, IReadOnlyList<string> filter, CancellationToken token)
        {
            var files = new List<string>();
            int inaccessible = 0;
            var pending = new Stack<string>();
            pending.Push(folder);
            var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                try
                {
                    var here = Directory.EnumerateFiles(dir, "*", options).Where(f => MatchesFilter(Path.GetFileName(f), filter)).ToList();
                    here.Sort(StringComparer.OrdinalIgnoreCase);
                    files.AddRange(here);
                    if (!includeSubfolders) continue;
                    var subs = Directory.EnumerateDirectories(dir, "*", options).ToList();
                    subs.Sort(StringComparer.OrdinalIgnoreCase);
                    for (int i = subs.Count - 1; i >= 0; i--) pending.Push(subs[i]);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    inaccessible++;
                }
            }
            return (files, inaccessible);
        }

        /// <summary>Searches every matching file; one bad file never stops the rest. Each file has its own time limit.</summary>
        public static Task<RegexFolderResult> RunOnFolderAsync(string folder, RegexTestOptions options, bool includeSubfolders, string? fileFilter,
            IProgress<RegexProgress>? progress, CancellationToken token) =>
            Task.Run(() =>
            {
                var regex = TryCreate(options, FileTimeout, out string? error, out _);
                if (regex == null) return new RegexFolderResult { Error = error };
                if (!Directory.Exists(folder)) return new RegexFolderResult { Error = "That folder isn't there any more." };

                var rows = new List<RegexMatchRow>();
                var skipped = new List<RegexSkippedFile>();
                int matches = 0, withMatches = 0, scanned = 0;
                bool truncated = false, cancelled = false;
                List<string> files;
                int inaccessible;
                try
                {
                    (files, inaccessible) = ListFiles(folder, includeSubfolders, ParseFilter(fileFilter), token);
                }
                catch (OperationCanceledException)
                {
                    return new RegexFolderResult { Cancelled = true };
                }
                if (inaccessible > 0) skipped.Add(new RegexSkippedFile(folder, $"{inaccessible:N0} {RegexText.Plural(inaccessible, "folder", "folders")} inside couldn't be opened."));

                var last = Stopwatch.StartNew();
                for (int i = 0; i < files.Count; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    string path = files[i];
                    if (last.ElapsedMilliseconds > 80 || i == 0)
                    {
                        progress?.Report(new RegexProgress(i, files.Count, matches, Path.GetFileName(path)));
                        last.Restart();
                    }

                    var (text, reason) = ReadTextFile(path, MaxFolderFileBytes);
                    if (text == null)
                    {
                        skipped.Add(new RegexSkippedFile(path, reason ?? "Skipped."));
                        continue;
                    }
                    RegexTextResult r;
                    try
                    {
                        r = MatchText(text, regex, Path.GetFileName(path), path, null, token, Math.Max(0, MaxFolderRows - rows.Count), 0);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;   // this file's partial results are dropped, so it isn't counted
                        break;
                    }
                    scanned++;
                    if (r.TimedOut) skipped.Add(new RegexSkippedFile(path, "The pattern was too slow on this file and was stopped (the matches found before that are kept)."));
                    if (r.MatchCount > 0) withMatches++;
                    matches += r.MatchCount;
                    rows.AddRange(r.Rows);
                    if (rows.Count >= MaxFolderRows) truncated = true;
                }
                progress?.Report(new RegexProgress(cancelled ? scanned : files.Count, files.Count, matches, ""));

                return new RegexFolderResult
                {
                    Rows = rows,
                    MatchCount = matches,
                    FilesScanned = scanned,
                    FilesWithMatches = withMatches,
                    Skipped = skipped,
                    Truncated = truncated,
                    Cancelled = cancelled,
                };
            }, token);

        public static string DescribeFolder(RegexFolderResult r)
        {
            if (r.Error != null) return r.Error;
            var parts = new List<string>
            {
                $"{r.MatchCount:N0} {RegexText.Plural(r.MatchCount, "match", "matches")} in {r.FilesWithMatches:N0} of {r.FilesScanned:N0} {RegexText.Plural(r.FilesScanned, "file", "files")}",
            };
            if (r.Skipped.Count > 0) parts.Add($"{r.Skipped.Count:N0} skipped (hover the pill for why)");
            if (r.Truncated) parts.Add($"showing the first {r.Rows.Count:N0} rows");
            string text = string.Join(" · ", parts) + ".";
            return r.Cancelled ? "Stopped. " + text : text;
        }

        // =================================================================
        //  CSV export
        // =================================================================

        /// <summary>Writes the rows as CSV (UTF-8 with BOM for Excel), via a temp file. Matched text can't run as a formula.</summary>
        public static Task ExportCsvAsync(IReadOnlyList<RegexMatchRow> rows, string path) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Directory.CreateDirectory(folder);
                string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var w = new StreamWriter(temp, false, new UTF8Encoding(true)))
                    {
                        w.WriteLine("File,Folder,Match #,Group,Group value,Line,Column,Position,Length,Whole match");
                        foreach (var r in rows)
                        {
                            w.WriteLine(string.Join(",",
                                CsvCell(r.FileName), CsvCell(Path.GetDirectoryName(r.FullPath) ?? ""),
                                r.MatchNumber, CsvCell(r.GroupName), CsvCell(r.GroupValue),
                                r.Line, r.Column, r.Position, r.Length, CsvCell(r.Match)));
                        }
                    }
                    File.Move(temp, path, overwrite: true);   // the Save dialog already asked about replacing
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            });

        /// <summary>Quotes as needed; a cell starting with = + - @ gets a leading apostrophe (unless it's just a number) so Excel can't run it.</summary>
        public static string CsvCell(string value)
        {
            string v = value ?? "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            bool quote = v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || v != v.Trim();
            return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        public static string PlainSaveError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows didn't allow saving there - pick another folder.",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "The file is open in another program (Excel?) - close it and try again.",
            _ => ex.Message,
        };

        // =================================================================
        //  Settings (%AppData%\Smart Tools\regex_tester.json - no text, no paths)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "regex_tester.json");

        public static RegexSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new RegexSettings();
                var s = JsonSerializer.Deserialize<RegexSettings>(File.ReadAllText(file), SettingsJson) ?? new RegexSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = RegexInputMode.Text;
                s.Pattern ??= "";
                s.Replacement ??= "";
                s.FileFilter ??= "";
                if (s.Pattern.Length > 10_000) s.Pattern = s.Pattern[..10_000];
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new RegexSettings();
            }
        }

        public static void SaveSettings(RegexSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
