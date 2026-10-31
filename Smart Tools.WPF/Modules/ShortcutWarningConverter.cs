using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.Windows.Data;
using Application = System.Windows.Application;

namespace Smart_Tools.WPF.Modules
{
    /// <summary>Turns a shortcut's warning code ("clash:Title" / "taken:") into a sentence in the current language.</summary>
    public sealed class ShortcutWarningConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string code || code.Length == 0) return "";

            if (code.StartsWith(ShortcutManager.ClashMark, StringComparison.Ordinal))
            {
                string format = Application.Current?.TryFindResource("Str_Shortcuts_Clash") as string ?? "Also used by: {0}";
                return string.Format(format, code[ShortcutManager.ClashMark.Length..]);
            }
            return Application.Current?.TryFindResource("Str_Shortcuts_Taken") as string
                   ?? "Another program already uses this key.";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
