using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// "What's new": the patch notes of the version that was just installed (see <see cref="ReleaseNotes"/>), as an in-window modal.
    /// </summary>
    internal static class WhatsNewDialog
    {
        public static void Show(IReadOnlyList<ReleaseEntry> entries)
        {
            AppModal.Present(null, close =>
            {
                var root = new DockPanel();

                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
                var icon = new PackIcon { Kind = PackIconKind.PartyPopper, Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
                icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentColor");
                head.Children.Add(icon);
                var heading = AppModal.Heading(AppModal.Text("Str_WhatsNew_Heading", "Smart Tools was updated"), 0);
                heading.FontSize = 18;
                heading.Margin = new Thickness(12, 0, 0, 0);
                heading.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(heading);
                DockPanel.SetDock(head, Dock.Top);
                root.Children.Add(head);

                var gotIt = AppModal.MakeButton(AppModal.Text("Str_WhatsNew_Close", "Got it"), primary: true);
                gotIt.Margin = new Thickness(0, 16, 0, 0);
                gotIt.HorizontalAlignment = HorizontalAlignment.Right;
                gotIt.Click += (_, _) => close();
                DockPanel.SetDock(gotIt, Dock.Bottom);
                root.Children.Add(gotIt);

                var list = new StackPanel();
                foreach (var entry in entries)
                {
                    var version = new TextBlock { Text = Loc.F("Str_WhatsNew_Version", "Version {0}", entry.Version), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 6) };
                    version.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                    list.Children.Add(version);
                    foreach (string key in entry.ItemKeys)
                    {
                        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        var dot = new PackIcon { Kind = PackIconKind.CheckCircleOutline, Width = 15, Height = 15, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                        dot.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentColor");
                        var text = new TextBlock { Text = AppModal.Text(key, key), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                        Grid.SetColumn(text, 1);
                        row.Children.Add(dot);
                        row.Children.Add(text);
                        list.Children.Add(row);
                    }
                }
                root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

                AppModal.CancelOnEscape(root, close);
                return (root, gotIt);
            }, 480, 560);
        }
    }
}
