namespace Smart_Tools.WPF.Models
{
    /// <summary>Background behind the SVG in the preview.</summary>
    public enum SvgBackground { Light, Dark, Checker }

    /// <summary>Which version of the file is shown.</summary>
    public enum SvgVersion { Original, Optimized }

    /// <summary>Preview (rendered) or Code (the text).</summary>
    public enum SvgPane { Preview, Code }

    /// <summary>How the preview is sized.</summary>
    public enum SvgZoomMode { Fit, Actual }

    /// <summary>What the optimizer may remove or rewrite. Every option keeps the picture the same.</summary>
    public sealed class SvgOptimizeOptions
    {
        public bool StripComments { get; set; } = true;
        /// <summary>&lt;metadata&gt;, editor namespaces (Inkscape, Sodipodi, Illustrator...), XML declaration, DOCTYPE.</summary>
        public bool StripMetadata { get; set; } = true;
        /// <summary>&lt;title&gt; and &lt;desc&gt; (they are read by screen readers, so off by default).</summary>
        public bool StripTitleDesc { get; set; }
        public bool RoundNumbers { get; set; } = true;
        /// <summary>Digits kept after the decimal point when rounding (0-6).</summary>
        public int Precision { get; set; } = 3;
        public bool RemoveUnusedDefs { get; set; } = true;
        public bool RemoveEmptyElements { get; set; } = true;
        public bool RemoveUnusedIds { get; set; } = true;
        /// <summary>Join neighbouring shapes painted the same way into one &lt;path&gt; (only where they don't overlap).</summary>
        public bool MergePaths { get; set; }
        public bool ShortenColors { get; set; } = true;
        public bool RemoveDefaultAttributes { get; set; } = true;
        /// <summary>&lt;script&gt; elements, on* handlers and javascript: links.</summary>
        public bool RemoveScripts { get; set; }
        /// <summary>Collapse whitespace and drop indentation. Off keeps the file readable.</summary>
        public bool Minify { get; set; } = true;

        public SvgOptimizeOptions Clone() => (SvgOptimizeOptions)MemberwiseClone();
    }

    /// <summary>What the optimizer did, for the summary line.</summary>
    public sealed record SvgOptimizeStats(
        int CommentsRemoved,
        int MetadataRemoved,
        int DefsRemoved,
        int EmptyRemoved,
        int IdsRemoved,
        int ScriptsRemoved,
        int NumbersRounded,
        int ShapesMerged = 0);

    public sealed record SvgOptimizeResult(string Text, int Bytes, SvgOptimizeStats Stats);

    /// <summary>Facts read from the SVG text.</summary>
    /// <param name="PixelWidth">Intrinsic width in CSS pixels (width attribute, else the view box, else 300 like a browser).</param>
    /// <param name="PixelHeight">Intrinsic height in CSS pixels (same fallbacks, else 150).</param>
    public sealed record SvgInfo(
        double PixelWidth,
        double PixelHeight,
        string? ViewBox,
        int Elements,
        int Paths,
        bool HasScripts,
        bool HasExternalRefs,
        bool HasEmbeddedImages);

    /// <summary>A file that was loaded.</summary>
    public sealed record SvgDocument(string Path, string Text, long Bytes, SvgInfo Info);

    public sealed class SvgToolSettings
    {
        public SvgBackground Background { get; set; } = SvgBackground.Checker;
        public SvgOptimizeOptions Optimize { get; set; } = new();
        /// <summary>PNG export: width in pixels (height follows the aspect ratio).</summary>
        public int ExportWidth { get; set; } = 1024;
        public bool ExportTransparent { get; set; } = true;
        public bool SidebarCollapsed { get; set; }
    }
}
