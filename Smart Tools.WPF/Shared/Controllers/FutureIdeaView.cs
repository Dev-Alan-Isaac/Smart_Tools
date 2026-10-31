using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Temporary page shared by every tool in the "Future Ideas" section:
    /// says the tool is planned, not built yet. Delete this file and the
    /// section's catalog entries as each idea is implemented (see FUTURE_IDEAS.md).
    /// </summary>
    public sealed class FutureIdeaView : UserControl
    {
        public FutureIdeaView(ToolInfo tool)
        {
            var badge = new ContentControl
            {
                Style = (Style)FindResource("EmptyStateBadge"),
                Tag = PackIconKind.LightbulbOutline,
            };

            var title = new TextBlock
            {
                Text = "Coming soon",
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            var body = new TextBlock
            {
                Text = $"{tool.HeaderTitle} is planned but not built yet.\n{tool.Description}.\nSee FUTURE_IDEAS.md for the full plan.",
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(badge);
            stack.Children.Add(title);
            stack.Children.Add(body);

            Margin = new Thickness(16, 8, 16, 16);
            Content = stack;
        }
    }
}
