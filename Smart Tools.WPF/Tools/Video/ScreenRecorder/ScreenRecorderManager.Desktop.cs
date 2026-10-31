using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Screen Recorder - the Windows-only half of skipping security prompts. A UAC prompt, the lock screen, and Ctrl+Alt+Del
    /// run on a separate "secure desktop" that no screen capture can see; asking for the desktop that receives input fails
    /// while it is up, even for a program that isn't running as administrator.
    /// </summary>
    public sealed partial class ScreenRecorderManager
    {
        private const uint DesktopSwitchDesktop = 0x0100;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);

        /// <summary>True while the secure desktop (UAC prompt, lock screen) is showing instead of the user's desktop.</summary>
        public static bool IsSecureDesktopActive()
        {
            IntPtr desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
            if (desktop == IntPtr.Zero) return true;
            CloseDesktop(desktop);
            return false;
        }

        /// <summary>Polls <see cref="IsSecureDesktopActive"/> and reports each change, once two readings in a row agree.</summary>
        private sealed class SecurityPromptWatcher : IDisposable
        {
            private readonly System.Threading.Timer _timer;
            private readonly Action<bool> _changed;
            private bool _reported, _pending;
            private int _agree;

            public SecurityPromptWatcher(Action<bool> changed)
            {
                _changed = changed;
                _timer = new System.Threading.Timer(_ => Poll(), null, 150, 150);
            }

            private void Poll()
            {
                bool now;
                try { now = IsSecureDesktopActive(); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return; }

                if (now == _reported) { _agree = 0; return; }
                if (now != _pending) { _pending = now; _agree = 0; }
                if (++_agree < 2) return;

                _reported = now;
                _agree = 0;
                _changed(now);
            }

            public void Dispose() => _timer.Dispose();
        }
    }
}
