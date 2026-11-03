using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Collections.ObjectModel;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Keyboard shortcuts for the whole app: one list of actions (open a tool, a tool's own keys, ...),
    /// each with a key the user can change in Settings. Views attach a handler to an action id instead of
    /// checking keys themselves. Global actions work from anywhere in Windows (RegisterHotKey, in
    /// ShortcutManager.Windows.cs); in-app actions work while Smart Tools is in front.
    /// Only keys that differ from the default are saved, by action id - nothing else.
    /// </summary>
    public static partial class ShortcutManager
    {
        public const string ToolActionPrefix = "tool.open:";
        public const string SearchActionId = "shell.search";
        public const string ToggleWindowActionId = "shell.toggleWindow";
        public const string ScreenshotCaptureActionId = "screenshot.capture";

        private static readonly Dictionary<string, ShortcutBinding> ById = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Action> Handlers = new(StringComparer.OrdinalIgnoreCase);

        // Chords Windows keeps for itself (or that would make closing/switching programs unusable).
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "Ctrl+Alt+Delete", "Alt+F4", "Alt+Tab", "Ctrl+Shift+Escape", "Ctrl+Escape", "Alt+Escape", "Win+L",
        };

        /// <summary>Every known action, in display order (what Settings lists).</summary>
        public static ObservableCollection<ShortcutBinding> Bindings { get; } = new();

        /// <summary>Called when a <c>tool.open:*</c> action fires, with the tool id.</summary>
        public static Action<string>? OpenToolRequested { get; set; }

        static ShortcutManager()
        {
            Define(new ShortcutAction(SearchActionId, "Str_Shortcut_Search", "Search the tools", ShortcutScope.InApp, "Ctrl+K"));
            Define(new ShortcutAction(ToggleWindowActionId, "Str_Shortcut_ToggleWindow", "Show or hide Smart Tools", ShortcutScope.Global));

            foreach (var tool in ToolCatalogManager.Tools)
                Define(new ShortcutAction(ToolActionPrefix + tool.Id, "", tool.Name, ShortcutScope.Global));

            // Keys inside a tool. Views attach the handler with Attach(id, ...).
            Define(new ShortcutAction("notes.new", "Str_Shortcut_NotesNew", "New note", ShortcutScope.InApp, "Ctrl+N", "notes"));
            Define(new ShortcutAction("notes.search", "Str_Shortcut_NotesSearch", "Search notes", ShortcutScope.InApp, "Ctrl+F", "notes"));
            Define(new ShortcutAction("password.generate", "Str_Shortcut_PasswordGenerate", "Generate passwords", ShortcutScope.InApp, "F5", "password-generator"));
            Define(new ShortcutAction("password.generateAlt", "Str_Shortcut_PasswordGenerate", "Generate passwords", ShortcutScope.InApp, "Ctrl+G", "password-generator"));
            Define(new ShortcutAction("vault.lock", "Str_Shortcut_VaultLock", "Lock the vault", ShortcutScope.InApp, "Ctrl+Shift+L", "password-vault"));
            Define(new ShortcutAction("regex.run", "Str_Shortcut_RegexRun", "Run the test", ShortcutScope.InApp, "F5", "regex-tester"));
            // No default key: a global key that Windows or another app already uses would just fail to register. Set one in Settings.
            Define(new ShortcutAction(ScreenshotCaptureActionId, "Str_Shortcut_ScreenshotCapture", "Take a screenshot", ShortcutScope.Global, null, "screenshot"));
            Define(new ShortcutAction("ocr.capture", "Str_Shortcut_OcrCapture", "Capture a screen area", ShortcutScope.InApp, "Ctrl+Shift+X", "screen-ocr"));
            Define(new ShortcutAction("ocr.open", "Str_Shortcut_OcrOpen", "Open an image", ShortcutScope.InApp, "Ctrl+O", "screen-ocr"));

            Define(new ShortcutAction("dup.scan", "Str_Shortcut_GroupsScan", "Start or stop the scan", ShortcutScope.InApp, "F5", "duplicate-data"));
            Define(new ShortcutAction("dup.collapseAll", "Str_Shortcut_GroupsCollapseAll", "Fold every group", ShortcutScope.InApp, "Ctrl+Shift+Left", "duplicate-data"));
            Define(new ShortcutAction("dup.expandAll", "Str_Shortcut_GroupsExpandAll", "Unfold every group", ShortcutScope.InApp, "Ctrl+Shift+Right", "duplicate-data"));
            Define(new ShortcutAction("dup.nextGroup", "Str_Shortcut_GroupsNextGroup", "Go to the next group", ShortcutScope.InApp, "Ctrl+Down", "duplicate-data"));
            Define(new ShortcutAction("dup.prevGroup", "Str_Shortcut_GroupsPrevGroup", "Go to the previous group", ShortcutScope.InApp, "Ctrl+Up", "duplicate-data"));
            Define(new ShortcutAction("dup.deleteMarked", "Str_Shortcut_GroupsDeleteMarked", "Delete the marked files", ShortcutScope.InApp, "Ctrl+Delete", "duplicate-data"));
            Define(new ShortcutAction("similar.scan", "Str_Shortcut_GroupsScan", "Start or stop the scan", ShortcutScope.InApp, "F5", "find-similar"));
            Define(new ShortcutAction("similar.collapseAll", "Str_Shortcut_GroupsCollapseAll", "Fold every group", ShortcutScope.InApp, "Ctrl+Shift+Left", "find-similar"));
            Define(new ShortcutAction("similar.expandAll", "Str_Shortcut_GroupsExpandAll", "Unfold every group", ShortcutScope.InApp, "Ctrl+Shift+Right", "find-similar"));
            Define(new ShortcutAction("similar.nextGroup", "Str_Shortcut_GroupsNextGroup", "Go to the next group", ShortcutScope.InApp, "Ctrl+Down", "find-similar"));
            Define(new ShortcutAction("similar.prevGroup", "Str_Shortcut_GroupsPrevGroup", "Go to the previous group", ShortcutScope.InApp, "Ctrl+Up", "find-similar"));
            Define(new ShortcutAction("similar.deleteMarked", "Str_Shortcut_GroupsDeleteMarked", "Delete the marked files", ShortcutScope.InApp, "Ctrl+Delete", "find-similar"));

            Revalidate();
        }

        private static void Define(ShortcutAction action)
        {
            var binding = new ShortcutBinding(action)
            {
                Chord = KeyChord.Parse(action.DefaultChord),
                Enabled = !AppSettingsStore.DisabledShortcuts.Contains(action.Id),
            };
            if (AppSettingsStore.Shortcuts.TryGetValue(action.Id, out var saved))
                binding.Chord = KeyChord.Parse(saved);   // "" or unreadable text = no shortcut

            ById[action.Id] = binding;
            Bindings.Add(binding);
        }

        public static ShortcutBinding? Find(string actionId) => ById.GetValueOrDefault(actionId);

        /// <summary>Makes the action do something. A view calls this once when it's created.</summary>
        public static void Attach(string actionId, Action handler) => Handlers[actionId] = handler;

        /// <summary>The current key as text ("Ctrl+N"), or "" when none - for tooltips.</summary>
        public static string ChordText(string actionId) =>
            ById.TryGetValue(actionId, out var b) && b.Enabled ? b.ChordText : "";

        // =================================================================
        //  Changing a binding (Settings)
        // =================================================================

        /// <summary>Sets a new key (null clears it). Returns an error text when the key can't be used; the binding is left alone then.</summary>
        public static string? SetChord(ShortcutBinding binding, KeyChord? chord)
        {
            if (chord is { } c)
            {
                if (!c.HasKey) chord = null;
                else if (Reserved.Contains(c.ToString())) return "reserved";
                else if (binding.Action.Scope == ShortcutScope.Global && c.Modifiers == ChordModifiers.None) return "needsModifier";
                else if (binding.Action.Scope == ShortcutScope.Global && c.Modifiers == ChordModifiers.Shift) return "needsModifier";
            }

            binding.Chord = chord;
            Persist(binding);
            Revalidate();
            return null;
        }

        public static void SetEnabled(ShortcutBinding binding, bool enabled)
        {
            binding.Enabled = enabled;
            Persist(binding);
            Revalidate();
        }

        public static void Reset(ShortcutBinding binding)
        {
            binding.Chord = KeyChord.Parse(binding.Action.DefaultChord);
            binding.Enabled = true;
            Persist(binding);
            Revalidate();
        }

        public static void ResetAll()
        {
            foreach (var b in Bindings)
            {
                b.Chord = KeyChord.Parse(b.Action.DefaultChord);
                b.Enabled = true;
                Persist(b, save: false);
            }
            AppSettingsStore.Save();
            Revalidate();
        }

        private static void Persist(ShortcutBinding b, bool save = true)
        {
            string id = b.Action.Id;
            string current = b.ChordText;
            string def = KeyChord.Parse(b.Action.DefaultChord)?.ToString() ?? "";

            if (string.Equals(current, def, StringComparison.OrdinalIgnoreCase)) AppSettingsStore.Shortcuts.Remove(id);
            else AppSettingsStore.Shortcuts[id] = current;   // "" = cleared on purpose

            if (b.Enabled) AppSettingsStore.DisabledShortcuts.Remove(id);
            else AppSettingsStore.DisabledShortcuts.Add(id);

            if (save) AppSettingsStore.Save();
        }

        // =================================================================
        //  Conflicts
        // =================================================================

        /// <summary>
        /// Marks every binding that shares its key with another one that could also fire. A global key is also
        /// taken from the app itself, so a global/in-app pair clashes too; in-app keys only clash when they could
        /// be active on the same page.
        /// </summary>
        internal static void Revalidate()
        {
            foreach (var b in Bindings) b.Warning = null;

            var active = Bindings.Where(b => b.Enabled && b.Chord is { HasKey: true }).ToList();
            foreach (var group in active.GroupBy(b => b.ChordText, StringComparer.OrdinalIgnoreCase))
            {
                var items = group.ToList();
                if (items.Count < 2) continue;
                foreach (var a in items)
                {
                    var other = items.FirstOrDefault(o => !ReferenceEquals(o, a) && CanClash(a.Action, o.Action));
                    if (other != null && a.Warning == null)
                        a.Warning = ClashMark + (other.Title.Length > 0 ? other.Title : other.Action.FallbackTitle);
                }
            }

            // Last, so "another program already uses this key" wins over a clash note.
            ApplyGlobalHotkeys();
        }

        internal const string ClashMark = "clash:";
        internal const string RegistrationFailedMark = "taken:";

        private static bool CanClash(ShortcutAction a, ShortcutAction b)
        {
            if (a.Scope == ShortcutScope.Global || b.Scope == ShortcutScope.Global) return true;
            return a.OwnerToolId == null || b.OwnerToolId == null
                   || string.Equals(a.OwnerToolId, b.OwnerToolId, StringComparison.OrdinalIgnoreCase);
        }

        // =================================================================
        //  Firing
        // =================================================================

        /// <summary>
        /// Runs the in-app action on this key, if there is one that's active on the page shown
        /// (<paramref name="currentToolId"/>). True when a handler ran.
        /// </summary>
        public static bool TryHandle(KeyChord chord, string? currentToolId)
        {
            if (IsRecording || !chord.HasKey) return false;
            string text = chord.ToString();

            foreach (var b in Bindings)
            {
                if (b.Action.Scope != ShortcutScope.InApp || !b.Enabled) continue;
                if (b.Chord is not { } c || !string.Equals(c.ToString(), text, StringComparison.OrdinalIgnoreCase)) continue;
                if (b.Action.OwnerToolId != null
                    && !string.Equals(b.Action.OwnerToolId, currentToolId, StringComparison.OrdinalIgnoreCase)) continue;
                if (Invoke(b)) return true;
            }
            return false;
        }

        private static bool Invoke(ShortcutBinding b)
        {
            try
            {
                if (Handlers.TryGetValue(b.Action.Id, out var handler))
                {
                    handler();
                    return true;
                }
                if (b.Action.Id.StartsWith(ToolActionPrefix, StringComparison.OrdinalIgnoreCase) && OpenToolRequested != null)
                {
                    OpenToolRequested(b.Action.Id[ToolActionPrefix.Length..]);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // A failing action must not take the shortcut system (or the app) down.
                Log.Info($"Shortcut {b.Action.Id}: {ex.Message}");
            }
            return false;
        }
    }
}
