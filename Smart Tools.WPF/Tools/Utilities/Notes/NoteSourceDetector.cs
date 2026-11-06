using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Works out where the thing on the clipboard came from, for the "Source" line under a pasted picture:
    /// the page address when it was copied from a browser (the HTML clipboard format carries it), otherwise the program
    /// (and window title) that put it there. Best effort - an empty string means "unknown".
    /// </summary>
    internal static class NoteSourceDetector
    {
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        public static string FromClipboard()
        {
            try
            {
                if (Clipboard.ContainsData(DataFormats.Html) && Clipboard.GetData(DataFormats.Html) is string html)
                {
                    foreach (string line in html.Split('\n').Take(12))
                    {
                        if (!line.StartsWith("SourceURL:", StringComparison.OrdinalIgnoreCase)) continue;
                        string url = line["SourceURL:".Length..].Trim();
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return url;
                    }
                }

                IntPtr owner = GetClipboardOwner();
                if (owner == IntPtr.Zero) return "";
                GetWindowThreadProcessId(owner, out uint pid);
                if (pid == 0 || pid == Environment.ProcessId) return "";

                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName;
                var title = new StringBuilder(256);
                GetWindowText(owner, title, title.Capacity);
                string windowTitle = title.Length > 0 ? title.ToString() : process.MainWindowTitle;
                return string.IsNullOrWhiteSpace(windowTitle) ? name : $"{name} - {windowTitle.Trim()}";
            }
            catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or ArgumentException or Win32Exception)
            {
                return "";
            }
        }
    }
}
