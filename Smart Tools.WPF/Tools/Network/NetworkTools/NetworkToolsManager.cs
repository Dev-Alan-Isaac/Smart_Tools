using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Network Tools - everything that isn't UI: parsing hosts, ports and
    /// hex, ping with statistics, adapter and public-IP info, and settings.
    /// The port scanner is in NetworkToolsManager.Scan.cs, the TCP/UDP
    /// client in NetworkToolsManager.Socket.cs.
    /// </summary>
    public static partial class NetworkToolsManager
    {
        public const int PingTimeoutMs = 2000;

        /// <summary>Console lines kept (a chatty socket used to grow the old TextBox without limit).</summary>
        public const int MaxConsoleLines = 5000;

        // =================================================================
        //  Hosts
        // =================================================================

        /// <summary>
        /// Trims what people paste: "https://example.com:8080/path" -> "example.com",
        /// "[::1]" -> "::1". Returns null when nothing usable is left.
        /// </summary>
        public static string? CleanHost(string? input)
        {
            string s = (input ?? "").Trim();
            if (s.Length == 0) return null;
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && s.Contains("://", StringComparison.Ordinal))
                s = uri.Host;
            s = s.Trim('[', ']');
            if (IPAddress.TryParse(s, out _)) return s;
            int colon = s.IndexOf(':');
            if (colon > 0 && s.IndexOf(':', colon + 1) < 0) s = s[..colon];   // host:port
            int slash = s.IndexOf('/');
            if (slash > 0) s = s[..slash];
            return s.Length == 0 || s.Any(char.IsWhiteSpace) ? null : s;
        }

        /// <summary>
        /// Resolves once, preferring IPv4 (the old scanner resolved the name
        /// again for every port, and an unknown name reported "no open ports"
        /// instead of "can't find that host").
        /// </summary>
        public static async Task<IPAddress> ResolveAsync(string host, CancellationToken token, AddressFamily? family = null)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            IPAddress[] all;
            try
            {
                all = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"Can't find \"{host}\" - check the name ({ex.SocketErrorCode}).", ex);
            }
            var pick = family != null
                ? all.FirstOrDefault(a => a.AddressFamily == family)
                : all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
            return pick ?? throw new InvalidOperationException(family == AddressFamily.InterNetwork
                ? $"\"{host}\" has no IPv4 address."
                : $"Can't find \"{host}\".");
        }

        // =================================================================
        //  Ports
        // =================================================================

        /// <summary>Common services, for labels and the "Common" preset.</summary>
        public static readonly IReadOnlyDictionary<int, string> Services = new Dictionary<int, string>
        {
            [20] = "FTP data", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS",
            [67] = "DHCP", [69] = "TFTP", [80] = "HTTP", [110] = "POP3", [119] = "NNTP", [123] = "NTP",
            [135] = "RPC", [137] = "NetBIOS", [139] = "NetBIOS", [143] = "IMAP", [161] = "SNMP", [389] = "LDAP",
            [443] = "HTTPS", [445] = "SMB", [465] = "SMTPS", [514] = "Syslog", [548] = "AFP", [554] = "RTSP",
            [587] = "SMTP", [631] = "IPP", [636] = "LDAPS", [873] = "rsync", [993] = "IMAPS", [995] = "POP3S",
            [1080] = "SOCKS", [1433] = "SQL Server", [1521] = "Oracle", [1723] = "PPTP", [1883] = "MQTT",
            [2049] = "NFS", [3000] = "Dev server", [3306] = "MySQL", [3389] = "RDP", [5000] = "Dev server",
            [5353] = "mDNS", [5432] = "PostgreSQL", [5900] = "VNC", [5985] = "WinRM", [6379] = "Redis",
            [8000] = "HTTP alt", [8080] = "HTTP alt", [8443] = "HTTPS alt", [8883] = "MQTT TLS", [9000] = "",
            [9100] = "Printer", [9200] = "Elasticsearch", [11211] = "Memcached", [25565] = "Minecraft", [27017] = "MongoDB",
        };

        public static string ServiceName(int port) => Services.TryGetValue(port, out var s) ? s : "";

        public static IReadOnlyList<int> CommonPorts => Services.Where(kv => kv.Value.Length > 0).Select(kv => kv.Key).Order().ToList();

        /// <summary>
        /// "22, 80, 443, 8000-8100" (or "common") -> sorted distinct ports.
        /// Returns an error instead of throwing.
        /// </summary>
        public static (List<int> Ports, string? Error) ParsePorts(string? text)
        {
            string s = (text ?? "").Trim();
            if (s.Length == 0) return (new(), "Enter ports, for example 80, 443 or 1-1024.");
            if (s.Equals("common", StringComparison.OrdinalIgnoreCase)) return (CommonPorts.ToList(), null);
            if (s.Equals("all", StringComparison.OrdinalIgnoreCase)) s = "1-65535";

            var ports = new SortedSet<int>();
            foreach (string raw in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                int dash = part.IndexOf('-', 1);
                if (dash > 0)
                {
                    if (!TryPort(part[..dash], out int a) || !TryPort(part[(dash + 1)..], out int b))
                        return (new(), $"\"{part}\" isn't a valid range - ports go from 1 to 65535.");
                    if (a > b) (a, b) = (b, a);
                    for (int p = a; p <= b; p++) ports.Add(p);
                }
                else if (TryPort(part, out int p))
                {
                    ports.Add(p);
                }
                else
                {
                    return (new(), $"\"{part}\" isn't a port - use numbers from 1 to 65535.");
                }
            }
            return (ports.ToList(), null);
        }

        public static bool TryPort(string text, out int port) =>
            int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

        // =================================================================
        //  Hex and text
        // =================================================================

        /// <summary>Accepts "41 42 0D 0A", "41-42", "0x41,0x42", "4142", "41:42".</summary>
        public static bool TryParseHex(string? text, out byte[] data)
        {
            data = Array.Empty<byte>();
            string s = (text ?? "").Replace("0x", " ", StringComparison.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (Uri.IsHexDigit(c)) sb.Append(c);
                else if (c is ' ' or '-' or ',' or ':' or '\t' or '\r' or '\n') continue;
                else return false;
            }
            if (sb.Length == 0 || sb.Length % 2 != 0) return false;
            data = Convert.FromHexString(sb.ToString());
            return true;
        }

        public static string FormatHex(ReadOnlySpan<byte> data, int max = 4096)
        {
            string hex = Convert.ToHexString(data.Length > max ? data[..max] : data);
            var sb = new StringBuilder(hex.Length * 3 / 2);
            for (int i = 0; i < hex.Length; i += 2)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(hex, i, 2);
            }
            if (data.Length > max) sb.Append($" ... (+{data.Length - max} bytes)");
            return sb.ToString();
        }

        /// <summary>
        /// Shows control characters instead of letting them garble the console:
        /// CR/LF are dropped at the end and shown as ↵ inside, other controls as ␀-style symbols.
        /// </summary>
        public static string Printable(string text)
        {
            string trimmed = text.TrimEnd('\r', '\n');
            var sb = new StringBuilder(trimmed.Length);
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '\r' && i + 1 < trimmed.Length && trimmed[i + 1] == '\n') continue;
                if (c is '\n' or '\r') sb.Append('↵').Append('\n');
                else if (c == '\t') sb.Append(c);
                else if (c < 0x20) sb.Append((char)(0x2400 + c));   // Unicode "control pictures"
                else if (c == 0x7F) sb.Append('␡');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Bytes to send: text (UTF-8) or hex, plus CR LF when asked.</summary>
        public static (byte[] Data, string Display, string? Error) BuildPayload(string input, bool hex, bool appendNewline)
        {
            byte[] data;
            string display;
            if (hex)
            {
                if (!TryParseHex(input, out data))
                    return (Array.Empty<byte>(), "", "That isn't valid hex - use byte pairs like 41 42 0D 0A.");
                display = FormatHex(data);
            }
            else
            {
                data = Encoding.UTF8.GetBytes(input);
                display = input;
            }
            if (appendNewline)
            {
                data = data.Concat(new byte[] { 0x0D, 0x0A }).ToArray();
                display += hex ? " 0D 0A" : " ↵";
            }
            if (data.Length == 0) return (data, display, "Type something to send.");
            return (data, display, null);
        }

        // =================================================================
        //  Ping
        // =================================================================

        /// <summary>Tests replace the pinger (ICMP needs rights a test run may not have).</summary>
        public static Func<IPAddress, int, CancellationToken, Task<PingSample>>? PingOverride { get; set; }

        /// <summary>
        /// Pings <paramref name="count"/> times (0 = until cancelled), one per
        /// second like the ping command, reporting each reply. Resolves the
        /// name once. With <paramref name="source"/>, every ping leaves from that
        /// address (that adapter) - the host is then looked up as IPv4.
        /// </summary>
        public static async Task<PingStats> PingAsync(string host, int count, IProgress<PingSample>? progress, CancellationToken token, TimeSpan? interval = null, IPAddress? source = null)
        {
            var address = await ResolveAsync(host, token, source?.AddressFamily).ConfigureAwait(false);
            var stats = new PingStats();
            var gap = interval ?? TimeSpan.FromSeconds(1);
            using var ping = new Ping();

            for (int seq = 1; count == 0 || seq <= count; seq++)
            {
                token.ThrowIfCancellationRequested();
                var started = DateTime.UtcNow;
                PingSample sample;
                if (PingOverride != null)
                {
                    sample = await PingOverride(address, seq, token).ConfigureAwait(false);
                }
                else if (source != null)
                {
                    int n = seq;
                    sample = await Task.Run(() => PingFrom(source, address, n, PingTimeoutMs), token).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(PingTimeoutMs), null, null, token).ConfigureAwait(false);
                        sample = reply.Status == IPStatus.Success
                            ? new PingSample(seq, reply.Address.ToString(), "Reply", reply.RoundtripTime, reply.Options?.Ttl)
                            : new PingSample(seq, address.ToString(), Describe(reply.Status), null, null);
                    }
                    catch (PingException ex)
                    {
                        sample = new PingSample(seq, address.ToString(), ex.InnerException?.Message ?? ex.Message, null, null);
                    }
                }
                stats.Add(sample);
                progress?.Report(sample);

                if (count != 0 && seq == count) break;
                var wait = gap - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            }
            return stats;
        }

        public static string Describe(IPStatus status) => status switch
        {
            IPStatus.TimedOut => "Timed out",
            IPStatus.DestinationHostUnreachable => "Host unreachable",
            IPStatus.DestinationNetworkUnreachable => "Network unreachable",
            IPStatus.TtlExpired => "TTL expired on the way",
            IPStatus.DestinationPortUnreachable => "Port unreachable",
            _ => status.ToString(),
        };

        public static string DescribePing(PingSample s) => s.Success
            ? $"#{s.Sequence} reply from {s.Address}: {s.RoundtripMs} ms" + (s.Ttl is int ttl ? $", TTL {ttl}" : "")
            : $"#{s.Sequence} {s.Status}";

        public static string DescribeStats(string host, PingStats st) =>
            $"{host}: {st.Sent} sent, {st.Received} received, {st.LossPercent:0}% lost" +
            (st.Received > 0 ? $" · min {st.Min} / avg {st.Average:0} / max {st.Max} ms" + (st.Jitter is double j ? $" · jitter {j:0} ms" : "") : "");

        /// <summary>Bars for the latency strip, scaled to the slowest reply (at least 50 ms).</summary>
        public static List<LatencyBar> LatencyBars(IReadOnlyList<PingSample> samples, int max = 40)
        {
            var last = samples.Skip(Math.Max(0, samples.Count - max)).ToList();
            double top = Math.Max(50, last.Where(s => s.Success).Select(s => (double)s.RoundtripMs!.Value).DefaultIfEmpty(0).Max());
            return last.Select(s => s.Success
                    ? new LatencyBar(Math.Max(0.06, s.RoundtripMs!.Value / top), false, $"#{s.Sequence}: {s.RoundtripMs} ms")
                    : new LatencyBar(1, true, $"#{s.Sequence}: {s.Status}"))
                .ToList();
        }

        // =================================================================
        //  IP info (adapters are in NetworkToolsManager.Adapters.cs)
        // =================================================================

        private static List<string> SafeList(Func<List<string>> get)
        {
            try
            {
                return get();
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                return new();
            }
        }

        private static long SafeSpeed(NetworkInterface nic)
        {
            try
            {
                return nic.Speed is > 0 and <= 800_000_000_000 ? nic.Speed : 0;   // virtual adapters report nonsense like 4 Pbps
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                return 0;
            }
        }

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

        /// <summary>The address the internet sees (asks api.ipify.org). One shared HttpClient - the old code made one per lookup.</summary>
        public static async Task<string> GetPublicIpAsync(CancellationToken token)
        {
            string text = (await Http.GetStringAsync("https://api.ipify.org", token).ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out var ip) ? ip.ToString() : throw new InvalidDataException("The lookup service sent something that isn't an IP address.");
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "network_tools.json");

        public static NetworkSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new NetworkSettings();
                var s = JsonSerializer.Deserialize<NetworkSettings>(File.ReadAllText(file), JsonOptions) ?? new NetworkSettings();
                s.PingHost ??= "";
                s.ScanHost ??= "";
                s.ScanPorts ??= "1-1024";
                s.SocketHost ??= "";
                s.PingCount = s.PingCount is 0 or 4 or 10 or 50 ? s.PingCount : 4;
                if (!TryPort(s.SocketPort.ToString(CultureInfo.InvariantCulture), out _)) s.SocketPort = 9000;
                s.LocalPort = Math.Clamp(s.LocalPort, 0, 65535);
                s.ScanTimeoutMs = s.ScanTimeoutMs is 300 or 800 or 2000 ? s.ScanTimeoutMs : 800;
                s.PingSourceId ??= "";
                s.ScanSourceId ??= "";
                s.SocketSourceId ??= "";
                return s;
            }
            catch (Exception)
            {
                return new NetworkSettings();
            }
        }

        private static readonly object SettingsGate = new();

        /// <summary>
        /// Changes only what <paramref name="change"/> touches: reads the file
        /// fresh, applies the change, writes it back. The four network tools share
        /// one settings file, so saving a whole copy loaded earlier would undo what
        /// another open tool saved in the meantime.
        /// </summary>
        public static void UpdateSettings(Action<NetworkSettings> change, string? path = null)
        {
            lock (SettingsGate)
            {
                var s = LoadSettings(path);
                change(s);
                SaveSettings(s, path);
            }
        }

        public static void SaveSettings(NetworkSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
