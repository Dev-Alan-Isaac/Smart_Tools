using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Models
{
    /// <summary>Modifier keys of a <see cref="KeyChord"/>. The values match the Win32 MOD_* flags used by RegisterHotKey.</summary>
    [Flags]
    public enum ChordModifiers
    {
        None = 0,
        Alt = 1,
        Ctrl = 2,
        Shift = 4,
        Win = 8,
    }

    /// <summary>Where a shortcut works: only while Smart Tools is in front, or from anywhere in Windows.</summary>
    public enum ShortcutScope
    {
        InApp,
        Global,
    }

    /// <summary>
    /// A key plus modifiers, e.g. "Ctrl+Alt+C". The key is a WPF <c>Key</c> name kept as text, so this
    /// model stays free of WPF types; it is saved and shown as the same text.
    /// </summary>
    public readonly record struct KeyChord(ChordModifiers Modifiers, string Key)
    {
        public bool HasKey => !string.IsNullOrEmpty(Key);

        public override string ToString()
        {
            if (!HasKey) return "";
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(ChordModifiers.Ctrl)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ChordModifiers.Win)) parts.Add("Win");
            parts.Add(Key);
            return string.Join("+", parts);
        }

        /// <summary>Reads "Ctrl+Alt+C"; null for empty or unreadable text (a bad saved value just means "no shortcut").</summary>
        public static KeyChord? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var mods = ChordModifiers.None;
            string? key = null;
            foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl" or "control": mods |= ChordModifiers.Ctrl; break;
                    case "alt": mods |= ChordModifiers.Alt; break;
                    case "shift": mods |= ChordModifiers.Shift; break;
                    case "win" or "windows": mods |= ChordModifiers.Win; break;
                    default:
                        if (key != null) return null;   // two non-modifier keys
                        key = raw;
                        break;
                }
            }
            return key == null ? null : new KeyChord(mods, key);
        }
    }

    /// <summary>One thing a shortcut can do (open a tool, start a capture, ...). Ids are stable: they are what's saved.</summary>
    public sealed record ShortcutAction(
        string Id,
        string TitleKey,
        string FallbackTitle,
        ShortcutScope Scope,
        string? DefaultChord = null,
        string? OwnerToolId = null);

    /// <summary>An action with its current key, as listed in Settings.</summary>
    public sealed class ShortcutBinding : INotifyPropertyChanged
    {
        private KeyChord? _chord;
        private bool _enabled = true;
        private string? _warning;

        public ShortcutBinding(ShortcutAction action) => Action = action;

        private string _title = "";
        private string _groupTitle = "";

        public ShortcutAction Action { get; }

        /// <summary>Display texts, filled in the current language by ShortcutManager.RefreshTitles.</summary>
        [JsonIgnore]
        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                _title = value;
                OnChanged(nameof(Title));
            }
        }

        [JsonIgnore]
        public string GroupTitle
        {
            get => _groupTitle;
            set
            {
                if (_groupTitle == value) return;
                _groupTitle = value;
                OnChanged(nameof(GroupTitle));
            }
        }

        /// <summary>Orders the list: app actions, then "open a tool", then each tool's own actions.</summary>
        [JsonIgnore] public string SortKey { get; set; } = "";

        public KeyChord? Chord
        {
            get => _chord;
            set
            {
                if (_chord == value) return;
                _chord = value;
                OnChanged(nameof(Chord));
                OnChanged(nameof(ChordText));
                OnChanged(nameof(HasChord));
                OnChanged(nameof(IsModified));
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                OnChanged(nameof(Enabled));
            }
        }

        /// <summary>Why this shortcut doesn't work right now (clashes with another one, or Windows/another program owns the key).</summary>
        [JsonIgnore]
        public string? Warning
        {
            get => _warning;
            set
            {
                if (_warning == value) return;
                _warning = value;
                OnChanged(nameof(Warning));
                OnChanged(nameof(HasWarning));
            }
        }

        [JsonIgnore] public bool HasWarning => !string.IsNullOrEmpty(_warning);
        [JsonIgnore] public bool HasChord => _chord is { HasKey: true };
        [JsonIgnore] public string ChordText => _chord?.ToString() ?? "";
        [JsonIgnore] public bool IsModified => _chord?.ToString() != (KeyChord.Parse(Action.DefaultChord)?.ToString());
        [JsonIgnore] public bool IsGlobal => Action.Scope == ShortcutScope.Global;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
