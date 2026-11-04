using Microsoft.Win32;
using System.Globalization;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Colours and words for the small windows that show BEFORE the app's own theme and language are loaded
    /// (the Install / Run window and the copy-progress window). They follow the Windows light / dark setting and
    /// the Windows display language instead of the app's own.
    /// </summary>
    internal static class WindowsPalette
    {
        public static bool IsDark { get; } = ReadIsDark();

        public static string Background => IsDark ? "#1F2328" : "#F6F7F9";
        public static string Card => IsDark ? "#2A2F36" : "#FFFFFF";
        public static string CardHover => IsDark ? "#323943" : "#F0F9FF";
        public static string Line => IsDark ? "#3A414A" : "#D9DDE3";
        public static string Text => IsDark ? "#F3F4F6" : "#1F2937";
        public static string Muted => IsDark ? "#9AA3AE" : "#6B7280";
        public static string Accent => IsDark ? "#38BDF8" : "#0EA5E9";
        public static string Error => IsDark ? "#F87171" : "#DC2626";

        public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

        public static bool IsSpanish { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase);

        /// <summary>English or Spanish, by the Windows display language.</summary>
        public static string T(string english, string spanish) => IsSpanish ? spanish : english;

        private static bool ReadIsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
