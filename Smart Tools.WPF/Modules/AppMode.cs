using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

// Root namespace on purpose (like AppPaths and Log): every sub-namespace sees it without an extra using.
namespace Smart_Tools.WPF
{
    /// <summary>How this copy of the app is being used.</summary>
    public enum AppRunMode
    {
        /// <summary>A portable.txt sits next to the exe: settings live in UserData beside it.</summary>
        Portable,

        /// <summary>Installed by the app itself (per user, in %LocalAppData%\Programs\Smart Tools).</summary>
        Installed,

        /// <summary>Installed by the old MSI (under Program Files). Windows removes it, not this app.</summary>
        LegacyMsi,

        /// <summary>Running from some folder, with settings in %AppData% (someone who used the app before this choice existed).</summary>
        Loose,

        /// <summary>Running from a Visual Studio build folder or under a debugger: never ask, never install.</summary>
        Development,

        /// <summary>Brand-new profile in a folder that is neither: the Install / Run window must ask.</summary>
        Ask,
    }

    /// <summary>
    /// Works out how this copy is being used. It runs before anything else at start-up, so it must NOT touch
    /// <see cref="AppPaths"/> or <see cref="Log"/> (they create folders and fix the data location for the whole run).
    /// </summary>
    public static class AppMode
    {
        public const string ProductName = "Smart Tools";
        public const string ExeName = "SmartTools.exe";
        public const string NoPromptArgument = "--no-prompt";
        public const string UninstallArgument = "--uninstall";
        public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SmartTools";

        /// <summary>The mode Detect gave at start-up (Settings reads it to show the Installation card).</summary>
        public static AppRunMode Current { get; private set; } = AppRunMode.Loose;

        /// <summary>%LocalAppData%\Programs\Smart Tools - a per-user folder the app can write to, so no administrator prompt.</summary>
        public static string DefaultInstallFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

        /// <summary>Where the app keeps its data when it is not portable (the same place AppPaths uses).</summary>
        public static string InstalledDataFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName);

        public static string ExeFolder => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // AppPaths.PortableMarker is a const, so reading it does not start AppPaths (which would fix the data folder).
        public static string PortableMarkerPath => Path.Combine(AppContext.BaseDirectory, AppPaths.PortableMarker);

        /// <summary>The folder the app was installed to, if it is installed: from the Uninstall entry, else the default folder.</summary>
        public static string? InstalledFolder()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
                if (key?.GetValue("InstallLocation") is string folder && File.Exists(Path.Combine(folder, ExeName))) return folder.TrimEnd('\\');
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // fall through to the default folder
            }
            return File.Exists(Path.Combine(DefaultInstallFolder, ExeName)) ? DefaultInstallFolder : null;
        }

        private static bool SameFolder(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the exe sits in bin\Debug\net...\ (or Release) of a project folder - a Visual Studio run.</summary>
        private static bool LooksLikeBuildOutput()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; up < 4 && dir?.Parent != null; up++, dir = dir.Parent)
                {
                    if (dir.Parent.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && dir.Parent.Parent != null)
                        return dir.Parent.Parent.EnumerateFiles("*.csproj").Any();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // not a build folder we can read: treat as a normal folder
            }
            return false;
        }

        /// <summary>Decides the mode from the folder the exe is in and what already exists on this PC. Call it first thing.</summary>
        public static AppRunMode Detect(IReadOnlyList<string> args)
        {
            AppRunMode mode;
            if (File.Exists(PortableMarkerPath)) mode = AppRunMode.Portable;
            else if (InstalledFolder() is { } installed && SameFolder(installed, ExeFolder)) mode = AppRunMode.Installed;
            else if (IsUnderProgramFiles(ExeFolder)) mode = AppRunMode.LegacyMsi;
            else if (Debugger.IsAttached || LooksLikeBuildOutput() || args.Contains(NoPromptArgument, StringComparer.OrdinalIgnoreCase)) mode = AppRunMode.Development;
            // Someone who already has settings must never be moved to a different data folder by a first-run question.
            else if (File.Exists(Path.Combine(InstalledDataFolder, "settings.json"))) mode = AppRunMode.Loose;
            else mode = AppRunMode.Ask;

            Current = mode;
            return mode;
        }

        /// <summary>Called when the Install / Run window finished with Run: the app is portable from now on.</summary>
        public static void SetPortable() => Current = AppRunMode.Portable;

        private static bool IsUnderProgramFiles(string folder)
        {
            foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(special);
                if (root.Length > 0 && folder.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Whether the app can write in its own folder (a portable copy needs to; a zip preview or a CD cannot).</summary>
        public static bool CanWriteHere(string folder)
        {
            try
            {
                string probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static string VersionText()
        {
            string? text = typeof(AppMode).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(text)) return typeof(AppMode).Assembly.GetName().Version?.ToString(3) ?? "";
            int plus = text.IndexOf('+');
            return plus >= 0 ? text[..plus] : text;
        }
    }
}
