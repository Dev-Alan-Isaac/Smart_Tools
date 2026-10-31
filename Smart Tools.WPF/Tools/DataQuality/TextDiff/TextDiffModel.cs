namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// What Text Diff remembers between sessions: how to compare and how to look at it. Never any of the text.
    /// The rows and highlights themselves are File Differences' own types (<see cref="FileDiffResult"/>), because
    /// both tools run the same comparison.
    /// </summary>
    public sealed class TextDiffSettings
    {
        public FileDiffViewMode ViewMode { get; set; } = FileDiffViewMode.SideBySide;
        public bool IgnoreWhitespace { get; set; }
        public bool IgnoreCase { get; set; }
        public bool OnlyChanges { get; set; }
    }

    /// <summary>What one comparison of two pieces of text found, for the pills above the list.</summary>
    public enum TextDiffVerdict
    {
        /// <summary>Both sides are empty.</summary>
        Nothing,

        /// <summary>The text is exactly the same.</summary>
        Identical,

        /// <summary>The text differs, but the ignore options make every difference not count.</summary>
        SameWithOptions,

        Different,
    }
}
