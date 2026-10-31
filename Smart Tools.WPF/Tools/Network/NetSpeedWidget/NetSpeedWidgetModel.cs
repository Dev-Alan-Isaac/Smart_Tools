namespace Smart_Tools.WPF.Models
{
    /// <summary>How the Network Speed Widget shows a rate.</summary>
    public enum NetWidgetUnits
    {
        /// <summary>KB/s, MB/s, GB/s.</summary>
        Bytes,
        /// <summary>Kbps, Mbps, Gbps.</summary>
        Bits
    }

    /// <summary>A network adapter the widget can watch.</summary>
    /// <param name="Id">Windows adapter id (GUID text); "" is never used - "all active" is a null choice.</param>
    /// <param name="Name">Connection name ("Wi-Fi").</param>
    /// <param name="Description">Card name as the driver reports it.</param>
    /// <param name="Mac">AA-BB-CC-DD-EE-FF, or null when the adapter has none.</param>
    /// <param name="Manufacturer">From the IEEE OUI list, "Private (randomized)", or null when unknown.</param>
    /// <param name="IsUp">Connected right now.</param>
    public sealed record NetWidgetAdapter(string Id, string Name, string Description, string? Mac, string? Manufacturer, bool IsUp);

    /// <summary>One reading, raised once per refresh.</summary>
    /// <param name="DownBytesPerSec">Download rate.</param>
    /// <param name="UpBytesPerSec">Upload rate.</param>
    /// <param name="SessionDownBytes">Bytes received since the widget started counting.</param>
    /// <param name="SessionUpBytes">Bytes sent since the widget started counting.</param>
    /// <param name="Adapter">The watched adapter, or null when "all active".</param>
    /// <param name="ActiveCount">How many adapters are being summed.</param>
    public sealed record NetWidgetReading(double DownBytesPerSec, double UpBytesPerSec, long SessionDownBytes, long SessionUpBytes, NetWidgetAdapter? Adapter, int ActiveCount);

    /// <summary>The widget's setup (net_speed_widget.json). Preferences only - no traffic, no addresses.</summary>
    public sealed class NetWidgetSettings
    {
        /// <summary>Adapter id, or null for "all active adapters".</summary>
        public string? AdapterId { get; set; }
        public NetWidgetUnits Units { get; set; } = NetWidgetUnits.Bytes;
        /// <summary>Refresh time in ms (500, 1000, 2000, 5000).</summary>
        public int RefreshMs { get; set; } = 1000;
        /// <summary>0.3 - 1.0.</summary>
        public double Opacity { get; set; } = 0.9;
        public bool ClickThrough { get; set; }
        /// <summary>Only the two numbers.</summary>
        public bool MiniMode { get; set; }
        public bool ShowManufacturer { get; set; } = true;
        /// <summary>Last widget position (device-independent px); null = bottom-right corner.</summary>
        public double? Left { get; set; }
        public double? Top { get; set; }
    }
}
