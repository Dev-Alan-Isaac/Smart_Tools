using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Asks for the group an entry should move to: pick an existing one or type a new path (Games/Launchers). Empty = ungrouped.</summary>
    internal sealed class VaultGroupDialog : Window
    {
        private readonly ComboBox _box = new() { IsEditable = true };

        /// <summary>The chosen group path (may be empty = ungrouped), or null if cancelled.</summary>
        public string? Result { get; private set; }

        public VaultGroupDialog(string title, string intro, IEnumerable<string> groups, string current)
        {
            Title = title;
            Width = 400;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;
            this.SetResourceReference(BackgroundProperty, "ContentBg");

            var stack = new StackPanel { Margin = new Thickness(20) };
            var t = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(t);

            foreach (string g in groups) _box.Items.Add(g);
            _box.Text = current;
            _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
            stack.Children.Add(_box);

            var ok = new Button { Content = Text("Str_Vault_DlgOk", "OK"), Padding = new Thickness(18, 6, 18, 6) };
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

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void Accept()
        {
            Result = _box.Text.Trim();
            Close();
        }
    }
}
