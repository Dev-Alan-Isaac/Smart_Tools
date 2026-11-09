using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Threading.Timer;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Network Speed Widget: reads the adapter byte counters on a timer and raises one
    /// <see cref="NetWidgetReading"/> per refresh on the UI thread. Rate maths, unit
    /// scaling, history and settings are plain logic (no WPF). Created on the UI thread.
    /// </summary>
    public sealed class NetworkSpeedWidgetManager : IDisposable
    {
        /// <summary>What the floating widget draws (60 readings).</summary>
        public const int HistoryCapacity = 60;

        /// <summary>What the page's big graph keeps (5 minutes at the 1 s refresh).</summary>
        public const int PageHistoryCapacity = 300;

        private readonly SynchronizationContext? _ui;
        private readonly object _gate = new();
        private readonly Dictionary<string, (long Rx, long Tx)> _last = new();
        private readonly List<double> _down = new(), _up = new();
        private Timer? _timer;
        private Timer? _changeTimer;
        private DateTime _lastUtc;
        private string? _adapterId;
        private long _sessionDown, _sessionUp;
        private bool _watching, _disposed;

        public NetworkSpeedWidgetManager()
        {
            _ui = SynchronizationContext.Current;
        }

        /// <summary>A new reading (UI thread).</summary>
        public event Action<NetWidgetReading>? ReadingReady;

        /// <summary>Adapters appeared, disappeared or changed address (UI thread, debounced ~1 s).</summary>
        public event Action? AdaptersChanged;

        public bool IsRunning => _timer != null;

        /// <summary>Download rates in bytes/s, oldest first. Read on the UI thread only.</summary>
        public IReadOnlyList<double> DownHistory => _down;

        /// <summary>Upload rates in bytes/s, oldest first. Read on the UI thread only.</summary>
        public IReadOnlyList<double> UpHistory => _up;

        /// <summary>Starts (or restarts) reading. <paramref name="adapterId"/> null = all active adapters.</summary>
        public void Start(string? adapterId, int refreshMs)
        {
            if (_disposed) return;
            refreshMs = Math.Clamp(refreshMs, 250, 10_000);
            lock (_gate)
            {
                _adapterId = adapterId;
                _last.Clear();
                _lastUtc = default;
                _sessionDown = _sessionUp = 0;
                _down.Clear();
                _up.Clear();
                _timer?.Dispose();
                _timer = new Timer(_ => Tick(), null, 0, refreshMs);
            }
            if (!_watching)
            {
                _watching = true;
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            }
        }

        /// <summary>Stops reading and unhooks the (static) network events; returns at once.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _changeTimer?.Dispose();
                _changeTimer = null;
            }
            if (_watching)
            {
                _watching = false;
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private void OnNetworkChanged(object? sender, EventArgs e)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _changeTimer?.Dispose();
                _changeTimer = new Timer(_ => Post(() => AdaptersChanged?.Invoke()), null, 1000, Timeout.Infinite);
            }
        }

        private void Post(Action action)
        {
            if (_disposed) return;
            if (_ui != null) _ui.Post(_ => { if (!_disposed) action(); }, null);
            else action();
        }

        private void Tick()
        {
            NetWidgetReading? reading;
            lock (_gate)
            {
                if (_timer == null || _disposed) return;
                reading = Sample();
            }
            if (reading == null) return;
            var r = reading;
            Post(() =>
            {
                Push(_down, r.DownBytesPerSec);
                Push(_up, r.UpBytesPerSec);
                ReadingReady?.Invoke(r);
            });
        }

        private NetWidgetReading? Sample()
        {
            var now = DateTime.UtcNow;
            double seconds = _lastUtc == default ? 0 : (now - _lastUtc).TotalSeconds;
            _lastUtc = now;

            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { return null; }

            // A remembered adapter that is gone falls back to "all active".
            var chosen = _adapterId == null ? null : nics.FirstOrDefault(n => n.Id == _adapterId);
            double down = 0, up = 0;
            int active = 0;
            var seen = new HashSet<string>();
            foreach (var nic in chosen != null ? new[] { chosen } : nics)
            {
                if (!IsCandidate(nic) || nic.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    long rx = stats.BytesReceived, tx = stats.BytesSent;
                    seen.Add(nic.Id);
                    active++;
                    if (_last.TryGetValue(nic.Id, out var before) && seconds > 0)
                    {
                        long dRx = Delta(before.Rx, rx), dTx = Delta(before.Tx, tx);
                        _sessionDown += dRx;
                        _sessionUp += dTx;
                        down += dRx / seconds;
                        up += dTx / seconds;
                    }
                    _last[nic.Id] = (rx, tx);
                }
                catch (NetworkInformationException) { }
            }
            foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);

            return new NetWidgetReading(down, up, _sessionDown, _sessionUp, chosen == null ? null : Describe(chosen), active);
        }

        private static void Push(List<double> list, double value)
        {
            list.Add(value);
            if (list.Count > PageHistoryCapacity) list.RemoveRange(0, list.Count - PageHistoryCapacity);
        }

        // =================================================================
        //  Adapters
        // =================================================================

        private static bool IsCandidate(NetworkInterface nic) =>
            nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel);

        /// <summary>Every real adapter (connected ones first), with MAC and manufacturer.</summary>
        public static List<NetWidgetAdapter> ListAdapters()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(IsCandidate)
                    .Select(Describe)
                    .OrderByDescending(a => a.IsUp)
                    .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (NetworkInformationException)
            {
                return new List<NetWidgetAdapter>();
            }
        }

        private static NetWidgetAdapter Describe(NetworkInterface nic)
        {
            string? mac = null;
            try { mac = FormatMac(nic.GetPhysicalAddress().GetAddressBytes()); }
            catch (NetworkInformationException) { }
            return new NetWidgetAdapter(nic.Id, nic.Name, nic.Description, mac,
                OuiLookupManager.Shared.Lookup(mac), nic.OperationalStatus == OperationalStatus.Up);
        }

        /// <summary>AA-BB-CC-DD-EE-FF, or null for an empty / too short address.</summary>
        public static string? FormatMac(byte[]? bytes) =>
            bytes == null || bytes.Length < 6 ? null : string.Join("-", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        // =================================================================
        //  Rate maths and text (pure)
        // =================================================================

        /// <summary>Bytes counted between two readings; a counter that went down (reset / wrap) counts as 0.</summary>
        public static long Delta(long before, long now) => now >= before ? now - before : 0;

        /// <summary>Bytes per second between two counter readings over <paramref name="seconds"/>.</summary>
        public static double ByteRate(long before, long now, double seconds) =>
            seconds <= 0 ? 0 : Delta(before, now) / seconds;

        /// <summary>Auto-scaled rate: value text and unit ("1.5", "MB/s"). Bits use 1000s, bytes use 1024s.</summary>
        public static (string Value, string Unit) FormatRate(double bytesPerSec, NetWidgetUnits units)
        {
            if (bytesPerSec < 0 || double.IsNaN(bytesPerSec)) bytesPerSec = 0;
            if (units == NetWidgetUnits.Bits)
            {
                double bits = bytesPerSec * 8;
                string[] names = { "bps", "Kbps", "Mbps", "Gbps" };
                int i = 0;
                while (bits >= 1000 && i < names.Length - 1) { bits /= 1000; i++; }
                return (Scaled(bits), names[i]);
            }
            string[] byteNames = { "B/s", "KB/s", "MB/s", "GB/s" };
            double v = bytesPerSec;
            int j = 0;
            while (v >= 1024 && j < byteNames.Length - 1) { v /= 1024; j++; }
            return (Scaled(v), byteNames[j]);
        }

        private static string Scaled(double v) =>
            v >= 100 ? v.ToString("0", CultureInfo.CurrentCulture)
            : v >= 10 ? v.ToString("0.0", CultureInfo.CurrentCulture)
            : v.ToString("0.00", CultureInfo.CurrentCulture);

        /// <summary>Total bytes as text ("12.4 MB").</summary>
        public static string FormatTotal(long bytes)
        {
            string[] names = { "B", "KB", "MB", "GB", "TB" };
            double v = Math.Max(0, bytes);
            int i = 0;
            while (v >= 1024 && i < names.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0", CultureInfo.CurrentCulture) : v.ToString("0.0", CultureInfo.CurrentCulture)) + " " + names[i];
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\net_speed_widget.json - preferences only)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "net_speed_widget.json");

        public static NetWidgetSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new NetWidgetSettings();
                var s = JsonSerializer.Deserialize<NetWidgetSettings>(File.ReadAllText(file), SettingsJson) ?? new NetWidgetSettings();
                if (!Enum.IsDefined(s.Units)) s.Units = NetWidgetUnits.Bytes;
                s.RefreshMs = Math.Clamp(s.RefreshMs, 250, 10_000);
                s.Opacity = double.IsNaN(s.Opacity) ? 0.9 : Math.Clamp(s.Opacity, 0.3, 1.0);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new NetWidgetSettings();
            }
        }

        public static void SaveSettings(NetWidgetSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort: AtomicFile already removed its temp file
            }
        }
    }
}
