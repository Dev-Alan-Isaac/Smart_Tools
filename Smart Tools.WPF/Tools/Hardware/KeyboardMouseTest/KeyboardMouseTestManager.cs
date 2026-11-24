using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Keyboard &amp; Mouse Tester backend - the portable half (no WPF, no Windows APIs): the keyboard layout, key and button
    /// bookkeeping (what is held, how many at once, chatter), mouse polling-rate and DPI maths, reaction-time statistics
    /// and settings. The raw device input itself is in <c>KeyboardMouseTestManager.Windows.cs</c>.
    /// </summary>
    public static partial class KeyboardMouseTestManager
    {
        // =================================================================
        //  The keyboard (a full-size ANSI board). Keys are identified by what the hardware sends -
        //  the scan code, plus "E0-" for the extended keys - so the map is right for any language layout.
        // =================================================================

        public static string KeyId(int scanCode, bool extended) => (extended ? "E0-" : string.Empty) + scanCode.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>
        /// Windows sends a "fake" Shift (E0-2A / E0-36) around some extended keys, and a second code for Pause; neither is a key.
        /// </summary>
        public static bool IsPhantomKey(string id) => id is "E0-2A" or "E0-36" or "E0-00" or "00";

        public static IReadOnlyList<KeyDef> Layout { get; } = BuildLayout();

        public static int KeyCount => Layout.Count;

        private static List<KeyDef> BuildLayout()
        {
            var keys = new List<KeyDef>();
            void K(string id, string label, double x, double y, double w = 1, double h = 1) => keys.Add(new KeyDef(id, label, x, y, w, h));

            // function row
            K("01", "Esc", 0, 0);
            string[] f1 = { "3B", "3C", "3D", "3E" }, f2 = { "3F", "40", "41", "42" }, f3 = { "43", "44", "57", "58" };
            for (int i = 0; i < 4; i++) K(f1[i], $"F{i + 1}", 2 + i, 0);
            for (int i = 0; i < 4; i++) K(f2[i], $"F{i + 5}", 6.5 + i, 0);
            for (int i = 0; i < 4; i++) K(f3[i], $"F{i + 9}", 11 + i, 0);
            K("E0-37", "PrtSc", 15.25, 0);
            K("46", "ScrLk", 16.25, 0);
            K("PAUSE", "Pause", 17.25, 0);

            // number row
            K("29", "`", 0, 1.5);
            string[] nums = { "02", "03", "04", "05", "06", "07", "08", "09", "0A", "0B" };
            string[] numLabels = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };
            for (int i = 0; i < 10; i++) K(nums[i], numLabels[i], 1 + i, 1.5);
            K("0C", "-", 11, 1.5);
            K("0D", "=", 12, 1.5);
            K("0E", "Backspace", 13, 1.5, 2);
            K("E0-52", "Ins", 15.25, 1.5);
            K("E0-47", "Home", 16.25, 1.5);
            K("E0-49", "PgUp", 17.25, 1.5);

            // Q row
            K("0F", "Tab", 0, 2.5, 1.5);
            string q = "QWERTYUIOP";
            string[] qIds = { "10", "11", "12", "13", "14", "15", "16", "17", "18", "19" };
            for (int i = 0; i < 10; i++) K(qIds[i], q[i].ToString(), 1.5 + i, 2.5);
            K("1A", "[", 11.5, 2.5);
            K("1B", "]", 12.5, 2.5);
            K("2B", "\\", 13.5, 2.5, 1.5);
            K("E0-53", "Del", 15.25, 2.5);
            K("E0-4F", "End", 16.25, 2.5);
            K("E0-51", "PgDn", 17.25, 2.5);

            // A row
            K("3A", "Caps", 0, 3.5, 1.75);
            string a = "ASDFGHJKL";
            string[] aIds = { "1E", "1F", "20", "21", "22", "23", "24", "25", "26" };
            for (int i = 0; i < 9; i++) K(aIds[i], a[i].ToString(), 1.75 + i, 3.5);
            K("27", ";", 10.75, 3.5);
            K("28", "'", 11.75, 3.5);
            K("1C", "Enter", 12.75, 3.5, 2.25);

            // Z row
            K("2A", "Shift", 0, 4.5, 2.25);
            string z = "ZXCVBNM";
            string[] zIds = { "2C", "2D", "2E", "2F", "30", "31", "32" };
            for (int i = 0; i < 7; i++) K(zIds[i], z[i].ToString(), 2.25 + i, 4.5);
            K("33", ",", 9.25, 4.5);
            K("34", ".", 10.25, 4.5);
            K("35", "/", 11.25, 4.5);
            K("36", "Shift", 12.25, 4.5, 2.75);
            K("E0-48", "↑", 16.25, 4.5);

            // bottom row
            K("1D", "Ctrl", 0, 5.5, 1.25);
            K("E0-5B", "Win", 1.25, 5.5, 1.25);
            K("38", "Alt", 2.5, 5.5, 1.25);
            K("39", "Space", 3.75, 5.5, 6.25);
            K("E0-38", "Alt", 10, 5.5, 1.25);
            K("E0-5C", "Win", 11.25, 5.5, 1.25);
            K("E0-5D", "Menu", 12.5, 5.5, 1.25);
            K("E0-1D", "Ctrl", 13.75, 5.5, 1.25);
            K("E0-4B", "←", 15.25, 5.5);
            K("E0-50", "↓", 16.25, 5.5);
            K("E0-4D", "→", 17.25, 5.5);

            // numpad
            const double nx = 18.75;
            K("45", "Num", nx, 1.5);
            K("E0-35", "/", nx + 1, 1.5);
            K("37", "*", nx + 2, 1.5);
            K("4A", "-", nx + 3, 1.5);
            K("47", "7", nx, 2.5);
            K("48", "8", nx + 1, 2.5);
            K("49", "9", nx + 2, 2.5);
            K("4E", "+", nx + 3, 2.5, 1, 2);
            K("4B", "4", nx, 3.5);
            K("4C", "5", nx + 1, 3.5);
            K("4D", "6", nx + 2, 3.5);
            K("4F", "1", nx, 4.5);
            K("50", "2", nx + 1, 4.5);
            K("51", "3", nx + 2, 4.5);
            K("E0-1C", "Enter", nx + 3, 4.5, 1, 2);
            K("52", "0", nx, 5.5, 2);
            K("53", ".", nx + 2, 5.5);
            return keys;
        }

        /// <summary>Width and height of the whole board in key units.</summary>
        public static (double Width, double Height) LayoutSize { get; } = (22.75, 6.5);

        // =================================================================
        //  What is held
        // =================================================================

        /// <summary>Remembers which keys are down, which have ever been pressed, and the most that were down at once (the rollover).</summary>
        public sealed class RolloverTracker
        {
            private readonly HashSet<string> _held = new();
            private readonly HashSet<string> _tested = new();
            private readonly List<string> _maxSet = new();

            public IReadOnlyCollection<string> Held => _held;
            public IReadOnlyCollection<string> Tested => _tested;

            /// <summary>The keys that were down together at the busiest moment, in the order they went down.</summary>
            public IReadOnlyList<string> MaxSet => _maxSet;
            public int MaxHeld => _maxSet.Count;

            private readonly List<string> _order = new();

            /// <summary>False when the key was already down (the keyboard's auto-repeat), so it isn't counted twice.</summary>
            public bool Press(string id)
            {
                if (!_held.Add(id)) return false;
                _tested.Add(id);
                _order.Add(id);
                if (_held.Count > _maxSet.Count)
                {
                    _maxSet.Clear();
                    _maxSet.AddRange(_order.Where(_held.Contains));
                }
                return true;
            }

            public bool Release(string id)
            {
                _order.Remove(id);
                return _held.Remove(id);
            }

            /// <summary>Forgets the tested keys and the maximum (keys still down stay down).</summary>
            public void Reset()
            {
                _tested.Clear();
                _maxSet.Clear();
                foreach (var h in _held) _tested.Add(h);
                _maxSet.AddRange(_order.Where(_held.Contains));
            }
        }

        /// <summary>What the most keys held at once says about the keyboard. 0 means nothing was tried yet.</summary>
        public static string RolloverVerdictKey(int maxHeld) => maxHeld switch
        {
            0 => "RolloverNone",
            <= 3 => "RolloverFew",
            <= 6 => "Rollover6",
            < 10 => "RolloverMore",
            _ => "RolloverFull",
        };

        // =================================================================
        //  Chatter: pressed again too soon after being let go
        // =================================================================

        /// <summary>
        /// A switch that is worn or dirty can register two presses for one. This watches for a second press that follows the release by less
        /// than <see cref="ThresholdMs"/>: far faster than a finger can lift and press again.
        /// </summary>
        public sealed class ChatterDetector
        {
            private readonly Dictionary<string, double> _lastUp = new();
            private readonly Dictionary<string, int> _counts = new();
            private readonly Dictionary<string, double> _shortest = new();

            public double ThresholdMs { get; set; } = 30;

            public IReadOnlyDictionary<string, int> Counts => _counts;
            public int Total => _counts.Values.Sum();

            /// <summary>The shortest gap (ms) seen between a release and the next press of this key or button, or null.</summary>
            public double? ShortestGapMs(string id) => _shortest.TryGetValue(id, out var v) ? v : null;

            public void Release(string id, double time) => _lastUp[id] = time;

            /// <summary>Returns the event when this press is chatter.</summary>
            public ChatterEvent? Press(string id, double time)
            {
                if (!_lastUp.TryGetValue(id, out double up)) return null;
                double gap = (time - up) * 1000;
                if (gap < 0) return null;
                if (!_shortest.TryGetValue(id, out double s) || gap < s) _shortest[id] = gap;
                if (gap >= ThresholdMs) return null;
                _counts[id] = _counts.GetValueOrDefault(id) + 1;
                return new ChatterEvent(id, gap);
            }

            public void Reset()
            {
                _lastUp.Clear();
                _counts.Clear();
                _shortest.Clear();
            }
        }

        // =================================================================
        //  Mouse: polling rate
        // =================================================================

        public const int HistogramBuckets = 12;

        /// <summary>Upper edge (ms) of each histogram bucket; the last one also takes everything slower.</summary>
        public static readonly double[] HistogramEdgesMs = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2, 3, 4, 6, 8, 12 };

        /// <summary>
        /// From the arrival times (seconds, in order) of the mouse's movement reports. Gaps longer than 50 ms are the hand
        /// stopping, not the mouse, and are left out. The rate comes from the middle interval, so a few late reports don't drag it down;
        /// "late" counts reports that took more than twice the usual time. Null with too few reports.
        /// </summary>
        public static PollingResult? AnalyzePolling(IReadOnlyList<double> times)
        {
            if (times.Count < 30) return null;

            var gaps = new List<double>(times.Count);
            for (int i = 1; i < times.Count; i++)
            {
                double g = times[i] - times[i - 1];
                if (g > 0 && g < 0.050) gaps.Add(g);
            }
            if (gaps.Count < 20) return null;

            var sorted = gaps.OrderBy(v => v).ToList();
            double median = sorted[sorted.Count / 2];
            double mean = gaps.Average();
            double variance = gaps.Sum(v => (v - mean) * (v - mean)) / gaps.Count;
            int late = gaps.Count(v => v > median * 2);

            var histogram = new int[HistogramBuckets];
            foreach (var g in gaps)
            {
                double ms = g * 1000;
                int bucket = Array.FindIndex(HistogramEdgesMs, e => ms <= e + 1e-6);   // a tiny tolerance: 1.0000000000000009 ms is 1 ms
                histogram[bucket < 0 ? HistogramBuckets - 1 : bucket]++;
            }

            return new PollingResult(
                Hz: 1.0 / median,
                MedianMs: median * 1000,
                MinMs: sorted[0] * 1000,
                MaxMs: sorted[^1] * 1000,
                JitterMs: Math.Sqrt(variance) * 1000,
                LatePercent: late * 100.0 / gaps.Count,
                Reports: gaps.Count,
                Histogram: histogram);
        }

        /// <summary>The usual speeds mice report at (so "993 Hz" reads as "about 1000 Hz").</summary>
        public static int NearestStandardRate(double hz)
        {
            int[] standard = { 125, 250, 500, 1000, 2000, 4000, 8000 };
            return standard.OrderBy(s => Math.Abs(s - hz)).First();
        }

        // =================================================================
        //  Mouse: sensitivity and wheel
        // =================================================================

        /// <summary>
        /// Counts per inch from a measured move: the mouse's real DPI. <paramref name="counts"/> is how far the mouse reported
        /// (raw, so Windows' pointer speed and acceleration don't matter) for a move of <paramref name="centimetres"/>.
        /// </summary>
        public static double MeasuredDpi(double counts, double centimetres) =>
            centimetres <= 0 ? 0 : counts / (centimetres / 2.54);

        /// <summary>The distance the report travelled, in counts (the straight line between where it started and where it ended).</summary>
        public static double NetDistance(long sumDx, long sumDy) => Math.Sqrt((double)sumDx * sumDx + (double)sumDy * sumDy);

        /// <summary>The usual sensor settings, to say what a measured value is closest to.</summary>
        public static int NearestCommonDpi(double dpi)
        {
            int[] common = { 400, 500, 800, 1000, 1200, 1600, 2000, 2400, 3200, 4000, 4800, 6400, 8000, 12000, 16000 };
            return common.OrderBy(c => Math.Abs(c - dpi)).First();
        }

        /// <summary>
        /// Notches that turn the wrong way in the middle of a scroll are a sign of a dirty or worn wheel sensor.
        /// Call with every wheel report; a reversal is a notch against the direction of the one before it, within 150 ms.
        /// </summary>
        public sealed class WheelAnalyzer
        {
            private int _lastSign;
            private double _lastTime = double.NegativeInfinity;

            public int Up { get; private set; }
            public int Down { get; private set; }
            public int Reversals { get; private set; }

            /// <summary>Notches in one report (a normal wheel gives +/-120 per notch).</summary>
            public bool Add(int delta, double time)
            {
                if (delta == 0) return false;
                int sign = Math.Sign(delta);
                int notches = Math.Max(1, Math.Abs(delta) / 120);
                if (sign > 0) Up += notches; else Down += notches;

                bool reversed = _lastSign != 0 && sign != _lastSign && time - _lastTime < 0.150;
                if (reversed) Reversals++;
                _lastSign = sign;
                _lastTime = time;
                return reversed;
            }

            public void Reset()
            {
                Up = Down = Reversals = 0;
                _lastSign = 0;
                _lastTime = double.NegativeInfinity;
            }
        }

        // =================================================================
        //  Mouse: movement indicator (circle + chevron)
        // =================================================================

        /// <summary>
        /// Turns the mouse's counts into what a NohBoard-style movement indicator draws: a smoothed velocity, the direction it points
        /// in, how far from the centre of the ring the puck sits (0..1) and a short trail of where it has been. Feed it every
        /// frame with the counts moved since the last one; with no movement the puck eases back to the centre and the direction is kept.
        /// </summary>
        public sealed class MotionTracker
        {
            public const int TrailLength = 28;
            private const double SmoothingSeconds = 0.06;
            private const double MovingThreshold = 25;   // counts per second

            private readonly Queue<(double X, double Y)> _trail = new();

            /// <summary>Counts per second that put the puck on the edge of the ring.</summary>
            public double FullScale { get; set; } = 3000;

            public double VelocityX { get; private set; }
            public double VelocityY { get; private set; }

            /// <summary>Counts per second.</summary>
            public double Speed => Math.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY);

            public bool Moving => Speed > MovingThreshold;

            /// <summary>Degrees, 0 = right, 90 = down (screen axes). Holds the last direction once the mouse stops.</summary>
            public double Angle { get; private set; }

            /// <summary>Distance of the puck from the centre as a share of the ring radius, 0..1.</summary>
            public double Deflection => Math.Min(1, Math.Pow(Math.Min(1, Speed / Math.Max(1, FullScale)), 0.6));

            /// <summary>The puck in ring units (-1..1 on both axes).</summary>
            public (double X, double Y) Puck
            {
                get
                {
                    double rad = Angle * Math.PI / 180;
                    double d = Moving ? Deflection : Deflection * Math.Clamp(Speed / MovingThreshold, 0, 1);
                    return (Math.Cos(rad) * d, Math.Sin(rad) * d);
                }
            }

            public IReadOnlyCollection<(double X, double Y)> Trail => _trail;

            /// <summary>Counts moved over the last <paramref name="seconds"/> (clamped to something sensible).</summary>
            public void Step(long dx, long dy, double seconds)
            {
                double dt = Math.Clamp(seconds, 0.001, 0.1);
                double alpha = 1 - Math.Exp(-dt / SmoothingSeconds);
                VelocityX += (dx / dt - VelocityX) * alpha;
                VelocityY += (dy / dt - VelocityY) * alpha;
                if (Speed > MovingThreshold) Angle = (Math.Atan2(VelocityY, VelocityX) * 180 / Math.PI + 360) % 360;

                _trail.Enqueue(Puck);
                while (_trail.Count > TrailLength) _trail.Dequeue();
            }

            public void Reset()
            {
                VelocityX = VelocityY = 0;
                _trail.Clear();
            }
        }

        private static readonly string[] CompassArrows = { "→", "↘", "↓", "↙", "←", "↖", "↑", "↗" };

        /// <summary>One of eight arrows for a direction in degrees (0 = right, clockwise).</summary>
        public static string CompassArrow(double angleDegrees) =>
            CompassArrows[(int)Math.Round(((angleDegrees % 360) + 360) % 360 / 45) % 8];

        // =================================================================
        //  Reaction time
        // =================================================================

        /// <summary>How long to wait before the signal, between 1.2 and 4 seconds, from a number in 0..1.</summary>
        public static double ReactionDelaySeconds(double random01) => 1.2 + Math.Clamp(random01, 0, 1) * 2.8;

        public sealed record ReactionSummary(double Average, double Best, double Median, double Worst, int Count);

        public static ReactionSummary? SummarizeReactions(IReadOnlyList<ReactionResult> results)
        {
            if (results.Count == 0) return null;
            var ms = results.Select(r => r.Milliseconds).OrderBy(v => v).ToList();
            return new ReactionSummary(ms.Average(), ms[0], ms[ms.Count / 2], ms[^1], ms.Count);
        }

        // =================================================================
        //  Labels
        // =================================================================

        public static string LabelOf(string keyId) => Layout.FirstOrDefault(k => k.Id == keyId)?.Label ?? $"({keyId})";

        public static string FullLabelOf(string keyId) => keyId switch
        {
            "2A" => "Left Shift", "36" => "Right Shift", "1D" => "Left Ctrl", "E0-1D" => "Right Ctrl",
            "38" => "Left Alt", "E0-38" => "Right Alt", "E0-5B" => "Left Win", "E0-5C" => "Right Win",
            "E0-1C" => "Numpad Enter", "1C" => "Enter", "45" => "Num Lock", "3A" => "Caps Lock",
            _ => LabelOf(keyId),
        };

        public static string ButtonName(MouseButtonKind b) => b switch
        {
            MouseButtonKind.Left => "Left", MouseButtonKind.Right => "Right", MouseButtonKind.Middle => "Middle",
            MouseButtonKind.Back => "Back (4)", _ => "Forward (5)",
        };

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "keyboard_mouse_test.json");

        public static KeyboardMouseTestSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new KeyboardMouseTestSettings();
                var s = JsonSerializer.Deserialize<KeyboardMouseTestSettings>(File.ReadAllText(file), JsonOptions) ?? new KeyboardMouseTestSettings();
                s.ChatterMs = Math.Clamp(s.ChatterMs, 5, 100);
                s.DpiDistanceCm = Math.Clamp(s.DpiDistanceCm, 2, 40);
                s.MotionRange = Math.Clamp(s.MotionRange, 500, 10000);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new KeyboardMouseTestSettings();
            }
        }

        public static void SaveSettings(KeyboardMouseTestSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);
    }
}
