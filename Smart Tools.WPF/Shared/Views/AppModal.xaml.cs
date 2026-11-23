using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The app's one modal: a card over a dimmed main window (not a separate OS window). <see cref="Show(string,string,MessageBoxButton,MessageBoxImage,MessageBoxResult)"/>
    /// has the shape of <see cref="MessageBox.Show(string)"/> and blocks the caller (nested dispatcher frame) until a button, Esc or
    /// shutdown closes it, so callers read the result inline. The main window registers its overlay grid in <see cref="Host"/>.
    /// When the overlay cannot be used (no host, main window hidden or minimized, theme not loaded, another window as owner)
    /// the plain Windows box is shown instead, so a message is never lost. Calls from another thread move to the UI thread.
    /// </summary>
    public partial class AppModal : UserControl
    {
        /// <summary>The overlay grid on top of the main window's content. Set once by <c>MainWindow</c>.</summary>
        public static Grid? Host { get; set; }

        private static int _open;
        private static readonly List<UIElement> _disabled = new();

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
            if (app == null) return MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Show(owner, messageBoxText, caption, button, icon, defaultResult));

            var main = app.MainWindow;
            bool canOverlay = Host is { IsLoaded: true } && main is { IsVisible: true, WindowState: not WindowState.Minimized }
                              && (owner == null || ReferenceEquals(owner, main))
                              && app.TryFindResource("CardBg") != null;
            if (canOverlay)
            {
                try { return ShowOverlay(app, messageBoxText, caption, button, icon, defaultResult); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Xaml.XamlException)
                {
                    Smart_Tools.WPF.Log.Info($"Modal fell back to the Windows message box: {ex.Message}");
                }
            }
            var parent = owner is { IsLoaded: true } ? owner : main is { IsVisible: true } ? main : null;
            return parent != null
                ? MessageBox.Show(parent, messageBoxText, caption, button, icon, defaultResult)
                : MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
        }

        private static MessageBoxResult ShowOverlay(Application app, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            var host = Host!;
            var modal = new AppModal(text, caption, buttons, icon, defaultResult);
            var frame = new DispatcherFrame();
            modal._frame = frame;
            void OnShutdown(object? s, EventArgs e) => frame.Continue = false;
            app.Dispatcher.ShutdownStarted += OnShutdown;

            var previousFocus = Keyboard.FocusedElement;
            if (_open++ == 0) DisableBackground(host);
            Panel.SetZIndex(modal, 1000);
            host.Children.Add(modal);
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                app.Dispatcher.ShutdownStarted -= OnShutdown;
                host.Children.Remove(modal);
                if (--_open == 0) RestoreBackground();
                if (previousFocus is IInputElement { } element) element.Focus();
            }
            return modal._result;
        }

        /// <summary>Everything in the window except the overlay grid stops taking mouse and keyboard while a modal is open.</summary>
        private static void DisableBackground(Grid host)
        {
            _disabled.Clear();
            if (host.Parent is not Panel root) return;
            foreach (UIElement child in root.Children)
                if (!ReferenceEquals(child, host) && child.IsEnabled) { child.IsEnabled = false; _disabled.Add(child); }
        }

        private static void RestoreBackground()
        {
            foreach (var child in _disabled) child.IsEnabled = true;
            _disabled.Clear();
        }

        private DispatcherFrame? _frame;
        private MessageBoxResult _result;
        private readonly string _text;
        private readonly MessageBoxButton _buttons;

        private AppModal(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
        {
            InitializeComponent();
            _text = text ?? "";
            _buttons = buttons;
            _result = CloseResult(buttons);
            TxtTitle.Text = string.IsNullOrWhiteSpace(caption) ? Application.Current?.MainWindow?.Title ?? "Smart Tools" : caption;
            TxtMessage.Text = _text;
            if (IconFor(image) is { } icon)
            {
                IconChip.Kind = icon.Kind;
                IconChip.SetResourceReference(ForegroundProperty, icon.Token);
                IconChip.Visibility = Visibility.Visible;
            }

            var list = ButtonsFor(buttons);
            var chosen = defaultResult != MessageBoxResult.None && list.Contains(defaultResult) ? defaultResult : list[0];
            Button? defaultButton = null;
            foreach (var result in list)
            {
                var primary = result == chosen;
                var button = new Button
                {
                    Content = new TextBlock { Text = Label(result), VerticalAlignment = VerticalAlignment.Center },
                    MinWidth = 84,
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = primary,
                };
                button.SetResourceReference(StyleProperty, primary ? "PrimaryButtonStyle" : "NeutralIconButtonStyle");
                var captured = result;
                button.Click += (_, _) => Finish(captured);
                if (primary) defaultButton = button;
                ButtonRow.Children.Add(button);
            }

            Loaded += (_, _) => defaultButton?.Focus();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _buttons != MessageBoxButton.YesNo) { Finish(CloseResult(_buttons)); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { TryCopy(); e.Handled = true; }
        }

        private void Finish(MessageBoxResult result)
        {
            _result = result;
            if (_frame != null) _frame.Continue = false;
        }

        private void TryCopy()
        {
            try { Clipboard.SetText(_text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard was busy */ }
        }

        /// <summary>What Esc returns, like the Windows box: Cancel if there is one, else No, else OK.</summary>
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

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
