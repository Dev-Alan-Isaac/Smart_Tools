using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Monitor Test backend - the portable half (no WPF, no Windows APIs): the list of test pictures and the numbers behind them,
    /// moving-object maths, frame-timing statistics and settings. The pictures themselves are drawn by
    /// <c>Controls/MonitorPatternSurface.cs</c>; screens and the refresh probe are in <c>MonitorTestManager.Windows.cs</c>.
    /// </summary>
    public static partial class MonitorTestManager
    {
        // =================================================================
        //  The pictures
        // =================================================================

        public static readonly IReadOnlyList<MonitorPattern> Patterns = new MonitorPattern[]
        {
            // dead and stuck pixels: a pixel that is wrong shows on a colour that it can't make
            new("solid_black", MonitorPatternGroup.DeadPixels),
            new("solid_white", MonitorPatternGroup.DeadPixels),
            new("solid_red", MonitorPatternGroup.DeadPixels),
            new("solid_green", MonitorPatternGroup.DeadPixels),
            new("solid_blue", MonitorPatternGroup.DeadPixels),
            new("solid_cyan", MonitorPatternGroup.DeadPixels),
            new("solid_magenta", MonitorPatternGroup.DeadPixels),
            new("solid_yellow", MonitorPatternGroup.DeadPixels),
            new("solid_gray25", MonitorPatternGroup.DeadPixels),
            new("solid_gray50", MonitorPatternGroup.DeadPixels),
            new("solid_gray75", MonitorPatternGroup.DeadPixels),

            // light bleed, clouding and uneven backlight
            new("bleed_black", MonitorPatternGroup.Backlight),
            new("clouding_5", MonitorPatternGroup.Backlight),
            new("clouding_10", MonitorPatternGroup.Backlight),
            new("clouding_20", MonitorPatternGroup.Backlight),

            // gradients and banding
            new("ramp_gray", MonitorPatternGroup.Gradients),
            new("ramp_red", MonitorPatternGroup.Gradients),
            new("ramp_green", MonitorPatternGroup.Gradients),
            new("ramp_blue", MonitorPatternGroup.Gradients),
            new("steps_gray", MonitorPatternGroup.Gradients),

            // calibration
            new("black_levels", MonitorPatternGroup.Calibration),
            new("white_levels", MonitorPatternGroup.Calibration),
            new("gray_steps", MonitorPatternGroup.Calibration),
            new("gamma", MonitorPatternGroup.Calibration),
            new("color_bars", MonitorPatternGroup.Calibration),
            new("sharpness", MonitorPatternGroup.Calibration),
            new("geometry", MonitorPatternGroup.Calibration),
            new("checker_1px", MonitorPatternGroup.Calibration),
            new("lines", MonitorPatternGroup.Calibration),

            // image retention and burn-in
            new("cycle_colors", MonitorPatternGroup.Retention, Animated: true),
            new("inverse", MonitorPatternGroup.Retention, Animated: true),
            new("exerciser", MonitorPatternGroup.Retention, Animated: true, Flashing: true),

            // motion
            new("ufo", MonitorPatternGroup.Motion, Animated: true),
            new("scroll_bars", MonitorPatternGroup.Motion, Animated: true),
        };

        public static MonitorPattern? Find(string id) => Patterns.FirstOrDefault(p => p.Id == id);

        /// <summary>The flat colour of a one-colour picture (the plain colours and the dark greys), or null for anything drawn.</summary>
        public static (byte R, byte G, byte B)? SolidColor(string id) => id switch
        {
            "solid_black" or "bleed_black" => (0, 0, 0),
            "solid_white" => (255, 255, 255),
            "solid_red" => (255, 0, 0),
            "solid_green" => (0, 255, 0),
            "solid_blue" => (0, 0, 255),
            "solid_cyan" => (0, 255, 255),
            "solid_magenta" => (255, 0, 255),
            "solid_yellow" => (255, 255, 0),
            "solid_gray25" => (GrayFromPercent(25), GrayFromPercent(25), GrayFromPercent(25)),
            "solid_gray50" => (GrayFromPercent(50), GrayFromPercent(50), GrayFromPercent(50)),
            "solid_gray75" => (GrayFromPercent(75), GrayFromPercent(75), GrayFromPercent(75)),
            "clouding_5" => (GrayFromPercent(5), GrayFromPercent(5), GrayFromPercent(5)),
            "clouding_10" => (GrayFromPercent(10), GrayFromPercent(10), GrayFromPercent(10)),
            "clouding_20" => (GrayFromPercent(20), GrayFromPercent(20), GrayFromPercent(20)),
            _ => null,
        };

        /// <summary>A grey as a 0..255 value, from a percentage of full brightness written in the display's own numbers (not light output).</summary>
        public static byte GrayFromPercent(double percent) => (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0, MidpointRounding.AwayFromZero);

        // The levels the calibration pictures show side by side. Most displays should tell all the black ones apart
        // (a monitor that "crushes" blacks merges the first few) and all the white ones (clipping merges the last few).
        public static readonly IReadOnlyList<byte> BlackLevels = new byte[] { 0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32 };
        public static readonly IReadOnlyList<byte> WhiteLevels = new byte[] { 255, 254, 253, 252, 251, 250, 248, 246, 244, 242, 240, 238, 236, 234, 232, 230 };

        /// <summary>0, 10, 20 ... 100 percent as display values.</summary>
        public static IReadOnlyList<byte> GrayStepValues { get; } = Enumerable.Range(0, 11).Select(i => GrayFromPercent(i * 10)).ToArray();

        public static readonly IReadOnlyList<double> GammaCandidates = new[] { 1.8, 2.0, 2.2, 2.4, 2.6 };

        /// <summary>
        /// The grey that gives the same average light as a 1-pixel black and white checker, if the display follows <paramref name="gamma"/>.
        /// The gamma test puts that solid grey next to such a checker: where the two look the same, the display's gamma is that number.
        /// </summary>
        public static byte GammaMatchGray(double gamma) => (byte)Math.Round(255 * Math.Pow(0.5, 1.0 / gamma), MidpointRounding.AwayFromZero);

        public static readonly IReadOnlyList<(byte R, byte G, byte B)> ColorBars = new (byte, byte, byte)[]
        {
            (255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0), (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0),
        };

        public static readonly IReadOnlyList<(byte R, byte G, byte B)> CycleColors = new (byte, byte, byte)[]
        {
            (255, 255, 255), (0, 0, 0), (255, 0, 0), (0, 255, 0), (0, 0, 255), (128, 128, 128),
        };

        // =================================================================
        //  Animated pictures
        // =================================================================

        /// <summary>Which colour of the cycle is showing at <paramref name="seconds"/> when each lasts <paramref name="period"/> seconds.</summary>
        public static int CycleIndex(double seconds, double period, int count) =>
            count <= 0 || period <= 0 ? 0 : (int)(Math.Max(0, seconds) / period) % count;

        /// <summary>The two states of the "inverse" test: the picture and its negative, swapped every <paramref name="period"/> seconds.</summary>
        public static bool InverseOn(double seconds, double period) => period > 0 && (int)(Math.Max(0, seconds) / period) % 2 == 1;

        /// <summary>Speeds of the moving boxes, in pixels per second, one lane each (slow to very fast).</summary>
        public static readonly IReadOnlyList<int> UfoSpeeds = new[] { 240, 480, 960, 1920 };

        /// <summary>Left edge of a block that moves at <paramref name="speed"/> px/s and wraps around a line of <paramref name="width"/> pixels.</summary>
        public static double MovingPosition(double seconds, double speed, double width, double blockWidth)
        {
            double travel = Math.Max(1, width + blockWidth);
            double x = (seconds * speed) % travel;
            return x - blockWidth;
        }

        /// <summary>A deterministic "random" colour for the pixel exerciser at a given frame (the same frame always gives the same colour).</summary>
        public static (byte R, byte G, byte B) ExerciserColor(int frame)
        {
            uint h = (uint)frame * 2654435761u;
            return ((byte)((h >> 8) & 0xFF), (byte)((h >> 16) & 0xFF), (byte)((h >> 24) & 0xFF));
        }

        // =================================================================
        //  Refresh rate and frame timing
        // =================================================================

        /// <summary>
        /// From the moments the display refreshed (seconds, in order). A refresh that took clearly longer than the usual one counts as missed:
        /// that is the computer being late (a stutter), not the monitor. Null when there are too few frames.
        /// </summary>
        public static FrameTimingResult? AnalyzeFrames(IReadOnlyList<double> times)
        {
            if (times.Count < 20) return null;

            var intervals = new List<double>(times.Count - 1);
            for (int i = 1; i < times.Count; i++)
            {
                double d = times[i] - times[i - 1];
                if (d > 0) intervals.Add(d);
            }
            if (intervals.Count < 15) return null;

            var sorted = intervals.OrderBy(v => v).ToList();
            double median = sorted[sorted.Count / 2];
            double mean = intervals.Average();
            double variance = intervals.Sum(v => (v - mean) * (v - mean)) / intervals.Count;
            int missed = intervals.Count(v => v > median * 1.5);

            // "1% low": the speed of the slowest 1% of frames, as frames per second
            int worstCount = Math.Max(1, intervals.Count / 100);
            double worstAvg = sorted.TakeLast(worstCount).Average();

            return new FrameTimingResult(
                Hz: 1.0 / median,
                AverageMs: mean * 1000,
                JitterMs: Math.Sqrt(variance) * 1000,
                WorstMs: sorted[^1] * 1000,
                Frames: intervals.Count,
                Missed: missed,
                MissedPercent: missed * 100.0 / intervals.Count,
                LowOnePercentHz: 1.0 / worstAvg);
        }

        /// <summary>The refresh rate Windows reports, next to the measured one: "close" when they agree within 3%.</summary>
        public static bool RatesAgree(int reportedHz, double measuredHz) =>
            reportedHz > 0 && Math.Abs(measuredHz - reportedHz) <= reportedHz * 0.03;

        // =================================================================
        //  Suspect pixels
        // =================================================================

        /// <summary>Text for the clipboard: one line per spot, with the picture it was seen on.</summary>
        public static string FormatMarks(IReadOnlyList<MonitorMark> marks, Func<string, string> patternName)
        {
            var sb = new StringBuilder();
            foreach (var m in marks)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"x={m.X}, y={m.Y}  ({patternName(m.PatternId)}, {m.At:HH:mm:ss})"));
            return sb.ToString().TrimEnd();
        }

        /// <summary>Adds a spot unless one is already within a pixel of it (a double click).</summary>
        public static bool AddMark(List<MonitorMark> marks, MonitorMark mark)
        {
            if (marks.Any(m => Math.Abs(m.X - mark.X) <= 1 && Math.Abs(m.Y - mark.Y) <= 1 && m.PatternId == mark.PatternId)) return false;
            marks.Add(mark);
            return true;
        }

        // =================================================================
        //  Settings (how the tool is set up - never marks or screens)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "monitor_test.json");

        public static MonitorTestSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new MonitorTestSettings();
                var s = JsonSerializer.Deserialize<MonitorTestSettings>(File.ReadAllText(file), JsonOptions) ?? new MonitorTestSettings();
                s.SlideshowSeconds = Math.Clamp(s.SlideshowSeconds, 0, 60);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new MonitorTestSettings();
            }
        }

        public static void SaveSettings(MonitorTestSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
