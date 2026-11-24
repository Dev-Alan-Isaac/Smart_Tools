using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// PDF Reader &amp; Signer - writes the marks (text, dates, signatures, check marks) onto a copy of the PDF with PDFsharp.
    /// The original is never changed: the copy is written to a temporary name and moved into place when complete.
    /// This is a visual signature (a picture of a signature on the page), not a certificate-based digital signature.
    /// Drawing the pages on screen is done with <see cref="PdfExtrasManager.PageRenderer"/> (Windows' own PDF renderer).
    /// </summary>
    public static class PdfReaderManager
    {
        private const string FontFamily = "Verdana";   // WindowsFontResolver falls back to Arial, Tahoma or Segoe UI

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true };

        /// <summary>
        /// Writes <paramref name="marks"/> onto a copy of the PDF. <paramref name="pageWidths"/> is each page's width in the units the marks use, so they can be scaled to real points. Throws <see cref="InvalidOperationException"/> with a
        /// plain message when the PDF does not allow changes or cannot be read.
        /// </summary>
        public static void Save(string inputPath, string? password, IReadOnlyList<PdfMark> marks, string outputPath, IReadOnlyDictionary<int, double> pageWidths)
        {
            if (GlobalFontSettings.FontResolver == null)
                GlobalFontSettings.FontResolver = new WindowsFontResolver();

            PdfDocument document;
            try
            {
                document = string.IsNullOrEmpty(password)
                    ? PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify)
                    : PdfReader.Open(inputPath, password, PdfDocumentOpenMode.Modify);
            }
            catch (PdfReaderException ex)
            {
                throw new InvalidOperationException("This PDF can't be changed: it is protected or damaged. " + ex.Message, ex);
            }

            using (document)
            {
                foreach (var group in marks.GroupBy(m => m.PageIndex))
                {
                    if (group.Key < 0 || group.Key >= document.PageCount) continue;
                    var page = document.Pages[group.Key];
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    ApplyRotation(gfx, page);
                    double shown = ((page.Rotate % 360) + 360) % 360 is 90 or 270 ? page.Height.Point : page.Width.Point;
                    double scale = pageWidths.TryGetValue(group.Key, out double units) && units > 0 ? shown / units : 1;
                    foreach (var mark in group) Draw(gfx, mark, scale);
                }

                string temp = outputPath + ".tmp";
                try
                {
                    document.Save(temp);
                    File.Move(temp, outputPath);
                }
                catch
                {
                    try { File.Delete(temp); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    throw;
                }
            }
        }

        /// <summary>
        /// Marks are placed on the page as it is shown (turned, top-left origin). PDFsharp draws on the unturned page, so for a
        /// turned page the drawing is moved into the shown frame first.
        /// </summary>
        private static void ApplyRotation(XGraphics gfx, PdfPage page)
        {
            double w = page.Width.Point, h = page.Height.Point;
            switch (((page.Rotate % 360) + 360) % 360)
            {
                case 90:
                    gfx.MultiplyTransform(new XMatrix(0, -1, 1, 0, 0, h));
                    break;
                case 180:
                    gfx.MultiplyTransform(new XMatrix(-1, 0, 0, -1, w, h));
                    break;
                case 270:
                    gfx.MultiplyTransform(new XMatrix(0, 1, -1, 0, w, 0));
                    break;
            }
        }

        private static void Draw(XGraphics gfx, PdfMark mark, double scale)
        {
            switch (mark.Kind)
            {
                case PdfMarkKind.Text:
                    if (string.IsNullOrEmpty(mark.Text)) return;
                    var font = new XFont(FontFamily, Math.Max(4, mark.Size * scale));
                    gfx.DrawString(mark.Text, font, XBrushes.Black, new XPoint(mark.X * scale, mark.Y * scale), XStringFormats.TopLeft);
                    break;

                case PdfMarkKind.Signature:
                    if (mark.Image.Length == 0) return;
                    using (var stream = new MemoryStream(mark.Image))
                    {
                        var image = XImage.FromStream(stream);
                        gfx.DrawImage(image, mark.X * scale, mark.Y * scale, mark.Size * scale, mark.Height * scale);
                    }
                    break;

                case PdfMarkKind.Check:
                    var pen = new XPen(XColors.Black, Math.Max(1, mark.Size * scale * 0.1)) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
                    double s = mark.Size * scale, ox = mark.X * scale, oy = mark.Y * scale;
                    gfx.DrawLines(pen, new[]
                    {
                        new XPoint(ox + s * 0.15, oy + s * 0.55),
                        new XPoint(ox + s * 0.40, oy + s * 0.80),
                        new XPoint(ox + s * 0.85, oy + s * 0.20),
                    });
                    break;

                case PdfMarkKind.Cross:
                    {
                        var crossPen = new XPen(XColors.Black, Math.Max(1, mark.Size * scale * 0.1)) { LineCap = XLineCap.Round };
                        double side = mark.Size * scale, left = mark.X * scale, top = mark.Y * scale;
                        gfx.DrawLine(crossPen, left + side * 0.20, top + side * 0.20, left + side * 0.80, top + side * 0.80);
                        gfx.DrawLine(crossPen, left + side * 0.80, top + side * 0.20, left + side * 0.20, top + side * 0.80);
                        break;
                    }

                case PdfMarkKind.Line:
                    {
                        var linePen = new XPen(XColors.Black, LineThickness * scale);
                        double middle = (mark.Y + PdfMark.LineBox / 2) * scale;
                        gfx.DrawLine(linePen, mark.X * scale, middle, (mark.X + mark.Size) * scale, middle);
                        break;
                    }

                case PdfMarkKind.Highlight:
                    gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(110, 255, 235, 59)), mark.X * scale, mark.Y * scale, mark.Size * scale, mark.Height * scale);
                    break;
            }
        }

        /// <summary>Thickness in points of a line mark (also used to draw it on screen).</summary>
        public const double LineThickness = 1.5;

        /// <summary>
        /// Every PDF in the files and folders given (a folder gives the PDFs directly inside it, in natural order: 2 before 10),
        /// without repeats, at most <paramref name="limit"/>. Unreadable folders are skipped.
        /// </summary>
        public static List<string> FindPdfs(IEnumerable<string> paths, int limit = 20000)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (result.Count >= limit) break;
                try
                {
                    if (File.Exists(path))
                    {
                        if (IsPdf(path) && seen.Add(path)) result.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        var here = Directory.EnumerateFiles(path, "*.pdf", SearchOption.TopDirectoryOnly).Where(IsPdf).ToList();
                        here.Sort(NaturalStringComparer.Instance);
                        foreach (string file in here)
                        {
                            if (result.Count >= limit) break;
                            if (seen.Add(file)) result.Add(file);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable folder adds nothing.
                }
            }
            return result;
        }

        private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

        // =================================================================
        //  The remembered signature + settings
        // =================================================================

        public static string SignaturePath => Path.Combine(AppPaths.DataRoot, "pdf-signature.png");

        public static string SettingsPath => Path.Combine(AppPaths.DataRoot, "pdf-reader.json");

        /// <summary>The signature the user kept from last time (PNG bytes), or null.</summary>
        public static byte[]? LoadSignature()
        {
            try
            {
                return File.Exists(SignaturePath) ? File.ReadAllBytes(SignaturePath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static void SaveSignature(byte[] png)
        {
            try
            {
                AtomicFile.WriteAllBytes(SignaturePath, png);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Remembering the signature is a convenience; never fail the tool over it.
            }
        }

        public static void ForgetSignature()
        {
            try
            {
                if (File.Exists(SignaturePath)) File.Delete(SignaturePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing else to do.
            }
        }

        public static PdfReaderSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return Sanitize(JsonSerializer.Deserialize<PdfReaderSettings>(File.ReadAllText(SettingsPath), SettingsJson) ?? new PdfReaderSettings());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A damaged file: start from defaults.
            }
            return new PdfReaderSettings();
        }

        public static void SaveSettings(PdfReaderSettings settings)
        {
            try
            {
                AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(Sanitize(settings), SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience.
            }
        }

        public static PdfReaderSettings Sanitize(PdfReaderSettings s)
        {
            s.TextSize = Math.Clamp(s.TextSize, 6, 72);
            return s;
        }
    }
}
