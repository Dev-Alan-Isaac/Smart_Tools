using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Asks for one line of text (a name) in an in-window modal. Returns null when cancelled; empty text is not accepted.</summary>
    internal static class TextPromptDialog
    {
        /// <summary>Shows the prompt; null when cancelled.</summary>
        public static string? Ask(string title, string prompt, string initial = "", int maxLength = 60)
        {
            string? result = null;
            AppModal.Present(null, close =>
            {
                var stack = new StackPanel();
                stack.Children.Add(AppModal.Heading(title, 6));
                var text = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                stack.Children.Add(text);

                var box = new TextBox { Text = initial, MaxLength = maxLength };
                box.GotKeyboardFocus += (_, _) => box.SelectAll();
                void Accept()
                {
                    string value = box.Text.Trim();
                    if (value.Length == 0) return;
                    result = value;
                    close();
                }
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
                stack.Children.Add(box);

                var ok = AppModal.MakeButton(AppModal.Text("Str_Vault_DlgOk", "OK"), primary: true);
                ok.Click += (_, _) => Accept();
                var cancel = AppModal.MakeButton(AppModal.Text("Str_Vault_Cancel", "Cancel"), primary: false);
                cancel.Click += (_, _) => close();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
                buttons.Children.Add(cancel);
                buttons.Children.Add(ok);
                stack.Children.Add(buttons);

                AppModal.CancelOnEscape(stack, close);
                return (stack, box);
            }, 400, 520);
            return result;
        }
    }
}
