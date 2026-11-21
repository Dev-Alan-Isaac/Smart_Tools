using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Application = System.Windows.Application;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>One thing the launcher can do: open a tool or run an app action.</summary>
    internal sealed record PaletteAction(string Title, string Subtitle, PackIconKind Icon, Action Run);

    /// <summary>
    /// Ctrl+K from any page: a small popup with a search box. Type to filter the tools (and a few app actions), Up/Down to move,
    /// Enter to open, Esc or a click outside to close. With nothing typed it lists favorites, then recent tools, then the actions.
    /// </summary>
    internal sealed class CommandPalette : Window
    {
        private readonly TextBox _box = new();
        private readonly ListBox _list = new();
        private readonly Func<IReadOnlyList<ToolInfo>> _startTools;
        private readonly IReadOnlyList<PaletteAction> _actions;
        private readonly Action<string> _openTool;
        private bool _closing;

        /// <param name="startTools">The tools to list before anything is typed (favorites, then recent).</param>
        /// <param name="actions">App actions that match the typed text too (switch theme, open Settings...).</param>
        /// <param name="openTool">Opens a tool by catalog id.</param>
        public CommandPalette(Func<IReadOnlyList<ToolInfo>> startTools, IReadOnlyList<PaletteAction> actions, Action<string> openTool)
        {
            _startTools = startTools;
            _actions = actions;
            _openTool = openTool;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            Owner = Application.Current?.MainWindow;
            WindowStartupLocation = WindowStartupLocation.Manual;
            this.SetResourceReference(BackgroundProperty, "CardBg");
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
                Top = Owner.Top + Math.Min(140, Owner.ActualHeight / 5);
            }

            var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 8) };
            var magnify = new PackIcon { Kind = PackIconKind.Magnify, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            magnify.SetResourceReference(ForegroundProperty, "AccentColor");
            searchRow.Children.Add(magnify);
            _box.Width = 480;
            _box.FontSize = 16;
            _box.Margin = new Thickness(10, 0, 0, 0);
            _box.BorderThickness = new Thickness(0);
            _box.Background = Brushes.Transparent;
            _box.SetResourceReference(ForegroundProperty, "TextPrimary");
            _box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimary");
            _box.VerticalContentAlignment = VerticalAlignment.Center;
            _box.TextChanged += (_, _) => Fill();
            _box.PreviewKeyDown += Box_PreviewKeyDown;
            searchRow.Children.Add(_box);

            var divider = new Border { Height = 1 };
            divider.SetResourceReference(Border.BackgroundProperty, "CardBorder");

            _list.MaxHeight = 360;
            _list.BorderThickness = new Thickness(0);
            _list.Background = Brushes.Transparent;
            _list.Margin = new Thickness(6, 6, 6, 8);
            _list.Focusable = false;
            _list.MouseUp += (_, e) => { if (_list.SelectedItem != null && e.ChangedButton == MouseButton.Left) RunSelected(); };

            var root = new StackPanel();
            root.Children.Add(searchRow);
            root.Children.Add(divider);
            root.Children.Add(_list);
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = root };
            frame.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            Content = frame;

            Loaded += (_, _) => { Fill(); _box.Focus(); };
            Deactivated += (_, _) => CloseOnce();
        }

        private void Fill()
        {
            string query = _box.Text.Trim();
            var rows = new List<PaletteAction>();
            var tools = query.Length == 0 ? _startTools() : ToolCatalogManager.Search(query);
            foreach (var tool in tools.Take(query.Length == 0 ? 8 : 10))
            {
                var t = tool;
                rows.Add(new PaletteAction(t.Name, t.CategoryTitle, ParseIcon(t.Icon), () => _openTool(t.Id)));
            }
            // An action whose title starts with what was typed ("Home", "Settings") comes first: Enter then does what the words say.
            // Other matching actions stay after the tools.
            var matching = _actions.Where(a => query.Length == 0 || a.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            var exact = matching.Where(a => query.Length > 0 && a.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            rows.InsertRange(0, exact);
            rows.AddRange(matching.Except(exact));

            _list.Items.Clear();
            foreach (var row in rows) _list.Items.Add(Build(row));
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static PackIconKind ParseIcon(string name) => Enum.TryParse<PackIconKind>(name, out var k) ? k : PackIconKind.Apps;

        private static ListBoxItem Build(PaletteAction row)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
            var icon = new PackIcon { Kind = row.Icon, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(ForegroundProperty, "AccentColor");
            line.Children.Add(icon);
            var title = new TextBlock { Text = row.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            line.Children.Add(title);
            var sub = new TextBlock { Text = row.Subtitle, FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            line.Children.Add(sub);
            return new ListBoxItem { Content = line, Tag = row };
        }

        private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; CloseOnce(); break;
                case Key.Enter: e.Handled = true; RunSelected(); break;
                case Key.Down: e.Handled = true; Move(1); break;
                case Key.Up: e.Handled = true; Move(-1); break;
            }
        }

        private void Move(int step)
        {
            if (_list.Items.Count == 0) return;
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + step, 0, _list.Items.Count - 1);
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void RunSelected()
        {
            if (_list.SelectedItem is not ListBoxItem { Tag: PaletteAction row }) return;
            CloseOnce();
            row.Run();
        }

        private void CloseOnce()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }
    }
}
