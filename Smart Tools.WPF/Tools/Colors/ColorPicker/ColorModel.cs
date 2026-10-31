namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Color Picker + Color Wheel data model (shared by both tools, and by
    //  the Barcode / QR generators for hex parsing).
    //
    //  Plain data only - no WPF brushes, no I/O. Views turn Hex strings into
    //  brushes themselves (WPF converts "#RRGGBB" to a Brush in bindings).
    // =====================================================================

    /// <summary>An sRGB color, 0-255 per channel.</summary>
    public readonly record struct RgbColor(byte R, byte G, byte B)
    {
        public string Hex => $"#{R:X2}{G:X2}{B:X2}";
        public override string ToString() => Hex;
    }

    /// <summary>H 0-360, S 0-100, L 0-100.</summary>
    public readonly record struct HslColor(double H, double S, double L);

    /// <summary>H 0-360, S 0-1, V 0-1 - what the picker's square + hue bar work in.</summary>
    public readonly record struct HsvColor(double H, double S, double V);

    /// <summary>Matches RadioModeSingle / RadioModeBatch.</summary>
    public enum ColorInputMode
    {
        Single,
        Batch,
    }

    public enum ColorExportFormat
    {
        Csv,
        Json,
        Xml,
    }

    public enum ColorRowStatus
    {
        Converted,
        Failed,
    }

    /// <summary>One row of Batch mode's results grid / export.</summary>
    public sealed class BatchColorRow
    {
        public required int Index { get; init; }
        public required string Input { get; init; }
        public ColorRowStatus Status { get; init; }

        public string Hex { get; init; } = "";
        public string Rgb { get; init; } = "";
        public string Hsl { get; init; } = "";

        /// <summary>"Converted", or why the input couldn't be read.</summary>
        public string Message { get; init; } = "";

        /// <summary>Bound by the grid's swatch cell; transparent when the row failed.</summary>
        public string SwatchHex => Status == ColorRowStatus.Converted ? Hex : "#00000000";
    }

    /// <summary>WCAG 2.x contrast between two colors.</summary>
    public readonly record struct ContrastResult(double Ratio)
    {
        /// <summary>Normal text: 4.5:1.</summary>
        public bool PassesAA => Ratio >= 4.5;

        /// <summary>Normal text: 7:1.</summary>
        public bool PassesAAA => Ratio >= 7.0;

        /// <summary>Large text (18pt+, or 14pt bold) and UI parts: 3:1.</summary>
        public bool PassesAALarge => Ratio >= 3.0;

        public string Grade => PassesAAA ? "AAA" : PassesAA ? "AA" : PassesAALarge ? "AA Large" : "Fail";
    }

    /// <summary>Recent Colors / Favorites as persisted to color_history.json.</summary>
    public sealed class ColorHistory
    {
        public List<string> Recent { get; set; } = new();
        public List<string> Favorites { get; set; } = new();
    }

    /// <summary>Family of a wedge on Itten's 12-part wheel.</summary>
    public enum WheelTier
    {
        Primary,
        Secondary,
        Tertiary,
    }

    /// <summary>One wedge of the 12-hue Itten wheel.</summary>
    public sealed class WheelSwatch
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required RgbColor Color { get; init; }
        public WheelTier Tier { get; init; }

        /// <summary>Position on the artist's (RYB) wheel, 0-330 in 30-degree steps.</summary>
        public double WheelAngle { get; init; }

        public string Hex => Color.Hex;
        public string TierName => Tier.ToString();
    }

    /// <summary>Classic color-scheme rules, measured on the artist's wheel.</summary>
    public enum ColorHarmony
    {
        Complementary,
        Analogous,
        Triadic,
        SplitComplementary,
        Tetradic,
    }

    /// <summary>How a two-color gradient travels between its end points.</summary>
    public enum GradientBlend
    {
        /// <summary>Around the hue wheel - stays vivid.</summary>
        Hsl,

        /// <summary>Straight line through RGB - can pass through grey.</summary>
        Rgb,
    }
}
