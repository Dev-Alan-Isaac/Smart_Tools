using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Asks for the password of one protected PDF. Used by the PDF tools the moment a file needs it.</summary>
    internal sealed class PdfPasswordDialog : Window
    {
        private readonly PasswordBox _box = new();

        /// <summary>The typed password, or null when the person cancelled.</summary>
        public string? Result { get; private set; }

        public PdfPasswordDialog(string title, string fileName, bool wrong)
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;
            this.SetResourceReference(BackgroundProperty, "ContentBg");

            var stack = new StackPanel { Margin = new Thickness(22) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var icon = new PackIcon { Kind = PackIconKind.LockOutline, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(ForegroundProperty, "AccentColor");
            head.Children.Add(icon);
            var heading = new TextBlock { Text = Text("Str_PdfPwd_Heading", "This PDF is protected"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            head.Children.Add(heading);
            stack.Children.Add(head);

            var intro = new TextBlock { Text = string.Format(Text("Str_PdfPwd_Intro", "Type the password to open {0}. It is only kept while this tool is open."), fileName), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            intro.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(intro);

            _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
            stack.Children.Add(_box);

            if (wrong)
            {
                var bad = new TextBlock { Text = Text("Str_PdfX_RowWrongPassword", "Wrong password"), Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
                bad.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                stack.Children.Add(bad);
            }

            var ok = new Button { Content = Text("Str_PdfPwd_Unlock", "Unlock"), Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
            ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = Text("Str_Vault_Cancel", "Cancel"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            cancel.Click += (_, _) => { Result = null; Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);
            Content = stack;

            Loaded += (_, _) => _box.Focus();
        }

        /// <summary>Shows the dialog and returns the password, or null when cancelled.</summary>
        public static string? Ask(string title, string fileName, bool wrong)
        {
            var dlg = new PdfPasswordDialog(title, fileName, wrong);
            dlg.ShowDialog();
            return dlg.Result;
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void Accept()
        {
            if (_box.Password.Length == 0) return;
            Result = _box.Password;
            Close();
        }
    }
}
