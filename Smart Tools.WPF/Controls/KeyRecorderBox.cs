using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A box that records a key combination: click it (or Tab to it), press the keys. Backspace/Delete clears,
    /// Esc cancels. Its DataContext is the <see cref="ShortcutBinding"/> being edited. While it has the keyboard
    /// focus no shortcut fires (<see cref="ShortcutManager.BeginRecording"/>), so pressing a key that is already
    /// a shortcut records it instead of running it.
    /// </summary>
    public class KeyRecorderBox : Button
    {
        public static readonly RoutedEvent ChordChangedEvent = EventManager.RegisterRoutedEvent(
            nameof(ChordChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(KeyRecorderBox));

        public static readonly RoutedEvent ChordRejectedEvent = EventManager.RegisterRoutedEvent(
            nameof(ChordRejected), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(KeyRecorderBox));

        private ShortcutBinding? _binding;
        private bool _recording;

        public event RoutedEventHandler ChordChanged
        {
            add => AddHandler(ChordChangedEvent, value);
            remove => RemoveHandler(ChordChangedEvent, value);
        }

        public event RoutedEventHandler ChordRejected
        {
            add => AddHandler(ChordRejectedEvent, value);
            remove => RemoveHandler(ChordRejectedEvent, value);
        }

        /// <summary>Why the last key was refused: "reserved" or "needsModifier" (null when it was accepted).</summary>
        public string? RejectCode { get; private set; }

        public KeyRecorderBox()
        {
            DataContextChanged += (_, _) => Attach(DataContext as ShortcutBinding);
            Unloaded += (_, _) =>
            {
                if (_recording) StopRecording();
            };
        }

        private void Attach(ShortcutBinding? binding)
        {
            if (_binding != null) _binding.PropertyChanged -= Binding_PropertyChanged;
            _binding = binding;
            if (_binding != null) _binding.PropertyChanged += Binding_PropertyChanged;
            ShowText();
        }

        private void Binding_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShortcutBinding.ChordText)) ShowText();
        }

        private static string Str(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        private void ShowText()
        {
            if (_recording)
            {
                Content = Str("Str_Shortcuts_Recording", "Press the keys...");
                return;
            }
            Content = _binding is { HasChord: true } b ? b.ChordText : Str("Str_Shortcuts_None", "Not set");
            Opacity = _binding is { HasChord: true } ? 1 : 0.6;
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            if (_recording) return;
            _recording = true;
            ShortcutManager.BeginRecording();
            Opacity = 1;
            ShowText();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            StopRecording();
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            ShortcutManager.EndRecording();
            ShowText();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!_recording || _binding == null)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;

            // Plain Tab still moves on; everything else is recorded.
            if (key == Key.Tab && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return;

            e.Handled = true;

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;   // wait for the real key

            if (key == Key.Escape && mods == ModifierKeys.None)
            {
                Keyboard.ClearFocus();
                return;
            }

            KeyChord? chord;
            if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) chord = null;
            else if (ShortcutManager.FromKeyEvent(e) is { } recorded) chord = recorded;
            else return;

            RejectCode = ShortcutManager.SetChord(_binding, chord);
            RaiseEvent(new RoutedEventArgs(RejectCode == null ? ChordChangedEvent : ChordRejectedEvent, this));
            if (RejectCode == null) Keyboard.ClearFocus();
        }
    }
}
