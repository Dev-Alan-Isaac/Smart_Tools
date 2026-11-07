using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Settings logic with no WPF: cleaning up extension lists, spotting an
    /// extension claimed by two categories, checking names and search
    /// patterns, and trying a pattern against sample text. The data itself
    /// lives in Modules/AppSettingsStore.cs (shared by every tool).
    /// </summary>
    public static class SettingsManager
    {
        // =================================================================
        //  Appearance
        // =================================================================

        public const string ThemeLight = "Light";
        public const string ThemeDark = "Dark";
        public const string ThemeSystem = "Match System";

        /// <summary>Known theme names; anything else (an old or hand-edited file) falls back to Light.</summary>
        public static string NormalizeTheme(string? theme) => theme?.Trim() switch
        {
            var t when string.Equals(t, ThemeDark, StringComparison.OrdinalIgnoreCase) => ThemeDark,
            var t when string.Equals(t, ThemeSystem, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "System", StringComparison.OrdinalIgnoreCase) => ThemeSystem,
            _ => ThemeLight,
        };

        public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[] { ("en", "English"), ("es", "Español") };

        public static string NormalizeLanguage(string? code) =>
            Languages.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ? code!.ToLowerInvariant() : "en";

        // =================================================================
        //  Extensions
        // =================================================================

        private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };
        private static readonly char[] BadExtensionChars = Path.GetInvalidFileNameChars().Concat(new[] { '*', '?', '/', '\\' }).Distinct().ToArray();

        /// <summary>
        /// "jpg PNG, *.gif;.webp" -> [".jpg", ".png", ".gif", ".webp"]:
        /// any separator, leading dot added, "*." accepted, lower case,
        /// duplicates dropped (first kept). Entries that can't be an
        /// extension (".", "a/b") are left out.
        /// </summary>
        public static List<string> ParseExtensions(string? raw)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (raw ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string ext = part.Trim().ToLowerInvariant();
                if (ext.StartsWith("*.", StringComparison.Ordinal)) ext = ext[1..];
                if (!ext.StartsWith('.')) ext = "." + ext;
                if (ext.Length < 2 || ext.EndsWith('.') || ext[1..].IndexOfAny(BadExtensionChars) >= 0) continue;
                if (seen.Add(ext)) result.Add(ext);
            }
            return result;
        }

        /// <summary>The stored form: ".jpg,.png".</summary>
        public static string NormalizeExtensions(string? raw) => string.Join(",", ParseExtensions(raw));

        public sealed record ExtensionMerge(string Extensions, IReadOnlyList<string> Added, IReadOnlyList<string> AlreadyThere);

        /// <summary>Adds whatever was typed ("png jpg") to a category's list.</summary>
        public static ExtensionMerge AddExtensions(string? existing, string? typed)
        {
            var current = ParseExtensions(existing);
            var added = new List<string>();
            var already = new List<string>();
            foreach (string ext in ParseExtensions(typed))
            {
                if (current.Contains(ext, StringComparer.OrdinalIgnoreCase)) already.Add(ext);
                else
                {
                    current.Add(ext);
                    added.Add(ext);
                }
            }
            return new ExtensionMerge(string.Join(",", current), added, already);
        }

        public static string RemoveExtension(string? existing, string extension) =>
            string.Join(",", ParseExtensions(existing).Where(e => !string.Equals(e, NormalizeExtensions(extension), StringComparison.OrdinalIgnoreCase)));

        /// <summary>What the add box would do, for the live hint under it.</summary>
        public static string DescribeAdd(string? existing, string? typed)
        {
            if (string.IsNullOrWhiteSpace(typed)) return "";
            var merge = AddExtensions(existing, typed);
            if (merge.Added.Count == 0 && merge.AlreadyThere.Count == 0) return Loc.T("Str_SettingsMgr_NotExtension", "That isn't a file extension.");
            var parts = new List<string>();
            if (merge.Added.Count > 0) parts.Add(Loc.F("Str_SettingsMgr_Adds", "Adds {0}", string.Join(" ", merge.Added)));
            if (merge.AlreadyThere.Count > 0) parts.Add(Loc.F("Str_SettingsMgr_AlreadyHere", "{0} already here", string.Join(" ", merge.AlreadyThere)));
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// Warnings per category (by index): an extension another category
        /// above already claims (Organize Files uses the first match, so this
        /// one never gets it), or no extensions at all.
        /// </summary>
        public static List<string> CategoryWarnings(IReadOnlyList<(string Name, string Extensions)> categories)
        {
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            foreach (var (name, extensions) in categories)
            {
                var list = ParseExtensions(extensions);
                var lost = new List<string>();
                foreach (string ext in list)
                {
                    if (owner.TryGetValue(ext, out string? first)) lost.Add($"{ext} ({first})");
                    else owner[ext] = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
                }

                if (list.Count == 0)
                    warnings.Add(string.Equals(name?.Trim(), "Other", StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : Loc.T("Str_SettingsMgr_NoExtensions", "No extensions yet - nothing will be sorted into this category."));
                else if (lost.Count > 0)
                    warnings.Add(Loc.F("Str_SettingsMgr_AlreadyUsed", "Already used by a category above, so never sorted here: {0}.", string.Join(", ", lost)));
                else
                    warnings.Add("");
            }
            return warnings;
        }

        /// <summary>Null when fine; otherwise why the name can't be used.</summary>
        public static string? ValidateCategoryName(string? name, IEnumerable<string?> otherNames)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) return Loc.T("Str_SettingsMgr_GiveName", "Give the category a name.");
            if (trimmed.Length > 60) return Loc.T("Str_SettingsMgr_NameTooLong", "Keep the name under 60 characters.");
            if (otherNames.Any(n => string.Equals(n?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
                return Loc.F("Str_SettingsMgr_CategoryExists", "There's already a category called \"{0}\".", trimmed);
            return null;
        }

        // =================================================================
        //  Search patterns
        // =================================================================

        public const string PatternRegex = "Regex";
        public const string PatternWildcard = "Wildcard";

        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        public static string NormalizePatternType(string? type) =>
            string.Equals(type, PatternWildcard, StringComparison.OrdinalIgnoreCase) ? PatternWildcard : PatternRegex;

        /// <summary>"*.txt" -> ^.*\.txt$ ("?" = one character).</summary>
        public static string WildcardToRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            foreach (char c in pattern)
            {
                sb.Append(c switch
                {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(c.ToString()),
                });
            }
            return sb.Append('$').ToString();
        }

        /// <summary>Null when the pattern is usable; otherwise a short reason.</summary>
        public static string? ValidatePattern(string? pattern, string? type)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return Loc.T("Str_SettingsMgr_EnterPattern", "Enter a pattern.");
            if (NormalizePatternType(type) == PatternWildcard)
            {
                char bad = pattern.FirstOrDefault(c => c is '<' or '>' or '|' or '"' || char.IsControl(c));
                return bad != default ? Loc.F("Str_SettingsMgr_BadWildcardChar", "'{0}' can't be in a file name wildcard.", bad) : null;
            }
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
                return null;
            }
            catch (ArgumentException ex)
            {
                return ShortRegexError(ex.Message);
            }
        }

        /// <summary>.NET's messages start with the whole pattern; keep the useful tail.</summary>
        private static string ShortRegexError(string message)
        {
            var m = Regex.Match(message, @"^Invalid pattern '.*' at offset (\d+)\. (.*)$", RegexOptions.Singleline);
            if (m.Success) return Loc.F("Str_SettingsMgr_AtCharacter", "At character {0}: {1}", m.Groups[1].Value, m.Groups[2].Value.Trim());
            int i = message.LastIndexOf(" - ", StringComparison.Ordinal);
            return (i >= 0 ? message[(i + 3)..] : message).Trim();
        }

        /// <summary>
        /// How many times the pattern matches <paramref name="text"/> (regex:
        /// anywhere; wildcard: whole lines, like file names). Null when the
        /// pattern is invalid or too slow.
        /// </summary>
        public static int? CountMatches(string? pattern, string? type, string? text)
        {
            if (ValidatePattern(pattern, type) != null || string.IsNullOrEmpty(text)) return null;
            try
            {
                if (NormalizePatternType(type) == PatternWildcard)
                {
                    var rx = new Regex(WildcardToRegex(pattern!), RegexOptions.IgnoreCase, MatchTimeout);
                    return text.Split('\n').Count(line => rx.IsMatch(line.TrimEnd('\r')));
                }
                return new Regex(pattern!, RegexOptions.None, MatchTimeout).Matches(text).Count;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        public static string DescribeMatches(string? pattern, string? type, string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (ValidatePattern(pattern, type) != null) return "";
            string summary = CountMatches(pattern, type, text) switch
            {
                null => Loc.T("Str_SettingsMgr_TooSlow", "Too slow to test"),
                0 => Loc.T("Str_SettingsMgr_NoMatch", "No match"),
                1 => Loc.T("Str_SettingsMgr_OneMatch", "1 match"),
                int n => Loc.F("Str_SettingsMgr_Matches", "{0:N0} matches", n),
            };
            string parts = DescribeSegments(pattern, type, text);
            return parts.Length == 0 ? summary : $"{summary} · {parts}";
        }

        /// <summary>The named groups of the first match, "year=2026, month=10, day=31"; empty for wildcards, no named groups, or no match.</summary>
        public static string DescribeSegments(string? pattern, string? type, string? text)
        {
            if (string.IsNullOrEmpty(text) || NormalizePatternType(type) == PatternWildcard || ValidatePattern(pattern, type) != null) return "";
            try
            {
                var rx = new Regex(pattern!, RegexOptions.CultureInvariant, MatchTimeout);
                var m = rx.Match(text);
                if (!m.Success) return "";
                var parts = rx.GetGroupNames()
                    .Where(n => !int.TryParse(n, out _) && m.Groups[n].Success && m.Groups[n].Length > 0)
                    .Select(n => $"{n}={m.Groups[n].Value}");
                return string.Join(", ", parts);
            }
            catch (RegexMatchTimeoutException)
            {
                return "";
            }
        }
    }
}
