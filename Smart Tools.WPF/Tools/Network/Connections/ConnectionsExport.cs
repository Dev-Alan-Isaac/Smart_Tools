using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Network Connections list written as plain text, HTML or PDF (CSV is <see cref="ConnectionsManager.ToCsv"/>).</summary>
    public static class ConnectionsExport
    {
        private static readonly string[] Headers = { "App", "PID", "Protocol", "Local address", "Remote address", "Remote port", "Host", "State", "Where" };

        private static List<string[]> Cells(IEnumerable<ConnectionRow> rows) =>
            rows.Select(r =>
            {
                var c = r.Info;
                return new[] { c.AppName, c.Pid.ToString(CultureInfo.InvariantCulture), c.Protocol, c.LocalAddress + ":" + c.LocalPort, c.RemoteAddress,
                    c.RemotePort > 0 ? c.RemotePort.ToString(CultureInfo.InvariantCulture) : "", r.Host, c.State, c.IsOutside ? "Outside" : "Local" };
            }).ToList();

        /// <summary>Aligned columns, one line per connection.</summary>
        public static string ToText(IEnumerable<ConnectionRow> rows)
        {
            var data = Cells(rows);
            var widths = Headers.Select((h, i) => Math.Max(h.Length, data.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
            var sb = new StringBuilder();
            void Line(string[] cells)
            {
                for (int i = 0; i < cells.Length; i++) sb.Append(cells[i].PadRight(widths[i] + (i < cells.Length - 1 ? 2 : 0)));
                sb.Append("\r\n");
            }
            Line(Headers);
            Line(widths.Select(w => new string('-', w)).ToArray());
            foreach (var r in data) Line(r);
            return sb.ToString();
        }

        /// <summary>A self-contained page with a table (theme-neutral colours, works offline).</summary>
        public static string ToHtml(IEnumerable<ConnectionRow> rows, string title)
        {
            var data = Cells(rows);
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>")
              .Append("body{font:14px Segoe UI,Arial,sans-serif;margin:24px;color:#16181d}h1{font-size:20px;margin:0 0 4px}p{color:#5d6572;margin:0 0 16px}")
              .Append("table{border-collapse:collapse;width:100%}th{text-align:left;background:#3d6fe0;color:#fff;padding:8px 10px;font-weight:600}")
              .Append("td{padding:7px 10px;border-bottom:1px solid #e2e6ee}tr:nth-child(even) td{background:#f7f9fc}</style></head><body><h1>")
              .Append(WebUtility.HtmlEncode(title)).Append("</h1><p>").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
              .Append(" - ").Append(data.Count).Append(" connections</p><table><thead><tr>");
            foreach (var h in Headers) sb.Append("<th>").Append(WebUtility.HtmlEncode(h)).Append("</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var r in data)
            {
                sb.Append("<tr>");
                foreach (var cell in r) sb.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
                sb.Append("</tr>");
            }
            return sb.Append("</tbody></table></body></html>").ToString();
        }

        /// <summary>A landscape A4 table, as many pages as needed. Throws <see cref="IOException"/> if no font could be found.</summary>
        public static byte[] ToPdf(IEnumerable<ConnectionRow> rows, string title)
        {
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new WindowsFontResolver();
            var data = Cells(rows);

            const double margin = 36, rowH = 16;
            var head = new XFont("Verdana", 8, XFontStyleEx.Bold);
            var body = new XFont("Verdana", 7.5, XFontStyleEx.Regular);
            var big = new XFont("Verdana", 15, XFontStyleEx.Bold);
            var ink = new XSolidBrush(XColor.FromArgb(22, 24, 29));
            var muted = new XSolidBrush(XColor.FromArgb(93, 101, 114));
            var white = new XSolidBrush(XColor.FromArgb(255, 255, 255));
            var accent = new XSolidBrush(XColor.FromArgb(61, 111, 224));
            var stripe = new XSolidBrush(XColor.FromArgb(244, 246, 250));

            // Column shares of the page width: App, PID, Proto, Local, Remote, Port, Host, State, Where
            double[] share = { 0.14, 0.05, 0.06, 0.15, 0.14, 0.06, 0.22, 0.10, 0.08 };

            using var doc = new PdfDocument();
            doc.Info.Title = title;
            doc.Info.Creator = "Smart Tools";

            PdfPage? page = null;
            XGraphics? g = null;
            double pageW = 0, pageH = 0, y = 0;
            double[] xs = Array.Empty<double>(), ws = Array.Empty<double>();

            string Clip(string text, XFont font, double width)
            {
                if (g!.MeasureString(text, font).Width <= width) return text;
                while (text.Length > 1 && g.MeasureString(text + "…", font).Width > width) text = text[..^1];
                return text + "…";
            }

            void Header()
            {
                g!.DrawRectangle(accent, margin, y, pageW - 2 * margin, rowH);
                for (int i = 0; i < Headers.Length; i++)
                    g.DrawString(Clip(Headers[i], head, ws[i] - 8), head, white, new XPoint(xs[i] + 4, y + 3.5), XStringFormats.TopLeft);
                y += rowH;
            }

            void NewPage()
            {
                g?.Dispose();
                page = doc.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                page.Orientation = PdfSharp.PageOrientation.Landscape;
                g = XGraphics.FromPdfPage(page);
                pageW = page.Width.Point;
                pageH = page.Height.Point;
                ws = share.Select(s => s * (pageW - 2 * margin)).ToArray();
                xs = new double[ws.Length];
                double x = margin;
                for (int i = 0; i < ws.Length; i++) { xs[i] = x; x += ws[i]; }
                y = margin;
                g.DrawString(title, big, ink, new XPoint(margin, y), XStringFormats.TopLeft);
                y += 22;
                g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " - " + data.Count + " connections - page " + doc.PageCount,
                    body, muted, new XPoint(margin, y), XStringFormats.TopLeft);
                y += 16;
                Header();
            }

            NewPage();
            int n = 0;
            foreach (var r in data)
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
}
