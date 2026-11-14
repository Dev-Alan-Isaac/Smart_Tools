using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Hardware Sensors - plain data only. HardwareSensorsManager reads
    //  LibreHardwareMonitorLib and fills these; the view binds to them.
    //  Its own types: nothing here is shared with System Monitor.
    // =====================================================================

    /// <summary>What a sensor measures (LibreHardwareMonitor's sensor types, matched by name, plus Other).</summary>
    public enum HardwareSensorKind
    {
        Temperature,
        Load,
        Clock,
        Power,
        Fan,
        Control,
        Voltage,
        Current,
        Data,
        SmallData,
        Throughput,
        Level,
        Frequency,
        Flow,
        Factor,
        TimeSpan,
        Energy,
        Noise,
        Humidity,
        Conductivity,
        Other,
    }

    /// <summary>Which part of the PC a sensor group belongs to (list order and icons).</summary>
    public enum HardwarePartKind
    {
        Cpu,
        Gpu,
        Memory,
        Motherboard,
        Storage,
        Network,
        Battery,
        Cooler,
        Psu,
        Other,

        /// <summary>The whole PC: key figures from every other part (built by the manager, not read from a sensor).</summary>
        Pc,
    }

    /// <summary>How worrying a value is: drives the colours.</summary>
    public enum HardwareLevel
    {
        Unknown,
        Normal,
        Warm,
        Hot,
    }

    /// <summary>Where a reading came from (shown in the sensor dump).</summary>
    public enum HardwareSensorSource
    {
        /// <summary>LibreHardwareMonitorLib, no kernel driver needed.</summary>
        Lhm,

        /// <summary>LibreHardwareMonitorLib through the PawnIO driver (CPU MSRs, Super I/O, embedded controller).</summary>
        PawnIo,

        /// <summary>Windows WMI / ACPI classes (manufacturer or generic Win32).</summary>
        Wmi,
    }

    /// <summary>One sensor reading, already in our units: temperatures in °C, clocks in MHz, small data in MB.</summary>
    public sealed record HardwareReading(
        string PartId,
        string PartName,
        HardwarePartKind Part,
        string SensorId,
        string Name,
        HardwareSensorKind Kind,
        float? Value,
        float? Min,
        float? Max,
        HardwareSensorSource Source = HardwareSensorSource.Lhm);

    /// <summary>Everything read once per refresh. Error says why there's nothing (the library couldn't start).</summary>
    public sealed record HardwareSnapshot(DateTime TakenUtc, IReadOnlyList<HardwareReading> Readings, string? Error);

    // ---------------------------------------------------------------------
    //  What the page shows, built fresh every refresh (pure)
    // ---------------------------------------------------------------------

    /// <summary>A big figure at the top of a part ("Temperature 54°C"), with its raw value for the little graph.</summary>
    public sealed record HardwareHeadline(HardwareSensorKind Kind, string Label, string ValueText, double? Raw, HardwareLevel Level);

    public sealed record HardwareSensorRowData(string Id, string Name, string ValueText, string MinText, string MaxText, HardwareLevel Level);

    /// <summary>All sensors of one kind in a part ("Temperatures": Core #1, Core #2...).</summary>
    public sealed record HardwareSensorGroupData(HardwareSensorKind Kind, string Title, IReadOnlyList<HardwareSensorRowData> Rows);

    /// <summary>One part (a CPU, a graphics card, a drive...) with its headline figures and every sensor.</summary>
    public sealed record HardwarePartData(
        string Id,
        string Name,
        HardwarePartKind Part,
        string Summary,
        HardwareLevel Level,
        IReadOnlyList<HardwareHeadline> Headlines,
        IReadOnlyList<HardwareSensorGroupData> Groups)
    {
        public int SensorCount => Groups.Sum(g => g.Rows.Count);
    }

    /// <summary>Remembered between sessions (hardware_sensors.json in %AppData%\Smart Tools). Holds no paths.</summary>
    public sealed class HardwareSensorsSettings
    {
        public int RefreshMs { get; set; } = 1000;
        public bool Fahrenheit { get; set; }

        /// <summary>The kind of part shown when the page opens (the first of that kind).</summary>
        public HardwarePartKind Part { get; set; } = HardwarePartKind.Pc;
    }

    // ---------------------------------------------------------------------
    //  Live items (updated in place every refresh: no flicker, selection kept)
    // ---------------------------------------------------------------------

    public abstract class HardwareLiveItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>A part in the list on the left.</summary>
    public sealed class HardwarePartItem : HardwareLiveItem
    {
        private string _name = "";
        private string _summary = "";
        private HardwareLevel _level;
        private string _partTitle = "";
        private IReadOnlyList<HardwareHeadline> _headlines = Array.Empty<HardwareHeadline>();

        public required string Id { get; init; }
        public required HardwarePartKind Part { get; init; }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Summary { get => _summary; set => Set(ref _summary, value); }
        public HardwareLevel Level { get => _level; set => Set(ref _level, value); }

        /// <summary>"Processor", "Graphics"... (the PC page's tiles show it under the name).</summary>
        public string PartTitle { get => _partTitle; set => Set(ref _partTitle, value); }

        /// <summary>Every headline figure of the part, for the PC page's at-a-glance tiles. Only replaced when a value changed.</summary>
        public IReadOnlyList<HardwareHeadline> Headlines
        {
            get => _headlines;
            set
            {
                if (!_headlines.SequenceEqual(value)) Set(ref _headlines, value);
            }
        }
    }

    /// <summary>A headline tile: label, value, colour and the last minute as a little graph.</summary>
    public sealed class HardwareHeadlineTile : HardwareLiveItem
    {
        private string _valueText = "–";
        private HardwareLevel _level;
        private IReadOnlyList<double> _history = Array.Empty<double>();

        public required HardwareSensorKind Kind { get; init; }
        public required string Label { get; init; }
        public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
        public HardwareLevel Level { get => _level; set => Set(ref _level, value); }
        public IReadOnlyList<double> History { get => _history; set => Set(ref _history, value); }

        /// <summary>Loads are 0-100; everything else scales to what was seen.</summary>
        public double GraphMaximum => Kind is HardwareSensorKind.Load or HardwareSensorKind.Control or HardwareSensorKind.Level ? 100 : 0;
    }

    public sealed class HardwareSensorRow : HardwareLiveItem
    {
        private string _valueText = "–";
        private string _minText = "";
        private string _maxText = "";
        private HardwareLevel _level;

        public required string Id { get; init; }
        public required string Name { get; init; }
        public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
        public string MinText { get => _minText; set => Set(ref _minText, value); }
        public string MaxText { get => _maxText; set => Set(ref _maxText, value); }
        public HardwareLevel Level { get => _level; set => Set(ref _level, value); }
    }

    public sealed class HardwareSensorGroup
    {
        public required HardwareSensorKind Kind { get; init; }
        public required string Title { get; init; }
        public ObservableCollection<HardwareSensorRow> Rows { get; } = new();
        public string CountText => Rows.Count == 1 ? "1 sensor" : $"{Rows.Count} sensors";
    }
}
