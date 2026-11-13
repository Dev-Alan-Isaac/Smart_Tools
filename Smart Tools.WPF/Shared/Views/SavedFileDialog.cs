using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.IO;
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
    /// The one "saved" message every tool shows after writing a file: where it went, with Open file / Show in folder / Close.
    /// Opening goes through <see cref="ShellOpen"/> (a program or script is revealed, never run).
    /// </summary>
    internal sealed class SavedFileDialog : Window
    {
        /// <param name="title">The tool's name (window title).</param>
        /// <param name="path">The file that was written.</param>
        /// <param name="note">Optional extra line, e.g. "kept the existing file, this one got its own name".</param>
        public static void Show(string title, string path, string note = "") => new SavedFileDialog(title, path, note).ShowDialog();

        private SavedFileDialog(string title, string path, string note)
        {
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;
            this.SetResourceReference(BackgroundProperty, "ContentBg");

            var stack = new StackPanel { Margin = new Thickness(22) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var icon = new PackIcon { Kind = PackIconKind.CheckCircleOutline, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(ForegroundProperty, "StatusSuccessFg");
            head.Children.Add(icon);
            var heading = new TextBlock { Text = Text("Str_Saved_Heading", "Saved"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            head.Children.Add(heading);
            stack.Children.Add(head);

            var name = new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            stack.Children.Add(name);
            var where = new TextBlock { Text = Path.GetDirectoryName(path) ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), FontSize = 12, ToolTip = path };
            where.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(where);

            if (note.Trim().Length > 0)
            {
                var extra = new TextBlock { Text = note.Trim(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
                extra.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                stack.Children.Add(extra);
            }

            var open = MakeButton(Text("Str_Saved_Open", "Open file"), PackIconKind.OpenInNew, primary: true);
            open.Click += (_, _) => { ShellOpen.OpenFile(path); Close(); };
            var show = MakeButton(Text("Str_Saved_Show", "Show in folder"), PackIconKind.FolderOpenOutline, primary: false);
            show.Click += (_, _) => { ShellOpen.Reveal(path); Close(); };
            var close = MakeButton(Text("Str_Saved_Close", "Close"), null, primary: false);
            close.IsCancel = true;
            close.Click += (_, _) => Close();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            buttons.Children.Add(close);
            buttons.Children.Add(show);
            buttons.Children.Add(open);
            stack.Children.Add(buttons);
            Content = stack;

            Loaded += (_, _) => open.Focus();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private static Button MakeButton(string text, PackIconKind? icon, bool primary)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon is { } kind) row.Children.Add(new PackIcon { Kind = kind, Width = 15, Height = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
            button.SetResourceReference(StyleProperty, primary ? "PrimaryButtonStyle" : "NeutralIconButtonStyle");
            return button;
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
