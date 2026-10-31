using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Network Device Scanner data model. Plain data (DeviceScanResult
    //  notifies so the grid updates in place when a hostname or MAC
    //  arrives after the row is shown). No WPF, no I/O.
    // =====================================================================

    public enum DeviceScanPhase
    {
        Idle,
        Pinging,
        ReadingArp,
        ResolvingNames,
        Completed,
        Cancelled,
    }

    /// <summary>How the device was found.</summary>
    public enum DeviceStatus
    {
        /// <summary>Answered ping.</summary>
        Online,

        /// <summary>Ignores ping, but its MAC showed up in the ARP table - it is there.</summary>
        NoPingReply,
    }

    public enum LatencyClass
    {
        Fast,    // under 20 ms
        Normal,  // under 100 ms
        Slow,
        Unknown,
    }

    /// <summary>One device found on the network - a row in the results grid.</summary>
    public sealed class DeviceScanResult : INotifyPropertyChanged
    {
        private string? _hostName;
        private string? _macAddress;
        private bool _isResolvingName;

        public required string IPAddress { get; init; }

        /// <summary>Numeric IPv4 value, for correct sorting (10.0.0.9 before 10.0.0.10).</summary>
        public uint IpSortKey { get; init; }

        public DeviceStatus Status { get; init; }

        /// <summary>Round trip in ms; null when the device didn't answer ping.</summary>
        public long? ResponseTimeMs { get; init; }

        public bool IsThisDevice { get; init; }
        public bool IsGateway { get; init; }

        public string? HostName
        {
            get => _hostName;
            set { if (_hostName != value) { _hostName = value; Changed(); Changed(nameof(HostNameDisplay)); Changed(nameof(HasHostName)); } }
        }

        public string? MacAddress
        {
            get => _macAddress;
            set { if (_macAddress != value) { _macAddress = value; Changed(); Changed(nameof(MacAddressDisplay)); Changed(nameof(IsRandomizedMac)); Changed(nameof(Vendor)); Changed(nameof(VendorDisplay)); } }
        }

        /// <summary>
        /// Manufacturer from the IEEE OUI list (first 3 bytes of the MAC), "Private (randomized)"
        /// for locally administered MACs, or null when unknown. The list is read once, on first use.
        /// </summary>
        public string? Vendor => Smart_Tools.WPF.Controllers.OuiLookupManager.Shared.Lookup(MacAddress);

        public string VendorDisplay => Vendor ?? "(unknown)";

        /// <summary>True while the reverse-DNS lookup is still running.</summary>
        public bool IsResolvingName
        {
            get => _isResolvingName;
            set { if (_isResolvingName != value) { _isResolvingName = value; Changed(); Changed(nameof(HostNameDisplay)); } }
        }

        public bool HasHostName => !string.IsNullOrWhiteSpace(HostName);

        public string HostNameDisplay =>
            HasHostName ? HostName! : IsResolvingName ? "Looking up name…" : IsThisDevice ? "This PC" : IsGateway ? "Router" : "(unknown)";

        public string MacAddressDisplay => string.IsNullOrWhiteSpace(MacAddress) ? "(unknown)" : MacAddress!;

        /// <summary>
        /// Locally administered MAC (second-lowest bit of the first byte):
        /// phones and laptops use these as private, per-network addresses.
        /// </summary>
        public bool IsRandomizedMac =>
            MacAddress is { Length: >= 2 } mac &&
            byte.TryParse(mac[..2], System.Globalization.NumberStyles.HexNumber, null, out byte first) &&
            (first & 0x02) != 0;

        public string ResponseDisplay => ResponseTimeMs is long ms ? (ms < 1 ? "<1 ms" : $"{ms} ms") : "no reply";

        public LatencyClass Latency => ResponseTimeMs switch
        {
            null => LatencyClass.Unknown,
            < 20 => LatencyClass.Fast,
            < 100 => LatencyClass.Normal,
            _ => LatencyClass.Slow,
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>An inclusive IPv4 address range to sweep.</summary>
    public readonly record struct ScanRange(uint First, uint Last, string Label)
    {
        public int Count => (int)(Last - First + 1);
    }

    /// <summary>Settings for one scan, built from the sidebar.</summary>
    public sealed class DeviceScanOptions
    {
        public int TimeoutMs { get; init; } = 500;
        public int MaxConcurrency { get; init; } = 64;
        public required ScanRange Range { get; init; }

        /// <summary>Reverse-DNS each device (runs alongside the sweep).</summary>
        public bool ResolveHostNames { get; init; } = true;

        /// <summary>Also list devices that ignore ping but appear in the ARP table.</summary>
        public bool IncludeArpOnly { get; init; } = true;

        /// <summary>This PC's address/MAC and the gateway, so their rows can be labelled.</summary>
        public string? LocalAddress { get; init; }
        public string? LocalMacAddress { get; init; }
        public string? GatewayAddress { get; init; }

        /// <summary>Prefix of the selected adapter; addresses inside it are also probed with ARP out of that adapter (0 = off).</summary>
        public int LocalPrefixLength { get; init; }
    }

    /// <summary>A network this PC is connected to (one per active adapter).</summary>
    public sealed class LocalNetwork
    {
        public required string InterfaceName { get; init; }
        public required string Kind { get; init; }          // "Wi-Fi", "Ethernet", ...
        public required string LocalAddress { get; init; }
        public int PrefixLength { get; init; }
        public string? GatewayAddress { get; init; }
        public string? MacAddress { get; init; }
        public required ScanRange Range { get; init; }

        /// <summary>The adapter's real network was larger than the scan limit, so the scan covers the /24 around this PC.</summary>
        public bool WasNarrowed { get; init; }

        public string Display => $"{Kind} · {Range.Label}";
    }

    public readonly record struct DeviceScanProgress(int Done, int Total, int Found, DeviceScanPhase Phase);

    public sealed class DeviceScanSummary
    {
        public required IReadOnlyList<DeviceScanResult> Devices { get; init; }
        public required ScanRange Range { get; init; }
        public bool Cancelled { get; init; }
        public TimeSpan Elapsed { get; init; }
        public int Responded => Devices.Count(d => d.Status == DeviceStatus.Online);
    }
}
