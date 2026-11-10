using Smart_Tools.WPF.Controls;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    public enum HogMetric { Cpu, Memory, Disk }

    /// <summary>Remembered between sessions (resource_hogs.json): only whether the history is being recorded.</summary>
    public sealed class ResourceHogsSettings
    {
        public bool Recording { get; set; }
        public int PeriodHours { get; set; } = 3;
        public HogMetric Metric { get; set; } = HogMetric.Cpu;
    }

    /// <summary>
    /// Takes a reading of every program every few seconds (CPU share, memory, disk bytes) and keeps one summary per minute for a day.
    /// Programs are grouped by name, so twenty browser processes are one "chrome". Runs on its own thread while Smart Tools is open.
    /// </summary>
    public sealed class ResourceHogRecorder
    {
        public static ResourceHogRecorder Instance { get; } = new();

        public const string SettingsName = "resource_hogs";
        public const string HistoryName = "resource_hogs_history";

        // Slots of a minute: sums over the readings, and the highest reading.
        public const int SlotCpuSum = 0, SlotMemSum = 1, SlotDiskBytes = 2, SlotCpuPeak = 3, SlotMemPeak = 4, SlotCount = 5;

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

        public AppUsageStore Store { get; } = new(HistoryName, SlotCount, TimeSpan.FromHours(24));

        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private readonly Dictionary<int, (double CpuMs, ulong IoBytes)> _previous = new();
        private DateTime _lastReading;
        private DateTime _lastSave = DateTime.UtcNow;
        private bool _loaded;

        public bool IsRunning { get { lock (_gate) return _cts != null; } }

        public void Start()
        {
            lock (_gate)
            {
                if (_cts != null) return;
                if (!_loaded) { Store.Load(); _loaded = true; }
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _previous.Clear();
                _lastReading = DateTime.UtcNow;
                new Thread(() => Loop(token)) { IsBackground = true, Name = "Resource hogs recorder", Priority = ThreadPriority.BelowNormal }.Start();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts = null;
            }
            Store.Save();
        }

        /// <summary>Loads the saved history once (so a page can show it before recording starts).</summary>
        public void EnsureLoaded()
        {
            lock (_gate) { if (_loaded) return; Store.Load(); _loaded = true; }
        }

        private void Loop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (token.WaitHandle.WaitOne(Interval)) break;
                try { Sample(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException)
                {
                    Log.Warn("Resource hogs reading failed: " + ex.Message);
                }
                if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(2)) { _lastSave = DateTime.UtcNow; Store.Save(); }
            }
        }

        private void Sample()
        {
            var now = DateTime.UtcNow;
            double elapsedMs = Math.Max(1, (now - _lastReading).TotalMilliseconds);
            _lastReading = now;
            int cores = Math.Max(1, Environment.ProcessorCount);

            var perApp = new Dictionary<string, (double Cpu, double MemMb, double Disk)>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<int>();
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    int pid = p.Id;
                    if (pid == 0) continue;   // System Idle Process
                    string name = p.ProcessName;
                    seen.Add(pid);

                    double cpuMs = 0;
                    try { cpuMs = p.TotalProcessorTime.TotalMilliseconds; } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                    ulong io = ReadIoBytes(pid);
                    double memMb = p.WorkingSet64 / 1048576.0;

                    double cpu = 0, disk = 0;
                    if (_previous.TryGetValue(pid, out var before) && cpuMs >= before.CpuMs)
                    {
                        cpu = (cpuMs - before.CpuMs) / (elapsedMs * cores) * 100.0;
                        if (io >= before.IoBytes) disk = io - before.IoBytes;
                    }
                    _previous[pid] = (cpuMs, io);

                    perApp.TryGetValue(name, out var sum);
                    perApp[name] = (sum.Cpu + cpu, sum.MemMb + memMb, sum.Disk + disk);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // the program ended while we looked, or Windows keeps it private
                }
                finally { p.Dispose(); }
            }
            foreach (int gone in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(gone);

            Store.Tick(now);
            foreach (var (name, v) in perApp)
            {
                // Only programs that matter: a small program that sleeps is not worth a row in a day of history.
                if (v.Cpu < 0.1 && v.MemMb < 50 && v.Disk < 100 * 1024) continue;
                Store.Add(now, name, SlotCpuSum, v.Cpu);
                Store.Add(now, name, SlotMemSum, v.MemMb);
                Store.Add(now, name, SlotDiskBytes, v.Disk);
                Store.Peak(now, name, SlotCpuPeak, v.Cpu);
                Store.Peak(now, name, SlotMemPeak, v.MemMb);
            }
        }

        // ---- disk bytes read + written by a program (kernel32; needs only limited query rights)

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static ulong ReadIoBytes(int pid)
        {
            IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (handle == IntPtr.Zero) return 0;
            try { return GetProcessIoCounters(handle, out var c) ? c.ReadTransferCount + c.WriteTransferCount : 0; }
            finally { CloseHandle(handle); }
        }
    }

    public static class ResourceHogsManager
    {
        public static UsageBoardData Build(AppUsageStore store, HogMetric metric, TimeSpan period)
        {
            var since = DateTime.UtcNow - period;
            var buckets = store.Since(since);
            double samples = buckets.Sum(b => (double)b.Samples);
            double seconds = Math.Max(1, samples * 5.0);   // a reading is taken every 5 seconds

            // Per program: the headline value, and the second column.
            var totals = new Dictionary<string, (double Sum, double Peak)>(StringComparer.OrdinalIgnoreCase);
            int sumSlot = metric switch { HogMetric.Cpu => ResourceHogRecorder.SlotCpuSum, HogMetric.Memory => ResourceHogRecorder.SlotMemSum, _ => ResourceHogRecorder.SlotDiskBytes };
            int peakSlot = metric switch { HogMetric.Cpu => ResourceHogRecorder.SlotCpuPeak, HogMetric.Memory => ResourceHogRecorder.SlotMemPeak, _ => -1 };
            foreach (var b in buckets)
                foreach (var (app, slots) in b.Apps)
                {
                    totals.TryGetValue(app, out var t);
                    double peak = peakSlot >= 0 ? Math.Max(t.Peak, slots[peakSlot]) : Math.Max(t.Peak, slots[sumSlot] / Math.Max(5.0, b.Samples * 5.0));
                    totals[app] = (t.Sum + slots[sumSlot], peak);
                }

            // Ranked by an average over the whole period for CPU and memory, by the total for disk.
            double Value(double sum) => metric == HogMetric.Disk ? sum : samples > 0 ? sum / samples : 0;
            var all = totals.Select(kv => (Name: kv.Key, Value: Value(kv.Value.Sum), kv.Value.Peak)).Where(r => r.Value > 0).OrderByDescending(r => r.Value).ToList();
            var ranked = all.Take(40).ToList();
            double top = ranked.Count > 0 ? ranked[0].Value : 1;

            Func<double, string> format = metric switch { HogMetric.Cpu => UsageMath.Percent, HogMetric.Memory => UsageMath.Megabytes, _ => UsageMath.Rate };
            string Text(double v) => metric switch { HogMetric.Cpu => UsageMath.Percent(v), HogMetric.Memory => UsageMath.Megabytes(v), _ => UsageMath.Bytes(v) };
            string Second(double peak, double total) => metric switch { HogMetric.Disk => UsageMath.Rate(total / seconds), HogMetric.Cpu => UsageMath.Percent(peak), _ => UsageMath.Megabytes(peak) };

            var rows = ranked.Select((r, i) => new UsageRow
            {
                Rank = i + 1,
                Name = r.Name,
                ValueText = Text(r.Value),
                PeakText = Second(r.Peak, r.Value),
                Share = top > 0 ? r.Value / top * 100.0 : 0,
                ColorBrush = UsageMath.BrushOf(i < UsageMath.StackedPrograms ? UsageMath.Palette[i] : UsageMath.OtherColor),
            }).ToList();

            Func<double[], double> num = metric switch
            {
                HogMetric.Cpu => s => s[ResourceHogRecorder.SlotCpuSum],
                HogMetric.Memory => s => s[ResourceHogRecorder.SlotMemSum],
                _ => s => s[ResourceHogRecorder.SlotDiskBytes],
            };
            Func<UsageBucket, double> den = metric == HogMetric.Disk ? b => b.Samples * 5.0 : b => b.Samples;
            var slices = UsageMath.Cut(buckets, since, period, num, den);
            var stack = UsageMath.Stack(slices, ranked.Select(r => r.Name).ToList(), format, "HH:mm");

            var highlights = new List<UsageHighlight>();
            if (ranked.Count > 0)
                highlights.Add(new UsageHighlight(MaterialDesignThemes.Wpf.PackIconKind.TrendingUp, Loc.T("Str_ResourceHogs_StatHeaviest", "Heaviest program"), ranked[0].Name, Text(ranked[0].Value)));
            highlights.Add(metric == HogMetric.Disk
                ? new UsageHighlight(MaterialDesignThemes.Wpf.PackIconKind.Harddisk, Loc.T("Str_ResourceHogs_StatMoved", "Read and written"), UsageMath.Bytes(all.Sum(r => r.Value)), Loc.T("Str_ResourceHogs_StatMovedSub", "by all programs"))
                : new UsageHighlight(MaterialDesignThemes.Wpf.PackIconKind.FormatListNumbered, Loc.T("Str_ResourceHogs_StatPrograms", "Programs counted"), all.Count.ToString("N0"), Loc.T("Str_ResourceHogs_StatProgramsSub", "that used something")));
            var busiest = UsageMath.Busiest(slices);
            highlights.Add(busiest == null
                ? new UsageHighlight(MaterialDesignThemes.Wpf.PackIconKind.ClockOutline, Loc.T("Str_ResourceHogs_StatBusiest", "Busiest moment"), "-", "")
                : new UsageHighlight(MaterialDesignThemes.Wpf.PackIconKind.ClockOutline, Loc.T("Str_ResourceHogs_StatBusiest", "Busiest moment"), busiest.Value.When.ToString("HH:mm"), format(busiest.Value.Value) + " " + Loc.T("Str_ResourceHogs_StatBusiestSub", "all programs together")));

            return new UsageBoardData
            {
                Rows = rows,
                Highlights = highlights,
                Stack = stack,
                ValueHeaderKey = metric == HogMetric.Disk ? "Str_ResourceHogs_ColTotal" : "Str_ResourceHogs_ColAverage",
                PeakHeaderKey = metric == HogMetric.Disk ? "Str_ResourceHogs_ColRate" : "Str_ResourceHogs_ColHighest",
            };
        }
    }
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Starts the history recorders the user switched on last time, and stops them (and their trace session) when the app closes.</summary>
    public static class UsageRecorders
    {
        public static void StartIfEnabled()
        {
            try
            {
                if (JsonSettings.Load<ResourceHogsSettings>(ResourceHogRecorder.SettingsName).Recording) ResourceHogRecorder.Instance.Start();
                if (JsonSettings.Load<BandwidthSettings>(BandwidthRecorder.SettingsName).Recording) BandwidthRecorder.Instance.Start();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Warn("Could not start the history recorders: " + ex.Message);
            }
        }

        public static void StopAll()
        {
            if (ResourceHogRecorder.Instance.IsRunning) ResourceHogRecorder.Instance.Stop();
            if (BandwidthRecorder.Instance.IsRunning) BandwidthRecorder.Instance.Stop();
        }
    }
}
