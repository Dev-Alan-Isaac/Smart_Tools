namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  File Differences data model.
    //
    //  Plain data only - no WPF, no I/O. Every type carries a "FileDiff"
    //  prefix because all tools share the Smart_Tools.WPF.Models namespace
    //  (Code Toolkit already owns CodeDiffLine / CodeDiffKind).
    // =====================================================================

    /// <summary>What a row (or one side of a side-by-side row) shows.</summary>
    public enum FileDiffLineKind
    {
        Unchanged,
        Added,      // only in the changed file (B)
        Removed,    // only in the original file (A)
        Modified,   // side-by-side row pairing a removed line with an added one
        Gap,        // "⋯ 120 unchanged lines" - click to show them
        Filler,     // empty side of an added/removed side-by-side row
    }

    public enum FileDiffViewMode
    {
        SideBySide,
        Inline,
    }

    /// <summary>The overall verdict of one comparison.</summary>
    public enum FileDiffOutcome
    {
        Identical,              // same bytes
        Different,              // lines differ
        SameText,               // no line differences, but the bytes differ (line endings, encoding, ignore options)
        BinaryIdentical,
        BinaryDifferent,
    }

    /// <summary>Something worth saying that the line list doesn't show. The view words it.</summary>
    public enum FileDiffNote
    {
        LineEndingsDiffer,      // CRLF vs LF
        EncodingDiffers,        // UTF-8 vs UTF-16 LE...
        FinalNewlineDiffers,    // only one file ends with a line break
        IgnoredDifferences,     // the ignore options hid real differences
    }

    /// <summary>The part of the comparison options that changes what counts as "the same line".</summary>
    public sealed class FileDiffOptions
    {
        public bool IgnoreWhitespace { get; init; }
        public bool IgnoreCase { get; init; }
    }

    /// <summary>A piece of a line; IsChanged pieces get the stronger highlight.</summary>
    public sealed record FileDiffSegment(string Text, bool IsChanged);

    /// <summary>Anything that draws one line of text with optional word highlights.</summary>
    public interface IFileDiffText
    {
        FileDiffLineKind Kind { get; }
        string DisplayText { get; }
        IReadOnlyList<FileDiffSegment>? Segments { get; }
    }

    /// <summary>A row of either list: the projection (hide unchanged lines) only needs the kind.</summary>
    public interface IFileDiffRow
    {
        FileDiffLineKind Kind { get; }
    }

    /// <summary>One side of a side-by-side row.</summary>
    public sealed class FileDiffCell : IFileDiffText
    {
        public static readonly FileDiffCell Filler = new() { Kind = FileDiffLineKind.Filler };

        public FileDiffLineKind Kind { get; init; }
        public int? Number { get; init; }
        public string DisplayText { get; init; } = "";
        public IReadOnlyList<FileDiffSegment>? Segments { get; init; }

        public string NumberText => Number?.ToString() ?? "";
    }

    /// <summary>Side-by-side row: File A on the left, File B on the right.</summary>
    public sealed class FileDiffRow : IFileDiffRow
    {
        public FileDiffLineKind Kind { get; init; }
        public FileDiffCell Left { get; init; } = FileDiffCell.Filler;
        public FileDiffCell Right { get; init; } = FileDiffCell.Filler;

        // Gap rows only: which rows of the full list they stand for.
        public int GapStart { get; init; }
        public int GapCount { get; init; }
        public string GapText { get; init; } = "";
    }

    /// <summary>Inline (unified) row: removed lines, then added lines, in one column.</summary>
    public sealed class FileDiffInlineRow : IFileDiffRow, IFileDiffText
    {
        public FileDiffLineKind Kind { get; init; }
        public int? OldNumber { get; init; }
        public int? NewNumber { get; init; }
        public string Text { get; init; } = "";           // the full line (used for exports)
        public string DisplayText { get; init; } = "";    // capped for the screen
        public IReadOnlyList<FileDiffSegment>? Segments { get; init; }

        public int GapStart { get; init; }
        public int GapCount { get; init; }
        public string GapText { get; init; } = "";

        public string OldNumberText => OldNumber?.ToString() ?? "";
        public string NewNumberText => NewNumber?.ToString() ?? "";
        public string Sign => Kind switch
        {
            FileDiffLineKind.Added => "+",
            FileDiffLineKind.Removed => "−",
            _ => "",
        };
    }

    /// <summary>A file read from disk, ready to compare.</summary>
    public sealed class FileDiffDocument
    {
        public required string Path { get; init; }
        public string Name => System.IO.Path.GetFileName(Path);
        public long Size { get; init; }
        public DateTime LastWriteTime { get; init; }
        public string Hash { get; init; } = "";

        // Text files only (Lines is null for binary or too-big files).
        public string[]? Lines { get; init; }
        public string EncodingName { get; init; } = "";
        public string LineEnding { get; init; } = "";    // "CRLF", "LF", "CR", "Mixed" or "" (single line)
        public bool EndsWithNewline { get; init; }

        public bool IsBinary { get; init; }
        public bool IsTooLarge { get; init; }
        public bool IsText => Lines != null;
    }

    /// <summary>Everything one comparison found. Rows hold every line; the view projects them.</summary>
    public sealed class FileDiffResult
    {
        public FileDiffOutcome Outcome { get; init; }

        public List<FileDiffRow> Rows { get; init; } = new();
        public List<FileDiffInlineRow> InlineRows { get; init; } = new();

        public int LinesAdded { get; init; }
        public int LinesRemoved { get; init; }
        public int LinesUnchanged { get; init; }
        public int ChangeBlocks { get; init; }
        public double SimilarityPercent { get; init; }

        // Binary comparison only: where the files first differ (null = one is a prefix of the other, or identical).
        public long? FirstDifferentByte { get; init; }

        public List<FileDiffNote> Notes { get; init; } = new();

        public bool HasLineDiff => Rows.Count > 0;
    }

    /// <summary>What the view shows right now: the projected rows and where each change starts.</summary>
    public sealed class FileDiffProjection
    {
        public List<IFileDiffRow> Rows { get; init; } = new();
        public List<int> ChangeStarts { get; init; } = new();
    }

    /// <summary>Saved between sessions. Never file paths.</summary>
    public sealed class FileDiffSettings
    {
        public FileDiffViewMode ViewMode { get; set; } = FileDiffViewMode.SideBySide;
        public bool IgnoreWhitespace { get; set; }
        public bool IgnoreCase { get; set; }
        public bool OnlyChanges { get; set; }
    }
}
