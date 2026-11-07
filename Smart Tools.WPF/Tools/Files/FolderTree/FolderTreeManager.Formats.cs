using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The web page, XML, YAML and path-list outputs, and the format names.</summary>
    public static partial class FolderTreeManager
    {
        public static bool IsVisual(FolderTreeFormat f) => f is FolderTreeFormat.Pdf or FolderTreeFormat.Image;

        public static string FormatTitle(FolderTreeFormat f) => f switch
        {
            FolderTreeFormat.Markdown => Loc.T("Str_MarkdownHtmlEditor_Markdown", "Markdown"),
            FolderTreeFormat.Html => Loc.T("Str_FolderTree_FmtHtml", "HTML page"),
            FolderTreeFormat.Json => "JSON",
            FolderTreeFormat.Xml => "XML",
            FolderTreeFormat.Yaml => "YAML",
            FolderTreeFormat.Csv => "CSV",
            FolderTreeFormat.Paths => Loc.T("Str_FolderTree_FmtPaths", "List of paths"),
            FolderTreeFormat.Pdf => Loc.T("Str_FolderTree_FmtPdf", "PDF document"),
            FolderTreeFormat.Image => Loc.T("Str_FolderTree_FmtImage", "Picture (PNG)"),
            _ => Loc.T("Str_FolderTree_TextTree", "Text tree"),
        };

        private static string MetaText(FolderTreeNode n, FolderTreeOptions o)
        {
            string meta = Meta(n, o).Trim();
            return meta.Length >= 2 && meta[0] == '[' && meta[^1] == ']' ? meta[1..^1] : meta;
        }

        // =================================================================
        //  HTML: one page, folders fold and unfold
        // =================================================================

        private const string HtmlStyle = @":root{--bg:#fff;--fg:#1f2937;--muted:#6b7280;--line:#e5e7eb;--accent:#d97706;--btn:#f3f4f6}
@media (prefers-color-scheme:dark){:root{--bg:#111827;--fg:#e5e7eb;--muted:#9ca3af;--line:#374151;--accent:#fbbf24;--btn:#1f2937}}
body{font:14px/1.55 system-ui,'Segoe UI',sans-serif;background:var(--bg);color:var(--fg);margin:24px auto;max-width:1100px;padding:0 16px}
h1{font-size:22px;margin:0 0 4px}.path{color:var(--muted);margin:0 0 14px;word-break:break-all}
.bar{margin:0 0 12px}button{font:inherit;color:var(--fg);background:var(--btn);border:1px solid var(--line);border-radius:6px;padding:4px 10px;cursor:pointer}
ul{list-style:none;margin:0;padding-left:20px;border-left:1px solid var(--line)}ul.tree{padding-left:0;border-left:0}
li{padding:1px 0}summary{cursor:pointer}summary::marker{color:var(--accent)}.m{color:var(--muted);font-size:12px;margin-left:8px}
.foot{color:var(--muted);margin-top:16px}
";

        private static string RenderHtml(FolderTreeResult r, FolderTreeOptions o)
        {
            static string E(string s) => WebUtility.HtmlEncode(s);
            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"").Append(E(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>").Append(E(r.Root.Name)).Append("</title>\n<style>\n").Append(HtmlStyle).Append("</style>\n</head>\n<body>\n");
            sb.Append("<h1>📁 ").Append(E(r.Root.Name)).Append("</h1>\n<p class=\"path\">").Append(E(r.Root.FullPath)).Append("</p>\n");
            sb.Append("<div class=\"bar\"><button onclick=\"fold(false)\">").Append(E(Loc.T("Str_FolderTree_HtmlCollapse", "Collapse all")))
              .Append("</button> <button onclick=\"fold(true)\">").Append(E(Loc.T("Str_FolderTree_HtmlExpand", "Expand all"))).Append("</button></div>\n<ul class=\"tree\">\n");

            void Emit(FolderTreeNode n, int depth)
            {
                foreach (var k in Sorted(n))
                {
                    string meta = MetaText(k, o);
                    string metaHtml = meta.Length > 0 ? "<span class=\"m\">" + E(meta) + "</span>" : "";
                    if (k.IsFolder && k.Children.Count > 0)
                    {
                        sb.Append("<li><details").Append(depth < 2 ? " open" : "").Append("><summary>📁 ").Append(E(k.Name)).Append("/").Append(metaHtml).Append("</summary>\n<ul>\n");
                        Emit(k, depth + 1);
                        sb.Append("</ul></details></li>\n");
                    }
                    else
                    {
                        sb.Append("<li>").Append(k.IsFolder ? "📁 " : "📄 ").Append(E(k.Name)).Append(k.IsFolder ? "/" : "").Append(metaHtml).Append("</li>\n");
                    }
                }
            }
            Emit(r.Root, 0);
            sb.Append("</ul>\n<p class=\"foot\">").Append(E(Footer(r))).Append("</p>\n")
              .Append("<script>function fold(open){document.querySelectorAll('details').forEach(function(d){d.open=open;});}</script>\n</body>\n</html>\n");
            return sb.ToString();
        }

        // =================================================================
        //  XML
        // =================================================================

        /// <summary>Characters XML cannot hold are replaced by "?" (a file name never has them on Windows, but a damaged one can).</summary>
        private static string X(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
                sb.Append(XmlConvert.IsXmlChar(c) ? c : '?');
            }
            return sb.ToString();
        }

        private static string RenderXml(FolderTreeResult r, FolderTreeOptions o)
        {
            var sb = new StringBuilder();
            using (var xw = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
            {
                void Attrs(FolderTreeNode n)
                {
                    xw.WriteAttributeString("name", X(n.Name));
                    if (o.ShowSize) xw.WriteAttributeString("size", n.Size.ToString(CultureInfo.InvariantCulture));
                    if (o.ShowDate && n.Modified != default) xw.WriteAttributeString("modified", n.Modified.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                    if (n.Note != null) xw.WriteAttributeString("note", X(n.Note));
                }
                void Emit(FolderTreeNode n)
                {
                    foreach (var k in Sorted(n))
                    {
                        xw.WriteStartElement(k.IsFolder ? "folder" : "file");
                        Attrs(k);
                        if (k.IsFolder) Emit(k);
                        xw.WriteEndElement();
                    }
                }
                xw.WriteStartElement("folder");
                xw.WriteAttributeString("path", X(r.Root.FullPath));
                Attrs(r.Root);
                xw.WriteAttributeString("folders", r.Folders.ToString(CultureInfo.InvariantCulture));
                xw.WriteAttributeString("files", r.Files.ToString(CultureInfo.InvariantCulture));
                xw.WriteAttributeString("bytes", r.Bytes.ToString(CultureInfo.InvariantCulture));
                Emit(r.Root);
                xw.WriteEndElement();
            }
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + sb + "\n";
        }

        // =================================================================
        //  YAML
        // =================================================================

        private static readonly JsonSerializerOptions YamlString = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>A double-quoted YAML scalar (YAML accepts JSON's string escapes).</summary>
        private static string Y(string s) => JsonSerializer.Serialize(s, YamlString);

        private static string RenderYaml(FolderTreeResult r, FolderTreeOptions o)
        {
            var sb = new StringBuilder();
            void Node(FolderTreeNode n, string indent, string firstPrefix)
            {
                sb.Append(indent).Append(firstPrefix).Append("name: ").AppendLine(Y(n.Name));
                string pad = indent + new string(' ', firstPrefix.Length);
                sb.Append(pad).Append("type: ").AppendLine(n.IsFolder ? "folder" : "file");
                if (o.ShowSize) sb.Append(pad).Append("size: ").AppendLine(n.Size.ToString(CultureInfo.InvariantCulture));
                if (o.ShowDate && n.Modified != default) sb.Append(pad).Append("modified: ").AppendLine(Y(n.Modified.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));
                if (n.Note != null) sb.Append(pad).Append("note: ").AppendLine(Y(n.Note));
                if (!n.IsFolder) return;
                var kids = Sorted(n).ToList();
                if (kids.Count == 0) { sb.Append(pad).AppendLine("children: []"); return; }
                sb.Append(pad).AppendLine("children:");
                foreach (var k in kids) Node(k, pad + "  ", "- ");
            }
            Node(r.Root, "", "");
            return sb.ToString();
        }

        // =================================================================
        //  Paths
        // =================================================================

        private static string RenderPaths(FolderTreeResult r)
        {
            var sb = new StringBuilder();
            void Emit(FolderTreeNode n)
            {
                sb.Append(n.FullPath);
                if (n.IsFolder && !n.FullPath.EndsWith('\\') && !n.FullPath.EndsWith('/')) sb.Append('\\');
                sb.AppendLine();
                foreach (var k in Sorted(n)) Emit(k);
            }
            Emit(r.Root);
            return sb.ToString();
        }
    }
}
