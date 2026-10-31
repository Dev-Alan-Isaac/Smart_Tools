namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Image Color Palette Extractor data model. Plain data only - no WPF
    //  brushes, no I/O. Colours are RgbColor (see ColorModel.cs).
    // =====================================================================

    /// <summary>How the dominant colours are found.</summary>
    public enum PaletteMethod
    {
        /// <summary>Groups similar colours around moving centres; good for natural pictures.</summary>
        KMeans,

        /// <summary>Repeatedly splits the colour box with the widest spread; fast and predictable.</summary>
        MedianCut,
    }

    /// <summary>Which related palette to build from one extracted colour.</summary>
    public enum PaletteVariantKind
    {
        LightDark,
        Complementary,
        Analogous,
        Triadic,
    }

    public enum PaletteExportFormat
    {
        HexList,
        Css,
        Json,
        Png,
    }

    /// <summary>What to extract. Saved between sessions (never the picture or its path).</summary>
    public sealed class PaletteExtractOptions
    {
        public const int MinColors = 3;
        public const int MaxColors = 16;

        public int ColorCount { get; set; } = 6;
        public PaletteMethod Method { get; set; } = PaletteMethod.KMeans;
        public bool IgnoreTransparent { get; set; } = true;
        public bool IgnoreNearWhite { get; set; }
        public bool IgnoreNearBlack { get; set; }

        public PaletteExtractOptions Clamped()
        {
            ColorCount = Math.Clamp(ColorCount, MinColors, MaxColors);
            return this;
        }
    }

    /// <summary>One colour of the result and how much of the picture it covers.</summary>
    public sealed class PaletteColor
    {
        public required int Index { get; init; }
        public required RgbColor Color { get; init; }

        /// <summary>Share of the counted pixels, 0-1.</summary>
        public required double Share { get; init; }

        /// <summary>Black or white, whichever reads better on this colour.</summary>
        public required RgbColor TextColor { get; init; }

        /// <summary>Contrast ratio of <see cref="TextColor"/> on this colour.</summary>
        public required double TextContrast { get; init; }

        public string Hex => Color.Hex;
        public string SharePercent => Share >= 0.1 ? $"{Share * 100:0}%" : $"{Share * 100:0.0}%";
    }

    public sealed class PaletteExtractResult
    {
        public required IReadOnlyList<PaletteColor> Colors { get; init; }

        /// <summary>Pixels that were counted.</summary>
        public int CountedPixels { get; init; }

        /// <summary>Pixels left out by the transparent / near-white / near-black filters.</summary>
        public int SkippedPixels { get; init; }

        /// <summary>True when the filters removed every pixel.</summary>
        public bool NothingLeft => Colors.Count == 0;
    }
}
