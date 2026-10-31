using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The Windows-only half of <see cref="ShortcutManager"/>: global hotkeys on a hidden message-only
    /// window (RegisterHotKey / WM_HOTKEY), turning key events into <see cref="KeyChord"/>s, localized titles
    /// and the "start with Windows" entry.
    /// </summary>
    public static partial class ShortcutManager
    {
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SmartTools";
        private const string TrayArgument = "--tray";

        private static readonly Dictionary<int, ShortcutBinding> Registered = new();
        private static HwndSource? _source;
        private static int _nextId = 0xB000;

        /// <summary>True while a key box in Settings is waiting for a new key: shortcuts (even global ones) must not fire then.</summary>
        public static bool IsRecording { get; private set; }

        /// <summary>The start-up argument that opens the app straight into the tray.</summary>
        public static string StartInTrayArgument => TrayArgument;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // =================================================================
        //  Global hotkeys
        // =================================================================

        /// <summary>Creates the hidden window and registers the global keys. Call once, on the UI thread, at start-up.</summary>
        public static void StartGlobalHotkeys()
        {
            if (_source != null) return;
            try
            {
                var parameters = new HwndSourceParameters("SmartToolsHotkeys")
                {
                    ParentWindow = new IntPtr(-3),   // HWND_MESSAGE: no visible window, only receives messages
                    WindowStyle = 0,
                };
                _source = new HwndSource(parameters);
                _source.AddHook(WndProc);
            }
            catch (Exception ex)
            {
                Log.Info($"Global hotkeys unavailable: {ex.Message}");
                _source = null;
                return;
            }

            RefreshTitles();
            Revalidate();   // registers the keys
        }

        /// <summary>Releases every global key. Call when the app exits.</summary>
        public static void Shutdown()
        {
            UnregisterAll();
            _source?.RemoveHook(WndProc);
            _source?.Dispose();
            _source = null;
        }

        /// <summary>Settings is waiting for a key: let go of the global ones so the key reaches the box.</summary>
        public static void BeginRecording()
        {
            IsRecording = true;
            UnregisterAll();
        }

        public static void EndRecording()
        {
            IsRecording = false;
            ApplyGlobalHotkeys();
        }

        private static void UnregisterAll()
        {
            if (_source == null) return;
            foreach (var id in Registered.Keys) UnregisterHotKey(_source.Handle, id);
            Registered.Clear();
            // RegisterHotKey ids for a window must stay in 0x0000-0xBFFF; every re-apply used to keep counting up.
            _nextId = 0xB000;
        }

        /// <summary>(Re)registers every enabled global key; one another program already owns gets a warning, never an exception.</summary>
        internal static void ApplyGlobalHotkeys()
        {
            if (_source == null) return;
            UnregisterAll();
            if (IsRecording || !Modules.AppSettingsStore.GlobalHotkeysEnabled) return;

            foreach (var b in Bindings)
            {
                if (b.Action.Scope != ShortcutScope.Global || !b.Enabled || b.Chord is not { HasKey: true } chord) continue;
                if (!Enum.TryParse<Key>(chord.Key, out var key)) continue;

                uint mods = (uint)chord.Modifiers | ModNoRepeat;
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                int id = _nextId++;
                if (RegisterHotKey(_source.Handle, id, mods, vk)) Registered[id] = b;
                else b.Warning = RegistrationFailedMark;
            }
        }

        /// <summary>Called when the master switch in Settings changes.</summary>
        public static void SetGlobalHotkeysEnabled(bool enabled)
        {
            Modules.AppSettingsStore.GlobalHotkeysEnabled = enabled;
            Modules.AppSettingsStore.Save();
            Revalidate();
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && Registered.TryGetValue(wParam.ToInt32(), out var binding))
            {
                handled = true;
                Invoke(binding);
            }
            return IntPtr.Zero;
        }

        // =================================================================
        //  Key events
        // =================================================================

        private static Key Resolve(KeyEventArgs e) => e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            var k => k,
        };

        /// <summary>The chord a key event stands for, or null for a bare modifier / unusable key.</summary>
        public static KeyChord? FromKeyEvent(KeyEventArgs e)
        {
            var key = Resolve(e);
            if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Capital or Key.NumLock
                or Key.Scroll or Key.Apps or Key.DeadCharProcessed or Key.ImeProcessed) return null;

            var mods = ChordModifiers.None;
            var kb = Keyboard.Modifiers;
            if (kb.HasFlag(ModifierKeys.Control)) mods |= ChordModifiers.Ctrl;
            if (kb.HasFlag(ModifierKeys.Alt)) mods |= ChordModifiers.Alt;
            if (kb.HasFlag(ModifierKeys.Shift)) mods |= ChordModifiers.Shift;
            if (kb.HasFlag(ModifierKeys.Windows)) mods |= ChordModifiers.Win;
            return new KeyChord(mods, key.ToString());
        }

        /// <summary>
        /// For the window's PreviewKeyDown: runs the in-app action on this key for the page shown.
        /// Plain letters and the like are left alone while a text box has the focus.
        /// </summary>
        public static bool TryHandle(KeyEventArgs e, string? currentToolId)
        {
            if (IsRecording || FromKeyEvent(e) is not { } chord) return false;

            var key = Resolve(e);
            bool functionKey = key is >= Key.F1 and <= Key.F24;
            bool hasCommandModifier = (chord.Modifiers & (ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Win)) != 0;
            if (!functionKey && !hasCommandModifier
                && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox)
                return false;

            return TryHandle(chord, currentToolId);
        }

        // =================================================================
        //  Titles (current language)
        // =================================================================

        private static string Str(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Fills each binding's title and group in the current language. Call when Settings opens.</summary>
        public static void RefreshTitles()
        {
            string openFormat = Str("Str_Shortcut_OpenTool", "Open {0}");
            string groupApp = Str("Str_Shortcuts_GroupApp", "Smart Tools");
            string groupOpen = Str("Str_Shortcuts_GroupOpen", "Open a tool");

            foreach (var b in Bindings)
            {
                var a = b.Action;
                if (a.Id.StartsWith(ToolActionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // The tool's name in the active language (FallbackTitle was captured when the action was defined).
                    string toolName = ToolCatalogManager.Find(a.Id[ToolActionPrefix.Length..])?.Name ?? a.FallbackTitle;
                    b.Title = string.Format(openFormat, toolName);
                    b.GroupTitle = groupOpen;
                    b.SortKey = "1";
                }
                else if (a.OwnerToolId == null)
                {
                    b.Title = Str(a.TitleKey, a.FallbackTitle);
                    b.GroupTitle = groupApp;
                    b.SortKey = "0";
                }
                else
                {
                    string tool = ToolCatalogManager.Find(a.OwnerToolId)?.Name ?? a.OwnerToolId;
                    b.Title = Str(a.TitleKey, a.FallbackTitle);
                    b.GroupTitle = tool;
                    b.SortKey = "2" + tool;
                }
            }
        }

        // =================================================================
        //  Start with Windows
        // =================================================================

        /// <summary>Adds or removes the Windows start-up entry (current user only). Starts into the tray.</summary>
        public static void ApplyStartWithWindows(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return;
                if (enabled && Environment.ProcessPath is { } exe)
                    key.SetValue(RunValueName, $"\"{exe}\" {TrayArgument}");
                else
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Info($"Start with Windows not changed: {ex.Message}");
            }
        }
    }
}
