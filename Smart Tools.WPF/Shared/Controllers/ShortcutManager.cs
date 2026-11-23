using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Collections.ObjectModel;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

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
            Define(new ShortcutAction("notes.widget", "Str_Shortcut_NotesWidget", "Show or close the note as a widget", ShortcutScope.InApp, "Ctrl+Shift+W", "notes"));
            Define(new ShortcutAction("password.generateAlt", "Str_Shortcut_PasswordGenerate", "Generate passwords", ShortcutScope.InApp, "Ctrl+G", "password-generator"));
            Define(new ShortcutAction("vault.lock", "Str_Shortcut_VaultLock", "Lock the vault", ShortcutScope.InApp, "Ctrl+Shift+L", "password-vault"));
            // No default key: a global key that Windows or another app already uses would just fail to register. Set one in Settings.
            Define(new ShortcutAction(ScreenshotCaptureActionId, "Str_Shortcut_ScreenshotCapture", "Take a screenshot", ShortcutScope.Global, null, "screenshot"));
            Define(new ShortcutAction("ocr.capture", "Str_Shortcut_OcrCapture", "Capture a screen area", ShortcutScope.InApp, "Ctrl+Shift+X", "screen-ocr"));
            Define(new ShortcutAction("dup.collapseAll", "Str_Shortcut_GroupsCollapseAll", "Fold every group", ShortcutScope.InApp, "Ctrl+Shift+Left", "duplicate-data"));
            Define(new ShortcutAction("dup.expandAll", "Str_Shortcut_GroupsExpandAll", "Unfold every group", ShortcutScope.InApp, "Ctrl+Shift+Right", "duplicate-data"));
            Define(new ShortcutAction("dup.nextGroup", "Str_Shortcut_GroupsNextGroup", "Go to the next group", ShortcutScope.InApp, "Ctrl+Down", "duplicate-data"));
            Define(new ShortcutAction("dup.prevGroup", "Str_Shortcut_GroupsPrevGroup", "Go to the previous group", ShortcutScope.InApp, "Ctrl+Up", "duplicate-data"));
            Define(new ShortcutAction("dup.deleteMarked", "Str_Shortcut_GroupsDeleteMarked", "Delete the marked files", ShortcutScope.InApp, "Ctrl+Delete", "duplicate-data"));
            Define(new ShortcutAction("similar.collapseAll", "Str_Shortcut_GroupsCollapseAll", "Fold every group", ShortcutScope.InApp, "Ctrl+Shift+Left", "find-similar"));
            Define(new ShortcutAction("similar.expandAll", "Str_Shortcut_GroupsExpandAll", "Unfold every group", ShortcutScope.InApp, "Ctrl+Shift+Right", "find-similar"));
            Define(new ShortcutAction("similar.nextGroup", "Str_Shortcut_GroupsNextGroup", "Go to the next group", ShortcutScope.InApp, "Ctrl+Down", "find-similar"));
            Define(new ShortcutAction("similar.prevGroup", "Str_Shortcut_GroupsPrevGroup", "Go to the previous group", ShortcutScope.InApp, "Ctrl+Up", "find-similar"));
            Define(new ShortcutAction("similar.deleteMarked", "Str_Shortcut_GroupsDeleteMarked", "Delete the marked files", ShortcutScope.InApp, "Ctrl+Delete", "find-similar"));

            DefineCommon();

            Revalidate();
        }

        // =================================================================
        //  Common actions: the same key in every tool that has the action
        // =================================================================

        public const string CommonPrefix = "common.";
        public const string RunId = "common.run", StopId = "common.stop", SaveId = "common.save", OpenId = "common.open",
            PlayPauseId = "common.playPause", MuteId = "common.mute", TheaterId = "common.theater";

        // id, title key, fallback title, default key, tools that can give it their own key (the others just follow the common one)
        private static readonly (string Id, string TitleKey, string Fallback, string Key, string[] Tools)[] CommonDefs =
        {
            (RunId, "Str_Shortcut_CommonRun", "Start / run / scan / generate", "F5", new[] { "password-generator", "regex-tester", "duplicate-data", "find-similar", "audio-editor" }),
            (StopId, "Str_Shortcut_CommonStop", "Cancel / stop", "Escape", Array.Empty<string>()),
            (SaveId, "Str_Shortcut_CommonSave", "Save / export", "Ctrl+S", Array.Empty<string>()),
            (OpenId, "Str_Shortcut_CommonOpen", "Open a file", "Ctrl+O", new[] { "screen-ocr" }),
            (PlayPauseId, "Str_Shortcut_CommonPlayPause", "Play / pause", "Space", new[] { "media-viewer", "video-editor", "audio-editor", "metronome", "midi-playalong" }),
            (MuteId, "Str_Shortcut_CommonMute", "Mute / unmute", "M", new[] { "media-viewer", "video-editor" }),
            (TheaterId, "Str_Shortcut_CommonTheater", "Theater (full view)", "T", new[] { "media-viewer" }),
        };

        // Keys these actions had before they became common: copied to the tool's own override so nothing a user set is lost.
        private static readonly (string OldId, string NewId)[] OldIds =
        {
            ("password.generate", RunId + "@password-generator"), ("regex.run", RunId + "@regex-tester"),
            ("dup.scan", RunId + "@duplicate-data"), ("similar.scan", RunId + "@find-similar"),
            ("loudness.measure", RunId + "@audio-editor"), ("ocr.open", OpenId + "@screen-ocr"),
        };

        private static string OverrideId(string commonId, string toolId) => commonId + "@" + toolId;

        private static void DefineCommon()
        {
            bool changed = false;
            foreach (var (oldId, newId) in OldIds)
            {
                // A key the user cleared or switched off must stay off in that tool: the tool's override is then switched off
                // (EffectiveChord reads that as "no key here"), instead of falling back to the common key.
                if (AppSettingsStore.Shortcuts.Remove(oldId, out var key))
                {
                    if (string.IsNullOrWhiteSpace(key) || KeyChord.Parse(key) is not { HasKey: true }) AppSettingsStore.DisabledShortcuts.Add(newId);
                    else AppSettingsStore.Shortcuts[newId] = key;
                    changed = true;
                }
                if (AppSettingsStore.DisabledShortcuts.Remove(oldId)) { AppSettingsStore.DisabledShortcuts.Add(newId); changed = true; }
            }
            if (changed) AppSettingsStore.Save();

            foreach (var c in CommonDefs)
            {
                Define(new ShortcutAction(c.Id, c.TitleKey, c.Fallback, ShortcutScope.InApp, c.Key));
                foreach (var tool in c.Tools)
                    Define(new ShortcutAction(OverrideId(c.Id, tool), c.TitleKey, c.Fallback, ShortcutScope.InApp, null, tool));
            }
        }

        private static readonly Dictionary<(string Tool, string Id), Action> CommonHandlers = new();

        /// <summary>A tool makes a common action do its own thing (the tool id is the catalog id).</summary>
        public static void AttachCommon(string toolId, string commonId, Action handler) => CommonHandlers[(toolId, commonId)] = handler;

        /// <summary>The key a common action has in this tool: the tool's own key if it set one, else the common key; null when off (the tool's override switched off turns the key off in that tool only).</summary>
        public static KeyChord? EffectiveChord(string commonId, string? toolId)
        {
            if (toolId != null && ById.TryGetValue(OverrideId(commonId, toolId), out var own))
            {
                if (!own.Enabled) return null;   // switched off for this tool
                if (own.Chord is { HasKey: true } oc) return oc;
            }
            return ById.TryGetValue(commonId, out var b) && b.Enabled && b.Chord is { HasKey: true } c ? c : null;
        }

        public static bool IsCommon(string commonId, KeyChord chord, string? toolId) =>
            EffectiveChord(commonId, toolId) is { } k && string.Equals(k.ToString(), chord.ToString(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// For a view's own key handler: which common action (<see cref="PlayPauseId"/>, <see cref="MuteId"/>, ...) this key stands for in the tool, or null.
        /// The view keeps its own focus checks and runs the action itself.
        /// </summary>
        public static string? CommonAction(KeyEventArgs e, string toolId)
        {
            if (IsRecording || FromKeyEvent(e) is not { } chord) return null;
            foreach (var c in CommonDefs)
                if (IsCommon(c.Id, chord, toolId)) return c.Id;
            return null;
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
                if (b.Action.Id.StartsWith(CommonPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (InvokeCommon(b, currentToolId)) return true;
                    continue;
                }
                if (Invoke(b)) return true;
            }
            return false;
        }

        /// <summary>A common action (or a tool's own key for it) runs the handler the current tool attached; tools without one fall back to the shell's button search.</summary>
        private static bool InvokeCommon(ShortcutBinding b, string? currentToolId)
        {
            if (currentToolId == null) return false;
            string id = b.Action.Id;
            int at = id.IndexOf('@');
            string commonId = at < 0 ? id : id[..at];
            // The common key stays quiet in a tool that has its own key for this action.
            if (at < 0 && EffectiveChord(commonId, currentToolId) is { } eff && b.Chord is { } mine
                && !string.Equals(eff.ToString(), mine.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
            if (!CommonHandlers.TryGetValue((currentToolId, commonId), out var handler)) return false;
            try { handler(); return true; }
            catch (Exception ex) { Log.Info($"Shortcut {id}: {ex.Message}"); return false; }
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
