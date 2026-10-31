using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// SVG optimizer: "merge shapes into one path". Neighbouring shapes that are painted the same way
    /// (same fill, stroke, transform ... on the same parent) become one &lt;path&gt;. Rects, circles,
    /// ellipses, lines, polylines and polygons are turned into path data first.
    ///
    /// The picture must not change, so shapes are only merged when they sit next to each other in paint
    /// order AND their (stroke-padded) boxes don't overlap: with no overlap, fill rule, opacity and
    /// stroke order can't make a difference. Anything unusual (ids that are used, filters, markers,
    /// units, malformed data) is left alone.
    /// </summary>
    internal sealed partial class SvgOptimizer
    {
        private int _merged;

        private readonly record struct Seg(char Cmd, double[] Args);

        private sealed class MergeItem
        {
            public required XElement Element { get; init; }
            public required List<Seg> Segments { get; init; }
            public required string Key { get; init; }
            public required double Left { get; init; }
            public required double Top { get; init; }
            public required double Right { get; init; }
            public required double Bottom { get; init; }

            public bool Overlaps(MergeItem o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
        }

        private static readonly Dictionary<string, string[]> GeometryAttributes = new()
        {
            ["path"] = new[] { "d" },
            ["rect"] = new[] { "x", "y", "width", "height", "rx", "ry" },
            ["circle"] = new[] { "cx", "cy", "r" },
            ["ellipse"] = new[] { "cx", "cy", "rx", "ry" },
            ["line"] = new[] { "x1", "y1", "x2", "y2" },
            ["polyline"] = new[] { "points" },
            ["polygon"] = new[] { "points" },
        };

        private static readonly Regex StructuralCssRx = new(@":(?:nth|first|last|only)-|[+~]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private void MergeShapes(XElement root, bool hasCss)
        {
            // Structural selectors (:nth-child, sibling combinators) would notice elements disappearing.
            if (hasCss && root.Descendants().Where(e => e.Name.LocalName == "style").Any(s => StructuralCssRx.IsMatch(s.Value)))
                return;

            var refs = CollectReferencedIds(root);
            foreach (var parent in root.DescendantsAndSelf().Where(e => e.HasElements).ToList())
            {
                if (parent.Name.Namespace != Svg || parent.Name.LocalName == "switch" || TextContainers.Contains(parent.Name.LocalName))
                    continue;
                MergeChildren(parent, refs, hasCss);
            }
        }

        private void MergeChildren(XElement parent, HashSet<string> refs, bool hasCss)
        {
            var run = new List<MergeItem>();

            foreach (var node in parent.Nodes().ToList())
            {
                if (node is XComment || (node is XText t && string.IsNullOrWhiteSpace(t.Value))) continue;

                MergeItem? item = node is XElement el ? ReadItem(el, refs, hasCss) : null;
                if (item == null)
                {
                    Flush(run);
                    continue;
                }

                if (run.Count > 0 && (run[0].Key != item.Key || run.Count >= 2000 || run.Any(r => r.Overlaps(item))))
                    Flush(run);
                run.Add(item);
            }
            Flush(run);
        }

        private void Flush(List<MergeItem> run)
        {
            if (run.Count > 1)
            {
                var first = run[0].Element;
                var parts = new List<string>(run.Count);
                foreach (var item in run)
                    parts.Add(Serialize(item.Segments));

                if (first.Name.LocalName != "path")
                {
                    foreach (string g in GeometryAttributes[first.Name.LocalName])
                        first.Attribute(g)?.Remove();
                    first.Name = Svg + "path";
                }
                first.SetAttributeValue("d", string.Join(" ", parts));

                for (int i = 1; i < run.Count; i++)
                {
                    run[i].Element.Remove();
                    _merged++;
                }
            }
            run.Clear();
        }

        // ---- reading one candidate ----

        private static MergeItem? ReadItem(XElement el, HashSet<string> refs, bool hasCss)
        {
            if (el.Name.Namespace != Svg || el.Nodes().Any()) return null;
            string tag = el.Name.LocalName;
            if (!GeometryAttributes.TryGetValue(tag, out var geometry)) return null;
            if (hasCss && tag != "path") return null;   // a "rect { ... }" rule must not lose its rect

            foreach (var a in el.Attributes())
            {
                if (a.IsNamespaceDeclaration) return null;
                string n = a.Name.LocalName;
                if (a.Name.Namespace != XNamespace.None || n == "pathLength" || n.StartsWith("marker", StringComparison.Ordinal)) return null;
            }
            if (el.Attribute("id") is { } id && refs.Contains(id.Value)) return null;
            if (OwnProp(el, "filter") is { } filter && filter != "none") return null;
            if (OwnProp(el, "marker") != null || OwnProp(el, "marker-start") != null || OwnProp(el, "marker-mid") != null || OwnProp(el, "marker-end") != null) return null;

            var segments = tag == "path" ? ParsePathSegments((string?)el.Attribute("d") ?? "") : ShapeToSegments(el, tag);
            if (segments == null || segments.Count == 0 || !Bounds(segments, out double l, out double t, out double r, out double b)) return null;

            // Stroke sticks out of the geometry (miter joins most): pad the box so strokes can't touch either.
            double pad = 0;
            bool mayStroke = hasCss || (FindProp(el, "stroke") is { } stroke && !stroke.Equals("none", StringComparison.OrdinalIgnoreCase));
            if (mayStroke)
            {
                double width = ParseNumber(FindProp(el, "stroke-width")) ?? 1;
                double miter = ParseNumber(FindProp(el, "stroke-miterlimit")) ?? 4;
                pad = Math.Abs(width) * Math.Max(4, miter);
            }

            var skip = new HashSet<string>(geometry) { "id" };
            string key = string.Join(";", el.Attributes().Where(a => !skip.Contains(a.Name.LocalName))
                .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
                .Select(a => a.Name.LocalName + "=" + a.Value.Trim()));

            return new MergeItem { Element = el, Segments = segments, Key = key, Left = l - pad, Top = t - pad, Right = r + pad, Bottom = b + pad };
        }

        private static string? OwnProp(XElement el, string prop)
        {
            if (el.Attribute(prop) is { } a) return a.Value.Trim();
            return StyleValue(el, prop);
        }

        private static string? FindProp(XElement el, string prop)
        {
            for (var e = el; e != null; e = e.Parent)
            {
                if (OwnProp(e, prop) is { } v) return v;
            }
            return null;
        }

        private static string? StyleValue(XElement el, string prop)
        {
            if (el.Attribute("style") is not { } st) return null;
            foreach (string decl in SplitDeclarations(st.Value))
            {
                int colon = decl.IndexOf(':');
                if (colon > 0 && decl[..colon].Trim().Equals(prop, StringComparison.OrdinalIgnoreCase))
                    return decl[(colon + 1)..].Trim();
            }
            return null;
        }

        private static double? ParseNumber(string? text) =>
            text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;

        // ---- shapes -> path segments (absolute commands only) ----

        private static List<Seg>? ShapeToSegments(XElement el, string tag)
        {
            double? Attr(string name) => ParseNumber((string?)el.Attribute(name));
            bool Has(string name) => el.Attribute(name) != null;
            var s = new List<Seg>();
            void Add(char c, params double[] a) => s.Add(new Seg(c, a));

            switch (tag)
            {
                case "rect":
                {
                    double x = Has("x") ? Attr("x") ?? double.NaN : 0, y = Has("y") ? Attr("y") ?? double.NaN : 0;
                    double? w = Attr("width"), h = Attr("height");
                    if (double.IsNaN(x) || double.IsNaN(y) || w is not > 0 || h is not > 0) return null;
                    if ((Has("rx") && Attr("rx") is null) || (Has("ry") && Attr("ry") is null)) return null;
                    double? rxa = Attr("rx"), rya = Attr("ry");
                    if (rxa < 0 || rya < 0) return null;
                    double rx = rxa ?? rya ?? 0, ry = rya ?? rxa ?? 0;
                    rx = Math.Min(rx, w.Value / 2);
                    ry = Math.Min(ry, h.Value / 2);
                    if (rx > 0 && ry > 0)
                    {
                        Add('M', x + rx, y);
                        Add('H', x + w.Value - rx);
                        Add('A', rx, ry, 0, 0, 1, x + w.Value, y + ry);
                        Add('V', y + h.Value - ry);
                        Add('A', rx, ry, 0, 0, 1, x + w.Value - rx, y + h.Value);
                        Add('H', x + rx);
                        Add('A', rx, ry, 0, 0, 1, x, y + h.Value - ry);
                        Add('V', y + ry);
                        Add('A', rx, ry, 0, 0, 1, x + rx, y);
                    }
                    else
                    {
                        Add('M', x, y);
                        Add('H', x + w.Value);
                        Add('V', y + h.Value);
                        Add('H', x);
                    }
                    Add('Z');
                    return s;
                }
                case "circle":
                case "ellipse":
                {
                    double cx = Has("cx") ? Attr("cx") ?? double.NaN : 0, cy = Has("cy") ? Attr("cy") ?? double.NaN : 0;
                    double rx, ry;
                    if (tag == "circle")
                    {
                        double? r = Attr("r");
                        if (r is not > 0) return null;
                        rx = ry = r.Value;
                    }
                    else
                    {
                        if ((Has("rx") && Attr("rx") is null) || (Has("ry") && Attr("ry") is null)) return null;
                        double? a = Attr("rx") ?? Attr("ry"), b = Attr("ry") ?? Attr("rx");
                        if (a is not > 0 || b is not > 0) return null;
                        rx = a.Value;
                        ry = b.Value;
                    }
                    if (double.IsNaN(cx) || double.IsNaN(cy)) return null;
                    Add('M', cx - rx, cy);
                    Add('A', rx, ry, 0, 1, 0, cx + rx, cy);
                    Add('A', rx, ry, 0, 1, 0, cx - rx, cy);
                    Add('Z');
                    return s;
                }
                case "line":
                {
                    foreach (string n in new[] { "x1", "y1", "x2", "y2" })
                        if (Has(n) && Attr(n) is null) return null;
                    Add('M', Attr("x1") ?? 0, Attr("y1") ?? 0);
                    Add('L', Attr("x2") ?? 0, Attr("y2") ?? 0);
                    return s;
                }
                case "polyline":
                case "polygon":
                {
                    string pts = (string?)el.Attribute("points") ?? "";
                    // Anything but numbers, commas and spaces (units, junk) means "leave it alone".
                    if (NumberRx.Replace(pts, "").Trim(' ', ',', '\t', '\r', '\n').Length > 0) return null;
                    var nums = NumberRx.Matches(pts).Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
                    if (nums.Count < 4 || nums.Count % 2 != 0) return null;
                    Add('M', nums[0], nums[1]);
                    for (int i = 2; i < nums.Count; i += 2) Add('L', nums[i], nums[i + 1]);
                    if (tag == "polygon") Add('Z');
                    return s;
                }
            }
            return null;
        }

        // ---- path data <-> segments ----

        /// <summary>Path data as one segment per command group (repeats split, M repeats become L). Null if unreadable.</summary>
        private static List<Seg>? ParsePathSegments(string d)
        {
            var list = new List<Seg>();
            int i = 0;
            char cmd = '\0';
            while (true)
            {
                SkipSeparators(d, ref i);
                if (i >= d.Length) break;

                char c = d[i];
                if (char.IsLetter(c))
                {
                    if ("MmLlHhVvCcSsQqTtAaZz".IndexOf(c) < 0) return null;
                    i++;
                    cmd = c;
                    if (c is 'Z' or 'z')
                    {
                        list.Add(new Seg(c, Array.Empty<double>()));
                        continue;
                    }
                }
                else
                {
                    if (cmd == '\0' || cmd is 'Z' or 'z') return null;
                    if (cmd == 'M') cmd = 'L';
                    else if (cmd == 'm') cmd = 'l';
                }

                int rounded = 0;
                if (!ReadGroup(d, ref i, cmd, 8, out var args, ref rounded)) return null;
                list.Add(new Seg(cmd, args.Select(a => double.Parse(a, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray()));
            }

            return list.Count > 0 && list[0].Cmd is 'M' or 'm' ? list : null;
        }

        /// <summary>Back to text. A path's first moveto is already absolute (it starts at 0,0), so an 'm' there becomes 'M' to survive being appended to another path.</summary>
        private static string Serialize(List<Seg> segments)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < segments.Count; i++)
            {
                var (cmd, args) = segments[i];
                if (i == 0 && cmd == 'm') cmd = 'M';
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(cmd);
                for (int k = 0; k < args.Length; k++)
                {
                    sb.Append(k == 0 ? "" : " ");
                    sb.Append(args[k].ToString("R", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        /// <summary>A box that surely contains the whole path (control points count; arcs get a generous allowance).</summary>
        private static bool Bounds(List<Seg> segments, out double left, out double top, out double right, out double bottom)
        {
            double cx = 0, cy = 0, sx = 0, sy = 0;
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;

            void P(double x, double y)
            {
                l = Math.Min(l, x); r = Math.Max(r, x);
                t = Math.Min(t, y); b = Math.Max(b, y);
            }

            foreach (var (cmd, a) in segments)
            {
                bool rel = char.IsLower(cmd);
                double ox = rel ? cx : 0, oy = rel ? cy : 0;
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        cx = a[0] + ox; cy = a[1] + oy; sx = cx; sy = cy; P(cx, cy);
                        break;
                    case 'L':
                    case 'T':
                        cx = a[0] + ox; cy = a[1] + oy; P(cx, cy);
                        break;
                    case 'H':
                        cx = a[0] + ox; P(cx, cy);
                        break;
                    case 'V':
                        cy = a[0] + oy; P(cx, cy);
                        break;
                    case 'C':
                        P(a[0] + ox, a[1] + oy); P(a[2] + ox, a[3] + oy);
                        cx = a[4] + ox; cy = a[5] + oy; P(cx, cy);
                        break;
                    case 'S':
                    case 'Q':
                        P(a[0] + ox, a[1] + oy);
                        cx = a[2] + ox; cy = a[3] + oy; P(cx, cy);
                        break;
                    case 'A':
                    {
                        double x = a[5] + ox, y = a[6] + oy;
                        double reach = 2 * Math.Max(Math.Max(Math.Abs(a[0]), Math.Abs(a[1])), Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)));
                        P(cx - reach, cy - reach); P(cx + reach, cy + reach);
                        cx = x; cy = y; P(cx, cy);
                        break;
                    }
                    case 'Z':
                        cx = sx; cy = sy;
                        break;
                }
            }

            left = l; top = t; right = r; bottom = b;
            return l <= r && t <= b && double.IsFinite(l) && double.IsFinite(r) && double.IsFinite(t) && double.IsFinite(b);
        }
    }
}
