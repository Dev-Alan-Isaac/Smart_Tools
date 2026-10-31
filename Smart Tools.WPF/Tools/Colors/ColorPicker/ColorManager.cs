using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Color engine behind ColorPickerView (and the hex parsing the Barcode /
    /// QR generators use): conversions between HEX / RGB / HSL / HSV, a
    /// forgiving parser (hex, rgb(), hsl(), CSS names), WCAG contrast,
    /// shades &amp; tints, batch conversion and CSV / JSON / XML export.
    ///
    /// No WPF types - everything here is unit-testable headlessly.
    ///
    /// The visual picker works in HSV (a saturation/brightness square plus a
    /// hue bar maps naturally onto it - Windows' and Photoshop's pickers do
    /// the same); the fields and exports show HSL, which is what CSS uses.
    /// </summary>
    public static class ColorManager
    {
        // =================================================================
        //  HEX
        // =================================================================

        public static string RgbToHex(RgbColor c) => c.Hex;

        // #RGB, #RGBA, #RRGGBB, #RRGGBBAA (alpha is accepted and ignored).
        private static readonly Regex HexPattern = new(
            @"^#?([0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

        public static bool TryHexToRgb(string? hex, out RgbColor color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var match = HexPattern.Match(hex.Trim());
            if (!match.Success)
                return false;

            string h = match.Groups[1].Value;
            if (h.Length is 3 or 4)
                h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);

            color = new RgbColor(
                byte.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(h[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return true;
        }

        // =================================================================
        //  HSL
        // =================================================================

        public static HslColor RgbToHsl(RgbColor c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            double h = HueOf(r, g, b, max, delta);
            double l = (max + min) / 2;
            double s = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * l - 1));

            return new HslColor(h, Math.Clamp(s * 100, 0, 100), l * 100);
        }

        public static RgbColor HslToRgb(HslColor hsl)
        {
            double h = NormalizeHue(hsl.H);
            double s = Math.Clamp(hsl.S, 0, 100) / 100.0;
            double l = Math.Clamp(hsl.L, 0, 100) / 100.0;

            double c = (1 - Math.Abs(2 * l - 1)) * s;
            return FromChroma(h, c, l - c / 2);
        }

        // =================================================================
        //  HSV (picker UI)
        // =================================================================

        public static RgbColor HsvToRgb(double h, double s, double v)
        {
            h = NormalizeHue(h);
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double c = v * s;
            return FromChroma(h, c, v - c);
        }

        public static RgbColor HsvToRgb(HsvColor hsv) => HsvToRgb(hsv.H, hsv.S, hsv.V);

        public static HsvColor RgbToHsv(RgbColor c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            return new HsvColor(HueOf(r, g, b, max, delta), max == 0 ? 0 : delta / max, max);
        }

        private static double HueOf(double r, double g, double b, double max, double delta)
        {
            if (delta <= 0) return 0;

            double h;
            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * (((b - r) / delta) + 2);
            else h = 60 * (((r - g) / delta) + 4);

            return h < 0 ? h + 360 : h;
        }

        private static RgbColor FromChroma(double h, double c, double m)
        {
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));

            (double r1, double g1, double b1) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };

            return new RgbColor(ToByte(r1 + m), ToByte(g1 + m), ToByte(b1 + m));
        }

        private static byte ToByte(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);

        /// <summary>Wraps any angle into [0, 360).</summary>
        public static double NormalizeHue(double h)
        {
            h %= 360;
            return h < 0 ? h + 360 : h;
        }

        // =================================================================
        //  Text formats
        // =================================================================

        public static string ToCssRgb(RgbColor c) => $"rgb({c.R}, {c.G}, {c.B})";

        public static string ToCssHsl(RgbColor c)
        {
            var hsl = RgbToHsl(c);
            return string.Create(CultureInfo.InvariantCulture, $"hsl({hsl.H:0}, {hsl.S:0}%, {hsl.L:0}%)");
        }

        public static string ToRgbTriplet(RgbColor c) => $"{c.R}, {c.G}, {c.B}";

        public static string ToHslTriplet(RgbColor c)
        {
            var hsl = RgbToHsl(c);
            return string.Create(CultureInfo.InvariantCulture, $"{hsl.H:0}°, {hsl.S:0}%, {hsl.L:0}%");
        }

        // =================================================================
        //  Parsing (manual entry, batch, paste)
        // =================================================================

        private const string Num = @"(\d{1,3}(?:\.\d+)?)";

        private static readonly Regex RgbPattern = new(
            $@"^(?:rgba?)?\s*\(?\s*{Num}\s*[,\s]\s*{Num}\s*[,\s]\s*{Num}\s*(?:[,/]\s*[\d.]+%?\s*)?\)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HslPattern = new(
            $@"^hsla?\s*\(?\s*{Num}\s*(?:°|deg)?\s*[,\s]\s*{Num}\s*%?\s*[,\s]\s*{Num}\s*%?\s*(?:[,/]\s*[\d.]+%?\s*)?\)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads a color in any common notation: "#RRGGBB", "RRGGBB", "#RGB",
        /// "#RRGGBBAA", "r, g, b", "rgb(r g b)", "rgba(r,g,b,a)",
        /// "hsl(h, s%, l%)", "hsl(210deg 50% 40%)", or a CSS color name
        /// ("tomato", "rebeccapurple"). Alpha is accepted and ignored.
        /// </summary>
        public static bool TryParseColor(string? input, out RgbColor color)
            => TryParseColor(input, out color, out _);

        /// <param name="error">Why the input was rejected (empty when it parsed).</param>
        public static bool TryParseColor(string? input, out RgbColor color, out string error)
        {
            color = default;
            error = "";

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "Empty";
                return false;
            }

            string s = input.Trim().TrimEnd(';');

            if (TryHexToRgb(s, out color))
                return true;

            var rgb = RgbPattern.Match(s);
            if (rgb.Success)
            {
                double r = Parse(rgb.Groups[1].Value), g = Parse(rgb.Groups[2].Value), b = Parse(rgb.Groups[3].Value);
                if (r > 255 || g > 255 || b > 255)
                {
                    error = "RGB values must be 0-255";
                    return false;
                }
                color = new RgbColor((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
                return true;
            }

            var hsl = HslPattern.Match(s);
            if (hsl.Success)
            {
                double h = Parse(hsl.Groups[1].Value), sat = Parse(hsl.Groups[2].Value), l = Parse(hsl.Groups[3].Value);
                if (h > 360 || sat > 100 || l > 100)
                {
                    error = "HSL needs hue 0-360 and saturation/lightness 0-100%";
                    return false;
                }
                color = HslToRgb(new HslColor(h, sat, l));
                return true;
            }

            if (TryNamedColor(s, out color))
                return true;

            error = s.StartsWith('#') || s.All(Uri.IsHexDigit)
                ? "Hex colors need 3, 4, 6 or 8 digits"
                : "Not a recognised color - try #1A2B3C, rgb(26, 43, 60), hsl(210, 40%, 17%) or a name like 'tomato'";
            return false;
        }

        private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>CSS / .NET named colors ("tomato", "SteelBlue"). System UI colors are excluded.</summary>
        public static bool TryNamedColor(string name, out RgbColor color)
        {
            color = default;
            string key = name.Replace(" ", "").Replace("-", "");
            if (key.Length == 0 || !key.All(char.IsLetter))
                return false;

            var known = System.Drawing.Color.FromName(key);
            if (!known.IsKnownColor || known.IsSystemColor || known.A == 0)
                return false;

            color = new RgbColor(known.R, known.G, known.B);
            return true;
        }

        // =================================================================
        //  Contrast (WCAG 2.x)
        // =================================================================

        public static double RelativeLuminance(RgbColor c)
        {
            static double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        public static ContrastResult Contrast(RgbColor a, RgbColor b)
        {
            double la = RelativeLuminance(a), lb = RelativeLuminance(b);
            double ratio = (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
            return new ContrastResult(Math.Round(ratio, 2));
        }

        public static readonly RgbColor White = new(255, 255, 255);
        public static readonly RgbColor Black = new(0, 0, 0);

        /// <summary>Black or white - whichever reads better on <paramref name="background"/>.</summary>
        public static RgbColor BestTextColor(RgbColor background) =>
            Contrast(background, Black).Ratio >= Contrast(background, White).Ratio ? Black : White;

        // =================================================================
        //  Shades & tints
        // =================================================================

        /// <summary>
        /// <paramref name="count"/> steps from dark to light at the color's
        /// hue and saturation (lightness 10% to 95%); the step nearest the
        /// color's own lightness is replaced by the exact color.
        /// Returns the list and the index of the original color.
        /// </summary>
        public static (List<RgbColor> Colors, int OriginalIndex) GenerateShades(RgbColor color, int count = 9)
        {
            count = Math.Max(3, count);
            var hsl = RgbToHsl(color);
            var list = new List<RgbColor>(count);

            int nearest = 0;
            double best = double.MaxValue;
            for (int i = 0; i < count; i++)
            {
                double l = 10 + (95 - 10) * i / (double)(count - 1);
                list.Add(HslToRgb(new HslColor(hsl.H, hsl.S, l)));
                double d = Math.Abs(l - hsl.L);
                if (d < best) { best = d; nearest = i; }
            }

            list[nearest] = color;
            return (list, nearest);
        }

        // =================================================================
        //  Batch
        // =================================================================

        /// <summary>One row per non-empty line. Lines starting with "//" or ";" are comments.</summary>
        public static List<BatchColorRow> ConvertBatch(IEnumerable<string> lines)
        {
            var rows = new List<BatchColorRow>();
            int index = 0;

            foreach (string raw in lines)
            {
                string input = raw.Trim();
                if (input.Length == 0 || input.StartsWith("//") || input.StartsWith(';'))
                    continue;

                index++;
                if (TryParseColor(input, out var rgb, out string error))
                {
                    rows.Add(new BatchColorRow
                    {
                        Index = index,
                        Input = input,
                        Status = ColorRowStatus.Converted,
                        Hex = rgb.Hex,
                        Rgb = ToRgbTriplet(rgb),
                        Hsl = ToHslTriplet(rgb),
                        Message = "Converted",
                    });
                }
                else
                {
                    rows.Add(new BatchColorRow
                    {
                        Index = index,
                        Input = input,
                        Status = ColorRowStatus.Failed,
                        Message = error,
                    });
                }
            }

            return rows;
        }

        /// <summary>Splits pasted/loaded text into lines (handles \r\n, \r and \n).</summary>
        public static IEnumerable<string> SplitLines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // =================================================================
        //  Export
        // =================================================================

        public static string ExtensionFor(ColorExportFormat format) => format switch
        {
            ColorExportFormat.Json => ".json",
            ColorExportFormat.Xml => ".xml",
            _ => ".csv",
        };

        public static string FileFilterFor(ColorExportFormat format) => format switch
        {
            ColorExportFormat.Json => "JSON file (*.json)|*.json",
            ColorExportFormat.Xml => "XML file (*.xml)|*.xml",
            _ => "CSV file (*.csv)|*.csv",
        };

        public static void Export(IReadOnlyList<BatchColorRow> rows, string path, ColorExportFormat format)
        {
            string text = format switch
            {
                ColorExportFormat.Json => ToJson(rows),
                ColorExportFormat.Xml => ToXml(rows),
                _ => ToCsv(rows),
            };
            // UTF-8 with BOM for CSV so Excel shows "°" correctly; plain UTF-8 otherwise.
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ColorExportFormat.Csv));
        }

        public static string ToCsv(IReadOnlyList<BatchColorRow> rows)
        {
            var sb = new StringBuilder("Input,Hex,RGB,HSL,Status\r\n");
            foreach (var row in rows)
            {
                sb.Append(CsvField(row.Input)).Append(',')
                  .Append(CsvField(row.Hex)).Append(',')
                  .Append(CsvField(row.Rgb)).Append(',')
                  .Append(CsvField(row.Hsl)).Append(',')
                  .Append(CsvField(row.Message))
                  .Append("\r\n");
            }
            return sb.ToString();
        }

        public static string ToJson(IReadOnlyList<BatchColorRow> rows)
        {
            var items = rows.Select(r => new
            {
                input = r.Input,
                hex = r.Hex,
                rgb = r.Rgb,
                hsl = r.Hsl,
                status = r.Status == ColorRowStatus.Converted ? "Converted" : "Failed",
                message = r.Message,
            });

            return JsonSerializer.Serialize(items, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "°" readable
            }) + "\n";
        }

        public static string ToXml(IReadOnlyList<BatchColorRow> rows)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
            using (var w = XmlWriter.Create(sb, settings))
            {
                w.WriteStartElement("Colors");
                foreach (var r in rows)
                {
                    w.WriteStartElement("Color");
                    w.WriteElementString("Input", XmlSafe(r.Input));
                    w.WriteElementString("Hex", r.Hex);
                    w.WriteElementString("Rgb", r.Rgb);
                    w.WriteElementString("Hsl", r.Hsl);
                    w.WriteElementString("Status", r.Status == ColorRowStatus.Converted ? "Converted" : "Failed");
                    w.WriteElementString("Message", XmlSafe(r.Message));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + sb.ToString().Replace("\r\n", "\n") + "\n";
        }

        private static string CsvField(string value)
        {
            // Leading = + - @ would run as a formula in Excel ("CSV injection").
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0)
                value = "'" + value;

            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        /// <summary>Drops characters XML 1.0 can't represent (pasted control characters).</summary>
        private static string XmlSafe(string value) =>
            new(value.Where(XmlConvert.IsXmlChar).ToArray());
    }
}
