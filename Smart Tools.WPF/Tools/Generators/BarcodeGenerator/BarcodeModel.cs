using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Barcode Generator data model.
    //
    //  Plain data only - no ZXing, no controls, no file I/O. (BitmapSource
    //  appears here only as a frozen, immutable result image.) Same split
    //  as the other tools' *Model.cs files.
    // =====================================================================

    /// <summary>Linear symbologies supported by the generator.</summary>
    public enum BarcodeSymbology
    {
        Code39,
        Code128,
        Ean13,
        Interleaved2of5,
    }

    /// <summary>What the single-mode input field holds.</summary>
    public enum BarcodeInputMode
    {
        Text,
        Numeric,
        File,
    }

    public enum BarcodeExportFormat
    {
        Png,
        Jpg,
        Svg,
        Pdf,
    }

    public enum BarcodeRowStatus
    {
        Generated,
        Failed,
    }

    /// <summary>Everything needed to render one barcode (single and batch share it).</summary>
    public sealed class BarcodeOptions
    {
        public int WidthPx { get; init; } = 320;
        public int HeightPx { get; init; } = 140;
        public int MarginPx { get; init; } = 8;
        public string ForegroundHex { get; init; } = "#000000";
        public string BackgroundHex { get; init; } = "#FFFFFF";
        public bool ShowText { get; init; } = true;
    }

    /// <summary>
    /// Result of one generation. Besides the final image it keeps the parts
    /// needed to write a TRUE vector SVG (bars image + label layout), so
    /// SVG export no longer just wraps a PNG.
    /// </summary>
    public sealed class BarcodeGenerationResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";

        /// <summary>Final image: bars + optional label, on the background colour.</summary>
        public BitmapSource? Image { get; init; }

        /// <summary>Bars only (no label) - used to trace vector bars for SVG.</summary>
        public BitmapSource? BarsImage { get; init; }

        /// <summary>The value actually encoded (e.g. EAN-13 with its check digit added).</summary>
        public string EncodedText { get; init; } = "";
        public string InputText { get; init; } = "";

        public BarcodeSymbology Symbology { get; init; }
        public BarcodeOptions Options { get; init; } = new();

        /// <summary>Label font size used (0 when ShowText is off).</summary>
        public double LabelFontSize { get; init; }

        public int PixelWidth => Image?.PixelWidth ?? 0;
        public int PixelHeight => Image?.PixelHeight ?? 0;
    }

    /// <summary>
    /// One batch result row. Holds a small thumbnail, not the full image:
    /// 10,000 full-size rows used to cost ~1.8 GB of memory. Export
    /// re-renders each code at full size on the fly instead.
    /// </summary>
    public sealed class BatchBarcodeRow
    {
        public required int Index { get; init; }
        public required string Input { get; init; }
        public required BarcodeSymbology Symbology { get; init; }
        public required BarcodeOptions Options { get; init; }
        public required BarcodeRowStatus Status { get; init; }

        /// <summary>"Generated" or the reason it failed.</summary>
        public string Message { get; init; } = "";
        public BitmapSource? Thumbnail { get; init; }

        public string SymbologyName => BarcodeNames.Display(Symbology);
    }

    public static class BarcodeNames
    {
        public static string Display(BarcodeSymbology symbology) => symbology switch
        {
            BarcodeSymbology.Code39 => "Code 39",
            BarcodeSymbology.Code128 => "Code 128",
            BarcodeSymbology.Ean13 => "EAN-13",
            BarcodeSymbology.Interleaved2of5 => "ITF",
            _ => symbology.ToString(),
        };
    }
}
