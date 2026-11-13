using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// "What's new" window: the patch notes of the version that was just installed (see <see cref="ReleaseNotes"/>).
    /// </summary>
    internal sealed class WhatsNewDialog : Window
    {
        public static void Show(IReadOnlyList<ReleaseEntry> entries) => new WhatsNewDialog(entries).ShowDialog();

        private WhatsNewDialog(IReadOnlyList<ReleaseEntry> entries)
        {
            Title = Text("Str_WhatsNew_Title", "What's new");
            Width = 520;
            MaxHeight = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;
            this.SetResourceReference(BackgroundProperty, "ContentBg");

            var root = new DockPanel { Margin = new Thickness(24) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var icon = new PackIcon { Kind = PackIconKind.PartyPopper, Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(ForegroundProperty, "AccentColor");
            head.Children.Add(icon);
            var heading = new TextBlock { Text = Text("Str_WhatsNew_Heading", "Smart Tools was updated"), FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            head.Children.Add(heading);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var close = new Button { Content = Text("Str_WhatsNew_Close", "Got it"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
            close.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            close.Click += (_, _) => Close();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);

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
                    dot.SetResourceReference(ForegroundProperty, "AccentColor");
                    var text = new TextBlock { Text = Text(key, key), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                    text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    Grid.SetColumn(text, 1);
                    row.Children.Add(dot);
                    row.Children.Add(text);
                    list.Children.Add(row);
                }
            }
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            Loaded += (_, _) => close.Focus();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
