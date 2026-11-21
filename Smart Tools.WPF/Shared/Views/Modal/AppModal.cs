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
    /// <summary>
    /// The app's message box and the entry point for every in-window dialog. Shown inside the window that asked (a dimmed layer with a
    /// card, see <see cref="ModalLayer"/>), never as a separate operating-system window. Same call shape as
    /// <see cref="System.Windows.MessageBox.Show(string)"/>: it returns when the user has answered. Calls from another thread are moved to the UI thread.
    /// Only when no app window can host it (start-up errors, no visible window) does it fall back to the Windows message box, so a message is never lost.
    /// </summary>
    internal static class AppModal
    {
        public static MessageBoxResult Show(string messageBoxText) => Show(null, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

        public static MessageBoxResult Show(string messageBoxText, string caption) => Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) => Show(null, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) => Show(null, messageBoxText, caption, button, icon, MessageBoxResult.None);

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) => Show(null, messageBoxText, caption, button, icon, defaultResult);

        public static MessageBoxResult Show(Window? owner, string messageBoxText) => Show(owner, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

        public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption) => Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

        public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button) => Show(owner, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

        public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) => Show(owner, messageBoxText, caption, button, icon, MessageBoxResult.None);

        public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            var app = Application.Current;
            if (app == null) return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Show(owner, messageBoxText, caption, button, icon, defaultResult));

            var result = CloseResult(button);
            bool shown = Present(owner, close => BuildMessage(close, r => result = r, messageBoxText ?? "", caption, button, icon, defaultResult), 380, 560);
            if (shown) return result;

            Smart_Tools.WPF.Log.Info("No window could host the message box; the Windows one was used.");
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
        }

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

        // ------------------------------------------------------------------ the message box card

        private static (FrameworkElement, IInputElement?) BuildMessage(Action close, Action<MessageBoxResult> setResult, string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
        {
            var root = new StackPanel();

            if (!string.IsNullOrWhiteSpace(caption))
            {
                var title = new TextBlock { Text = caption, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 12) };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                root.Children.Add(title);
            }

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (IconFor(image) is { } icon)
            {
                var chip = new PackIcon { Kind = icon.Kind, Width = 32, Height = 32, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
                chip.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, icon.Token);
                body.Children.Add(chip);
            }
            var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var scroll = new ScrollViewer { Content = message, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(scroll, 1);
            body.Children.Add(scroll);
            root.Children.Add(body);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var list = ButtonsFor(buttons);
            var chosenDefault = defaultResult != MessageBoxResult.None && list.Contains(defaultResult) ? defaultResult : list[0];
            Button? defaultButton = null;
            foreach (var result in list)
            {
                var button = MakeButton(Label(result), result == chosenDefault);
                var captured = result;
                button.Click += (_, _) => { setResult(captured); close(); };
                if (result == chosenDefault) defaultButton = button;
                row.Children.Add(button);
            }
            root.Children.Add(row);

            root.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && buttons != MessageBoxButton.YesNo) { setResult(CloseResult(buttons)); close(); e.Handled = true; }
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase) { TryCopy(text); e.Handled = true; }
            };
            return (root, defaultButton);
        }

        private static void TryCopy(string text)
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard was busy */ }
        }

        /// <summary>What closing with Esc returns, like the Windows box: Cancel if there is one, else No, else OK.</summary>
        private static MessageBoxResult CloseResult(MessageBoxButton buttons) => buttons switch
        {
            MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.OK,
        };

        private static List<MessageBoxResult> ButtonsFor(MessageBoxButton buttons) => buttons switch
        {
            MessageBoxButton.OKCancel => new() { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new() { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new() { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            _ => new() { MessageBoxResult.OK },
        };

        private static string Label(MessageBoxResult result) => result switch
        {
            MessageBoxResult.Yes => Text("Str_Msg_Yes", "Yes"),
            MessageBoxResult.No => Text("Str_Msg_No", "No"),
            MessageBoxResult.Cancel => Text("Str_Common_Cancel", "Cancel"),
            _ => Text("Str_Msg_Ok", "OK"),
        };

        private static (PackIconKind Kind, string Token)? IconFor(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Error => (PackIconKind.CloseCircleOutline, "StatusErrorFg"),
            MessageBoxImage.Warning => (PackIconKind.AlertOutline, "StatusWarningFg"),
            MessageBoxImage.Question => (PackIconKind.HelpCircleOutline, "AccentColor"),
            MessageBoxImage.Information => (PackIconKind.InformationOutline, "StatusInfoFg"),
            _ => null,
        };

        /// <summary>A card heading (16 pt semi-bold, primary text) used by every dialog.</summary>
        internal static TextBlock Heading(string text, double bottom = 10)
        {
            var heading = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, bottom) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            return heading;
        }

        /// <summary>Calls <paramref name="cancel"/> when Esc is pressed inside <paramref name="root"/>.</summary>
        internal static void CancelOnEscape(UIElement root, Action cancel) => root.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { cancel(); e.Handled = true; } };

        /// <summary>The shared look of a dialog button (primary = accent). Enter works through keyboard focus, not IsDefault, so a page's own default button cannot clash.</summary>
        internal static Button MakeButton(string text, bool primary, PackIconKind? icon = null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon is { } kind) content.Children.Add(new PackIcon { Kind = kind, Width = 15, Height = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, MinWidth = 84, MinHeight = 36, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
            button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButtonStyle" : "NeutralIconButtonStyle");
            return button;
        }

        internal static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
