using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Smart_Tools.WPF.Controllers;
using Application = System.Windows.Application;

namespace Smart_Tools.WPF.Modules
{
    /// <summary>
    /// Gets the app out of a broken screen. After the PC wakes from sleep (or the graphics driver resets), WPF's drawing thread can fail
    /// for good: every redraw throws an OutOfMemoryException (or a render-thread COMException) from MediaContext / DUCE although there
    /// is plenty of memory. The global handler used to swallow each one, so the app kept running and taking clicks behind a picture
    /// that never changed again (seen 2026-11-27: 681 errors after a resume, the 3D Modeler open).
    /// What it does: the first such failure restarts the app once (each tool saves as the window closes, the new copy waits for the
    /// old one to exit). If the restarted copy fails the same way soon after, it does not restart again: it switches to software
    /// drawing in place, which needs no graphics card. Nothing is asked: a message box cannot be drawn either.
    /// </summary>
    public static class RenderRecovery
    {
        /// <summary>Passed to the restarted copy, so a second failure there switches to software drawing instead of restarting again.</summary>
        public const string RestartedArgument = "--render-restarted";

        private const int UceerrRenderThreadFailure = unchecked((int)0x88980406);
        private const long RealOutOfMemoryBytes = 3L * 1024 * 1024 * 1024;   // above this the app may truly be out of memory: leave it alone

        private static bool _restartedOnce;
        private static bool _acting;
        private static bool _software;
        private static int _swallowed;

        /// <summary>Reads the start arguments (call once at start-up).</summary>
        public static void Init(IReadOnlyList<string> args) =>
            _restartedOnce = args.Any(a => string.Equals(a, RestartedArgument, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// True when <paramref name="ex"/> is the broken-drawing failure; then it has been dealt with (the caller marks it handled and
        /// need not log it: only the first one and a count are logged).
        /// </summary>
        public static bool TryHandle(Exception ex)
        {
            if (!IsRenderFailure(ex)) return false;
            if (_acting || _software)
            {
                // the burst that follows: one line per 100, not hundreds of stack traces
                if (++_swallowed % 100 == 0) Log.Warn($"Render failure still happening ({_swallowed} more since recovery started).");
                return true;
            }
            _acting = true;
            Log.Error("The screen stopped drawing (graphics reset, e.g. after sleep): recovering.", ex);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return true;
            // after the current message: the dispatcher still runs, only drawing is broken
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_restartedOnce && Environment.ProcessPath is { } exe && Restart(exe)) return;
                UseSoftwareDrawing();
            }));
            return true;
        }

        private static bool IsRenderFailure(Exception ex)
        {
            bool kind = ex is OutOfMemoryException || ex is COMException { HResult: UceerrRenderThreadFailure };
            if (!kind) return false;
            string stack = ex.StackTrace ?? "";
            bool drawing = stack.Contains("System.Windows.Media.MediaContext", StringComparison.Ordinal)
                || stack.Contains("System.Windows.Media.Composition.DUCE", StringComparison.Ordinal)
                || stack.Contains("System.Windows.Interop.HwndTarget", StringComparison.Ordinal);
            if (!drawing) return false;
            if (ex is COMException) return true;
            try
            {
                using var me = Process.GetCurrentProcess();
                return me.PrivateMemorySize64 < RealOutOfMemoryBytes;
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                return true;
            }
        }

        private static bool Restart(string exe)
        {
            AppRestartManager.StartCopy(exe, RestartedArgument);
            if (!AppRestartManager.IsRestarting) return false;
            Log.Info("Render recovery: restarting the app.");
            Application.Current.Shutdown();
            return true;
        }

        /// <summary>Draws every window with the processor from now on (also windows opened later).</summary>
        private static void UseSoftwareDrawing()
        {
            _software = true;
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            foreach (Window window in Application.Current.Windows)
            {
                try
                {
                    if (PresentationSource.FromVisual(window) is HwndSource { CompositionTarget: { } target })
                        target.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    window.InvalidateVisual();
                }
                catch (Exception e) when (e is InvalidOperationException or COMException or OutOfMemoryException)
                {
                    Log.Warn("Render recovery: could not switch a window to software drawing.", e);
                }
            }
            Log.Info("Render recovery: switched to software drawing for this session.");
        }
    }
}
