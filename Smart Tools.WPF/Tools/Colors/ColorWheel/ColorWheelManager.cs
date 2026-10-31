using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind ColorWheelView: Itten's 12-part artist's wheel,
    /// color harmonies measured on that wheel, and two-color gradients.
    ///
    /// This file is pure logic (unit-testable, no WPF). Drawing the wheel
    /// and gradient strips to bitmaps lives in ColorWheelManager.Render.cs.
    ///
    /// Why an RYB mapping: Itten's wheel is a painter's wheel - red, yellow
    /// and blue are its primaries, 120 degrees apart, with green opposite
    /// red. On a screen's RGB hue circle, yellow sits at 60 and green at
    /// 120, so placing the painter's names at even 30-degree RGB steps
    /// (as before) labelled pure green "Yellow" and cyan "Green". Angles on
    /// the artist's wheel are now converted to RGB hues through the
    /// standard piecewise mapping below, so every wedge shows the color
    /// its name says, and "opposite" means what a painter expects.
    /// </summary>
    public static partial class ColorWheelManager
    {
        // Name, tier - in clock order from Red at 12 o'clock, 30 degrees apart.
        private static readonly (string Name, WheelTier Tier)[] WheelLayout =
        {
            ("Red", WheelTier.Primary),
            ("Red-Orange", WheelTier.Tertiary),
            ("Orange", WheelTier.Secondary),
            ("Yellow-Orange", WheelTier.Tertiary),
            ("Yellow", WheelTier.Primary),
            ("Yellow-Green", WheelTier.Tertiary),
            ("Green", WheelTier.Secondary),
            ("Blue-Green", WheelTier.Tertiary),
            ("Blue", WheelTier.Primary),
            ("Blue-Violet", WheelTier.Tertiary),
            ("Violet", WheelTier.Secondary),
            ("Red-Violet", WheelTier.Tertiary),
        };

        // Artist's-wheel angle (index * 30) -> RGB/HSV hue. Monotonic, so it
        // can be inverted for harmonies. Last entry closes the circle.
        private static readonly double[] RybToRgbHueTable =
        {
            0,   // Red
            20,  // Red-Orange
            35,  // Orange
            45,  // Yellow-Orange
            60,  // Yellow
            90,  // Yellow-Green
            120, // Green
            180, // Blue-Green
            240, // Blue
            260, // Blue-Violet
            280, // Violet
            320, // Red-Violet
            360, // Red again
        };

        public const int WedgeCount = 12;
        public const double RingInnerFraction = 0.62;

        // Indices of the hexagon corners (primaries + secondaries).
        internal const int RedIndex = 0, OrangeIndex = 2, YellowIndex = 4, GreenIndex = 6, BlueIndex = 8, VioletIndex = 10;

        // =================================================================
        //  Wheel
        // =================================================================

        /// <summary>The 12 wedges in clock order from Red. Lower saturation/value for a softer or darker wheel.</summary>
        public static List<WheelSwatch> GetIttenWheel(double saturation = 1.0, double value = 1.0)
        {
            var swatches = new List<WheelSwatch>(WedgeCount);
            for (int i = 0; i < WedgeCount; i++)
            {
                var (name, tier) = WheelLayout[i];
                swatches.Add(new WheelSwatch
                {
                    Index = i,
                    Name = name,
                    Tier = tier,
                    WheelAngle = i * 30,
                    Color = ColorManager.HsvToRgb(RybToRgbHue(i * 30), saturation, value),
                });
            }
            return swatches;
        }

        /// <summary>Artist's-wheel angle (0 = red, 120 = yellow, 240 = blue) to an RGB/HSV hue.</summary>
        public static double RybToRgbHue(double rybAngle)
        {
            double a = ColorManager.NormalizeHue(rybAngle);
            int i = (int)(a / 30);
            double t = (a - i * 30) / 30;
            return ColorManager.NormalizeHue(RybToRgbHueTable[i] + (RybToRgbHueTable[i + 1] - RybToRgbHueTable[i]) * t);
        }

        /// <summary>Inverse of <see cref="RybToRgbHue"/>.</summary>
        public static double RgbHueToRyb(double rgbHue)
        {
            double h = ColorManager.NormalizeHue(rgbHue);
            for (int i = 0; i < WedgeCount; i++)
            {
                double lo = RybToRgbHueTable[i], hi = RybToRgbHueTable[i + 1];
                if (h >= lo && h < hi)
                    return ColorManager.NormalizeHue(i * 30 + (h - lo) / (hi - lo) * 30);
            }
            return 0;
        }

        /// <summary>
        /// Which outer wedge is under a point of a square wheel image
        /// (<paramref name="x"/>, <paramref name="y"/> in the same units as
        /// <paramref name="size"/>). Inside the hexagon, returns the nearest
        /// primary/secondary corner; -1 for the neutral centre or outside.
        /// </summary>
        public static int SwatchIndexAt(double x, double y, double size)
        {
            double r = size / 2;
            double dx = x - r, dy = y - r;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > r || dist < r * RingInnerFraction * 0.3) return -1;

            // Clockwise angle from 12 o'clock.
            double angle = ColorManager.NormalizeHue(Math.Atan2(dx, -dy) * 180 / Math.PI);

            if (dist >= r * RingInnerFraction)
                return (int)Math.Round(angle / 30) % WedgeCount;

            return (int)Math.Round(angle / 60) % 6 * 2; // hexagon: primaries & secondaries only
        }

        // =================================================================
        //  Harmonies
        // =================================================================

        public static string Describe(ColorHarmony harmony) => harmony switch
        {
            ColorHarmony.Complementary => "Opposite on the wheel - maximum contrast.",
            ColorHarmony.Analogous => "Neighbours on the wheel - calm and cohesive.",
            ColorHarmony.Triadic => "Three evenly spaced colors - lively but balanced.",
            ColorHarmony.SplitComplementary => "A color plus the two beside its opposite - contrast with less tension.",
            ColorHarmony.Tetradic => "Four colors in a square - rich; let one dominate.",
            _ => "",
        };

        /// <summary>Artist's-wheel offsets for each harmony (the base color is always first).</summary>
        public static double[] OffsetsFor(ColorHarmony harmony) => harmony switch
        {
            ColorHarmony.Complementary => new[] { 0.0, 180 },
            ColorHarmony.Analogous => new[] { 0.0, -30, 30 },
            ColorHarmony.Triadic => new[] { 0.0, 120, 240 },
            ColorHarmony.SplitComplementary => new[] { 0.0, 150, 210 },
            ColorHarmony.Tetradic => new[] { 0.0, 90, 180, 270 },
            _ => new[] { 0.0 },
        };

        /// <summary>
        /// Harmony for any color: rotates it on the artist's wheel, keeping
        /// its saturation and brightness. The base color is returned exactly.
        /// </summary>
        public static List<RgbColor> GenerateHarmony(RgbColor baseColor, ColorHarmony harmony)
        {
            var hsv = ColorManager.RgbToHsv(baseColor);
            double ryb = RgbHueToRyb(hsv.H);

            var result = new List<RgbColor>();
            foreach (double offset in OffsetsFor(harmony))
            {
                result.Add(offset == 0
                    ? baseColor
                    : ColorManager.HsvToRgb(RybToRgbHue(ryb + offset), hsv.S, hsv.V));
            }
            return result;
        }

        // =================================================================
        //  Gradients
        // =================================================================

        public static List<RgbColor> GenerateGradient(RgbColor from, RgbColor to, int steps, GradientBlend blend) =>
            blend == GradientBlend.Rgb ? GenerateRgbGradient(from, to, steps) : GenerateHslGradient(from, to, steps);

        /// <summary>Straight line through RGB; <paramref name="steps"/> colors including both ends (min 2).</summary>
        public static List<RgbColor> GenerateRgbGradient(RgbColor from, RgbColor to, int steps)
        {
            steps = Math.Max(2, steps);
            var result = new List<RgbColor>(steps);
            for (int i = 0; i < steps; i++)
            {
                double t = i / (double)(steps - 1);
                result.Add(new RgbColor(Lerp(from.R, to.R, t), Lerp(from.G, to.G, t), Lerp(from.B, to.B, t)));
            }
            return result;
        }

        /// <summary>
        /// Through HSL, the short way round the hue wheel - stays vivid
        /// where an RGB blend would pass through grey. A grey/black/white end
        /// has no real hue, so it borrows the other end's (before, a white ->
        /// blue gradient travelled through reds and magentas, because grey's
        /// hue reads as 0 = red).
        /// </summary>
        public static List<RgbColor> GenerateHslGradient(RgbColor from, RgbColor to, int steps)
        {
            steps = Math.Max(2, steps);
            var a = ColorManager.RgbToHsl(from);
            var b = ColorManager.RgbToHsl(to);

            const double Achromatic = 0.5; // % saturation, or at pure black / white
            bool aGrey = a.S < Achromatic || a.L <= 0 || a.L >= 100;
            bool bGrey = b.S < Achromatic || b.L <= 0 || b.L >= 100;
            if (aGrey && !bGrey) a = a with { H = b.H };
            if (bGrey && !aGrey) b = b with { H = a.H };

            double hueDelta = b.H - a.H;
            if (Math.Abs(hueDelta) > 180) hueDelta += hueDelta > 0 ? -360 : 360;

            var result = new List<RgbColor>(steps);
            for (int i = 0; i < steps; i++)
            {
                double t = i / (double)(steps - 1);
                if (i == 0) { result.Add(from); continue; }
                if (i == steps - 1) { result.Add(to); continue; }

                result.Add(ColorManager.HslToRgb(new HslColor(
                    a.H + hueDelta * t,
                    a.S + (b.S - a.S) * t,
                    a.L + (b.L - a.L) * t)));
            }
            return result;
        }

        private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

        /// <summary>CSS linear-gradient() for the given stops, evenly spaced.</summary>
        public static string ToCssGradient(IReadOnlyList<RgbColor> colors)
        {
            if (colors.Count == 0) return "";
            if (colors.Count == 1) return colors[0].Hex;
            return "linear-gradient(90deg, " + string.Join(", ", colors.Select(c => c.Hex)) + ")";
        }
    }
}
