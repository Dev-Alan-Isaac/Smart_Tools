using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The Input and Output cards can sit in a tool's options panel (the old place) or in its content area (the standard
    /// page since the 2026-11-27 restyle). This gives them the look of where they are: in the options panel a settings
    /// group (caps heading, divider under it), in the content area a plain block with the same heading as the cards
    /// beside it. Called from each card's Loaded, so moving a card in a view's XAML is all a tool needs.
    /// </summary>
    public static class CardPlacement
    {
        /// <summary>True when the element is inside a <see cref="ToolSidebar"/> or a hand-made options panel (SidebarPanelStyle).</summary>
        public static bool InOptionsPanel(DependencyObject element)
        {
            object? panelStyle = System.Windows.Application.Current?.TryFindResource("SidebarPanelStyle");
            for (DependencyObject? d = VisualTreeHelper.GetParent(element); d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is ToolSidebar) return true;
                if (d is Border border && panelStyle != null && ReferenceEquals(border.Style, panelStyle)) return true;
            }
            return false;
        }

        /// <summary>Styles the card's outer border and its heading for where the card is placed.</summary>
        public static void Apply(UserControl card, ContentControl header)
        {
            bool inOptions = InOptionsPanel(card);
            header.SetResourceReference(FrameworkElement.StyleProperty, inOptions ? "SidebarGroupHeader" : "CardHeader");
            header.Margin = new Thickness(0, 0, 0, inOptions ? 10 : 12);
            if (card.Content is not Border outer) return;
            if (inOptions)
            {
                outer.SetResourceReference(FrameworkElement.StyleProperty, "SidebarGroupCardStyle");
                return;
            }
            outer.Style = null;
            outer.Background = System.Windows.Media.Brushes.Transparent;
            outer.BorderThickness = new Thickness(0);
            outer.Padding = new Thickness(0);
            outer.Margin = new Thickness(0);
        }
    }
}
