using Smart_Tools.WPF.Controls;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Takes a reading of every program every few seconds (CPU share, memory, disk bytes) and keeps one summary per minute for a day.
    /// Programs are grouped by name, so twenty browser processes are one "chrome". Runs on its own thread while Smart Tools is open.
    /// </summary>
    public sealed class ResourceHogRecorder : UsageRecorder
    {
        public static ResourceHogRecorder Instance { get; } = new();

        // Slots of a minute: sums over the readings, and the highest reading.
        public const int SlotCpuSum = 0, SlotMemSum = 1, SlotDiskBytes = 2, SlotCpuPeak = 3, SlotMemPeak = 4, SlotCount = 5;

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(AppUsageStore.SampleSeconds);

        private Run? _run;

        private ResourceHogRecorder() : base("resource_hogs", "resource_hogs_history", SlotCount) { }

        public override string ToolId => "resource-hogs";

        public override string SettingsNoteKey => "Str_Settings_UhHogsNote";

        public override string AlertUnit => "% CPU";

        public override double AlertDefaultLimit => 80;

        /// <summary>
        /// State of one recording. Every Start gets its own, so a sampler thread that is still finishing a reading after
        /// Stop cannot mix its numbers with the next recording.
        /// </summary>
        private sealed class Run
        {
            public readonly CancellationTokenSource Cts = new();
            public readonly Dictionary<int, (double CpuMs, ulong IoBytes)> Previous = new();
            public DateTime LastReading = DateTime.UtcNow;
        }

        public override bool IsRunning { get { lock (Gate) return _run != null; } }

        public override string? Start()
        {
            lock (Gate)
            {
                if (_run != null) return null;
                var run = _run = new Run();
                new Thread(() => { EnsureLoaded(); Loop(run); }) { IsBackground = true, Name = "Resource hogs recorder", Priority = ThreadPriority.BelowNormal }.Start();
            }
            return null;
        }

        protected override void StopCore()
        {
            lock (Gate)
            {
                _run?.Cts.Cancel();
                _run = null;
            }
        }

        private void Loop(Run run)
        {
            var token = run.Cts.Token;
            while (!token.IsCancellationRequested)
            {
                if (token.WaitHandle.WaitOne(Interval)) break;
                try { Sample(run); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException)
                {
                    Log.Warn("Resource hogs reading failed: " + ex.Message);
                }
                SaveIfDue();
            }
        }

        private void Sample(Run run)
        {
            var now = DateTime.UtcNow;
            double elapsedMs = Math.Max(1, (now - run.LastReading).TotalMilliseconds);
            run.LastReading = now;
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
                    if (run.Previous.TryGetValue(pid, out var before) && cpuMs >= before.CpuMs)
                    {
                        cpu = (cpuMs - before.CpuMs) / (elapsedMs * cores) * 100.0;
                        if (io >= before.IoBytes) disk = io - before.IoBytes;
                    }
                    run.Previous[pid] = (cpuMs, io);

                    perApp.TryGetValue(name, out var sum);
                    perApp[name] = (sum.Cpu + cpu, sum.MemMb + memMb, sum.Disk + disk);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // the program ended while we looked, or Windows keeps it private
                }
                finally { p.Dispose(); }
            }
            foreach (int gone in run.Previous.Keys.Where(k => !seen.Contains(k)).ToList()) run.Previous.Remove(gone);

            Store.Tick(now);
            foreach (var (name, v) in perApp) CheckAlert(name, v.Cpu);
            var top = perApp.Count == 0 ? default : perApp.OrderByDescending(kv => kv.Value.Cpu).First();
            TopSummary = top.Key == null ? "" : $"{top.Key} {top.Value.Cpu:0} % CPU";
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

    /// <summary>What Resource Hogs History can measure. The page, ranking and chart come from <see cref="UsageBoardBuilder"/>.</summary>
    public static class ResourceHogsManager
    {
        private const double Step = AppUsageStore.SampleSeconds;

        public static IReadOnlyList<UsageMetric> Metrics { get; } = new[]
        {
            Average("Cpu", "Str_ResourceHogs_Cpu", ResourceHogRecorder.SlotCpuSum, ResourceHogRecorder.SlotCpuPeak, UsageFormat.Percent),
            Average("Memory", "Str_ResourceHogs_Memory", ResourceHogRecorder.SlotMemSum, ResourceHogRecorder.SlotMemPeak, UsageFormat.Megabytes),
            Disk(),
        };

        /// <summary>CPU and memory: ranked by the average over the period, second column is the highest reading.</summary>
        private static UsageMetric Average(string name, string labelKey, int sumSlot, int peakSlot, Func<double, string> text) => new()
        {
            Name = name,
            LabelKey = labelKey,
            Sum = s => s[sumSlot],
            Den = b => b.Samples,
            PeakOf = (s, _) => s[peakSlot],
            Rank = (sum, t) => t.Samples > 0 ? sum / t.Samples : 0,
            ValueText = text,
            ChartFormat = text,
            SecondText = (peak, _, _) => text(peak),
            ValueHeaderKey = "Str_ResourceHogs_ColAverage",
            PeakHeaderKey = "Str_ResourceHogs_ColHighest",
            Middle = (_, all) => new UsageHighlight(MaterialDesignThemes.Wpf.PackIconKind.FormatListNumbered, Loc.T("Str_ResourceHogs_StatPrograms", "Programs counted"), all.Count.ToString("N0"), Loc.T("Str_ResourceHogs_StatProgramsSub", "that used something")),
        };

        /// <summary>Disk: ranked by the bytes moved over the period, second column is the average rate.</summary>
        private static UsageMetric Disk() => new()
        {
            Name = "Disk",
            LabelKey = "Str_ResourceHogs_Disk",
            Sum = s => s[ResourceHogRecorder.SlotDiskBytes],
            Den = b => b.Samples * Step,
            PeakOf = (s, b) => s[ResourceHogRecorder.SlotDiskBytes] / Math.Max(Step, b.Samples * Step),
            Rank = (sum, _) => sum,
            ValueText = UsageFormat.Bytes,
            ChartFormat = UsageFormat.Rate,
            SecondText = (_, value, t) => UsageFormat.Rate(value / t.Seconds),
            ValueHeaderKey = "Str_ResourceHogs_ColTotal",
            PeakHeaderKey = "Str_ResourceHogs_ColRate",
            Middle = (_, all) => new UsageHighlight(MaterialDesignThemes.Wpf.PackIconKind.Harddisk, Loc.T("Str_ResourceHogs_StatMoved", "Read and written"), UsageFormat.Bytes(all.Sum(r => r.Value)), Loc.T("Str_ResourceHogs_StatMovedSub", "by all programs")),
        };
    }
}
