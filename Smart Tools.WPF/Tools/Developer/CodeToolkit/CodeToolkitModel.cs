namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Code Toolkit data model.
    //
    //  Plain data only - no WPF, no I/O. Names carry a "Code" prefix because
    //  every tool shares the Smart_Tools.WPF.Models namespace (the old
    //  "ToolMode" / "DiffLine" were too generic to live there).
    // =====================================================================

    /// <summary>Which of the three things the toolkit is doing.</summary>
    public enum CodeToolMode
    {
        Minify,
        Beautify,
        Diff,
    }

    /// <summary>Languages the Minify/Beautify engines understand.</summary>
    public enum CodeLanguage
    {
        Css,
        Js,
        Html,
        Json,
    }

    /// <summary>Indentation used by Beautify.</summary>
    public enum CodeIndentStyle
    {
        TwoSpaces,
        FourSpaces,
        Tab,
    }

    /// <summary>Result of one Minify/Beautify run.</summary>
    public sealed class CodeTransformResult
    {
        public required string Output { get; init; }
        public CodeToolMode Mode { get; init; }
        public CodeLanguage Language { get; init; }

        public int BeforeBytes { get; init; }
        public int AfterBytes { get; init; }
        public int BeforeLines { get; init; }
        public int AfterLines { get; init; }

        /// <summary>Positive = smaller. Only meaningful for Minify.</summary>
        public double SavedPercent { get; init; }

        public TimeSpan Elapsed { get; init; }
    }

    public enum CodeDiffKind
    {
        Unchanged,
        Added,
        Removed,
    }

    /// <summary>One row of the diff view.</summary>
    public sealed class CodeDiffLine
    {
        public required string Text { get; init; }
        public CodeDiffKind Kind { get; init; }

        /// <summary>1-based line number in Original (null for added lines).</summary>
        public int? OldNumber { get; init; }

        /// <summary>1-based line number in Modified (null for removed lines).</summary>
        public int? NewNumber { get; init; }

        public string Sign => Kind switch
        {
            CodeDiffKind.Added => "+",
            CodeDiffKind.Removed => "−",
            _ => "",
        };

        public string OldNumberText => OldNumber?.ToString() ?? "";
        public string NewNumberText => NewNumber?.ToString() ?? "";
    }

    /// <summary>How lines are compared.</summary>
    public sealed class CodeDiffOptions
    {
        /// <summary>Treat runs of spaces/tabs as one, and ignore leading/trailing whitespace.</summary>
        public bool IgnoreWhitespace { get; init; }
        public bool IgnoreCase { get; init; }
    }

    /// <summary>Result of comparing two texts line by line.</summary>
    public sealed class CodeDiffResult
    {
        public List<CodeDiffLine> Lines { get; init; } = new();
        public int AddedCount { get; init; }
        public int RemovedCount { get; init; }
        public int UnchangedCount { get; init; }

        /// <summary>Index in <see cref="Lines"/> where each block of changes starts (for next/previous navigation).</summary>
        public List<int> ChangeStarts { get; init; } = new();

        public bool AreIdentical => AddedCount == 0 && RemovedCount == 0;
    }

    /// <summary>
    /// Input the engine can't process (e.g. invalid JSON). Carries the
    /// 1-based position when known so the view can jump to it.
    /// </summary>
    public sealed class CodeFormatException : Exception
    {
        public int? Line { get; }
        public int? Column { get; }

        public CodeFormatException(string message, int? line = null, int? column = null, Exception? inner = null)
            : base(message, inner)
        {
            Line = line;
            Column = column;
        }
    }
}
