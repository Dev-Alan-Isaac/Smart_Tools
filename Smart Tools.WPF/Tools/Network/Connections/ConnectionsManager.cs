using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Threading.Timer;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Connections: reads Windows' TCP/UDP table on a timer (see <see cref="TcpUdpTable"/>), names the app behind each
    /// endpoint, and says which remote addresses are outside this PC's own network. Raises one list per reading on
    /// the UI thread. The classification, filtering, CSV and settings are plain logic (no WPF). Created on the UI thread.
    /// </summary>
    public sealed class ConnectionsManager : IDisposable
    {
        private const int MaxParallelLookups = 8;
        private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

        private readonly SynchronizationContext? _ui;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _lookupSlots = new(MaxParallelLookups);
        private readonly ConcurrentDictionary<string, string> _hosts = new();
        private Dictionary<int, string> _appNames = new();
        private HashSet<string>? _previousKeys;
        private Timer? _timer;
        private int _reading;
        private int _generation;
        private bool _disposed;

        public ConnectionsManager() => _ui = SynchronizationContext.Current;

        /// <summary>A new reading, and the keys that weren't in the one before (empty for the first). UI thread.</summary>
        public event Action<IReadOnlyList<ConnectionInfo>, IReadOnlySet<string>>? Updated;

        public bool IsRunning => _timer != null;

        /// <summary>Starts (or restarts) reading every <paramref name="seconds"/>; reads once right away.</summary>
        public void Start(int seconds)
        {
            if (_disposed) return;
            seconds = Math.Clamp(seconds, 1, 60);
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = new Timer(_ => ReadNow(), null, 0, seconds * 1000);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _generation++;   // a reading still in flight is dropped
            }
        }

        /// <summary>One reading now, even while paused.</summary>
        public void ReadNow()
        {
            if (_disposed || Interlocked.Exchange(ref _reading, 1) == 1) return;
            int generation = _generation;
            try
            {
                var list = Read();
                var keys = list.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                IReadOnlySet<string> added;
                lock (_gate)
                {
                    added = _previousKeys == null ? new HashSet<string>() : keys.Where(k => !_previousKeys.Contains(k)).ToHashSet();
                    _previousKeys = keys;
                }
                Post(() =>
                {
                    if (!_disposed && generation == _generation) Updated?.Invoke(list, added);
                });
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't read the network connections", ex);
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        }

        private void Post(Action action)
        {
            if (_ui != null) _ui.Post(_ => action(), null);
            else action();
        }

        private List<ConnectionInfo> Read()
        {
            var uses = TcpUdpTable.ReadAll();
            var names = new Dictionary<int, string>();
            var result = new List<ConnectionInfo>(uses.Count);
            foreach (var u in uses)
            {
                if (!names.TryGetValue(u.Pid, out string? app))
                {
                    app = _appNames.TryGetValue(u.Pid, out string? known) ? known : AppNameOf(u.Pid);
                    names[u.Pid] = app;
                }
                bool listening = u.State == "Listen" || u.RemoteAddress == "*";
                result.Add(new ConnectionInfo(
                    Key: string.Join('|', u.Protocol, u.Pid, u.LocalAddress, u.LocalPort, u.RemoteAddress, u.RemotePort),
                    Pid: u.Pid,
                    AppName: app,
                    Protocol: u.Protocol,
                    LocalAddress: u.LocalAddress,
                    LocalPort: u.LocalPort,
                    RemoteAddress: u.RemoteAddress,
                    RemotePort: u.RemotePort,
                    State: u.State,
                    IsOutside: !listening && IsOutsideAddress(u.RemoteAddress),
                    IsListening: listening));
            }
            _appNames = names;   // only the PIDs still in use stay cached, so a reused PID is looked up again
            return result;
        }

        /// <summary>The process name for a PID, or a placeholder when Windows won't say.</summary>
        private static string AppNameOf(int pid)
        {
            if (pid is 0 or 4) return "System";
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return "Unknown";   // it ended since the table was read, or Windows keeps its details private
            }
        }

        // =================================================================
        //  Outside or local
        // =================================================================

        /// <summary>
        /// True when <paramref name="address"/> is somewhere beyond this PC and its own network: not loopback, not a
        /// private, link-local, carrier-grade NAT (100.64/10), or multicast address, and not a "no address" placeholder.
        /// </summary>
        public static bool IsOutsideAddress(string address)
        {
            if (!IPAddress.TryParse(address, out var ip)) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;

            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return !(b[0] == 10
                    || (b[0] == 172 && b[1] is >= 16 and <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] is >= 64 and <= 127)
                    || b[0] >= 224);   // multicast and reserved
            }
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);   // fe80::/10, fec0::/10, ff00::/8, fc00::/7
        }

        // =================================================================
        //  Filtering and numbers
        // =================================================================

        /// <summary>Whether a row passes the scope, the "hide listening ports" option and the search text.</summary>
        public static bool Matches(ConnectionRow row, ConnectionScope scope, bool hideListening, string query)
        {
            var c = row.Info;
            if (scope == ConnectionScope.Outside && !c.IsOutside) return false;
            if (scope == ConnectionScope.Local && c.IsOutside) return false;
            if (hideListening && c.IsListening) return false;

            string q = query.Trim();
            if (q.Length == 0) return true;
            return c.AppName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || c.RemoteAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
                || row.Host.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || c.Pid.ToString().Equals(q, StringComparison.Ordinal)
                || c.RemotePort.ToString().Equals(q, StringComparison.Ordinal)
                || c.LocalPort.ToString().Equals(q, StringComparison.Ordinal);
        }

        /// <summary>Connections that are open or opening to somewhere outside (not closing, not waiting to be cleaned up).</summary>
        public static IEnumerable<ConnectionInfo> ActiveOutside(IEnumerable<ConnectionInfo> all) =>
            all.Where(c => c.IsOutside && c.State is not ("TimeWait" or "Closed" or "DeleteTcb" or "LastAck"));

        // =================================================================
        //  Host names (only when asked for)
        // =================================================================

        /// <summary>The host name for a remote address, or "" when there is none. Cached, limited, and cancelable.</summary>
        public async Task<string> LookUpHostAsync(string address, CancellationToken token)
        {
            if (_hosts.TryGetValue(address, out string? cached)) return cached;
            if (!IPAddress.TryParse(address, out var ip)) return "";

            await _lookupSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string host;
                try
                {
                    var lookup = Dns.GetHostEntryAsync(ip);
                    // WaitAsync stops waiting on a timeout or cancel but the lookup keeps going; read its failure so it isn't reported later as unobserved.
                    _ = lookup.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    host = (await lookup.WaitAsync(LookupTimeout, token).ConfigureAwait(false)).HostName;
                }
                catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
                {
                    host = "";   // no reverse record, or no answer in time: remember that too, so it isn't asked again
                }
                _hosts[address] = host == address ? "" : host;
                return _hosts[address];
            }
            finally
            {
                _lookupSlots.Release();
            }
        }

        // =================================================================
        //  CSV
        // =================================================================

        /// <summary>The rows as CSV (header first), fields quoted when they need it.</summary>
        public static string ToCsv(IEnumerable<ConnectionRow> rows)
        {
            var sb = new StringBuilder("App,PID,Protocol,Local address,Remote address,Remote port,Host,State,Where\r\n");
            foreach (var r in rows)
            {
                var c = r.Info;
                sb.AppendJoin(',', Csv(c.AppName), c.Pid, c.Protocol, Csv(c.LocalAddress + ":" + c.LocalPort), Csv(c.RemoteAddress),
                    c.RemotePort, Csv(r.Host), c.State, c.IsOutside ? "Outside" : "Local").Append("\r\n");
            }
            return sb.ToString();
        }

        private static string Csv(string value) =>
            value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        // =================================================================
        //  Settings (%AppData%\Smart Tools\connections.json - preferences only)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppSettingsStore.SettingsDirectory, "connections.json");

        public static ConnectionsSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ConnectionsSettings();
                var s = JsonSerializer.Deserialize<ConnectionsSettings>(File.ReadAllText(file), SettingsJson) ?? new ConnectionsSettings();
                s.RefreshSeconds = s.RefreshSeconds == 0 ? 0 : Math.Clamp(s.RefreshSeconds, 1, 60);
                return s;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new ConnectionsSettings();   // unreadable settings: start from the defaults
            }
        }

        public static void SaveSettings(ConnectionsSettings settings, string? path = null)
        {
            try
            {
                AtomicFile.WriteAllText(path ?? SettingsPath, JsonSerializer.Serialize(settings, SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Couldn't save the Connections settings", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _lookupSlots.Dispose();
        }
    }
}
