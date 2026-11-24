using Smart_Tools.WPF.Controllers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// A small modal asking for one or more passwords (change master password, backup password, ...).
    /// Values are read from the PasswordBoxes' SecureString into char[] the caller must wipe
    /// (<see cref="Wipe"/>); a plain string of the password is never made here.
    /// </summary>
    internal sealed class VaultPasswordDialog : Window
    {
        private readonly List<PasswordBox> _boxes = new();
        private readonly TextBlock _error = new();
        private readonly bool _lastTwoMustMatch;
        private readonly bool _checkStrengthOfFirstNew;

        /// <summary>One char[] per field, in order, or null if cancelled.</summary>
        public char[][]? Result { get; private set; }

        /// <param name="allowReveal">Adds an eye to every field so the password can be seen as plain text while it is typed (it is only kept in memory while the eye is on).</param>
        /// <param name="lastTwoMustMatch">The last two fields are "new" and "confirm": they must be equal, long enough and not weak (a weak one asks first).</param>
        public VaultPasswordDialog(string title, string intro, IReadOnlyList<string> labels, bool lastTwoMustMatch, bool allowReveal = false)
        {
            _lastTwoMustMatch = lastTwoMustMatch;
            _checkStrengthOfFirstNew = lastTwoMustMatch;
            Title = title;
            Width = 400;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;
            this.SetResourceReference(BackgroundProperty, "ContentBg");

            var stack = new StackPanel { Margin = new Thickness(20) };
            if (intro.Length > 0)
            {
                var t = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                stack.Children.Add(t);
            }
            foreach (string label in labels)
            {
                var l = new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = 12 };
                l.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                var box = new PasswordBox();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
                _boxes.Add(box);
                stack.Children.Add(l);
                stack.Children.Add(allowReveal ? Smart_Tools.WPF.Controls.PasswordReveal.Wrap(box) : box);
            }

            _error.Margin = new Thickness(0, 10, 0, 0);
            _error.TextWrapping = TextWrapping.Wrap;
            _error.Visibility = Visibility.Collapsed;
            _error.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
            stack.Children.Add(_error);

            var ok = new Button { Content = Text("Str_Vault_DlgOk", "OK"), Padding = new Thickness(18, 6, 18, 6), IsDefault = false };
            ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = Text("Str_Vault_Cancel", "Cancel"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            cancel.Click += (_, _) => { Result = null; Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);
            Content = stack;

            Loaded += (_, _) => _boxes[0].Focus();
            Closed += (_, _) => { foreach (var b in _boxes) b.Clear(); };
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void Accept()
        {
            var values = _boxes.Select(ReadSecure).ToArray();
            string? problem = null;
            if (_lastTwoMustMatch)
            {
                var a = values[^2];
                var b = values[^1];
                if (!a.AsSpan().SequenceEqual(b)) problem = Text("Str_Vault_NoMatch", "The passwords don't match.");
                else if (a.Length < 10) problem = Text("Str_Vault_TooShort", "Use at least 10 characters.");
                else if (_checkStrengthOfFirstNew && PasswordVaultManager.EstimateBits(a) < PasswordVaultManager.WeakBelowBits &&
                         AppModal.Show(this, Text("Str_Vault_WeakWarn", "This password is weak and could be guessed. Use it anyway?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    problem = "";
            }
            if (problem != null)
            {
                foreach (var v in values) Wipe(v);
                _error.Text = problem;
                _error.Visibility = problem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            Result = values;
            Close();
        }

        /// <summary>Reads a PasswordBox into a new char[] (via its SecureString).</summary>
        public static char[] ReadSecure(PasswordBox box)
        {
            using SecureString secure = box.SecurePassword;
            IntPtr ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
            try
            {
                char[] chars = new char[secure.Length];
                Marshal.Copy(ptr, chars, 0, chars.Length);
                return chars;
            }
            finally
            {
                Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }

        public static void Wipe(char[]? chars)
        {
            if (chars != null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));
        }
    }
}
