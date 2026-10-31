namespace Smart_Tools.WPF.Models
{
    public enum DataFormatKind { Json, Yaml, Xml }

    public enum DataFormatterAction { Pretty, Minify }

    /// <summary>One indent choice: spaces, or a tab when <see cref="Spaces"/> is 0.</summary>
    public enum DataFormatterIndent { TwoSpaces, FourSpaces, Tab }

    /// <summary>The outcome of formatting, minifying, or converting. On failure <see cref="Message"/> says what is wrong and where.</summary>
    public sealed class DataFormatterResult
    {
        public bool Ok { get; init; }

        public string Text { get; init; } = "";

        /// <summary>Plain-language error (the parser's own wording), without the position.</summary>
        public string Message { get; init; } = "";

        /// <summary>1-based line and column of the error, or 0 when the parser didn't say.</summary>
        public int Line { get; init; }

        public int Column { get; init; }

        /// <summary>The format the input was read as.</summary>
        public DataFormatKind Source { get; init; }

        public static DataFormatterResult Fail(DataFormatKind source, string message, int line = 0, int column = 0) =>
            new() { Ok = false, Source = source, Message = message, Line = line, Column = column };
    }
}
