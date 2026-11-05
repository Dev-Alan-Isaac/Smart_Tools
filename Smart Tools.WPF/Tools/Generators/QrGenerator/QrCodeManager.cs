using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.QrCode.Internal;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using QrEncoder = ZXing.QrCode.Internal.Encoder; // System.Text also has an Encoder

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind QrCodeGeneratorView. Encodes with ZXing.Net's QR
    /// encoder and renders the module grid itself, which gives:
    ///  - pixel-perfect output (whole-pixel modules, no resampling blur),
    ///  - true vector SVG (one &lt;rect&gt; per run of dark modules),
    ///  - a centre logo on a clean pad (error correction forced to High),
    ///  - an optional caption under the code.
    /// Anything that draws text or a logo uses WPF drawing, so Generate must
    /// run on an STA thread; batch work uses ZxingImageHelpers.RunOnStaThread.
    /// No controls and no clipboard - those stay in the view.
    /// </summary>
    public sealed class QrCodeManager
    {
        public const int MaxDimensionPx = 4000;
        public const int MaxFileBytes = 4096;
        private const int ThumbnailSize = 64;
        private const int MaxLabelChars = 48;

        // =================================================================
        //  Content helpers
        // =================================================================

        /// <summary>Starter text for the quick-start chips.</summary>
        public static string TemplateFor(QrContentTemplate template) => template switch
        {
            QrContentTemplate.Url => "https://example.com",
            QrContentTemplate.Wifi => "WIFI:T:WPA;S:NetworkName;P:Password;;",
            QrContentTemplate.Email => "mailto:name@example.com?subject=Hello&body=Hi%20there",
            QrContentTemplate.Phone => "tel:+15551234567",
            QrContentTemplate.Sms => "SMSTO:+15551234567:Hello!",
            _ => "",
        };

        /// <summary>Friendly name for what a phone will do with this content.</summary>
        public static string DescribeContent(string data)
        {
            string d = data.TrimStart();
            if (d.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                d.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                d.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                return "Link";
            if (d.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return "Wi-Fi network";
            if (d.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                d.StartsWith("MATMSG:", StringComparison.OrdinalIgnoreCase)) return "Email";
            if (d.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return "Phone number";
            if (d.StartsWith("SMSTO:", StringComparison.OrdinalIgnoreCase) ||
                d.StartsWith("sms:", StringComparison.OrdinalIgnoreCase)) return "Text message";
            if (d.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) return "Contact card";
            if (d.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) return "Calendar event";
            if (d.StartsWith("geo:", StringComparison.OrdinalIgnoreCase)) return "Location";
            return "Text";
        }

        /// <summary>
        /// Reads a small text file (a .vcf contact, a .txt note...) so its
        /// CONTENTS are encoded. QR codes hold ~3 KB at most, so larger or
        /// binary files are refused with a clear reason.
        /// </summary>
        public static bool TryReadFileForEncoding(string path, out string text, out string error)
        {
            text = "";
            error = "";

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    error = Loc.T("Str_QrGen_ErrFileGone", "That file no longer exists.");
                    return false;
                }
                if (info.Length == 0)
                {
                    error = Loc.T("Str_QrGen_ErrFileEmpty", "That file is empty.");
                    return false;
                }
                if (info.Length > MaxFileBytes)
                {
                    error = $"That file is {info.Length / 1024.0:0.#} KB - a QR code holds about 3 KB at most. Try a short text or .vcf file, or encode a link to the file instead.";
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                {
                    error = Loc.T("Str_QrGen_ErrBinary", "That looks like a binary file - QR codes can only hold text here. Encode a link to the file instead.");
                    return false;
                }

                text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Couldn't read the file: {ex.Message}";
                return false;
            }
        }

        /// <summary>Scannability warning for the colour pair, or null when fine.</summary>
        public static string? GetColorWarning(string foregroundHex, string backgroundHex) =>
            ZxingImageHelpers.GetColorWarning(foregroundHex, backgroundHex, "squares");

        // =================================================================
        //  Generation
        // =================================================================

        /// <summary>Renders one QR code. Must run on an STA thread (the UI thread, or the batch worker).</summary>
        public QrCodeGenerationResult Generate(string data, QrCodeOptions options)
        {
            if (string.IsNullOrEmpty(data) || data.Trim().Length == 0)
                return Failure(data, options, Loc.T("Str_Generators_ErrNoContent", "Enter some content to encode first."));

            if (options.WidthPx <= 0 || options.HeightPx <= 0 || options.WidthPx > MaxDimensionPx || options.HeightPx > MaxDimensionPx)
                return Failure(data, options, Loc.F("Str_QrGen_ErrSize", "Width and height must be between 1 and {0} px.", MaxDimensionPx));

            QrErrorCorrection level = options.LogoImage != null ? QrErrorCorrection.High : options.ErrorCorrection;

            QRCode code;
            try
            {
                var hints = new Dictionary<EncodeHintType, object>();
                if (data.Any(c => c > 127))
                    hints[EncodeHintType.CHARACTER_SET] = "UTF-8"; // emoji, accents, non-Latin scripts

                code = QrEncoder.encode(data, ToZxingLevel(level), hints);
            }
            catch (Exception)
            {
                int bytes = Encoding.UTF8.GetByteCount(data);
                string hint = level == QrErrorCorrection.Low
                    ? Loc.T("Str_QrGen_ErrHintLow", "Shorten the content, or encode a link to it instead.")
                    : options.LogoImage != null
                        ? Loc.T("Str_QrGen_ErrHintLogo", "Shorten the content or choose a lower error-correction level (remove the logo first - it forces High).")
                        : Loc.T("Str_QrGen_ErrHintLevel", "Shorten the content or choose a lower error-correction level.");
                return Failure(data, options, Loc.F("Str_QrGen_ErrTooMuch", "Too much content for one QR code ({0:N0} bytes). {1}", bytes, hint));
            }

            try
            {
                bool[,] modules = ToModuleGrid(code.Matrix);
                return Render(data, options, level, code.Version.VersionNumber, modules);
            }
            catch (Exception ex)
            {
                return Failure(data, options, Loc.F("Str_QrGen_ErrGenerate", "Couldn't generate the QR code: {0}", ex.Message));
            }
        }

        private static QrCodeGenerationResult Failure(string data, QrCodeOptions options, string message) => new()
        {
            Success = false,
            ErrorMessage = message,
            InputText = data ?? "",
            Options = options,
        };

        private static ErrorCorrectionLevel ToZxingLevel(QrErrorCorrection level) => level switch
        {
            QrErrorCorrection.Low => ErrorCorrectionLevel.L,
            QrErrorCorrection.Quartile => ErrorCorrectionLevel.Q,
            QrErrorCorrection.High => ErrorCorrectionLevel.H,
            _ => ErrorCorrectionLevel.M,
        };

        private static bool[,] ToModuleGrid(ByteMatrix matrix)
        {
            int n = matrix.Width;
            var grid = new bool[n, n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    grid[y, x] = matrix[x, y] == 1;
            return grid;
        }

        /// <summary>
        /// Lays out code, logo and label, then draws. Modules are whole
        /// pixels (the code is centred in the requested size), so edges stay
        /// razor sharp at any size.
        /// </summary>
        private static QrCodeGenerationResult Render(string data, QrCodeOptions options, QrErrorCorrection level, int version, bool[,] modules)
        {
            int n = modules.GetLength(0);
            int quiet = Math.Clamp(options.QuietZoneModules, 0, 20);
            int totalModules = n + 2 * quiet;

            Color fg = ToColor(options.ForegroundHex, Colors.Black);
            Color bg = ToColor(options.BackgroundHex, Colors.White);

            // ---- label band ----
            string labelText = "";
            double fontSize = 0;
            int band = 0;
            if (options.ShowText)
            {
                string firstLine = data.Split('\n')[0].TrimEnd('\r');
                labelText = firstLine.Length > MaxLabelChars ? firstLine[..(MaxLabelChars - 1)] + "…" : firstLine;
                fontSize = Math.Clamp(options.WidthPx * 0.045, 11, 28);
                band = (int)Math.Ceiling(fontSize * 1.45) + 6;
            }

            // ---- code area ----
            int side = Math.Min(options.WidthPx, options.HeightPx - band);
            int modulePx = Math.Max(1, side / totalModules);
            int symbolPx = totalModules * modulePx;

            int canvasW = Math.Max(options.WidthPx, symbolPx);
            int canvasH = Math.Max(options.HeightPx, symbolPx + band);

            int symbolX = (canvasW - symbolPx) / 2;
            int symbolY = (canvasH - band - symbolPx) / 2;
            int originX = symbolX + quiet * modulePx;
            int originY = symbolY + quiet * modulePx;

            BitmapSource image = DrawModules(modules, canvasW, canvasH, originX, originY, modulePx, fg, bg);

            // ---- logo ----
            double logoX = 0, logoY = 0, logoSize = 0;
            if (options.LogoImage != null)
            {
                // Snap to whole modules, and keep it odd so it centres on the grid.
                int logoModules = Math.Max(3, (int)Math.Round(n * Math.Clamp(options.LogoScale, 0.05, 0.35)));
                if (logoModules % 2 == 0) logoModules++;
                logoSize = logoModules * modulePx;
                logoX = originX + (n - logoModules) / 2.0 * modulePx;
                logoY = originY + (n - logoModules) / 2.0 * modulePx;
            }

            // ---- label ----
            double baseline = 0;
            FormattedText? label = null;
            double labelY = symbolY + symbolPx + 2;
            if (options.ShowText && labelText.Length > 0)
            {
                label = MakeLabel(labelText, fontSize, fg);
                double maxWidth = canvasW - 12;
                if (label.Width > maxWidth)
                {
                    fontSize = Math.Max(8, fontSize * maxWidth / label.Width);
                    label = MakeLabel(labelText, fontSize, fg);
                }
                // Tuck the caption into the lower half of the quiet zone when there is one.
                labelY -= Math.Min(quiet * modulePx / 2.0, fontSize * 0.5);
                baseline = labelY + label.Baseline;
            }

            if (options.LogoImage != null || label != null)
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawImage(image, new Rect(0, 0, canvasW, canvasH));

                    if (options.LogoImage != null)
                    {
                        double pad = modulePx;
                        var padRect = new Rect(logoX - pad, logoY - pad, logoSize + 2 * pad, logoSize + 2 * pad);
                        dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, padRect, modulePx * 1.5, modulePx * 1.5);
                        dc.DrawImage(options.LogoImage, FitUniform(options.LogoImage, new Rect(logoX, logoY, logoSize, logoSize)));
                    }

                    if (label != null)
                        dc.DrawText(label, new Point((canvasW - label.Width) / 2, labelY));
                }

                var rendered = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(visual);
                rendered.Freeze();
                image = rendered;
            }

            return new QrCodeGenerationResult
            {
                Success = true,
                Image = image,
                InputText = data,
                Options = options,
                EffectiveErrorCorrection = level,
                Version = version,
                Modules = modules,
                ModulePx = modulePx,
                OriginX = originX,
                OriginY = originY,
                LogoX = logoX,
                LogoY = logoY,
                LogoSize = logoSize,
                LabelText = label != null ? labelText : "",
                LabelFontSize = label != null ? fontSize : 0,
                LabelBaseline = baseline,
                ContentKind = DescribeContent(data),
            };
        }

        /// <summary>Fills a Bgra32 buffer directly - fast and free of anti-aliasing.</summary>
        private static BitmapSource DrawModules(bool[,] modules, int width, int height, int originX, int originY, int modulePx, Color fg, Color bg)
        {
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = bg.B; pixels[i + 1] = bg.G; pixels[i + 2] = bg.R; pixels[i + 3] = 255;
            }

            int n = modules.GetLength(0);
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    if (!modules[r, c]) continue;

                    int x0 = originX + c * modulePx, y0 = originY + r * modulePx;
                    for (int y = y0; y < y0 + modulePx; y++)
                    {
                        int offset = y * stride + x0 * 4;
                        for (int x = 0; x < modulePx; x++, offset += 4)
                        {
                            pixels[offset] = fg.B; pixels[offset + 1] = fg.G; pixels[offset + 2] = fg.R; pixels[offset + 3] = 255;
                        }
                    }
                }
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static FormattedText MakeLabel(string text, double size, Color fg)
        {
            var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            return new FormattedText(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                typeface, size, new SolidColorBrush(fg), 1.0);
        }

        /// <summary>Largest rect with the image's aspect ratio centred inside <paramref name="box"/>.</summary>
        private static Rect FitUniform(BitmapSource image, Rect box)
        {
            double ratio = image.PixelWidth / (double)Math.Max(1, image.PixelHeight);
            double w = box.Width, h = box.Height;
            if (ratio >= 1) h = w / ratio; else w = h * ratio;
            return new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }

        private static Color ToColor(string hex, Color fallback) =>
            ColorManager.TryHexToRgb(hex, out RgbColor c) ? Color.FromRgb(c.R, c.G, c.B) : fallback;

        // =================================================================
        //  Batch
        // =================================================================

        /// <summary>
        /// Validates and previews every input on a dedicated STA thread. Rows
        /// keep a small thumbnail only; export re-renders at full size.
        /// The sidebar's logo IS applied (it shows in every thumbnail, so it
        /// can't slip into an export unnoticed).
        /// </summary>
        public Task<List<BatchQrCodeRow>> GenerateBatchAsync(
            IReadOnlyList<string> inputs,
            QrCodeOptions options,
            IProgress<(int Done, int Total)>? progress,
            CancellationToken cancellationToken)
        {
            var thumbOptions = options.With(ThumbnailSize, ThumbnailSize, showText: false);

            return ZxingImageHelpers.RunOnStaThread(() =>
            {
                var rows = new List<BatchQrCodeRow>(inputs.Count);
                int reportEvery = Math.Max(1, inputs.Count / 200); // ~200 UI updates max

                for (int i = 0; i < inputs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string input = inputs[i];
                    var result = Generate(input, thumbOptions);
                    rows.Add(new BatchQrCodeRow
                    {
                        Index = i + 1,
                        Input = input,
                        Options = options,
                        Status = result.Success ? QrRowStatus.Generated : QrRowStatus.Failed,
                        Message = result.Success ? "Generated" : result.ErrorMessage,
                        Thumbnail = result.Image,
                        SizeInfo = result.Success ? $"v{result.Version} · {result.ModuleCount}×{result.ModuleCount}" : "",
                    });

                    if ((i + 1) % reportEvery == 0 || i == inputs.Count - 1)
                        progress?.Report((i + 1, inputs.Count));
                }

                return rows;
            });
        }

        /// <summary>
        /// Re-renders every successful row at full size into the folder.
        /// Never overwrites: repeated or existing names get " (2)", " (3)"...
        /// Returns how many files were written.
        /// </summary>
        public Task<int> ExportBatchAsync(
            IReadOnlyList<BatchQrCodeRow> rows,
            string folder,
            QrExportFormat format,
            IProgress<(int Done, int Total)>? progress,
            CancellationToken cancellationToken)
        {
            return ZxingImageHelpers.RunOnStaThread(() =>
            {
                var ok = rows.Where(r => r.Status == QrRowStatus.Generated).ToList();
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string ext = ExtensionFor(format);
                int written = 0;
                int reportEvery = Math.Max(1, ok.Count / 200);

                for (int i = 0; i < ok.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = ok[i];

                    var result = Generate(row.Input, row.Options);
                    if (result.Success)
                    {
                        string name = ZxingImageHelpers.SanitizeFileName(row.Input, row.Index, "qrcode");
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

        public static string ExtensionFor(QrExportFormat format) => format switch
        {
            QrExportFormat.Jpg => ".jpg",
            QrExportFormat.Svg => ".svg",
            QrExportFormat.Pdf => ".pdf",
            _ => ".png",
        };

        public static string FileFilterFor(QrExportFormat format) => format switch
        {
            QrExportFormat.Jpg => "JPEG image (*.jpg)|*.jpg",
            QrExportFormat.Svg => "SVG vector (*.svg)|*.svg",
            QrExportFormat.Pdf => "PDF document (*.pdf)|*.pdf",
            _ => "PNG image (*.png)|*.png",
        };

        /// <summary>Writes one result. Must run on an STA thread (encoders are WPF objects).</summary>
        public static void SaveToFile(QrCodeGenerationResult result, string filePath, QrExportFormat format)
        {
            if (result.Image == null)
                throw new InvalidOperationException(Loc.T("Str_QrGen_ErrNothingToExport", "Nothing to export - generate a QR code first."));

            switch (format)
            {
                case QrExportFormat.Jpg:
                    AtomicFile.WriteAllBytes(filePath, ZxingImageHelpers.EncodeToBytes(result.Image, new JpegBitmapEncoder { QualityLevel = 95 }));
                    break;

                case QrExportFormat.Svg:
                    AtomicFile.WriteAllText(filePath, BuildVectorSvg(result), new UTF8Encoding(false));
                    break;

                case QrExportFormat.Pdf:
                    PdfLiteWriter.SaveBitmapAsPdf(result.Image, filePath); // lossless
                    break;

                default:
                    AtomicFile.WriteAllBytes(filePath, ZxingImageHelpers.EncodeToBytes(result.Image, new PngBitmapEncoder()));
                    break;
            }
        }

        /// <summary>
        /// True vector SVG: each horizontal run of dark modules is one
        /// &lt;rect&gt;, the logo is embedded as PNG on its pad, and the
        /// caption is real &lt;text&gt;. Scales to any print size.
        /// </summary>
        public static string BuildVectorSvg(QrCodeGenerationResult result)
        {
            var modules = result.Modules ?? throw new InvalidOperationException(Loc.T("Str_QrGen_ErrNoModules", "No module data to export."));
            var options = result.Options;
            int width = result.PixelWidth, height = result.PixelHeight, m = result.ModulePx, n = result.ModuleCount;

            string fg = Hex(ToColor(options.ForegroundHex, Colors.Black));
            string bg = Hex(ToColor(options.BackgroundHex, Colors.White));

            var sb = new StringBuilder();
            sb.Append(FormattableString.Invariant(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" shape-rendering=\"crispEdges\">\n"));
            sb.Append($"  <rect width=\"100%\" height=\"100%\" fill=\"{bg}\" />\n");
            sb.Append($"  <g fill=\"{fg}\">\n");

            for (int r = 0; r < n; r++)
            {
                int c = 0;
                while (c < n)
                {
                    if (!modules[r, c]) { c++; continue; }
                    int start = c;
                    while (c < n && modules[r, c]) c++;
                    sb.Append(FormattableString.Invariant(
                        $"    <rect x=\"{result.OriginX + start * m}\" y=\"{result.OriginY + r * m}\" width=\"{(c - start) * m}\" height=\"{m}\" />\n"));
                }
            }
            sb.Append("  </g>\n");

            if (options.LogoImage != null && result.LogoSize > 0)
            {
                double pad = m;
                sb.Append(FormattableString.Invariant(
                    $"  <rect x=\"{result.LogoX - pad:0.##}\" y=\"{result.LogoY - pad:0.##}\" width=\"{result.LogoSize + 2 * pad:0.##}\" height=\"{result.LogoSize + 2 * pad:0.##}\" rx=\"{m * 1.5:0.##}\" fill=\"{bg}\" />\n"));

                string logo = Convert.ToBase64String(ZxingImageHelpers.EncodeToBytes(options.LogoImage, new PngBitmapEncoder()));
                sb.Append(FormattableString.Invariant(
                    $"  <image x=\"{result.LogoX:0.##}\" y=\"{result.LogoY:0.##}\" width=\"{result.LogoSize:0.##}\" height=\"{result.LogoSize:0.##}\" preserveAspectRatio=\"xMidYMid meet\" href=\"data:image/png;base64,"));
                sb.Append(logo);
                sb.Append("\" />\n");
            }

            if (result.LabelText.Length > 0)
            {
                sb.Append(FormattableString.Invariant(
                    $"  <text x=\"{width / 2.0:0.##}\" y=\"{result.LabelBaseline:0.##}\" font-family=\"'Segoe UI', Arial, sans-serif\" font-weight=\"600\" font-size=\"{result.LabelFontSize:0.##}\" text-anchor=\"middle\" fill=\"{fg}\">"));
                sb.Append(SecurityElement.Escape(result.LabelText));
                sb.Append("</text>\n");
            }

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
