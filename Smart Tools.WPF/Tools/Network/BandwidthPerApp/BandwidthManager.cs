using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Smart_Tools.WPF.Controls;
using System.ComponentModel;
using System.Diagnostics;

namespace Smart_Tools.WPF.Controllers
{
    public enum BandwidthMetric { Total, Download, Upload }

    public sealed class BandwidthSettings
    {
        public bool Recording { get; set; }
        public int PeriodHours { get; set; } = 3;
        public BandwidthMetric Metric { get; set; } = BandwidthMetric.Total;
    }

    /// <summary>
    /// Counts the bytes each program sends and receives, from Windows' own network trace (the "Microsoft-Windows-Kernel-Network" event
    /// provider: TCP and UDP, IPv4 and IPv6, one event per data transfer with the program's id and the size). Windows only allows
    /// reading that trace to an administrator. Only totals per program name are kept (per minute, for a day), never addresses or content.
    /// </summary>
    public sealed class BandwidthRecorder
    {
        public static BandwidthRecorder Instance { get; } = new();

        public const string SettingsName = "bandwidth_per_app";
        public const string HistoryName = "bandwidth_per_app_history";
        public const int SlotDown = 0, SlotUp = 1, SlotCount = 2;

        private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
        private const string SessionName = "SmartToolsBandwidth";

        public AppUsageStore Store { get; } = new(HistoryName, SlotCount, TimeSpan.FromHours(24));

        private readonly object _gate = new();
        private readonly Dictionary<int, (long Down, long Up)> _pending = new();
        private readonly Dictionary<int, string> _names = new();
        private TraceEventSession? _session;
        private CancellationTokenSource? _cts;
        private bool _loaded;
        private DateTime _lastSave = DateTime.UtcNow;
        private long _events;

        public bool IsRunning { get { lock (_gate) return _session != null; } }

        /// <summary>How many transfers were seen since recording started (0 for a while after starting: nothing may be moving).</summary>
        public long EventsSeen => Interlocked.Read(ref _events);

        public void EnsureLoaded()
        {
            lock (_gate) { if (_loaded) return; Store.Load(); _loaded = true; }
        }

        /// <summary>Starts counting. Returns null, or why it couldn't (not administrator, the trace is busy...).</summary>
        public string? Start()
        {
            lock (_gate)
            {
                if (_session != null) return null;
                if (!HardwareSensorsManager.IsAdministrator()) return "NotAdministrator";
                if (!_loaded) { Store.Load(); _loaded = true; }
                try
                {
                    var session = new TraceEventSession(SessionName);
                    session.EnableProvider(KernelNetwork);
                    session.Source.Dynamic.All += OnEvent;
                    _session = session;
                    Interlocked.Exchange(ref _events, 0);
                    _pending.Clear();
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    new Thread(() => { try { session.Source.Process(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn("Bandwidth trace ended: " + ex.Message); } })
                        { IsBackground = true, Name = "Bandwidth trace" }.Start();
                    new Thread(() => FlushLoop(token)) { IsBackground = true, Name = "Bandwidth recorder", Priority = ThreadPriority.BelowNormal }.Start();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception or System.Runtime.InteropServices.COMException or NotSupportedException)
                {
                    StopLocked();
                    return ex.Message;
                }
            }
            return null;
        }

        public void Stop()
        {
            lock (_gate) StopLocked();
            Flush();
            Store.Save();
        }

        private void StopLocked()
        {
            _cts?.Cancel();
            _cts = null;
            try { _session?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            _session = null;
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
                // Traffic to this PC itself (127.x) is not internet use.
                if (e.PayloadByName("daddr") is uint dest && (dest & 0xFF) == 127) return;
                Interlocked.Increment(ref _events);
                lock (_pending)
                {
                    _pending.TryGetValue(pid, out var p);
                    _pending[pid] = sent.Value ? (p.Down, p.Up + size) : (p.Down + size, p.Up);
                }
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { }
        }

        private void FlushLoop(CancellationToken token)
        {
            while (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Flush();
                if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(2)) { _lastSave = DateTime.UtcNow; Store.Save(); }
            }
        }

        private void Flush()
        {
            Dictionary<int, (long Down, long Up)> take;
            lock (_pending) { take = new Dictionary<int, (long, long)>(_pending); _pending.Clear(); }
            var now = DateTime.UtcNow;
            Store.Tick(now);
            foreach (var (pid, v) in take)
            {
                string name = NameOf(pid);
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
            if (_names.TryGetValue(pid, out string? known)) return known;
            string name;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { name = "(ended) #" + pid; }
            if (_names.Count > 4000) _names.Clear();
            if (!name.StartsWith("(ended)")) _names[pid] = name;
            return name;
        }
    }

    public static class BandwidthManager
    {
        public static UsageBoardData Build(AppUsageStore store, BandwidthMetric metric, TimeSpan period)
        {
            var since = DateTime.UtcNow - period;
            var buckets = store.Since(since);

            double Of(double[] s) => metric switch
            {
                BandwidthMetric.Download => s[BandwidthRecorder.SlotDown],
                BandwidthMetric.Upload => s[BandwidthRecorder.SlotUp],
                _ => s[BandwidthRecorder.SlotDown] + s[BandwidthRecorder.SlotUp],
            };

            var totals = new Dictionary<string, (double Sum, double Peak, double Down, double Up)>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in buckets)
                foreach (var (app, slots) in b.Apps)
                {
                    totals.TryGetValue(app, out var t);
                    totals[app] = (t.Sum + Of(slots), Math.Max(t.Peak, Of(slots) / Math.Max(5.0, b.Samples * 5.0)), t.Down + slots[BandwidthRecorder.SlotDown], t.Up + slots[BandwidthRecorder.SlotUp]);
                }
            var ranked = totals.Where(kv => kv.Value.Sum > 0).OrderByDescending(kv => kv.Value.Sum).Take(40).ToList();
            double top = ranked.Count > 0 ? ranked[0].Value.Sum : 1;
            var rows = ranked.Select((kv, i) => new UsageRow
            {
                Rank = i + 1,
                Name = kv.Key,
                ValueText = UsageMath.Bytes(kv.Value.Sum),
                PeakText = UsageMath.Rate(kv.Value.Peak),
                Detail = $"↓ {UsageMath.Bytes(kv.Value.Down)}   ↑ {UsageMath.Bytes(kv.Value.Up)}",
                Share = top > 0 ? kv.Value.Sum / top * 100.0 : 0,
            }).ToList();

            return new UsageBoardData
            {
                Rows = rows,
                Series = name => UsageMath.Series(buckets, since, period, name, Of, b => b.Samples * 5.0),
                ChartFormat = UsageMath.Rate,
                ValueHeaderKey = "Str_Bandwidth_ColTotal",
                PeakHeaderKey = "Str_Bandwidth_ColFastest",
            };
        }
    }
}
