using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Markdown → HTML (CommonMark core + GitHub tables, strikethrough, task lists
    /// and bare-URL links). Pure logic, no WPF. Raw HTML in the source is shown as
    /// text, not rendered, and link/image URLs with script schemes are dropped,
    /// so an exported page is safe to open in a browser.
    /// </summary>
    public sealed partial class MarkdownHtmlEditorManager
    {
        // Placeholders survive HTML escaping and the emphasis regexes.
        private const char PhOpen = '\u0001';
        private const char PhClose = '\u0002';
        private const char HardBreak = '\u0003';

        private static readonly Regex AtxHeading = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex ThematicBreak = new(@"^ {0,3}(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$", RegexOptions.Compiled);
        private static readonly Regex FenceOpen = new(@"^( {0,3})(`{3,}|~{3,})[ \t]*([^`\s]*)", RegexOptions.Compiled);
        private static readonly Regex BlockQuoteLine = new(@"^ {0,3}> ?(.*)$", RegexOptions.Compiled);
        private static readonly Regex ListItem = new(@"^( {0,3})([-*+]|\d{1,9}[.)])(?:([ \t]+)(.*)|$)", RegexOptions.Compiled);
        private static readonly Regex SetextH1 = new(@"^ {0,3}=+[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex SetextH2 = new(@"^ {0,3}-+[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex TableDelimiter = new(@"^ {0,3}\|?[ \t]*:?-+:?[ \t]*(?:\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex TaskMarker = new(@"^\[([ xX])\][ \t]+", RegexOptions.Compiled);

        private static readonly Regex CodeSpan = new(@"(?<!`)(`+)(?!`)(.+?)(?<!`)\1(?!`)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex BackslashEscape = new(@"\\([\\`*_{}\[\]()#+\-.!|~>])", RegexOptions.Compiled);
        private static readonly Regex AngleAutolink = new(@"<((?:https?://|mailto:)[^\s<>]+)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // URL part allows one level of balanced parentheses, e.g. https://en.wikipedia.org/wiki/Foo_(bar)
        private static readonly Regex Image = new(@"!\[([^\]]*)\]\(\s*<?((?:[^()\s<>]|\([^()\s<>]*\))*)>?(?:\s+""([^""]*)"")?\s*\)", RegexOptions.Compiled);
        private static readonly Regex Link = new(@"\[((?:[^\[\]]|\[[^\]]*\])*)\]\(\s*<?((?:[^()\s<>]|\([^()\s<>]*\))*)>?(?:\s+""([^""]*)"")?\s*\)", RegexOptions.Compiled);
        private static readonly Regex BareUrl = new(@"(?<![\w""'=/\u0001])(https?://[^\s<\u0001\u0002]*[^\s<.,:;""')\]\u0001\u0002])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StrongEm = new(@"\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*", RegexOptions.Compiled);
        private static readonly Regex StrongStar = new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", RegexOptions.Compiled);
        private static readonly Regex StrongUnderscore = new(@"(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)", RegexOptions.Compiled);
        private static readonly Regex EmStar = new(@"\*(?=\S)(.+?)(?<=\S)\*", RegexOptions.Compiled);
        private static readonly Regex EmUnderscore = new(@"(?<!\w)_(?=\S)(.+?)(?<=\S)_(?!\w)", RegexOptions.Compiled);
        private static readonly Regex Strike = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);
        private static readonly Regex Placeholder = new("\u0001(\\d+)\u0002", RegexOptions.Compiled);

        public static string MarkdownToHtml(string markdown)
        {
            var lines = (markdown ?? string.Empty)
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(ExpandLeadingTabs)
                .ToList();

            var ctx = new RenderContext();
            var sb = new StringBuilder();
            RenderBlocks(lines, sb, ctx, tight: false);
            return sb.ToString();
        }

        private sealed class RenderContext
        {
            public Dictionary<string, int> Slugs { get; } = new(StringComparer.Ordinal);
        }

        // =================================================================
        //  Blocks
        // =================================================================

        private static void RenderBlocks(List<string> lines, StringBuilder sb, RenderContext ctx, bool tight)
        {
            int i = 0;
            while (i < lines.Count)
            {
                string line = lines[i];

                if (IsBlank(line)) { i++; continue; }

                Match m;
                if ((m = FenceOpen.Match(line)).Success)
                {
                    i = RenderFence(lines, i, m, sb);
                    continue;
                }

                if ((m = AtxHeading.Match(line)).Success)
                {
                    AppendHeading(sb, ctx, m.Groups[1].Value.Length, m.Groups[2].Value);
                    i++;
                    continue;
                }

                if (ThematicBreak.IsMatch(line))
                {
                    sb.Append("<hr>\n");
                    i++;
                    continue;
                }

                if (BlockQuoteLine.IsMatch(line))
                {
                    i = RenderBlockQuote(lines, i, sb, ctx);
                    continue;
                }

                if (ListItem.IsMatch(line))
                {
                    i = RenderList(lines, i, sb, ctx);
                    continue;
                }

                if (IsTableStart(lines, i))
                {
                    i = RenderTable(lines, i, sb);
                    continue;
                }

                if (IndentOf(line) >= 4)
                {
                    i = RenderIndentedCode(lines, i, sb);
                    continue;
                }

                i = RenderParagraph(lines, i, sb, ctx, tight);
            }
        }

        private static int RenderFence(List<string> lines, int start, Match open, StringBuilder sb)
        {
            int indent = open.Groups[1].Value.Length;
            string fence = open.Groups[2].Value;
            string lang = open.Groups[3].Value;
            var closing = new Regex($"^ {{0,3}}{Regex.Escape(fence[0].ToString())}{{{fence.Length},}}[ \\t]*$");

            var code = new StringBuilder();
            int i = start + 1;
            for (; i < lines.Count; i++)
            {
                if (closing.IsMatch(lines[i])) { i++; break; }
                string l = lines[i];
                int strip = Math.Min(indent, IndentOf(l));
                code.Append(l, strip, l.Length - strip).Append('\n');
            }

            sb.Append("<pre><code");
            if (lang.Length > 0) sb.Append(" class=\"language-").Append(Escape(lang)).Append('"');
            sb.Append('>').Append(Escape(code.ToString())).Append("</code></pre>\n");
            return i;
        }

        private static int RenderIndentedCode(List<string> lines, int start, StringBuilder sb)
        {
            var code = new List<string>();
            int i = start;
            while (i < lines.Count && (IndentOf(lines[i]) >= 4 || IsBlank(lines[i])))
            {
                code.Add(lines[i].Length >= 4 ? lines[i][4..] : string.Empty);
                i++;
            }
            while (code.Count > 0 && code[^1].Length == 0) code.RemoveAt(code.Count - 1);

            sb.Append("<pre><code>").Append(Escape(string.Join("\n", code) + "\n")).Append("</code></pre>\n");
            return i;
        }

        private static int RenderBlockQuote(List<string> lines, int start, StringBuilder sb, RenderContext ctx)
        {
            var inner = new List<string>();
            int i = start;
            while (i < lines.Count)
            {
                var m = BlockQuoteLine.Match(lines[i]);
                if (m.Success)
                {
                    inner.Add(m.Groups[1].Value);
                    i++;
                }
                else if (!IsBlank(lines[i]) && inner.Count > 0 && !IsBlank(inner[^1]) && !StartsBlock(lines[i]))
                {
                    inner.Add(lines[i]);   // lazy continuation of a quoted paragraph
                    i++;
                }
                else break;
            }

            sb.Append("<blockquote>\n");
            RenderBlocks(inner, sb, ctx, tight: false);
            sb.Append("</blockquote>\n");
            return i;
        }

        private static int RenderList(List<string> lines, int start, StringBuilder sb, RenderContext ctx)
        {
            var first = ListItem.Match(lines[start]);
            string marker = first.Groups[2].Value;
            bool ordered = char.IsDigit(marker[0]);
            char kind = marker[^1];   // '-', '*', '+', '.' or ')'
            int startNumber = ordered && int.TryParse(marker[..^1], out int n) ? n : 1;

            var items = new List<List<string>>();
            bool loose = false;
            int i = start;

            while (i < lines.Count)
            {
                var m = ListItem.Match(lines[i]);
                if (!m.Success || m.Groups[2].Value[^1] != kind || char.IsDigit(m.Groups[2].Value[0]) != ordered) break;

                int lead = m.Groups[1].Value.Length;
                int gap = m.Groups[3].Success ? m.Groups[3].Value.Length : 1;
                string rest = m.Groups[4].Success ? m.Groups[4].Value : string.Empty;
                if (gap > 4 || rest.Length == 0) { rest = (gap > 4 ? new string(' ', gap - 1) : string.Empty) + rest; gap = 1; }
                int contentIndent = lead + m.Groups[2].Value.Length + gap;

                var item = new List<string> { rest };
                i++;

                while (i < lines.Count)
                {
                    string l = lines[i];
                    if (IsBlank(l))
                    {
                        int next = i + 1;
                        while (next < lines.Count && IsBlank(lines[next])) next++;
                        if (next >= lines.Count) { i = next; break; }

                        if (IndentOf(lines[next]) >= contentIndent)
                        {
                            for (int k = i; k < next; k++) item.Add(string.Empty);
                            i = next;
                            loose = true;
                            continue;
                        }

                        var sibling = ListItem.Match(lines[next]);
                        if (sibling.Success && sibling.Groups[2].Value[^1] == kind && IndentOf(lines[next]) < contentIndent)
                            loose = true;
                        i = next;
                        break;
                    }

                    if (IndentOf(l) >= contentIndent)
                    {
                        item.Add(l[contentIndent..]);
                        i++;
                        continue;
                    }

                    if (ListItem.IsMatch(l) || StartsBlock(l) || IsBlank(item[^1])) break;

                    item.Add(l.TrimStart());   // lazy paragraph continuation
                    i++;
                }

                items.Add(item);
                if (i < lines.Count && !ListItem.IsMatch(lines[i])) break;
            }

            if (ordered)
            {
                sb.Append("<ol");
                if (startNumber != 1) sb.Append(" start=\"").Append(startNumber).Append('"');
                sb.Append(">\n");
            }
            else sb.Append("<ul>\n");

            foreach (var item in items)
            {
                var task = TaskMarker.Match(item[0]);
                if (task.Success)
                {
                    bool done = task.Groups[1].Value != " ";
                    item[0] = item[0][task.Length..];
                    sb.Append("<li class=\"task\"><input type=\"checkbox\" disabled").Append(done ? " checked" : string.Empty).Append("> ");
                }
                else sb.Append("<li>");

                var inner = new StringBuilder();
                RenderBlocks(item, inner, ctx, tight: !loose);
                sb.Append(inner.ToString().TrimEnd('\n')).Append("</li>\n");
            }

            sb.Append(ordered ? "</ol>\n" : "</ul>\n");
            return i;
        }

        private static bool IsTableStart(List<string> lines, int i) =>
            i + 1 < lines.Count && lines[i].Contains('|') && TableDelimiter.IsMatch(lines[i + 1]) && lines[i + 1].Contains('-');

        private static int RenderTable(List<string> lines, int start, StringBuilder sb)
        {
            var header = SplitRow(lines[start]);
            var aligns = SplitRow(lines[start + 1]).Select(c =>
            {
                string t = c.Trim();
                bool left = t.StartsWith(':'), right = t.EndsWith(':');
                return left && right ? "center" : right ? "right" : left ? "left" : null;
            }).ToList();

            sb.Append("<table>\n<thead>\n<tr>");
            for (int c = 0; c < header.Count; c++)
                AppendCell(sb, "th", header[c], c < aligns.Count ? aligns[c] : null);
            sb.Append("</tr>\n</thead>\n");

            int i = start + 2;
            bool bodyOpen = false;
            for (; i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|') && !StartsBlock(lines[i]); i++)
            {
                if (!bodyOpen) { sb.Append("<tbody>\n"); bodyOpen = true; }
                var cells = SplitRow(lines[i]);
                sb.Append("<tr>");
                for (int c = 0; c < header.Count; c++)
                    AppendCell(sb, "td", c < cells.Count ? cells[c] : string.Empty, c < aligns.Count ? aligns[c] : null);
                sb.Append("</tr>\n");
            }
            if (bodyOpen) sb.Append("</tbody>\n");
            sb.Append("</table>\n");
            return i;
        }

        private static void AppendCell(StringBuilder sb, string tag, string content, string? align)
        {
            sb.Append('<').Append(tag);
            if (align != null) sb.Append(" style=\"text-align:").Append(align).Append('"');
            sb.Append('>').Append(RenderInline(content.Trim())).Append("</").Append(tag).Append('>');
        }

        private static List<string> SplitRow(string row)
        {
            string t = row.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t[..^1];

            var cells = new List<string>();
            var cell = new StringBuilder();
            for (int k = 0; k < t.Length; k++)
            {
                if (t[k] == '\\' && k + 1 < t.Length && t[k + 1] == '|') { cell.Append('|'); k++; }
                else if (t[k] == '|') { cells.Add(cell.ToString()); cell.Clear(); }
                else cell.Append(t[k]);
            }
            cells.Add(cell.ToString());
            return cells;
        }

        private static int RenderParagraph(List<string> lines, int start, StringBuilder sb, RenderContext ctx, bool tight)
        {
            var para = new List<string>();
            int i = start;
            while (i < lines.Count && !IsBlank(lines[i]))
            {
                string l = lines[i];
                if (para.Count > 0)
                {
                    if (SetextH1.IsMatch(l) || SetextH2.IsMatch(l))
                    {
                        AppendHeading(sb, ctx, SetextH1.IsMatch(l) ? 1 : 2, string.Join(" ", para.Select(p => p.Trim())));
                        return i + 1;
                    }
                    if (StartsBlock(l) || IsTableStart(lines, i)) break;
                }
                para.Add(l);
                i++;
            }

            var text = new StringBuilder();
            for (int k = 0; k < para.Count; k++)
            {
                string l = para[k].TrimStart();
                bool isLast = k == para.Count - 1;
                if (!isLast && l.EndsWith("  ")) text.Append(l.TrimEnd()).Append(HardBreak);
                else if (!isLast && l.EndsWith('\\')) text.Append(l[..^1]).Append(HardBreak);
                else text.Append(l.TrimEnd()).Append(isLast ? string.Empty : "\n");
            }

            string html = RenderInline(text.ToString()).Replace(HardBreak.ToString(), "<br>\n");
            if (tight) sb.Append(html).Append('\n');
            else sb.Append("<p>").Append(html).Append("</p>\n");
            return i;
        }

        private static void AppendHeading(StringBuilder sb, RenderContext ctx, int level, string text)
        {
            string slug = Slugify(text);
            if (ctx.Slugs.TryGetValue(slug, out int count))
            {
                ctx.Slugs[slug] = count + 1;
                slug = $"{slug}-{count}";
            }
            else ctx.Slugs[slug] = 1;

            sb.Append("<h").Append(level).Append(" id=\"").Append(slug).Append("\">")
              .Append(RenderInline(text.Trim()))
              .Append("</h").Append(level).Append(">\n");
        }

        /// <summary>GitHub-style anchor: lower case, spaces to hyphens, punctuation dropped.</summary>
        public static string Slugify(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            return sb.Length > 0 ? sb.ToString() : "section";
        }

        private static bool StartsBlock(string line) =>
            AtxHeading.IsMatch(line) || ThematicBreak.IsMatch(line) || FenceOpen.IsMatch(line)
            || BlockQuoteLine.IsMatch(line) || ListItem.IsMatch(line);

        private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

        private static int IndentOf(string line)
        {
            int n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            return n;
        }

        private static string ExpandLeadingTabs(string line)
        {
            if (!line.Contains('\t')) return line;
            var sb = new StringBuilder();
            int k = 0;
            for (; k < line.Length && (line[k] == ' ' || line[k] == '\t'); k++)
            {
                if (line[k] == '\t') sb.Append(' ', 4 - sb.Length % 4);
                else sb.Append(' ');
            }
            return sb.Append(line, k, line.Length - k).ToString();
        }

        // =================================================================
        //  Inline
        // =================================================================

        private static string RenderInline(string text)
        {
            var slots = new List<string>();
            string Hold(string html) { slots.Add(html); return $"{PhOpen}{slots.Count - 1}{PhClose}"; }

            text = CodeSpan.Replace(text, m =>
            {
                string code = m.Groups[2].Value.Replace('\n', ' ');
                if (code.Length > 1 && code.StartsWith(' ') && code.EndsWith(' ') && code.Trim().Length > 0) code = code[1..^1];
                return Hold("<code>" + Escape(code) + "</code>");
            });

            text = BackslashEscape.Replace(text, m => Hold(Escape(m.Groups[1].Value)));

            text = AngleAutolink.Replace(text, m => Hold(AnchorHtml(m.Groups[1].Value, Escape(m.Groups[1].Value), null)));

            text = Image.Replace(text, m =>
            {
                string? src = SafeUrl(m.Groups[2].Value, allowDataImage: true);
                if (src == null) return Hold(Escape(m.Groups[1].Value));
                var img = new StringBuilder("<img src=\"").Append(Escape(src)).Append("\" alt=\"").Append(Escape(m.Groups[1].Value)).Append('"');
                if (m.Groups[3].Success) img.Append(" title=\"").Append(Escape(m.Groups[3].Value)).Append('"');
                return Hold(img.Append('>').ToString());
            });

            text = Link.Replace(text, m =>
            {
                string inner = RenderInline(m.Groups[1].Value);
                return Hold(AnchorHtml(m.Groups[2].Value, inner, m.Groups[3].Success ? m.Groups[3].Value : null));
            });

            text = BareUrl.Replace(text, m => Hold(AnchorHtml(m.Groups[1].Value, Escape(m.Groups[1].Value), null)));

            text = Escape(text);
            text = StrongEm.Replace(text, "<strong><em>$1</em></strong>");
            text = StrongStar.Replace(text, "<strong>$1</strong>");
            text = StrongUnderscore.Replace(text, "<strong>$1</strong>");
            text = EmStar.Replace(text, "<em>$1</em>");
            text = EmUnderscore.Replace(text, "<em>$1</em>");
            text = Strike.Replace(text, "<del>$1</del>");

            // Slots can contain other slots (a link holding code), so restore until none are left.
            for (int pass = 0; pass < 8 && text.Contains(PhOpen); pass++)
                text = Placeholder.Replace(text, m => slots[int.Parse(m.Groups[1].Value)]);
            return text;
        }

        private static string AnchorHtml(string url, string innerHtml, string? title)
        {
            string? href = SafeUrl(url, allowDataImage: false);
            if (href == null) return innerHtml;
            var a = new StringBuilder("<a href=\"").Append(Escape(href)).Append('"');
            if (title != null) a.Append(" title=\"").Append(Escape(title)).Append('"');
            return a.Append('>').Append(innerHtml).Append("</a>").ToString();
        }

        /// <summary>Drops javascript:, vbscript: and (except images) data: URLs. Relative paths and anchors pass.</summary>
        public static string? SafeUrl(string url, bool allowDataImage)
        {
            string u = url.Trim();
            string probe = new string(u.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToLowerInvariant();
            int colon = probe.IndexOf(':');
            int slash = probe.IndexOfAny(['/', '?', '#']);
            if (colon < 0 || (slash >= 0 && slash < colon)) return u;   // relative

            string scheme = probe[..colon];
            if (scheme is "http" or "https" or "mailto" or "tel" or "ftp") return u;
            if (allowDataImage && probe.StartsWith("data:image/") && !probe.StartsWith("data:image/svg")) return u;
            if (scheme.Length == 1 && char.IsLetter(scheme[0])) return u;   // C:\ path
            return null;
        }

        /// <summary>Escapes only what HTML needs, so accented text stays readable in exported files.</summary>
        private static string Escape(string s)
        {
            if (s.AsSpan().IndexOfAny("&<>\"") < 0) return s;
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                sb.Append(c switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    '"' => "&quot;",
                    _ => c.ToString(),
                });
            }
            return sb.ToString();
        }
    }
}
