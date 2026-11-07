using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind DeviceScannerView: finds the local networks, sweeps an
    /// address range with async pings, then reads the ARP cache for MAC
    /// addresses - which also reveals devices that ignore ping (most phones,
    /// and Windows PCs with the default firewall) - and resolves hostnames
    /// in the background.
    ///
    /// Results stream: <see cref="DeviceFound"/> fires as each device turns
    /// up, on the thread that created the manager (the UI thread). No WPF
    /// types here - the whole class compiles and tests headlessly.
    /// </summary>
    public sealed partial class DeviceScannerManager
    {
        /// <summary>Largest sweep allowed (a /20). Bigger networks are narrowed to the /24 around this PC.</summary>
        public const int MaxHosts = 4096;
        public const int MaxConcurrency = 256;
        public const int MaxTimeoutMs = 5000;
        private const int HostNameTimeoutMs = 2500;

        private readonly SynchronizationContext? _context;

        /// <summary>A device answered (or was found in the ARP table). Raised on the creating thread.</summary>
        public event EventHandler<DeviceScanResult>? DeviceFound;

        public DeviceScannerManager()
        {
            _context = SynchronizationContext.Current;
        }

        private void Post(Action action)
        {
            if (_context == null) action();
            else _context.Post(_ => action(), null);
        }

        // =================================================================
        //  Scan
        // =================================================================

        public async Task<DeviceScanSummary> ScanAsync(DeviceScanOptions options, IProgress<DeviceScanProgress>? progress, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var range = options.Range;
            var found = new ConcurrentDictionary<uint, DeviceScanResult>();
            var nameLookups = new ConcurrentBag<Task>();
            int total = range.Count, done = 0;
            bool cancelled = false;

            int concurrency = Math.Clamp(options.MaxConcurrency, 1, MaxConcurrency);
            int timeout = Math.Clamp(options.TimeoutMs, 50, MaxTimeoutMs);
            int reportEvery = Math.Max(1, total / 200);

            uint local = TryParseIPv4(options.LocalAddress, out uint l) ? l : 0;
            uint gateway = TryParseIPv4(options.GatewayAddress, out uint g) ? g : 0;
            bool probeArp = options.IncludeArpOnly && local != 0 && options.LocalPrefixLength > 0;

            // Network-wide "who are you" (mDNS + SSDP) runs alongside the sweep; name lookups wait for it.
            var discovery = options.ResolveHostNames
                ? DiscoverNamesAsync(options.LocalAddress, token)
                : Task.FromResult(new DiscoveredNames());

            void Add(DeviceScanResult device)
            {
                if (!found.TryAdd(device.IpSortKey, device)) return;
                Post(() => DeviceFound?.Invoke(this, device));

                if (options.ResolveHostNames && !token.IsCancellationRequested)
                {
                    device.IsResolvingName = true;
                    nameLookups.Add(ResolveNameAsync(device, discovery, token));
                }
            }

            progress?.Report(new DeviceScanProgress(0, total, 0, DeviceScanPhase.Pinging));

            // ---- 1. Ping sweep: async pings, a bounded number in flight ----
            var gate = new SemaphoreSlim(concurrency);
            var tasks = new List<Task>(total);
            try
            {
                for (long next = range.First; next <= range.Last; next++)
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    uint address = (uint)next;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            long? rtt = await PingAsync(address, timeout, token).ConfigureAwait(false);
                            if (rtt is long ms)
                            {
                                Add(new DeviceScanResult
                                {
                                    IPAddress = ToText(address),
                                    IpSortKey = address,
                                    Status = DeviceStatus.Online,
                                    ResponseTimeMs = ms,
                                    IsThisDevice = address == local,
                                    IsGateway = address == gateway,
                                    MacAddress = address == local ? options.LocalMacAddress : null,
                                });
                            }
                            else if (probeArp && address != local && SameSubnet(address, local, options.LocalPrefixLength))
                            {
                                // No ping reply: ask the selected adapter directly (devices behind a USB
                                // Ethernet dongle, or ones with a firewall, still answer ARP).
                                string? mac = await Task.Run(() => ArpProbe(address, local), CancellationToken.None).ConfigureAwait(false);
                                if (mac != null && !token.IsCancellationRequested)
                                {
                                    Add(new DeviceScanResult
                                    {
                                        IPAddress = ToText(address),
                                        IpSortKey = address,
                                        Status = DeviceStatus.NoPingReply,
                                        IsGateway = address == gateway,
                                        MacAddress = mac,
                                    });
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // stopping - this address just isn't checked
                        }
                        finally
                        {
                            gate.Release();
                            int d = Interlocked.Increment(ref done);
                            if (d % reportEvery == 0 || d == total)
                                progress?.Report(new DeviceScanProgress(d, total, found.Count, DeviceScanPhase.Pinging));
                        }
                    }, CancellationToken.None));
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                // Let pings already in flight finish (each is bounded by the timeout)
                // before the semaphore goes away - they still release it.
                await Task.WhenAll(tasks).ConfigureAwait(false);
                gate.Dispose();
            }

            // ---- 2. ARP cache: MACs, plus devices that ignore ping ----
            // (Even when a device drops the ping, Windows had to ARP for it,
            // so it is in the cache for a while afterwards.)
            progress?.Report(new DeviceScanProgress(done, total, found.Count, DeviceScanPhase.ReadingArp));
            var arp = await Task.Run(ReadArpTable, CancellationToken.None).ConfigureAwait(false);

            foreach (var (ipText, mac) in arp)
            {
                if (!TryParseIPv4(ipText, out uint ip) || ip < range.First || ip > range.Last) continue;

                if (found.TryGetValue(ip, out var device))
                {
                    if (device.MacAddress == null) Post(() => device.MacAddress = mac);
                }
                else if (options.IncludeArpOnly && !cancelled)
                {
                    Add(new DeviceScanResult
                    {
                        IPAddress = ipText,
                        IpSortKey = ip,
                        Status = DeviceStatus.NoPingReply,
                        IsThisDevice = ip == local,
                        IsGateway = ip == gateway,
                        MacAddress = mac,
                    });
                }
            }

            // ---- 3. Let outstanding name lookups finish (they have their own timeout) ----
            if (!nameLookups.IsEmpty)
            {
                progress?.Report(new DeviceScanProgress(done, total, found.Count, DeviceScanPhase.ResolvingNames));
                await Task.WhenAll(nameLookups).ConfigureAwait(false);
            }

            cancelled |= token.IsCancellationRequested;
            progress?.Report(new DeviceScanProgress(done, total, found.Count, cancelled ? DeviceScanPhase.Cancelled : DeviceScanPhase.Completed));

            return new DeviceScanSummary
            {
                Devices = found.Values.OrderBy(d => d.IpSortKey).ToList(),
                Range = range,
                Cancelled = cancelled,
                Elapsed = watch.Elapsed,
            };
        }

        /// <summary>Round-trip ms, or null for no reply. Never throws except for cancellation.</summary>
        private static async Task<long?> PingAsync(uint address, int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(new IPAddress(ToBytes(address)), timeoutMs).ConfigureAwait(false);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException or NotSupportedException)
            {
                return null; // no route / unreachable: an empty address, not an error
            }
        }

        /// <summary>
        /// Best name for a device, never throws. Order: the name the user gave it
        /// (Roku, mDNS, SSDP) -> reverse DNS -> NetBIOS / direct mDNS -> its mDNS hostname.
        /// </summary>
        private async Task ResolveNameAsync(DeviceScanResult device, Task<DiscoveredNames> discovery, CancellationToken token)
        {
            string? name = null;
            DiscoveredNames found;
            try { found = await discovery.ConfigureAwait(false); }
            catch (Exception) { found = new DiscoveredNames(); }

            if (!token.IsCancellationRequested)
            {
                try { name = await QueryRokuFriendlyNameAsync(device.IPAddress, token).ConfigureAwait(false); }
                catch (Exception) { /* not a Roku */ }
            }
            // A PC (Windows/Samba) answers NetBIOS with its real computer name. That must beat an mDNS
            // *service* name such as "Spotify Desktop Launcher", which is an app, not the machine.
            if (name == null && !token.IsCancellationRequested && IPAddress.TryParse(device.IPAddress, out var pcIp)
                && pcIp.AddressFamily == AddressFamily.InterNetwork)
            {
                try { name = await QueryNetBiosAsync(pcIp, token).ConfigureAwait(false); }
                catch (Exception) { /* not a NetBIOS host */ }
            }
            name ??= found.Friendly.GetValueOrDefault(device.IPAddress);

            if (name == null)
            {
                try
                {
                    var lookup = Dns.GetHostEntryAsync(device.IPAddress);
                    var winner = await Task.WhenAny(lookup, Task.Delay(HostNameTimeoutMs, token)).ConfigureAwait(false);
                    if (winner == lookup && lookup.IsCompletedSuccessfully)
                    {
                        string host = lookup.Result.HostName;
                        // Some resolvers just echo the IP back - that is not a name.
                        if (!string.IsNullOrWhiteSpace(host) && host != device.IPAddress)
                            name = host;
                    }
                    else
                    {
                        _ = lookup.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe late failures
                    }
                }
                catch (Exception)
                {
                    // no reverse entry, DNS down, or cancelled - fall through to the other lookups
                }
            }

            if (name == null && !token.IsCancellationRequested)
            {
                try { name = await ResolveNameDirectAsync(device.IPAddress, token).ConfigureAwait(false); }
                catch (Exception) { /* fall through */ }
            }
            name ??= found.Host.GetValueOrDefault(device.IPAddress);

            Post(() =>
            {
                device.HostName = name;
                device.IsResolvingName = false;
            });
        }

        // =================================================================
        //  Local networks
        // =================================================================

        /// <summary>
        /// Active IPv4 networks this PC is on, best first: adapters with a
        /// default gateway (the real LAN) before virtual ones (Hyper-V, VPN,
        /// VirtualBox), Ethernet/Wi-Fi before others. Link-local 169.254.x.x is skipped.
        /// </summary>
        public static List<LocalNetwork> GetLocalNetworks()
        {
            var list = new List<(LocalNetwork Net, int Score)>();

            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { return new List<LocalNetwork>(); }

            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }

                string? gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                    ?.ToString();

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string addressText = ua.Address.ToString();
                    // 169.254.x.x = no DHCP answered, typical of a USB Ethernet adapter cabled straight
                    // to a device. Still scannable (ARP works), just listed after the real LANs.
                    bool linkLocal = addressText.StartsWith("169.254.");

                    int prefix = ua.PrefixLength is > 0 and <= 32 ? ua.PrefixLength : 24;
                    uint address = ToUInt(ua.Address.GetAddressBytes());
                    var (range, narrowed) = RangeForInterface(address, prefix);

                    string kind = ClassifyKind(ni);

                    bool looksVirtual = Regex.IsMatch(ni.Description + " " + ni.Name,
                        "virtual|vEthernet|Hyper-V|VMware|VirtualBox|WSL|Docker|TAP|VPN|Loopback", RegexOptions.IgnoreCase);

                    int score = (gateway != null ? 100 : 0)
                              + (kind is "Wi-Fi" or "Ethernet" or "USB Ethernet" ? 20 : 0)
                              - (looksVirtual ? 50 : 0)
                              - (linkLocal ? 30 : 0);

                    string? mac = FormatMac(ni.GetPhysicalAddress().GetAddressBytes());

                    list.Add((new LocalNetwork
                    {
                        InterfaceName = ni.Name,
                        Kind = kind,
                        LocalAddress = addressText,
                        PrefixLength = prefix,
                        GatewayAddress = gateway,
                        MacAddress = mac,
                        Range = range,
                        WasNarrowed = narrowed,
                    }, score));
                }
            }

            return list.OrderByDescending(x => x.Score).Select(x => x.Net).ToList();
        }

        private static readonly Regex UsbWords = new("\\busb\\b|RTL8153|RTL8152|AX88179|AX88772", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WirelessWords =new("wi-?fi|wireless|wlan|802\\.11", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A connection type for the dropdown ("Wi-Fi", "Ethernet", ...) - never the adapter's own
        /// name or model. <see cref="NetworkInterfaceType"/> alone isn't enough: Windows' Native
        /// WiFi miniport reports plain Ethernet to the IP stack, so most Wi-Fi adapters would
        /// otherwise show up as "Ethernet" (or, for the handful that report something else
        /// entirely, as their own device/model name - the actual bug reported here).
        /// </summary>
        private static string ClassifyKind(NetworkInterface ni)
        {
            string hint = ni.Description + " " + ni.Name;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || WirelessWords.IsMatch(hint))
                return "Wi-Fi";

            if (UsbWords.IsMatch(hint)) return "USB Ethernet";

            return ni.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "VPN",
                _ => "Other",
            };
        }

        /// <summary>The adapter's subnet (minus network/broadcast); if larger than <see cref="MaxHosts"/>, the /24 around it.</summary>
        public static (ScanRange Range, bool Narrowed) RangeForInterface(uint address, int prefix)
        {
            bool narrowed = false;
            if (prefix < 32 - 12) // bigger than /20
            {
                prefix = 24;
                narrowed = true;
            }
            return (SubnetRange(address, prefix), narrowed);
        }

        private static ScanRange SubnetRange(uint address, int prefix)
        {
            uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            uint network = address & mask;
            uint broadcast = network | ~mask;

            // /31 and /32 have no network/broadcast addresses to skip.
            uint first = prefix >= 31 ? network : network + 1;
            uint last = prefix >= 31 ? broadcast : broadcast - 1;
            return new ScanRange(first, last, $"{ToText(network)}/{prefix}");
        }

        // =================================================================
        //  Custom ranges
        // =================================================================

        /// <summary>
        /// Accepts "192.168.1" (a /24 prefix, as before), "192.168.1.0/24",
        /// "10.0.0.1-10.0.0.80", "10.0.0.1-80", or a single address.
        /// </summary>
        public static bool TryParseRange(string? text, out ScanRange range, out string error)
        {
            range = default;
            error = "";
            string s = (text ?? "").Trim();

            if (s.Length == 0)
            {
                error = "Enter a range like 192.168.1.0/24 or 192.168.1.10-192.168.1.50.";
                return false;
            }

            // Legacy prefix: "192.168.1"
            if (Regex.IsMatch(s, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.?$"))
                s = s.TrimEnd('.') + ".0/24";

            ScanRange candidate;
            int slash = s.IndexOf('/');
            int dash = s.IndexOf('-');

            if (slash > 0)
            {
                if (!TryParseIPv4(s[..slash], out uint ip) || !int.TryParse(s[(slash + 1)..], out int prefix) || prefix < 0 || prefix > 32)
                {
                    error = "Use CIDR notation like 192.168.1.0/24.";
                    return false;
                }
                if (prefix < 20)
                {
                    error = $"/{prefix} is {Math.Pow(2, 32 - prefix):N0} addresses - the limit is /20 ({MaxHosts:N0}). Try a smaller range.";
                    return false;
                }
                candidate = SubnetRange(ip, prefix);
            }
            else if (dash > 0)
            {
                string left = s[..dash].Trim(), right = s[(dash + 1)..].Trim();
                if (!TryParseIPv4(left, out uint first))
                {
                    error = $"\"{left}\" isn't an IPv4 address.";
                    return false;
                }

                uint last;
                if (int.TryParse(right, out int lastOctet) && lastOctet is >= 0 and <= 255 && !right.Contains('.'))
                    last = (first & 0xFFFFFF00) | (uint)lastOctet; // "10.0.0.1-80"
                else if (!TryParseIPv4(right, out last))
                {
                    error = $"\"{right}\" isn't an IPv4 address or a last number (0-255).";
                    return false;
                }

                if (last < first) (first, last) = (last, first);
                candidate = new ScanRange(first, last, $"{ToText(first)} â€“ {ToText(last)}");
            }
            else if (TryParseIPv4(s, out uint single))
            {
                candidate = new ScanRange(single, single, ToText(single));
            }
            else
            {
                error = "Use 192.168.1.0/24, 192.168.1.10-192.168.1.50, or a single address.";
                return false;
            }

            if (candidate.Count > MaxHosts)
            {
                error = $"That's {candidate.Count:N0} addresses - the limit is {MaxHosts:N0}.";
                return false;
            }

            range = candidate;
            return true;
        }

        // =================================================================
        //  ARP
        // =================================================================

        /// <summary>IP -> MAC from the OS neighbour cache ("arp -a"). Empty if unavailable.</summary>
        public static Dictionary<string, string> ReadArpTable()
        {
            try
            {
                var psi = new ProcessStartInfo("arp", "-a")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return new Dictionary<string, string>();

                var read = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch (Exception) { }
                    return new Dictionary<string, string>();
                }
                return ParseArpOutput(read.GetAwaiter().GetResult());
            }
            catch (Exception)
            {
                return new Dictionary<string, string>(); // arp missing or blocked: MACs stay unknown
            }
        }

        private static readonly Regex ArpLine = new(
            @"(?<ip>\d{1,3}(?:\.\d{1,3}){3})\)?\s+(?:at\s+)?(?<mac>[0-9a-fA-F]{1,2}(?:[-:][0-9a-fA-F]{1,2}){5})",
            RegexOptions.Compiled);

        /// <summary>
        /// Parses Windows ("192.168.1.10   a4-83-e7-3c-9b-21   dynamic") and
        /// Unix ("? (192.168.1.10) at a4:83:e7:3c:9b:21 [ether]") output.
        /// Broadcast, multicast and incomplete entries are skipped.
        /// </summary>
        public static Dictionary<string, string> ParseArpOutput(string output)
        {
            var result = new Dictionary<string, string>();
            foreach (Match m in ArpLine.Matches(output))
            {
                string? mac = NormalizeMac(m.Groups["mac"].Value);
                if (mac == null) continue;
                result[m.Groups["ip"].Value] = mac;
            }
            return result;
        }

        /// <summary>"a4:83:e7:3c:9b:1" -> "A4-83-E7-3C-9B-01"; null for broadcast/multicast/zero MACs.</summary>
        public static string? NormalizeMac(string raw)
        {
            string[] parts = raw.Split('-', ':');
            if (parts.Length != 6) return null;

            var bytes = new byte[6];
            for (int i = 0; i < 6; i++)
                if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                    return null;

            if (bytes.All(b => b == 0) || bytes.All(b => b == 0xFF)) return null;
            if ((bytes[0] & 0x01) != 0) return null; // multicast (01-00-5E-..., 33-33-...)

            return FormatMac(bytes);
        }

        private static string? FormatMac(byte[] bytes) =>
            bytes.Length == 6 && bytes.Any(b => b != 0) ? string.Join("-", bytes.Select(b => b.ToString("X2"))) : null;

        // =================================================================
        //  Export
        // =================================================================

        public static void ExportToCsv(IEnumerable<DeviceScanResult> rows, string filePath) =>
            AtomicFile.WriteAllText(filePath, ToCsv(rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        public static string ToCsv(IEnumerable<DeviceScanResult> rows)
        {
            var sb = new StringBuilder("IP Address,Hostname,MAC Address,Manufacturer,Response Time (ms),Status,Notes\r\n");
            foreach (var r in rows)
            {
                var notes = new List<string>();
                if (r.IsThisDevice) notes.Add("This PC");
                if (r.IsGateway) notes.Add("Router / gateway");
                if (r.IsRandomizedMac) notes.Add("Private (randomized) MAC");

                sb.Append(Csv(r.IPAddress)).Append(',')
                  .Append(Csv(r.HostName ?? "")).Append(',')
                  .Append(Csv(r.MacAddress ?? "")).Append(',')
                  .Append(Csv(r.Vendor ?? "")).Append(',')
                  .Append(r.ResponseTimeMs?.ToString() ?? "").Append(',')
                  .Append(r.Status == DeviceStatus.Online ? "Online" : "No ping reply").Append(',')
                  .Append(Csv(string.Join("; ", notes)))
                  .Append("\r\n");
            }
            return sb.ToString();
        }

        private static string Csv(string value)
        {
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value; // no formula injection in Excel
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        // =================================================================
        //  IPv4 helpers
        // =================================================================

        public static bool TryParseIPv4(string? text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Trim().Split('.');
            if (parts.Length != 4) return false;

            foreach (string p in parts)
            {
                if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || !int.TryParse(p, out int octet) || octet > 255)
                    return false;
                value = (value << 8) | (uint)octet;
            }
            return true;
        }

        public static string ToText(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";

        private static byte[] ToBytes(uint ip) => new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip };

        private static uint ToUInt(byte[] b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
