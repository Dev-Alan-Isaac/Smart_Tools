using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Network adapters: every one Windows has (connected, unplugged or
    /// disabled - USB Ethernet included), their IP settings, and changing
    /// them: automatic/manual address and DNS, enable and disable. Changes go
    /// through netsh (part of Windows) and need administrator rights, so
    /// Windows asks for permission once per change. The first half of this
    /// file is pure (unit-tested); reading and changing Windows is below.
    /// </summary>
    public static partial class NetworkToolsManager
    {
        // =================================================================
        //  Pure: state, kind, virtual
        // =================================================================

        /// <summary>
        /// From Win32_NetworkAdapter: NetEnabled false = disabled; otherwise
        /// NetConnectionStatus (2 connected, 7 media disconnected, 4/5 hardware
        /// missing or disabled...). Without WMI, the live status decides.
        /// </summary>
        public static AdapterState StateFrom(bool? netEnabled, int? connectionStatus, OperationalStatus? live)
        {
            if (netEnabled == false) return AdapterState.Disabled;
            return connectionStatus switch
            {
                2 or 9 or 11 => AdapterState.Connected,          // connected, authenticated, connected without a valid address
                1 or 8 or 12 => AdapterState.Connecting,         // connecting, authenticating, needs credentials
                0 or 3 or 7 or 10 => AdapterState.Disconnected,  // disconnected, disconnecting, cable unplugged, authentication failed
                4 or 5 => AdapterState.Disabled,                 // hardware not present / disabled
                null => live switch
                {
                    OperationalStatus.Up => AdapterState.Connected,
                    OperationalStatus.Down or OperationalStatus.NotPresent or OperationalStatus.LowerLayerDown => AdapterState.Disconnected,
                    OperationalStatus.Dormant => AdapterState.Connecting,
                    _ => AdapterState.Unknown,
                },
                _ => AdapterState.Unknown,
            };
        }

        private static readonly string[] VirtualWords =
        {
            "Virtual", "Hyper-V", "VPN", "TAP-", "TAP ", "WireGuard", "Tunnel", "WAN Miniport", "Loopback", "Kernel Debug",
            "Wi-Fi Direct", "VMware", "VirtualBox", "Npcap", "ZeroTier", "Tailscale", "Teredo", "isatap", "Pseudo",
        };

        /// <summary>Hyper-V, VPN, Wi-Fi Direct and similar - hidden unless "Show virtual adapters" is ticked.</summary>
        public static bool IsVirtualAdapter(bool? physicalAdapter, string? pnpDeviceId, string description)
        {
            if (physicalAdapter == false) return true;
            string pnp = pnpDeviceId ?? "";
            if (pnp.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)) return true;
            return VirtualWords.Any(w => description.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>What people call it, with USB adapters told apart ("USB Ethernet" for a dongle or dock).</summary>
        public static string KindFrom(string description, string? pnpDeviceId, string? interfaceType, bool isVirtual)
        {
            bool Has(string w) => description.Contains(w, StringComparison.OrdinalIgnoreCase);
            bool usb = (pnpDeviceId ?? "").StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
            if (Has("Bluetooth")) return "Bluetooth";
            if (Has("Mobile Broadband") || Has("WWAN") || Has("Cellular") || interfaceType is "Wwanpp" or "Wwanpp2") return "Cellular";
            bool wifi = interfaceType == "Wireless80211" || Has("Wi-Fi") || Has("Wireless") || Has("WLAN") || Has("802.11");
            if (isVirtual)
            {
                if (Has("VPN") || Has("TAP") || Has("WireGuard") || Has("Tunnel") || Has("Tailscale") || Has("ZeroTier") || interfaceType is "Tunnel" or "Ppp") return "VPN";
                return "Virtual";
            }
            if (wifi) return usb ? "USB Wi-Fi" : "Wi-Fi";
            return usb ? "USB Ethernet" : "Ethernet";
        }

        // =================================================================
        //  Pure: addresses and masks
        // =================================================================

        /// <summary>
        /// A dotted IPv4 address with exactly four decimal parts. IPAddress.TryParse
        /// alone takes "1" as 0.0.0.1, "10.1" as 10.0.0.1 and reads a leading zero
        /// as octal ("010.0.0.1" = 8.0.0.1) - fine for a URL, wrong for a form
        /// where a half-typed or zero-padded address must mean what it shows.
        /// </summary>
        public static bool TryParseIPv4(string? text, out IPAddress address)
        {
            address = IPAddress.None;
            var parts = (text ?? "").Trim().Split('.');
            if (parts.Length != 4) return false;
            var bytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                string p = parts[i];
                if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit)) return false;
                int v = int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
                if (v > 255) return false;
                bytes[i] = (byte)v;
            }
            address = new IPAddress(bytes);
            return true;
        }

        /// <summary>"255.255.255.0", "/24" or "24" → 24. Masks must be contiguous ones (255.0.255.0 isn't a mask).</summary>
        public static bool TryParseMask(string? text, out int prefix)
        {
            prefix = -1;
            string s = (text ?? "").Trim().TrimStart('/');
            if (s.Length is > 0 and <= 2 && s.All(char.IsAsciiDigit))
            {
                int p = int.Parse(s, CultureInfo.InvariantCulture);
                if (p is < 1 or > 32) return false;
                prefix = p;
                return true;
            }
            if (!TryParseIPv4(s, out var mask)) return false;
            uint bits = ToUInt(mask);
            int ones = BitOperations(bits);
            uint expected = ones == 0 ? 0 : uint.MaxValue << (32 - ones);
            if (bits != expected || ones == 0) return false;
            prefix = ones;
            return true;
        }

        private static int BitOperations(uint v) => System.Numerics.BitOperations.PopCount(v);

        public static string MaskFromPrefix(int prefix)
        {
            uint bits = prefix <= 0 ? 0 : uint.MaxValue << (32 - Math.Min(32, prefix));
            return FromUInt(bits).ToString();
        }

        private static uint ToUInt(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

        public static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
        {
            uint mask = prefix <= 0 ? 0 : uint.MaxValue << (32 - Math.Min(32, prefix));
            return (ToUInt(a) & mask) == (ToUInt(b) & mask);
        }

        /// <summary>"8.8.8.8,1.1.1.1" or "8.8.8.8 1.1.1.1" (registry NameServer) → the addresses.</summary>
        public static List<string> ParseNameServers(string? value) =>
            (value ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(s => TryParseIPv4(s, out _)).ToList();

        // =================================================================
        //  Pure: checking an edit, the netsh commands
        // =================================================================

        /// <summary>The first problem with the form, in words, or null when it can be applied.</summary>
        public static string? ValidateEdit(IpConfigEdit e)
        {
            if (!e.Dhcp)
            {
                if (!TryParseIPv4(e.Ip, out var ip)) return "Enter the IP address as four numbers, like 192.168.1.50.";
                byte first = ip.GetAddressBytes()[0];
                if (first == 0 || first == 127 || first >= 224) return $"{ip} can't be used for an adapter - pick an address on your network, like 192.168.1.50.";
                if (!TryParseMask(e.Mask, out int prefix)) return "Enter the subnet mask, like 255.255.255.0 (or 24).";
                if (prefix <= 30)
                {
                    uint host = ToUInt(ip) & ~(uint.MaxValue << (32 - prefix));
                    if (host == 0) return $"{ip} is the network's own address with that mask - the last number can't be 0.";
                    if (host == ~(uint.MaxValue << (32 - prefix))) return $"{ip} is the network's broadcast address with that mask - pick another.";
                }
                if (e.Gateway.Trim().Length > 0)
                {
                    if (!TryParseIPv4(e.Gateway, out var gw)) return "The gateway must be an address like 192.168.1.1 (or leave it empty).";
                    if (gw.Equals(ip)) return "The gateway can't be this computer's own address.";
                    if (!SameSubnet(ip, gw, prefix)) return $"The gateway {gw} isn't on the same network as {ip}/{prefix} - check the mask or the gateway.";
                }
                if (e.DnsAutomatic) return "With a manual address there's no DHCP to hand out DNS - enter DNS servers too.";
            }
            if (!e.DnsAutomatic)
            {
                if (!TryParseIPv4(e.Dns1, out _)) return "Enter the preferred DNS server, like 1.1.1.1 or your router's address.";
                if (e.Dns2.Trim().Length > 0 && !TryParseIPv4(e.Dns2, out _)) return "The alternate DNS server must be an address like 8.8.8.8 (or leave it empty).";
            }
            return null;
        }

        /// <summary>A name netsh can take inside quotes (Windows allows almost anything in a connection name, but not a quote).</summary>
        public static bool IsUsableAdapterName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && !name.Any(c => c == '"' || char.IsControl(c));

        private static string Q(string name) => "\"" + name + "\"";

        /// <summary>"010.000.000.001" → "10.0.0.1": netsh gets the address the form showed, with no zero padding to misread.</summary>
        private static string Norm(string text) => TryParseIPv4(text, out var ip) ? ip.ToString() : text.Trim();

        /// <summary>The netsh commands (script form, without "netsh") that make Windows match the form. Call <see cref="ValidateEdit"/> first.</summary>
        public static List<string> NetshCommands(string adapterName, IpConfigEdit e)
        {
            if (!IsUsableAdapterName(adapterName)) throw new ArgumentException("That adapter's name contains a quote - rename it in Windows first.", nameof(adapterName));
            string n = Q(adapterName);
            var list = new List<string>();
            if (e.Dhcp)
            {
                list.Add($"interface ipv4 set address name={n} source=dhcp");
            }
            else
            {
                TryParseMask(e.Mask, out int prefix);
                string gateway = e.Gateway.Trim().Length > 0 ? Norm(e.Gateway) : "none";
                list.Add($"interface ipv4 set address name={n} source=static address={Norm(e.Ip)} mask={MaskFromPrefix(prefix)} gateway={gateway}");
            }
            if (e.DnsAutomatic)
            {
                list.Add($"interface ipv4 set dnsservers name={n} source=dhcp");
            }
            else
            {
                list.Add($"interface ipv4 set dnsservers name={n} source=static address={Norm(e.Dns1)} register=primary validate=no");
                if (e.Dns2.Trim().Length > 0) list.Add($"interface ipv4 add dnsservers name={n} address={Norm(e.Dns2)} index=2 validate=no");
            }
            return list;
        }

        public static List<string> NetshEnableCommands(string adapterName, bool enable)
        {
            if (!IsUsableAdapterName(adapterName)) throw new ArgumentException("That adapter's name contains a quote - rename it in Windows first.", nameof(adapterName));
            return new List<string> { $"interface set interface name={Q(adapterName)} admin={(enable ? "enabled" : "disabled")}" };
        }

        /// <summary>The edit form, filled from what Windows has now (stored settings first, so it works for unplugged adapters).</summary>
        public static IpConfigEdit EditFromAdapter(AdapterInfo a)
        {
            var first = a.IPv4.FirstOrDefault();
            string ip = a.ConfiguredIp.Length > 0 ? a.ConfiguredIp : first.Address ?? "";
            string mask = a.ConfiguredMask.Length > 0 ? a.ConfiguredMask : first.Address != null ? MaskFromPrefix(first.Prefix) : "255.255.255.0";
            string gateway = a.ConfiguredGateway.Length > 0 ? a.ConfiguredGateway : a.Gateways.FirstOrDefault() ?? "";
            return new IpConfigEdit(a.DhcpEnabled, ip, mask, gateway, a.DnsAutomatic, a.Dns.ElementAtOrDefault(0) ?? "", a.Dns.ElementAtOrDefault(1) ?? "");
        }

        /// <summary>One line for the confirmation and the console: "Ethernet 2 → manual 192.168.1.50/24, gateway 192.168.1.1, DNS 1.1.1.1".</summary>
        public static string DescribeEdit(string adapterName, IpConfigEdit e)
        {
            string address = e.Dhcp ? "automatic address (DHCP)"
                : $"manual {Norm(e.Ip)}/{(TryParseMask(e.Mask, out int p) ? p : 0)}" + (e.Gateway.Trim().Length > 0 ? $", gateway {Norm(e.Gateway)}" : ", no gateway");
            string dns = e.DnsAutomatic ? "automatic DNS"
                : "DNS " + string.Join(", ", new[] { e.Dns1, e.Dns2 }.Where(x => x.Trim().Length > 0).Select(Norm));
            return $"{adapterName} → {address}, {dns}";
        }

        /// <summary>"3 h 12 min left", "expired".</summary>
        public static string FormatLease(TimeSpan? left)
        {
            if (left is not TimeSpan t) return "";
            if (t <= TimeSpan.Zero) return "expired";
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h left";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min left";
            return $"{Math.Max(1, t.Minutes)} min left";
        }

        /// <summary>The details panel for one adapter. Empty facts are left out.</summary>
        public static List<NetworkInfoRow> DescribeAdapter(AdapterInfo a)
        {
            var rows = new List<NetworkInfoRow>();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) rows.Add(new NetworkInfoRow(label, value));
            }
            Add("Status", a.StatusText);
            Add("Type", a.Kind);
            Add("Adapter", a.Description);
            Add("MAC address", a.Mac);
            Add("Speed", a.State == AdapterState.Connected && a.SpeedBitsPerSecond > 0 ? NetworkText.FormatSpeed(a.SpeedBitsPerSecond) : null);
            if (a.IsEnabled)
            {
                Add("IP address", a.IPv4.Count > 0 ? string.Join(Environment.NewLine, a.IPv4.Select(x => $"{x.Address}/{x.Prefix}")) : a.State == AdapterState.Connected ? "None yet" : null);
                Add("Subnet mask", a.IPv4.Count > 0 ? MaskFromPrefix(a.IPv4[0].Prefix) : null);
                Add("Default gateway", string.Join(", ", a.Gateways));
                Add("DNS servers", a.Dns.Count > 0 ? string.Join(", ", a.Dns) + (a.DnsAutomatic ? "  (automatic)" : "  (manual)") : null);
                string dhcp = a.DhcpEnabled
                    ? "On" + (a.DhcpServer != null ? $" · server {a.DhcpServer}" : "") + (a.LeaseRemaining != null ? $" · lease {FormatLease(a.LeaseRemaining)}" : "")
                    : "Off (manual address)";
                Add("DHCP", dhcp);
                Add("IPv6", string.Join(Environment.NewLine, a.IPv6));
            }
            if (!a.IsEnabled || a.State == AdapterState.Disconnected)
            {
                // What it will use once it's back: Windows keeps the settings.
                Add("Will use", a.DhcpEnabled ? "Automatic address (DHCP)" : $"{a.ConfiguredIp} / {a.ConfiguredMask}" + (a.ConfiguredGateway.Length > 0 ? $", gateway {a.ConfiguredGateway}" : ""));
            }
            return rows;
        }

        /// <summary>
        /// netsh says nothing (or "Ok.") when a change worked, and prints the
        /// reason when it didn't. Some failures still exit with 0, so any other
        /// text counts as a problem worth showing.
        /// </summary>
        public static NetshResult InterpretNetsh(int exitCode, string output)
        {
            var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim())
                              .Where(l => l.Length > 0 && !l.Equals("Ok.", StringComparison.OrdinalIgnoreCase)).ToList();
            string text = string.Join(" ", lines);
            if (exitCode == 0 && lines.Count == 0) return new NetshResult(true, false, "Done.");
            if (exitCode == 0) return new NetshResult(false, false, text);
            return new NetshResult(false, false, text.Length > 0 ? text : $"Windows couldn't make the change (netsh exit code {exitCode}).");
        }

        // =================================================================
        //  Reading Windows
        // =================================================================

        /// <summary>
        /// Every adapter Windows knows (Win32_NetworkAdapter lists disabled and
        /// unplugged ones too; the live network stack only lists enabled ones),
        /// with live addresses and the settings Windows has stored. Connected
        /// first, then unplugged, then disabled.
        /// </summary>
        public static List<AdapterInfo> GetAdapters(bool includeVirtual)
        {
            var live = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    if (nic.NetworkInterfaceType != NetworkInterfaceType.Loopback) live[nic.Id] = nic;
            }
            catch (NetworkInformationException)
            {
            }

            var list = new List<AdapterInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in WmiQuery("SELECT NetConnectionID, Description, GUID, NetEnabled, NetConnectionStatus, PhysicalAdapter, PNPDeviceID, MACAddress, Speed FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL"))
            {
                string name = w["NetConnectionID"]?.ToString() ?? "";
                string id = w["GUID"]?.ToString() ?? "";
                if (name.Length == 0 || id.Length == 0 || !seen.Add(id)) continue;
                string description = w["Description"]?.ToString() ?? "";
                string? pnp = w["PNPDeviceID"]?.ToString();
                bool virt = IsVirtualAdapter(w["PhysicalAdapter"] as bool?, pnp, description);
                if (virt && !includeVirtual) continue;
                live.TryGetValue(id, out var nic);
                int? status = w["NetConnectionStatus"] is ushort st ? st : null;   // boxed UInt16: unbox first
                var state = StateFrom(w["NetEnabled"] as bool?, status, nic?.OperationalStatus);
                long speed = w["Speed"] is ulong sp && sp < 800_000_000_000 ? (long)sp : 0;
                list.Add(Build(name, id, description, KindFrom(description, pnp, nic?.NetworkInterfaceType.ToString(), virt), state, virt,
                               nic, w["MACAddress"]?.ToString(), speed));
            }

            // No WMI (broken repository, locked-down PC): the live list, which at least has the enabled adapters.
            if (list.Count == 0 && seen.Count == 0)
            {
                foreach (var nic in live.Values)
                {
                    bool virt = IsVirtualAdapter(null, null, nic.Description) || nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp;
                    if (virt && !includeVirtual) continue;
                    list.Add(Build(nic.Name, nic.Id, nic.Description, KindFrom(nic.Description, null, nic.NetworkInterfaceType.ToString(), virt),
                                   StateFrom(null, null, nic.OperationalStatus), virt, nic, null, SafeSpeed(nic)));
                }
            }
            return SortAdapters(list);
        }

        /// <summary>Connected first (the one with a gateway before others), then connecting, unplugged, disabled; by name within.</summary>
        public static List<AdapterInfo> SortAdapters(IEnumerable<AdapterInfo> adapters) =>
            adapters.OrderBy(a => a.State switch
                    {
                        AdapterState.Connected => 0,
                        AdapterState.Connecting => 1,
                        AdapterState.Disconnected => 2,
                        AdapterState.Unknown => 3,
                        _ => 4,
                    })
                    .ThenByDescending(a => a.Gateways.Count > 0)
                    .ThenBy(a => a.IsVirtual)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

        private static AdapterInfo Build(string name, string id, string description, string kind, AdapterState state, bool isVirtual,
                                         NetworkInterface? nic, string? wmiMac, long speed)
        {
            var cfg = ReadTcpipConfig(id);
            var ipv4 = new List<(string, int)>();
            var ipv6 = new List<string>();
            var gateways = new List<string>();
            var dns = new List<string>();
            string? dhcpServer = null;
            TimeSpan? lease = null;
            bool dhcp = cfg?.Dhcp ?? true;
            string mac = wmiMac?.Replace('-', ':').ToUpperInvariant() ?? "";

            if (nic != null && state != AdapterState.Disabled)
            {
                try
                {
                    var props = nic.GetIPProperties();
                    foreach (var u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            // 169.254.x.x = Windows gave up waiting for DHCP; shown, since it explains "no internet".
                            ipv4.Add((u.Address.ToString(), u.PrefixLength));
                            if (lease == null && dhcp && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                            {
                                try
                                {
                                    lease = u.DhcpLeaseLifetime > 0 ? TimeSpan.FromSeconds(u.DhcpLeaseLifetime) : null;
                                }
                                catch (PlatformNotSupportedException)
                                {
                                }
                            }
                        }
                        else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal)
                        {
                            ipv6.Add(u.Address.ToString());
                        }
                    }
                    gateways = SafeList(() => props.GatewayAddresses.Select(g => g.Address)
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).Select(a => a.ToString()).ToList());
                    dns = SafeList(() => props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList());
                    dhcpServer = SafeList(() => props.DhcpServerAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList()).FirstOrDefault();
                    try
                    {
                        dhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? dhcp;
                    }
                    catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
                    {
                    }
                    if (mac.Length == 0)
                        mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                    if (speed == 0) speed = SafeSpeed(nic);
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
                {
                    // the adapter went away mid-read
                }
            }
            if (dns.Count == 0 && cfg != null && cfg.NameServers.Count > 0) dns = cfg.NameServers;

            return new AdapterInfo
            {
                Name = name,
                Id = id,
                Description = description,
                Kind = kind,
                State = state,
                IsVirtual = isVirtual,
                IPv4 = ipv4,
                IPv6 = ipv6,
                Gateways = gateways,
                Dns = dns,
                Mac = mac,
                SpeedBitsPerSecond = speed,
                DhcpEnabled = dhcp,
                DhcpServer = dhcp ? dhcpServer : null,
                LeaseRemaining = dhcp ? lease : null,
                DnsAutomatic = cfg == null || cfg.NameServers.Count == 0,
                ConfiguredIp = cfg?.Ip ?? "",
                ConfiguredMask = cfg?.Mask ?? "",
                ConfiguredGateway = cfg?.Gateway ?? "",
            };
        }

        private sealed record TcpipConfig(bool Dhcp, string Ip, string Mask, string Gateway, List<string> NameServers);

        /// <summary>
        /// What Windows has stored for the adapter (HKLM\...\Tcpip\Parameters\Interfaces\{GUID}):
        /// DHCP on/off, the manual address, and hand-typed DNS (NameServer is empty when DNS is automatic).
        /// Readable without admin rights, and there for unplugged and disabled adapters too.
        /// </summary>
        private static TcpipConfig? ReadTcpipConfig(string id)
        {
            if (!OperatingSystem.IsWindows() || id.Length == 0 || id.IndexOfAny(new[] { '\\', '/' }) >= 0) return null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{id}");
                if (key == null) return null;
                bool dhcp = key.GetValue("EnableDHCP") is int e ? e != 0 : true;
                string First(string value) => key.GetValue(value) switch
                {
                    string[] arr => arr.FirstOrDefault(x => TryParseIPv4(x, out var ip) && !ip.Equals(IPAddress.Any)) ?? "",
                    string one => TryParseIPv4(one, out var ip2) && !ip2.Equals(IPAddress.Any) ? one : "",
                    _ => "",
                };
                return new TcpipConfig(dhcp, First("IPAddress"), First("SubnetMask"), First("DefaultGateway"), ParseNameServers(key.GetValue("NameServer") as string));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        /// <summary>A WMI query that never throws (a broken WMI just gives nothing).</summary>
        private static List<ManagementBaseObject> WmiQuery(string wql)
        {
            var list = new List<ManagementBaseObject>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\cimv2", wql);
                using var results = searcher.Get();
                foreach (var item in results) list.Add(item);
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Info($"Network Tools: WMI failed: {ex.Message}");
            }
            return list;
        }

        // =================================================================
        //  Changing Windows (netsh, with administrator rights)
        // =================================================================

        public static bool IsAdministrator()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the commands as one netsh script. Already administrator: runs
        /// directly and reads the output. Otherwise starts it elevated (Windows
        /// asks once) and reads the output from a temporary file. Saying No at
        /// that prompt gives Cancelled, not an error. Temporary files are removed.
        /// </summary>
        public static async Task<NetshResult> RunNetshAsync(IReadOnlyList<string> commands, CancellationToken token = default)
        {
            string folder = Path.Combine(Path.GetTempPath(), "Smart Tools");
            Directory.CreateDirectory(folder);
            string stamp = Guid.NewGuid().ToString("N")[..8];
            string script = Path.Combine(folder, $"netsh-{stamp}.txt");
            string output = Path.Combine(folder, $"netsh-{stamp}.out");
            string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            try
            {
                await File.WriteAllLinesAsync(script, commands, new UTF8Encoding(false), token).ConfigureAwait(false);
                if (IsAdministrator())
                {
                    var psi = new ProcessStartInfo(netsh, $"-f \"{script}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi) ?? throw new InvalidOperationException("netsh didn't start.");
                    var stdout = p.StandardOutput.ReadToEndAsync(token);
                    var stderr = p.StandardError.ReadToEndAsync(token);
                    await p.WaitForExitAsync(token).ConfigureAwait(false);
                    return InterpretNetsh(p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
                }

                // Elevated: no output pipe, so cmd writes netsh's text to a file this process can read.
                var elevated = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    $"/s /c \"\"{netsh}\" -f \"{script}\" > \"{output}\" 2>&1\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process? proc;
                try
                {
                    proc = Process.Start(elevated);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return new NetshResult(false, true, "Nothing changed - Windows' permission prompt was answered No.");
                }
                if (proc == null) return new NetshResult(false, false, "netsh didn't start.");
                using (proc)
                {
                    await proc.WaitForExitAsync(token).ConfigureAwait(false);
                    string text = File.Exists(output) ? await File.ReadAllTextAsync(output, token).ConfigureAwait(false) : "";
                    return InterpretNetsh(proc.ExitCode, text);
                }
            }
            catch (Win32Exception ex)
            {
                return new NetshResult(false, false, "Couldn't run netsh: " + ex.Message);
            }
            finally
            {
                TryDelete(script);
                TryDelete(output);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a temp file left behind is harmless
            }
        }
    }
}
