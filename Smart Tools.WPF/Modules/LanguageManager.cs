using System.Windows;
using Application = System.Windows.Application;

namespace Smart_Tools.WPF.Modules
{
    /// <summary>
    /// Swaps the app's active language at runtime, the same way ThemeManager
    /// swaps Light.xaml/Dark.xaml: find the currently-merged Strings
    /// dictionary (if any), remove it, and merge in the requested one
    /// instead. Every Text="{DynamicResource Str_...}" in the views picks
    /// up the change immediately - no restart, no rebuild.
    /// </summary>
    public static class LanguageManager
    {
        /// <summary>
        /// Supported language codes, in the order they should appear in
        /// any language picker. Add a new entry here + a matching
        /// Language/{code}.xaml file (with the exact same x:Key set as
        /// Language/en.xaml) to add a language - nothing else to touch.
        /// </summary>
        public static readonly (string Code, string DisplayName)[] Supported =
        {
            ("en", "English"),
            ("es", "Español"),
        };

        private const string FallbackCode = "en";

        /// <summary>Raised after a language was applied, so text built in code (the sidebar, breadcrumbs, Home) can refresh.</summary>
        public static event Action? Changed;

        /// <summary>
        /// The active language dictionary is found by filename against the
        /// known language codes, not by folder: that keeps it from ever
        /// matching (and removing) the active theme dictionary.
        /// </summary>
        private static string FileNameOf(Uri uri) =>
            uri.OriginalString.Split('/', '\\').Last();

        public static void Apply(string languageCode)
        {
            string code = Supported.Any(l => l.Code == languageCode)
                ? languageCode
                : FallbackCode;

            var appResources = Application.Current.Resources.MergedDictionaries;

            var existing = appResources.FirstOrDefault(d =>
                d.Source != null &&
                Supported.Any(l => string.Equals(FileNameOf(d.Source), $"{l.Code}.xaml", StringComparison.OrdinalIgnoreCase)));

            if (existing != null)
                appResources.Remove(existing);

            var replacement = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Language/{code}.xaml", UriKind.Absolute)
            };

            appResources.Add(replacement);

            // Also drives number/date formatting for anything that reads
            // CultureInfo.CurrentUICulture instead of a DynamicResource.
            var culture = new System.Globalization.CultureInfo(code);
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            // Tasks and background threads start from the OS culture otherwise; give them the UI language too.
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;

            Changed?.Invoke();
        }
    }
}