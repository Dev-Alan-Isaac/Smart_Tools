using System.ComponentModel;
using System.Diagnostics;

namespace Smart_Tools.WPF.Controllers
{
    public enum AppRestartResult
    {
        /// <summary>The elevated copy was started: the caller should now close this instance.</summary>
        Started,

        /// <summary>The user said No at the UAC prompt (or policy blocked it): stay as we are.</summary>
        Declined,

        Failed,
    }

    /// <summary>
    /// Switches the running app to an administrator copy of itself. The elevated copy is started
    /// with <c>--restart-from &lt;pid&gt;</c> and, before it shows anything, waits for that process to
    /// exit - so the user sees one window close and the elevated one open, with the settings the
    /// old one saved on its way out. No UI here: the callers confirm and close the window.
    /// </summary>
    public static class AppRestartManager
    {
        public const string RestartFromArgument = "--restart-from";

        private const int ErrorCancelledByUser = 1223;   // ERROR_CANCELLED: UAC prompt declined
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

        /// <summary>True once an elevated copy is running and this instance is only waiting to close.</summary>
        public static bool IsRestarting { get; private set; }

        /// <summary>Starts the elevated copy (shows the UAC prompt). Sets <see cref="IsRestarting"/> on success.</summary>
        public static AppRestartResult StartElevatedCopy(out string? error)
        {
            error = null;
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                error = "Couldn't find this app's executable to restart it.";
                return AppRestartResult.Failed;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = $"{RestartFromArgument} {Environment.ProcessId}",
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                IsRestarting = true;
                return AppRestartResult.Started;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
            {
                return AppRestartResult.Declined;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                error = ex.Message;
                return AppRestartResult.Failed;
            }
        }

        /// <summary>
        /// Called first thing at startup: when this copy was started by a restart, waits (up to
        /// 15 s) for the previous one to finish closing. Does nothing for a normal launch.
        /// </summary>
        public static void WaitForPreviousInstance(IReadOnlyList<string> args)
        {
            int index = -1;
            for (int i = 0; i < args.Count; i++)
            {
                if (string.Equals(args[i], RestartFromArgument, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            }
            if (index < 0 || index + 1 >= args.Count || !int.TryParse(args[index + 1], out int pid)) return;

            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(MaxWait);
            }
            catch (ArgumentException)
            {
                // already gone - nothing to wait for
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                Log.Info($"Restart: couldn't wait for the previous instance: {ex.Message}");
            }
        }
    }
}
