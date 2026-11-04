using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Hardware Sensors - the pure part: names to our kinds, units, warning
    /// levels, the headline picks, the parts the page lists, the report and
    /// settings. No hardware library, no WPF - unit-tested. Reading the
    /// hardware is in HardwareSensorsManager.cs.
    /// </summary>
    public sealed partial class HardwareSensorsManager
    {
        public static readonly int[] RefreshChoicesMs = { 500, 1000, 2000, 5000 };
        public const int HistoryLength = 60;

        private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

        // =================================================================
        //  From LibreHardwareMonitor's names to ours
        // =================================================================

        /// <summary>HardwareType name → our part. Names, not the enum, so a newer library with new members still maps.</summary>
        public static HardwarePartKind PartFrom(string hardwareType) => hardwareType switch
        {
            "Cpu" => HardwarePartKind.Cpu,
            "GpuNvidia" or "GpuAmd" or "GpuIntel" => HardwarePartKind.Gpu,
            "Memory" => HardwarePartKind.Memory,
            "Motherboard" or "SuperIO" or "EmbeddedController" => HardwarePartKind.Motherboard,
            "Storage" => HardwarePartKind.Storage,
            "Network" => HardwarePartKind.Network,
            "Battery" => HardwarePartKind.Battery,
            "Cooler" => HardwarePartKind.Cooler,
            "Psu" => HardwarePartKind.Psu,
            _ => HardwarePartKind.Other,
        };

        public static HardwareSensorKind KindFrom(string sensorType) =>
            Enum.TryParse(sensorType, out HardwareSensorKind k) && Enum.IsDefined(k) && !int.TryParse(sensorType, out _) ? k : HardwareSensorKind.Other;

        // =================================================================
        //  Units
        // =================================================================

        /// <summary>"512 B", "1.5 KB", "12 GB".</summary>
        public static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = Math.Max(0, bytes);
            int u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return u == 0 ? $"{v:0} B" : v.ToString(v < 10 ? "0.0" : "0", Culture) + " " + units[u];
        }

        public static string FormatTemperature(double? celsius, bool fahrenheit) =>
            celsius is double c ? $"{(fahrenheit ? c * 9 / 5 + 32 : c):0}°{(fahrenheit ? "F" : "C")}" : "–";

        /// <summary>"4.20 GHz" or "800 MHz".</summary>
        public static string FormatClock(double mhz) => mhz >= 1000 ? (mhz / 1000).ToString("0.00", Culture) + " GHz" : $"{mhz:0} MHz";

        /// <summary>"3 h 12 min", "12 min", "45 s".</summary>
        public static string FormatDuration(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
            if (t.TotalMinutes >= 1) return $"{t.Minutes} min";
            return $"{t.Seconds} s";
        }

        /// <summary>A sensor value in its unit (temperatures in °C or °F). Unknown/NaN → "–".</summary>
        public static string FormatValue(HardwareSensorKind kind, float? value, bool fahrenheit)
        {
            if (value is not float raw || float.IsNaN(raw) || float.IsInfinity(raw)) return "–";
            double v = raw;
            return kind switch
            {
                HardwareSensorKind.Temperature => FormatTemperature(v, fahrenheit),
                HardwareSensorKind.Load or HardwareSensorKind.Control or HardwareSensorKind.Level or HardwareSensorKind.Humidity => v.ToString("0.0", Culture) + " %",
                HardwareSensorKind.Clock => FormatClock(v),
                HardwareSensorKind.Fan => $"{v:0} RPM",
                HardwareSensorKind.Voltage => v.ToString("0.000", Culture) + " V",
                HardwareSensorKind.Current => v.ToString("0.00", Culture) + " A",
                HardwareSensorKind.Power => v.ToString(v < 10 ? "0.0" : "0", Culture) + " W",
                HardwareSensorKind.Data => v.ToString(v < 10 ? "0.00" : "0.0", Culture) + " GB",
                HardwareSensorKind.SmallData => FormatBytes(v * 1024 * 1024),
                HardwareSensorKind.Throughput => FormatBytes(v) + "/s",
                HardwareSensorKind.Frequency => $"{v:0} Hz",
                HardwareSensorKind.Flow => v.ToString("0.0", Culture) + " L/h",
                HardwareSensorKind.TimeSpan => FormatDuration(v),
                HardwareSensorKind.Energy => $"{v:0} mWh",
                HardwareSensorKind.Noise => v.ToString("0.0", Culture) + " dBA",
                HardwareSensorKind.Conductivity => v.ToString("0.0", Culture) + " µS/cm",
                HardwareSensorKind.Factor => v.ToString("0.000", Culture),
                _ => v.ToString("0.###", Culture),
            };
        }

        // =================================================================
        //  Warning levels
        // =================================================================

        /// <summary>Usage: amber from 80%, red from 95%.</summary>
        public static HardwareLevel UsageLevel(double? percent) => percent switch
        {
            null => HardwareLevel.Unknown,
            >= 95 => HardwareLevel.Hot,
            >= 80 => HardwareLevel.Warm,
            _ => HardwareLevel.Normal,
        };

        /// <summary>Drives get warm far sooner than chips (SSDs throttle around 70 °C, CPUs/GPUs around 95-100 °C).</summary>
        public static HardwareLevel TemperatureLevel(HardwarePartKind part, double? celsius)
        {
            if (celsius is not double c || double.IsNaN(c)) return HardwareLevel.Unknown;
            (double warm, double hot) = part switch
            {
                HardwarePartKind.Storage => (55, 65),
                HardwarePartKind.Battery => (45, 55),
                HardwarePartKind.Cpu or HardwarePartKind.Gpu => (80, 90),
                _ => (70, 85),
            };
            return c >= hot ? HardwareLevel.Hot : c >= warm ? HardwareLevel.Warm : HardwareLevel.Normal;
        }

        public static HardwareLevel LevelFor(HardwareReading r) => r.Kind switch
        {
            HardwareSensorKind.Temperature => TemperatureLevel(r.Part, r.Value),
            HardwareSensorKind.Load => UsageLevel(r.Value),
            _ => r.Value.HasValue ? HardwareLevel.Normal : HardwareLevel.Unknown,
        };

        private static HardwareLevel Worst(IEnumerable<HardwareLevel> levels) => levels.DefaultIfEmpty(HardwareLevel.Normal).Max();

        // =================================================================
        //  Headline picks: the number people mean
        // =================================================================

        private static IEnumerable<HardwareReading> Valid(IEnumerable<HardwareReading> all, HardwareSensorKind kind) =>
            all.Where(r => r.Kind == kind && r.Value is float v && !float.IsNaN(v));

        private static HardwareReading? FirstNamed(IEnumerable<HardwareReading> list, params string[] names)
        {
            var items = list.ToList();
            foreach (string n in names)
            {
                var hit = items.FirstOrDefault(r => r.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// The temperature people mean for a part: Package (Intel) or Tctl/Tdie
        /// (AMD) for a CPU - not the hottest core; the core for a GPU; the drive's
        /// own (Composite on NVMe). Falls back to the hottest.
        /// </summary>
        public static HardwareReading? PickTemperature(IEnumerable<HardwareReading> part)
        {
            var temps = Valid(part, HardwareSensorKind.Temperature).ToList();
            if (temps.Count == 0) return null;
            return FirstNamed(temps, "Package", "Tctl", "Tdie", "Core Average", "GPU Core", "Composite", "Temperature", "CPU")
                   ?? temps.MaxBy(t => t.Value);
        }

        public static HardwareReading? PickLoad(IEnumerable<HardwareReading> part)
        {
            var loads = Valid(part, HardwareSensorKind.Load).ToList();
            return loads.Count == 0 ? null : FirstNamed(loads, "CPU Total", "GPU Core", "D3D 3D", "Memory", "Used Space", "Total Activity") ?? loads[0];
        }

        /// <summary>CPU: the average of the core clocks (the bus clock left out); otherwise the core clock or the first.</summary>
        public static double? PickClock(IEnumerable<HardwareReading> part, HardwarePartKind kind)
        {
            var clocks = Valid(part, HardwareSensorKind.Clock).Where(r => r.Value > 0).ToList();
            if (clocks.Count == 0) return null;
            if (kind == HardwarePartKind.Cpu)
            {
                var cores = clocks.Where(r => r.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
                                              && !r.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)).ToList();
                if (cores.Count > 0) return cores.Average(r => (double)r.Value!.Value);
                clocks = clocks.Where(r => !r.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase)).ToList();
                return clocks.Count == 0 ? null : clocks.Average(r => (double)r.Value!.Value);
            }
            return (FirstNamed(clocks, "GPU Core", "Core") ?? clocks[0]).Value;
        }

        public static HardwareReading? PickPower(IEnumerable<HardwareReading> part)
        {
            var power = Valid(part, HardwareSensorKind.Power).ToList();
            return power.Count == 0 ? null : FirstNamed(power, "Package", "GPU Power", "Total", "CPU") ?? power[0];
        }

        /// <summary>The fastest fan that's turning (0 RPM fans are often unconnected headers).</summary>
        public static HardwareReading? PickFan(IEnumerable<HardwareReading> part) =>
            Valid(part, HardwareSensorKind.Fan).Where(r => r.Value > 0).MaxBy(r => r.Value);

        // =================================================================
        //  Parts: what the page shows
        // =================================================================

        private static readonly HardwareSensorKind[] GroupOrder =
        {
            HardwareSensorKind.Temperature, HardwareSensorKind.Load, HardwareSensorKind.Clock, HardwareSensorKind.Power,
            HardwareSensorKind.Fan, HardwareSensorKind.Control, HardwareSensorKind.Voltage, HardwareSensorKind.Current,
            HardwareSensorKind.Data, HardwareSensorKind.SmallData, HardwareSensorKind.Throughput, HardwareSensorKind.Level,
            HardwareSensorKind.Frequency, HardwareSensorKind.Energy, HardwareSensorKind.TimeSpan, HardwareSensorKind.Flow,
            HardwareSensorKind.Factor, HardwareSensorKind.Noise, HardwareSensorKind.Humidity, HardwareSensorKind.Conductivity,
            HardwareSensorKind.Other,
        };

        public static string GroupTitle(HardwareSensorKind kind) => kind switch
        {
            HardwareSensorKind.Temperature => "Temperatures",
            HardwareSensorKind.Load => "Load",
            HardwareSensorKind.Clock => "Clocks",
            HardwareSensorKind.Power => "Power",
            HardwareSensorKind.Fan => "Fans",
            HardwareSensorKind.Control => "Fan control",
            HardwareSensorKind.Voltage => "Voltages",
            HardwareSensorKind.Current => "Currents",
            HardwareSensorKind.Data => "Data",
            HardwareSensorKind.SmallData => "Memory",
            HardwareSensorKind.Throughput => "Throughput",
            HardwareSensorKind.Level => "Levels",
            HardwareSensorKind.Frequency => "Frequencies",
            HardwareSensorKind.Energy => "Energy",
            HardwareSensorKind.TimeSpan => "Time",
            HardwareSensorKind.Flow => "Flow",
            HardwareSensorKind.Factor => "Factors",
            HardwareSensorKind.Noise => "Noise",
            HardwareSensorKind.Humidity => "Humidity",
            HardwareSensorKind.Conductivity => "Conductivity",
            _ => "Other",
        };

        /// <summary>
        /// One entry per part (and per sub-part, like the motherboard's sensor
        /// chip), CPUs first then graphics, memory, motherboard, drives...,
        /// in the library's order within a kind. Parts with no sensors are left out.
        /// </summary>
        public static List<HardwarePartData> BuildParts(IEnumerable<HardwareReading> readings, bool fahrenheit)
        {
            var list = new List<HardwarePartData>();
            var byPart = readings.GroupBy(r => r.PartId).ToList();
            foreach (var g in byPart.OrderBy(g => g.First().Part).ThenBy(g => byPart.IndexOf(g)))
            {
                var part = g.ToList();
                var first = part[0];

                var headlines = new List<HardwareHeadline>();
                if (PickTemperature(part) is { } t) headlines.Add(new HardwareHeadline(HardwareSensorKind.Temperature, "Temperature", FormatTemperature(t.Value, fahrenheit), t.Value, TemperatureLevel(first.Part, t.Value)));
                if (PickLoad(part) is { } l) headlines.Add(new HardwareHeadline(HardwareSensorKind.Load, first.Part == HardwarePartKind.Storage && l.Name.Contains("Used", StringComparison.OrdinalIgnoreCase) ? "Used space" : "Load", $"{l.Value:0}%", l.Value, UsageLevel(l.Value)));
                if (PickClock(part, first.Part) is double mhz) headlines.Add(new HardwareHeadline(HardwareSensorKind.Clock, "Clock", FormatClock(mhz), mhz, HardwareLevel.Normal));
                if (PickPower(part) is { } p) headlines.Add(new HardwareHeadline(HardwareSensorKind.Power, "Power", FormatValue(HardwareSensorKind.Power, p.Value, fahrenheit), p.Value, HardwareLevel.Normal));
                if (PickFan(part) is { } f) headlines.Add(new HardwareHeadline(HardwareSensorKind.Fan, "Fan", FormatValue(HardwareSensorKind.Fan, f.Value, fahrenheit), f.Value, HardwareLevel.Normal));
                if (first.Part == HardwarePartKind.Battery && Valid(part, HardwareSensorKind.Level).FirstOrDefault(r => r.Name.Contains("Charge", StringComparison.OrdinalIgnoreCase)) is { } charge)
                    headlines.Add(new HardwareHeadline(HardwareSensorKind.Level, "Charge", $"{charge.Value:0}%", charge.Value, charge.Value < 15 ? HardwareLevel.Warm : HardwareLevel.Normal));

                var groups = new List<HardwareSensorGroupData>();
                foreach (var kind in GroupOrder)
                {
                    var rows = part.Where(r => r.Kind == kind)
                                   .Select(r => new HardwareSensorRowData(r.SensorId, r.Name, FormatValue(kind, r.Value, fahrenheit), FormatValue(kind, r.Min, fahrenheit), FormatValue(kind, r.Max, fahrenheit), LevelFor(r)))
                                   .ToList();
                    if (rows.Count > 0) groups.Add(new HardwareSensorGroupData(kind, GroupTitle(kind), rows));
                }
                if (groups.Count == 0) continue;

                string summary = headlines.Count == 0
                    ? (groups.Sum(x => x.Rows.Count) is int n && n == 1 ? "1 sensor" : $"{groups.Sum(x => x.Rows.Count)} sensors")
                    : string.Join(" · ", headlines.Take(2).Select(h => h.ValueText));
                list.Add(new HardwarePartData(first.PartId, first.PartName, first.Part, summary,
                    Worst(headlines.Select(h => h.Level)), headlines, groups));
            }
            if (list.Count > 0) list.Insert(0, BuildPc(list));
            return list;
        }

        public const string PcId = "pc";

        /// <summary>
        /// The "This PC" entry: the figures people check first, taken from the other parts
        /// (CPU and graphics temperature/load/power, memory in use, the hottest drive). It has no
        /// sensors of its own - the page shows a tile per part under these.
        /// </summary>
        public static HardwarePartData BuildPc(IReadOnlyList<HardwarePartData> parts)
        {
            var headlines = new List<HardwareHeadline>();

            void Add(HardwarePartData? part, HardwareSensorKind kind, string label)
            {
                if (part?.Headlines.FirstOrDefault(h => h.Kind == kind) is { } h)
                    headlines.Add(h with { Label = label });
            }

            var cpu = parts.FirstOrDefault(p => p.Part == HardwarePartKind.Cpu);
            var gpu = parts.Where(p => p.Part == HardwarePartKind.Gpu).MaxBy(p => p.SensorCount);   // the real card over the integrated one
            var memory = parts.FirstOrDefault(p => p.Part == HardwarePartKind.Memory);
            Add(cpu, HardwareSensorKind.Temperature, "CPU temperature");
            Add(cpu, HardwareSensorKind.Load, "CPU load");
            Add(cpu, HardwareSensorKind.Power, "CPU power");
            Add(gpu, HardwareSensorKind.Temperature, "GPU temperature");
            Add(gpu, HardwareSensorKind.Load, "GPU load");
            Add(gpu, HardwareSensorKind.Power, "GPU power");
            Add(memory, HardwareSensorKind.Load, "Memory used");
            var hottestDrive = parts.Where(p => p.Part == HardwarePartKind.Storage)
                                    .Select(p => p.Headlines.FirstOrDefault(h => h.Kind == HardwareSensorKind.Temperature))
                                    .Where(h => h?.Raw != null).MaxBy(h => h!.Raw);
            if (hottestDrive != null) headlines.Add(hottestDrive with { Label = "Hottest drive" });

            int sensors = parts.Sum(p => p.SensorCount);
            string summary = (parts.Count == 1 ? "1 part" : $"{parts.Count} parts") + " · " + (sensors == 1 ? "1 sensor" : $"{sensors} sensors");
            return new HardwarePartData(PcId, "This PC", HardwarePartKind.Pc, summary,
                Worst(parts.Select(p => p.Level)), headlines, Array.Empty<HardwareSensorGroupData>());
        }

        /// <summary>
        /// Why the CPU (and motherboard) show no temperature, when they don't:
        /// LibreHardwareMonitor reads them through its PawnIO driver, and only
        /// with administrator rights. Null when the CPU temperature is there (or
        /// nothing was read yet). Graphics cards and drives work without either.
        /// </summary>
        public static string? CoverageNote(IReadOnlyList<HardwarePartData> parts, bool isAdmin, bool driverInstalled)
        {
            if (parts.Count == 0) return null;
            bool cpuTemp = parts.Any(p => p.Part == HardwarePartKind.Cpu && p.Headlines.Any(h => h.Kind == HardwareSensorKind.Temperature));
            if (cpuTemp) return null;
            string what = "No CPU temperature here: LibreHardwareMonitor reads the CPU and motherboard through its PawnIO driver";
            return (isAdmin, driverInstalled) switch
            {
                (true, true) => "No CPU temperature here: this processor doesn't report one to LibreHardwareMonitor.",
                (false, true) => what + ", and only when this app runs as administrator.",
                (true, false) => what + ", which isn't installed on this PC.",
                _ => what + " (not installed on this PC) and administrator rights.",
            } + " Graphics cards and drives don't need either.";
        }

        // =================================================================
        //  Keeping the page's lists up to date in place
        // =================================================================

        /// <summary>
        /// Makes <paramref name="items"/> hold one item per id, in the order given:
        /// existing items are kept (moved if needed), new ids get
        /// <paramref name="create"/>, gone ones are removed - no flicker, the
        /// selection stays put.
        /// </summary>
        public static void SyncList<T>(ObservableCollection<T> items, IReadOnlyList<string> ids, Func<T, string> idOf, Func<string, T> create)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (i < items.Count && idOf(items[i]) == ids[i]) continue;
                int found = -1;
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (idOf(items[j]) != ids[i]) continue;
                    found = j;
                    break;
                }
                if (found >= 0) items.Move(found, i);
                else items.Insert(i, create(ids[i]));
            }
            while (items.Count > ids.Count) items.RemoveAt(items.Count - 1);
        }

        /// <summary>"Temperatures:a,b|Load:c" - when it changes, the sensor groups are rebuilt; otherwise rows update in place.</summary>
        public static string GroupsSignature(HardwarePartData part) =>
            string.Join("|", part.Groups.Select(g => g.Kind + ":" + string.Join(",", g.Rows.Select(r => r.Id))));

        // =================================================================
        //  History for the little graphs
        // =================================================================

        /// <summary>The last <see cref="HistoryLength"/> values, oldest first.</summary>
        public sealed class History
        {
            private readonly double[] _values;
            private int _start;

            public History(int capacity = HistoryLength) => _values = new double[Math.Max(2, capacity)];

            public int Count { get; private set; }

            public void Add(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
                if (Count < _values.Length)
                {
                    _values[(_start + Count) % _values.Length] = value;
                    Count++;
                }
                else
                {
                    _values[_start] = value;
                    _start = (_start + 1) % _values.Length;
                }
            }

            public IReadOnlyList<double> Values()
            {
                var list = new double[Count];
                for (int i = 0; i < Count; i++) list[i] = _values[(_start + i) % _values.Length];
                return list;
            }
        }

        // =================================================================
        //  Report
        // =================================================================

        /// <summary>Every part and sensor as text (Copy and Save report), with lowest and highest seen.</summary>
        public static string BuildReport(IReadOnlyList<HardwarePartData> parts, DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Hardware sensors - {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
            sb.AppendLine(new string('=', 40));
            if (parts.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("No sensors were found.");
            }
            foreach (var part in parts)
            {
                sb.AppendLine();
                sb.AppendLine($"{part.Name} ({PartTitle(part.Part)})");
                if (part.Part == HardwarePartKind.Pc)
                {
                    int labelWidth = part.Headlines.Select(h => h.Label.Length).DefaultIfEmpty(0).Max();
                    foreach (var h in part.Headlines) sb.AppendLine($"  {h.Label.PadRight(labelWidth)}  {h.ValueText}");
                    continue;
                }
                int width = part.Groups.SelectMany(g => g.Rows).Select(r => r.Name.Length).DefaultIfEmpty(0).Max();
                foreach (var g in part.Groups)
                {
                    sb.AppendLine($"  {g.Title}");
                    foreach (var r in g.Rows)
                        sb.AppendLine($"    {r.Name.PadRight(width)}  {r.ValueText}" + (r.MinText != "–" || r.MaxText != "–" ? $"   (low {r.MinText}, high {r.MaxText})" : ""));
                }
            }
            return sb.ToString();
        }

        public static string PartTitle(HardwarePartKind part) => part switch
        {
            HardwarePartKind.Cpu => "Processor",
            HardwarePartKind.Gpu => "Graphics",
            HardwarePartKind.Memory => "Memory",
            HardwarePartKind.Motherboard => "Motherboard",
            HardwarePartKind.Storage => "Drive",
            HardwarePartKind.Network => "Network",
            HardwarePartKind.Battery => "Battery",
            HardwarePartKind.Cooler => "Cooler",
            HardwarePartKind.Psu => "Power supply",
            HardwarePartKind.Pc => "Overview",
            _ => "Other",
        };

        /// <summary>"hardware-sensors 2026-10-09 14-32-05.txt": dated, so a saved report never replaces another.</summary>
        public static string ReportFileName(DateTime now) =>
            $"hardware-sensors {now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}.txt";

        // =================================================================
        //  Settings (%AppData%\Smart Tools\hardware_sensors.json - no paths)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "hardware_sensors.json");

        public static HardwareSensorsSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new HardwareSensorsSettings();
                return Sanitize(JsonSerializer.Deserialize<HardwareSensorsSettings>(File.ReadAllText(file), SettingsJson) ?? new HardwareSensorsSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new HardwareSensorsSettings();
            }
        }

        public static HardwareSensorsSettings Sanitize(HardwareSensorsSettings s)
        {
            s.RefreshMs = RefreshChoicesMs.OrderBy(c => Math.Abs(c - s.RefreshMs)).First();
            if (!Enum.IsDefined(s.Part)) s.Part = HardwarePartKind.Cpu;
            return s;
        }

        public static void SaveSettings(HardwareSensorsSettings s, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(Sanitize(s), SettingsJson));
        }
    }
}
