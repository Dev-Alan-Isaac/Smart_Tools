using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    // Entry point and shared pieces for the small in-window dialogs (saved file, text prompt, PDF password, What's new, vault group).
    public partial class AppModal
    {
        /// <summary>
        /// Shows a dialog card in the window that asked and waits until the content calls the close action it is given.
        /// The builder returns the card's content and the element that gets the keyboard first (null = the first control).
        /// False means no window could host it (nothing was shown) and the caller should use its fallback.
        /// </summary>
        internal static bool Present(Window? owner, Func<Action, (FrameworkElement Content, IInputElement? Focus)> build, double minWidth = 380, double maxWidth = 560)
        {
            var app = Application.Current;
            if (app == null) return false;
            if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Present(owner, build, minWidth, maxWidth));

            var host = HostFor(owner);
            if (host == null || app.TryFindResource("PopupBg") == null) return false;
            try
            {
                var layer = ModalLayer.For(host);
                if (layer == null) return false;
                layer.Run(build, minWidth, maxWidth);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Xaml.XamlException)
            {
                Smart_Tools.WPF.Log.Info($"In-window modal failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>The window that asked, else the active app window, else the main window; null when none is visible and on screen.</summary>
        private static Window? HostFor(Window? owner)
        {
            var app = Application.Current;
            if (app == null) return null;
            Window? host = owner is { IsLoaded: true, IsVisible: true } ? owner : null;
            host ??= app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w is not CommandPalette);
            host ??= app.MainWindow is { IsVisible: true } main ? main : null;
            return host is { IsVisible: true, WindowState: not WindowState.Minimized } ? host : null;
        }

        /// <summary>A card heading (16 pt semi-bold, primary text) used by every dialog.</summary>
        internal static TextBlock Heading(string text, double bottom = 10)
        {
            var heading = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, bottom) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            return heading;
        }

        /// <summary>Calls <paramref name="cancel"/> when Esc is pressed inside <paramref name="root"/>.</summary>
        internal static void CancelOnEscape(UIElement root, Action cancel) => root.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { cancel(); e.Handled = true; } };

        /// <summary>The shared look of a dialog button (primary = accent). Enter works through keyboard focus, not IsDefault.</summary>
        internal static Button MakeButton(string text, bool primary, PackIconKind? icon = null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon is { } kind) content.Children.Add(new PackIcon { Kind = kind, Width = 15, Height = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, MinWidth = 84, MinHeight = 36, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
            button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButtonStyle" : "NeutralIconButtonStyle");
            return button;
        }
    }
}
