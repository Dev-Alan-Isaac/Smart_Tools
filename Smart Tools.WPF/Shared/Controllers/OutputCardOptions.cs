using Smart_Tools.WPF.Controls;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Builds the controls a tool puts in <see cref="OutputFolderCard.Options"/>
    /// (File name, a hint line, "Save without asking"...). They're made in code
    /// because nothing inside the card's Options may carry an x:Name (MC3093);
    /// the view keeps them in fields with the names its code already uses.
    /// </summary>
    public static class OutputCardOptions
    {
        /// <summary>A small bold label ("File name"), the shared FieldLabel style.</summary>
        public static TextBlock Label(string text, double top = 10)
        {
            var label = new TextBlock { Text = text };
            label.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");
            label.Margin = new Thickness(0, top, 0, 4);
            return label;
        }

        /// <summary>A text box; <paramref name="placeholder"/> shows while it's empty (the theme's Tag placeholder).</summary>
        public static TextBox TextBox(string placeholder, string? tip, int maxLength = 0) =>
            new() { Tag = placeholder, ToolTip = tip, MaxLength = maxLength };

        /// <summary>A muted, wrapping hint line under a field.</summary>
        public static TextBlock Hint(double top = 6)
        {
            var hint = new TextBlock { Margin = new Thickness(0, top, 0, 0), FontSize = 10.5, TextWrapping = TextWrapping.Wrap };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextMuted");
            return hint;
        }

        public static CheckBox CheckBox(string text, bool isChecked, object? tip)
        {
            var box = new CheckBox { Content = text, IsChecked = isChecked, ToolTip = tip, Margin = new Thickness(0, 12, 0, 0) };
            box.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "SidebarText");
            return box;
        }

        public static StackPanel Panel(params UIElement[] children)
        {
            var panel = new StackPanel();
            foreach (var child in children) panel.Children.Add(child);
            return panel;
        }
    }
}
