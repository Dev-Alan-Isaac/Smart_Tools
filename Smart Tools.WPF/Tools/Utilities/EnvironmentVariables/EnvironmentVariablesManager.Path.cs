using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Pure helpers for variable names and PATH-style lists. No registry, no WPF; the folder check is passed in so it can be tested.</summary>
    public sealed partial class EnvironmentVariablesManager
    {
        public const int MaxNameLength = 255;
        public const int MaxValueLength = 32767;

        private static readonly Regex ExpandToken = new(@"%[^%\r\n]+%", RegexOptions.Compiled);

        public static EnvNameProblem CheckName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return EnvNameProblem.Empty;
            if (name.Length > MaxNameLength) return EnvNameProblem.TooLong;
            // '=' starts a hidden variable in the environment block; control characters and surrounding spaces cause trouble in shells.
            if (name.Contains('=') || name.Any(char.IsControl) || name != name.Trim()) return EnvNameProblem.Illegal;
            return EnvNameProblem.None;
        }

        /// <summary>True when the value refers to another variable (%SystemRoot%), so it should be stored as expandable.</summary>
        public static bool HasExpandToken(string value) => ExpandToken.IsMatch(value);

        /// <summary>True for the variable that holds the program search folders.</summary>
        public static bool IsPathVariable(string name) => name.Equals("Path", StringComparison.OrdinalIgnoreCase);

        /// <summary>Splits on ';'. Empty pieces are kept so a stray ';' shows up as an Empty entry instead of vanishing silently.</summary>
        public static List<string> SplitPath(string value) => value.Split(';').Select(p => p.Trim()).ToList();

        public static string JoinPath(IEnumerable<string> entries) => string.Join(';', entries);

        /// <summary>Marks each folder: empty, a repeat of an earlier one, not an absolute path, or not on disk. The first of several duplicates is fine.</summary>
        public static List<EnvPathEntry> Analyze(IEnumerable<string> entries, Func<string, bool> directoryExists)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<EnvPathEntry>();
            foreach (string entry in entries)
            {
                var issue = EnvPathIssue.None;
                string expanded = Normalize(entry);
                if (expanded.Length == 0) issue = EnvPathIssue.Empty;
                else if (!seen.Add(expanded)) issue = EnvPathIssue.Duplicate;
                else if (!Path.IsPathRooted(expanded) && !expanded.Contains('%')) issue = EnvPathIssue.Relative;
                else if (expanded.Contains('%') || !directoryExists(expanded)) issue = EnvPathIssue.Missing;   // an unresolved %VAR% can't point anywhere
                result.Add(new EnvPathEntry { Path = entry, Issue = issue });
            }
            return result;
        }

        /// <summary>Drops empty, duplicate, and missing folders. Relative ones stay: whether they are wanted depends on where a program starts.</summary>
        public static List<string> CleanUp(IEnumerable<string> entries, Func<string, bool> directoryExists) =>
            Analyze(entries, directoryExists)
                .Where(e => e.Issue is EnvPathIssue.None or EnvPathIssue.Relative)
                .Select(e => e.Path)
                .ToList();

        /// <summary>The folder with %VARS% replaced, quotes removed, and no trailing slash, for comparing.</summary>
        public static string Normalize(string entry)
        {
            string text = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).Trim();
            return text.Length > 3 ? text.TrimEnd('\\', '/') : text;   // keep "C:\" intact
        }
    }
}
