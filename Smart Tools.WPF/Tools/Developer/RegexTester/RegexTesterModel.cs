using System.IO;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Regex Tester data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/RegexView/RegexModels.cs plus RegexBreakdownRow in
    //  the view's code-behind.)
    // =====================================================================

    public enum RegexInputMode
    {
        Text,
        File,
        Folder,
    }

    /// <summary>The pattern and its flags, read from the page.</summary>
    public sealed class RegexTestOptions
    {
        public required string Pattern { get; init; }
        public bool IgnoreCase { get; init; }
        public bool Multiline { get; init; }
        public bool Singleline { get; init; }

        /// <summary>Spaces and # comments in the pattern are ignored (x flag).</summary>
        public bool IgnoreWhitespace { get; init; }

        /// <summary>Replace preview; null = no replacement.</summary>
        public string? Replacement { get; init; }
    }

    /// <summary>One group of one match: a row in the groups grid and the CSV.</summary>
    public sealed class RegexMatchRow
    {
        public string FileName { get; init; } = "";
        public string FullPath { get; init; } = "";

        /// <summary>1-based match number within its text/file.</summary>
        public int MatchNumber { get; init; }

        public int GroupNumber { get; init; }

        /// <summary>"0 (whole match)", "1", or a named group's name.</summary>
        public string GroupName { get; init; } = "";

        public string Match { get; init; } = "";
        public string GroupValue { get; init; } = "";

        /// <summary>Character offset from the start (0-based, like .NET).</summary>
        public int Position { get; init; }

        /// <summary>1-based line and column, so a match can be found in an editor.</summary>
        public int Line { get; init; }

        public int Column { get; init; }
        public int Length { get; init; }

        public bool IsWholeMatch => GroupNumber == 0;
        public string LineColumn => $"{Line}:{Column}";
    }

    /// <summary>Where a match is in the text, for highlighting.</summary>
    public readonly record struct RegexSpan(int Start, int Length);

    /// <summary>Result of matching one block of text.</summary>
    public sealed class RegexTextResult
    {
        public IReadOnlyList<RegexMatchRow> Rows { get; init; } = Array.Empty<RegexMatchRow>();
        public IReadOnlyList<RegexSpan> Highlights { get; init; } = Array.Empty<RegexSpan>();
        public int MatchCount { get; init; }

        /// <summary>More matches than are listed (the list and highlights are capped so huge results stay quick).</summary>
        public bool Truncated { get; init; }

        public bool TimedOut { get; init; }
        public string? Error { get; init; }

        /// <summary>Where in the pattern the error is (-1 = unknown), so the view can point at it.</summary>
        public int ErrorOffset { get; init; } = -1;

        public TimeSpan Elapsed { get; init; }

        /// <summary>The replaced text when a replacement was given (null otherwise).</summary>
        public string? Replaced { get; init; }

        public bool Success => Error == null && !TimedOut;
    }

    public sealed record RegexSkippedFile(string FullPath, string Reason)
    {
        public string FileName => Path.GetFileName(FullPath);
    }

    public sealed class RegexFolderResult
    {
        public IReadOnlyList<RegexMatchRow> Rows { get; init; } = Array.Empty<RegexMatchRow>();
        public int MatchCount { get; init; }
        public int FilesScanned { get; init; }
        public int FilesWithMatches { get; init; }
        public IReadOnlyList<RegexSkippedFile> Skipped { get; init; } = Array.Empty<RegexSkippedFile>();
        public bool Truncated { get; init; }
        public bool Cancelled { get; init; }
        public string? Error { get; init; }
    }

    public readonly record struct RegexProgress(int Done, int Total, int Matches, string CurrentFile)
    {
        public double Percent => Total <= 0 ? 0 : Math.Clamp(Done * 100.0 / Total, 0, 100);
    }

    public enum RegexBreakdownKind
    {
        Literal,
        CharClass,
        Anchor,
        Group,
        Quantified,
        Alternation,
        Reference,
        Option,
        Comment,
        Problem,
    }

    /// <summary>
    /// A line of the pattern breakdown: a piece of the pattern and what it
    /// means. Depth is how deep inside groups it is, so the grid can indent
    /// a group's contents under it. Start is where the token is in the
    /// pattern (-1 if it isn't one piece of it, e.g. text split by spaces
    /// in Ignore-whitespace mode), so the view can select it.
    /// </summary>
    public sealed record RegexBreakdownRow(string Token, string Meaning, int Depth = 0, RegexBreakdownKind Kind = RegexBreakdownKind.Literal, int Start = -1)
    {
        /// <summary>The token indented by depth (for the grid's monospace column).</summary>
        public string DisplayToken => Depth == 0 ? Token : new string(' ', (Depth - 1) * 2) + "└ " + Token;

        public bool IsProblem => Kind == RegexBreakdownKind.Problem;
    }

    /// <summary>A ready-made example: name, pattern and text it matches.</summary>
    public sealed record RegexPreset(string Name, string Pattern, string Sample);

    /// <summary>
    /// Remembered between sessions (regex_tester.json in %AppData%\Smart Tools).
    /// The pattern and flags only - never the test text (it may be private)
    /// or any file/folder path.
    /// </summary>
    public sealed class RegexSettings
    {
        public string Pattern { get; set; } = "";
        public string Replacement { get; set; } = "";
        public bool IgnoreCase { get; set; }
        public bool Multiline { get; set; }
        public bool Singleline { get; set; }
        public bool IgnoreWhitespace { get; set; }
        public RegexInputMode Mode { get; set; } = RegexInputMode.Text;
        public bool IncludeSubfolders { get; set; } = true;
        public string FileFilter { get; set; } = "*.txt;*.log;*.csv;*.json;*.xml;*.md";
    }

    public static class RegexText
    {
        public static string Plural(int count, string one, string many) => count == 1 ? one : many;

        public static string Bytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }
    }
}
