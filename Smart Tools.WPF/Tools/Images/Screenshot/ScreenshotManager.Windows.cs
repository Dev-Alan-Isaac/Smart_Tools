using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Windows-only half of Screenshot: which window is in front. The screen itself is captured by <see cref="ScreenOcrManager.CaptureVirtualScreen"/>.</summary>
    public static partial class ScreenshotManager
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rect, int size);

        private const int DwmExtendedFrameBounds = 9;

        /// <summary>
        /// The front window's visible bounds in screen pixels (without the invisible resize border Windows adds),
        /// or null when there is none worth taking: nothing in front, it is minimized, or it is this app.
        /// </summary>
        public static (int X, int Y, int Width, int Height)? ForegroundWindowBounds()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return null;

            if (DwmGetWindowAttribute(hwnd, DwmExtendedFrameBounds, out var rect, Marshal.SizeOf<NativeRect>()) != 0
                && !GetWindowRect(hwnd, out rect))
                return null;

            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            return width < 1 || height < 1 ? null : (rect.Left, rect.Top, width, height);
        }
    }
}
