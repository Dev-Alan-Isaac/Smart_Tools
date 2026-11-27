using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The small card at the bottom right of a tool the first time it is opened: the tool's icon, name and one-line description,
    /// "Show me how" (its guided tour, when it has one), "Don't show tips" and a close button. It goes away by itself after a while
    /// (not while the mouse is on it). MainWindow decides when; this is UI only. Built in code like the other shell overlays.
    /// </summary>
    public sealed class ToolTipCard : Border
    {
        private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(12);

        private readonly PackIcon _icon = new() { Width = 18, Height = 18, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        private readonly TextBlock _title = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        private readonly TextBlock _text = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
        private readonly Button _tour = new() { Height = 32, Padding = new Thickness(12, 0, 12, 0) };
        private readonly Button _never = new() { Height = 32, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0) };
        private readonly DispatcherTimer _timer = new() { Interval = ShowFor };

        /// <summary>"Show me how" was pressed.</summary>
        public event Action? TourRequested;

        /// <summary>"Don't show tips" was pressed.</summary>
        public event Action? TurnOffRequested;

        public ToolTipCard()
        {
            Visibility = Visibility.Collapsed;
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
            Margin = new Thickness(0, 0, 20, 20);
            Width = 360;
            Padding = new Thickness(16, 14, 12, 14);
            CornerRadius = new CornerRadius(12);
            BorderThickness = new Thickness(1);
            SetResourceReference(BackgroundProperty, "PopupBg");
            SetResourceReference(BorderBrushProperty, "AccentColor");
            System.Windows.Controls.Panel.SetZIndex(this, 50);

            var badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0), Child = _icon };
            badge.SetResourceReference(BackgroundProperty, "AccentColorSubtle");
            _icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentColor");
            _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            _text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            var close = new Button { Width = 28, Height = 28, Padding = new Thickness(0), VerticalAlignment = System.Windows.VerticalAlignment.Top, Content = new PackIcon { Kind = PackIconKind.Close, Width = 14, Height = 14 } };
            close.SetResourceReference(StyleProperty, "NeutralIconButtonStyle");
            close.SetResourceReference(ToolTipProperty, "Str_ToolTip_Close");
            close.Click += (_, _) => Hide();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(badge);
            Grid.SetColumn(_title, 1);
            head.Children.Add(_title);
            Grid.SetColumn(close, 2);
            head.Children.Add(close);

            _tour.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            _tour.Content = Row(PackIconKind.HelpCircleOutline, "Str_ToolTip_ShowMe");
            _tour.Click += (_, _) => { Hide(); TourRequested?.Invoke(); };
            _never.SetResourceReference(StyleProperty, "NeutralIconButtonStyle");
            _never.Content = Row(null, "Str_ToolTip_TurnOff");
            _never.SetResourceReference(ToolTipProperty, "Str_ToolTip_TurnOffTip");
            _never.Click += (_, _) => { Hide(); TurnOffRequested?.Invoke(); };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tour, _never } };

            Child = new StackPanel { Children = { head, _text, actions } };

            _timer.Tick += (_, _) => Hide();
            MouseEnter += (_, _) => _timer.Stop();
            MouseLeave += (_, _) => { if (IsVisible) { _timer.Stop(); _timer.Start(); } };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        }

        private static StackPanel Row(PackIconKind? icon, string textKey)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon is { } kind) row.Children.Add(new PackIcon { Kind = kind, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var text = new TextBlock { VerticalAlignment = System.Windows.VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.TextProperty, textKey);
            row.Children.Add(text);
            return row;
        }

        /// <summary>Shows the card for a tool. <paramref name="hasTour"/> false hides "Show me how" (the page has no tour steps).</summary>
        public void Show(string title, string description, PackIconKind icon, bool hasTour)
        {
            _title.Text = title;
            _text.Text = description;
            _icon.Kind = icon;
            _tour.Visibility = hasTour ? Visibility.Visible : Visibility.Collapsed;
            _never.Margin = new Thickness(hasTour ? 8 : 0, 0, 0, 0);
            UiMotion.FadeVisibility(this, true);
            _timer.Stop();
            _timer.Start();
        }

        public void Hide()
        {
            _timer.Stop();
            if (Visibility == Visibility.Visible) UiMotion.FadeVisibility(this, false);
        }
    }
}
