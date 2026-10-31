using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Game Controller Tester backend - the portable half (no WPF, no Windows APIs): turning raw readings
    /// into something comparable, dead zones, and the three analyses (rest drift, range / roundness, trigger curve).
    /// The controller reading itself (XInput and the legacy joystick API) is in <c>GameControllerManager.Windows.cs</c>.
    /// </summary>
    public static partial class GameControllerManager
    {
        // What Microsoft recommends for XInput (of the full +/-32767 range), as percentages.
        public const int XInputLeftDeadzonePercent = 24;     // 7849 / 32767
        public const int XInputRightDeadzonePercent = 27;    // 8689 / 32767
        public const int XInputTriggerThresholdPercent = 12; // 30 / 255

        public const int EnvelopeBuckets = 36;               // 10 degrees each
        public const double MinRestSamples = 30;

        // =================================================================
        //  Numbers
        // =================================================================

        /// <summary>An XInput stick value (-32768..32767) as -1..1.</summary>
        public static double NormalizeStick(short raw) => raw < 0 ? Math.Max(-1.0, raw / 32768.0) : raw / 32767.0;

        /// <summary>A trigger (0..255) as 0..1.</summary>
        public static double NormalizeTrigger(byte raw) => raw / 255.0;

        /// <summary>A legacy joystick axis (0..max, the middle is the centre) as -1..1.</summary>
        public static double NormalizeAxis(uint raw, uint min, uint max)
        {
            if (max <= min) return 0;
            return Math.Clamp((raw - min) * 2.0 / (max - min) - 1.0, -1.0, 1.0);
        }

        public static double Magnitude(double x, double y) => Math.Sqrt(x * x + y * y);

        // =================================================================
        //  Dead zones
        // =================================================================

        /// <summary>
        /// The stick position after the dead zone. <paramref name="deadzone"/> is 0..1 of full travel.
        /// Axial cuts each axis on its own; Radial cuts by distance from the middle; Scaled radial also
        /// stretches what is left so the output starts from 0 right at the edge of the dead zone.
        /// </summary>
        public static (double X, double Y) ApplyDeadzone(double x, double y, double deadzone, DeadzoneMode mode)
        {
            deadzone = Math.Clamp(deadzone, 0, 0.95);
            if (deadzone <= 0) return (x, y);

            switch (mode)
            {
                case DeadzoneMode.Axial:
                    return (Math.Abs(x) < deadzone ? 0 : x, Math.Abs(y) < deadzone ? 0 : y);

                case DeadzoneMode.Radial:
                    return Magnitude(x, y) < deadzone ? (0, 0) : (x, y);

                default:
                {
                    double m = Magnitude(x, y);
                    if (m < deadzone) return (0, 0);
                    double scaled = Math.Min(1.0, (m - deadzone) / (1.0 - deadzone));
                    double k = scaled / m;
                    return (x * k, y * k);
                }
            }
        }

        /// <summary>A trigger after its threshold: 0 below it, then stretched to 0..1.</summary>
        public static double ApplyTriggerThreshold(double value, double threshold)
        {
            threshold = Math.Clamp(threshold, 0, 0.95);
            if (value <= threshold) return 0;
            return Math.Min(1.0, (value - threshold) / (1.0 - threshold));
        }

        // =================================================================
        //  Analysis: rest drift
        // =================================================================

        /// <summary>
        /// Looks at a stick that nobody is touching. <paramref name="samples"/> are (x, y) readings. The result says
        /// where the stick really rests (it is rarely exactly 0, 0), how far it wanders, and the smallest dead zone
        /// that would hide that (the farthest wander, with a little room, rounded up to a whole percent).
        /// </summary>
        public static StickRestResult? AnalyzeRest(IReadOnlyList<(double X, double Y)> samples)
        {
            if (samples.Count < MinRestSamples) return null;

            double cx = samples.Average(s => s.X), cy = samples.Average(s => s.Y);
            double maxFromZero = samples.Max(s => Magnitude(s.X, s.Y));
            double noise = samples.Max(s => Magnitude(s.X - cx, s.Y - cy));

            int suggested = (int)Math.Ceiling(Math.Min(0.95, maxFromZero * 1.15 + 0.005) * 100);
            return new StickRestResult(cx, cy, maxFromZero, noise, samples.Count, Math.Clamp(suggested, 1, 95));
        }

        // =================================================================
        //  Analysis: range and roundness
        // =================================================================

        /// <summary>Which 10-degree slice a stick position is in (0 = pointing right, counter-clockwise).</summary>
        public static int BucketOf(double x, double y)
        {
            double angle = Math.Atan2(y, x);
            if (angle < 0) angle += 2 * Math.PI;
            return Math.Min(EnvelopeBuckets - 1, (int)(angle / (2 * Math.PI) * EnvelopeBuckets));
        }

        /// <summary>
        /// While the stick is swept around its edge, remembers the farthest point in every direction.
        /// Only readings near the edge count (more than half way out), so the path to the edge doesn't pull the shape in.
        /// </summary>
        public static void AddRangeSample(double[] envelope, double x, double y)
        {
            double m = Magnitude(x, y);
            if (m < 0.5) return;
            int bucket = BucketOf(x, y);
            if (m > envelope[bucket]) envelope[bucket] = m;
        }

        /// <summary>
        /// <b>Coverage</b>: how much of the circle was reached (share of slices with an edge reading).
        /// <b>Roundness</b>: the shortest reach divided by the longest among the slices reached; 1 is a perfect circle,
        /// an octagonal gate gives about 0.92 on the corners, a worn or sticky stick gives less.
        /// </summary>
        public static StickRangeResult? AnalyzeRange(double[] envelope, int samples)
        {
            var reached = envelope.Where(v => v > 0).ToList();
            if (reached.Count < 6) return null;

            double max = reached.Max(), min = reached.Min();
            return new StickRangeResult((double[])envelope.Clone(), max, min, reached.Count / (double)envelope.Length, min / max, samples);
        }

        // =================================================================
        //  Analysis: trigger
        // =================================================================

        /// <summary>
        /// One pull of a trigger, as (time, value 0..1) readings. Finds where it starts to register, how far it goes,
        /// how steady the response is (1 = a perfectly straight line against time while it is being pulled), and a
        /// 10-point curve (the value at each tenth of the pull) for drawing.
        /// </summary>
        public static TriggerResult? AnalyzeTrigger(IReadOnlyList<(double Time, double Value)> samples)
        {
            if (samples.Count < 10) return null;

            double max = samples.Max(s => s.Value);
            if (max < 0.2) return null;   // never pulled

            int first = samples.ToList().FindIndex(s => s.Value > 0);
            int peak = samples.ToList().FindIndex(s => s.Value >= max);
            if (first < 0 || peak <= first + 2) return null;

            var rise = samples.Skip(first).Take(peak - first + 1).ToList();
            double firstValue = rise[0].Value;

            // Straightness: how far the real curve strays from the straight line start -> peak, as a share of the peak.
            double t0 = rise[0].Time, t1 = rise[^1].Time;
            double dt = Math.Max(1e-6, t1 - t0);
            double v0 = rise[0].Value, v1 = rise[^1].Value;
            double sumSq = 0;
            foreach (var (t, v) in rise)
            {
                double line = v0 + (v1 - v0) * (t - t0) / dt;
                sumSq += (v - line) * (v - line);
            }
            double rms = Math.Sqrt(sumSq / rise.Count);
            double linearity = Math.Clamp(1.0 - rms / Math.Max(max, 1e-6) * 2.5, 0, 1);

            var curve = new double[10];
            for (int i = 0; i < 10; i++)
            {
                double t = t0 + dt * (i + 1) / 10.0;
                curve[i] = rise.OrderBy(s => Math.Abs(s.Time - t)).First().Value;
            }
            return new TriggerResult(firstValue, max, linearity, samples.Count, curve);
        }

        // =================================================================
        //  Report rate
        // =================================================================

        /// <summary>
        /// Reports per second, from the moments new readings arrived. Pauses (stick not moving) are ignored, and the
        /// middle interval is used so a few late reports don't pull the figure down. Null when there is too little data.
        /// </summary>
        public static double? ReportRateHz(IReadOnlyList<double> arrivalTimes)
        {
            if (arrivalTimes.Count < 12) return null;
            var intervals = new List<double>();
            for (int i = 1; i < arrivalTimes.Count; i++)
            {
                double d = arrivalTimes[i] - arrivalTimes[i - 1];
                if (d > 0 && d < 0.05) intervals.Add(d);   // longer gaps are the stick being still
            }
            if (intervals.Count < 8) return null;
            intervals.Sort();
            double median = intervals[intervals.Count / 2];
            return 1.0 / median;
        }

        /// <summary>The usual speeds controllers report at, so "123 Hz" shows as "about 125 Hz".</summary>
        public static int NearestStandardRate(double hz)
        {
            int[] standard = { 60, 100, 125, 250, 500, 1000, 2000, 4000, 8000 };
            return standard.OrderBy(s => Math.Abs(s - hz)).First();
        }

        // =================================================================
        //  Settings (how the tool is set up - never the device)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "game_controller.json");

        public static GameControllerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new GameControllerSettings();
                var s = JsonSerializer.Deserialize<GameControllerSettings>(File.ReadAllText(file), JsonOptions) ?? new GameControllerSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = DeadzoneMode.ScaledRadial;
                s.LeftDeadzonePercent = Math.Clamp(s.LeftDeadzonePercent, 0, 50);
                s.RightDeadzonePercent = Math.Clamp(s.RightDeadzonePercent, 0, 50);
                s.TriggerThresholdPercent = Math.Clamp(s.TriggerThresholdPercent, 0, 50);
                return s;
            }
            catch (Exception)
            {
                return new GameControllerSettings();
            }
        }

        public static void SaveSettings(GameControllerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
