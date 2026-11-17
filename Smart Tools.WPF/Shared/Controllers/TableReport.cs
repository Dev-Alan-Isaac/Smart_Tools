
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Smart_Tools.WPF.Views;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// A titled table written as plain text, a self-contained HTML page or a landscape A4 PDF. Pure data in, no WPF types
    /// (<see cref="TableReportSaver"/> adds the save dialog). <see cref="Shares"/> is each column's share of the PDF page width.
    /// </summary>
    public sealed record TableReport(string Title, string Note, IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows, IReadOnlyList<double> Shares)
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private string Stamp => DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv) + (string.IsNullOrEmpty(Note) ? "" : " - " + Note);

        /// <summary>Aligned columns, one line per row.</summary>
        public string ToText()
        {
            var widths = Headers.Select((h, i) => Math.Max(h.Length, Rows.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
            var sb = new StringBuilder();
            sb.Append(Title).Append("\r\n").Append(Stamp).Append("\r\n\r\n");
            void Line(IReadOnlyList<string> cells)
            {
                for (int i = 0; i < cells.Count; i++) sb.Append(cells[i].PadRight(widths[i] + (i < cells.Count - 1 ? 2 : 0)));
                sb.Append("\r\n");
            }
            Line(Headers);
            Line(widths.Select(w => new string('-', w)).ToArray());
            foreach (var r in Rows) Line(r);
            return sb.ToString();
        }

        /// <summary>A self-contained page with a table (theme-neutral colours, works offline).</summary>
        public string ToHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(Title)).Append("</title><style>")
              .Append("body{font:14px Segoe UI,Arial,sans-serif;margin:24px;color:#16181d}h1{font-size:20px;margin:0 0 4px}p{color:#5d6572;margin:0 0 16px}")
              .Append("table{border-collapse:collapse;width:100%}th{text-align:left;background:#3d6fe0;color:#fff;padding:8px 10px;font-weight:600}")
              .Append("td{padding:7px 10px;border-bottom:1px solid #e2e6ee}tr:nth-child(even) td{background:#f7f9fc}</style></head><body><h1>")
              .Append(WebUtility.HtmlEncode(Title)).Append("</h1><p>").Append(WebUtility.HtmlEncode(Stamp)).Append("</p><table><thead><tr>");
            foreach (var h in Headers) sb.Append("<th>").Append(WebUtility.HtmlEncode(h)).Append("</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var r in Rows)
            {
                sb.Append("<tr>");
                foreach (var cell in r) sb.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
                sb.Append("</tr>");
            }
            return sb.Append("</tbody></table></body></html>").ToString();
        }

        /// <summary>A landscape A4 table, as many pages as needed. Throws <see cref="IOException"/> if no font could be found.</summary>
        public byte[] ToPdf()
        {
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new WindowsFontResolver();

            const double margin = 36, rowH = 16;
            var head = new XFont("Verdana", 8, XFontStyleEx.Bold);
            var body = new XFont("Verdana", 7.5, XFontStyleEx.Regular);
            var big = new XFont("Verdana", 15, XFontStyleEx.Bold);
            var ink = new XSolidBrush(XColor.FromArgb(22, 24, 29));
            var muted = new XSolidBrush(XColor.FromArgb(93, 101, 114));
            var white = new XSolidBrush(XColor.FromArgb(255, 255, 255));
            var accent = new XSolidBrush(XColor.FromArgb(61, 111, 224));
            var stripe = new XSolidBrush(XColor.FromArgb(244, 246, 250));
            double total = Shares.Sum();

            using var doc = new PdfDocument();
            doc.Info.Title = Title;
            doc.Info.Creator = "Smart Tools";

            XGraphics? g = null;
            double pageW = 0, pageH = 0, y = 0;
            double[] xs = Array.Empty<double>(), ws = Array.Empty<double>();

            string Clip(string text, XFont font, double width)
            {
                if (g!.MeasureString(text, font).Width <= width) return text;
                while (text.Length > 1 && g.MeasureString(text + "…", font).Width > width) text = text[..^1];
                return text + "…";
            }

            void NewPage()
            {
                g?.Dispose();
                var page = doc.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                page.Orientation = PdfSharp.PageOrientation.Landscape;
                g = XGraphics.FromPdfPage(page);
                pageW = page.Width.Point;
                pageH = page.Height.Point;
                ws = Shares.Select(s => s / total * (pageW - 2 * margin)).ToArray();
                xs = new double[ws.Length];
                double x = margin;
                for (int i = 0; i < ws.Length; i++) { xs[i] = x; x += ws[i]; }
                y = margin;
                g.DrawString(Clip(Title, big, pageW - 2 * margin), big, ink, new XPoint(margin, y), XStringFormats.TopLeft);
                y += 22;
                g.DrawString(Clip(Stamp + " - page " + doc.PageCount, body, pageW - 2 * margin), body, muted, new XPoint(margin, y), XStringFormats.TopLeft);
                y += 16;
                g.DrawRectangle(accent, margin, y, pageW - 2 * margin, rowH);
                for (int i = 0; i < Headers.Count; i++)
                    g.DrawString(Clip(Headers[i], head, ws[i] - 8), head, white, new XPoint(xs[i] + 4, y + 3.5), XStringFormats.TopLeft);
                y += rowH;
            }

            NewPage();
            int n = 0;
            foreach (var r in Rows)
            {
                if (y + rowH > pageH - margin) NewPage();
                if (n++ % 2 == 1) g!.DrawRectangle(stripe, margin, y, pageW - 2 * margin, rowH);
                for (int i = 0; i < r.Length; i++)
                    g!.DrawString(Clip(r[i], body, ws[i] - 8), body, ink, new XPoint(xs[i] + 4, y + 4), XStringFormats.TopLeft);
                y += rowH;
            }
            g?.Dispose();

            using var ms = new MemoryStream();
            doc.Save(ms, false);
            return ms.ToArray();
        }
    }

    /// <summary>The save dialog for a <see cref="TableReport"/>: asks where, writes through <see cref="AtomicFile"/>, then offers to open it.</summary>
    public static class TableReportSaver
    {
        /// <summary>Writes the report as <paramref name="kind"/> ("html", "pdf" or "txt"). False when the user cancelled or it failed (a message was shown).</summary>
        public static bool Save(Window? owner, TableReport report, string kind, string baseName)
        {
            string ext = kind switch { "pdf" => ".pdf", "html" => ".html", _ => ".txt" };
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = kind switch { "pdf" => "PDF (*.pdf)|*.pdf", "html" => "HTML (*.html)|*.html", _ => "Text (*.txt)|*.txt" },
                FileName = baseName + "-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + ext,
                AddExtension = true,
                DefaultExt = ext,
            };
            if (dialog.ShowDialog(owner) != true) return false;
            try
            {
                switch (kind)
                {
                    case "pdf": AtomicFile.WriteAllBytes(dialog.FileName, report.ToPdf()); break;
                    case "html": AtomicFile.WriteAllText(dialog.FileName, report.ToHtml(), new UTF8Encoding(false)); break;
                    default: AtomicFile.WriteAllText(dialog.FileName, report.ToText(), new UTF8Encoding(true)); break;
                }
                SavedFileDialog.Show(report.Title, dialog.FileName);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show(owner, ex.Message, Loc.T("Str_Connections_Export", "Export"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
    }
}
