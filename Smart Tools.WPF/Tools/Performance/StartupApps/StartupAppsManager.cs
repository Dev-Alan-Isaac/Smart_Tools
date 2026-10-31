using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Startup Apps - finds the programs that start with Windows (Run keys and Startup folders), turns them on or off
    /// the same way Task Manager does, and removes them. Nothing is stored by this app. Entries for all users can only
    /// be changed as administrator.
    /// </summary>
    public sealed partial class StartupAppsManager
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

        /// <summary>Reads every startup entry. Safe to call off the UI thread.</summary>
        public List<StartupApp> Scan()
        {
            var apps = new List<StartupApp>();
            ScanRunKey(apps, Registry.CurrentUser, RunPath, "Run", StartupLocation.UserRegistry);
            ScanRunKey(apps, Registry.LocalMachine, RunPath, "Run", StartupLocation.MachineRegistry);
            ScanRunKey(apps, Registry.LocalMachine, Run32Path, "Run32", StartupLocation.MachineRegistry32);
            ScanFolder(apps, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserFolder);
            ScanFolder(apps, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonFolder);
            return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void ScanRunKey(List<StartupApp> apps, RegistryKey hive, string path, string approvedName, StartupLocation location)
        {
            try
            {
                using var run = hive.OpenSubKey(path);
                if (run == null) return;
                using var approved = hive.OpenSubKey($@"{ApprovedPath}\{approvedName}");
                foreach (string name in run.GetValueNames())
                {
                    if (name.Length == 0 || run.GetValue(name) is not string command) continue;
                    string file = FindProgram(command);
                    apps.Add(new StartupApp
                    {
                        Location = location,
                        SourceName = name,
                        Name = name,
                        Command = command,
                        FilePath = file,
                        Publisher = PublisherOf(file),
                        FileMissing = file.Length == 0 || !File.Exists(file),
                        IsEnabled = IsApprovedEnabled(approved?.GetValue(name) as byte[]),
                    });
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // A key we can't read is skipped; the rest of the list is still useful.
            }
        }

        private static void ScanFolder(List<StartupApp> apps, string folder, StartupLocation location)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                var hive = location == StartupLocation.UserFolder ? Registry.CurrentUser : Registry.LocalMachine;
                using var approved = hive.OpenSubKey($@"{ApprovedPath}\StartupFolder");
                foreach (string path in Directory.EnumerateFiles(folder))
                {
                    string fileName = Path.GetFileName(path);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(path) : path;
                    apps.Add(new StartupApp
                    {
                        Location = location,
                        SourceName = fileName,
                        Name = Path.GetFileNameWithoutExtension(fileName),
                        Command = path,
                        FilePath = target,
                        Publisher = PublisherOf(target),
                        FileMissing = target.Length == 0 || !File.Exists(target),
                        IsEnabled = IsApprovedEnabled(approved?.GetValue(fileName) as byte[]),
                    });
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        /// <summary>The program file a Run command starts, or "" when none of its possible paths exists.</summary>
        private static string FindProgram(string command)
        {
            foreach (string candidate in CommandCandidates(command))
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A path with illegal characters is just not a match.
                }
            }
            return "";
        }

        private static string PublisherOf(string file)
        {
            if (file.Length == 0 || !File.Exists(file)) return "";
            try
            {
                return FileVersionInfo.GetVersionInfo(file).CompanyName?.Trim() ?? "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }

        /// <summary>Where a .lnk points, through the Windows scripting host. "" when it can't be read.</summary>
        private static string ShortcutTarget(string shortcut)
        {
            object? shell = null, link = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return "";
                shell = Activator.CreateInstance(type);
                dynamic wsh = shell!;
                link = wsh.CreateShortcut(shortcut);
                dynamic lnk = link!;
                return Environment.ExpandEnvironmentVariables((string?)lnk.TargetPath ?? "");
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return "";
            }
            finally
            {
                if (link != null) Marshal.ReleaseComObject(link);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        // =================================================================
        //  Changes
        // =================================================================

        /// <summary>Turns an entry on or off (the flag Task Manager reads), without touching the entry itself.</summary>
        public StartupChangeResult SetEnabled(StartupApp app, bool enabled)
        {
            try
            {
                using var key = ApprovedKey(app, writable: true);
                key.SetValue(app.SourceName, ApprovedFlag(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
                return StartupChangeResult.Done;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                return StartupChangeResult.NeedsAdministrator;
            }
            catch (IOException)
            {
                return StartupChangeResult.Failed;
            }
        }

        /// <summary>Deletes the entry (the Run value or the Startup folder file) and its on/off flag.</summary>
        public StartupChangeResult Remove(StartupApp app)
        {
            try
            {
                if (app.IsFolderItem)
                    File.Delete(app.Command);
                else
                {
                    var (hive, path, _) = RunLocation(app.Location);
                    using var run = hive.OpenSubKey(path, writable: true);
                    run?.DeleteValue(app.SourceName, throwOnMissingValue: false);
                }
                using var approved = ApprovedKey(app, writable: true);
                approved.DeleteValue(app.SourceName, throwOnMissingValue: false);
                return StartupChangeResult.Done;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                return StartupChangeResult.NeedsAdministrator;
            }
            catch (IOException)
            {
                return StartupChangeResult.Failed;
            }
        }

        /// <summary>Opens Explorer with the program (or the shortcut) selected.</summary>
        public static void ShowInFolder(StartupApp app)
        {
            string file = app.FilePath.Length > 0 && File.Exists(app.FilePath) ? app.FilePath : app.IsFolderItem ? app.Command : "";
            if (file.Length == 0 || !File.Exists(file)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        private static (RegistryKey Hive, string Path, string ApprovedName) RunLocation(StartupLocation location) => location switch
        {
            StartupLocation.UserRegistry => (Registry.CurrentUser, RunPath, "Run"),
            StartupLocation.MachineRegistry => (Registry.LocalMachine, RunPath, "Run"),
            StartupLocation.MachineRegistry32 => (Registry.LocalMachine, Run32Path, "Run32"),
            StartupLocation.UserFolder => (Registry.CurrentUser, "", "StartupFolder"),
            _ => (Registry.LocalMachine, "", "StartupFolder"),
        };

        private static RegistryKey ApprovedKey(StartupApp app, bool writable)
        {
            var (hive, _, approvedName) = RunLocation(app.Location);
            return hive.CreateSubKey($@"{ApprovedPath}\{approvedName}", writable)
                   ?? throw new IOException("The startup settings could not be opened.");
        }
    }
}
