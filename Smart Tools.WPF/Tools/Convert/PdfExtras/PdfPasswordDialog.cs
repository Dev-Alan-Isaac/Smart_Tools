using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Asks for the password of one protected PDF in an in-window modal. Used by the PDF tools the moment a file needs it.</summary>
    internal static class PdfPasswordDialog
    {
        /// <summary>Shows the prompt and returns the password, or null when cancelled.</summary>
        public static string? Ask(string title, string fileName, bool wrong)
        {
            string? result = null;
            bool shown = AppModal.Present(null, close =>
            {
                var stack = new StackPanel();

                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
                var icon = new PackIcon { Kind = PackIconKind.LockOutline, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
                icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentColor");
                head.Children.Add(icon);
                var heading = AppModal.Heading(AppModal.Text("Str_PdfPwd_Heading", "This PDF is protected"), 0);
                heading.Margin = new Thickness(10, 0, 0, 0);
                heading.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(heading);
                stack.Children.Add(head);

                var intro = new TextBlock { Text = string.Format(AppModal.Text("Str_PdfPwd_Intro", "Type the password to open {0}. It is only kept while this tool is open."), fileName), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
                intro.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                stack.Children.Add(intro);

                var box = new PasswordBox();
                void Accept()
                {
                    if (box.Password.Length == 0) return;
                    result = box.Password;
                    close();
                }
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
                stack.Children.Add(box);

                if (wrong)
                {
                    var bad = new TextBlock { Text = AppModal.Text("Str_PdfX_RowWrongPassword", "Wrong password"), Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
                    bad.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                    stack.Children.Add(bad);
                }

                var ok = AppModal.MakeButton(AppModal.Text("Str_PdfPwd_Unlock", "Unlock"), primary: true);
                ok.Click += (_, _) => Accept();
                var cancel = AppModal.MakeButton(AppModal.Text("Str_Vault_Cancel", "Cancel"), primary: false);
                cancel.Click += (_, _) => close();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
                buttons.Children.Add(cancel);
                buttons.Children.Add(ok);
                stack.Children.Add(buttons);

                AppModal.CancelOnEscape(stack, close);
                return (stack, box);
            }, 420, 520);
            if (!shown) Smart_Tools.WPF.Log.Info($"Password prompt for {title} could not be shown.");
            return result;
        }
    }
}
