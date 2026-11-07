using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// MD &amp; HTML Editor backend: documents (open/save), toolbar edits, stats,
    /// the preview/export page and settings. No WPF types - the WebView2 preview
    /// is hosted by the view; the rules for what it may load live here.
    /// </summary>
    public sealed partial class MarkdownHtmlEditorManager
    {
        public const long MaxDocumentBytes = 10L * 1024 * 1024;

        /// <summary>Virtual host the preview is served from (WebView2 WebResourceRequested).</summary>
        public const string PreviewHost = "mdhtml.preview";
        public const string PreviewDocumentPath = "/doc/";
        public static readonly string PreviewShellUrl = $"https://{PreviewHost}{PreviewDocumentPath}__smarttools_preview__.html";

        public const string PreviewShellHtml = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body></body></html>";

        public static readonly string[] OpenExtensions = [".md", ".markdown", ".mdown", ".mkd", ".txt", ".html", ".htm", ".xhtml"];

        private static readonly string[] HtmlExtensions = [".html", ".htm", ".xhtml"];

        private static readonly Dictionary<string, string> AssetMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".ico"] = "image/x-icon",
            [".avif"] = "image/avif",
            [".svg"] = "image/svg+xml",
            [".css"] = "text/css",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf",
            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm",
            [".mp3"] = "audio/mpeg",
            [".ogg"] = "audio/ogg",
            [".wav"] = "audio/wav",
        };

        // =================================================================
        //  Documents
        // =================================================================

        public static MdHtmlFormat FormatFromPath(string? path) =>
            path != null && HtmlExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                ? MdHtmlFormat.Html
                : MdHtmlFormat.Markdown;

        public static bool CanOpen(string path) =>
            OpenExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        public static string DefaultExtension(MdHtmlFormat format) => format == MdHtmlFormat.Html ? ".html" : ".md";

        /// <summary>Reads a document (BOM-aware, UTF-8 otherwise). Throws <see cref="InvalidDataException"/> when it's too big to edit.</summary>
        public static async Task<string> ReadDocumentAsync(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxDocumentBytes)
                throw new InvalidDataException($"{info.Length / (1024 * 1024)} MB");
            return await File.ReadAllTextAsync(path, Encoding.UTF8);
        }

        /// <summary>
        /// Writes UTF-8 (no BOM) to a temporary file next to the target, then swaps it in.
        /// Synchronous on purpose: it also runs from the app's closing prompt, which can't await.
        /// </summary>
        public static void SaveDocument(string path, string text)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string temp = Path.Combine(folder, $".{Path.GetFileName(path)}.saving-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }

        // =================================================================
        //  Preview / export pages
        // =================================================================

        /// <summary>The page shown in the preview: Markdown is rendered and themed; HTML is shown as written.</summary>
        public static string BuildPreviewPage(string source, MdHtmlFormat format, MdHtmlPreviewColors colors, string title) =>
            format == MdHtmlFormat.Html ? source : BuildMarkdownPage(MarkdownToHtml(source), colors, title);

        /// <summary>A standalone file: Markdown becomes a light page with its styles inlined; HTML is kept as is.</summary>
        public static string BuildExportPage(string source, MdHtmlFormat format, string title) =>
            format == MdHtmlFormat.Html ? source : BuildMarkdownPage(MarkdownToHtml(source), MdHtmlPreviewColors.Light, title);

        private static string BuildMarkdownPage(string bodyHtml, MdHtmlPreviewColors c, string title)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
              .Append("<title>").Append(Escape(title)).Append("</title>\n<style>\n")
              .Append(":root{color-scheme:").Append(IsDark(c.Background) ? "dark" : "light").Append(";}\n")
              .Append("html{background:").Append(c.Background).Append(";}\n")
              .Append("body{margin:0 auto;max-width:860px;padding:28px 32px 48px;color:").Append(c.Text)
              .Append(";background:").Append(c.Background)
              .Append(";font:15px/1.65 'Segoe UI',system-ui,-apple-system,sans-serif;word-wrap:break-word;}\n")
              .Append("h1,h2,h3,h4,h5,h6{margin:1.5em 0 .6em;font-weight:600;line-height:1.25;}\n")
              .Append("h1{font-size:2em;padding-bottom:.3em;border-bottom:1px solid ").Append(c.Border).Append(";}\n")
              .Append("h2{font-size:1.5em;padding-bottom:.3em;border-bottom:1px solid ").Append(c.Border).Append(";}\n")
              .Append("h3{font-size:1.25em;}h4{font-size:1em;}h5{font-size:.875em;}h6{font-size:.85em;color:").Append(c.Muted).Append(";}\n")
              .Append("body>:first-child{margin-top:0;}\n")
              .Append("p,ul,ol,blockquote,pre,table{margin:0 0 1em;}\n")
              .Append("a{color:").Append(c.Accent).Append(";text-decoration:none;}a:hover{text-decoration:underline;}\n")
              .Append("code{font-family:Consolas,'Cascadia Mono',monospace;font-size:.875em;padding:.15em .4em;border-radius:6px;background:").Append(c.CodeBackground).Append(";}\n")
              .Append("pre{padding:14px 16px;overflow:auto;border-radius:8px;background:").Append(c.CodeBackground).Append(";line-height:1.45;}\n")
              .Append("pre code{padding:0;background:none;font-size:.86em;}\n")
              .Append("blockquote{margin-left:0;padding:0 1em;color:").Append(c.Muted).Append(";border-left:4px solid ").Append(c.Border).Append(";}\n")
              .Append("ul,ol{padding-left:2em;}li+li{margin-top:.25em;}\n")
              .Append("li.task{list-style:none;}li.task input{margin:0 .4em 0 -1.4em;vertical-align:middle;}\n")
              .Append("hr{height:3px;border:0;margin:1.5em 0;background:").Append(c.Border).Append(";}\n")
              .Append("table{border-collapse:collapse;display:block;width:max-content;max-width:100%;overflow:auto;}\n")
              .Append("th,td{padding:6px 13px;border:1px solid ").Append(c.Border).Append(";}\n")
              .Append("th{font-weight:600;}tr:nth-child(2n) td{background:").Append(c.CodeBackground).Append(";}\n")
              .Append("img{max-width:100%;}\n")
              .Append("@media print{html,body{background:#fff;color:#1F2328;}body{max-width:none;padding:0;}")
              .Append("a{color:#0969DA;}code,pre,tr:nth-child(2n) td{background:#F6F8FA;}")
              .Append("blockquote,h6{color:#59636E;}h1,h2,th,td,hr,blockquote{border-color:#D1D9E0;}hr{background:#D1D9E0;}pre{white-space:pre-wrap;}}\n")
              .Append("</style>\n</head>\n<body>\n")
              .Append(bodyHtml)
              .Append("</body>\n</html>\n");
            return sb.ToString();
        }

        private static bool IsDark(string hex)
        {
            if (hex.Length != 7) return false;
            int r = Convert.ToInt32(hex.Substring(1, 2), 16), g = Convert.ToInt32(hex.Substring(3, 2), 16), b = Convert.ToInt32(hex.Substring(5, 2), 16);
            return (r * 299 + g * 587 + b * 114) / 1000 < 128;
        }

        /// <summary>
        /// Maps a preview request (path under <see cref="PreviewDocumentPath"/>) to a file in the
        /// document's folder. Only media, style and font files inside that folder are served -
        /// never anything above it, and never pages or scripts.
        /// </summary>
        public static string? ResolvePreviewAsset(string? documentFolder, string requestPath, out string mimeType)
        {
            mimeType = string.Empty;
            if (string.IsNullOrEmpty(documentFolder) || !requestPath.StartsWith(PreviewDocumentPath, StringComparison.Ordinal))
                return null;

            string relative = Uri.UnescapeDataString(requestPath[PreviewDocumentPath.Length..]).Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0 || !AssetMimeTypes.TryGetValue(Path.GetExtension(relative), out var mime))
                return null;

            string root = Path.GetFullPath(documentFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, relative)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            mimeType = mime;
            return full;
        }

        // =================================================================
        //  Toolbar edits
        // =================================================================

        /// <summary>Placeholder words used when a command runs with nothing selected (localized by the view).</summary>
        public sealed record Placeholders(string Text, string Link, string Image, string Column, string Cell);

        public static MdHtmlEdit Apply(MdHtmlCommand command, MdHtmlFormat format, string text, int selStart, int selLength, Placeholders ph)
        {
            var edit = ApplyCore(command, format, text, selStart, selLength, ph);
            return text.Contains("\r\n") ? ToCrLf(edit) : edit;
        }

        /// <summary>Snippets are built with \n; a document typed in the TextBox uses \r\n, so match it and shift the selection.</summary>
        private static MdHtmlEdit ToCrLf(MdHtmlEdit e)
        {
            int Added(string s, int upTo)
            {
                int n = 0;
                for (int k = 0; k < upTo && k < s.Length; k++)
                    if (s[k] == '\n' && (k == 0 || s[k - 1] != '\r')) n++;
                return n;
            }

            string r = e.Replacement;
            int relStart = Math.Clamp(e.SelectStart - e.ReplaceStart, 0, r.Length);
            int relEnd = Math.Clamp(relStart + e.SelectLength, 0, r.Length);
            int newStart = relStart + Added(r, relStart);
            int newEnd = relEnd + Added(r, relEnd);
            string converted = Regex.Replace(r, "(?<!\r)\n", "\r\n");

            return e with { Replacement = converted, SelectStart = e.ReplaceStart + newStart, SelectLength = newEnd - newStart };
        }

        private static MdHtmlEdit ApplyCore(MdHtmlCommand command, MdHtmlFormat format, string text, int selStart, int selLength, Placeholders ph)
        {
            bool html = format == MdHtmlFormat.Html;
            string selected = text.Substring(selStart, selLength);

            switch (command)
            {
                case MdHtmlCommand.Bold: return html ? Wrap(text, selStart, selLength, "<strong>", "</strong>", ph.Text) : Wrap(text, selStart, selLength, "**", "**", ph.Text);
                case MdHtmlCommand.Italic: return html ? Wrap(text, selStart, selLength, "<em>", "</em>", ph.Text) : Wrap(text, selStart, selLength, "*", "*", ph.Text);
                case MdHtmlCommand.Strikethrough: return html ? Wrap(text, selStart, selLength, "<del>", "</del>", ph.Text) : Wrap(text, selStart, selLength, "~~", "~~", ph.Text);

                case MdHtmlCommand.Code:
                    if (selLength > 0 && !selected.Contains('\n'))
                        return html ? Wrap(text, selStart, selLength, "<code>", "</code>", ph.Text) : Wrap(text, selStart, selLength, "`", "`", ph.Text);
                    return html
                        ? InsertBlock(text, selStart, selLength, "<pre><code>", selected, "</code></pre>", ph.Text)
                        : InsertBlock(text, selStart, selLength, "```\n", selected, "\n```", ph.Text);

                case MdHtmlCommand.Heading1:
                case MdHtmlCommand.Heading2:
                    int level = command == MdHtmlCommand.Heading1 ? 1 : 2;
                    return html
                        ? WrapLines(text, selStart, selLength, l => $"<h{level}>{l}</h{level}>")
                        : PrefixLines(text, selStart, selLength, _ => new string('#', level) + " ", stripHeading: true);

                case MdHtmlCommand.BulletList:
                    return html ? WrapLinesInContainer(text, selStart, selLength, "ul") : PrefixLines(text, selStart, selLength, _ => "- ");
                case MdHtmlCommand.NumberedList:
                    return html ? WrapLinesInContainer(text, selStart, selLength, "ol") : PrefixLines(text, selStart, selLength, i => $"{i + 1}. ");
                case MdHtmlCommand.Quote:
                    return html ? WrapBlockAroundLines(text, selStart, selLength, "<blockquote>", "</blockquote>") : PrefixLines(text, selStart, selLength, _ => "> ");

                case MdHtmlCommand.Link:
                {
                    string label = selLength > 0 && !selected.Contains('\n') ? selected : ph.Link;
                    const string url = "https://";
                    string snippet = html ? $"<a href=\"{url}\">{label}</a>" : $"[{label}]({url})";
                    int urlAt = selStart + snippet.IndexOf(url, StringComparison.Ordinal);
                    return new MdHtmlEdit(selStart, selLength, snippet, urlAt, url.Length);
                }

                case MdHtmlCommand.Image:
                {
                    string alt = selLength > 0 && !selected.Contains('\n') ? selected : ph.Image;
                    const string src = "image.png";
                    string snippet = html ? $"<img src=\"{src}\" alt=\"{alt}\">" : $"![{alt}]({src})";
                    int srcAt = selStart + snippet.IndexOf(src, StringComparison.Ordinal);
                    return new MdHtmlEdit(selStart, selLength, snippet, srcAt, src.Length);
                }

                case MdHtmlCommand.Table:
                {
                    string c = ph.Column, d = ph.Cell;
                    string table = html
                        ? $"<table>\n  <tr><th>{c} 1</th><th>{c} 2</th><th>{c} 3</th></tr>\n  <tr><td>{d}</td><td>{d}</td><td>{d}</td></tr>\n  <tr><td>{d}</td><td>{d}</td><td>{d}</td></tr>\n</table>"
                        : $"| {c} 1 | {c} 2 | {c} 3 |\n| --- | --- | --- |\n| {d} | {d} | {d} |\n| {d} | {d} | {d} |";
                    return InsertBlock(text, selStart, selLength, string.Empty, table, string.Empty, table, selectInner: $"{c} 1");
                }
            }

            return new MdHtmlEdit(selStart, selLength, selected, selStart, selLength);
        }

        /// <summary>Wraps the selection in markers, or removes them when they're already there.</summary>
        private static MdHtmlEdit Wrap(string text, int start, int length, string before, string after, string placeholder)
        {
            if (start >= before.Length && start + length + after.Length <= text.Length
                && string.CompareOrdinal(text, start - before.Length, before, 0, before.Length) == 0
                && string.CompareOrdinal(text, start + length, after, 0, after.Length) == 0)
            {
                string inner = text.Substring(start, length);
                return new MdHtmlEdit(start - before.Length, length + before.Length + after.Length, inner, start - before.Length, length);
            }

            string content = length > 0 ? text.Substring(start, length) : placeholder;
            return new MdHtmlEdit(start, length, before + content + after, start + before.Length, content.Length);
        }

        /// <summary>Puts a block on lines of its own (adding line breaks around it when needed).</summary>
        private static MdHtmlEdit InsertBlock(string text, int start, int length, string open, string inner, string close, string placeholder, string? selectInner = null)
        {
            string content = inner.Length > 0 ? inner : placeholder;
            string lead = start > 0 && text[start - 1] != '\n' ? "\n\n" : start > 1 && text[start - 2] != '\n' ? "\n" : string.Empty;
            int end = start + length;
            string trail = end < text.Length && text[end] != '\n' && text[end] != '\r' ? "\n\n" : string.Empty;
            string block = lead + open + content + close + trail;

            int innerAt = start + lead.Length + open.Length;
            if (selectInner != null)
            {
                int at = content.IndexOf(selectInner, StringComparison.Ordinal);
                return new MdHtmlEdit(start, length, block, at >= 0 ? innerAt + at : innerAt, at >= 0 ? selectInner.Length : 0);
            }
            return new MdHtmlEdit(start, length, block, innerAt, content.Length);
        }

        /// <summary>Selection expanded to whole lines: [lineStart, lineEnd) without the final line break.</summary>
        private static (int Start, int End) LineRange(string text, int start, int length)
        {
            int lineStart = start > 0 ? text.LastIndexOf('\n', start - 1) + 1 : 0;
            int last = Math.Max(start, start + length - (length > 0 && text[start + length - 1] == '\n' ? 1 : 0));
            int lineEnd = text.IndexOf('\n', last);
            if (lineEnd < 0) lineEnd = text.Length;
            if (lineEnd > lineStart && text[lineEnd - 1] == '\r') lineEnd--;
            return (lineStart, lineEnd);
        }

        private static string[] SplitKeepingCr(string block, out string newline)
        {
            newline = block.Contains("\r\n") ? "\r\n" : "\n";
            return block.Split(newline);
        }

        private static readonly Regex HeadingPrefix = new(@"^#{1,6}[ \t]+", RegexOptions.Compiled);

        /// <summary>Adds a prefix to every selected line, or removes it when every non-empty line already has it.</summary>
        private static MdHtmlEdit PrefixLines(string text, int start, int length, Func<int, string> prefixFor, bool stripHeading = false)
        {
            var (from, to) = LineRange(text, start, length);
            var lines = SplitKeepingCr(text[from..to], out string nl);

            bool allHave = lines.Where(l => l.Trim().Length > 0).Select((l, i) => l.StartsWith(prefixFor(i), StringComparison.Ordinal)).DefaultIfEmpty(false).All(x => x);

            int n = 0;
            var result = lines.Select(l =>
            {
                if (allHave) return l.Trim().Length == 0 ? l : l[prefixFor(n++).Length..];
                string body = stripHeading ? HeadingPrefix.Replace(l, string.Empty) : l;
                return prefixFor(n++) + body;
            });

            string replaced = string.Join(nl, result);
            return new MdHtmlEdit(from, to - from, replaced, from + replaced.Length, 0);
        }

        private static MdHtmlEdit WrapLines(string text, int start, int length, Func<string, string> wrap)
        {
            var (from, to) = LineRange(text, start, length);
            var lines = SplitKeepingCr(text[from..to], out string nl);
            string replaced = string.Join(nl, lines.Select(l => l.Trim().Length == 0 ? l : wrap(l.Trim())));
            return new MdHtmlEdit(from, to - from, replaced, from + replaced.Length, 0);
        }

        private static MdHtmlEdit WrapLinesInContainer(string text, int start, int length, string tag)
        {
            var (from, to) = LineRange(text, start, length);
            var lines = SplitKeepingCr(text[from..to], out string nl);
            var items = lines.Where(l => l.Trim().Length > 0).Select(l => $"  <li>{l.Trim()}</li>").DefaultIfEmpty("  <li></li>");
            string replaced = $"<{tag}>{nl}{string.Join(nl, items)}{nl}</{tag}>";
            return new MdHtmlEdit(from, to - from, replaced, from + replaced.Length, 0);
        }

        private static MdHtmlEdit WrapBlockAroundLines(string text, int start, int length, string open, string close)
        {
            var (from, to) = LineRange(text, start, length);
            var lines = SplitKeepingCr(text[from..to], out string nl);
            string replaced = $"{open}{nl}{string.Join(nl, lines.Select(l => "  " + l))}{nl}{close}";
            return new MdHtmlEdit(from, to - from, replaced, from + replaced.Length, 0);
        }

        // =================================================================
        //  Status bar
        // =================================================================

        public static MdHtmlCaret CaretPosition(string text, int index)
        {
            index = Math.Clamp(index, 0, text.Length);
            int line = 1, lineStart = 0;
            for (int k = 0; k < index; k++)
            {
                if (text[k] == '\n') { line++; lineStart = k + 1; }
            }
            return new MdHtmlCaret(line, index - lineStart + 1);
        }

        private static readonly Regex HtmlTag = new(@"<[^>]*>", RegexOptions.Compiled);

        public static MdHtmlStats Stats(string text, MdHtmlFormat format)
        {
            string words = format == MdHtmlFormat.Html ? HtmlTag.Replace(text, " ") : text;
            int wordCount = 0;
            bool inWord = false;
            foreach (char c in words)
            {
                bool letter = !char.IsWhiteSpace(c);
                if (letter && !inWord) wordCount++;
                inWord = letter;
            }

            int lines = 1;
            foreach (char c in text) if (c == '\n') lines++;
            return new MdHtmlStats(wordCount, text.Length, lines);
        }

        // =================================================================
        //  Settings (how the tool is set up - never file paths)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "md_html_editor.json");

        public static MdHtmlEditorSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new MdHtmlEditorSettings();
                var s = JsonSerializer.Deserialize<MdHtmlEditorSettings>(File.ReadAllText(file), JsonOptions) ?? new MdHtmlEditorSettings();
                if (!Enum.IsDefined(s.Layout)) s.Layout = MdHtmlLayout.Split;
                if (!Enum.IsDefined(s.PreviewTheme)) s.PreviewTheme = MdHtmlPreviewTheme.App;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new MdHtmlEditorSettings();
            }
        }

        public static void SaveSettings(MdHtmlEditorSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
        }

        /// <summary>WebView2's cache and cookies; kept per-user so an installed copy never writes next to the .exe.</summary>
        public static string WebViewDataFolder => AppPaths.WebViewData;
    }
}
