using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  Reports with charts, shared by System Monitor and Hardware Sensors.
    //  Pure data in, a self-contained HTML page or a PDF out. No WPF types,
    //  so it can be tested without a window.
    // =====================================================================

    /// <summary>One line on a chart. Colours are fixed hex values (a report is a file, not part of the app's theme).</summary>
    public sealed record PerfSeries(string Name, IReadOnlyList<double> Values, string Color, bool Dashed = false);

    /// <summary>A chart: its lines, how to write a value, and a fixed top of the scale when there is one (100 for percentages).</summary>
    public sealed record PerfChart(string Title, IReadOnlyList<PerfSeries> Series, Func<double, string> Format, double? FixedMax = null);

    public sealed record PerfRow(string Label, string Value);

    /// <summary>One block of the report: a part of the PC, its figures and its charts.</summary>
    public sealed record PerfSection(string Title, string Subtitle, IReadOnlyList<PerfRow> Rows, IReadOnlyList<PerfChart> Charts);

    /// <summary>The whole report. <see cref="SampleSeconds"/> is the time between two points of a chart (for the time axis).</summary>
    public sealed record PerfReportData(string Title, DateTime When, double SampleSeconds, string Note, IReadOnlyList<PerfSection> Sections);

    /// <summary>
    /// The readings kept for the report: more than the little graphs show (up to <see cref="Capacity"/> points per line), because
    /// a report is read later and a minute is too short to say anything. One list per key; the oldest points fall off.
    /// </summary>
    public sealed class PerfRecorder
    {
        public const int Capacity = 900;

        private readonly Dictionary<string, List<double>> _values = new();

        public void Add(string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            if (!_values.TryGetValue(key, out var list)) _values[key] = list = new List<double>(64);
            list.Add(value);
            if (list.Count > Capacity) list.RemoveRange(0, list.Count - Capacity);
        }

        public IReadOnlyList<double> Get(string key) => _values.TryGetValue(key, out var list) ? list.ToArray() : Array.Empty<double>();

        /// <summary>Forgets every line whose key starts with <paramref name="prefix"/> (a part that went away).</summary>
        public void RemoveWithPrefix(string prefix)
        {
            foreach (string key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _values.Remove(key);
        }
    }

    public static class PerfReportBuilder
    {
        // Two neutral, colour-blind-friendly line colours that read on a white and on a dark page.
        public const string Blue = "#3b82f6";
        public const string Orange = "#f59e0b";
        public const string Green = "#22a06b";
        public const string Red = "#e5484d";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Time axis text: "5 min ago", "45 s ago" for the left end of a chart.</summary>
        public static string Span(double seconds)
        {
            if (seconds >= 3600) return (seconds / 3600).ToString("0.#", Inv) + " h";
            if (seconds >= 90) return (seconds / 60).ToString("0.#", Inv) + " min";
            return seconds.ToString("0", Inv) + " s";
        }

        /// <summary>Top of the scale: the fixed one, or the highest value with 15 % of room above it, never below a small floor.</summary>
        public static double ScaleMax(PerfChart chart)
        {
            if (chart.FixedMax is double fixedMax) return fixedMax;
            double max = chart.Series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max();
            return max <= 0 ? 1 : max * 1.15;
        }

        public static (double Min, double Avg, double Max) Stats(IReadOnlyList<double> values) =>
            values.Count == 0 ? (0, 0, 0) : (values.Min(), values.Average(), values.Max());

        // =================================================================
        //  HTML
        // =================================================================

        public static string ToHtml(PerfReportData data)
        {
            var sb = new StringBuilder(32 * 1024);
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>").Append(WebUtility.HtmlEncode(data.Title)).Append("</title>\n<style>\n");
            sb.Append(Css);
            sb.Append("</style>\n</head>\n<body>\n<main>\n");
            sb.Append("<header><h1>").Append(WebUtility.HtmlEncode(data.Title)).Append("</h1><p class=\"muted\">")
              .Append(WebUtility.HtmlEncode(data.When.ToString("yyyy-MM-dd HH:mm", Inv)));
            if (data.Note.Length > 0) sb.Append(" &middot; ").Append(WebUtility.HtmlEncode(data.Note));
            sb.Append("</p></header>\n");

            foreach (var section in data.Sections)
            {
                sb.Append("<section class=\"card\">\n<h2>").Append(WebUtility.HtmlEncode(section.Title)).Append("</h2>\n");
                if (section.Subtitle.Length > 0) sb.Append("<p class=\"muted\">").Append(WebUtility.HtmlEncode(section.Subtitle)).Append("</p>\n");
                foreach (var chart in section.Charts) AppendChartHtml(sb, chart, data.SampleSeconds);
                if (section.Rows.Count > 0)
                {
                    sb.Append("<table>\n");
                    foreach (var r in section.Rows)
                        sb.Append("<tr><th>").Append(WebUtility.HtmlEncode(r.Label)).Append("</th><td>").Append(WebUtility.HtmlEncode(r.Value)).Append("</td></tr>\n");
                    sb.Append("</table>\n");
                }
                sb.Append("</section>\n");
            }
            sb.Append("<footer class=\"muted\">Made with Smart Tools</footer>\n</main>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static void AppendChartHtml(StringBuilder sb, PerfChart chart, double sampleSeconds)
        {
            const double W = 720, H = 170, L = 56, R = 10, T = 8, B = 22;
            double max = ScaleMax(chart);
            int count = chart.Series.Select(s => s.Values.Count).DefaultIfEmpty(0).Max();

            sb.Append("<figure>\n<figcaption>").Append(WebUtility.HtmlEncode(chart.Title)).Append("</figcaption>\n");
            if (count < 2)
            {
                sb.Append("<p class=\"muted\">Not enough readings yet for a chart. Leave the tool open a little longer.</p>\n</figure>\n");
                return;
            }

            sb.Append("<svg viewBox=\"0 0 720 170\" role=\"img\" aria-label=\"").Append(WebUtility.HtmlEncode(chart.Title)).Append("\">\n");
            for (int i = 0; i <= 4; i++)
            {
                double y = T + (H - T - B) * i / 4.0;
                double value = max * (4 - i) / 4.0;
                sb.Append("<line class=\"grid\" x1=\"").Append(F(L)).Append("\" x2=\"").Append(F(W - R)).Append("\" y1=\"").Append(F(y)).Append("\" y2=\"").Append(F(y)).Append("\"/>\n");
                sb.Append("<text class=\"axis\" x=\"").Append(F(L - 6)).Append("\" y=\"").Append(F(y + 3.5)).Append("\" text-anchor=\"end\">")
                  .Append(WebUtility.HtmlEncode(chart.Format(value))).Append("</text>\n");
            }
            double span = (count - 1) * Math.Max(0.1, sampleSeconds);
            sb.Append("<text class=\"axis\" x=\"").Append(F(L)).Append("\" y=\"").Append(F(H - 6)).Append("\">").Append(WebUtility.HtmlEncode(Span(span) + " ago")).Append("</text>\n");
            sb.Append("<text class=\"axis\" x=\"").Append(F(W - R)).Append("\" y=\"").Append(F(H - 6)).Append("\" text-anchor=\"end\">now</text>\n");

            for (int si = 0; si < chart.Series.Count; si++)
            {
                var s = chart.Series[si];
                if (s.Values.Count < 2) continue;
                var pts = new StringBuilder();
                for (int i = 0; i < s.Values.Count; i++)
                {
                    double x = L + (W - L - R) * i / (double)(count - 1);
                    double y = T + (H - T - B) * (1 - Math.Clamp(s.Values[i] / max, 0, 1));
                    pts.Append(F(x)).Append(',').Append(F(y)).Append(' ');
                }
                if (si == 0)
                {
                    double x0 = L, x1 = L + (W - L - R) * (s.Values.Count - 1) / (double)(count - 1), yb = H - B;
                    sb.Append("<polygon fill=\"").Append(s.Color).Append("\" fill-opacity=\"0.14\" points=\"").Append(F(x0)).Append(',').Append(F(yb)).Append(' ').Append(pts).Append(F(x1)).Append(',').Append(F(yb)).Append("\"/>\n");
                }
                sb.Append("<polyline fill=\"none\" stroke=\"").Append(s.Color).Append("\" stroke-width=\"2\" stroke-linejoin=\"round\"")
                  .Append(s.Dashed ? " stroke-dasharray=\"6 4\"" : "").Append(" points=\"").Append(pts.ToString().TrimEnd()).Append("\"/>\n");
            }
            sb.Append("</svg>\n<ul class=\"legend\">\n");
            foreach (var s in chart.Series)
            {
                var (min, avg, mx) = Stats(s.Values);
                sb.Append("<li><i style=\"background:").Append(s.Color).Append("\"></i><b>").Append(WebUtility.HtmlEncode(s.Name)).Append("</b> ")
                  .Append("<span class=\"muted\">low ").Append(WebUtility.HtmlEncode(chart.Format(min))).Append(" &middot; average ")
                  .Append(WebUtility.HtmlEncode(chart.Format(avg))).Append(" &middot; high ").Append(WebUtility.HtmlEncode(chart.Format(mx))).Append("</span></li>\n");
            }
            sb.Append("</ul>\n</figure>\n");
        }

        private static string F(double v) => v.ToString("0.##", Inv);

        private const string Css = @":root{color-scheme:light dark;--bg:#f4f5f7;--card:#fff;--text:#16181d;--muted:#5d6572;--line:#dfe3ea;--grid:#e6e9ef}
@media (prefers-color-scheme:dark){:root{--bg:#0f1115;--card:#181b21;--text:#eceff4;--muted:#9aa3b2;--line:#2a2f39;--grid:#262b34}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif}
main{max-width:860px;margin:0 auto;padding:24px 16px 40px}h1{font-size:1.5rem;margin:0}h2{font-size:1.1rem;margin:0 0 4px}
.muted{color:var(--muted)}header{margin-bottom:16px}.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px 18px;margin-bottom:16px}
figure{margin:14px 0 6px}figcaption{font-weight:600;margin-bottom:4px}svg{width:100%;height:auto;display:block}
.grid{stroke:var(--grid);stroke-width:1}.axis{fill:var(--muted);font-size:10px;font-family:inherit}
.legend{list-style:none;margin:6px 0 0;padding:0;font-size:.85rem}.legend li{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:2px 0}
.legend i{width:12px;height:12px;border-radius:3px;display:inline-block}
table{width:100%;border-collapse:collapse;margin-top:12px;font-size:.92rem}th,td{padding:5px 8px;border-top:1px solid var(--line);text-align:left;vertical-align:top}
th{width:38%;font-weight:600;color:var(--muted)}footer{text-align:center;font-size:.8rem;margin-top:8px}
@media print{body{background:#fff;color:#16181d}.card{break-inside:avoid;border-color:#ccc}}
";

        // =================================================================
        //  PDF (PDFsharp, A4)
        // =================================================================

        /// <summary>The report as a PDF. Throws <see cref="IOException"/> if no font could be found.</summary>
        public static byte[] ToPdf(PerfReportData data)
        {
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new WindowsFontResolver();

            const double margin = 40, chartH = 120, lineH = 13.5;
            using var doc = new PdfDocument();
            doc.Info.Title = data.Title;
            doc.Info.Creator = "Smart Tools";

            PdfPage? page = null;
            XGraphics? g = null;
            double pageW = 0, pageH = 0, y = 0;
            int pageNo = 0;
            var small = new XFont("Verdana", 7.5, XFontStyleEx.Regular);
            var body = new XFont("Verdana", 8.5, XFontStyleEx.Regular);
            var bold = new XFont("Verdana", 8.5, XFontStyleEx.Bold);
            var heading = new XFont("Verdana", 12, XFontStyleEx.Bold);
            var title = new XFont("Verdana", 16, XFontStyleEx.Bold);
            var ink = new XSolidBrush(XColor.FromArgb(22, 24, 29));
            var muted = new XSolidBrush(XColor.FromArgb(93, 101, 114));

            void NewPage()
            {
                g?.Dispose();
                page = doc.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                g = XGraphics.FromPdfPage(page);
                pageW = page.Width.Point;
                pageH = page.Height.Point;
                pageNo++;
                y = margin;
                g.DrawString($"{data.Title} - {pageNo}", small, muted, new XPoint(pageW - margin, pageH - margin + 14), XStringFormats.TopRight);
            }

            void Need(double height)
            {
                if (page == null || y + height > pageH - margin - 6) NewPage();
            }

            NewPage();
            g!.DrawString(data.Title, title, ink, new XPoint(margin, y), XStringFormats.TopLeft);
            y += 24;
            string sub = data.When.ToString("yyyy-MM-dd HH:mm", Inv) + (data.Note.Length > 0 ? "  -  " + data.Note : "");
            g.DrawString(sub, small, muted, new XPoint(margin, y), XStringFormats.TopLeft);
            y += 22;

            foreach (var section in data.Sections)
            {
                Need(40 + (section.Charts.Count > 0 ? chartH + 36 : lineH * Math.Min(3, section.Rows.Count)));
                g!.DrawString(Clip(g, section.Title, heading, pageW - 2 * margin), heading, ink, new XPoint(margin, y), XStringFormats.TopLeft);
                y += 18;
                if (section.Subtitle.Length > 0)
                {
                    g.DrawString(Clip(g, section.Subtitle, small, pageW - 2 * margin), small, muted, new XPoint(margin, y), XStringFormats.TopLeft);
                    y += 13;
                }

                foreach (var chart in section.Charts)
                {
                    Need(chartH + 40 + chart.Series.Count * 11);
                    y = DrawChartPdf(g!, chart, data.SampleSeconds, margin, y, pageW - 2 * margin, chartH, body, bold, small, muted, ink);
                }

                double labelW = (pageW - 2 * margin) * 0.38;
                foreach (var r in section.Rows)
                {
                    Need(lineH + 2);
                    g!.DrawString(Clip(g, r.Label, body, labelW - 8), body, muted, new XPoint(margin, y), XStringFormats.TopLeft);
                    g.DrawString(Clip(g, r.Value, body, pageW - 2 * margin - labelW), body, ink, new XPoint(margin + labelW, y), XStringFormats.TopLeft);
                    y += lineH;
                }
                y += 12;
            }
            g?.Dispose();

            using var ms = new MemoryStream();
            doc.Save(ms, false);
            return ms.ToArray();
        }

        private static string Clip(XGraphics g, string text, XFont font, double width)
        {
            if (g.MeasureString(text, font).Width <= width) return text;
            while (text.Length > 1 && g.MeasureString(text + "...", font).Width > width) text = text[..^1];
            return text + "...";
        }

        private static XColor Hex(string hex) => XColor.FromArgb(Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

        /// <summary>Draws one chart with its title, axes and legend; returns the y under it.</summary>
        private static double DrawChartPdf(XGraphics g, PerfChart chart, double sampleSeconds, double x0, double y0, double width, double height,
            XFont body, XFont bold, XFont small, XBrush muted, XBrush ink)
        {
            g.DrawString(chart.Title, bold, ink, new XPoint(x0, y0), XStringFormats.TopLeft);
            double top = y0 + 16, left = x0 + 50, right = x0 + width - 6, bottom = top + height - 16;
            double max = ScaleMax(chart);
            int count = chart.Series.Select(s => s.Values.Count).DefaultIfEmpty(0).Max();

            if (count < 2)
            {
                g.DrawString("Not enough readings yet for a chart.", small, muted, new XPoint(x0, top), XStringFormats.TopLeft);
                return top + 18;
            }

            var gridPen = new XPen(XColor.FromArgb(222, 226, 233), 0.6);
            for (int i = 0; i <= 4; i++)
            {
                double yy = top + (bottom - top) * i / 4.0;
                g.DrawLine(gridPen, left, yy, right, yy);
                string label = chart.Format(max * (4 - i) / 4.0);
                g.DrawString(label, small, muted, new XPoint(left - 4, yy - 4), XStringFormats.TopRight);
            }
            double span = (count - 1) * Math.Max(0.1, sampleSeconds);
            g.DrawString(Span(span) + " ago", small, muted, new XPoint(left, bottom + 3), XStringFormats.TopLeft);
            g.DrawString("now", small, muted, new XPoint(right, bottom + 3), XStringFormats.TopRight);

            foreach (var s in chart.Series)
            {
                if (s.Values.Count < 2) continue;
                var pen = new XPen(Hex(s.Color), 1.4) { LineJoin = XLineJoin.Round };
                if (s.Dashed) pen.DashStyle = XDashStyle.Dash;
                var pts = new XPoint[s.Values.Count];
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = new XPoint(left + (right - left) * i / (double)(count - 1), top + (bottom - top) * (1 - Math.Clamp(s.Values[i] / max, 0, 1)));
                g.DrawLines(pen, pts);
            }

            double ly = bottom + 16;
            foreach (var s in chart.Series)
            {
                var (min, avg, mx) = Stats(s.Values);
                g.DrawRectangle(new XSolidBrush(Hex(s.Color)), x0, ly + 1.5, 8, 8);
                g.DrawString($"{s.Name}  -  low {chart.Format(min)}, average {chart.Format(avg)}, high {chart.Format(mx)}", small, ink, new XPoint(x0 + 13, ly), XStringFormats.TopLeft);
                ly += 11;
            }
            return ly + 8;
        }
    }
}
