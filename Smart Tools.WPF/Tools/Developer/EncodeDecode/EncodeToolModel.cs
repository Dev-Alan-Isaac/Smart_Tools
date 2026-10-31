namespace Smart_Tools.WPF.Models
{
    // Base64 & URL Encoder / Decoder - plain data only (no WPF types, no I/O).

    /// <summary>Which format the page is working in.</summary>
    public enum EncodeToolMode { Base64, Url, Html, File }

    public enum EncodeToolDirection { Encode, Decode }

    /// <summary>How text turns into bytes (and back) before Base64 or percent encoding.</summary>
    public enum EncodeToolTextEncoding { Utf8, Utf16, Latin1 }

    /// <summary>Component = everything is escaped; FullUrl = the characters that give a URL its shape stay.</summary>
    public enum EncodeToolUrlScope { Component, FullUrl }

    /// <summary>Everything one conversion needs. Built by the view from the sidebar.</summary>
    public sealed record EncodeToolOptions
    {
        public EncodeToolMode Mode { get; init; } = EncodeToolMode.Base64;
        public EncodeToolDirection Direction { get; init; } = EncodeToolDirection.Encode;
        public EncodeToolTextEncoding TextEncoding { get; init; } = EncodeToolTextEncoding.Utf8;

        // Base64
        public bool UrlSafeAlphabet { get; init; }
        public bool Padding { get; init; } = true;
        public bool WrapLines { get; init; }

        // URL
        public EncodeToolUrlScope UrlScope { get; init; } = EncodeToolUrlScope.Component;
        public bool SpaceAsPlus { get; init; }

        // HTML
        public bool HtmlAllNonAscii { get; init; }

        // File
        public bool DataUri { get; init; }
    }

    /// <summary>
    /// The outcome of one conversion. <see cref="ProblemCode"/> is empty when it worked; otherwise
    /// <see cref="ProblemIndex"/> (0-based, -1 = none) points at the offending character and
    /// <see cref="ProblemChar"/> is that character. <see cref="Notes"/> are codes for things worth
    /// knowing that are not errors (padding was missing, the bytes are not valid text ...).
    /// </summary>
    public sealed class EncodeToolResult
    {
        public bool Ok => ProblemCode.Length == 0;
        public string ProblemCode { get; init; } = "";
        public int ProblemIndex { get; init; } = -1;
        public string ProblemChar { get; init; } = "";

        /// <summary>The result as text. For decoded bytes that are not text this holds the best-effort reading.</summary>
        public string Output { get; init; } = "";

        /// <summary>The raw bytes behind the output (decoding Base64 / percent-encoding, or encoding a file).</summary>
        public byte[]? Bytes { get; init; }

        public int InputBytes { get; init; }
        public int OutputBytes { get; init; }

        /// <summary>False when decoded bytes are not valid in the chosen text encoding (or look binary).</summary>
        public bool IsText { get; init; } = true;

        /// <summary>What the decoded bytes look like ("PNG image", "PDF document") or empty.</summary>
        public string DetectedKind { get; init; } = "";

        /// <summary>MIME type from a data: URI prefix, or empty.</summary>
        public string Mime { get; init; } = "";

        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public static EncodeToolResult Fail(string code, int index = -1, string ch = "", int inputBytes = 0) =>
            new() { ProblemCode = code, ProblemIndex = index, ProblemChar = ch, InputBytes = inputBytes };
    }

    /// <summary>The setup that is remembered between sessions. Never what was typed or any file path.</summary>
    public sealed class EncodeToolSettings
    {
        public EncodeToolMode Mode { get; set; } = EncodeToolMode.Base64;
        public EncodeToolDirection Direction { get; set; } = EncodeToolDirection.Encode;
        public EncodeToolTextEncoding TextEncoding { get; set; } = EncodeToolTextEncoding.Utf8;
        public bool? UrlSafeAlphabet { get; set; }
        public bool? Padding { get; set; }
        public bool? WrapLines { get; set; }
        public EncodeToolUrlScope? UrlScope { get; set; }
        public bool? SpaceAsPlus { get; set; }
        public bool? HtmlAllNonAscii { get; set; }
        public bool? DataUri { get; set; }
        public bool? ShowHex { get; set; }
        public bool OptionsCollapsed { get; set; }
    }
}
