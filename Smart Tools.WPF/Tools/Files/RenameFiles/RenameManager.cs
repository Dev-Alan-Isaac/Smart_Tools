using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Rename Files backend, split in two:
    /// <list type="bullet">
    /// <item>RenameManager.cs (this file) - no side effects: the new name
    /// for each rule, safe Windows names, clashes inside a batch, the plan,
    /// settings and the undo journal on disk.</item>
    /// <item>RenameManager.Run.cs - reading the folders, renaming (swaps and
    /// chains included) and undo.</item>
    /// </list>
    /// </summary>
    public static partial class RenameManager
    {
        // =================================================================
        //  Names for one file
        // =================================================================

        // Letters in any language: the old [a-zA-Z] split "Café" into "Caf" and dropped the é.
        private static readonly Regex WordSplit = new(@"[^\p{L}\p{N}]+|(?<=[\p{Ll}\p{N}])(?=\p{Lu})", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex Symbols = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

        public static string ReplaceLiteral(string input, string find, string replacement, bool matchCase, bool wholeWord)
        {
            if (string.IsNullOrEmpty(find)) return input;
            if (wholeWord)
            {
                // "Whole word" means not touching another letter or digit. The old \b counted
                // "_" as part of a word, so "2024" was never found in "IMG_2024".
                string pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(find)}(?![\p{{L}}\p{{N}}])";
                return Regex.Replace(input, pattern, _ => replacement, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            }

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var sb = new StringBuilder();
            int pos = 0;
            while (true)
            {
                int idx = input.IndexOf(find, pos, comparison);
                if (idx < 0)
                {
                    sb.Append(input, pos, input.Length - pos);
                    return sb.ToString();
                }
                sb.Append(input, pos, idx - pos).Append(replacement);
                pos = idx + find.Length;
            }
        }

        public static string ApplyCase(string text, RenameCaseStyle style) => style switch
        {
            RenameCaseStyle.Upper => text.ToUpperInvariant(),
            RenameCaseStyle.Lower => text.ToLowerInvariant(),
            RenameCaseStyle.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
            RenameCaseStyle.Sentence => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant(),
            RenameCaseStyle.Camel => JoinWords(text, firstLower: true),
            RenameCaseStyle.Pascal => JoinWords(text, firstLower: false),
            _ => text,
        };

        private static string JoinWords(string text, bool firstLower)
        {
            var words = WordSplit.Split(text).Where(w => w.Length > 0).ToArray();
            if (words.Length == 0) return text;
            var sb = new StringBuilder();
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                sb.Append(i == 0 && firstLower
                    ? w.ToLowerInvariant()
                    : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
            }
            return sb.ToString();
        }

        public static string TrimName(string name, RenameOptions o)
        {
            string result = name;
            int start = Math.Clamp(o.TrimStart, 0, result.Length);
            result = result[start..];
            int end = Math.Clamp(o.TrimEnd, 0, result.Length);
            result = result[..(result.Length - end)];
            if (o.StripLeadingTrailingSpaces) result = result.Trim();
            if (o.CollapseRepeatedSpaces) result = Whitespace.Replace(result, " ");
            return result;
        }

        public static string RemoveCharacters(string name, RenameOptions o)
        {
            string result = name;
            if (o.RemoveDiacritics) result = StripDiacritics(result);
            foreach (char c in o.CharsToRemove ?? "") result = result.Replace(c.ToString(), "");
            if (o.RemoveNumbers) result = new string(result.Where(c => !char.IsDigit(c)).ToArray());
            if (o.RemoveSymbols) result = Symbols.Replace(result, "");
            if (o.RemoveSpaces) result = result.Replace(" ", "");
            return result;
        }

        // Digit limits keep real names safe: "IMG_0001" (4 digits) and "2024-01-15" (digit before the dash) don't match.
        private const RegexOptions CleanOptions = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly TimeSpan CleanTimeout = TimeSpan.FromMilliseconds(250);
        private static readonly Regex UnderscoreCounter = new(@"_\d{1,3}$", CleanOptions, CleanTimeout);
        private static readonly Regex DashCounter = new(@"(?<=\D)-\d{1,3}$", CleanOptions, CleanTimeout);
        private static readonly Regex CopySuffix = new(@"\s*[-–_]?\s*(?:copy|copia)(?:\s*\(\d+\))?$", CleanOptions, CleanTimeout);
        private static readonly Regex CopyPrefix = new(@"^(?:copy of|copia de)\s+", CleanOptions, CleanTimeout);
        private static readonly Regex SecondWindowsCounter = new(@"(?<=\(\d+\))\s*\(\d+\)$", CleanOptions, CleanTimeout);

        /// <summary>
        /// Takes the leftovers of copying off the end of a name (no extension):
        /// "Ia  (1)_1" -> "Ia  (1)", "photo - Copy" -> "photo", "IMG-1" -> "IMG".
        /// One marker is removed per round, up to 5 rounds. Spaces inside the name are left
        /// alone, and a name is never emptied.
        /// </summary>
        public static string CleanCopyMarkers(string name, RenameOptions o)
        {
            string result = name;
            for (int round = 0; round < 5; round++)
            {
                string before = result;
                if (o.StripCopyWords) result = CopyPrefix.Replace(result, "");
                if (o.StripCopyWords && result == before) result = CopySuffix.Replace(result, "");
                if (o.StripUnderscoreCounter && result == before) result = UnderscoreCounter.Replace(result, "");
                if (o.StripDashCounter && result == before) result = DashCounter.Replace(result, "");
                if (o.StripWindowsCounter && result == before) result = SecondWindowsCounter.Replace(result, "");
                if (result == before || result.Trim().Length == 0) break;
            }
            return result.Trim().Length == 0 ? name : result;
        }

        public static string StripDiacritics(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static string FormatNumber(int number, int padding)
        {
            string digits = Math.Abs((long)number).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Clamp(padding, 0, 10), '0');
            return number < 0 ? "-" + digits : digits;
        }

        /// <summary>
        /// The new file name (with extension) the rules give one file - before
        /// making it safe for Windows and before clashes are sorted out.
        /// </summary>
        public static string NewNameFor(string fileName, RenameOptions o, int number, Regex? regex)
        {
            var (name, ext) = RenameText.SplitName(fileName);
            string trailing = "";

            switch (o.Method)
            {
                case RenameMethod.FindReplace:
                    name = ReplaceLiteral(name, o.FindText, o.ReplaceText ?? "", o.MatchCase, o.WholeWordOnly);
                    break;
                case RenameMethod.PrefixSuffix:
                    name = (o.Prefix ?? "") + name + (o.SuffixBeforeExtension ? o.Suffix ?? "" : "");
                    if (!o.SuffixBeforeExtension) trailing = o.Suffix ?? "";
                    break;
                case RenameMethod.NumberSequence:
                    string n = FormatNumber(number, o.Padding);
                    name = o.NumberPosition switch
                    {
                        RenameNumberPosition.Prefix => n + o.Separator + name,
                        RenameNumberPosition.ReplaceEntireName => n,
                        _ => name + o.Separator + n,
                    };
                    break;
                case RenameMethod.ReplaceName:
                    string baseName = (o.NewName ?? "").Trim();
                    name = o.WindowsStyleNumbering
                        ? $"{baseName} ({FormatNumber(number, o.Padding)})"
                        : baseName + o.Separator + FormatNumber(number, o.Padding);
                    break;
                case RenameMethod.ChangeCase:
                    name = ApplyCase(name, o.CaseStyle);
                    // The extension only follows UPPER; every other style makes it lower
                    // (the old code camel-cased ".jpg" into "jpg" and lost the dot).
                    if (o.ApplyCaseToExtension && o.ExtensionMode == RenameExtensionMode.Keep)
                        ext = o.CaseStyle == RenameCaseStyle.Upper ? ext.ToUpperInvariant() : ext.ToLowerInvariant();
                    break;
                case RenameMethod.TrimStrip:
                    name = TrimName(name, o);
                    break;
                case RenameMethod.RegexReplace:
                    if (regex != null) name = o.ReplaceAllMatches ? regex.Replace(name, o.Replacement ?? "") : regex.Replace(name, o.Replacement ?? "", 1);
                    break;
                case RenameMethod.RemoveCharacters:
                    name = RemoveCharacters(name, o);
                    break;
                case RenameMethod.CleanSuffixes:
                    name = CleanCopyMarkers(name, o);
                    break;
            }

            ext = o.ExtensionMode switch
            {
                RenameExtensionMode.Lower => ext.ToLowerInvariant(),
                RenameExtensionMode.Upper => ext.ToUpperInvariant(),
                RenameExtensionMode.Change => NormalizeExtension(o.NewExtension),
                _ => ext,
            };
            return name + ext + trailing;
        }

        /// <summary>"JPG", ".jpg", " .Jpg " -> ".Jpg"/".jpg" as typed, with one leading dot. Empty stays empty (no extension).</summary>
        public static string NormalizeExtension(string? typed)
        {
            string e = (typed ?? "").Trim().TrimStart('.').Trim();
            return e.Length == 0 ? "" : "." + e;
        }

        // =================================================================
        //  Safe Windows names
        // =================================================================

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars()
            .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

        public const int MaxNameLength = 255;

        /// <summary>
        /// Makes a name Windows will accept as it is: characters it forbids
        /// become "_", and trailing dots and spaces go (Windows drops them
        /// silently, so the file used to end up with a different name than
        /// the preview showed). Returns why it can't be used, or null.
        /// </summary>
        public static (string Name, string? Problem) MakeSafe(string fileName)
        {
            var sb = new StringBuilder(fileName.Length);
            foreach (char c in fileName) sb.Append(InvalidChars.Contains(c) || char.IsControl(c) ? '_' : c);
            string safe = sb.ToString().TrimEnd('.', ' ');

            if (safe.Trim().Length == 0 || safe.All(c => c == '.')) return (safe, Loc.T("Str_Rename_ErrEmptyName", "The new name would be empty."));
            string stem = safe.Split('.')[0].Trim();
            if (ReservedNames.Contains(stem)) return (safe, Loc.F("Str_Rename_ErrReserved", "\"{0}\" is a name Windows reserves for devices.", stem));
            if (safe.Length > MaxNameLength) return (safe, Loc.F("Str_Rename_ErrNameLong", "The new name is {0} characters - Windows allows {1}.", safe.Length, MaxNameLength));
            return (safe, null);
        }

        /// <summary>"photo.jpg" -> "photo (2).jpg".</summary>
        public static string WithCounter(string fileName, int n)
        {
            var (name, ext) = RenameText.SplitName(fileName);
            return $"{name} ({n}){ext}";
        }

        // =================================================================
        //  The plan
        // =================================================================

        /// <summary>
        /// Checks the settings themselves. A broken pattern used to mark every
        /// row "Invalid" one by one (and was compiled again for every file).
        /// </summary>
        public static string? ValidateOptions(RenameOptions o, out Regex? regex)
        {
            regex = null;
            if (o.Method == RenameMethod.ReplaceName && string.IsNullOrWhiteSpace(o.NewName))
                return Loc.T("Str_Rename_ErrTypeName", "Type the new name for the files.");
            if (o.Method == RenameMethod.RegexReplace)
            {
                if (string.IsNullOrEmpty(o.Pattern)) return Loc.T("Str_Rename_ErrTypePattern", "Type a pattern to search for.");
                try
                {
                    regex = new Regex(o.Pattern,
                        (o.RegexCaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    return Loc.F("Str_Rename_ErrPatternInvalid", "The pattern isn't valid: {0}", ex.Message);
                }
            }
            if (o.ExtensionMode == RenameExtensionMode.Change)
            {
                string e = NormalizeExtension(o.NewExtension);
                if (e.Length == 0) return Loc.T("Str_Rename_ErrTypeExtension", "Type the new extension (for example jpg).");
                if (e.Skip(1).Any(c => InvalidChars.Contains(c) || c == '.' || char.IsWhiteSpace(c)))
                    return Loc.F("Str_Rename_ErrBadExtension", "\"{0}\" can't be an extension - use letters and numbers only.", o.NewExtension.Trim());
            }
            return null;
        }

        /// <summary>"2" before "10", then case-insensitive.</summary>
        public sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new();

            public int Compare(string? a, string? b)
            {
                if (a == null || b == null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                        int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                        if (c != 0) return c;
                        i++;
                        j++;
                    }
                }
                return (a.Length - i).CompareTo(b.Length - j);
            }
        }

        /// <summary>Folder by folder, then by name or date - the order numbers are handed out in.</summary>
        public static List<RenameSourceFile> Order(IEnumerable<RenameSourceFile> files, RenameOptions o)
        {
            var byFolder = files.OrderBy(f => f.Folder, NaturalComparer.Instance);
            return ((o.Method == RenameMethod.NumberSequence || o.Method == RenameMethod.ReplaceName) && o.NumberOrder == RenameNumberOrder.DateModified
                    ? byFolder.ThenBy(f => f.Modified).ThenBy(f => f.FileName, NaturalComparer.Instance)
                    : byFolder.ThenBy(f => f.FileName, NaturalComparer.Instance))
                .ToList();
        }

        /// <summary>
        /// Works out every file's new name without touching anything.
        /// <list type="bullet">
        /// <item>Names are made safe for Windows; ones that can't be are "Can't use" with the reason.</item>
        /// <item>A name that's taken - by a file staying put, by another file in
        /// this batch or by a file on disk that isn't being renamed - gets
        /// " (2)", " (3)"... The old preview marked such files "Collision"
        /// and skipped them, even for a swap (a→b, b→a) or a chain (a→b,
        /// b→c) that works fine once done in the right order.</item>
        /// <item>A change of case only (photo.jpg → Photo.jpg) is allowed.</item>
        /// </list>
        /// </summary>
        public static RenamePlan BuildPlan(IReadOnlyList<RenameSourceFile> files, RenameOptions o, Func<string, bool>? fileExists = null, CancellationToken token = default)
        {
            fileExists ??= File.Exists;
            var ordered = Order(files, o);
            string? error = ValidateOptions(o, out var regex);

            var rows = new List<(RenameSourceFile File, string Desired, string? Problem)>(ordered.Count);
            int step = o.Increment == 0 ? 1 : o.Increment;
            int number = o.StartAt;
            string? lastFolder = null;
            bool regexTooSlow = false;

            foreach (var f in ordered)
            {
                token.ThrowIfCancellationRequested();
                if (o.NumberPerFolder && !string.Equals(lastFolder, f.Folder, StringComparison.OrdinalIgnoreCase)) number = o.StartAt;
                lastFolder = f.Folder;

                if (error != null || regexTooSlow)
                {
                    rows.Add((f, f.FileName, null));
                    continue;
                }

                string desired;
                try
                {
                    desired = NewNameFor(f.FileName, o, number, regex);
                }
                catch (RegexMatchTimeoutException)
                {
                    // One slow file means the pattern is the problem - stop rather than wait a second per file.
                    regexTooSlow = true;
                    rows.Add((f, f.FileName, null));
                    continue;
                }
                number = unchecked(number + step);

                var (safe, problem) = MakeSafe(desired);
                rows.Add((f, problem == null ? safe : desired, problem));
            }
            if (regexTooSlow) error = Loc.T("Str_Rename_ErrPatternSlow", "The pattern takes too long on these names - simplify it (for example avoid nested repeats like (a+)+).");

            // Sources that will move away free their names for others.
            var leaving = new HashSet<string>(
                rows.Where(r => r.Problem == null && !string.Equals(r.Desired, r.File.FileName, StringComparison.Ordinal))
                    .Select(r => r.File.FullPath),
                StringComparer.OrdinalIgnoreCase);

            // Names that stay: unchanged and unusable rows keep theirs.
            var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> TakenIn(string folder)
            {
                if (!taken.TryGetValue(folder, out var set)) taken[folder] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return set;
            }
            foreach (var r in rows.Where(r => !leaving.Contains(r.File.FullPath))) TakenIn(r.File.Folder).Add(r.File.FileName);

            var items = new List<RenamePreviewItem>(rows.Count);
            string? example = null;
            foreach (var (f, desired, problem) in rows)
            {
                if (problem != null)
                {
                    items.Add(Row(f, f.FileName, RenameStatus.Invalid, problem, adjusted: false));
                    continue;
                }
                if (!leaving.Contains(f.FullPath))
                {
                    items.Add(Row(f, f.FileName, RenameStatus.NoChange, Loc.T("Str_Rename_MsgNoChange", "The rules don't change this name."), adjusted: false));
                    continue;
                }

                var inFolder = TakenIn(f.Folder);
                bool Free(string name)
                {
                    if (inFolder.Contains(name)) return false;
                    string path = Path.Combine(f.Folder, name);
                    return !fileExists(path) || leaving.Contains(path);   // leaving also covers a case-only change of this file
                }

                string final = desired;
                for (int n = 2; !Free(final); n++) final = WithCounter(desired, n);
                inFolder.Add(final);

                bool adjusted = !string.Equals(final, desired, StringComparison.Ordinal);
                if (string.Equals(final, f.FileName, StringComparison.Ordinal))
                {
                    items.Add(Row(f, f.FileName, RenameStatus.NoChange, Loc.T("Str_Rename_MsgNoChange", "The rules don't change this name."), adjusted: false));
                    continue;
                }
                items.Add(Row(f, final, RenameStatus.Ready,
                    adjusted ? Loc.F("Str_Rename_MsgTakenBecomes", "{0} is already taken in this folder - this one becomes {1}.", desired, final) : Loc.F("Str_Rename_MsgBecomes", "Becomes {0}", final), adjusted));
                example ??= $"{f.FileName}  →  {final}";
            }

            return new RenamePlan { Items = items, Error = error, Example = example };
        }

        private static RenamePreviewItem Row(RenameSourceFile f, string newName, RenameStatus status, string message, bool adjusted) => new()
        {
            FullPath = f.FullPath,
            OriginalName = f.FileName,
            NewName = newName,
            Modified = f.Modified,
            IsAdjusted = adjusted,
            Status = status,
            Message = message,
        };

        /// <summary>"120 files to rename · 3 unchanged · 2 get (2)".</summary>
        public static string DescribePlan(IReadOnlyCollection<RenamePreviewItem> items)
        {
            if (items.Count == 0) return Loc.T("Str_Rename_MsgNoFiles", "No files in these folders.");
            int ready = items.Count(i => i.Status == RenameStatus.Ready);
            var parts = new List<string> { Loc.N(ready, "Str_Rename_PlanOne", "{0:N0} file to rename", "Str_Rename_PlanMany", "{0:N0} files to rename", ready) };
            int same = items.Count(i => i.Status == RenameStatus.NoChange);
            int bad = items.Count(i => i.Status == RenameStatus.Invalid);
            int adjusted = items.Count(i => i.Status == RenameStatus.Ready && i.IsAdjusted);
            if (same > 0) parts.Add(Loc.F("Str_Rename_SumUnchanged", "{0:N0} unchanged", same));
            if (adjusted > 0) parts.Add(Loc.F("Str_Rename_PlanTaken", "{0:N0} get \" (2)\" (name taken)", adjusted));
            if (bad > 0) parts.Add(Loc.F("Str_Rename_PlanBad", "{0:N0} can't use the new name", bad));
            return string.Join(" · ", parts);
        }

        public static string DescribeRun(RenameRunResult r)
        {
            var parts = new List<string> { Loc.N(r.Renamed, "Str_Rename_RunOne", "Renamed {0:N0} file in {1:0.0} s", "Str_Rename_RunMany", "Renamed {0:N0} files in {1:0.0} s", r.Renamed, r.Elapsed.TotalSeconds) };
            if (r.Failed > 0) parts.Add(Loc.F("Str_CopyQueue_RunFailed", "{0:N0} failed", r.Failed));
            if (r.Cancelled > 0) parts.Add(Loc.F("Str_Files_RunCancelled", "{0:N0} cancelled", r.Cancelled));
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  Settings + undo journal
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string AppDataFile(string name) => Path.Combine(
            AppPaths.DataRoot, name);

        private static T? Load<T>(string file) where T : class
        {
            try
            {
                return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                if (ex is JsonException or NotSupportedException) JsonSettings.KeepBadCopy(file);
                return null;
            }
        }

        private static void Save<T>(T value, string file) => AtomicFile.WriteAllText(file, JsonSerializer.Serialize(value, JsonOptions));

        public static RenameSettings LoadSettings(string? path = null)
        {
            var s = Load<RenameSettings>(path ?? AppDataFile("rename_files.json")) ?? new RenameSettings();
            var o = s.Options ??= new RenameOptions();
            if (!Enum.IsDefined(o.Method)) o.Method = RenameMethod.FindReplace;
            if (!Enum.IsDefined(o.CaseStyle)) o.CaseStyle = RenameCaseStyle.Upper;
            if (!Enum.IsDefined(o.NumberPosition)) o.NumberPosition = RenameNumberPosition.Suffix;
            if (!Enum.IsDefined(o.NumberOrder)) o.NumberOrder = RenameNumberOrder.Name;
            if (!Enum.IsDefined(o.ExtensionMode)) o.ExtensionMode = RenameExtensionMode.Keep;
            o.Padding = Math.Clamp(o.Padding, 0, 10);
            o.TrimStart = Math.Max(0, o.TrimStart);
            o.TrimEnd = Math.Max(0, o.TrimEnd);
            o.FindText ??= "";
            o.ReplaceText ??= "";
            o.Prefix ??= "";
            o.Suffix ??= "";
            o.Separator ??= "_";
            o.Pattern ??= "^(.*)$";
            o.Replacement ??= "";
            o.CharsToRemove ??= "";
            o.NewExtension ??= "";
            return s;
        }

        public static void SaveSettings(RenameSettings settings, string? path = null) =>
            Save(settings, path ?? AppDataFile("rename_files.json"));

        public static string DefaultJournalPath => AppDataFile("rename_undo.json");

        public static RenameJournal? LoadJournal(string? path = null)
        {
            var j = Load<RenameJournal>(path ?? DefaultJournalPath);
            return j is { Steps.Count: > 0 } ? j : null;
        }

        public static void SaveJournal(RenameJournal journal, string? path = null) => Save(journal, path ?? DefaultJournalPath);

        public static void DeleteJournal(string? path = null)
        {
            try
            {
                File.Delete(path ?? DefaultJournalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}
