using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// SVG Viewer &amp; Optimizer backend - no WPF types. Reads files (plain and .svgz),
    /// describes them, runs <see cref="SvgOptimizer"/>, builds the pages the WebView2
    /// preview shows, saves results and keeps the tool's settings.
    /// The view only reads controls, calls this and renders what comes back.
    /// </summary>
    public static class SvgToolManager
    {
        /// <summary>Virtual host the preview is served from (WebView2 WebResourceRequested); nothing is fetched from the network.</summary>
        public const string PreviewHost = "svgtool.smarttools.local";
        public const string ViewPath = "/view.html";
        public const string ExportPagePath = "/export.html";
        public const string ImagePath = "/image.svg";
        public static string ViewUrl => $"https://{PreviewHost}{ViewPath}";
        public static string ExportUrl => $"https://{PreviewHost}{ExportPagePath}";

        public const long MaxFileBytes = 30L * 1024 * 1024;
        public const int MaxExportSide = 8192;
        public const int MinExportSide = 16;
        public const int CodePreviewLimit = 1_500_000;

        // =================================================================
        //  Reading files
        // =================================================================

        /// <summary>Reads an .svg / .svgz and checks that it is really an SVG. On failure <paramref name="error"/> says why in plain words.</summary>
        public static bool TryLoad(string path, out SvgDocument? document, out string error)
        {
            document = null;
            error = string.Empty;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { error = "NotFound"; return false; }
                if (fi.Length > MaxFileBytes) { error = "TooBig"; return false; }

                byte[] bytes = File.ReadAllBytes(path);
                string text = DecodeText(bytes);
                if (!TryAnalyze(text, out var info, out error)) return false;

                document = new SvgDocument(path, text, fi.Length, info!);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Reads SVG text typed or pasted in (no file). Same checks as a file.</summary>
        public static bool TryFromText(string text, out SvgDocument? document, out string error)
        {
            document = null;
            if (!TryAnalyze(text, out var info, out error)) return false;
            document = new SvgDocument(string.Empty, text, Encoding.UTF8.GetByteCount(text), info!);
            return true;
        }

        /// <summary>Gzip (.svgz) is unpacked; text is UTF-8 unless a BOM says UTF-16 (a UTF-8 BOM is dropped).</summary>
        internal static string DecodeText(byte[] bytes)
        {
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                // Bounded, so a tiny archive can't expand into gigabytes.
                var buf = new byte[81920];
                int read;
                while ((read = gz.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, read);
                    if (ms.Length > MaxFileBytes * 2) throw new InvalidDataException("TooBig");
                }
                bytes = ms.ToArray();
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
        }

        // =================================================================
        //  Describing an SVG
        // =================================================================

        private static readonly Regex ExternalRefRx = new(@"(?:href\s*=\s*[""']|url\(\s*[""']?)\s*(?:https?:)?//", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Parses the text and gathers its size, view box and element counts. False (with a short reason code) if it isn't an SVG.</summary>
        public static bool TryAnalyze(string text, out SvgInfo? info, out string error)
        {
            info = null;
            error = string.Empty;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Parse,
                    XmlResolver = null,
                    MaxCharactersFromEntities = 1_000_000,
                };
                using var sr = new StringReader(text);
                using var reader = XmlReader.Create(sr, settings);

                string? widthText = null, heightText = null, viewBox = null;
                bool rootSeen = false, hasScripts = false, embedded = false;
                int elements = 0, paths = 0;

                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    elements++;

                    if (!rootSeen)
                    {
                        rootSeen = true;
                        if (!reader.LocalName.Equals("svg", StringComparison.Ordinal)) { error = "NotSvg"; return false; }
                        widthText = reader.GetAttribute("width");
                        heightText = reader.GetAttribute("height");
                        viewBox = reader.GetAttribute("viewBox");
                    }

                    switch (reader.LocalName)
                    {
                        case "path": paths++; break;
                        case "script": hasScripts = true; break;
                        case "image":
                            string? href = reader.GetAttribute("href") ?? reader.GetAttribute("href", "http://www.w3.org/1999/xlink");
                            if (href != null && href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) embedded = true;
                            break;
                    }

                    if (!hasScripts && reader.HasAttributes)
                    {
                        for (int i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            string n = reader.LocalName;
                            if (n.Length > 2 && n.StartsWith("on", StringComparison.Ordinal) && n.All(char.IsLower)) hasScripts = true;
                        }
                        reader.MoveToElement();
                    }
                }

                if (!rootSeen) { error = "NotSvg"; return false; }

                double? w = ParseLength(widthText), h = ParseLength(heightText);
                if (viewBox != null && TryParseViewBox(viewBox, out var vb))
                {
                    if (w == null && h == null) { w = vb.W; h = vb.H; }
                    else if (w == null) w = h!.Value * vb.W / vb.H;
                    else if (h == null) h = w.Value * vb.H / vb.W;
                }

                bool external = ExternalRefRx.IsMatch(text);
                info = new SvgInfo(w ?? 300, h ?? 150, viewBox?.Trim(), elements, paths, hasScripts, external, embedded);
                return true;
            }
            catch (XmlException)
            {
                error = "NotXml";
                return false;
            }
        }

        internal static bool TryParseViewBox(string value, out (double X, double Y, double W, double H) vb)
        {
            vb = default;
            var parts = value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return false;
            var n = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) return false;
            if (n[2] <= 0 || n[3] <= 0) return false;
            vb = (n[0], n[1], n[2], n[3]);
            return true;
        }

        /// <summary>A CSS length to pixels (px, pt, pc, mm, cm, in, em, unitless). Percentages and junk give null.</summary>
        internal static double? ParseLength(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var m = Regex.Match(value.Trim(), @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-zA-Z%]*)$", RegexOptions.CultureInvariant);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
            double? px = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "" or "px" => v,
                "pt" => v * 4 / 3,
                "pc" => v * 16,
                "mm" => v * 96 / 25.4,
                "cm" => v * 96 / 2.54,
                "in" => v * 96,
                "em" => v * 16,
                _ => null,
            };
            return px is > 0 ? px : null;
        }

        // =================================================================
        //  Optimizing
        // =================================================================

        /// <summary>Optimizes the text. Throws <see cref="XmlException"/> if it can't be read as XML.</summary>
        public static SvgOptimizeResult Optimize(string svgText, SvgOptimizeOptions options) => SvgOptimizer.Run(svgText, Normalize(options));

        public static SvgOptimizeOptions Normalize(SvgOptimizeOptions o)
        {
            var c = o.Clone();
            c.Precision = Math.Clamp(c.Precision, 0, 6);
            return c;
        }

        /// <summary>"12.4 KB" / "1.2 MB" - the same words in both languages.</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return string.Create(CultureInfo.CurrentCulture, $"{kb:0.#} KB");
            return string.Create(CultureInfo.CurrentCulture, $"{kb / 1024.0:0.##} MB");
        }

        /// <summary>Whole-number percentage saved (never negative: a bigger result shows 0).</summary>
        public static int SavedPercent(long before, long after) =>
            before <= 0 || after >= before ? 0 : (int)Math.Round(100.0 * (before - after) / before, MidpointRounding.AwayFromZero);

        // =================================================================
        //  Saving
        // =================================================================

        /// <summary>UTF-8 without a BOM, written to a temporary name then moved into place so a failed write never leaves half a file.</summary>
        public static void SaveText(string path, string text)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, text, new UTF8Encoding(false));
        }

        public static void SaveBytes(string path, byte[] data)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllBytes(path, data);
        }

        /// <summary>Suggested name for the optimized copy: "logo.svg" becomes "logo.min.svg" (or " (2)" if that exists). Never the source itself.</summary>
        public static string SuggestOptimizedPath(string sourcePath)
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            if (name.EndsWith(".min", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return UniquePath(Path.Combine(dir, name + ".min.svg"));
        }

        public static string SuggestPngPath(string sourcePath, int width)
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            return UniquePath(Path.Combine(dir, $"{name}-{width}px.png"));
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 10_000; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }

        // =================================================================
        //  PNG export maths
        // =================================================================

        /// <summary>Export size from the chosen width, keeping the SVG's proportions, within the limits Chromium can capture.</summary>
        public static (int W, int H) ExportSize(SvgInfo info, int width)
        {
            width = Math.Clamp(width, MinExportSide, MaxExportSide);
            double ratio = info.PixelHeight / Math.Max(info.PixelWidth, 0.0001);
            int height = (int)Math.Round(width * ratio, MidpointRounding.AwayFromZero);
            if (height > MaxExportSide)
            {
                height = MaxExportSide;
                width = (int)Math.Round(height / ratio, MidpointRounding.AwayFromZero);
            }
            return (Math.Max(MinExportSide / 4, width), Math.Max(1, height));
        }

        // =================================================================
        //  Preview pages (served to WebView2)
        // =================================================================

        /// <summary>Headers for every served page: the SVG is shown in an &lt;img&gt;, which runs no scripts, and the page itself may load nothing else.</summary>
        public const string PageHeaders =
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";

        public const string ImageHeaders = "Content-Type: image/svg+xml; charset=utf-8\r\nCache-Control: no-store";

        /// <summary>Flat colour (#RRGGBB) of a background choice; the checkerboard counts as white where a single colour is needed (PNG export, control background).</summary>
        public static string BackgroundHex(SvgBackground bg) => bg == SvgBackground.Dark ? "#1B1F27" : "#FFFFFF";

        /// <summary>One value out of a URL query ("?w=512&amp;h=512"), or null.</summary>
        public static string? QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part[..eq] == key) return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            return null;
        }

        /// <summary>Whether a dropped or chosen path is something this tool can open.</summary>
        public static bool CanOpen(string path) =>
            path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase);

        /// <summary>Colours of the stage, as CSS. Light and Dark are fixed (the stage mimics paper/screen, not the app theme).</summary>
        public static string BackgroundCss(SvgBackground bg) => bg switch
        {
            SvgBackground.Dark => "background:#1b1f27;",
            SvgBackground.Checker =>
                "background-color:#ffffff;background-image:linear-gradient(45deg,#d9dde3 25%,transparent 25%,transparent 75%,#d9dde3 75%)," +
                "linear-gradient(45deg,#d9dde3 25%,transparent 25%,transparent 75%,#d9dde3 75%);background-size:20px 20px;background-position:0 0,10px 10px;",
            _ => "background:#ffffff;",
        };

        /// <summary>
        /// The page that shows the SVG. <paramref name="fit"/> scales it to the pane; otherwise it is shown at its own pixel size
        /// (the control's zoom factor does the rest). <paramref name="version"/> changes the image address so a new file always reloads.
        /// </summary>
        public static string BuildViewHtml(SvgInfo info, SvgBackground bg, bool fit, int version)
        {
            string w = info.PixelWidth.ToString("0.###", CultureInfo.InvariantCulture);
            string h = info.PixelHeight.ToString("0.###", CultureInfo.InvariantCulture);
            string img = fit
                ? "width:100%;height:100%;object-fit:contain;"
                : $"width:{w}px;height:{h}px;flex:none;margin:auto;";
            string body = fit
                ? "height:100vh;padding:24px;box-sizing:border-box;"
                : "min-height:100vh;display:flex;padding:24px;box-sizing:border-box;";
            string overflow = fit ? "overflow:hidden;" : "overflow:auto;";

            return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
                   $"html,body{{margin:0;{BackgroundCss(bg)}{overflow}}}" +
                   $"body{{{body}}}" +
                   $"img{{display:block;{img}}}" +
                   $"</style></head><body><img alt=\"\" src=\"{ImagePath}?v={version}\"></body></html>";
        }

        /// <summary>The page the PNG is captured from: the SVG at exactly <paramref name="width"/> x <paramref name="height"/> with no margin, on a colour or on nothing.</summary>
        public static string BuildExportHtml(int width, int height, string? backgroundColor, int version)
        {
            string bg = string.IsNullOrEmpty(backgroundColor) ? "background:transparent;" : $"background:{backgroundColor};";
            return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
                   $"html,body{{margin:0;padding:0;{bg}overflow:hidden;}}" +
                   $"img{{display:block;width:{width}px;height:{height}px;}}" +
                   $"</style></head><body><img alt=\"\" src=\"{ImagePath}?v={version}\"></body></html>";
        }

        /// <summary>The SVG as served: never executes (it is only ever the source of an &lt;img&gt;) but still declared with a charset.</summary>
        public static byte[] ImageBytes(string svgText) => new UTF8Encoding(false).GetBytes(svgText);

        // =================================================================
        //  Code pane
        // =================================================================

        /// <summary>Text for the Code pane; a very long file is cut so the TextBox stays quick.</summary>
        public static string CodeForDisplay(string text, out bool truncated)
        {
            truncated = text.Length > CodePreviewLimit;
            return truncated ? text[..CodePreviewLimit] : text;
        }

        // =================================================================
        //  Settings (how the tool is set up - never file paths)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "svg_tool.json");

        public static SvgToolSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new SvgToolSettings();
                var s = JsonSerializer.Deserialize<SvgToolSettings>(File.ReadAllText(file), JsonOptions) ?? new SvgToolSettings();
                if (!Enum.IsDefined(s.Background)) s.Background = SvgBackground.Checker;
                s.Optimize ??= new SvgOptimizeOptions();
                s.Optimize.Precision = Math.Clamp(s.Optimize.Precision, 0, 6);
                s.ExportWidth = Math.Clamp(s.ExportWidth, MinExportSide, MaxExportSide);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                return new SvgToolSettings();
            }
        }

        public static void SaveSettings(SvgToolSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);

        /// <summary>WebView2's cache and cookies; kept per-user so an installed copy never writes next to the .exe.</summary>
        public static string WebViewDataFolder => AppPaths.WebViewData;
    }
}
