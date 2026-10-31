using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>What the widget's copy buttons ask for.</summary>
    internal enum VaultWidgetCopy
    {
        Username,
        Password,
        Totp,
    }

    /// <summary>
    /// The floating quick-access window of the Password Vault: borderless, always on top, not in the taskbar.
    /// Search, then copy a username / password / one-time code. While the vault is locked it holds NO entries and
    /// shows only an unlock button. It is hidden from screen capture and recordings. It only displays what the
    /// view hands it and raises events - copying, locking and saving are the view's job.
    /// </summary>
    internal sealed class PasswordVaultWidgetWindow : Window
    {
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        private const uint WdaExcludeFromCapture = 0x11;
        private const int MaxRows = 8;

        private readonly Border _card = new();
        private readonly TextBox _search = new();
        private readonly Border _searchBar = new();
        private readonly StackPanel _rows = new();
        private readonly TextBlock _status = new();
        private readonly Button _unlockButton = new();
        private readonly StackPanel _unlocked = new();
        private readonly StackPanel _lockedPanel = new();
        private readonly Button _lockButton = new();
        private readonly TextBlock _title = new();

        public event Action? CloseRequested;
        public event Action<double, double>? PositionChanged;
        public event Action? LockRequested;
        public event Action? UnlockRequested;
        public event Action<string>? SearchChanged;
        public event Action<Guid, VaultWidgetCopy>? CopyRequested;

        public PasswordVaultWidgetWindow(VaultSettings settings)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            Width = 320;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "Password Vault";
            UseLayoutRounding = true;

            _card.CornerRadius = new CornerRadius(12);
            _card.BorderThickness = new Thickness(1);
            _card.Padding = new Thickness(12, 10, 12, 12);
            _card.SetResourceReference(Border.BackgroundProperty, "CardBg");
            _card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

            // ---- header (drag handle) ----
            var icon = new PackIcon { Kind = PackIconKind.ShieldKeyOutline, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(PackIcon.ForegroundProperty, "AccentFill");
            _title.FontWeight = FontWeights.SemiBold;
            _title.Margin = new Thickness(8, 0, 0, 0);
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            _lockButton = IconButton(PackIconKind.LockOutline, () => LockRequested?.Invoke());
            var close = IconButton(PackIconKind.Close, () => CloseRequested?.Invoke());
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_lockButton);
            buttons.Children.Add(close);

            var header = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(icon);
            left.Children.Add(_title);
            header.Children.Add(left);
            Grid.SetColumn(buttons, 2);
            header.Children.Add(buttons);
            header.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); } catch (InvalidOperationException) { return; }
                PositionChanged?.Invoke(Left, Top);
            };

            // ---- unlocked: search + rows ----
            _search.BorderThickness = new Thickness(0);
            _search.Background = Brushes.Transparent;
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            _search.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
            _search.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimary");
            _search.TextChanged += (_, _) => SearchChanged?.Invoke(_search.Text);
            _searchBar.CornerRadius = new CornerRadius(8);
            _searchBar.BorderThickness = new Thickness(1);
            _searchBar.Padding = new Thickness(8, 5, 8, 5);
            _searchBar.Margin = new Thickness(0, 10, 0, 8);
            _searchBar.SetResourceReference(Border.BackgroundProperty, "CardBg");
            _searchBar.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            _searchBar.Child = _search;

            _status.FontSize = 11.5;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(2, 2, 2, 0);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            var scroll = new ScrollViewer { MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rows };
            _unlocked.Children.Add(_searchBar);
            _unlocked.Children.Add(scroll);
            _unlocked.Children.Add(_status);

            // ---- locked ----
            _unlockButton.Padding = new Thickness(14, 6, 14, 6);
            _unlockButton.HorizontalAlignment = HorizontalAlignment.Left;
            _unlockButton.Margin = new Thickness(0, 10, 0, 0);
            _unlockButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            _unlockButton.Click += (_, _) => UnlockRequested?.Invoke();
            var lockedText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Tag = "locked" };
            lockedText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _lockedPanel.Children.Add(lockedText);
            _lockedPanel.Children.Add(_unlockButton);

            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(_unlocked);
            root.Children.Add(_lockedPanel);
            _card.Child = root;
            Content = _card;

            RefreshTexts();
            SetLocked(true);
            Loaded += (_, _) =>
            {
                PlaceOnScreen(settings);
                try { SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WdaExcludeFromCapture); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            };
            Closed += (_, _) => _search.Clear();
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Re-reads the texts (language can change while the widget is open).</summary>
        public void RefreshTexts()
        {
            _title.Text = Text("Str_Vault_Title", "Password Vault");
            _search.ToolTip = Text("Str_Vault_WidgetSearch", "Search");
            _lockButton.ToolTip = Text("Str_Vault_LockNow", "Lock now");
            _unlockButton.Content = Text("Str_Vault_WidgetUnlock", "Open Password Vault to unlock");
            if (_lockedPanel.Children[0] is TextBlock t) t.Text = Text("Str_Vault_WidgetLocked", "The vault is locked.");
        }

        public string SearchText => _search.Text;

        public void FocusSearch() => _search.Focus();

        /// <summary>Locked = show only the unlock button, and forget everything that was listed.</summary>
        public void SetLocked(bool locked)
        {
            _unlocked.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            _lockedPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
            _lockButton.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            if (locked)
            {
                _rows.Children.Clear();
                _search.Clear();
                _status.Text = "";
            }
        }

        /// <summary>Shows the first matches (the view does the searching). Empty list = a hint, not a blank box.</summary>
        public void SetEntries(IReadOnlyList<VaultEntry> matches, bool hasAnyEntries)
        {
            _rows.Children.Clear();
            foreach (var e in matches.Take(MaxRows)) _rows.Children.Add(BuildRow(e));
            _status.Text = matches.Count == 0
                ? Text(hasAnyEntries ? "Str_Vault_WidgetNoMatch" : "Str_Vault_WidgetEmpty", hasAnyEntries ? "No matches." : "No entries yet.")
                : matches.Count > MaxRows ? string.Format(Text("Str_Vault_WidgetMore", "{0} more - keep typing to narrow it down."), matches.Count - MaxRows) : "";
        }

        public void ShowMessage(string text) => _status.Text = text;

        private UIElement BuildRow(VaultEntry e)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = e.Title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var sub = new TextBlock { Text = e.Subtitle, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(name);
            text.Children.Add(sub);
            grid.Children.Add(text);

            var id = e.Id;
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(IconButton(PackIconKind.AccountOutline, () => CopyRequested?.Invoke(id, VaultWidgetCopy.Username), Text("Str_Vault_CopyUsername", "Copy username")));
            actions.Children.Add(IconButton(PackIconKind.KeyOutline, () => CopyRequested?.Invoke(id, VaultWidgetCopy.Password), Text("Str_Vault_CopyPassword", "Copy password")));
            if (e.HasTotp) actions.Children.Add(IconButton(PackIconKind.TimerOutline, () => CopyRequested?.Invoke(id, VaultWidgetCopy.Totp), Text("Str_Vault_CopyCode", "Copy one-time code")));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            return grid;
        }

        private static Button IconButton(PackIconKind kind, Action click, string? tip = null)
        {
            var b = new Button { Content = new PackIcon { Kind = kind, Width = 16, Height = 16 }, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, Width = 30, Height = 30, Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "ListToolButtonStyle");
            b.Click += (_, _) => click();
            return b;
        }

        /// <summary>Bottom-right corner of the work area.</summary>
        public void SnapToCorner()
        {
            var wa = SystemParameters.WorkArea;
            UpdateLayout();
            Left = wa.Right - ActualWidth - 16;
            Top = wa.Bottom - ActualHeight - 16;
            PositionChanged?.Invoke(Left, Top);
        }

        private void PlaceOnScreen(VaultSettings s)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (s.WidgetLeft is double l && s.WidgetTop is double t && screen.Contains(new System.Windows.Point(l + 20, t + 20)))
            {
                Left = l;
                Top = t;
            }
            else
            {
                SnapToCorner();
            }
        }
    }
}
