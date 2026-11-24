using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Numbers typed into a box: either a comma or a dot for the decimals, whatever the Windows regional setting says.</summary>
    public static class NumberInput
    {
        public static bool TryParse(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string clean = text.Trim();
            if (!clean.Contains('.')) clean = clean.Replace(',', '.');
            if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return false;
            if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
            value = parsed;
            return true;
        }

        /// <summary>"1200", "12.5", "0.33" - at most two decimals, always a dot.</summary>
        public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
