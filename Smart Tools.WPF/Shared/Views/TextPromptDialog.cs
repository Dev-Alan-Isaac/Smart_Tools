using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Asks for one line of text (a name). Returns null when cancelled; empty text is not accepted.</summary>
    internal sealed class TextPromptDialog : Window
    {
        private readonly TextBox _box = new();

        public string? Result { get; private set; }

        private TextPromptDialog(string title, string prompt, string initial, int maxLength)
        {
            Title = title;
            Width = 400;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;
            this.SetResourceReference(BackgroundProperty, "ContentBg");

            var stack = new StackPanel { Margin = new Thickness(22) };
            var text = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(text);

            _box.Text = initial;
            _box.MaxLength = maxLength;
            _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
            stack.Children.Add(_box);

            var ok = new Button { Content = Text("Str_Vault_DlgOk", "OK"), Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
            ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = Text("Str_Vault_Cancel", "Cancel"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            cancel.Click += (_, _) => { Result = null; Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);
            Content = stack;

            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }

        /// <summary>Shows the dialog; null when cancelled.</summary>
        public static string? Ask(string title, string prompt, string initial = "", int maxLength = 60)
        {
            var dlg = new TextPromptDialog(title, prompt, initial, maxLength);
            dlg.ShowDialog();
            return dlg.Result;
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void Accept()
        {
            string text = _box.Text.Trim();
            if (text.Length == 0) return;
            Result = text;
            Close();
        }
    }
}
