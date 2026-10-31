using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Environment Variables Editor - reads and writes the user and system variables straight in the registry (the only way
    /// to keep a %REFERENCE% unexpanded and to keep REG_EXPAND_SZ), then tells Windows so new programs see the change.
    /// Nothing is stored by this app except an in-memory undo of the last changes. System variables need administrator rights.
    /// </summary>
    public sealed partial class EnvironmentVariablesManager
    {
        private const string UserKey = "Environment";
        private const string SystemKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        private const int KeepUndo = 20;

        private readonly Stack<EnvChange> _undo = new();

        public bool CanUndo => _undo.Count > 0;

        public static bool IsAdministrator
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static RegistryKey? Open(EnvScope scope, bool writable) =>
            scope == EnvScope.User ? Registry.CurrentUser.OpenSubKey(UserKey, writable) : Registry.LocalMachine.OpenSubKey(SystemKey, writable);

        /// <summary>All text variables of a scope, sorted by name. Throws nothing; an unreadable key gives an empty list.</summary>
        public List<EnvVariable> Load(EnvScope scope)
        {
            var list = new List<EnvVariable>();
            try
            {
                using var key = Open(scope, writable: false);
                if (key == null) return list;
                foreach (string name in key.GetValueNames())
                {
                    if (name.Length == 0) continue;
                    var kind = key.GetValueKind(name);
                    if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                    list.Add(new EnvVariable
                    {
                        Scope = scope,
                        Name = name,
                        Value = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "",
                        IsExpandable = kind == RegistryValueKind.ExpandString,
                    });
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // An unreadable key just shows an empty list.
            }
            return list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Creates or changes a variable. The previous value is remembered for <see cref="Undo"/>.</summary>
        public EnvChangeResult Save(EnvScope scope, string name, string value, bool expandable, out string? error)
        {
            error = null;
            if (CheckName(name) != EnvNameProblem.None || value.Length > MaxValueLength) return EnvChangeResult.Failed;
            try
            {
                using var key = Open(scope, writable: true);
                if (key == null) return EnvChangeResult.Failed;

                EnvChange before = Snapshot(key, scope, name);
                key.SetValue(name, value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                Remember(before);
                Broadcast();
                return EnvChangeResult.Done;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                return EnvChangeResult.NeedsAdministrator;
            }
            catch (IOException ex)
            {
                error = ex.Message;
                return EnvChangeResult.Failed;
            }
        }

        public EnvChangeResult Delete(EnvScope scope, string name, out string? error)
        {
            error = null;
            try
            {
                using var key = Open(scope, writable: true);
                if (key == null) return EnvChangeResult.Failed;

                EnvChange before = Snapshot(key, scope, name);
                key.DeleteValue(name, throwOnMissingValue: false);
                Remember(before);
                Broadcast();
                return EnvChangeResult.Done;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                return EnvChangeResult.NeedsAdministrator;
            }
            catch (IOException ex)
            {
                error = ex.Message;
                return EnvChangeResult.Failed;
            }
        }

        /// <summary>Puts the last changed variable back as it was (or removes it again if the change created it).</summary>
        public EnvChange? Undo(out EnvChangeResult result)
        {
            result = EnvChangeResult.Done;
            if (_undo.Count == 0) return null;
            var change = _undo.Peek();
            try
            {
                using var key = Open(change.Scope, writable: true);
                if (key == null)
                {
                    result = EnvChangeResult.Failed;
                    return change;
                }
                if (change.OldValue == null) key.DeleteValue(change.Name, throwOnMissingValue: false);
                else key.SetValue(change.Name, change.OldValue, change.OldExpandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                _undo.Pop();
                Broadcast();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                result = EnvChangeResult.NeedsAdministrator;
            }
            catch (IOException)
            {
                result = EnvChangeResult.Failed;
            }
            return change;
        }

        private static EnvChange Snapshot(RegistryKey key, EnvScope scope, string name)
        {
            if (Array.IndexOf(key.GetValueNames(), name) < 0 && key.GetValue(name) == null) return new EnvChange(scope, name, null, false);
            bool expandable = key.GetValueKind(name) == RegistryValueKind.ExpandString;
            return new EnvChange(scope, name, key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "", expandable);
        }

        private void Remember(EnvChange change)
        {
            _undo.Push(change);
            if (_undo.Count <= KeepUndo) return;
            var keep = _undo.Take(KeepUndo).Reverse().ToList();   // Stack enumerates newest first
            _undo.Clear();
            foreach (var item in keep) _undo.Push(item);
        }

        public static bool DirectoryExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        // =================================================================
        //  Telling Windows
        // =================================================================

        private const int HwndBroadcast = 0xFFFF;
        private const int WmSettingChange = 0x001A;
        private const int SmtoAbortIfHung = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

        /// <summary>Tells running programs (Explorer, new terminals) that the environment changed, so they re-read it. Programs already running keep their copy.</summary>
        private static void Broadcast() =>
            SendMessageTimeout(new IntPtr(HwndBroadcast), WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 2000, out _);
    }
}
