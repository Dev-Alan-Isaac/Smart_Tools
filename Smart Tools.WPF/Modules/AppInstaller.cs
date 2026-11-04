using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF
{
    /// <summary>How far a copy has got.</summary>
    public sealed record CopyProgress(long BytesDone, long BytesTotal, string File);

    public sealed record InstallResult(bool Ok, bool Cancelled, string Message, string? ExePath);

    /// <summary>
    /// The app installing itself, making a portable copy of itself, and uninstalling - no MSI. Everything is per user:
    /// the files go to %LocalAppData%\Programs\Smart Tools, the shortcuts to the user's Start menu and desktop, and the
    /// "Apps &amp; features" entry to HKCU, so no administrator prompt is ever needed. No WPF types here.
    /// </summary>
    public static class AppInstaller
    {
        // Left behind on purpose: the data of a portable copy is copied separately, and the marker must not reach an installed copy.
        private static readonly string[] SkipTopLevel = { "UserData", AppPaths.PortableMarker };

        private const string InstanceMutexName = @"Local\SmartTools.SingleInstance";

        /// <summary>True when a copy of the app is running (it keeps files open, so an install over it would fail half-way).</summary>
        public static bool IsAnotherCopyRunning()
        {
            try
            {
                using var existing = Mutex.OpenExisting(InstanceMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }

        // =================================================================
        //  Install
        // =================================================================

        /// <summary>
        /// Copies the app from <paramref name="source"/> to the install folder and registers it. Cancelling removes what was copied.
        /// <paramref name="checkRunning"/> is for the first-run window (no copy of the app is meant to be running); the running app installing itself passes false, because the one that is running is itself.
        /// </summary>
        public static Task<InstallResult> InstallAsync(string source, bool desktopShortcut, bool copyPortableData, bool checkRunning, IProgress<CopyProgress>? progress, CancellationToken token) =>
            Task.Run(() => Install(source, desktopShortcut, copyPortableData, checkRunning, progress, token));

        private static InstallResult Install(string source, bool desktopShortcut, bool copyPortableData, bool checkRunning, IProgress<CopyProgress>? progress, CancellationToken token)
        {
            string target = AppMode.DefaultInstallFolder;
            bool existed = Directory.Exists(target);
            var created = new List<string>();
            try
            {
                if (checkRunning && IsAnotherCopyRunning() && !SameFolder(source, target))
                    return new InstallResult(false, false, "Close Smart Tools first (another copy is running), then try again.", null);

                if (!SameFolder(source, target))
                    CopyTree(source, target, created, progress, token);

                string exe = Path.Combine(target, AppMode.ExeName);
                if (!File.Exists(exe)) return new InstallResult(false, false, $"{AppMode.ExeName} was not found in the copy.", null);

                if (copyPortableData)
                {
                    string data = Path.Combine(source, "UserData");
                    if (Directory.Exists(data)) CopyDataNoOverwrite(data, AppMode.InstalledDataFolder, token);
                }

                MakeShortcut(StartMenuShortcutPath, exe, target);
                if (desktopShortcut) MakeShortcut(DesktopShortcutPath, exe, target);
                WriteUninstallEntry(exe, target, DirectorySize(target));
                return new InstallResult(true, false, "", exe);
            }
            catch (OperationCanceledException)
            {
                RollBack(created, existed ? null : target);
                return new InstallResult(false, true, "Cancelled. Nothing was installed.", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RollBack(created, existed ? null : target);
                Trace.WriteLine($"Install failed: {ex.Message}");
                return new InstallResult(false, false, ex.Message, null);
            }
        }

        private static string StartMenuShortcutPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppMode.ProductName + ".lnk");

        private static string DesktopShortcutPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppMode.ProductName + ".lnk");

        private static bool SameFolder(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> AppFiles(string root)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                string first = relative.Split(Path.DirectorySeparatorChar)[0];
                if (SkipTopLevel.Contains(first, StringComparer.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }

        private static long DirectorySize(string folder)
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { /* a file that vanished is not counted */ }
            }
            return total;
        }

        /// <summary>Copies the app files, remembering every file and folder it created so a cancel can take them away again.</summary>
        private static void CopyTree(string source, string target, List<string> created, IProgress<CopyProgress>? progress, CancellationToken token)
        {
            var files = AppFiles(source).ToList();
            long total = files.Sum(f => new FileInfo(f).Length), done = 0;
            var clock = Stopwatch.StartNew();

            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                string destination = Path.Combine(target, Path.GetRelativePath(source, file));
                string folder = Path.GetDirectoryName(destination)!;
                if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); }

                bool isNew = !File.Exists(destination);
                CopyFile(file, destination, token, bytes =>
                {
                    done += bytes;
                    if (clock.ElapsedMilliseconds >= 80) { clock.Restart(); progress?.Report(new CopyProgress(done, total, Path.GetFileName(file))); }
                });
                if (isNew) created.Add(destination);
            }
            progress?.Report(new CopyProgress(total, total, ""));
        }

        private static void CopyFile(string from, string to, CancellationToken token, Action<long> copied)
        {
            const int Buffer = 1 << 20;
            using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer);
            using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, Buffer);
            var block = new byte[Buffer];
            int read;
            while ((read = input.Read(block, 0, block.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(block, 0, read);
                copied(read);
            }
        }

        private static void RollBack(List<string> created, string? wholeFolder)
        {
            try
            {
                if (wholeFolder != null && Directory.Exists(wholeFolder)) { Directory.Delete(wholeFolder, true); return; }
                foreach (string file in created) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Install clean-up incomplete: {ex.Message}");
            }
        }

        // =================================================================
        //  Shortcuts and the "Apps & features" entry
        // =================================================================

        /// <summary>Makes a .lnk through Windows Script Host (late bound, so no interop assembly is needed).</summary>
        private static void MakeShortcut(string path, string exe, string workingFolder)
        {
            try
            {
                Type? type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = exe;
                link.WorkingDirectory = workingFolder;
                link.IconLocation = exe + ",0";
                link.Description = AppMode.ProductName;
                link.Save();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                Trace.WriteLine($"Shortcut not made ({path}): {ex.Message}");
            }
        }

        private static void WriteUninstallEntry(string exe, string folder, long sizeBytes)
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppMode.UninstallKeyPath);
            key.SetValue("DisplayName", AppMode.ProductName);
            key.SetValue("DisplayVersion", AppMode.VersionText());
            key.SetValue("Publisher", "Sheep");
            key.SetValue("InstallLocation", folder);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", $"\"{exe}\" {AppMode.UninstallArgument}");
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeBytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        // =================================================================
        //  Data
        // =================================================================

        /// <summary>Copies the data folder; a file that already exists at the destination is kept (the user's current settings win).</summary>
        public static void CopyDataNoOverwrite(string from, string to, CancellationToken token)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(from, file);
                string first = relative.Split(Path.DirectorySeparatorChar)[0];
                // logs and the browser cache are rebuilt; the marker would only confuse the next start
                if (first.Equals("logs", StringComparison.OrdinalIgnoreCase) || first.Equals("WebView2", StringComparison.OrdinalIgnoreCase)) continue;

                string destination = Path.Combine(to, relative);
                if (File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try { File.Copy(file, destination); }
                catch (IOException ex) { Trace.WriteLine($"Data file not copied ({relative}): {ex.Message}"); }
            }
        }

        // =================================================================
        //  Portable copy
        // =================================================================

        /// <summary>Makes a folder that runs on its own (for a USB drive): the app, a portable.txt, and a copy of the current data in UserData.</summary>
        public static Task<InstallResult> MakePortableCopyAsync(string targetFolder, bool copyData, IProgress<CopyProgress>? progress, CancellationToken token) =>
            Task.Run(() =>
            {
                var created = new List<string>();
                bool existed = Directory.Exists(targetFolder);
                try
                {
                    if (existed && Directory.EnumerateFileSystemEntries(targetFolder).Any())
                        return new InstallResult(false, false, "Choose an empty folder.", null);
                    Directory.CreateDirectory(targetFolder);

                    CopyTree(AppMode.ExeFolder, targetFolder, created, progress, token);
                    File.WriteAllText(Path.Combine(targetFolder, AppPaths.PortableMarker), "");
                    if (copyData && Directory.Exists(AppPaths.DataRoot))
                        CopyDataNoOverwrite(AppPaths.DataRoot, Path.Combine(targetFolder, "UserData"), token);
                    return new InstallResult(true, false, "", Path.Combine(targetFolder, AppMode.ExeName));
                }
                catch (OperationCanceledException)
                {
                    RollBack(created, existed ? null : targetFolder);
                    return new InstallResult(false, true, "Cancelled. Nothing was copied.", null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RollBack(created, existed ? null : targetFolder);
                    return new InstallResult(false, false, ex.Message, null);
                }
            });

        // =================================================================
        //  Run / launch
        // =================================================================

        /// <summary>The first-run "Run" choice: the app becomes portable by writing the marker next to the exe. False when the folder is read-only.</summary>
        public static bool TryMakePortableHere()
        {
            try
            {
                File.WriteAllText(AppMode.PortableMarkerPath, "");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static void Launch(string exe, string arguments = "")
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Trace.WriteLine($"Could not start {exe}: {ex.Message}");
            }
        }

        // =================================================================
        //  Uninstall
        // =================================================================

        /// <summary>
        /// Takes the shortcuts, the Apps &amp; features entry and the start-with-Windows value away, then starts a hidden command that deletes
        /// the install folder once this process has exited. The data folder is only deleted when asked for.
        /// </summary>
        public static void Uninstall(bool deleteData)
        {
            string? folder = AppMode.InstalledFolder();

            foreach (string link in new[] { StartMenuShortcutPath, DesktopShortcutPath })
            {
                try { File.Delete(link); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine($"Shortcut not removed: {ex.Message}"); }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(AppMode.UninstallKeyPath, throwOnMissingSubKey: false); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { Trace.WriteLine($"Uninstall entry not removed: {ex.Message}"); }
            Controllers.ShortcutManager.ApplyStartWithWindows(false);

            var commands = new List<string>();
            if (folder != null) commands.Add($"rmdir /s /q \"{folder}\"");
            if (deleteData) commands.Add($"rmdir /s /q \"{AppMode.InstalledDataFolder}\"");
            if (commands.Count == 0) return;

            try
            {
                // ping is the usual way to wait a few seconds in a batch command: this process must exit first or it keeps its files locked
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & {string.Join(" & ", commands)}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Trace.WriteLine($"Install folder not removed: {ex.Message}");
            }
        }
    }
}
