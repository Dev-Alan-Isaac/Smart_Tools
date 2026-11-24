using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Adds an eye button to a PasswordBox so the password can be seen as plain text while the user types it.
    /// The PasswordBox stays the one source of truth (callers keep reading its SecurePassword, Clear() and Focus() it as before):
    /// while the eye is on, a TextBox is drawn over it and mirrors it both ways, and the PasswordBox is only made see-through.
    /// The plain text exists in memory only while the eye is on, and is dropped when it is switched off or the box goes away.
    /// </summary>
    public static class PasswordReveal
    {
        /// <summary>Wraps <paramref name="box"/> in a grid with the eye button; put the returned element where the box was going to go.</summary>
        public static FrameworkElement Wrap(PasswordBox box)
        {
            var grid = new Grid();
            box.Padding = new Thickness(box.Padding.Left, box.Padding.Top, 36, box.Padding.Bottom);

            var text = new TextBox
            {
                Visibility = Visibility.Collapsed,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = box.Padding,
                FontFamily = box.FontFamily,
                FontSize = box.FontSize,
            };
            text.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding(nameof(FrameworkElement.ActualHeight)) { Source = box });

            var eye = new Button
            {
                Margin = new Thickness(0, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                Content = new PackIcon { Kind = PackIconKind.EyeOutline, Width = 18, Height = 18 },
            };
            eye.SetResourceReference(FrameworkElement.StyleProperty, "ListToolButtonStyle");
            eye.Width = eye.Height = 28;
            eye.MinHeight = 28;
            bool shown = false;
            SetTip(eye, false);

            bool syncing = false;

            void Show(bool on)
            {
                shown = on;
                ((PackIcon)eye.Content).Kind = on ? PackIconKind.EyeOffOutline : PackIconKind.EyeOutline;
                SetTip(eye, on);
                if (on)
                {
                    syncing = true;
                    text.Text = box.Password;
                    syncing = false;
                    text.Visibility = Visibility.Visible;
                    box.Opacity = 0;                       // still there (focus, Clear, SecurePassword), just not seen
                    box.IsHitTestVisible = false;
                    text.Focus();
                    text.CaretIndex = text.Text.Length;
                }
                else
                {
                    text.Visibility = Visibility.Collapsed;
                    text.Text = "";                        // the plain text is not kept
                    box.Opacity = 1;
                    box.IsHitTestVisible = true;
                    box.Focus();
                }
            }

            eye.Click += (_, _) => Show(!shown);

            text.TextChanged += (_, _) =>
            {
                if (syncing) return;
                syncing = true;
                box.Password = text.Text;
                syncing = false;
            };
            box.PasswordChanged += (_, _) =>
            {
                if (syncing || text.Visibility != Visibility.Visible) return;
                syncing = true;
                text.Text = box.Password;
                syncing = false;
            };
            // A caller that focuses the box (after a wrong password, say) lands in the visible field.
            box.GotKeyboardFocus += (_, _) =>
            {
                if (text.Visibility != Visibility.Visible) return;
                text.Focus();
                text.CaretIndex = text.Text.Length;
            };
            // Enter in the text field does what Enter in the password box does for the caller.
            text.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || PresentationSource.FromVisual(box) is not { } source) return;
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                box.RaiseEvent(enter);
                e.Handled = true;
            };
            grid.Unloaded += (_, _) => { if (shown) Show(false); text.Text = ""; };

            grid.Children.Add(box);
            grid.Children.Add(text);
            grid.Children.Add(eye);
            return grid;
        }

        /// <summary>Swaps a PasswordBox that XAML placed in a panel for its wrapped version, in the same spot.</summary>
        public static void WrapInPlace(PasswordBox box)
        {
            if (box.Parent is not Panel panel) return;
            int index = panel.Children.IndexOf(box);
            panel.Children.RemoveAt(index);
            panel.Children.Insert(index, Wrap(box));
        }

        private static void SetTip(Button eye, bool shown) =>
            eye.ToolTip = shown
                ? (Application.Current?.TryFindResource("Str_Password_Hide") as string ?? "Hide the password")
                : (Application.Current?.TryFindResource("Str_Password_Show") as string ?? "Show the password");
    }
}
