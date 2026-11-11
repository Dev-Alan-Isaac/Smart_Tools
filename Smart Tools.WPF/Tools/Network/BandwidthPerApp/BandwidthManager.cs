using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Smart_Tools.WPF.Controls;
using System.Diagnostics;
using System.ComponentModel;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Counts the bytes each program sends and receives, from Windows' own network trace (the "Microsoft-Windows-Kernel-Network" event
    /// provider: TCP and UDP, IPv4 and IPv6, one event per data transfer with the program's id and the size). Windows only allows
    /// reading that trace to an administrator. Only totals per program name are kept (per minute, for a day), never addresses or content.
    /// </summary>
    public sealed class BandwidthRecorder : UsageRecorder
    {
        public static BandwidthRecorder Instance { get; } = new();

        public const int SlotDown = 0, SlotUp = 1, SlotCount = 2;

        private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
        private const string SessionName = "SmartToolsBandwidth";
        private const string EndedName = "(ended program)";   // one row for every program that was gone before we could ask its name
        private static readonly TimeSpan NameLife = TimeSpan.FromMinutes(10);

        private readonly Dictionary<string, (long Down, long Up)> _pending = new();
        private readonly Dictionary<int, (string Name, DateTime At)> _names = new();
        private TraceEventSession? _session;
        private CancellationTokenSource? _cts;
        private long _events;

        private BandwidthRecorder() : base("bandwidth_per_app", "bandwidth_per_app_history", SlotCount) { }

        public override string ToolId => "bandwidth-per-app";

        public override string SettingsNoteKey => "Str_Settings_UhBandwidthNote";

        public override bool IsRunning { get { lock (Gate) return _session != null; } }

        /// <summary>How many transfers were seen since recording started (0 for a while after starting: nothing may be moving).</summary>
        public long EventsSeen => Interlocked.Read(ref _events);

        /// <summary>Starts counting. Returns null, or why it couldn't (not administrator, the trace is busy...).</summary>
        public override string? Start()
        {
            lock (Gate)
            {
                if (_session != null) return null;
                if (!HardwareSensorsManager.IsAdministrator()) return NotAdministrator;
                TraceEventSession? session = null;
                try
                {
                    session = new TraceEventSession(SessionName);
                    session.EnableProvider(KernelNetwork);
                    session.Source.Dynamic.All += OnEvent;
                    _session = session;
                    Interlocked.Exchange(ref _events, 0);
                    _pending.Clear();
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    var live = session;
                    new Thread(() =>
                    {
                        try { live.Source.Process(); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn("Bandwidth trace ended: " + ex.Message); }
                        finally { Task.Run(() => TraceEnded(live)); }
                    }) { IsBackground = true, Name = "Bandwidth trace" }.Start();
                    new Thread(() => { EnsureLoaded(); FlushLoop(token); }) { IsBackground = true, Name = "Bandwidth recorder", Priority = ThreadPriority.BelowNormal }.Start();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The session may not be stored yet: dispose the local one too, or Windows keeps the trace running.
                    try { session?.Dispose(); } catch (Exception inner) when (inner is not OutOfMemoryException) { }
                    StopLocked();
                    Log.Warn("Bandwidth recording could not start: " + ex.Message);
                    return ex.Message;
                }
            }
            return null;
        }

        protected override void StopCore()
        {
            lock (Gate) StopLocked();
        }

        private void StopLocked()
        {
            _cts?.Cancel();
            _cts = null;
            try { _session?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            _session = null;
        }

        /// <summary>The trace ended by itself (Windows closed it, an error): the recorder is no longer recording.</summary>
        private void TraceEnded(TraceEventSession ended)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_session, ended)) return;   // a normal Stop already handled it
                StopLocked();
            }
            FinishStop();
        }

        private void OnEvent(TraceEvent e)
        {
            if (e.ProviderGuid != KernelNetwork) return;
            // Manifest ids: 10/26 TCP sent, 11/27 TCP received (IPv4/IPv6), 42/58 UDP sent, 43/59 UDP received.
            bool? sent = (int)e.ID switch { 10 or 26 or 42 or 58 => true, 11 or 27 or 43 or 59 => false, _ => null };
            if (sent == null) return;
            try
            {
                long size = Convert.ToInt64(e.PayloadByName("size") ?? 0);
                int pid = Convert.ToInt32(e.PayloadByName("PID") ?? e.ProcessID);
                if (size <= 0) return;
                // Traffic to this PC itself (127.x, ::1) is not internet use.
                object? to = e.PayloadByName("daddr");
                if (to is uint dest && (dest & 0xFF) == 127) return;
                if (to is byte[] v6 && IsLoopbackV6(v6)) return;
                string name = NameOf(pid);   // now, not at the next flush: short-lived programs are gone by then
                Interlocked.Increment(ref _events);
                lock (_pending)
                {
                    _pending.TryGetValue(name, out var p);
                    _pending[name] = sent.Value ? (p.Down, p.Up + size) : (p.Down + size, p.Up);
                }
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { }
        }

        private void FlushLoop(CancellationToken token)
        {
            while (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(AppUsageStore.SampleSeconds)))
            {
                Flush();
                SaveIfDue();
            }
        }

        protected override void Flush()
        {
            Dictionary<string, (long Down, long Up)> take;
            lock (_pending) { take = new Dictionary<string, (long, long)>(_pending); _pending.Clear(); }
            var now = DateTime.UtcNow;
            Store.Tick(now);
            foreach (var (name, v) in take)
            {
                Store.Add(now, name, SlotDown, v.Down);
                Store.Add(now, name, SlotUp, v.Up);
            }
        }

        private string NameOf(int pid)
        {
            if (pid == 0 || pid == 4) return "System";
            lock (_names) return NameOfLocked(pid);
        }

        private string NameOfLocked(int pid)
        {
            var now = DateTime.UtcNow;
            // A number can be given to another program later: a remembered name is only trusted for a few minutes.
            if (_names.TryGetValue(pid, out var known) && now - known.At < NameLife) return known.Name;
            string name;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; _names[pid] = (name, now); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { name = EndedName; }
            if (_names.Count > 4000) _names.Clear();
            return name;
        }

        private static bool IsLoopbackV6(byte[] a) => a.Length == 16 && a[15] == 1 && a.Take(15).All(x => x == 0);
    }

    /// <summary>What Bandwidth per App can measure. The page, ranking and chart come from <see cref="UsageBoardBuilder"/>.</summary>
    public static class BandwidthManager
    {
        private const double Step = AppUsageStore.SampleSeconds;

        public static IReadOnlyList<UsageMetric> Metrics { get; } = new[]
        {
            Of("Total", "Str_Bandwidth_Total", s => s[BandwidthRecorder.SlotDown] + s[BandwidthRecorder.SlotUp]),
            Of("Download", "Str_Bandwidth_Download", s => s[BandwidthRecorder.SlotDown]),
            Of("Upload", "Str_Bandwidth_Upload", s => s[BandwidthRecorder.SlotUp]),
        };

        private static UsageMetric Of(string name, string labelKey, Func<double[], double> sum) => new()
        {
            Name = name,
            LabelKey = labelKey,
            Sum = sum,
            Den = b => b.Samples * Step,
            PeakOf = (s, b) => sum(s) / Math.Max(Step, b.Samples * Step),
            Rank = (total, _) => total,
            ValueText = UsageFormat.Bytes,
            ChartFormat = UsageFormat.Rate,
            SecondText = (peak, _, _) => UsageFormat.Rate(peak),
            ValueHeaderKey = "Str_Bandwidth_ColTotal",
            PeakHeaderKey = "Str_Bandwidth_ColFastest",
            Middle = (buckets, _) => new UsageHighlight(
                MaterialDesignThemes.Wpf.PackIconKind.SwapVertical,
                Loc.T("Str_Bandwidth_StatSplit", "Received and sent"),
                "↓ " + UsageFormat.Bytes(buckets.Sum(b => b.Apps.Values.Sum(s => s[BandwidthRecorder.SlotDown]))),
                "↑ " + UsageFormat.Bytes(buckets.Sum(b => b.Apps.Values.Sum(s => s[BandwidthRecorder.SlotUp])))),
        };
    }
}
