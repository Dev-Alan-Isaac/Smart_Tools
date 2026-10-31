using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  QR Code Generator data model.
    //
    //  Plain data only - no ZXing, no controls, no file I/O. (BitmapSource
    //  appears only as frozen, immutable images.) Same split as
    //  BarcodeModel.cs.
    // =====================================================================

    /// <summary>How much of the code can be damaged/covered and still scan.</summary>
    public enum QrErrorCorrection
    {
        Low,      // ~7%
        Medium,   // ~15%
        Quartile, // ~25%
        High,     // ~30%
    }

    /// <summary>What the single-mode input holds.</summary>
    public enum QrInputMode
    {
        Text,
        File,
    }

    public enum QrExportFormat
    {
        Png,
        Jpg,
        Svg,
        Pdf,
    }

    public enum QrRowStatus
    {
        Generated,
        Failed,
    }

    /// <summary>Quick-start content shapes the view offers as chips.</summary>
    public enum QrContentTemplate
    {
        Url,
        Wifi,
        Email,
        Phone,
        Sms,
    }

    /// <summary>Everything needed to render one QR code (single and batch share it).</summary>
    public sealed class QrCodeOptions
    {
        public int WidthPx { get; init; } = 512;
        public int HeightPx { get; init; } = 512;

        /// <summary>Quiet zone in MODULES (the QR standard asks for 4).</summary>
        public int QuietZoneModules { get; init; } = 4;

        public string ForegroundHex { get; init; } = "#000000";
        public string BackgroundHex { get; init; } = "#FFFFFF";
        public QrErrorCorrection ErrorCorrection { get; init; } = QrErrorCorrection.Medium;

        /// <summary>Optional centre logo (frozen). When set, error correction is raised to High.</summary>
        public BitmapSource? LogoImage { get; init; }

        /// <summary>Logo width as a fraction of the code's width (0.05 - 0.35).</summary>
        public double LogoScale { get; init; } = 0.22;

        public bool ShowText { get; init; }

        /// <summary>Copy with a different logo / size - used for thumbnails.</summary>
        public QrCodeOptions With(int? widthPx = null, int? heightPx = null, bool? showText = null) => new()
        {
            WidthPx = widthPx ?? WidthPx,
            HeightPx = heightPx ?? HeightPx,
            QuietZoneModules = QuietZoneModules,
            ForegroundHex = ForegroundHex,
            BackgroundHex = BackgroundHex,
            ErrorCorrection = ErrorCorrection,
            LogoImage = LogoImage,
            LogoScale = LogoScale,
            ShowText = showText ?? ShowText,
        };
    }

    /// <summary>
    /// Result of one generation. Keeps the module grid and layout so SVG
    /// export can write true vector squares instead of wrapping a PNG.
    /// </summary>
    public sealed class QrCodeGenerationResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";

        /// <summary>Final image: code + optional logo + optional label.</summary>
        public BitmapSource? Image { get; init; }

        public string InputText { get; init; } = "";
        public QrCodeOptions Options { get; init; } = new();

        /// <summary>Error correction actually used (a logo forces High).</summary>
        public QrErrorCorrection EffectiveErrorCorrection { get; init; }

        /// <summary>QR version 1-40 (size class of the symbol).</summary>
        public int Version { get; init; }

        /// <summary>Dark modules, [row, column], without the quiet zone.</summary>
        public bool[,]? Modules { get; init; }

        /// <summary>Side of one module in pixels, and where module (0,0) starts.</summary>
        public int ModulePx { get; init; }
        public int OriginX { get; init; }
        public int OriginY { get; init; }

        /// <summary>Logo rectangle in image pixels (empty when no logo).</summary>
        public double LogoX { get; init; }
        public double LogoY { get; init; }
        public double LogoSize { get; init; }

        /// <summary>Label as printed (may be shortened), its font size and text baseline in pixels.</summary>
        public string LabelText { get; init; } = "";
        public double LabelFontSize { get; init; }
        public double LabelBaseline { get; init; }

        /// <summary>What the content looks like ("Link", "Wi-Fi network", ...).</summary>
        public string ContentKind { get; init; } = "";

        public int ModuleCount => Modules?.GetLength(0) ?? 0;
        public int PixelWidth => Image?.PixelWidth ?? 0;
        public int PixelHeight => Image?.PixelHeight ?? 0;
    }

    /// <summary>
    /// One batch result row. Holds a small thumbnail only; export re-renders
    /// each code at full size, so 10,000 rows stay light on memory.
    /// </summary>
    public sealed class BatchQrCodeRow
    {
        public required int Index { get; init; }
        public required string Input { get; init; }
        public required QrCodeOptions Options { get; init; }
        public required QrRowStatus Status { get; init; }

        /// <summary>"Generated" or the reason it failed.</summary>
        public string Message { get; init; } = "";
        public BitmapSource? Thumbnail { get; init; }

        /// <summary>e.g. "v3 · 29×29".</summary>
        public string SizeInfo { get; init; } = "";
    }

    public static class QrNames
    {
        public static string Display(QrErrorCorrection level) => level switch
        {
            QrErrorCorrection.Low => "Low (~7%)",
            QrErrorCorrection.Medium => "Medium (~15%)",
            QrErrorCorrection.Quartile => "Quartile (~25%)",
            QrErrorCorrection.High => "High (~30%)",
            _ => level.ToString(),
        };
    }
}
