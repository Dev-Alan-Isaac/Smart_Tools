using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind BarCodeGeneratorView: validates input per symbology,
    /// drives ZXing.Net to encode, renders the human-readable label, and
    /// exports PNG / JPG / vector SVG / lossless PDF. Batch work runs on a
    /// dedicated STA thread (WPF drawing requires it). No controls, no
    /// clipboard - those stay in the view.
    ///
    /// Requires the ZXing.Net NuGet package (core only - no System.Drawing).
    /// Linear codes only: Code 39, Code 128, EAN-13, Interleaved 2 of 5.
    /// </summary>
    public sealed class BarcodeManager
    {
        private const string Code39Charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
        /// <summary>Batch rows that keep a preview picture (about 40 KB each); export re-renders every code anyway.</summary>
        internal const int BatchThumbnailRows = 5000;
        private const int ThumbnailWidth = 180;
        private const int ThumbnailHeight = 56;

        // =================================================================
        //  Validation / hints
        // =================================================================

        public static bool TryValidate(string data, BarcodeSymbology symbology, out string error)
        {
            error = "";

            if (string.IsNullOrWhiteSpace(data))
            {
                error = Loc.T("Str_Generators_ErrNoContent", "Enter some content to encode first.");
                return false;
            }

            switch (symbology)
            {
                case BarcodeSymbology.Ean13:
                    if (!data.All(char.IsAsciiDigit))
                    {
                        error = Loc.T("Str_BarcodeGen_ErrEanDigits", "EAN-13 only accepts digits (12 digits; the 13th check digit is added for you).");
                        return false;
                    }
                    if (data.Length != 12 && data.Length != 13)
                    {
                        error = Loc.F("Str_BarcodeGen_ErrEanCount", "EAN-13 needs 12 digits - you have {0}.", data.Length);
                        return false;
                    }
                    if (data.Length == 13)
                    {
                        // ZXing only says "checksum failed"; tell the user the right digit.
                        char expected = ComputeEan13CheckDigit(data[..12]);
                        if (data[12] != expected)
                        {
                            error = Loc.F("Str_BarcodeGen_ErrEanCheck", "The last digit should be {0} (EAN-13 check digit). Enter just the first 12 digits to have it added automatically.", expected);
                            return false;
                        }
                    }
                    break;

                case BarcodeSymbology.Interleaved2of5:
                    if (!data.All(char.IsAsciiDigit))
                    {
                        error = Loc.T("Str_BarcodeGen_ErrItfDigits", "Interleaved 2 of 5 only accepts digits.");
                        return false;
                    }
                    if (data.Length % 2 != 0)
                    {
                        error = Loc.T("Str_BarcodeGen_ErrItfEven", "Interleaved 2 of 5 needs an even number of digits - try adding a leading zero.");
                        return false;
                    }
                    break;

                case BarcodeSymbology.Code39:
                    char bad = data.ToUpperInvariant().FirstOrDefault(c => Code39Charset.IndexOf(c) < 0);
                    if (bad != default)
                    {
                        error = Loc.F("Str_BarcodeGen_ErrCode39", "Code 39 can't encode \"{0}\" - it supports digits, A-Z, space and - . $ / + %.", bad);
                        return false;
                    }
                    break;

                case BarcodeSymbology.Code128:
                    char nonAscii = data.FirstOrDefault(c => c > 127);
                    if (nonAscii != default)
                    {
                        error = Loc.F("Str_BarcodeGen_ErrCode128", "Code 128 can't encode \"{0}\" - only standard ASCII characters.", nonAscii);
                        return false;
                    }
                    break;
            }

            return true;
        }

        /// <summary>One-line "what can this type hold" hint for the UI.</summary>
        public static string DescribeSymbology(BarcodeSymbology symbology) => symbology switch
        {
            BarcodeSymbology.Code39 => "Digits, A-Z, space and - . $ / + %. Lowercase is converted to uppercase.",
            BarcodeSymbology.Code128 => "Any standard ASCII text. The most compact general-purpose choice.",
            BarcodeSymbology.Ean13 => "Retail products: 12 digits, check digit added automatically.",
            BarcodeSymbology.Interleaved2of5 => "Digits only, even count. Common on cartons and logistics labels.",
            _ => "",
        };

        /// <summary>Scannability warning for the colour pair, or null when fine.</summary>
        public static string? GetColorWarning(string foregroundHex, string backgroundHex) =>
            ZxingImageHelpers.GetColorWarning(foregroundHex, backgroundHex, "bars");

        // =================================================================
        //  Generation
        // =================================================================

        /// <summary>Renders one barcode. Must run on an STA thread (the UI thread, or via the batch worker).</summary>
        public BarcodeGenerationResult Generate(string data, BarcodeSymbology symbology, BarcodeOptions options)
        {
            string encoded = NormalizeForEncoding(data, symbology);

            if (!TryValidate(encoded, symbology, out string validationError))
                return Failure(data, symbology, options, validationError);

            try
            {
                // Label size scales with the barcode (it used to be a fixed 13px
                // however large the code was), within sensible limits.
                double fontSize = options.ShowText ? Math.Clamp(options.HeightPx * 0.11, 11, 40) : 0;
                int textBand = options.ShowText ? (int)Math.Ceiling(fontSize * 1.45) + 4 : 0;
                int barsHeight = Math.Max(20, options.HeightPx - textBand);

                var writer = new BarcodeWriterPixelData
                {
                    Format = ToZxingFormat(symbology),
                    Options = new EncodingOptions
                    {
                        Width = options.WidthPx,
                        Height = barsHeight,
                        Margin = options.MarginPx,
                        PureBarcode = true, // we draw the label ourselves
                    },
                };

                var pixels = writer.Write(encoded);
                BitmapSource bars = ZxingImageHelpers.PixelDataToBitmap(pixels, options.ForegroundHex, options.BackgroundHex);

                BitmapSource image = options.ShowText
                    ? ComposeWithText(bars, encoded, options, ref fontSize)
                    : bars;

                return new BarcodeGenerationResult
                {
                    Success = true,
                    Image = image,
                    BarsImage = bars,
                    EncodedText = encoded,
                    InputText = data,
                    Symbology = symbology,
                    Options = options,
                    LabelFontSize = fontSize,
                };
            }
            catch (Exception ex)
            {
                return Failure(data, symbology, options, Loc.F("Str_BarcodeGen_ErrGenerate", "Couldn't generate barcode: {0}", ex.Message));
            }
        }

        private static BarcodeGenerationResult Failure(string data, BarcodeSymbology symbology, BarcodeOptions options, string message) => new()
        {
            Success = false,
            ErrorMessage = message,
            InputText = data,
            Symbology = symbology,
            Options = options,
        };

        /// <summary>
        /// Validation, encoding and the printed label all use this one value, so
        /// they can never disagree: Code 39 is upper-cased (ZXing only accepts
        /// A-Z); a 12-digit EAN-13 gets its check digit appended.
        /// </summary>
        public static string NormalizeForEncoding(string data, BarcodeSymbology symbology)
        {
            if (string.IsNullOrEmpty(data))
                return data ?? "";

            data = data.Trim('\r', '\n');
            return symbology switch
            {
                BarcodeSymbology.Code39 => data.ToUpperInvariant(),
                BarcodeSymbology.Ean13 when data.Length == 12 && data.All(char.IsAsciiDigit) => data + ComputeEan13CheckDigit(data),
                _ => data,
            };
        }

        /// <summary>EAN-13 check digit: weights 1,3,1,3... then round up to the next multiple of 10.</summary>
        public static char ComputeEan13CheckDigit(string twelveDigits)
        {
            int sum = 0;
            for (int i = 0; i < 12; i++)
            {
                int digit = twelveDigits[i] - '0';
                sum += i % 2 == 0 ? digit : digit * 3;
            }
            return (char)('0' + (10 - sum % 10) % 10);
        }

        /// <summary>
        /// Bars + a centred label. The canvas is sized from the ACTUAL bars
        /// width - when the data needs more room than the requested width,
        /// ZXing makes the bars wider, and the old fixed-width canvas cropped
        /// them. The label shrinks to fit if it's wider than the code.
        /// </summary>
        private static BitmapSource ComposeWithText(BitmapSource bars, string text, BarcodeOptions options, ref double fontSize)
        {
            int canvasWidth = Math.Max(options.WidthPx, bars.PixelWidth);
            int canvasHeight = Math.Max(options.HeightPx, bars.PixelHeight + (int)Math.Ceiling(fontSize * 1.45) + 4);

            Color fg = ToColor(options.ForegroundHex, Colors.Black);
            Color bg = ToColor(options.BackgroundHex, Colors.White);
            var typeface = new Typeface(new System.Windows.Media.FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            FormattedText Label(double size) => new(
                text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(fg), 1.0);

            var formatted = Label(fontSize);
            double maxTextWidth = canvasWidth - 8;
            if (formatted.Width > maxTextWidth)
            {
                fontSize = Math.Max(8, fontSize * maxTextWidth / formatted.Width);
                formatted = Label(fontSize);
            }

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, canvasWidth, canvasHeight));
                double barsX = (canvasWidth - bars.PixelWidth) / 2.0;
                dc.DrawImage(bars, new Rect(barsX, 0, bars.PixelWidth, bars.PixelHeight));
                dc.DrawText(formatted, new Point((canvasWidth - formatted.Width) / 2, bars.PixelHeight + 2));
            }

            var rendered = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();
            return rendered;
        }

        private static Color ToColor(string hex, Color fallback) =>
            ColorManager.TryHexToRgb(hex, out RgbColor c) ? Color.FromRgb(c.R, c.G, c.B) : fallback;

        private static BarcodeFormat ToZxingFormat(BarcodeSymbology symbology) => symbology switch
        {
            BarcodeSymbology.Code39 => BarcodeFormat.CODE_39,
            BarcodeSymbology.Code128 => BarcodeFormat.CODE_128,
            BarcodeSymbology.Ean13 => BarcodeFormat.EAN_13,
            BarcodeSymbology.Interleaved2of5 => BarcodeFormat.ITF,
            _ => throw new ArgumentOutOfRangeException(nameof(symbology)),
        };

        // =================================================================
        //  Batch
        // =================================================================

        /// <summary>
        /// Validates and previews every input on a dedicated STA thread. Rows
        /// keep only a small thumbnail; export re-renders at full size.
        /// </summary>
        public Task<List<BatchBarcodeRow>> GenerateBatchAsync(
            IReadOnlyList<string> inputs,
            BarcodeSymbology symbology,
            BarcodeOptions options,
            IProgress<(int Done, int Total)>? progress,
            CancellationToken cancellationToken)
        {
            var thumbOptions = new BarcodeOptions
            {
                WidthPx = ThumbnailWidth,
                HeightPx = ThumbnailHeight,
                MarginPx = 2,
                ForegroundHex = options.ForegroundHex,
                BackgroundHex = options.BackgroundHex,
                ShowText = false,
            };

            return ZxingImageHelpers.RunOnStaThread(() =>
            {
                var rows = new List<BatchBarcodeRow>(inputs.Count);
                int reportEvery = Math.Max(1, inputs.Count / 200); // ~200 UI updates max

                for (int i = 0; i < inputs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string input = inputs[i];
                    var result = Generate(input, symbology, thumbOptions);
                    rows.Add(new BatchBarcodeRow
                    {
                        Index = i + 1,
                        Input = input,
                        Symbology = symbology,
                        Options = options,
                        Status = result.Success ? BarcodeRowStatus.Generated : BarcodeRowStatus.Failed,
                        Message = result.Success ? "Generated" : result.ErrorMessage,
                        Thumbnail = i < BatchThumbnailRows ? result.Image : null,   // later rows keep no picture: 100,000 thumbnails would be gigabytes
                    });

                    if ((i + 1) % reportEvery == 0 || i == inputs.Count - 1)
                        progress?.Report((i + 1, inputs.Count));
                }

                return rows;
            });
        }

        /// <summary>
        /// Re-renders every successful row at full size and writes it to the
        /// folder. Same-named entries get " (2)" instead of overwriting.
        /// Returns how many files were written.
        /// </summary>
        public Task<int> ExportBatchAsync(
            IReadOnlyList<BatchBarcodeRow> rows,
            string folder,
            BarcodeExportFormat format,
            IProgress<(int Done, int Total)>? progress,
            CancellationToken cancellationToken)
        {
            return ZxingImageHelpers.RunOnStaThread(() =>
            {
                var ok = rows.Where(r => r.Status == BarcodeRowStatus.Generated).ToList();
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string ext = ExtensionFor(format);
                int written = 0;
                int reportEvery = Math.Max(1, ok.Count / 200);

                for (int i = 0; i < ok.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = ok[i];

                    var result = Generate(row.Input, row.Symbology, row.Options);
                    if (result.Success)
                    {
                        string name = ZxingImageHelpers.SanitizeFileName(row.Input, row.Index, "barcode");
                        string path = ZxingImageHelpers.UniquePath(folder, name, ext, taken);
                        try
                        {
                            SaveToFile(result, path, format);
                            written++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // keep going - one locked file shouldn't stop the batch
                        }
                    }

                    if ((i + 1) % reportEvery == 0 || i == ok.Count - 1)
                        progress?.Report((i + 1, ok.Count));
                }

                return written;
            });
        }

        // =================================================================
        //  Export
        // =================================================================

        public static string ExtensionFor(BarcodeExportFormat format) => format switch
        {
            BarcodeExportFormat.Jpg => ".jpg",
            BarcodeExportFormat.Svg => ".svg",
            BarcodeExportFormat.Pdf => ".pdf",
            _ => ".png",
        };

        public static string FileFilterFor(BarcodeExportFormat format) => format switch
        {
            BarcodeExportFormat.Jpg => "JPEG image (*.jpg)|*.jpg",
            BarcodeExportFormat.Svg => "SVG vector (*.svg)|*.svg",
            BarcodeExportFormat.Pdf => "PDF document (*.pdf)|*.pdf",
            _ => "PNG image (*.png)|*.png",
        };

        /// <summary>Writes one result. Must run on an STA thread (encoders are WPF objects).</summary>
        public static void SaveToFile(BarcodeGenerationResult result, string filePath, BarcodeExportFormat format)
        {
            if (result.Image == null)
                throw new InvalidOperationException(Loc.T("Str_BarcodeGen_ErrNothingToExport", "Nothing to export - generate a barcode first."));

            switch (format)
            {
                case BarcodeExportFormat.Jpg:
                    AtomicFile.WriteAllBytes(filePath, ZxingImageHelpers.EncodeToBytes(result.Image, new JpegBitmapEncoder { QualityLevel = 95 }));
                    break;

                case BarcodeExportFormat.Svg:
                    AtomicFile.WriteAllText(filePath, BuildVectorSvg(result), Encoding.UTF8);
                    break;

                case BarcodeExportFormat.Pdf:
                    // Lossless: JPEG-in-PDF softened bar edges.
                    PdfLiteWriter.SaveBitmapAsPdf(result.Image, filePath);
                    break;

                default:
                    AtomicFile.WriteAllBytes(filePath, ZxingImageHelpers.EncodeToBytes(result.Image, new PngBitmapEncoder()));
                    break;
            }
        }

        /// <summary>
        /// True vector SVG: every bar is a &lt;rect&gt; traced from the rendered
        /// bars (read back from the pixels, so it matches the PNG exactly
        /// whatever ZXing version is installed), and the label is real
        /// &lt;text&gt;. Scales to any print size without blurring.
        /// </summary>
        public static string BuildVectorSvg(BarcodeGenerationResult result)
        {
            var bars = result.BarsImage ?? result.Image!;
            var options = result.Options;
            int width = result.PixelWidth, height = result.PixelHeight;
            double barsX = (width - bars.PixelWidth) / 2.0;

            var runs = TraceBars(bars, ToColor(options.ForegroundHex, Colors.Black));
            string fg = Hex(ToColor(options.ForegroundHex, Colors.Black));
            string bg = Hex(ToColor(options.BackgroundHex, Colors.White));

            var sb = new StringBuilder();
            sb.Append(FormattableString.Invariant(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" shape-rendering=\"crispEdges\">\n"));
            sb.Append($"  <rect width=\"100%\" height=\"100%\" fill=\"{bg}\" />\n");
            sb.Append($"  <g fill=\"{fg}\">\n");
            foreach (var (x, w) in runs)
                sb.Append(FormattableString.Invariant($"    <rect x=\"{barsX + x:0.##}\" y=\"0\" width=\"{w}\" height=\"{bars.PixelHeight}\" />\n"));
            sb.Append("  </g>\n");

            if (options.ShowText && result.LabelFontSize > 0)
            {
                double baseline = bars.PixelHeight + 2 + result.LabelFontSize * 0.83;
                sb.Append(FormattableString.Invariant(
                    $"  <text x=\"{width / 2.0:0.##}\" y=\"{baseline:0.##}\" font-family=\"Consolas, 'Courier New', monospace\" font-size=\"{result.LabelFontSize:0.##}\" text-anchor=\"middle\" fill=\"{fg}\">"));
                sb.Append(SecurityElement.Escape(result.EncodedText));
                sb.Append("</text>\n");
            }

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        /// <summary>Runs of foreground pixels along the middle row of the bars image: (x, width).</summary>
        private static List<(int X, int Width)> TraceBars(BitmapSource bars, Color foreground)
        {
            var bgra = bars.Format == PixelFormats.Bgra32 ? bars : new FormatConvertedBitmap(bars, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth;
            byte[] row = new byte[w * 4];
            bgra.CopyPixels(new Int32Rect(0, bgra.PixelHeight / 2, w, 1), row, w * 4, 0);

            var runs = new List<(int, int)>();
            int start = -1;
            for (int x = 0; x <= w; x++)
            {
                bool ink = x < w &&
                           row[x * 4] == foreground.B &&
                           row[x * 4 + 1] == foreground.G &&
                           row[x * 4 + 2] == foreground.R;

                if (ink && start < 0) start = x;
                else if (!ink && start >= 0) { runs.Add((start, x - start)); start = -1; }
            }
            return runs;
        }

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
