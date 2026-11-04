using System.Globalization;

// Root namespace on purpose (like Log): every view can call Loc.T / Loc.F with no extra using.
namespace Smart_Tools.WPF
{
    /// <summary>Looks up a UI string from the active language dictionary (en.xaml / es.xaml), with an English fallback.</summary>
    public static class Loc
    {
        /// <summary>The text for <paramref name="key"/> in the current language, or <paramref name="fallback"/> if the key is missing.</summary>
        public static string T(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Singular/plural: uses the "one" text when <paramref name="count"/> is 1, otherwise the "many" text, then fills {0}, {1}... with <paramref name="args"/>.</summary>
        public static string N(int count, string keyOne, string fallbackOne, string keyMany, string fallbackMany, params object?[] args) =>
            count == 1 ? F(keyOne, fallbackOne, args) : F(keyMany, fallbackMany, args);

        /// <summary>Like <see cref="T"/>, then fills {0}, {1}... with <paramref name="args"/>.</summary>
        public static string F(string key, string fallback, params object?[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, T(key, fallback), args); }
            catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, fallback, args); }
        }
    }
}
