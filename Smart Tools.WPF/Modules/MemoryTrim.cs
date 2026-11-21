using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Smart_Tools.WPF.Modules
{
    /// <summary>
    /// Gives memory back to Windows while nobody is looking at the app. Every tool you open keeps its view
    /// for the rest of the session, and the .NET heap and Windows keep memory the app has already freed, so
    /// Task Manager shows a high number for an app that is just sitting there. When the window has been
    /// inactive (another app in front, minimized, or in the tray) for <see cref="IdleDelay"/>, this runs one
    /// compacting collection and asks Windows to take back the pages the app is not using. Nothing is
    /// closed or reset; pages that are needed again are simply read back. It runs at most once per idle spell.
    /// </summary>
    public static class MemoryTrim
    {
        private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
        private static DispatcherTimer? _timer;

        /// <summary>The window lost focus: trim if it is still away after the delay.</summary>
        public static void Schedule()
        {
            _timer ??= CreateTimer();
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>The user is back: forget any pending trim.</summary>
        public static void Cancel() => _timer?.Stop();

        private static DispatcherTimer CreateTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = IdleDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();   // once per idle spell
                Trim();
            };
            return timer;
        }

        private static void Trim()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                using var process = Process.GetCurrentProcess();
                EmptyWorkingSet(process.Handle);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Info($"Memory trim skipped: {ex.Message}");
            }
        }

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr process);
    }
}
