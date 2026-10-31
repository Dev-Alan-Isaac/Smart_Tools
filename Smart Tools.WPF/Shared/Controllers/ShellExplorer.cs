using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// "Show in folder" that reuses an already-open File Explorer window
    /// instead of opening a new one each time. Moved out of the Duplicate
    /// Finder code-behind so other tools (Similar Files) can share it.
    /// Windows only; never throws.
    /// </summary>
    public static class ShellExplorer
    {
        public enum ShowResult
        {
            Shown,
            NotFound,
            Failed,
        }

        /// <summary>
        /// Shows <paramref name="fullPath"/> in File Explorer, reusing an open
        /// Explorer window when there is one. For a file, the file is also
        /// selected (the old version only navigated to its folder, leaving
        /// the user to hunt for it).
        /// </summary>
        public static ShowResult Show(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return ShowResult.NotFound;

            try
            {
                bool isFile = File.Exists(fullPath);
                string? folder = isFile ? Path.GetDirectoryName(fullPath) : fullPath;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return ShowResult.NotFound;

                if (TryReuseExistingExplorerWindow(folder, isFile ? Path.GetFileName(fullPath) : null)) return ShowResult.Shown;

                Process.Start("explorer.exe", isFile ? $"/select,\"{fullPath}\"" : $"\"{folder}\"");
                return ShowResult.Shown;
            }
            catch (Exception ex)
            {
                Log.Info($"ShellExplorer: {ex.Message}");
                return ShowResult.Failed;
            }
        }

        /// <summary>Navigates an open File Explorer window (not a browser) via Shell.Application late-bound COM.</summary>
        private static bool TryReuseExistingExplorerWindow(string folder, string? selectFileName)
        {
            if (!OperatingSystem.IsWindows()) return false;
            Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
            if (shellAppType == null) return false;

            object? shellApp = null;
            try
            {
                shellApp = Activator.CreateInstance(shellAppType);
                if (shellApp == null) return false;
                dynamic windows = ((dynamic)shellApp).Windows();

                int count = windows.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                    string exeName;
                    try
                    {
                        exeName = Path.GetFileNameWithoutExtension((string)window.FullName);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (!string.Equals(exeName, "explorer", StringComparison.OrdinalIgnoreCase)) continue;

                    window.Navigate(folder);
                    if (selectFileName != null) TrySelectAfterNavigation(window, folder, selectFileName);
                    ForceForegroundWindow((IntPtr)(long)window.HWND);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Shell automation unavailable or blocked by policy - caller opens a new window.
                Log.Info($"ShellExplorer: reuse failed: {ex.Message}");
            }
            finally
            {
                if (shellApp != null && OperatingSystem.IsWindows()) Marshal.ReleaseComObject(shellApp);
            }
            return false;
        }

        /// <summary>
        /// Navigation is asynchronous, so wait (up to ~1.5 s) for the window
        /// to show the folder, then select the file. Best effort - if it
        /// doesn't get there in time the folder is still shown.
        /// </summary>
        private static void TrySelectAfterNavigation(dynamic window, string folder, string fileName)
        {
            const int SelectFlags = 1 | 4 | 8 | 16;   // select, deselect others, ensure visible, focus
            string wanted = Path.TrimEndingDirectorySeparator(folder);

            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    dynamic document = window.Document;
                    string current = Path.TrimEndingDirectorySeparator((string)document.Folder.Self.Path);
                    if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        dynamic item = document.Folder.ParseName(fileName);
                        if (item != null) document.SelectItem(item, SelectFlags);
                        return;
                    }
                }
                catch (Exception)
                {
                    // Document not ready yet - keep waiting.
                }
                Thread.Sleep(75);
            }
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const int SwRestore = 9;
        private const int SwShow = 5;

        /// <summary>
        /// Brings a window forward past Windows' focus-stealing guard by
        /// briefly attaching to the foreground thread's input.
        /// </summary>
        private static void ForceForegroundWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);

            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint targetThread = GetWindowThreadProcessId(hWnd, IntPtr.Zero);
            uint currentThread = GetCurrentThreadId();

            bool attachedForeground = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            bool attachedTarget = targetThread != currentThread && targetThread != foregroundThread && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                ShowWindow(hWnd, SwShow);
            }
            finally
            {
                if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
                if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}
