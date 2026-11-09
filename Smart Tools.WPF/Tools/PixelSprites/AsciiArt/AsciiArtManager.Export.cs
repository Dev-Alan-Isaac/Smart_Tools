using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Getting the picture out: plain text, ANSI colour, HTML, RTF (coloured text for Word and WordPad), PNG, and the clipboard. Saves never overwrite a file.</summary>
    public static partial class AsciiArtManager
    {
        public enum ExportKind { Text, Ansi, Html, Rtf, Png }

        public static ExportKind KindFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".rtf" => ExportKind.Rtf,
            ".html" or ".htm" => ExportKind.Html,
            ".png" => ExportKind.Png,
            ".ansi" => ExportKind.Ansi,
            _ => ExportKind.Text,
        };

        // ---------------- Text ----------------

        public static string ToPlainText(AsciiGrid grid)
        {
            var sb = new StringBuilder(grid.Rows * (grid.Cols + 2));
            for (int y = 0; y < grid.Rows; y++)
            {
                sb.Append(grid.Chars, y * grid.Cols, grid.Cols);
                if (y < grid.Rows - 1) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Text with 24-bit colour codes, for terminals that show them (Windows Terminal does).</summary>
        public static string ToAnsi(AsciiGrid grid)
        {
            var sb = new StringBuilder();
            for (int y = 0; y < grid.Rows; y++)
            {
                uint last = uint.MaxValue;
                for (int x = 0; x < grid.Cols; x++)
                {
                    uint c = grid.ColorAt(x, y);
                    if (c != last)
                    {
                        if (c == 0) sb.Append("\u001b[39m");
                        else sb.Append("\u001b[38;2;").Append((c >> 16) & 255).Append(';').Append((c >> 8) & 255).Append(';').Append(c & 255).Append('m');
                        last = c;
                    }
                    sb.Append(grid[x, y]);
                }
                sb.Append("\u001b[0m\r\n");
            }
            return sb.ToString();
        }

        // ---------------- Colour runs ----------------

        /// <summary>Five bits per colour channel: nearly the same look, far fewer different colours, so a coloured picture is not one tag per character.</summary>
        private static uint Quantize(uint argb)
        {
            if (argb == 0) return 0;
            static uint Ch(uint v) { v &= 0xF8; return v | (v >> 5); }
            return 0xFF000000u | Ch((argb >> 16) & 255) << 16 | Ch((argb >> 8) & 255) << 8 | Ch(argb & 255);
        }

        private static string Hex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);

        // ---------------- HTML ----------------

        private static void AppendHtmlRows(StringBuilder sb, AsciiGrid grid)
        {
            for (int y = 0; y < grid.Rows; y++)
            {
                int x = 0;
                while (x < grid.Cols)
                {
                    uint color = Quantize(grid.ColorAt(x, y));
                    int end = x + 1;
                    while (end < grid.Cols && Quantize(grid.ColorAt(end, y)) == color) end++;
                    if (color != 0) sb.Append("<span style=\"color:").Append(Hex(color)).Append("\">");
                    for (int i = x; i < end; i++)
                    {
                        char c = grid[i, y];
                        sb.Append(c switch { '<' => "&lt;", '>' => "&gt;", '&' => "&amp;", '"' => "&quot;", _ => c.ToString() });
                    }
                    if (color != 0) sb.Append("</span>");
                    x = end;
                }
                if (y < grid.Rows - 1) sb.Append('\n');
            }
        }

        private static string FontStack(AsciiGlyphs glyphs) => "'" + glyphs.FontName.Replace("'", "") + "','Consolas','Courier New',monospace";

        private static string PreStyle(AsciiSettings s, AsciiGlyphs glyphs, double px, bool withPage)
        {
            var bg = AsciiRender.BackgroundColor(s.DarkBackground);
            var fg = AsciiRender.TextColor(s.DarkBackground);
            return $"margin:0;{(withPage ? "padding:16px;" : "")}background:#{bg.R:x2}{bg.G:x2}{bg.B:x2};color:#{fg.R:x2}{fg.G:x2}{fg.B:x2};" +
                   $"font-family:{FontStack(glyphs)};font-size:{px.ToString("0.##", CultureInfo.InvariantCulture)}px;" +
                   $"line-height:{glyphs.CellHeightEm.ToString("0.####", CultureInfo.InvariantCulture)};white-space:pre;letter-spacing:0";
        }

        public static string ToHtml(AsciiGrid grid, AsciiSettings s, AsciiGlyphs glyphs, string title)
        {
            var bg = AsciiRender.BackgroundColor(s.DarkBackground);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>")
              .Append(System.Net.WebUtility.HtmlEncode(title))
              .Append("</title><style>html,body{margin:0;background:#").Append(bg.R.ToString("x2")).Append(bg.G.ToString("x2")).Append(bg.B.ToString("x2"))
              .Append("}</style></head><body>\n<pre style=\"").Append(PreStyle(s, glyphs, 10, true)).Append("\">");
            AppendHtmlRows(sb, grid);
            sb.Append("</pre>\n</body></html>\n");
            return sb.ToString();
        }

        /// <summary>The CF_HTML clipboard envelope (byte offsets in UTF-8) around a fragment, so Word, Outlook and browsers paste it with colours.</summary>
        private static string ClipboardHtml(AsciiGrid grid, AsciiSettings s, AsciiGlyphs glyphs)
        {
            var body = new StringBuilder();
            body.Append("<pre style=\"").Append(PreStyle(s, glyphs, 10, false)).Append("\">");
            AppendHtmlRows(body, grid);
            body.Append("</pre>");

            const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string pre = "<html><body>\r\n<!--StartFragment-->";
            const string post = "<!--EndFragment-->\r\n</body></html>";
            int headerLen = string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0).Length;
            int startHtml = headerLen;
            int startFragment = startHtml + Encoding.UTF8.GetByteCount(pre);
            int endFragment = startFragment + Encoding.UTF8.GetByteCount(body.ToString());
            int endHtml = endFragment + Encoding.UTF8.GetByteCount(post);
            return string.Format(CultureInfo.InvariantCulture, header, startHtml, endHtml, startFragment, endFragment) + pre + body + post;
        }

        // ---------------- RTF ----------------

        public static string ToRtf(AsciiGrid grid, AsciiSettings s, AsciiGlyphs glyphs)
        {
            const double pt = 8;
            var colors = new List<uint>();   // index in the RTF colour table = position + 3
            var index = new Dictionary<uint, int>();
            int ColorIndex(uint q)
            {
                if (q == 0) return 2;
                if (index.TryGetValue(q, out int i)) return i;
                colors.Add(q);
                return index[q] = colors.Count + 2;
            }

            var body = new StringBuilder();
            for (int y = 0; y < grid.Rows; y++)
            {
                int x = 0;
                while (x < grid.Cols)
                {
                    uint color = Quantize(grid.ColorAt(x, y));
                    int end = x + 1;
                    while (end < grid.Cols && Quantize(grid.ColorAt(end, y)) == color) end++;
                    body.Append("\\cf").Append(ColorIndex(color)).Append(' ');
                    for (int i = x; i < end; i++)
                    {
                        char c = grid[i, y];
                        if (c is '\\' or '{' or '}') body.Append('\\').Append(c);
                        else if (c > 126) body.Append("\\u").Append((short)c).Append('?');
                        else body.Append(c);
                    }
                    x = end;
                }
                body.Append("\\par\r\n");
            }

            var bg = AsciiRender.BackgroundColor(s.DarkBackground);
            var fg = AsciiRender.TextColor(s.DarkBackground);
            var sb = new StringBuilder();
            sb.Append("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1{\\fonttbl{\\f0\\fmodern\\fcharset0 ").Append(glyphs.FontName).Append(";}}");
            sb.Append("{\\colortbl ;\\red").Append(bg.R).Append("\\green").Append(bg.G).Append("\\blue").Append(bg.B)
              .Append(";\\red").Append(fg.R).Append("\\green").Append(fg.G).Append("\\blue").Append(fg.B).Append(';');
            foreach (uint c in colors)
                sb.Append("\\red").Append((c >> 16) & 255).Append("\\green").Append((c >> 8) & 255).Append("\\blue").Append(c & 255).Append(';');
            sb.Append("}\r\n");
            int lineTwips = (int)Math.Round(glyphs.CellHeightEm * pt * 20);
            sb.Append("\\viewkind4\\pard\\sl-").Append(lineTwips).Append("\\slmult0\\f0\\fs").Append((int)(pt * 2)).Append("\\cb1 ");
            sb.Append(body).Append('}');
            return sb.ToString();
        }

        // ---------------- Clipboard ----------------

        /// <summary>Plain text, RTF and HTML together: a text box gets the characters, Word or WordPad get the colours too.</summary>
        public static System.Windows.DataObject BuildClipboard(AsciiGrid grid, AsciiSettings s, AsciiGlyphs glyphs, bool rich)
        {
            var data = new System.Windows.DataObject();
            string plain = ToPlainText(grid);
            data.SetText(plain, System.Windows.TextDataFormat.UnicodeText);
            if (rich)
            {
                data.SetData(System.Windows.DataFormats.Rtf, ToRtf(grid, s, glyphs));
                data.SetData(System.Windows.DataFormats.Html, ClipboardHtml(grid, s, glyphs));
            }
            return data;
        }

        // ---------------- Save ----------------

        public sealed record SaveResult(string Path, bool PngReduced);

        /// <summary>
        /// Saves in the format the file name asks for. The file is written to a temporary name and moved, and an
        /// existing file is never replaced (a number is added). <c>sourceWidth</c> sizes the PNG like the original picture (0 = a standard size).
        /// </summary>
        public static async Task<SaveResult> SaveAsync(string path, AsciiGrid grid, AsciiSettings s, AsciiGlyphs glyphs, int sourceWidth)
        {
            string target = SafeFileOps.UniquePath(path);
            var kind = KindFor(target);
            bool reduced = false;
            switch (kind)
            {
                case ExportKind.Png:
                {
                    double cellH = sourceWidth > 0
                        ? (sourceWidth / (double)grid.Cols) / glyphs.CellAspect
                        : 18;
                    var (png, _, _, red) = await ZxingImageHelpers.RunOnStaThread(() => AsciiRender.RenderPng(grid, glyphs, cellH, s.DarkBackground));
                    reduced = red;
                    AtomicFile.WriteAllBytes(target, png);
                    break;
                }
                case ExportKind.Html:
                    AtomicFile.WriteAllText(target, ToHtml(grid, s, glyphs, Path.GetFileNameWithoutExtension(target)), new UTF8Encoding(false));
                    break;
                case ExportKind.Rtf:
                    AtomicFile.WriteAllText(target, ToRtf(grid, s, glyphs), Encoding.ASCII);
                    break;
                case ExportKind.Ansi:
                    AtomicFile.WriteAllText(target, ToAnsi(grid), new UTF8Encoding(false));
                    break;
                default:
                    AtomicFile.WriteAllText(target, ToPlainText(grid), new UTF8Encoding(false));
                    break;
            }
            return new SaveResult(target, reduced);
        }
    }
}
