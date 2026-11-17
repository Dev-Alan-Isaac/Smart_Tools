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
    /// The app's message box: a drop-in for <see cref="System.Windows.MessageBox.Show(string)"/> that is modal and uses the
    /// theme tokens (dark or light) instead of the default Windows look. Same buttons, icons and results.
    /// Falls back to the Windows box when the theme is not loaded yet (start-up errors) or the dialog cannot be built, so a
    /// message is never lost. Calls from another thread are moved to the UI thread.
    /// </summary>
    internal sealed class ThemedMessageBox : Window
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

            if (owner == null || !owner.IsLoaded) owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? (app.MainWindow is { IsVisible: true } main ? main : null);
            try
            {
                if (app.TryFindResource("ContentBg") == null) throw new InvalidOperationException("The theme is not loaded.");
                var box = new ThemedMessageBox(owner, messageBoxText, caption, button, icon, defaultResult);
                box.ShowDialog();
                return box._result;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Xaml.XamlException)
            {
                Smart_Tools.WPF.Log.Info($"Themed message box fell back to the Windows one: {ex.Message}");
                return owner != null
                    ? System.Windows.MessageBox.Show(owner, messageBoxText, caption, button, icon, defaultResult)
                    : System.Windows.MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
            }
        }

        private MessageBoxResult _result;
        private readonly string _text;

        private ThemedMessageBox(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
        {
            _text = text ?? "";
            Title = string.IsNullOrWhiteSpace(caption) ? Application.Current?.MainWindow?.Title ?? "Smart Tools" : caption;
            MinWidth = 380;
            MaxWidth = 560;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            if (owner != null && owner.IsLoaded)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            this.SetResourceReference(BackgroundProperty, "ContentBg");
            _result = CloseResult(buttons);

            var root = new StackPanel { Margin = new Thickness(22) };

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (IconFor(image) is { } icon)
            {
                var chip = new PackIcon { Kind = icon.Kind, Width = 32, Height = 32, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
                chip.SetResourceReference(ForegroundProperty, icon.Token);
                body.Children.Add(chip);
            }
            var message = new TextBlock { Text = _text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
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
                button.Click += (_, _) => { _result = captured; Close(); };
                if (result == chosenDefault) defaultButton = button;
                row.Children.Add(button);
            }
            root.Children.Add(row);
            Content = root;

            Loaded += (_, _) => defaultButton?.Focus();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && buttons != MessageBoxButton.YesNo) { Close(); e.Handled = true; }
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { TryCopy(); e.Handled = true; }
            };
        }

        private void TryCopy()
        {
            try { System.Windows.Clipboard.SetText(_text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard was busy */ }
        }

        /// <summary>What closing with the X or Esc returns, like the Windows box: Cancel if there is one, else No, else OK.</summary>
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

        private static Button MakeButton(string text, bool primary)
        {
            var button = new Button { Content = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, MinWidth = 84, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
            button.SetResourceReference(StyleProperty, primary ? "PrimaryButtonStyle" : "NeutralIconButtonStyle");
            return button;
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
