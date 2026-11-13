using MaterialDesignThemes.Wpf;
using System.Windows;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The icon of a toggle choice. Put <c>controls:ChoiceIcon.Kind="Fingerprint"</c> on a RadioButton (ModeToggleButtonStyle /
    /// ModeToggleTallStyle / ModeTabStyle) or on a CheckBox (ToggleChipCheckBoxStyle) instead of wrapping its content in a StackPanel.
    /// The styles draw it beside the label (tall style: above it) and collapse it when none is set, so there is never an empty gap.
    /// A chip checkbox shows the icon while unchecked and a tick while checked.
    /// </summary>
    public static class ChoiceIcon
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
            "Kind", typeof(PackIconKind?), typeof(ChoiceIcon), new FrameworkPropertyMetadata(null));

        public static PackIconKind? GetKind(DependencyObject o) => (PackIconKind?)o.GetValue(KindProperty);
        public static void SetKind(DependencyObject o, PackIconKind? value) => o.SetValue(KindProperty, value);

        /// <summary>Set by ModeToggleTallStyle: the icon sits above the label instead of beside it.</summary>
        public static readonly DependencyProperty StackedProperty = DependencyProperty.RegisterAttached(
            "Stacked", typeof(bool), typeof(ChoiceIcon), new FrameworkPropertyMetadata(false));

        public static bool GetStacked(DependencyObject o) => (bool)o.GetValue(StackedProperty);
        public static void SetStacked(DependencyObject o, bool value) => o.SetValue(StackedProperty, value);
    }
}
