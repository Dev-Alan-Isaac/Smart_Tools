using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  System Monitor data. Plain data - no WPF, no hardware libraries.
    //  Everything comes from Windows itself (performance counters, WMI,
    //  the DirectX adapter list), like Task Manager's Performance tab.
    //  SystemMonitorManager fills these; the view binds to them.
    // =====================================================================

    /// <summary>The sections in the list on the left (Task Manager's Performance tab, plus PC).</summary>
    public enum SystemMonitorSectionKind
    {
        Pc,
        Cpu,
        Memory,
        Disk,
        Network,
        Gpu,
    }

    /// <summary>How worrying a value is: drives the graph colour.</summary>
    public enum SystemMonitorLevel
    {
        Unknown,
        Normal,
        Warm,
        Hot,
    }

    /// <summary>What the section's big graph shows.</summary>
    public enum SystemMonitorGraphKind
    {
        /// <summary>No graph (the PC section shows the other sections instead).</summary>
        None,

        /// <summary>0-100 %.</summary>
        Percent,

        /// <summary>Bytes per second, scaled to the highest value shown; two lines (receive / send).</summary>
        Rate,
    }

    // ---------------------------------------------------------------------
    //  Raw readings (one snapshot per refresh)
    // ---------------------------------------------------------------------

    /// <summary>One physical drive ("0 C: D:" in Windows' counters).</summary>
    public sealed record SystemMonitorDisk(int Number, string Letters, double ReadBytesPerSec, double WriteBytesPerSec, double ActivePercent, double? ResponseMs);

    /// <summary>One network adapter that's connected.</summary>
    public sealed record SystemMonitorAdapter(
        string Id,
        string Name,
        string Description,
        string Kind,
        double ReceiveBytesPerSec,
        double SendBytesPerSec,
        long SpeedBitsPerSec,
        string? IPv4,
        string? IPv6,
        string? DnsSuffix)
    {
        public bool IsWireless => Kind == "Wi-Fi";
    }

    /// <summary>One graphics engine's use ("3D", "Copy", "Video Decode"...).</summary>
    public sealed record SystemMonitorEngine(string Name, double Percent);

    /// <summary>A graphics adapter as DirectX lists it (read once): its id, name and memory sizes.</summary>
    public sealed record SystemMonitorGpuAdapter(string Luid, string Name, long DedicatedBytes, long SharedBytes);

    /// <summary>One graphics card's use right now.</summary>
    public sealed record SystemMonitorGpu(
        string Luid,
        string Name,
        double LoadPercent,
        IReadOnlyList<SystemMonitorEngine> Engines,
        long? DedicatedUsedBytes,
        long? DedicatedTotalBytes,
        long? SharedUsedBytes,
        long? SharedTotalBytes);

    public sealed record SystemMonitorBattery(int Percent, bool Charging, bool OnAc, TimeSpan? Remaining);

    /// <summary>
    /// Temperatures read once per refresh via LibreHardwareMonitorLib (the
    /// same library Hardware Sensors uses). Null/empty when the library
    /// couldn't start (no driver, no admin rights) - the rest of System
    /// Monitor still works. GPUs and disks are keyed by the hardware name
    /// LibreHardwareMonitor gives them, matched to our sections by name.
    /// </summary>
    public sealed record SystemMonitorTemps(double? CpuC, IReadOnlyDictionary<string, double> GpuByName, IReadOnlyDictionary<string, double> DiskByName)
    {
        public static readonly SystemMonitorTemps Empty = new(null, new Dictionary<string, double>(), new Dictionary<string, double>());
    }

    /// <summary>Everything read once per refresh.</summary>
    public sealed class SystemMonitorSnapshot
    {
        public DateTime TakenUtc { get; init; } = DateTime.UtcNow;

        // CPU and the whole PC
        public double? CpuPercent { get; init; }
        public IReadOnlyList<double> CorePercents { get; init; } = Array.Empty<double>();

        /// <summary>Speed against the base clock (can pass 100 with turbo): speed = base × this / 100.</summary>
        public double? CpuPerformancePercent { get; init; }

        public int ProcessCount { get; init; }
        public int ThreadCount { get; init; }
        public int HandleCount { get; init; }
        public TimeSpan Uptime { get; init; }
        public SystemMonitorBattery? Battery { get; init; }

        // Memory
        public long MemoryTotalBytes { get; init; }
        public long MemoryAvailableBytes { get; init; }
        public long CommitUsedBytes { get; init; }
        public long CommitLimitBytes { get; init; }
        public long? CachedBytes { get; init; }
        public long? PagedPoolBytes { get; init; }
        public long? NonPagedPoolBytes { get; init; }

        public IReadOnlyList<SystemMonitorDisk> Disks { get; init; } = Array.Empty<SystemMonitorDisk>();
        public IReadOnlyList<SystemMonitorAdapter> Adapters { get; init; } = Array.Empty<SystemMonitorAdapter>();
        public IReadOnlyList<SystemMonitorGpu> Gpus { get; init; } = Array.Empty<SystemMonitorGpu>();
        public SystemMonitorTemps Temps { get; init; } = SystemMonitorTemps.Empty;

        public long MemoryUsedBytes => Math.Max(0, MemoryTotalBytes - MemoryAvailableBytes);
        public double MemoryPercent => MemoryTotalBytes > 0 ? 100.0 * MemoryUsedBytes / MemoryTotalBytes : 0;
    }

    // ---------------------------------------------------------------------
    //  Fixed facts (read once, off the UI thread)
    // ---------------------------------------------------------------------

    /// <summary>A label and its value ("Cores" / "8"). Used for live figures and fixed facts alike.</summary>
    public sealed record SystemMonitorStat(string Label, string Value);

    public sealed record SystemMonitorDiskInfo(string Model, string? Media, string? Bus, long SizeBytes);

    public sealed record SystemMonitorGpuInfo(string? DriverVersion, DateTime? DriverDate);

    /// <summary>What doesn't change while the app runs: names, sizes, versions.</summary>
    public sealed class SystemMonitorSpecs
    {
        public static readonly SystemMonitorSpecs Empty = new();

        /// <summary>Computer, Windows and motherboard facts for the PC section.</summary>
        public IReadOnlyList<SystemMonitorStat> Pc { get; init; } = Array.Empty<SystemMonitorStat>();

        public string? CpuName { get; init; }
        public double? CpuBaseMhz { get; init; }
        public int CpuSockets { get; init; }
        public int CpuCores { get; init; }
        public int CpuThreads { get; init; }
        public bool? Virtualization { get; init; }
        public long? L2CacheBytes { get; init; }
        public long? L3CacheBytes { get; init; }

        public long InstalledMemoryBytes { get; init; }
        public string? MemoryType { get; init; }
        public int MemorySpeedMts { get; init; }
        public int MemorySlotsUsed { get; init; }
        public int MemorySlotsTotal { get; init; }
        public string? MemoryFormFactor { get; init; }

        /// <summary>Drive number (as in "Disk 0") → model, SSD/HDD, bus, size.</summary>
        public IReadOnlyDictionary<int, SystemMonitorDiskInfo> Disks { get; init; } = new Dictionary<int, SystemMonitorDiskInfo>();

        /// <summary>Drive letter ("C:") → formatted size.</summary>
        public IReadOnlyDictionary<string, long> VolumeSizes { get; init; } = new Dictionary<string, long>();

        /// <summary>"C:" - the drive Windows is on.</summary>
        public string? SystemDrive { get; init; }

        /// <summary>Graphics card name → driver.</summary>
        public IReadOnlyDictionary<string, SystemMonitorGpuInfo> Gpus { get; init; } = new Dictionary<string, SystemMonitorGpuInfo>();
    }

    // ---------------------------------------------------------------------
    //  What the page shows: one entry per section
    // ---------------------------------------------------------------------

    /// <summary>
    /// One section, built fresh every refresh from the snapshot and the specs
    /// (SystemMonitorManager.BuildSections). The view keeps the history and
    /// updates its list items in place from these.
    /// </summary>
    public sealed record SystemMonitorSectionData
    {
        /// <summary>Stable across refreshes: "cpu", "disk0", "net:{adapter id}", "gpu:{luid}".</summary>
        public required string Id { get; init; }
        public required SystemMonitorSectionKind Kind { get; init; }

        /// <summary>"CPU", "Disk 0 (C:)", "Wi-Fi", "GPU 0".</summary>
        public required string Title { get; init; }

        /// <summary>What it is: the processor's name, the drive's model, the adapter's name.</summary>
        public string Device { get; init; } = "";

        /// <summary>The short line under the title in the list ("12%  4.20 GHz").</summary>
        public string Summary { get; init; } = "";

        public SystemMonitorGraphKind Graph { get; init; }

        /// <summary>The big graph's caption ("% Utilization", "Throughput").</summary>
        public string GraphCaption { get; init; } = "";

        /// <summary>The graph's value: a percentage, or bytes/s (receive) for a rate graph.</summary>
        public double Value { get; init; }

        /// <summary>The second line of a rate graph (send).</summary>
        public double? Value2 { get; init; }

        /// <summary>Disks only: read + write bytes/s for the second, smaller graph.</summary>
        public double? TransferRead { get; init; }
        public double? TransferWrite { get; init; }

        /// <summary>CPU, GPU and disk sections: null when no sensor was found (no driver, no admin rights).</summary>
        public double? TemperatureC { get; init; }

        public bool IsWireless { get; init; }
        public SystemMonitorLevel Level { get; init; }

        /// <summary>The big figures under the graph (they change every refresh).</summary>
        public IReadOnlyList<SystemMonitorStat> Live { get; init; } = Array.Empty<SystemMonitorStat>();

        /// <summary>The fixed facts on the right (base speed, capacity, driver...).</summary>
        public IReadOnlyList<SystemMonitorStat> Info { get; init; } = Array.Empty<SystemMonitorStat>();

        /// <summary>CPU only: each logical processor's use.</summary>
        public IReadOnlyList<double> Cores { get; init; } = Array.Empty<double>();

        /// <summary>GPU only: the engines worth showing.</summary>
        public IReadOnlyList<SystemMonitorEngine> Engines { get; init; } = Array.Empty<SystemMonitorEngine>();
    }

    /// <summary>Remembered between sessions (system_monitor.json in %AppData%\Smart Tools). Holds no paths.</summary>
    public sealed class SystemMonitorSettings
    {
        public int RefreshMs { get; set; } = 1000;

        /// <summary>The section shown when the page opens (the first of that kind).</summary>
        public SystemMonitorSectionKind Section { get; set; } = SystemMonitorSectionKind.Pc;
    }

    // ---------------------------------------------------------------------
    //  Live items (updated in place every refresh: no flicker, selection kept)
    // ---------------------------------------------------------------------

    public abstract class SystemMonitorLiveItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>A section in the list on the left (and a tile on the PC section).</summary>
    public sealed class SystemMonitorSection : SystemMonitorLiveItem
    {
        private string _title = "";
        private string _device = "";
        private string _summary = "";
        private SystemMonitorLevel _level;
        private bool _isWireless;
        private IReadOnlyList<double> _history = Array.Empty<double>();
        private IReadOnlyList<double> _history2 = Array.Empty<double>();

        public required string Id { get; init; }
        public required SystemMonitorSectionKind Kind { get; init; }
        public SystemMonitorGraphKind Graph { get; init; }

        public string Title { get => _title; set => Set(ref _title, value); }
        public string Device { get => _device; set => Set(ref _device, value); }
        public string Summary { get => _summary; set => Set(ref _summary, value); }
        public SystemMonitorLevel Level { get => _level; set => Set(ref _level, value); }
        public bool IsWireless { get => _isWireless; set => Set(ref _isWireless, value); }

        /// <summary>The last minute, oldest first (the mini graph).</summary>
        public IReadOnlyList<double> History { get => _history; set => Set(ref _history, value); }

        /// <summary>The second line of a rate graph (send).</summary>
        public IReadOnlyList<double> History2 { get => _history2; set => Set(ref _history2, value); }

        /// <summary>0 = scale to the highest value (rates), 100 for percentages.</summary>
        public double GraphMaximum => Graph == SystemMonitorGraphKind.Percent ? 100 : 0;
    }

    /// <summary>A label/value shown under the graph (updated in place).</summary>
    public sealed class SystemMonitorStatRow : SystemMonitorLiveItem
    {
        private string _value = "";

        public required string Label { get; init; }
        public string Value { get => _value; set => Set(ref _value, value); }
    }

    /// <summary>One logical processor's bar, or one graphics engine's bar.</summary>
    public sealed class SystemMonitorBar : SystemMonitorLiveItem
    {
        private double _percent;
        private SystemMonitorLevel _level;
        private string _toolTipText = "";

        /// <summary>Under the bar ("3", "Video Decode").</summary>
        public required string Label { get; init; }

        /// <summary>The tooltip's name for it ("Logical processor 3"); the label when not set.</summary>
        public string? Caption { get; init; }

        public double Percent
        {
            get => _percent;
            set
            {
                Set(ref _percent, value);
                ToolTipText = $"{Caption ?? Label}: {value:0}%";
            }
        }

        public string ToolTipText { get => _toolTipText; private set => Set(ref _toolTipText, value); }
        public SystemMonitorLevel Level { get => _level; set => Set(ref _level, value); }
    }
}
