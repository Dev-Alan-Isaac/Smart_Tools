using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Opens files, folders, and web addresses with Windows' default handler, in one place.
    /// A file that can run code (a program, script, shortcut) is never launched from here: it is
    /// shown in Explorer instead, so opening a search result can't run it by accident.
    /// Every method returns false instead of throwing when Windows can't open the target.
    /// </summary>
    public static class ShellOpen
    {
        private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".scr", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".msi", ".msp", ".lnk", ".reg", ".hta", ".cpl", ".jar", ".appx", ".msix", ".application", ".gadget",
        };

        /// <summary>True for a file type that runs code when opened.</summary>
        public static bool IsRunnable(string path) => RunnableExtensions.Contains(Path.GetExtension(path));

        /// <summary>Opens the file in its default program. A runnable file is revealed in Explorer instead.</summary>
        public static bool OpenFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            return IsRunnable(path) ? Reveal(path) : Start(new ProcessStartInfo(path));
        }

        /// <summary>Opens the folder in Explorer.</summary>
        public static bool OpenFolder(string folder) =>
            !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && Start(Explorer($"\"{folder}\""));

        /// <summary>Opens Explorer with the file (or folder) selected.</summary>
        public static bool Reveal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (Directory.Exists(path)) return OpenFolder(path);
            return File.Exists(path) && Start(Explorer($"/select,\"{path}\""));
        }

        /// <summary>Opens a web address in the default browser. Only http and https are accepted.</summary>
        public static bool OpenUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Start(new ProcessStartInfo(uri.AbsoluteUri));

        private static ProcessStartInfo Explorer(string arguments) => new("explorer.exe", arguments);

        private static bool Start(ProcessStartInfo info)
        {
            try
            {
                info.UseShellExecute = true;
                using var process = Process.Start(info);
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return false;
            }
        }
    }
}
