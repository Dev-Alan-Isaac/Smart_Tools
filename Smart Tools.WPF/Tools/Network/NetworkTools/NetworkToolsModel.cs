using System.Globalization;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Network tools (Ping, Port Scanner, TCP/UDP Client, My IP & Adapters) -
    //  plain data only. Logic is in Controllers/NetworkToolsManager*.cs,
    //  shared by the four views.
    // =====================================================================

    /// <summary>The four tool tiles (read by Tag = enum name).</summary>
    public enum NetworkTool
    {
        Ping,
        Ports,
        Socket,
        IpInfo
    }

    /// <summary>Console line colour (themed chips, no brushes in code).</summary>
    public enum ConsoleKind
    {
        Info,
        Success,
        Warning,
        Error,
        Sent,
        Received
    }

    /// <summary>One console line. Source is a short tag: PING, SCAN, TCP, UDP, IP.</summary>
    public sealed record ConsoleLine(DateTime Time, ConsoleKind Kind, string Source, string Text)
    {
        public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public override string ToString() => $"[{TimeText}] {Source,-4} {Text}";
    }

    // ---------------- Ping ----------------

    /// <summary>One reply (or timeout). RoundtripMs is null when there was no reply.</summary>
    public sealed record PingSample(int Sequence, string Address, string Status, long? RoundtripMs, int? Ttl)
    {
        public bool Success => RoundtripMs.HasValue;
    }

    /// <summary>Running totals for the ping card: sent, received, loss, min/avg/max, jitter.</summary>
    public sealed class PingStats
    {
        private readonly List<long> _times = new();

        public int Sent { get; private set; }
        public int Received { get; private set; }
        public int Lost => Sent - Received;
        public double LossPercent => Sent == 0 ? 0 : Lost * 100.0 / Sent;
        public long? Min => _times.Count > 0 ? _times.Min() : null;
        public long? Max => _times.Count > 0 ? _times.Max() : null;
        public double? Average => _times.Count > 0 ? _times.Average() : null;

        /// <summary>Mean difference between consecutive replies (what makes calls and games stutter).</summary>
        public double? Jitter => _times.Count > 1 ? _times.Zip(_times.Skip(1), (a, b) => (double)Math.Abs(b - a)).Average() : null;

        public void Add(PingSample s)
        {
            Sent++;
            if (s.RoundtripMs is long ms)
            {
                Received++;
                _times.Add(ms);
            }
        }
    }

    /// <summary>One bar of the latency strip. Height is 0-1 of the strip; lost pings are full-height red.</summary>
    public sealed record LatencyBar(double Height, bool Lost, string ToolTip);

    // ---------------- Ports ----------------

    public sealed record PortResult(int Port, string Service, long LatencyMs)
    {
        public string Label => string.IsNullOrEmpty(Service) ? Port.ToString(CultureInfo.InvariantCulture) : $"{Port} · {Service}";
    }

    public readonly record struct PortScanProgress(int Checked, int Total, int Open);

    // ---------------- Socket ----------------

    public enum SocketProtocol
    {
        Tcp,
        Udp
    }

    public enum SocketState
    {
        Disconnected,
        Connecting,
        Connected
    }

    // ---------------- IP info / adapters ----------------

    /// <summary>Where an adapter is at: what decides its dot colour and which buttons make sense.</summary>
    public enum AdapterState
    {
        Connected,
        Connecting,
        Disconnected,
        Disabled,
        Unknown
    }

    /// <summary>A label/value line in the adapter's details ("Default gateway" / "192.168.1.1").</summary>
    public sealed record NetworkInfoRow(string Label, string Value);

    /// <summary>
    /// One network adapter - connected, unplugged or disabled (USB Ethernet,
    /// LAN, Wi-Fi...). Live addresses come from Windows' network stack;
    /// the Configured* values are what Windows has stored for it, so the edit
    /// form fills in even for an adapter that's unplugged or disabled.
    /// </summary>
    public sealed class AdapterInfo
    {
        /// <summary>The connection name Windows shows and netsh uses ("Ethernet 2").</summary>
        public required string Name { get; init; }

        /// <summary>"{GUID}" - stable across refreshes (the name can be renamed).</summary>
        public string Id { get; init; } = "";

        public string Description { get; init; } = "";

        /// <summary>"Ethernet", "USB Ethernet", "Wi-Fi", "USB Wi-Fi", "Bluetooth", "Cellular", "VPN", "Virtual".</summary>
        public string Kind { get; init; } = "";

        public AdapterState State { get; init; } = AdapterState.Unknown;
        public bool IsVirtual { get; init; }

        /// <summary>IPv4 addresses with their prefix length ("192.168.1.20", 24).</summary>
        public List<(string Address, int Prefix)> IPv4 { get; init; } = new();
        public List<string> IPv6 { get; init; } = new();
        public List<string> Gateways { get; init; } = new();
        public List<string> Dns { get; init; } = new();
        public string Mac { get; init; } = "";
        public long SpeedBitsPerSecond { get; init; }

        public bool DhcpEnabled { get; init; } = true;
        public string? DhcpServer { get; init; }
        public TimeSpan? LeaseRemaining { get; init; }

        /// <summary>False when DNS servers were typed in by hand.</summary>
        public bool DnsAutomatic { get; init; } = true;

        public string ConfiguredIp { get; init; } = "";
        public string ConfiguredMask { get; init; } = "";
        public string ConfiguredGateway { get; init; } = "";

        public bool IsEnabled => State != AdapterState.Disabled;

        public string AddressText => IPv4.Count > 0 ? string.Join(", ", IPv4.Select(a => a.Address)) : IPv6.FirstOrDefault() ?? "No address";

        public string StatusText => State switch
        {
            AdapterState.Connected => "Connected",
            AdapterState.Connecting => "Connecting…",
            AdapterState.Disconnected => Kind.Contains("Wi-Fi", StringComparison.Ordinal) ? "Not connected" : "Cable unplugged",
            AdapterState.Disabled => "Disabled",
            _ => "Unknown",
        };

        /// <summary>The second line in the list: the address when connected, else the state.</summary>
        public string ListLine => State == AdapterState.Connected ? $"{Kind} · {AddressText}" : $"{Kind} · {StatusText}";
    }

    /// <summary>What the edit form asks Windows to set. Empty strings = not given.</summary>
    public sealed record IpConfigEdit(bool Dhcp, string Ip, string Mask, string Gateway, bool DnsAutomatic, string Dns1, string Dns2);

    /// <summary>
    /// Where Ping, the port scanner and the TCP/UDP client send from: "Automatic"
    /// (Windows picks, Address null) or one adapter's IPv4 address.
    /// Id is the adapter's GUID ("" for Automatic) - what's remembered.
    /// </summary>
    public sealed record SourceChoice(string Id, string Label, string Detail, System.Net.IPAddress? Address, string Kind)
    {
        public bool IsAutomatic => Address == null;

        /// <summary>"from Ethernet 2 (192.168.1.20)", or "" for Automatic - added to log lines.</summary>
        public string FromText => Address == null ? "" : $"from {Label} ({Address})";
    }

    /// <summary>How a change went: Cancelled = the user said No at Windows' permission prompt.</summary>
    public sealed record NetshResult(bool Ok, bool Cancelled, string Message);

    // ---------------- Settings ----------------

    /// <summary>Remembered in %AppData%\Smart Tools\network_tools.json.</summary>
    public sealed class NetworkSettings
    {
        public NetworkTool Tool { get; set; } = NetworkTool.Ping;
        public string PingHost { get; set; } = "google.com";

        /// <summary>0 = until stopped.</summary>
        public int PingCount { get; set; } = 4;

        public string ScanHost { get; set; } = "127.0.0.1";
        public string ScanPorts { get; set; } = "1-1024";

        /// <summary>How long to wait for each port: 300 fast (local network), 800 normal, 2000 thorough.</summary>
        public int ScanTimeoutMs { get; set; } = 800;
        public SocketProtocol Protocol { get; set; } = SocketProtocol.Tcp;
        public string SocketHost { get; set; } = "127.0.0.1";
        public int SocketPort { get; set; } = 9000;
        public int LocalPort { get; set; }
        public bool AppendNewline { get; set; } = true;
        public bool SendHex { get; set; }
        public bool ShowReceivedHex { get; set; }
        public bool LookUpPublicIp { get; set; } = true;

        /// <summary>Also list Hyper-V, VPN and other virtual adapters.</summary>
        public bool ShowVirtualAdapters { get; set; }

        /// <summary>The adapter each tool sends from (its GUID; "" = automatic). Not a path.</summary>
        public string PingSourceId { get; set; } = "";
        public string ScanSourceId { get; set; } = "";
        public string SocketSourceId { get; set; } = "";
    }

    public static class NetworkText
    {
        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }

        public static string FormatSpeed(long bitsPerSecond) =>
            bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1e9:0.#} Gbps" :
            bitsPerSecond >= 1_000_000 ? $"{bitsPerSecond / 1e6:0} Mbps" :
            $"{bitsPerSecond / 1e3:0} Kbps";

        public static string Ms(double? ms) => ms is double v ? $"{v:0} ms" : "–";
    }
}
