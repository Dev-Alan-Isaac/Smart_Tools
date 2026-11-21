using Smart_Tools.WPF.Controllers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// An in-window modal asking for one or more passwords (change master password, backup password, ...).
    /// Values are read from the PasswordBoxes' SecureString into char[] the caller must wipe
    /// (<see cref="Wipe"/>); a plain string of the password is never made here.
    /// Use: <c>var d = new VaultPasswordDialog(...); d.ShowDialog(); var values = d.Result;</c>
    /// </summary>
    internal sealed class VaultPasswordDialog
    {
        private readonly string _title;
        private readonly string _intro;
        private readonly IReadOnlyList<string> _labels;
        private readonly List<PasswordBox> _boxes = new();
        private readonly TextBlock _error = new();
        private readonly bool _lastTwoMustMatch;
        private readonly bool _checkStrengthOfFirstNew;
        private Action? _close;

        /// <summary>One char[] per field, in order, or null if cancelled.</summary>
        public char[][]? Result { get; private set; }

        /// <param name="lastTwoMustMatch">The last two fields are "new" and "confirm": they must be equal, long enough and not weak (a weak one asks first).</param>
        public VaultPasswordDialog(string title, string intro, IReadOnlyList<string> labels, bool lastTwoMustMatch)
        {
            _title = title;
            _intro = intro;
            _labels = labels;
            _lastTwoMustMatch = lastTwoMustMatch;
            _checkStrengthOfFirstNew = lastTwoMustMatch;
        }

        /// <summary>Shows the dialog and waits for the answer (Result is null when cancelled).</summary>
        public void ShowDialog()
        {
            Result = null;
            bool shown = AppModal.Present(null, close =>
            {
                _close = close;
                _boxes.Clear();
                var stack = new StackPanel();
                stack.Children.Add(AppModal.Heading(_title, 6));
                if (_intro.Length > 0)
                {
                    var t = new TextBlock { Text = _intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    stack.Children.Add(t);
                }
                foreach (string label in _labels)
                {
                    var l = new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = 12 };
                    l.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                    var box = new PasswordBox();
                    box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
                    _boxes.Add(box);
                    stack.Children.Add(l);
                    stack.Children.Add(box);
                }

                _error.Margin = new Thickness(0, 10, 0, 0);
                _error.TextWrapping = TextWrapping.Wrap;
                _error.Visibility = Visibility.Collapsed;
                _error.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                stack.Children.Add(_error);

                var ok = AppModal.MakeButton(AppModal.Text("Str_Vault_DlgOk", "OK"), primary: true);
                ok.Click += (_, _) => Accept();
                var cancel = AppModal.MakeButton(AppModal.Text("Str_Vault_Cancel", "Cancel"), primary: false);
                cancel.Click += (_, _) => Finish();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
                buttons.Children.Add(cancel);
                buttons.Children.Add(ok);
                stack.Children.Add(buttons);

                AppModal.CancelOnEscape(stack, Finish);
                return (stack, _boxes[0]);
            }, 400, 520);
            if (!shown) Smart_Tools.WPF.Log.Info($"Password dialog '{_title}' could not be shown.");
        }

        /// <summary>Closes the card and clears the boxes (the typed characters are not kept in the controls).</summary>
        private void Finish()
        {
            foreach (var b in _boxes) b.Clear();
            _close?.Invoke();
        }

        private void Accept()
        {
            var values = _boxes.Select(ReadSecure).ToArray();
            string? problem = null;
            if (_lastTwoMustMatch)
            {
                var a = values[^2];
                var b = values[^1];
                if (!a.AsSpan().SequenceEqual(b)) problem = AppModal.Text("Str_Vault_NoMatch", "The passwords don't match.");
                else if (a.Length < 10) problem = AppModal.Text("Str_Vault_TooShort", "Use at least 10 characters.");
                else if (_checkStrengthOfFirstNew && PasswordVaultManager.EstimateBits(a) < PasswordVaultManager.WeakBelowBits &&
                         AppModal.Show(AppModal.Text("Str_Vault_WeakWarn", "This password is weak and could be guessed. Use it anyway?"), _title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
            Finish();
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
