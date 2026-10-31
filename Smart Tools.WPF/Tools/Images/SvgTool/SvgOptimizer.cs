using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The SVG optimizer: pure text/XML logic with no WPF types, so it compiles and
    /// can be tested in a plain console project. Every step keeps the picture the same;
    /// anything it can't prove safe (inherited defaults next to CSS or &lt;use&gt;,
    /// malformed path data) is left alone.
    /// </summary>
    internal sealed partial class SvgOptimizer
    {
        private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
        private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
        private static readonly XNamespace Xml = XNamespace.Xml;
        private static readonly XNamespace Xmlns = XNamespace.Xmlns;

        private static readonly Regex NumberRx = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UrlRefRx = new(@"url\(\s*['""]?#([^)'""\s]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CssIdRx = new(@"#([A-Za-z_][\w\-]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex CssCommentRx = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex CssPunctRx = new(@"\s*([{};,])\s*", RegexOptions.Compiled);
        private static readonly Regex ColorDeclRx = new(@"(?<prop>(?:^|[;{\s])(?:fill|stroke|stop-color|flood-color|lighting-color|color))\s*:\s*(?<val>[^;}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NumericDeclRx = new(@"(?<prop>(?:^|[;{\s])(?:stroke-width|opacity|fill-opacity|stroke-opacity|stop-opacity|flood-opacity|stroke-miterlimit|stroke-dashoffset|stroke-dasharray|font-size))\s*:\s*(?<val>[^;}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RgbRx = new(@"^rgb\(\s*(\d{1,3}(?:\.\d+)?%?)\s*[, ]\s*(\d{1,3}(?:\.\d+)?%?)\s*[, ]\s*(\d{1,3}(?:\.\d+)?%?)\s*\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Editor/vendor namespaces whose elements and attributes mean nothing to a renderer.
        private static readonly string[] EditorNamespaceHints =
        {
            "inkscape", "sodipodi", "ns.adobe.com", "bohemiancoding", "serif.com", "creativecommons.org",
            "purl.org/dc", "22-rdf-syntax-ns", "figma", "sketch", "illustrator", "corel", "krita", "affinity", "gravit",
        };

        private static readonly HashSet<string> TextContainers = new() { "text", "tspan", "textPath", "tref" };

        private static readonly HashSet<string> DefinitionOnly = new()
        {
            "linearGradient", "radialGradient", "clipPath", "mask", "filter", "pattern", "symbol", "marker",
        };

        // Geometry attributes whose numbers can be rounded.
        private static readonly HashSet<string> NumericAttributes = new()
        {
            "x", "y", "width", "height", "cx", "cy", "r", "rx", "ry", "x1", "y1", "x2", "y2", "fx", "fy", "offset",
            "stroke-width", "opacity", "fill-opacity", "stroke-opacity", "stop-opacity", "flood-opacity",
            "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "font-size", "dx", "dy",
        };

        private static readonly HashSet<string> NumericStyleProps = new(StringComparer.OrdinalIgnoreCase)
        {
            "stroke-width", "opacity", "fill-opacity", "stroke-opacity", "stop-opacity", "flood-opacity",
            "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "font-size",
        };

        private static readonly HashSet<string> ColorAttributes = new() { "fill", "stroke", "stop-color", "flood-color", "lighting-color", "color" };

        // Presentation attributes that are inherited: only removable when nothing above could set them.
        private static readonly Dictionary<string, string> InheritedDefaults = new()
        {
            ["fill-rule"] = "nonzero", ["clip-rule"] = "nonzero", ["stroke-linecap"] = "butt", ["stroke-linejoin"] = "miter",
            ["stroke-miterlimit"] = "4", ["stroke-width"] = "1", ["stroke-opacity"] = "1", ["fill-opacity"] = "1",
            ["stroke-dasharray"] = "none", ["stroke-dashoffset"] = "0", ["stroke"] = "none",
        };

        // Presentation attributes that are NOT inherited: the default is always safe to drop.
        private static readonly Dictionary<string, string> PlainDefaults = new()
        {
            ["opacity"] = "1", ["stop-opacity"] = "1", ["flood-opacity"] = "1", ["clip-path"] = "none", ["mask"] = "none",
            ["filter"] = "none", ["display"] = "inline", ["stop-color"] = "#000", ["stop-color#"] = "black",
        };

        private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = "#000000", ["white"] = "#ffffff", ["yellow"] = "#ffff00", ["magenta"] = "#ff00ff", ["fuchsia"] = "#ff00ff",
            ["cyan"] = "#00ffff", ["aqua"] = "#00ffff", ["blue"] = "#0000ff", ["lime"] = "#00ff00", ["red"] = "#ff0000",
            ["gray"] = "#808080", ["grey"] = "#808080", ["silver"] = "#c0c0c0", ["maroon"] = "#800000", ["purple"] = "#800080",
            ["green"] = "#008000", ["olive"] = "#808000", ["navy"] = "#000080", ["teal"] = "#008080", ["orange"] = "#ffa500",
            ["gold"] = "#ffd700", ["tan"] = "#d2b48c", ["pink"] = "#ffc0cb", ["brown"] = "#a52a2a", ["indigo"] = "#4b0082",
            ["violet"] = "#ee82ee", ["khaki"] = "#f0e68c", ["wheat"] = "#f5deb3", ["snow"] = "#fffafa", ["bisque"] = "#ffe4c4",
            ["coral"] = "#ff7f50", ["salmon"] = "#fa8072", ["orchid"] = "#da70d6", ["plum"] = "#dda0dd", ["peru"] = "#cd853f",
            ["sienna"] = "#a0522d", ["azure"] = "#f0ffff", ["beige"] = "#f5f5dc", ["ivory"] = "#fffff0", ["linen"] = "#faf0e6",
        };

        private readonly SvgOptimizeOptions _o;
        private int _comments, _metadata, _defs, _empty, _ids, _scripts, _numbers;

        private SvgOptimizer(SvgOptimizeOptions options) => _o = options;

        // =================================================================
        //  Entry point
        // =================================================================

        public static SvgOptimizeResult Run(string svgText, SvgOptimizeOptions options)
        {
            var doc = Parse(svgText);
            var opt = new SvgOptimizer(options);
            opt.Process(doc);
            string text = opt.Write(doc);
            var stats = new SvgOptimizeStats(opt._comments, opt._metadata, opt._defs, opt._empty, opt._ids, opt._scripts, opt._numbers, opt._merged);
            return new SvgOptimizeResult(text, Encoding.UTF8.GetByteCount(text), stats);
        }

        /// <summary>Parses with entities expanded (Illustrator files declare their namespaces as entities) but capped, and with no outside access.</summary>
        internal static XDocument Parse(string svgText)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                MaxCharactersFromEntities = 1_000_000,
                MaxCharactersInDocument = 200_000_000,
                IgnoreWhitespace = false,
            };
            using var sr = new StringReader(svgText);
            using var reader = XmlReader.Create(sr, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        private void Process(XDocument doc)
        {
            var root = doc.Root ?? throw new XmlException("No root element.");

            if (_o.StripComments) RemoveComments(doc);
            if (_o.StripMetadata)
            {
                if (doc.DocumentType != null) { doc.DocumentType.Remove(); _metadata++; }
                if (doc.Declaration != null) { doc.Declaration = null; _metadata++; }
            }
            if (_o.RemoveScripts) RemoveScripts(root);
            if (_o.StripMetadata) RemoveEditorData(root);
            if (_o.StripTitleDesc) RemoveTitleDesc(root);

            bool hasScripts = root.DescendantsAndSelf().Any(e => e.Name.LocalName == "script");
            bool hasCss = root.DescendantsAndSelf().Any(e => e.Name.LocalName == "style");
            bool hasUse = root.DescendantsAndSelf().Any(e => e.Name.LocalName == "use");

            if (_o.RemoveUnusedDefs && !hasScripts) RemoveUnusedDefinitions(root);
            if (_o.RemoveEmptyElements) RemoveEmptyElements(root);
            if (_o.MergePaths && !hasScripts) MergeShapes(root, hasCss);
            if (_o.RemoveUnusedIds && !hasScripts) RemoveUnusedIds(root);
            if (_o.RemoveDefaultAttributes) RemoveDefaults(root, allowInherited: !hasCss && !hasUse);

            foreach (var el in root.DescendantsAndSelf().ToList()) ProcessAttributes(el);
            foreach (var style in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "style").ToList()) ProcessStyleElement(style);

            RemoveWhitespaceNodes(root);   // pretty output indents itself; stray indentation would double up

            if (_o.StripMetadata) RemoveUnusedNamespaces(root);
        }

        // =================================================================
        //  Removal passes
        // =================================================================

        private void RemoveComments(XDocument doc)
        {
            foreach (var c in doc.DescendantNodes().OfType<XComment>().ToList())
            {
                c.Remove();
                _comments++;
            }
        }

        private void RemoveScripts(XElement root)
        {
            foreach (var s in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "script").ToList())
            {
                s.Remove();
                _scripts++;
            }

            foreach (var el in root.DescendantsAndSelf())
            {
                foreach (var a in el.Attributes().ToList())
                {
                    string local = a.Name.LocalName;
                    bool handler = a.Name.Namespace == XNamespace.None && local.Length > 2 && local.StartsWith("on", StringComparison.Ordinal) && local.All(char.IsLower);
                    bool jsLink = local == "href" && a.Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
                    if (handler || jsLink)
                    {
                        a.Remove();
                        _scripts++;
                    }
                }
            }
        }

        private static bool IsEditorNamespace(XNamespace ns)
        {
            string uri = ns.NamespaceName;
            if (uri.Length == 0 || ns == Svg || ns == XLink || ns == Xml || ns == Xmlns) return false;
            if (uri.Contains("w3.org/1999/xhtml", StringComparison.Ordinal)) return false;
            return EditorNamespaceHints.Any(h => uri.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        private void RemoveEditorData(XElement root)
        {
            foreach (var el in root.DescendantsAndSelf().ToList())
            {
                if (el.Parent == null && el != root) continue;   // already gone with its parent

                if (el != root && (el.Name.LocalName == "metadata" || IsEditorNamespace(el.Name.Namespace)))
                {
                    el.Remove();
                    _metadata++;
                    continue;
                }

                foreach (var a in el.Attributes().ToList())
                {
                    if (a.IsNamespaceDeclaration) continue;
                    bool editorAttr = IsEditorNamespace(a.Name.Namespace);
                    bool exportJunk = a.Name.Namespace == XNamespace.None && a.Name.LocalName is "data-name" or "enable-background" or "version" or "baseProfile";
                    if (editorAttr || exportJunk)
                    {
                        a.Remove();
                        _metadata++;
                    }
                }
            }
        }

        private void RemoveTitleDesc(XElement root)
        {
            foreach (var el in root.Descendants().Where(e => e.Name.Namespace == Svg && e.Name.LocalName is "title" or "desc").ToList())
            {
                el.Remove();
                _metadata++;
            }
        }

        private static HashSet<string> CollectReferencedIds(XElement root)
        {
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in root.DescendantsAndSelf())
            {
                foreach (var a in el.Attributes())
                {
                    string v = a.Value;
                    string local = a.Name.LocalName;

                    if (local == "href" && v.Length > 1 && v[0] == '#') refs.Add(v[1..].Trim());
                    if (v.Contains("url(", StringComparison.Ordinal))
                        foreach (Match m in UrlRefRx.Matches(v)) refs.Add(m.Groups[1].Value);
                    if (local is "begin" or "end")
                        foreach (var part in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int dot = part.IndexOf('.');
                            if (dot > 0) refs.Add(part[..dot].Trim());
                        }
                    if (local is "aria-labelledby" or "aria-describedby" or "aria-controls")
                        foreach (var t in v.Split(' ', StringSplitOptions.RemoveEmptyEntries)) refs.Add(t);
                }

                if (el.Name.LocalName == "style")
                    foreach (Match m in CssIdRx.Matches(el.Value)) refs.Add(m.Groups[1].Value);
            }
            return refs;
        }

        private void RemoveUnusedDefinitions(XElement root)
        {
            // Removing one gradient can leave the gradient it pointed at unused, so repeat until nothing goes.
            for (int pass = 0; pass < 20; pass++)
            {
                var refs = CollectReferencedIds(root);
                var doomed = root.Descendants()
                    .Where(e => e.Name.Namespace == Svg
                                && (DefinitionOnly.Contains(e.Name.LocalName) || e.Parent?.Name.LocalName == "defs")
                                && e.Name.LocalName is not ("style" or "script" or "defs")
                                && !(e.Attribute("id") is { } id && refs.Contains(id.Value))
                                && (e.Attribute("id") != null || DefinitionOnly.Contains(e.Name.LocalName)))
                    .Where(e => e.Parent != null && !e.Ancestors().Any(a => DefinitionOnly.Contains(a.Name.LocalName)))
                    .ToList();
                if (doomed.Count == 0) return;
                foreach (var e in doomed)
                {
                    if (e.Parent == null) continue;
                    e.Remove();
                    _defs++;
                }
            }
        }

        private void RemoveEmptyElements(XElement root)
        {
            var refs = CollectReferencedIds(root);
            // Deepest first, so a group emptied by this pass goes too.
            foreach (var el in root.Descendants().Reverse().ToList())
            {
                if (el.Parent == null || el.Name.Namespace != Svg) continue;
                if (el.Attribute("id") is { } id && refs.Contains(id.Value)) continue;
                if (!IsEmptyElement(el)) continue;
                el.Remove();
                _empty++;
            }
        }

        private static bool IsEmptyElement(XElement el)
        {
            switch (el.Name.LocalName)
            {
                case "g":
                case "defs":
                case "switch":
                    return !el.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value)))
                           && el.Attribute("filter") == null;
                case "path":
                    return string.IsNullOrWhiteSpace((string?)el.Attribute("d"));
                case "rect":
                    return IsZero(Num(el, "width")) || IsZero(Num(el, "height"));
                case "circle":
                    return IsZero(Num(el, "r"));
                case "ellipse":
                {
                    // A missing radius copies the other one (SVG 2 "auto").
                    double? rx = Num(el, "rx") ?? Num(el, "ry");
                    double? ry = Num(el, "ry") ?? Num(el, "rx");
                    return IsZero(rx) || IsZero(ry);
                }
                default:
                    return false;
            }
        }

        private static double? Num(XElement el, string name)
        {
            var a = el.Attribute(name);
            if (a == null) return null;
            var m = NumberRx.Match(a.Value);
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        private static bool IsZero(double? v) => v is null or 0;

        private void RemoveUnusedIds(XElement root)
        {
            var refs = CollectReferencedIds(root);
            foreach (var el in root.DescendantsAndSelf())
            {
                var id = el.Attribute("id");
                if (id == null || refs.Contains(id.Value)) continue;
                id.Remove();
                _ids++;
            }
        }

        private void RemoveDefaults(XElement root, bool allowInherited)
        {
            foreach (var el in root.DescendantsAndSelf())
            {
                if (el.Name.Namespace != Svg) continue;
                string tag = el.Name.LocalName;

                foreach (var a in el.Attributes().ToList())
                {
                    if (a.Name.Namespace != XNamespace.None) continue;
                    string name = a.Name.LocalName;
                    string v = a.Value.Trim();

                    bool drop = false;
                    if (PlainDefaults.TryGetValue(name, out var plain) && v.Equals(plain, StringComparison.OrdinalIgnoreCase)) drop = true;
                    else if (name == "stop-color" && (v.Equals("black", StringComparison.OrdinalIgnoreCase) || v == "#000000")) drop = true;
                    else if (name is "x" or "y" && tag is "rect" or "use" or "image" or "svg" or "foreignObject" && IsNumber(v, 0)) drop = true;
                    else if (name is "cx" or "cy" && tag is "circle" or "ellipse" && IsNumber(v, 0)) drop = true;
                    else if (name == "offset" && tag == "stop" && IsNumber(v, 0)) drop = true;
                    else if (name == "transform" && v.Length == 0) drop = true;
                    else if (allowInherited && InheritedDefaults.TryGetValue(name, out var inh) && v.Equals(inh, StringComparison.OrdinalIgnoreCase)
                             && !AncestorSets(el, name)) drop = true;

                    if (drop) a.Remove();
                }
            }
        }

        private static bool IsNumber(string v, double expected) =>
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == expected;

        private static bool AncestorSets(XElement el, string prop)
        {
            foreach (var anc in el.Ancestors())
            {
                if (anc.Attribute(prop) != null) return true;
                if (anc.Attribute("style") is { } st && st.Value.Contains(prop, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void RemoveUnusedNamespaces(XElement root)
        {
            var used = new HashSet<XNamespace>();
            foreach (var el in root.DescendantsAndSelf())
            {
                used.Add(el.Name.Namespace);
                foreach (var a in el.Attributes())
                    if (!a.IsNamespaceDeclaration) used.Add(a.Name.Namespace);
            }

            foreach (var el in root.DescendantsAndSelf())
                foreach (var a in el.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == Xmlns).ToList())
                {
                    if (used.Contains(a.Value)) continue;
                    a.Remove();
                    _metadata++;
                }
        }

        private static void RemoveWhitespaceNodes(XElement root)
        {
            foreach (var t in root.DescendantNodes().OfType<XText>().Where(t => t is not XCData && string.IsNullOrWhiteSpace(t.Value)).ToList())
            {
                var parent = t.Parent;
                if (parent == null) continue;
                bool inText = TextContainers.Contains(parent.Name.LocalName) || parent.Ancestors().Any(a => TextContainers.Contains(a.Name.LocalName));
                bool preserve = parent.AncestorsAndSelf().Any(a => (string?)a.Attribute(Xml + "space") == "preserve");
                if (inText || preserve) continue;
                if (parent.Name.LocalName is "style" or "script") continue;
                t.Remove();
            }
        }

        // =================================================================
        //  Attribute rewriting (numbers, colours, whitespace)
        // =================================================================

        private void ProcessAttributes(XElement el)
        {
            foreach (var a in el.Attributes().ToList())
            {
                if (a.IsNamespaceDeclaration) continue;
                string name = a.Name.LocalName;
                string original = a.Value;
                string v = original;

                if (a.Name.Namespace == XNamespace.None || a.Name.Namespace == Svg)
                {
                    if (name == "d")
                    {
                        if (_o.RoundNumbers && TryRewritePath(v, _o.Precision, out var d, out int n)) { v = d; _numbers += n; }
                        else if (_o.Minify) v = WhitespaceRx.Replace(v.Trim(), " ");
                    }
                    else if (name == "points")
                    {
                        if (_o.RoundNumbers) v = RoundList(v, _o.Precision, keepNonZero: false);
                        v = _o.Minify ? WhitespaceRx.Replace(v.Trim(), " ") : v;
                    }
                    else if (name == "transform" || name == "gradientTransform" || name == "patternTransform")
                    {
                        if (_o.RoundNumbers) v = RoundList(v, Math.Min(_o.Precision + 2, 8), keepNonZero: true);
                        if (_o.Minify) v = WhitespaceRx.Replace(v.Trim(), " ");
                    }
                    else if (name == "style")
                    {
                        v = ProcessStyleAttribute(v);
                    }
                    else if (NumericAttributes.Contains(name))
                    {
                        if (_o.RoundNumbers) v = RoundList(v, _o.Precision, keepNonZero: true);
                    }
                    else if (name == "viewBox" || name == "class")
                    {
                        if (_o.Minify) v = WhitespaceRx.Replace(v.Trim(), " ");
                    }

                    if (_o.ShortenColors && ColorAttributes.Contains(name)) v = ShortenColor(v);
                }

                if (v != original) a.Value = v;
            }
        }

        private string RoundList(string value, int precision, bool keepNonZero)
        {
            if (value.Contains("url(", StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal)) return value;
            int count = 0;
            string result = NumberRx.Replace(value, m =>
            {
                if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return m.Value;
                string f = FormatNumber(d, precision);
                if (keepNonZero && d != 0 && f == "0") return m.Value;
                if (f != m.Value) count++;
                return f;
            });
            _numbers += count;
            return result;
        }

        // ---- style attribute / <style> element ----

        private string ProcessStyleAttribute(string css)
        {
            var decls = SplitDeclarations(css);
            var sb = new StringBuilder();
            foreach (var decl in decls)
            {
                int colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                string prop = decl[..colon].Trim();
                string val = decl[(colon + 1)..].Trim();
                if (val.Length == 0) continue;

                if (_o.RoundNumbers && NumericStyleProps.Contains(prop)) val = RoundList(val, _o.Precision, keepNonZero: true);
                if (_o.ShortenColors && prop.ToLowerInvariant() is "fill" or "stroke" or "stop-color" or "flood-color" or "lighting-color" or "color")
                    val = ShortenColor(val);

                if (sb.Length > 0) sb.Append(_o.Minify ? ";" : "; ");
                sb.Append(prop).Append(_o.Minify ? ":" : ": ").Append(val);
            }
            return sb.ToString();
        }

        private static List<string> SplitDeclarations(string css)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            foreach (char c in css)
            {
                if (quote != '\0') { cur.Append(c); if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') { quote = c; cur.Append(c); continue; }
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                if (c == ';' && depth == 0) { parts.Add(cur.ToString()); cur.Clear(); continue; }
                cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            return parts.Where(p => p.Trim().Length > 0).ToList();
        }

        private void ProcessStyleElement(XElement style)
        {
            foreach (var node in style.Nodes().OfType<XText>().ToList())
            {
                string css = node.Value;
                string original = css;

                if (_o.StripComments) css = CssCommentRx.Replace(css, string.Empty);
                if (_o.ShortenColors)
                    css = ColorDeclRx.Replace(css, m => m.Groups["prop"].Value + ":" + ShortenColor(m.Groups["val"].Value.Trim()));
                // Only the numeric properties on the whitelist: a blanket rewrite would break selectors like :nth-child(2n+1).
                if (_o.RoundNumbers)
                    css = NumericDeclRx.Replace(css, m => m.Groups["prop"].Value + ":" + RoundList(m.Groups["val"].Value, _o.Precision, keepNonZero: true));
                if (_o.Minify) css = CssPunctRx.Replace(WhitespaceRx.Replace(css, " "), "$1").Trim().Replace(";}", "}").Replace(": ", ":");

                if (css == original) continue;
                if (node is XCData) node.ReplaceWith(new XCData(css));
                else node.Value = css;
            }
        }

        // ---- colours ----

        internal static string ShortenColor(string value)
        {
            string v = value.Trim();
            if (v.Length == 0 || v.Contains("url(", StringComparison.Ordinal)) return value;

            string? hex = null;
            if (v[0] == '#' && (v.Length == 7) && IsHex(v.AsSpan(1))) hex = v.ToLowerInvariant();
            else if (v[0] == '#' && v.Length == 4 && IsHex(v.AsSpan(1))) hex = ("#" + v[1] + v[1] + v[2] + v[2] + v[3] + v[3]).ToLowerInvariant();
            else if (RgbRx.Match(v) is { Success: true } m && TryRgb(m, out var rgb)) hex = rgb;
            else if (NamedColors.TryGetValue(v, out var named)) hex = named;
            else return value;

            string shortHex = hex[1] == hex[2] && hex[3] == hex[4] && hex[5] == hex[6]
                ? "#" + hex[1] + hex[3] + hex[5]
                : hex;

            string best = shortHex;
            foreach (var kv in NamedColors)
                if (kv.Value == hex && kv.Key.Length < best.Length && kv.Key != "aqua" && kv.Key != "fuchsia" && kv.Key != "grey")
                    best = kv.Key;
            return best;
        }

        private static bool IsHex(ReadOnlySpan<char> s)
        {
            foreach (char c in s)
                if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        private static bool TryRgb(Match m, out string hex)
        {
            hex = string.Empty;
            var parts = new int[3];
            for (int i = 0; i < 3; i++)
            {
                string g = m.Groups[i + 1].Value;
                bool pct = g.EndsWith('%');
                if (!double.TryParse(pct ? g[..^1] : g, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
                if (pct) d = d * 255 / 100;
                if (d < 0 || d > 255) return false;
                parts[i] = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            }
            hex = $"#{parts[0]:x2}{parts[1]:x2}{parts[2]:x2}";
            return true;
        }

        // =================================================================
        //  Numbers and path data
        // =================================================================

        internal static string FormatNumber(double v, int precision)
        {
            precision = Math.Clamp(precision, 0, 8);
            double r = Math.Round(v, precision, MidpointRounding.AwayFromZero);
            if (r == 0) return "0";
            string s = precision == 0
                ? r.ToString("0", CultureInfo.InvariantCulture)
                : r.ToString("0." + new string('#', precision), CultureInfo.InvariantCulture);
            if (s.StartsWith("0.", StringComparison.Ordinal)) s = s[1..];
            else if (s.StartsWith("-0.", StringComparison.Ordinal)) s = "-" + s[2..];
            return s;
        }

        private static int ArgCount(char cmd) => char.ToUpperInvariant(cmd) switch
        {
            'M' or 'L' or 'T' => 2,
            'H' or 'V' => 1,
            'C' => 6,
            'S' or 'Q' => 4,
            'A' => 7,
            _ => 0,
        };

        /// <summary>
        /// Re-writes path data with rounded numbers and the fewest separators. Understands the compact
        /// arc flags ("a1 1 0 00.5.5") and repeated commands. Returns false for data it can't read, so the original stays.
        /// </summary>
        internal static bool TryRewritePath(string d, int precision, out string result, out int rounded)
        {
            result = d;
            rounded = 0;
            var sb = new StringBuilder(d.Length);
            int i = 0, n = d.Length;
            char cmd = '\0';
            char lastLetter = '\0';
            string prevToken = string.Empty;   // last number written, for the separator rule
            bool any = false;

            void Emit(string token, bool isFlag = false)
            {
                if (prevToken.Length > 0)
                {
                    bool needSpace = token[0] != '-' && !(!isFlag && prevToken.Contains('.') && token[0] == '.');
                    if (needSpace) sb.Append(' ');
                }
                sb.Append(token);
                prevToken = token;
            }

            while (true)
            {
                SkipSeparators(d, ref i);
                if (i >= n) break;

                char c = d[i];
                if (char.IsLetter(c))
                {
                    if ("MmLlHhVvCcSsQqTtAaZz".IndexOf(c) < 0) return false;
                    i++;
                    cmd = c;
                    if (c is 'Z' or 'z')
                    {
                        sb.Append(c);
                        lastLetter = c;
                        prevToken = string.Empty;
                        any = true;
                        continue;
                    }
                    if (!ReadGroup(d, ref i, cmd, precision, out var firstArgs, ref rounded)) return false;
                    WriteGroup(cmd, firstArgs);
                    lastLetter = cmd is 'M' ? 'L' : cmd is 'm' ? 'l' : cmd;
                    any = true;
                    continue;
                }

                // Numbers with no new letter: the previous command repeats (M becomes L).
                if (cmd == '\0' || cmd is 'Z' or 'z') return false;
                char repeat = cmd is 'M' ? 'L' : cmd is 'm' ? 'l' : cmd;
                if (!ReadGroup(d, ref i, repeat, precision, out var args, ref rounded)) return false;
                WriteGroup(repeat, args);
                lastLetter = repeat;
                cmd = repeat;
            }

            if (!any) return false;
            result = sb.ToString();
            return true;

            void WriteGroup(char letter, List<string> args)
            {
                if (letter != lastLetter)
                {
                    sb.Append(letter);
                    prevToken = string.Empty;
                }
                for (int k = 0; k < args.Count; k++)
                {
                    bool flag = char.ToUpperInvariant(letter) == 'A' && k is 3 or 4;
                    Emit(args[k], flag);
                }
            }
        }

        private static void SkipSeparators(string d, ref int i)
        {
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
        }

        private static bool ReadGroup(string d, ref int i, char cmd, int precision, out List<string> args, ref int rounded)
        {
            int count = ArgCount(cmd);
            args = new List<string>(count);
            bool isArc = char.ToUpperInvariant(cmd) == 'A';
            for (int k = 0; k < count; k++)
            {
                SkipSeparators(d, ref i);
                if (i >= d.Length) return false;

                if (isArc && k is 3 or 4)
                {
                    char f = d[i];
                    if (f != '0' && f != '1') return false;
                    args.Add(f.ToString());
                    i++;
                    continue;
                }

                var m = NumberRx.Match(d, i);
                if (!m.Success || m.Index != i) return false;
                if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
                string f2 = FormatNumber(v, precision);
                if (f2 != m.Value) rounded++;
                args.Add(f2);
                i += m.Length;
            }
            return true;
        }

        // =================================================================
        //  Output
        // =================================================================

        private string Write(XDocument doc)
        {
            bool keepDeclaration = !_o.StripMetadata && doc.Declaration != null;
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = !keepDeclaration,
                Encoding = new UTF8Encoding(false),
                Indent = !_o.Minify,
                IndentChars = "  ",
                NewLineHandling = NewLineHandling.None,
                NamespaceHandling = NamespaceHandling.OmitDuplicates,
            };

            using var sw = new Utf8StringWriter();
            using (var xw = XmlWriter.Create(sw, settings)) doc.Save(xw);
            string text = sw.ToString();

            if (_o.Minify)
            {
                if (!doc.DescendantNodes().OfType<XCData>().Any()) text = text.Replace(" />", "/>");
                text = text.Trim();
            }
            return text;
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => new UTF8Encoding(false);
        }
    }
}
