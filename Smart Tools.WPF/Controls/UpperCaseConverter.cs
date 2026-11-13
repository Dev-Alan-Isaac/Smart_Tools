using System.Globalization;
using System.Windows.Data;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>Shows a string in capitals (WPF has no text-transform). Used by <c>SidebarGroupHeader</c> so every card title is ALL CAPS whatever the language file says.</summary>
    public sealed class UpperCaseConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
            => value is string s ? s.ToUpper(culture) : value;

        public object? ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
