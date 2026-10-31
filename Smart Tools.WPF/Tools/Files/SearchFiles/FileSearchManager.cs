using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// File Search engine: finds files, matches names and contents (plain
    /// text, wildcard, or several regex patterns at once), lists each value
    /// once when asked, and streams rows back in a stable order. No WPF, so
    /// it's tested in a plain test project. Export and settings are in
    /// FileSearchManager.Export.cs.
    ///
    /// Fixed against the old SearchManager:
    ///  - Only the FIRST match of the FIRST pattern on a line was found, so
    ///    "A1 A2" gave only A1 and a second pattern on the same line was
    ///    never seen. Every match of every pattern is found now.
    ///  - "Single occurrence" + "Stop at first match" lost values: a file
    ///    whose first match was already listed from another file stopped
    ///    there, so its new values were never found. Unique mode now reads
    ///    the whole file and "first match" means its first NEW value.
    ///  - Which copy "Single occurrence" kept was random (whichever thread
    ///    finished first). Rows now come in folder, file and line order, and
    ///    each unique row says how many times and in how many files it was
    ///    seen.
    ///  - Nothing was shown until the very end, and Cancel threw everything
    ///    away. Rows appear as they're found, and Cancel keeps them.
    ///  - Folder links were followed (a link loop listed the same files over
    ///    and over), and a folder added inside another added folder was
    ///    searched twice, so every match appeared twice.
    ///  - A slow pattern dropped the whole file silently, losing what was
    ///    already found; skipped files weren't reported at all.
    ///  - Multiline / "dot matches newline" did nothing (each line is matched
    ///    on its own), so they're gone from the UI.
    ///  - Files were opened in a way that fails on logs another program is
    ///    still writing; they're opened read-only, sharing with writers.
    /// </summary>
    public static partial class FileSearchManager
    {
        public static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromSeconds(1);
        public static int MaxResults { get; set; } = 200_000;
        public const int MaxOtherFilesListed = 10;
        private const int BinarySniffBytes = 8192;
        private const int MaxLineTextChars = 300;
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

        // =================================================================
        //  Matching
        // =================================================================

        public readonly record struct Hit(int Index, int Length, string PatternName, IReadOnlyDictionary<string, string>? Segments = null);

        /// <summary>The named groups of a match, e.g. (?&lt;year&gt;\d{4}) → year=2026. Unnamed groups and groups that matched nothing are left out.</summary>
        private static IReadOnlyDictionary<string, string>? NamedSegments(Regex regex, Match m)
        {
            Dictionary<string, string>? segments = null;
            foreach (string groupName in regex.GetGroupNames())
            {
                if (int.TryParse(groupName, out _)) continue;
                var g = m.Groups[groupName];
                if (!g.Success || g.Length == 0) continue;
                (segments ??= new Dictionary<string, string>())[groupName] = g.Value;
            }
            return segments;
        }

        /// <summary>Finds every match of every pattern in one string (a file name or a line).</summary>
        public sealed class Matcher
        {
            private readonly List<(string Name, Regex Regex)> _regexes = new();
            private readonly string? _plain;
            private readonly StringComparison _comparison;

            private Matcher(string plain, StringComparison comparison)
            {
                _plain = plain;
                _comparison = comparison;
            }

            private Matcher(List<(string, Regex)> regexes) => _regexes = regexes;

            public int PatternCount => _plain != null ? 1 : _regexes.Count;

            /// <summary>How many different patterns can be "found" in a file ("At most one row per file" counts per pattern).</summary>
            public int PatternNameCount => _plain != null ? 1 : Math.Max(1, _regexes.Select(r => r.Name).Distinct().Count());

            /// <summary>The matcher, or null with a plain reason (no text, a bad pattern...).</summary>
            public static Matcher? Build(FileSearchOptions o, out string? error)
            {
                error = null;
                var flags = RegexOptions.CultureInvariant | (o.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                try
                {
                    switch (o.Mode)
                    {
                        case FileSearchMode.Regex:
                        {
                            var list = new List<(string, Regex)>();
                            foreach (var p in o.Patterns.Where(p => !string.IsNullOrEmpty(p.Pattern)).DistinctBy(p => (p.Pattern, p.IsWildcard)))
                            {
                                string source = p.IsWildcard ? WildcardToRegex(p.Pattern) : p.Pattern;
                                try
                                {
                                    list.Add((p.Name, new Regex(source, flags, MatchTimeout)));
                                }
                                catch (ArgumentException ex)
                                {
                                    error = $"The pattern \"{p.Name}\" isn't valid: {ShortRegexError(ex)}";
                                    return null;
                                }
                            }
                            if (list.Count == 0)
                            {
                                error = "Tick at least one pattern, or type one.";
                                return null;
                            }
                            return new Matcher(list);
                        }
                        case FileSearchMode.Wildcard:
                            if (string.IsNullOrWhiteSpace(o.SearchText))
                            {
                                error = "Type a wildcard, like *.log or report-??.txt.";
                                return null;
                            }
                            return new Matcher(new List<(string, Regex)> { ("", new Regex(WildcardToRegex(o.SearchText.Trim()), flags, MatchTimeout)) });
                        default:
                            if (string.IsNullOrEmpty(o.SearchText))
                            {
                                error = "Type something to search for.";
                                return null;
                            }
                            if (o.WholeWord)
                                return new Matcher(new List<(string, Regex)> { ("", new Regex($@"(?<!\w){Regex.Escape(o.SearchText)}(?!\w)", flags, MatchTimeout)) });
                            return new Matcher(o.SearchText, o.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    }
                }
                catch (ArgumentException ex)
                {
                    error = ShortRegexError(ex);
                    return null;
                }
            }

            /// <summary>
            /// Every match, left to right. Empty matches are ignored (they
            /// aren't anything to list). When two patterns find the very same
            /// text, it's one hit with both names.
            /// </summary>
            public List<Hit> Matches(string text)
            {
                var hits = new List<Hit>();
                if (text.Length == 0) return hits;
                if (_plain != null)
                {
                    int at = 0;
                    while (at <= text.Length - _plain.Length && (at = text.IndexOf(_plain, at, _comparison)) >= 0)
                    {
                        hits.Add(new Hit(at, _plain.Length, ""));
                        at += _plain.Length;
                    }
                    return hits;
                }
                foreach (var (name, regex) in _regexes)
                {
                    for (var m = regex.Match(text); m.Success; m = m.NextMatch())
                    {
                        if (m.Length == 0) continue;
                        int same = hits.FindIndex(h => h.Index == m.Index && h.Length == m.Length);
                        var segments = NamedSegments(regex, m);
                        if (same >= 0)
                        {
                            var merged = hits[same].Segments;
                            if (segments != null)
                            {
                                var both = merged == null ? new Dictionary<string, string>() : new Dictionary<string, string>(merged);
                                foreach (var kv in segments) both.TryAdd(kv.Key, kv.Value);
                                merged = both;
                            }
                            if (name.Length > 0 && !hits[same].PatternName.Split(", ").Contains(name))
                                hits[same] = hits[same] with { PatternName = hits[same].PatternName.Length == 0 ? name : hits[same].PatternName + ", " + name, Segments = merged };
                            else if (!ReferenceEquals(merged, hits[same].Segments))
                                hits[same] = hits[same] with { Segments = merged };
                        }
                        else hits.Add(new Hit(m.Index, m.Length, name, segments));
                    }
                }
                if (_regexes.Count > 1) hits.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : b.Length.CompareTo(a.Length));
                return hits;
            }
        }

        /// <summary>The pattern names on a hit ("Email" or "Email, Phone" when both found the same text; "" in text mode).</summary>
        private static IEnumerable<string> PatternNames(string patternName) =>
            patternName.Length == 0 ? new[] { "" } : patternName.Split(", ");

        /// <summary>"*.txt" → ^.*\.txt$ ("?" = one character). Wildcards describe a whole file name (or a whole line).</summary>
        public static string WildcardToRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            foreach (char c in pattern)
                sb.Append(c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) });
            return sb.Append('$').ToString();
        }

        /// <summary>Null when the pattern can be used; otherwise a short reason (for the sidebar list).</summary>
        public static string? CheckPattern(string pattern, bool isWildcard)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return "It's empty.";
            if (isWildcard) return null;
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
                return null;
            }
            catch (ArgumentException ex)
            {
                return ShortRegexError(ex);
            }
        }

        private static string ShortRegexError(ArgumentException ex) =>
            ex is RegexParseException p ? $"{p.Error} at character {p.Offset}" : ex.Message;

        // =================================================================
        //  Finding files
        // =================================================================

        /// <summary>
        /// Every file under the folders, in a stable order (folder by folder,
        /// files before subfolders, A-Z). Links aren't followed, a file is
        /// listed once even when one added folder is inside another, and an
        /// unreadable folder is counted rather than stopping the search.
        /// </summary>
        public static (List<string> Files, int Inaccessible) ListFiles(IReadOnlyList<string> folders, bool includeSubfolders,
            CancellationToken token, Action<int>? onCount = null)
        {
            var files = new List<string>();
            var known = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            int inaccessible = 0;
            var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
            var clock = Stopwatch.StartNew();

            foreach (string root in folders)
            {
                if (!Directory.Exists(root)) continue;
                var pending = new Stack<string>();
                pending.Push(Path.GetFullPath(root));
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    string dir = pending.Pop();
                    try
                    {
                        var here = Directory.EnumerateFiles(dir, "*", options).ToList();
                        here.Sort(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in here)
                            if (known.Add(f)) files.Add(f);
                        if (includeSubfolders)
                        {
                            var subs = Directory.EnumerateDirectories(dir, "*", options).ToList();
                            subs.Sort(StringComparer.OrdinalIgnoreCase);
                            for (int i = subs.Count - 1; i >= 0; i--) pending.Push(subs[i]);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        inaccessible++;
                    }
                    if (onCount != null && clock.Elapsed > ReportEvery)
                    {
                        onCount(files.Count);
                        clock.Restart();
                    }
                }
            }
            return (files, inaccessible);
        }

        // =================================================================
        //  Searching one file
        // =================================================================

        /// <summary>A match found in a file, before it becomes a row.</summary>
        public sealed record RawMatch(string Value, int Line, int Column, string PatternName, string LineText, FileSearchMatchKind Kind, IReadOnlyDictionary<string, string>? Segments = null)
        {
            /// <summary>Unique mode: how many times this value is in this file.</summary>
            public int CountInFile { get; set; } = 1;
        }

        public sealed class FileScan
        {
            public List<RawMatch> Matches { get; } = new();
            public long MatchCount { get; set; }
            public string? Skip { get; set; }
        }

        /// <summary>
        /// The file name and/or every line of the file. Unique mode keeps one
        /// entry per value (with a count) and reads the whole file, so no
        /// value is missed; otherwise "first match per file" stops at the
        /// first hit. A slow pattern keeps what was found and notes it.
        /// </summary>
        public static FileScan ScanFile(string path, Matcher matcher, FileSearchOptions o, CancellationToken token)
        {
            var scan = new FileScan();
            var byValue = o.UniqueValues ? new Dictionary<string, RawMatch>(o.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) : null;
            int maxRows = MaxResults;
            // "At most one row per file" is per pattern: with Email and Phone ticked, a file gives
            // its first email AND its first phone (it used to stop at the first match of any
            // pattern, so a file with an email never showed its phones).
            var foundPatterns = new HashSet<string>(StringComparer.Ordinal);
            int patternsWanted = matcher.PatternNameCount;

            bool Add(RawMatch m)
            {
                scan.MatchCount++;
                if (byValue != null)
                {
                    if (byValue.TryGetValue(m.Value, out var first)) first.CountInFile++;
                    else
                    {
                        byValue[m.Value] = m;
                        scan.Matches.Add(m);
                    }
                    return true;
                }
                if (!o.FirstMatchPerFile)
                {
                    if (scan.Matches.Count < maxRows) scan.Matches.Add(m);
                    return true;
                }
                bool newPattern = false;
                foreach (string name in PatternNames(m.PatternName))
                    newPattern |= foundPatterns.Add(name);
                if (newPattern && scan.Matches.Count < maxRows) scan.Matches.Add(m);
                return foundPatterns.Count < patternsWanted;   // false = every pattern found: stop reading this file
            }

            string name = Path.GetFileName(path);
            try
            {
                if (o.SearchFileNames)
                {
                    foreach (var h in matcher.Matches(name))
                    {
                        if (!Add(new RawMatch(name.Substring(h.Index, h.Length), 0, h.Index + 1, h.PatternName, name, FileSearchMatchKind.FileName, h.Segments)))
                            return scan;
                    }
                }
                if (!o.SearchFileContents) return scan;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                if (LooksBinary(stream))
                {
                    if (stream.Length > 0) scan.Skip = "Binary file - only its name was checked.";
                    return scan;
                }
                stream.Position = 0;
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, 64 * 1024);
                int lineNumber = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if ((lineNumber & 255) == 0) token.ThrowIfCancellationRequested();
                    List<Hit> hits;
                    try
                    {
                        hits = matcher.Matches(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        scan.Skip = $"A pattern was too slow on line {lineNumber:N0}; the rest of this file was skipped (matches before it are kept).";
                        return scan;
                    }
                    if (hits.Count == 0) continue;
                    string lineText = ShortLine(line);
                    foreach (var h in hits)
                    {
                        if (!Add(new RawMatch(line.Substring(h.Index, h.Length), lineNumber, h.Index + 1, h.PatternName, lineText, FileSearchMatchKind.Content, h.Segments)))
                            return scan;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                scan.Skip = "A pattern was too slow on the file name.";
            }
            catch (UnauthorizedAccessException)
            {
                scan.Skip = "Windows didn't allow reading it.";
            }
            catch (IOException ex)
            {
                scan.Skip = (ex.HResult & 0xFFFF) is 32 or 33 ? "Another program has it locked." : "It couldn't be read: " + ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scan.Skip = "It couldn't be read: " + ex.Message;   // one odd file never stops the search
            }
            return scan;
        }

        private static string ShortLine(string line)
        {
            string t = line.Trim();
            return t.Length <= MaxLineTextChars ? t : t[..MaxLineTextChars] + "…";
        }

        // Longest first: UTF-32 LE starts with the same two bytes as UTF-16 LE.
        private static readonly byte[][] TextBoms =
        {
            new byte[] { 0xFF, 0xFE, 0x00, 0x00 },
            new byte[] { 0x00, 0x00, 0xFE, 0xFF },
            new byte[] { 0xEF, 0xBB, 0xBF },
            new byte[] { 0xFF, 0xFE },
            new byte[] { 0xFE, 0xFF },
        };

        /// <summary>A BOM means text; otherwise a NUL byte in the first 8 KB means binary.</summary>
        public static bool LooksBinary(Stream stream)
        {
            var buffer = new byte[BinarySniffBytes];
            int read = 0, n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
            foreach (var bom in TextBoms)
                if (read >= bom.Length && buffer.AsSpan(0, bom.Length).SequenceEqual(bom)) return false;
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }

        // =================================================================
        //  The search: files in parallel, rows merged back in order
        // =================================================================

        private sealed class Seen
        {
            public required FileSearchResult Row;
            public int Occurrences;
            public int Files;
            public string LastFile = "";
            public readonly List<string> Others = new();
        }

        /// <summary>
        /// Searches every file. Files are read in parallel, but their rows are
        /// merged strictly in file order, so "the first place a value was
        /// found" is always the same and rows stream in a stable order.
        /// Progress (with the new rows, and new totals for unique rows seen
        /// again) is reported about 10 times a second; apply the totals with
        /// <see cref="FileSearchResult.ApplySeen"/> on the UI thread.
        /// Cancel returns what was found so far (Cancelled = true).
        /// </summary>
        public static Task<FileSearchSummary> SearchAsync(FileSearchOptions o, IProgress<FileSearchProgress>? progress, CancellationToken token) =>
            Task.Run(() => Search(o, progress, token), CancellationToken.None);

        public static FileSearchSummary Search(FileSearchOptions o, IProgress<FileSearchProgress>? progress, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            var matcher = Matcher.Build(o, out string? error) ?? throw new ArgumentException(error);

            List<string> files;
            int inaccessible;
            try
            {
                (files, inaccessible) = ListFiles(o.Folders, o.IncludeSubfolders, token,
                    count => progress?.Report(new FileSearchProgress { StillListing = true, TotalFiles = count }));
            }
            catch (OperationCanceledException)
            {
                return new FileSearchSummary { Cancelled = true, Elapsed = clock.Elapsed };
            }

            var completed = new ConcurrentDictionary<int, FileScan>();
            using var signal = new AutoResetEvent(false);
            long scanned = 0;
            string current = "";
            bool cancelled = false;

            var worker = Task.Run(() =>
            {
                try
                {
                    Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxDegreeOfParallelism), CancellationToken = token }, i =>
                    {
                        Volatile.Write(ref current, Path.GetFileName(files[i]));
                        completed[i] = ScanFile(files[i], matcher, o, token);
                        Interlocked.Increment(ref scanned);
                        signal.Set();
                    });
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    signal.Set();
                }
            });

            // ---- merge in file order ----
            var results = new List<FileSearchResult>();
            var pending = new List<FileSearchResult>();
            var seen = new Dictionary<string, Seen>(o.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var dirty = new HashSet<Seen>();
            var skipped = new List<FileSearchSkip>();
            int next = 0, withMatches = 0;
            long matchesSeen = 0;
            bool truncated = false;
            var lastReport = Stopwatch.StartNew();

            void Report(bool force)
            {
                if (progress == null || (!force && lastReport.Elapsed < ReportEvery)) return;
                progress.Report(new FileSearchProgress
                {
                    FilesScanned = Interlocked.Read(ref scanned),
                    TotalFiles = files.Count,
                    Results = results.Count,
                    MatchesSeen = matchesSeen,
                    CurrentFile = Volatile.Read(ref current),
                    NewResults = pending.ToList(),
                    SeenUpdates = dirty.Select(s => new FileSearchSeenUpdate(s.Row, s.Occurrences, s.Files, s.Others.ToArray())).ToList(),
                });
                pending.Clear();
                dirty.Clear();
                lastReport.Restart();
            }

            void Merge(string path, FileScan scan)
            {
                if (scan.Skip != null) skipped.Add(new FileSearchSkip(path, scan.Skip));
                if (scan.MatchCount == 0) return;
                withMatches++;
                matchesSeen += scan.MatchCount;
                var listedHere = new HashSet<string>(StringComparer.Ordinal);   // patterns that already have a row for this file
                foreach (var m in scan.Matches)
                {
                    if (o.UniqueValues && seen.TryGetValue(m.Value, out var s))
                    {
                        s.Occurrences += m.CountInFile;
                        if (s.LastFile != path)
                        {
                            s.Files++;
                            s.LastFile = path;
                            if (s.Others.Count < MaxOtherFilesListed) s.Others.Add(path);
                        }
                        dirty.Add(s);
                        continue;
                    }
                    if (o.FirstMatchPerFile && PatternNames(m.PatternName).All(listedHere.Contains)) continue;
                    if (results.Count >= MaxResults)
                    {
                        truncated = true;
                        continue;
                    }
                    var row = new FileSearchResult
                    {
                        FullPath = path,
                        MatchText = m.Value,
                        LineText = m.LineText,
                        PatternName = m.PatternName,
                        Segments = m.Segments ?? FileSearchResult.NoSegments,
                        LineNumber = m.Line,
                        Column = m.Column,
                        Kind = m.Kind,
                        Occurrences = m.CountInFile,
                    };
                    results.Add(row);
                    pending.Add(row);
                    foreach (string name in PatternNames(m.PatternName)) listedHere.Add(name);
                    if (o.UniqueValues)
                    {
                        var entry = new Seen { Row = row, Occurrences = m.CountInFile, Files = 1, LastFile = path };
                        seen[m.Value] = entry;
                        if (m.CountInFile > 1) dirty.Add(entry);
                    }
                }
            }

            while (next < files.Count)
            {
                if (completed.TryRemove(next, out var scan))
                {
                    Merge(files[next], scan);
                    next++;
                    Report(false);
                    continue;
                }
                if (worker.IsCompleted && !completed.ContainsKey(next))
                {
                    cancelled = token.IsCancellationRequested;
                    break;   // cancelled: the rest was never read
                }
                signal.WaitOne(ReportEvery);
                Report(false);
            }
            worker.Wait();
            // On cancel, files after the gap may have finished: keep their rows too (still in order).
            foreach (var i in completed.Keys.Order())
                Merge(files[i], completed[i]);
            cancelled |= token.IsCancellationRequested && next < files.Count;
            Report(true);

            // Counts reach the rows through progress, on the UI thread. With nobody
            // listening (tests, scripts) they're set here instead.
            if (progress == null)
                foreach (var s in seen.Values) s.Row.ApplySeen(s.Occurrences, s.Files, s.Others.ToArray());

            return new FileSearchSummary
            {
                Results = results,
                FilesScanned = Interlocked.Read(ref scanned),
                TotalFiles = files.Count,
                FilesWithMatches = withMatches,
                MatchesSeen = matchesSeen,
                Skipped = skipped,
                InaccessibleFolders = inaccessible,
                Truncated = truncated,
                Cancelled = cancelled,
                Elapsed = clock.Elapsed,
            };
        }

        public static string Describe(FileSearchSummary s, bool unique)
        {
            var parts = new List<string>();
            string rows = unique
                ? $"{s.Results.Count:N0} different {FileSearchText.Plural(s.Results.Count, "value", "values")} ({s.MatchesSeen:N0} {FileSearchText.Plural(s.MatchesSeen, "match", "matches")} in total)"
                : $"{s.Results.Count:N0} {FileSearchText.Plural(s.Results.Count, "match", "matches")}";
            parts.Add($"{rows} in {s.FilesWithMatches:N0} of {s.FilesScanned:N0} {FileSearchText.Plural(s.FilesScanned, "file", "files")}");
            if (s.Skipped.Count > 0) parts.Add($"{s.Skipped.Count:N0} skipped");
            if (s.InaccessibleFolders > 0) parts.Add($"{s.InaccessibleFolders:N0} {FileSearchText.Plural(s.InaccessibleFolders, "folder", "folders")} couldn't be opened");
            if (s.Truncated) parts.Add($"list stopped at {MaxResults:N0} rows");
            parts.Add(s.Elapsed.TotalSeconds < 1 ? $"{s.Elapsed.TotalMilliseconds:0} ms" : $"{s.Elapsed.TotalSeconds:0.0} s");
            string text = string.Join(" · ", parts) + ".";
            return s.Cancelled ? "Stopped. " + text : text;
        }
    }
}
