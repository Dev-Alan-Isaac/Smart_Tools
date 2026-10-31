using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Smart_Tools.WPF.Modules
{
    /// <summary>
    /// Background watchdog: every second it checks that the UI thread still answers, every 5 seconds it
    /// writes one heartbeat line (memory, handles, threads, ffmpeg count, current tool, last logged
    /// action) straight to disk in heartbeat.log. After a freeze or hard reset, the last heartbeat shows
    /// what the app and the machine looked like just before.
    /// </summary>
    public static class ResourceMonitor
    {
        private const int HeartbeatEveryTicks = 5;
        private const long StallWarnMs = 1500;

        private static System.Threading.Timer? _timer;
        private static Dispatcher? _dispatcher;
        private static int _tick;
        private static long _pingSent;          // Stopwatch timestamp of an unanswered ping, 0 = none
        private static long _lastStallReport;
        private static bool _stalled;

        /// <summary>Id of the tool the user is looking at (set by the shell when the page changes).</summary>
        public static string CurrentTool { get; set; } = "";

        public static void Start(Dispatcher dispatcher)
        {
            if (_timer != null) return;
            _dispatcher = dispatcher;
            DurableLog.Write($"MONITOR started, pid {Environment.ProcessId}, {Environment.ProcessorCount} cores");
            _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            DurableLog.Write("MONITOR stopped (clean exit)");
            DurableLog.Close();
        }

        private static void Tick()
        {
            try
            {
                WatchUiThread();
                if (++_tick % HeartbeatEveryTicks == 0) Heartbeat();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Monitor tick failed: {ex.Message}");
            }
        }

        private static void WatchUiThread()
        {
            long now = Stopwatch.GetTimestamp();
            long sent = Interlocked.Read(ref _pingSent);
            if (sent == 0)
            {
                Interlocked.Exchange(ref _pingSent, now);
                _dispatcher?.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    long started = Interlocked.Exchange(ref _pingSent, 0);
                    if (_stalled && started != 0)
                    {
                        _stalled = false;
                        DurableLog.Write($"UI    recovered after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
                    }
                });
                return;
            }

            double waited = Stopwatch.GetElapsedTime(sent, now).TotalMilliseconds;
            if (waited < StallWarnMs) return;
            // First report, then a reminder every 5 s while it lasts (with the heartbeat, so memory is visible).
            if (!_stalled || Stopwatch.GetElapsedTime(_lastStallReport, now).TotalSeconds >= 5)
            {
                _stalled = true;
                _lastStallReport = now;
                DurableLog.Write($"UI    NOT RESPONDING for {waited:0} ms | tool={CurrentTool} | active=[{Log.ActiveSummary()}] | last={Log.LastActivity}");
                Heartbeat();
            }
        }

        private static void Heartbeat()
        {
            using var process = Process.GetCurrentProcess();
            var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            bool haveMem = GlobalMemoryStatusEx(ref mem);

            string system = haveMem
                ? $"sysFree={Mb(mem.AvailPhys)} ({mem.MemoryLoad}% used) commitFree={Mb(mem.AvailPageFile)}"
                : "sys=?";

            int ffmpeg = Count("ffmpeg") + Count("ffprobe");
            DurableLog.Write(
                $"BEAT  ws={Mb(process.WorkingSet64)} priv={Mb(process.PrivateMemorySize64)} gc={Mb(GC.GetTotalMemory(false))} " +
                $"thr={process.Threads.Count} hnd={process.HandleCount} gdi={GuiHandles(process, 0)} usr={GuiHandles(process, 1)} | " +
                $"{system} | ffmpeg={ffmpeg} | tool={CurrentTool} | active=[{Log.ActiveSummary()}] | last={Log.LastActivity}");
        }

        private static int Count(string name)
        {
            var found = Process.GetProcessesByName(name);
            int n = found.Length;
            foreach (var p in found) p.Dispose();
            return n;
        }

        private static int GuiHandles(Process process, uint kind)
        {
            try { return (int)GetGuiResources(process.Handle, kind); }
            catch { return -1; }
        }

        private static string Mb(long bytes) => $"{bytes / 1048576}MB";

        private static string Mb(ulong bytes) => $"{bytes / 1048576}MB";

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
