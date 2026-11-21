using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The one "saved" message every tool shows after writing a file, as an in-window modal: where it went, with Open file / Show in folder / Close.
    /// Opening goes through <see cref="ShellOpen"/> (a program or script is revealed, never run).
    /// </summary>
    internal static class SavedFileDialog
    {
        /// <param name="title">The tool's name.</param>
        /// <param name="path">The file that was written.</param>
        /// <param name="note">Optional extra line, e.g. "kept the existing file, this one got its own name".</param>
        public static void Show(string title, string path, string note = "")
        {
            bool shown = AppModal.Present(null, close =>
            {
                var stack = new StackPanel();

                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                var icon = new PackIcon { Kind = PackIconKind.CheckCircleOutline, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
                icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "StatusSuccessFg");
                head.Children.Add(icon);
                var heading = AppModal.Heading(AppModal.Text("Str_Saved_Heading", "Saved"), 0);
                heading.Margin = new Thickness(10, 0, 0, 0);
                heading.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(heading);
                stack.Children.Add(head);

                var name = new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title };
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

                var open = AppModal.MakeButton(AppModal.Text("Str_Saved_Open", "Open file"), primary: true, PackIconKind.OpenInNew);
                open.Click += (_, _) => { ShellOpen.OpenFile(path); close(); };
                var show = AppModal.MakeButton(AppModal.Text("Str_Saved_Show", "Show in folder"), primary: false, PackIconKind.FolderOpenOutline);
                show.Click += (_, _) => { ShellOpen.Reveal(path); close(); };
                var closeButton = AppModal.MakeButton(AppModal.Text("Str_Saved_Close", "Close"), primary: false);
                closeButton.Click += (_, _) => close();

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
                buttons.Children.Add(closeButton);
                buttons.Children.Add(show);
                buttons.Children.Add(open);
                stack.Children.Add(buttons);

                AppModal.CancelOnEscape(stack, close);
                return (stack, open);
            }, 420, 520);
            if (!shown) AppModal.Show(path, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
