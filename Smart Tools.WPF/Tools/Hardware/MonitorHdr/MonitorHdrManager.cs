using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Monitor HDR &amp; Color Depth Checker - the logic that needs no Windows API: the plain-words verdicts, how much of sRGB / DCI-P3 /
    /// Rec. 2020 a screen covers, the EDID bit-depth byte, the test picture numbers and the text report.
    /// The readings themselves come from <c>MonitorHdrManager.Windows.cs</c>.
    /// </summary>
    public static partial class MonitorHdrManager
    {
        // =================================================================
        //  Test pictures
        // =================================================================

        public static readonly IReadOnlyList<MonitorHdrPattern> Patterns = new[]
        {
            new MonitorHdrPattern("ramp_gray", MonitorHdrPatternGroup.Gradients),
            new MonitorHdrPattern("ramp_dark", MonitorHdrPatternGroup.Gradients),
            new MonitorHdrPattern("ramp_rgb", MonitorHdrPatternGroup.Gradients),
            new MonitorHdrPattern("black_steps", MonitorHdrPatternGroup.Levels),
            new MonitorHdrPattern("white_steps", MonitorHdrPatternGroup.Levels),
            new MonitorHdrPattern("gamut", MonitorHdrPatternGroup.ColorBrightness),
            new MonitorHdrPattern("white_window", MonitorHdrPatternGroup.ColorBrightness),
        };

        /// <summary>Near-black steps 0..15: a good screen shows all but the first as separate bars.</summary>
        public static readonly byte[] BlackSteps = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        /// <summary>Near-white steps 240..255: a clipped screen merges the last ones into plain white.</summary>
        public static readonly byte[] WhiteSteps = Enumerable.Range(240, 16).Select(i => (byte)i).ToArray();

        /// <summary>Share of the screen the white window covers, as the Up/Down keys step through it.</summary>
        public static readonly int[] WhiteWindowPercents = { 2, 5, 10, 25, 50, 75, 100 };

        /// <summary>The 8-bit value of column <paramref name="x"/> of <paramref name="width"/> on a ramp from <paramref name="low"/> to <paramref name="high"/>.</summary>
        public static byte RampValue(int x, int width, int low, int high)
        {
            if (width <= 1) return (byte)low;
            double t = Math.Clamp(x / (double)(width - 1), 0, 1);
            return (byte)Math.Clamp((int)Math.Round(low + (high - low) * t), 0, 255);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "monitor-hdr.json");

        public static MonitorHdrSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new MonitorHdrSettings();
                var s = JsonSerializer.Deserialize<MonitorHdrSettings>(File.ReadAllText(file), JsonOptions) ?? new MonitorHdrSettings();
                s.SlideshowSeconds = Math.Clamp(s.SlideshowSeconds, 0, 60);
                return s;
            }
            catch (Exception)
            {
                return new MonitorHdrSettings();
            }
        }

        public static void SaveSettings(MonitorHdrSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }

        // =================================================================
        //  Colour spaces
        // =================================================================

        private static readonly (string Name, MonitorHdrXy R, MonitorHdrXy G, MonitorHdrXy B)[] Standards =
        {
            ("sRGB", new(0.640, 0.330), new(0.300, 0.600), new(0.150, 0.060)),
            ("DCI-P3", new(0.680, 0.320), new(0.265, 0.690), new(0.150, 0.060)),
            ("Rec. 2020", new(0.708, 0.292), new(0.170, 0.797), new(0.131, 0.046)),
        };

        public static IReadOnlyList<(string Name, MonitorHdrXy R, MonitorHdrXy G, MonitorHdrXy B)> StandardSpaces => Standards;

        /// <summary>The outline of every colour the eye sees (CIE 1931 xy, spectral locus from 460 nm to 700 nm), for the gamut chart.</summary>
        public static readonly MonitorHdrXy[] SpectralLocus =
        {
            new(0.1741, 0.0050), new(0.1440, 0.0297), new(0.1241, 0.0578), new(0.0913, 0.1327), new(0.0454, 0.2950),
            new(0.0082, 0.5384), new(0.0139, 0.7502), new(0.0743, 0.8338), new(0.1547, 0.8059), new(0.2296, 0.7543),
            new(0.3016, 0.6923), new(0.3731, 0.6245), new(0.4441, 0.5547), new(0.5125, 0.4866), new(0.5752, 0.4242),
            new(0.6270, 0.3725), new(0.6658, 0.3340), new(0.6915, 0.3083), new(0.7079, 0.2920), new(0.7347, 0.2653),
        };

        /// <summary>How much of each standard the screen's triangle covers (the part of the standard's triangle that lies inside it), and its size next to it.</summary>
        public static List<MonitorHdrGamut> GamutCoverage(MonitorHdrPrimaries primaries)
        {
            var result = new List<MonitorHdrGamut>();
            if (!primaries.IsKnown) return result;
            var screen = new[] { primaries.Red, primaries.Green, primaries.Blue };
            double screenArea = Math.Abs(Area(screen));
            foreach (var (name, r, g, b) in Standards)
            {
                var standard = new[] { r, g, b };
                double standardArea = Math.Abs(Area(standard));
                double overlap = Math.Abs(Area(Clip(standard, screen)));
                result.Add(new MonitorHdrGamut(name, Math.Min(100, overlap / standardArea * 100), screenArea / standardArea * 100));
            }
            return result;
        }

        private static double Area(IReadOnlyList<MonitorHdrXy> poly)
        {
            double sum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2;
        }

        /// <summary>Sutherland-Hodgman: the part of convex polygon <paramref name="subject"/> that lies inside convex polygon <paramref name="clip"/>.</summary>
        private static List<MonitorHdrXy> Clip(IReadOnlyList<MonitorHdrXy> subject, IReadOnlyList<MonitorHdrXy> clip)
        {
            var output = subject.ToList();
            bool clockwise = Area(clip) < 0;
            for (int i = 0; i < clip.Count && output.Count > 0; i++)
            {
                var a = clip[i];
                var b = clip[(i + 1) % clip.Count];
                var input = output;
                output = new List<MonitorHdrXy>();
                for (int j = 0; j < input.Count; j++)
                {
                    var current = input[j];
                    var previous = input[(j + input.Count - 1) % input.Count];
                    bool currentIn = Inside(a, b, current, clockwise);
                    bool previousIn = Inside(a, b, previous, clockwise);
                    if (currentIn)
                    {
                        if (!previousIn) output.Add(Cross(previous, current, a, b));
                        output.Add(current);
                    }
                    else if (previousIn)
                    {
                        output.Add(Cross(previous, current, a, b));
                    }
                }
            }
            return output;
        }

        private static bool Inside(MonitorHdrXy a, MonitorHdrXy b, MonitorHdrXy p, bool clockwise)
        {
            double side = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            return clockwise ? side <= 0 : side >= 0;
        }

        private static MonitorHdrXy Cross(MonitorHdrXy p1, MonitorHdrXy p2, MonitorHdrXy a, MonitorHdrXy b)
        {
            double d1x = p2.X - p1.X, d1y = p2.Y - p1.Y, d2x = b.X - a.X, d2y = b.Y - a.Y;
            double denominator = d1x * d2y - d1y * d2x;
            if (Math.Abs(denominator) < 1e-12) return p2;
            double t = ((a.X - p1.X) * d2y - (a.Y - p1.Y) * d2x) / denominator;
            return new MonitorHdrXy(p1.X + t * d1x, p1.Y + t * d1y);
        }

        // =================================================================
        //  EDID
        // =================================================================

        /// <summary>Bits per colour the screen says it has, from byte 0x14 of its EDID (digital inputs only); 0 when the EDID doesn't say.</summary>
        public static int ParseEdidBits(byte[]? edid)
        {
            if (edid == null || edid.Length < 128) return 0;
            byte input = edid[0x14];
            if ((input & 0x80) == 0) return 0;      // analog input: no depth field
            return ((input >> 4) & 7) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => 0 };
        }

        // =================================================================
        //  Verdicts
        // =================================================================

        /// <summary>What the readings mean, in plain words, with what to change. Each one is a key into the language files.</summary>
        public static List<MonitorHdrVerdict> BuildVerdicts(MonitorHdrInfo m)
        {
            var list = new List<MonitorHdrVerdict>();
            static object[] None() => Array.Empty<object>();

            if (!m.HdrSupported)
                list.Add(new("no_hdr", MonitorHdrSeverity.Info, None()));
            else if (!m.HdrEnabled)
                list.Add(new("hdr_off", MonitorHdrSeverity.Warning, None()));
            else if (m.PeakNits > 0 && m.PeakNits < 400)
                list.Add(new("hdr_dim", MonitorHdrSeverity.Info, new object[] { m.PeakNits }));
            else
                list.Add(m.PeakNits > 0 ? new("hdr_on", MonitorHdrSeverity.Good, new object[] { m.PeakNits }) : new("hdr_on_plain", MonitorHdrSeverity.Good, None()));

            if (m.CurrentBits > 0)
            {
                if (m.PanelMaxBits > m.CurrentBits)
                    list.Add(new("bits_limited", MonitorHdrSeverity.Warning, new object[] { m.CurrentBits, m.PanelMaxBits, m.RefreshHz }));
                else if (m.HdrEnabled && m.CurrentBits <= 8)
                    list.Add(new("bits_hdr8", MonitorHdrSeverity.Warning, new object[] { m.CurrentBits }));
                else if (m.CurrentBits >= 10)
                    list.Add(new("bits_ok", MonitorHdrSeverity.Good, new object[] { m.CurrentBits }));
                else
                    list.Add(new("bits_8", MonitorHdrSeverity.Info, new object[] { m.CurrentBits }));
            }

            if (m.ColorFormat.StartsWith("YCbCr", StringComparison.OrdinalIgnoreCase) && !m.ColorFormat.Contains("4:4:4"))
                list.Add(new("chroma", MonitorHdrSeverity.Warning, new object[] { m.ColorFormat }));

            return list;
        }

        public static string FormatVerdict(MonitorHdrVerdict verdict, Func<string, string> text)
        {
            string template = text("Verdict_" + verdict.Key);
            try { return string.Format(CultureInfo.CurrentCulture, template, verdict.Args); }
            catch (FormatException) { return template; }
        }

        // =================================================================
        //  Report
        // =================================================================

        /// <summary>The readings of every screen as plain text, for copying or saving. <paramref name="text"/> turns a label key into the language of the page.</summary>
        public static string BuildReport(IReadOnlyList<MonitorHdrInfo> screens, Func<string, string> text)
        {
            var sb = new StringBuilder();
            var culture = CultureInfo.InvariantCulture;
            sb.AppendLine(text("ReportTitle"));
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm", culture));
            foreach (var m in screens)
            {
                sb.AppendLine();
                sb.AppendLine($"== {m.Name}{(m.FriendlyName.Length > 0 ? " - " + m.FriendlyName : string.Empty)} ==");
                sb.AppendLine($"{text("Resolution")}: {m.Width} x {m.Height} @ {(m.RefreshHz > 0 ? m.RefreshHz + " Hz" : "?")}");
                sb.AppendLine($"{text("BitDepth")}: {Bits(m.CurrentBits)}   ({text("PanelMax")}: {Bits(m.PanelMaxBits)})");
                sb.AppendLine($"{text("ColorFormat")}: {(m.ColorFormat.Length > 0 ? m.ColorFormat : "?")}");
                sb.AppendLine($"{text("Hdr")}: {(m.HdrSupported ? (m.HdrEnabled ? text("On") : text("Off")) : text("NotSupported"))}");
                if (m.HdrSupported && m.PeakNits > 0)
                {
                    sb.AppendLine($"{text("PeakNits")}: {m.PeakNits.ToString("0", culture)} nits");
                    sb.AppendLine($"{text("FullFrameNits")}: {m.FullFrameNits.ToString("0", culture)} nits");
                    sb.AppendLine($"{text("MinNits")}: {m.MinNits.ToString("0.####", culture)} nits");
                }
                if (m.HdrSupported && m.SdrWhiteNits > 0) sb.AppendLine($"{text("SdrWhite")}: {m.SdrWhiteNits.ToString("0", culture)} nits");
                foreach (var g in GamutCoverage(m.Primaries))
                    sb.AppendLine($"{g.Name}: {g.CoveragePercent.ToString("0", culture)}% ({text("Size")} {g.SizePercent.ToString("0", culture)}%)");
                foreach (var v in BuildVerdicts(m))
                    sb.AppendLine($"- {FormatVerdict(v, text)}");
            }
            return sb.ToString();
        }

        private static string Bits(int bits) => bits > 0 ? $"{bits}-bit" : "?";
    }
}
