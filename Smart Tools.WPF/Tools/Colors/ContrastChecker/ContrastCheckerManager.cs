using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One line of the WCAG table: what is being checked, the ratio it needs, and whether the pair passes.</summary>
    public sealed record ContrastCheck(string Label, string Needs, bool Pass);

    public sealed class ContrastCheckerSettings
    {
        public string Foreground { get; set; } = "#1F2937";
        public string Background { get; set; } = "#FFFFFF";
    }

    /// <summary>
    /// Contrast Checker logic: WCAG 2.x ratio for any two colors and a nearest-fix finder. The maths (luminance, ratio,
    /// colour parsing) is <see cref="ColorManager"/>'s; this adds the pass/fail table and the suggestion. No WPF types.
    /// </summary>
    public static class ContrastCheckerManager
    {
        public const string SettingsName = "contrast_checker";

        /// <summary>The ratio and the WCAG 2.x table (AA / AAA for normal and large text, 3:1 for interface parts).</summary>
        public static (double Ratio, IReadOnlyList<ContrastCheck> Checks) Evaluate(RgbColor foreground, RgbColor background)
        {
            double ratio = ColorManager.Contrast(foreground, background).Ratio;
            var checks = new List<ContrastCheck>
            {
                new("Normal text - AA", "4.5 : 1", ratio >= 4.5),
                new("Normal text - AAA", "7 : 1", ratio >= 7),
                new("Large text - AA", "3 : 1", ratio >= 3),
                new("Large text - AAA", "4.5 : 1", ratio >= 4.5),
                new("Icons, borders, inputs (AA)", "3 : 1", ratio >= 3),
            };
            return (ratio, checks);
        }

        /// <summary>
        /// The closest text color to <paramref name="foreground"/> (same hue and saturation, only lighter or darker) that
        /// reaches <paramref name="target"/> against the background. Null when even black or white can't.
        /// </summary>
        public static RgbColor? SuggestForeground(RgbColor foreground, RgbColor background, double target)
        {
            if (ColorManager.Contrast(foreground, background).Ratio >= target) return foreground;

            var hsl = ColorManager.RgbToHsl(foreground);
            bool bgIsLight = ColorManager.RelativeLuminance(background) > 0.179;   // whichever of black/white contrasts more
            RgbColor? best = null;
            // Walk from the current lightness toward black (on a light background) or white (on a dark one).
            for (double l = hsl.L; bgIsLight ? l >= 0 : l <= 100; l += bgIsLight ? -0.5 : 0.5)
            {
                var candidate = ColorManager.HslToRgb(hsl with { L = l });
                if (ColorManager.Contrast(candidate, background).Ratio >= target) { best = candidate; break; }
            }
            if (best != null) return best;

            // Same hue can't do it (e.g. a mid background): fall back to pure black or white if that works.
            var fallback = bgIsLight ? ColorManager.Black : ColorManager.White;
            return ColorManager.Contrast(fallback, background).Ratio >= target ? fallback : null;
        }

        public static string FormatRatio(double ratio) => ratio.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " : 1";
    }
}
