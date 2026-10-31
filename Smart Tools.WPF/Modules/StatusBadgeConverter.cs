using System.Globalization;
using System.Windows.Data;
using Brushes = System.Windows.Media.Brushes;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Maps a ConvertFileItem.Status string ("Ready", "Converted",
    /// an "Unsupported: ..." reason, or an "Error: ..." message) to the
    /// matching semantic status brush/short label for the DataGrid's status
    /// badge column. Reuses the same StatusSuccess/Info/Warning/ErrorBg/Fg
    /// brush pairs Light.xaml/Dark.xaml already define for pills elsewhere,
    /// so the badge stays legible across themes without any hardcoded colors
    /// here. ConverterParameter picks what to produce: "Bg"/"Fg" for the
    /// brush, anything else (or omitted) for the short badge label.
    ///
    /// Kept in its own file rather than alongside FileConverterView in
    /// FileConverterView.xaml.cs - a converter referenced from that view's
    /// own XAML via a local: namespace can otherwise get flagged by the
    /// XAML designer/build ("the name X does not exist in the namespace")
    /// even though it compiles fine, because the design-time build doesn't
    /// always pick up a second public type sharing a code-behind file with
    /// the x:Class partial class. Splitting it out avoids that.</summary>
    public sealed class StatusBadgeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string status = value as string ?? "";

            string kind = status switch
            {
                "Converted" => "Success",
                _ when status.StartsWith("Error", StringComparison.Ordinal) => "Error",
                _ when status.StartsWith("Unsupported", StringComparison.Ordinal) => "Warning",
                _ => "Info" // "Ready"
            };

            string mode = parameter as string ?? "Label";

            return mode switch
            {
                "Bg" => System.Windows.Application.Current.TryFindResource($"Status{kind}Bg") ?? Brushes.Transparent,
                "Fg" => System.Windows.Application.Current.TryFindResource($"Status{kind}Fg") ?? Brushes.Black,
                _ => kind switch
                {
                    "Success" => "CONVERTED",
                    "Error" => "ERROR",
                    "Warning" => "UNSUPPORTED",
                    _ => "READY"
                }
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
