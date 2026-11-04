using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// System Monitor - the pure part: rates, CPU %, units, warning levels,
    /// Windows' GPU and disk counters, the sections the page shows, the
    /// report and settings. No Windows APIs, no WPF - unit-tested. Reading
    /// the PC is in SystemMonitorManager.cs and SystemMonitorManager.Dxgi.cs.
    /// </summary>
    public sealed partial class SystemMonitorManager
    {
        public static readonly int[] RefreshChoicesMs = { 500, 1000, 2000, 5000 };
        public const int HistoryLength = 60;

        // =================================================================
        //  Rates and percentages
        // =================================================================

        /// <summary>
        /// CPU busy % between two GetSystemTimes samples. Kernel time includes
        /// idle time, so busy = kernel + user - idle. Null when no time passed.
        /// </summary>
        public static double? CpuPercent(ulong idle0, ulong kernel0, ulong user0, ulong idle1, ulong kernel1, ulong user1)
        {
            if (idle1 < idle0 || kernel1 < kernel0 || user1 < user0) return null;   // counters reset
            double idle = idle1 - idle0;
            double total = (kernel1 - kernel0) + (double)(user1 - user0);
            if (total <= 0) return null;
            return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
        }

        /// <summary>Bytes per second from two byte counters. A counter that went down (adapter reset, wrap) counts as 0, not a huge negative.</summary>
        public static double ByteRate(long before, long after, double seconds)
        {
            if (seconds <= 0 || after < before) return 0;
            return (after - before) / seconds;
        }

        /// <summary>
        /// The CPU's speed now, the way Task Manager works it out: the base clock
        /// times Windows' "% Processor Performance" (over 100 with turbo).
        /// </summary>
        public static double? CpuSpeedMhz(double? baseMhz, double? performancePercent) =>
            baseMhz is double b && b > 0 && performancePercent is double p && p > 0 ? b * p / 100 : null;

        // =================================================================
        //  Formatting
        // =================================================================

        private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;
        private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>"512 B", "1.5 KB", "12.4 GB" (binary units, like Task Manager).</summary>
        public static string FormatBytes(double bytes)
        {
            double v = Math.Max(0, bytes);
            int u = 0;
            while (v >= 1024 && u < ByteUnits.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return u == 0 ? $"{v:0} B" : v.ToString(v < 10 ? "0.0" : "0", Culture) + " " + ByteUnits[u];
        }

        /// <summary>"12.4 / 31.8 GB": both in the total's unit, so they read as one figure.</summary>
        public static string FormatBytesPair(double used, double total)
        {
            double t = Math.Max(0, total);
            int u = 0;
            while (t >= 1024 && u < ByteUnits.Length - 1)
            {
                t /= 1024;
                u++;
            }
            double scale = Math.Pow(1024, u);
            string f = u == 0 ? "0" : "0.0";
            return $"{(Math.Max(0, used) / scale).ToString(f, Culture)} / {t.ToString(f, Culture)} {ByteUnits[u]}";
        }

        /// <summary>Disk speed: "0 B/s", "85 MB/s".</summary>
        public static string FormatByteRate(double bytesPerSec) => FormatBytes(bytesPerSec) + "/s";

        /// <summary>Network speed in bits, as internet plans are sold: "0.0 Kbps", "940 Mbps", "1.2 Gbps".</summary>
        public static string FormatBitRate(double bytesPerSec)
        {
            double bits = Math.Max(0, bytesPerSec) * 8;
            if (bits >= 1e9) return (bits / 1e9).ToString(bits < 1e10 ? "0.0" : "0", Culture) + " Gbps";
            if (bits >= 1e6) return (bits / 1e6).ToString(bits < 1e7 ? "0.0" : "0", Culture) + " Mbps";
            return (bits / 1e3).ToString(bits < 1e4 ? "0.0" : "0", Culture) + " Kbps";
        }

        public static string FormatPercent(double? percent) => percent is double p ? $"{p:0}%" : "–";

        /// <summary>"3 days 4 h 12 min", "4 h 12 min", "12 min".</summary>
        public static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays} {((int)t.TotalDays == 1 ? "day" : "days")} {t.Hours} h {t.Minutes} min";
            if (t.TotalHours >= 1) return $"{t.Hours} h {t.Minutes} min";
            return $"{Math.Max(0, t.Minutes)} min";
        }

        /// <summary>"4.20 GHz" or "800 MHz".</summary>
        public static string FormatClock(double? mhz) =>
            mhz is double m ? (m >= 1000 ? (m / 1000).ToString("0.00", Culture) + " GHz" : $"{m:0} MHz") : "–";

        /// <summary>Disk response time: "0.4 ms", "12 ms".</summary>
        public static string FormatMs(double? ms) =>
            ms is double m && m >= 0 ? m.ToString(m < 10 ? "0.0" : "0", Culture) + " ms" : "–";

        /// <summary>"54°C", or "–" when no sensor was found.</summary>
        public static string FormatTemp(double? celsius) => celsius is double c ? $"{c:0}°C" : "–";

        // =================================================================
        //  Warning levels
        // =================================================================

        /// <summary>Usage: amber from 80%, red from 95%.</summary>
        public static SystemMonitorLevel UsageLevel(double? percent) => percent switch
        {
            null => SystemMonitorLevel.Unknown,
            >= 95 => SystemMonitorLevel.Hot,
            >= 80 => SystemMonitorLevel.Warm,
            _ => SystemMonitorLevel.Normal,
        };

        public const double CpuWarmC = 75, CpuHotC = 90;
        public const double GpuWarmC = 80, GpuHotC = 90;
        public const double DiskWarmC = 50, DiskHotC = 65;

        /// <summary>Temperature: amber from <paramref name="warm"/>, red from <paramref name="hot"/>. Unknown when there's no sensor for it.</summary>
        public static SystemMonitorLevel TempLevel(double? celsius, double warm, double hot) => celsius switch
        {
            null => SystemMonitorLevel.Unknown,
            double c when c >= hot => SystemMonitorLevel.Hot,
            double c when c >= warm => SystemMonitorLevel.Warm,
            _ => SystemMonitorLevel.Normal,
        };

        /// <summary>The more worrying of two levels (Unknown never outweighs a known reading).</summary>
        public static SystemMonitorLevel CombineLevel(SystemMonitorLevel a, SystemMonitorLevel b) =>
            (SystemMonitorLevel)Math.Max((int)a, (int)b);

        /// <summary>
        /// Matches a LibreHardwareMonitor hardware name to one of ours (DXGI's
        /// GPU name, WMI's disk model): names from different sources rarely
        /// match exactly, so either containing the other is close enough.
        /// </summary>
        public static double? MatchTemp(IReadOnlyDictionary<string, double> byName, string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            foreach (var (name, value) in byName)
                if (name.Contains(target, StringComparison.OrdinalIgnoreCase) || target.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return value;
            return null;
        }

        // =================================================================
        //  Windows' GPU counters
        // =================================================================

        /// <summary>"pid_1234_luid_0x00000000_0x0000C3A1_phys_0_eng_3_engtype_3D" → ("0x00000000_0x0000C3A1", "3D").</summary>
        public static (string Luid, string EngineType)? ParseGpuEngineInstance(string instance)
        {
            int l = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int p = instance.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
            int e = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            if (l < 0 || p < 0 || e < 0 || p < l) return null;
            string luid = instance[(l + 5)..p];
            string type = instance[(e + 8)..];
            return luid.Length == 0 ? null : (luid, type.Length == 0 ? "Other" : type);
        }

        /// <summary>"luid_0x00000000_0x0000C3A1_phys_0" (GPU Adapter Memory instance) → "0x00000000_0x0000C3A1".</summary>
        public static string? LuidOfAdapterInstance(string instance)
        {
            int l = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (l < 0) return null;
            int p = instance.IndexOf("_phys_", l, StringComparison.OrdinalIgnoreCase);
            return p < 0 ? instance[(l + 5)..] : instance[(l + 5)..p];
        }

        /// <summary>A DirectX adapter id written the way Windows' counters write it: "0x{high}_0x{low}".</summary>
        public static string FormatLuid(uint lowPart, int highPart) => $"0x{(uint)highPart:X8}_0x{lowPart:X8}";

        /// <summary>"VideoDecode" → "Video Decode", "Compute_0" → "Compute 0", "3D" stays.</summary>
        public static string EngineDisplayName(string type)
        {
            var sb = new StringBuilder();
            string t = type.Replace('_', ' ').Trim();
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(t[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.Length == 0 ? "Other" : sb.ToString();
        }

        private static readonly string[] StandardEngines = { "3D", "Copy", "Video Encode", "Video Decode" };

        /// <summary>
        /// One entry per graphics card, matched to Windows' counters by the
        /// card's adapter id (LUID), so every card keeps its own name - idle
        /// ones included. Load is the busiest engine, as in Task Manager: each
        /// engine adds up every process's use, capped at 100.
        /// When DirectX gave no list, the cards come from the counters, named
        /// from <paramref name="fallbackNames"/> only when the counts match.
        /// </summary>
        public static List<SystemMonitorGpu> BuildGpus(
            IReadOnlyList<SystemMonitorGpuAdapter> adapters,
            IEnumerable<(string Instance, double Value)> engineCounters,
            IEnumerable<(string Instance, double Value)> dedicatedCounters,
            IEnumerable<(string Instance, double Value)> sharedCounters,
            IReadOnlyList<string>? fallbackNames = null)
        {
            var engines = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instance, value) in engineCounters)
            {
                if (ParseGpuEngineInstance(instance) is not { } key || double.IsNaN(value)) continue;
                if (!engines.TryGetValue(key.Luid, out var perType)) engines[key.Luid] = perType = new(StringComparer.OrdinalIgnoreCase);
                perType[key.EngineType] = perType.GetValueOrDefault(key.EngineType) + Math.Max(0, value);
            }
            Dictionary<string, double> SumByLuid(IEnumerable<(string Instance, double Value)> counters)
            {
                var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var (instance, value) in counters)
                    if (LuidOfAdapterInstance(instance) is string luid && !double.IsNaN(value)) map[luid] = map.GetValueOrDefault(luid) + Math.Max(0, value);
                return map;
            }
            var dedicated = SumByLuid(dedicatedCounters);
            var shared = SumByLuid(sharedCounters);

            var cards = adapters.GroupBy(a => a.Luid, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (cards.Count == 0)
            {
                var luids = engines.Keys.Union(dedicated.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < luids.Count; i++)
                {
                    string name = fallbackNames != null && fallbackNames.Count == luids.Count ? fallbackNames[i] : "Graphics card";
                    cards.Add(new SystemMonitorGpuAdapter(luids[i], name, 0, 0));
                }
            }

            var list = new List<SystemMonitorGpu>();
            foreach (var card in cards)
            {
                var perType = engines.GetValueOrDefault(card.Luid) ?? new Dictionary<string, double>();
                var engineList = perType.Select(kv => new SystemMonitorEngine(EngineDisplayName(kv.Key), Math.Min(100, kv.Value))).ToList();
                double load = engineList.Count == 0 ? 0 : engineList.Max(e => e.Percent);
                list.Add(new SystemMonitorGpu(
                    card.Luid,
                    card.Name,
                    load,
                    engineList,
                    dedicated.TryGetValue(card.Luid, out var d) ? (long)d : null,
                    card.DedicatedBytes > 0 ? card.DedicatedBytes : null,
                    shared.TryGetValue(card.Luid, out var s) ? (long)s : null,
                    card.SharedBytes > 0 ? card.SharedBytes : null));
            }
            return list;
        }

        /// <summary>The engines shown under a GPU's graph: Task Manager's four (3D, Copy, Video Encode, Video Decode), plus any other while it's busy.</summary>
        public static List<SystemMonitorEngine> PickEngines(IEnumerable<SystemMonitorEngine> all)
        {
            var list = all.ToList();
            var picked = StandardEngines.Select(n => list.FirstOrDefault(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).OfType<SystemMonitorEngine>().ToList();
            picked.AddRange(list.Where(e => !StandardEngines.Contains(e.Name, StringComparer.OrdinalIgnoreCase) && e.Percent >= 1)
                                .OrderByDescending(e => e.Percent).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
            return picked;
        }

        // =================================================================
        //  Windows' disk counters
        // =================================================================

        /// <summary>PhysicalDisk instance "0 C: D:" → (0, "C: D:"); "_Total" or junk → null.</summary>
        public static (int Number, string Letters)? ParseDiskInstance(string instance)
        {
            if (string.IsNullOrWhiteSpace(instance) || instance.StartsWith('_')) return null;
            var parts = instance.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
            return (n, parts.Length == 2 ? parts[1].Trim() : "");
        }

        /// <summary>"Disk 0 (C: D:)", or "Disk 1" for a drive with no letter.</summary>
        /// <summary>Long adapter names ("Conexión de área local* 12") differ only at the end, which the list's ellipsis would cut off - keep that tail.</summary>
        public static string AdapterTitle(string name)
        {
            const int Max = 18;
            if (name.Length <= Max) return name;
            var tail = System.Text.RegularExpressions.Regex.Match(name, @"\*?\s*\d+$").Value;
            if (tail.Length == 0 || tail.Length > 6) return name;
            string head = name[..(Max - tail.Length - 1)].TrimEnd(' ', '*');
            return head + "… " + tail.Trim();
        }

        public static string DiskTitle(int number, string letters) => letters.Length > 0 ? $"Disk {number} ({letters})" : $"Disk {number}";

        /// <summary>
        /// One entry per drive, in drive-number order ("Disk 2" after "Disk 10"
        /// would be wrong), from Windows' counters: busy time is 100 - idle;
        /// response time comes in seconds per transfer.
        /// </summary>
        public static List<SystemMonitorDisk> BuildDisks(
            IEnumerable<(string Instance, double Value)> idle,
            IEnumerable<(string Instance, double Value)> read,
            IEnumerable<(string Instance, double Value)> write,
            IEnumerable<(string Instance, double Value)> secondsPerTransfer)
        {
            var reads = read.GroupBy(x => x.Instance).ToDictionary(g => g.Key, g => g.First().Value);
            var writes = write.GroupBy(x => x.Instance).ToDictionary(g => g.Key, g => g.First().Value);
            var response = secondsPerTransfer.GroupBy(x => x.Instance).ToDictionary(g => g.Key, g => g.First().Value);
            var list = new List<SystemMonitorDisk>();
            foreach (var (instance, idleValue) in idle)
            {
                if (ParseDiskInstance(instance) is not { } disk || list.Any(d => d.Number == disk.Number)) continue;
                list.Add(new SystemMonitorDisk(
                    disk.Number,
                    disk.Letters,
                    Math.Max(0, reads.GetValueOrDefault(instance)),
                    Math.Max(0, writes.GetValueOrDefault(instance)),
                    Math.Clamp(100 - idleValue, 0, 100),
                    response.TryGetValue(instance, out double sec) && sec >= 0 ? sec * 1000 : null));
            }
            return list.OrderBy(d => d.Number).ToList();
        }

        // =================================================================
        //  Network
        // =================================================================

        /// <summary>NetworkInterfaceType name → what people call it.</summary>
        public static string AdapterKind(string networkInterfaceType) => networkInterfaceType switch
        {
            "Wireless80211" => "Wi-Fi",
            "Ethernet" or "GigabitEthernet" or "FastEthernetT" or "FastEthernetFx" or "Ethernet3Megabit" => "Ethernet",
            "Wwanpp" or "Wwanpp2" => "Cellular",
            "Ppp" => "VPN / dial-up",
            _ => "Network",
        };

        // =================================================================
        //  Specs
        // =================================================================

        private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
        {
            "To Be Filled By O.E.M.", "To be filled by O.E.M.", "Default string", "System Product Name", "System manufacturer",
            "System Version", "Not Applicable", "None", "O.E.M.", "OEM", "Not Specified", "Unknown", "0", "Base Board Serial Number",
        };

        /// <summary>A WMI value worth showing, or null for blanks and the "To Be Filled By O.E.M." kind of placeholders.</summary>
        public static string? CleanSpecValue(object? value)
        {
            string? s = value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));   // collapse padding spaces
            return PlaceholderValues.Contains(s) ? null : s;
        }

        /// <summary>SMBIOS memory type code → "DDR4", "DDR5"...</summary>
        public static string? MemoryTypeName(int smbiosType) => smbiosType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            27 => "LPDDR",
            28 => "LPDDR2",
            29 => "LPDDR3",
            30 => "LPDDR4",
            34 => "DDR5",
            35 => "LPDDR5",
            _ => null,
        };

        /// <summary>WMI memory form factor → "DIMM" (desktop) / "SODIMM" (laptop).</summary>
        public static string? MemoryFormFactorName(int formFactor) => formFactor switch
        {
            8 => "DIMM",
            12 => "SODIMM",
            13 => "SRIMM",
            9 => "RIMM",
            _ => null,
        };

        /// <summary>"SSD (NVMe)", "HDD (SATA)", "SSD", "USB" or "".</summary>
        public static string DriveTypeText(SystemMonitorDiskInfo? info)
        {
            if (info == null) return "";
            if (info.Media != null && info.Bus != null) return $"{info.Media} ({info.Bus})";
            return info.Media ?? info.Bus ?? "";
        }

        // =================================================================
        //  Sections: what the page shows, from a snapshot and the specs
        // =================================================================

        /// <summary>
        /// PC, CPU, Memory, then each drive (by number), each connected
        /// adapter (by name - the list doesn't jump around as traffic
        /// changes) and each graphics card. Ids stay the same between
        /// refreshes so the page can update in place.
        /// </summary>
        public static List<SystemMonitorSectionData> BuildSections(SystemMonitorSnapshot s, SystemMonitorSpecs specs)
        {
            var list = new List<SystemMonitorSectionData>();
            var info = new List<SystemMonitorStat>();
            void Info(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) info.Add(new SystemMonitorStat(label, value));
            }
            List<SystemMonitorStat> TakeInfo()
            {
                var copy = info.ToList();
                info.Clear();
                return copy;
            }
            string Count(int n) => n > 0 ? n.ToString("N0", Culture) : "–";

            // ---- PC ----
            var pcLive = new List<SystemMonitorStat>
            {
                new("Up time", FormatUptime(s.Uptime)),
                new("Processes", Count(s.ProcessCount)),
                new("Threads", Count(s.ThreadCount)),
                new("Handles", Count(s.HandleCount)),
            };
            if (s.Battery is { } b)
            {
                string state = b.Charging ? "charging" : b.OnAc ? "plugged in" : b.Remaining is TimeSpan left ? $"{FormatUptime(left)} left" : "on battery";
                pcLive.Add(new SystemMonitorStat("Battery", $"{b.Percent}% · {state}"));
            }
            string pcDevice = string.Join(" ", new[] { Find(specs.Pc, "Maker"), Find(specs.Pc, "Model") }.Where(x => x != null));
            list.Add(new SystemMonitorSectionData
            {
                Id = "pc",
                Kind = SystemMonitorSectionKind.Pc,
                Title = "PC",
                Device = pcDevice.Length > 0 ? pcDevice : Find(specs.Pc, "Name") ?? "",
                Summary = "Up " + FormatUptime(s.Uptime),
                Graph = SystemMonitorGraphKind.None,
                Level = SystemMonitorLevel.Normal,
                Live = pcLive,
                Info = specs.Pc,
            });

            // ---- CPU ----
            double? speed = CpuSpeedMhz(specs.CpuBaseMhz, s.CpuPerformancePercent);
            double? cpuTemp = s.Temps.CpuC;
            Info("Base speed", specs.CpuBaseMhz is double baseMhz ? FormatClock(baseMhz) : null);
            Info("Sockets", specs.CpuSockets > 0 ? specs.CpuSockets.ToString(Culture) : null);
            Info("Cores", specs.CpuCores > 0 ? specs.CpuCores.ToString(Culture) : null);
            int threads = specs.CpuThreads > 0 ? specs.CpuThreads : s.CorePercents.Count;
            Info("Logical processors", threads > 0 ? threads.ToString(Culture) : null);
            Info("Virtualization", specs.Virtualization switch { true => "Enabled", false => "Disabled", _ => null });
            Info("L2 cache", specs.L2CacheBytes is long l2 && l2 > 0 ? FormatBytes(l2) : null);
            Info("L3 cache", specs.L3CacheBytes is long l3 && l3 > 0 ? FormatBytes(l3) : null);
            list.Add(new SystemMonitorSectionData
            {
                Id = "cpu",
                Kind = SystemMonitorSectionKind.Cpu,
                Title = "CPU",
                Device = specs.CpuName ?? "",
                Summary = FormatPercent(s.CpuPercent) + (speed is double sp ? "  " + FormatClock(sp) : "") + (cpuTemp is double ? "  " + FormatTemp(cpuTemp) : ""),
                Graph = SystemMonitorGraphKind.Percent,
                GraphCaption = "% Utilization",
                Value = s.CpuPercent ?? 0,
                Level = CombineLevel(UsageLevel(s.CpuPercent), TempLevel(cpuTemp, CpuWarmC, CpuHotC)),
                Live = new List<SystemMonitorStat>
                {
                    new("Utilization", FormatPercent(s.CpuPercent)),
                    new("Speed", FormatClock(speed)),
                    new("Temperature", FormatTemp(cpuTemp)),
                    new("Processes", Count(s.ProcessCount)),
                    new("Threads", Count(s.ThreadCount)),
                    new("Handles", Count(s.HandleCount)),
                    new("Up time", FormatUptime(s.Uptime)),
                },
                Info = TakeInfo(),
                Cores = s.CorePercents,
                TemperatureC = cpuTemp,
            });

            // ---- Memory ----
            long installed = specs.InstalledMemoryBytes > 0 ? specs.InstalledMemoryBytes : s.MemoryTotalBytes;
            Info("Speed", specs.MemorySpeedMts > 0 ? $"{specs.MemorySpeedMts} MT/s" : null);
            Info("Slots used", specs.MemorySlotsTotal > 0 ? $"{specs.MemorySlotsUsed} of {specs.MemorySlotsTotal}" : null);
            Info("Form factor", specs.MemoryFormFactor);
            Info("Type", specs.MemoryType);
            long reserved = installed - s.MemoryTotalBytes;
            Info("Hardware reserved", specs.InstalledMemoryBytes > 0 && reserved > 0 ? FormatBytes(reserved) : null);
            var memLive = new List<SystemMonitorStat>
            {
                new("In use", FormatBytes(s.MemoryUsedBytes)),
                new("Available", FormatBytes(s.MemoryAvailableBytes)),
                new("Committed", FormatBytesPair(s.CommitUsedBytes, s.CommitLimitBytes)),
            };
            if (s.CachedBytes is long cached) memLive.Add(new SystemMonitorStat("Cached", FormatBytes(cached)));
            if (s.PagedPoolBytes is long paged) memLive.Add(new SystemMonitorStat("Paged pool", FormatBytes(paged)));
            if (s.NonPagedPoolBytes is long nonPaged) memLive.Add(new SystemMonitorStat("Non-paged pool", FormatBytes(nonPaged)));
            list.Add(new SystemMonitorSectionData
            {
                Id = "memory",
                Kind = SystemMonitorSectionKind.Memory,
                Title = "Memory",
                Device = (FormatBytes(installed) + " " + (specs.MemoryType ?? "")).Trim(),
                Summary = $"{FormatBytesPair(s.MemoryUsedBytes, s.MemoryTotalBytes)} ({s.MemoryPercent:0}%)",
                Graph = SystemMonitorGraphKind.Percent,
                GraphCaption = "Memory usage",
                Value = s.MemoryPercent,
                Level = UsageLevel(s.MemoryPercent),
                Live = memLive,
                Info = TakeInfo(),
            });

            // ---- Drives ----
            foreach (var d in s.Disks)
            {
                var di = specs.Disks.GetValueOrDefault(d.Number);
                string type = DriveTypeText(di);
                double? diskTemp = MatchTemp(s.Temps.DiskByName, di?.Model);
                var letters = d.Letters.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long formatted = letters.Sum(l => specs.VolumeSizes.GetValueOrDefault(l.ToUpperInvariant()));
                Info("Capacity", di?.SizeBytes > 0 ? FormatBytes(di.SizeBytes) : null);
                Info("Formatted", formatted > 0 ? FormatBytes(formatted) : null);
                Info("System disk", specs.SystemDrive == null ? null : letters.Contains(specs.SystemDrive, StringComparer.OrdinalIgnoreCase) ? "Yes" : "No");
                Info("Type", type);
                var diskLive = new List<SystemMonitorStat>
                {
                    new("Active time", FormatPercent(d.ActivePercent)),
                    new("Average response time", FormatMs(d.ResponseMs)),
                    new("Read speed", FormatByteRate(d.ReadBytesPerSec)),
                    new("Write speed", FormatByteRate(d.WriteBytesPerSec)),
                };
                if (diskTemp is double) diskLive.Add(new SystemMonitorStat("Temperature", FormatTemp(diskTemp)));
                list.Add(new SystemMonitorSectionData
                {
                    Id = $"disk{d.Number}",
                    Kind = SystemMonitorSectionKind.Disk,
                    Title = DiskTitle(d.Number, d.Letters),
                    Device = di?.Model ?? "",
                    Summary = (type.Length > 0 ? type + "  " : "") + $"{d.ActivePercent:0}%" + (diskTemp is double ? "  " + FormatTemp(diskTemp) : ""),
                    Graph = SystemMonitorGraphKind.Percent,
                    GraphCaption = "Active time",
                    Value = d.ActivePercent,
                    TransferRead = d.ReadBytesPerSec,
                    TransferWrite = d.WriteBytesPerSec,
                    Level = CombineLevel(UsageLevel(d.ActivePercent), TempLevel(diskTemp, DiskWarmC, DiskHotC)),
                    Live = diskLive,
                    Info = TakeInfo(),
                    TemperatureC = diskTemp,
                });
            }

            // ---- Network adapters ----
            foreach (var a in s.Adapters.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                Info("Connection type", a.Kind);
                Info("Link speed", a.SpeedBitsPerSec > 0 ? FormatBitRate(a.SpeedBitsPerSec / 8.0) : null);
                Info("IPv4 address", a.IPv4);
                Info("IPv6 address", a.IPv6);
                Info("DNS suffix", a.DnsSuffix);
                list.Add(new SystemMonitorSectionData
                {
                    Id = "net:" + a.Id,
                    Kind = SystemMonitorSectionKind.Network,
                    Title = AdapterTitle(a.Name),
                    Device = string.IsNullOrWhiteSpace(a.Description) || a.Description == a.Name ? a.Name : a.Name + " · " + a.Description,
                    Summary = $"S: {FormatBitRate(a.SendBytesPerSec)}  R: {FormatBitRate(a.ReceiveBytesPerSec)}",
                    Graph = SystemMonitorGraphKind.Rate,
                    GraphCaption = "Throughput",
                    Value = a.ReceiveBytesPerSec,
                    Value2 = a.SendBytesPerSec,
                    IsWireless = a.IsWireless,
                    Level = SystemMonitorLevel.Normal,
                    Live = new List<SystemMonitorStat>
                    {
                        new("Send", FormatBitRate(a.SendBytesPerSec)),
                        new("Receive", FormatBitRate(a.ReceiveBytesPerSec)),
                    },
                    Info = TakeInfo(),
                });
            }

            // ---- Graphics cards ----
            for (int i = 0; i < s.Gpus.Count; i++)
            {
                var g = s.Gpus[i];
                var gi = specs.Gpus.GetValueOrDefault(g.Name);
                double? gpuTemp = MatchTemp(s.Temps.GpuByName, g.Name);
                Info("Driver version", gi?.DriverVersion);
                Info("Driver date", gi?.DriverDate?.ToString("d", Culture));
                Info("Dedicated memory", g.DedicatedTotalBytes is long dt ? FormatBytes(dt) : null);
                Info("Shared memory", g.SharedTotalBytes is long st ? FormatBytes(st) : null);
                string Memory(long? used, long? total) =>
                    used is long u ? (total is long t && t > 0 ? FormatBytesPair(u, t) : FormatBytes(u)) : "–";
                var gpuLive = new List<SystemMonitorStat>
                {
                    new("Utilization", FormatPercent(g.LoadPercent)),
                    new("Dedicated memory", Memory(g.DedicatedUsedBytes, g.DedicatedTotalBytes)),
                    new("Shared memory", Memory(g.SharedUsedBytes, g.SharedTotalBytes)),
                };
                if (gpuTemp is double) gpuLive.Add(new SystemMonitorStat("Temperature", FormatTemp(gpuTemp)));
                list.Add(new SystemMonitorSectionData
                {
                    Id = "gpu:" + g.Luid,
                    Kind = SystemMonitorSectionKind.Gpu,
                    Title = $"GPU {i}",
                    Device = g.Name,
                    Summary = $"{g.LoadPercent:0}%" + (gpuTemp is double ? "  " + FormatTemp(gpuTemp) : ""),
                    Graph = SystemMonitorGraphKind.Percent,
                    GraphCaption = "% Utilization",
                    Value = g.LoadPercent,
                    Level = CombineLevel(UsageLevel(g.LoadPercent), TempLevel(gpuTemp, GpuWarmC, GpuHotC)),
                    Live = gpuLive,
                    Info = TakeInfo(),
                    Engines = PickEngines(g.Engines),
                    TemperatureC = gpuTemp,
                });
            }
            return list;
        }

        private static string? Find(IReadOnlyList<SystemMonitorStat> stats, string label) =>
            stats.FirstOrDefault(x => x.Label.Equals(label, StringComparison.OrdinalIgnoreCase))?.Value;

        // =================================================================
        //  Keeping the page's lists up to date in place
        // =================================================================

        /// <summary>
        /// Makes <paramref name="items"/> hold one item per id, in the order
        /// given: existing items are kept (and moved if needed), new ids get
        /// <paramref name="create"/>, items whose id is gone are removed. Keeping
        /// items means no flicker and the selection stays put.
        /// </summary>
        public static void SyncList<T>(System.Collections.ObjectModel.ObservableCollection<T> items, IReadOnlyList<string> ids, Func<T, string> idOf, Func<string, T> create)
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

        /// <summary>Label/value rows: values updated in place while the labels stay the same; rebuilt when they change.</summary>
        public static void SyncStats(System.Collections.ObjectModel.ObservableCollection<SystemMonitorStatRow> rows, IReadOnlyList<SystemMonitorStat> stats)
        {
            bool same = rows.Count == stats.Count && rows.Select(r => r.Label).SequenceEqual(stats.Select(s => s.Label));
            if (!same)
            {
                rows.Clear();
                foreach (var s in stats) rows.Add(new SystemMonitorStatRow { Label = s.Label, Value = s.Value });
                return;
            }
            for (int i = 0; i < stats.Count; i++) rows[i].Value = stats[i].Value;
        }

        /// <summary>Bars (logical processors, engines): updated in place while the labels stay the same; rebuilt when they change.</summary>
        public static void SyncBars(System.Collections.ObjectModel.ObservableCollection<SystemMonitorBar> bars, IReadOnlyList<(string Label, string? Caption, double Percent)> values)
        {
            bool same = bars.Count == values.Count && bars.Select(b => b.Label).SequenceEqual(values.Select(v => v.Label));
            if (!same)
            {
                bars.Clear();
                foreach (var v in values) bars.Add(new SystemMonitorBar { Label = v.Label, Caption = v.Caption });
            }
            for (int i = 0; i < values.Count; i++)
            {
                bars[i].Percent = values[i].Percent;
                bars[i].Level = UsageLevel(values[i].Percent);
            }
        }

        // =================================================================
        //  Report
        // =================================================================

        private static readonly string[] PrivateLabels = { "IPv4 address", "IPv6 address", "DNS suffix" };

        /// <summary>
        /// Every section as text (Copy and Save report): live figures at that
        /// moment and the fixed facts. Addresses are left out, since the report
        /// is meant for forum posts and support chats.
        /// </summary>
        public static string BuildReport(IReadOnlyList<SystemMonitorSectionData> sections, DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"System report - {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
            sb.AppendLine(new string('=', 40));
            foreach (var section in sections)
            {
                sb.AppendLine();
                sb.AppendLine(section.Device.Length > 0 ? $"{section.Title} - {section.Device}" : section.Title);
                var rows = section.Live.Concat(section.Info).Where(r => !PrivateLabels.Contains(r.Label)).ToList();
                if (rows.Count == 0) continue;
                int width = rows.Max(r => r.Label.Length);
                foreach (var r in rows) sb.AppendLine($"  {r.Label.PadRight(width)}  {r.Value}");
            }
            return sb.ToString();
        }

        /// <summary>"system-report 2026-10-07 14-32-05.txt": dated, so a saved report never replaces another.</summary>
        public static string ReportFileName(DateTime now) =>
            $"system-report {now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}.txt";

        // =================================================================
        //  History for the graphs
        // =================================================================

        /// <summary>The last <see cref="HistoryLength"/> values, oldest first.</summary>
        public sealed class History
        {
            private readonly double[] _values;
            private int _start;

            public History(int capacity = HistoryLength) => _values = new double[Math.Max(2, capacity)];

            public int Count { get; private set; }
            public int Capacity => _values.Length;

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

            public void Clear()
            {
                Count = 0;
                _start = 0;
            }

            public IReadOnlyList<double> Values()
            {
                var list = new double[Count];
                for (int i = 0; i < Count; i++) list[i] = _values[(_start + i) % _values.Length];
                return list;
            }

            public double Max() => Count == 0 ? 0 : Values().Max();
        }

        /// <summary>The top of a rate graph's scale as text (the graph leaves 15% headroom above the highest value).</summary>
        public static string RateScaleText(double maxBytesPerSec, bool bits) =>
            bits ? FormatBitRate(maxBytesPerSec * 1.15) : FormatByteRate(maxBytesPerSec * 1.15);

        // =================================================================
        //  Settings (%AppData%\Smart Tools\system_monitor.json - no paths)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "system_monitor.json");

        public static SystemMonitorSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new SystemMonitorSettings();
                return Sanitize(JsonSerializer.Deserialize<SystemMonitorSettings>(File.ReadAllText(file), SettingsJson) ?? new SystemMonitorSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new SystemMonitorSettings();
            }
        }

        public static SystemMonitorSettings Sanitize(SystemMonitorSettings s)
        {
            s.RefreshMs = RefreshChoicesMs.OrderBy(c => Math.Abs(c - s.RefreshMs)).First();
            if (!Enum.IsDefined(s.Section)) s.Section = SystemMonitorSectionKind.Pc;
            return s;
        }

        public static void SaveSettings(SystemMonitorSettings s, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(Sanitize(s), SettingsJson));
        }
    }
}
