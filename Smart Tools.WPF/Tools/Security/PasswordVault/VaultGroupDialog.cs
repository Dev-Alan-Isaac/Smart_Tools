using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// An in-window modal asking for the group an entry should move to: pick an existing one or type a new path (Games/Launchers). Empty = ungrouped.
    /// Use: <c>var d = new VaultGroupDialog(...); d.ShowDialog(); var group = d.Result;</c>
    /// </summary>
    internal sealed class VaultGroupDialog
    {
        private readonly string _title;
        private readonly string _intro;
        private readonly List<string> _groups;
        private readonly string _current;

        /// <summary>The chosen group path (may be empty = ungrouped), or null if cancelled.</summary>
        public string? Result { get; private set; }

        public VaultGroupDialog(string title, string intro, IEnumerable<string> groups, string current)
        {
            _title = title;
            _intro = intro;
            _groups = groups.ToList();
            _current = current;
        }

        public void ShowDialog()
        {
            Result = null;
            AppModal.Present(null, close =>
            {
                var stack = new StackPanel();
                stack.Children.Add(AppModal.Heading(_title, 6));
                var t = new TextBlock { Text = _intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                stack.Children.Add(t);

                var box = new ComboBox { IsEditable = true };
                foreach (string g in _groups) box.Items.Add(g);
                box.Text = _current;
                void Accept() { Result = box.Text.Trim(); close(); }
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter && !box.IsDropDownOpen) { Accept(); e.Handled = true; } };
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
        }
    }
}
