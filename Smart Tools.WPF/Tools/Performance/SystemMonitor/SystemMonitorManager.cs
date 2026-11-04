using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// System Monitor: reads the PC on a background loop and raises one
    /// <see cref="SystemMonitorSnapshot"/> per refresh on the UI thread.
    /// Only what Windows itself offers, like Task Manager: performance
    /// counters (CPU, GPU engines, disks), kernel32 (memory, processes,
    /// battery), .NET (network) and DirectX (graphics cards). Fixed facts
    /// (names, sizes, versions) come from WMI once, off the UI thread.
    /// No extra packages beyond System.Management, no driver, no admin rights.
    /// </summary>
    public sealed partial class SystemMonitorManager : IDisposable
    {
        private readonly SynchronizationContext? _ui;
        private CancellationTokenSource? _loopCts;
        private volatile int _intervalMs = 1000;
        private volatile bool _disposed;

        public SystemMonitorManager()
        {
            _ui = SynchronizationContext.Current;
        }

        /// <summary>A new reading (UI thread).</summary>
        public event Action<SystemMonitorSnapshot>? SnapshotReady;

        public bool IsRunning => _loopCts != null;

        public int IntervalMs
        {
            get => _intervalMs;
            set => _intervalMs = Math.Clamp(value, 250, 60_000);
        }

        /// <summary>Starts reading (no-op if already running).</summary>
        public void Start()
        {
            if (_disposed || _loopCts != null) return;
            var cts = new CancellationTokenSource();
            _loopCts = cts;
            _ = Task.Factory.StartNew(() => LoopAsync(cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        /// <summary>Stops reading; returns at once.</summary>
        public void Stop()
        {
            var cts = _loopCts;
            _loopCts = null;
            cts?.Cancel();
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }

        private void Post(Action action)
        {
            if (_ui == null) action();
            else _ui.Post(_ => { if (!_disposed) action(); }, null);
        }

        private async Task LoopAsync(CancellationToken token)
        {
            using var pdh = new PdhCounters();
            using var temperatures = new TemperatureReader();
            temperatures.Open();
            var state = new ReadState { GpuAdapters = ReadGpuAdapters() };
            var clock = Stopwatch.StartNew();
            long lastTempMs = -10_000;
            var lastTemps = SystemMonitorTemps.Empty;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var started = Stopwatch.StartNew();
                    SystemMonitorSnapshot? snapshot = null;
                    try
                    {
                        // Temperatures come from the sensor library, whose update touches all the hardware: at most every 2 s, not every 0.5 s tick.
                        if (clock.ElapsedMilliseconds - lastTempMs >= 2_000)
                        {
                            lastTemps = temperatures.Read();
                            lastTempMs = clock.ElapsedMilliseconds;
                        }
                        snapshot = Read(pdh, state, lastTemps);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Info($"System Monitor: read failed: {ex.Message}");
                    }
                    if (snapshot != null && !token.IsCancellationRequested) Post(() => SnapshotReady?.Invoke(snapshot));
                    int wait = Math.Max(50, _intervalMs - (int)started.ElapsedMilliseconds);
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        }

        // =================================================================
        //  One reading
        // =================================================================

        /// <summary>What the previous reading needs to keep (counters are differences).</summary>
        private sealed class ReadState
        {
            public (ulong Idle, ulong Kernel, ulong User)? CpuTimes;
            public readonly Dictionary<string, (long Rx, long Tx)> NetBytes = new();
            public DateTime NetTimeUtc;
            public List<SystemMonitorGpuAdapter> GpuAdapters = new();
            public List<string>? GpuNames;
        }

        private SystemMonitorSnapshot Read(PdhCounters pdh, ReadState state, SystemMonitorTemps temps)
        {
            pdh.Collect();

            // CPU: Task Manager's "% Processor Utility" when Windows has it, else GetSystemTimes.
            var utility = pdh.Read(PdhCounters.CpuUtility);
            int totalAt = utility.FindIndex(u => u.Instance == "_Total");
            double? cpu = totalAt >= 0 ? Math.Clamp(utility[totalAt].Value, 0, 100) : null;
            if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                var now = (ToUlong(idleFt), ToUlong(kernelFt), ToUlong(userFt));
                if (cpu == null && state.CpuTimes is { } before)
                    cpu = CpuPercent(before.Idle, before.Kernel, before.User, now.Item1, now.Item2, now.Item3);
                state.CpuTimes = now;
            }
            var cores = utility.Where(u => !u.Instance.Contains("_Total", StringComparison.Ordinal))
                               .OrderBy(u => CoreSortKey(u.Instance))
                               .Select(u => Math.Clamp(u.Value, 0, 100))
                               .ToList();

            // Memory, processes, threads, handles: one kernel32 call.
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref mem);
            var perf = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
            bool havePerf = GetPerformanceInfo(ref perf, perf.cb);
            long page = havePerf ? (long)perf.PageSize : 4096;

            // Task Manager's "Cached": the standby lists plus modified pages.
            double? standby = Sum(pdh.One(PdhCounters.StandbyNormal), pdh.One(PdhCounters.StandbyReserve), pdh.One(PdhCounters.StandbyCore));
            double? modified = pdh.One(PdhCounters.Modified);
            long? cached = standby is double sb ? (long)(sb + (modified ?? 0)) : null;

            // Graphics cards: matched to the counters by adapter id.
            if (state.GpuAdapters.Count == 0) state.GpuNames ??= ReadVideoControllerNames();
            var gpus = BuildGpus(state.GpuAdapters, pdh.Read(PdhCounters.GpuEngine), pdh.Read(PdhCounters.GpuDedicated), pdh.Read(PdhCounters.GpuShared), state.GpuNames);

            return new SystemMonitorSnapshot
            {
                TakenUtc = DateTime.UtcNow,
                CpuPercent = cpu,
                CorePercents = cores,
                CpuPerformancePercent = pdh.One(PdhCounters.CpuPerformance),
                ProcessCount = havePerf ? (int)perf.ProcessCount : (int)(pdh.One(PdhCounters.Processes) ?? 0),
                ThreadCount = havePerf ? (int)perf.ThreadCount : 0,
                HandleCount = havePerf ? (int)perf.HandleCount : 0,
                Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
                Battery = ReadBattery(),
                MemoryTotalBytes = (long)mem.ullTotalPhys,
                MemoryAvailableBytes = (long)mem.ullAvailPhys,
                CommitUsedBytes = havePerf ? (long)perf.CommitTotal * page : (long)(mem.ullTotalPageFile - mem.ullAvailPageFile),
                CommitLimitBytes = havePerf ? (long)perf.CommitLimit * page : (long)mem.ullTotalPageFile,
                CachedBytes = cached,
                PagedPoolBytes = havePerf ? (long)perf.KernelPaged * page : null,
                NonPagedPoolBytes = havePerf ? (long)perf.KernelNonpaged * page : null,
                Disks = BuildDisks(pdh.Read(PdhCounters.DiskIdle), pdh.Read(PdhCounters.DiskRead), pdh.Read(PdhCounters.DiskWrite), pdh.Read(PdhCounters.DiskResponse)),
                Adapters = ReadAdapters(state),
                Gpus = gpus,
                Temps = temps,
            };
        }

        private static double? Sum(params double?[] values) =>
            values.All(v => v == null) ? null : values.Sum(v => v ?? 0);

        private static ulong ToUlong(FILETIME ft) => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

        /// <summary>"0,12" → 12 (group 0); keeps cores in order across processor groups.</summary>
        private static int CoreSortKey(string instance)
        {
            var parts = instance.Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int c) ? g * 1000 + c : int.MaxValue;
        }

        private static List<SystemMonitorAdapter> ReadAdapters(ReadState state)
        {
            var now = DateTime.UtcNow;
            double seconds = state.NetTimeUtc == default ? 0 : (now - state.NetTimeUtc).TotalSeconds;
            state.NetTimeUtc = now;
            var list = new List<SystemMonitorAdapter>();
            NetworkInterface[] nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                return list;
            }
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    long rx = stats.BytesReceived, tx = stats.BytesSent;
                    double down = 0, up = 0;
                    if (state.NetBytes.TryGetValue(nic.Id, out var before))
                    {
                        down = ByteRate(before.Rx, rx, seconds);
                        up = ByteRate(before.Tx, tx, seconds);
                    }
                    state.NetBytes[nic.Id] = (rx, tx);

                    var ip = nic.GetIPProperties();
                    var addresses = ip.UnicastAddresses.Select(a => a.Address).ToList();
                    string? ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
                    var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
                    string? ipv6 = (v6.FirstOrDefault(a => !a.IsIPv6LinkLocal) ?? v6.FirstOrDefault())?.ToString();
                    list.Add(new SystemMonitorAdapter(
                        nic.Id,
                        nic.Name,
                        nic.Description,
                        AdapterKind(nic.NetworkInterfaceType.ToString()),
                        down,
                        up,
                        Math.Max(0, nic.Speed),
                        ipv4,
                        ipv6,
                        string.IsNullOrWhiteSpace(ip.DnsSuffix) ? null : ip.DnsSuffix));
                }
                catch (NetworkInformationException)
                {
                    // an adapter that went away mid-read
                }
            }
            return list;
        }

        private static SystemMonitorBattery? ReadBattery()
        {
            if (!GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255) return null;
            TimeSpan? left = s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
            return new SystemMonitorBattery(s.BatteryLifePercent, (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1, left);
        }

        // =================================================================
        //  Specs (WMI, .NET) - once, off the UI thread
        // =================================================================

        public static Task<SystemMonitorSpecs> ReadSpecsAsync() => Task.Run(ReadSpecs);

        private static SystemMonitorSpecs ReadSpecs()
        {
            // ---- PC: computer, Windows, motherboard ----
            var pc = new List<SystemMonitorStat>();
            void Pc(string label, object? value)
            {
                if (CleanSpecValue(value) is string v) pc.Add(new SystemMonitorStat(label, v));
            }
            Pc("Name", Environment.MachineName);
            bool? hypervisor = null;
            foreach (var cs in Query("SELECT Manufacturer, Model, HypervisorPresent FROM Win32_ComputerSystem"))
            {
                Pc("Maker", cs["Manufacturer"]);
                Pc("Model", cs["Model"]);
                if (cs["HypervisorPresent"] is bool hp) hypervisor = hp;
            }
            foreach (var os in Query("SELECT Caption, BuildNumber, OSArchitecture, InstallDate FROM Win32_OperatingSystem"))
            {
                object? display = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
                object? ubr = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
                Pc("Windows", os["Caption"]);
                Pc("Version", display);
                Pc("Build", $"{os["BuildNumber"]}{(ubr != null ? "." + ubr : "")}");
                Pc("System type", os["OSArchitecture"]);
                if (os["InstallDate"] is string installDate && TryWmiDate(installDate, out var when)) Pc("Installed", when.ToString("d"));
            }
            foreach (var b in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                Pc("Motherboard", $"{CleanSpecValue(b["Manufacturer"])} {CleanSpecValue(b["Product"])}".Trim());
            foreach (var bios in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            {
                string date = bios["ReleaseDate"] is string rd && TryWmiDate(rd, out var d) ? $" ({d:d})" : "";
                Pc("BIOS", $"{CleanSpecValue(bios["Manufacturer"])} {CleanSpecValue(bios["SMBIOSBIOSVersion"])}{date}".Trim());
            }

            // ---- Processor (one row per socket) ----
            string? cpuName = null;
            double? baseMhz = null;
            int sockets = 0, coreCount = 0, threadCount = 0;
            bool? firmwareVirtualization = null;
            long? l2 = null, l3 = null;
            foreach (var p in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled FROM Win32_Processor"))
            {
                sockets++;
                cpuName ??= CleanSpecValue(p["Name"]);
                if (p["NumberOfCores"] is uint c) coreCount += (int)c;
                if (p["NumberOfLogicalProcessors"] is uint t) threadCount += (int)t;
                if (p["MaxClockSpeed"] is uint mhz && mhz > 0) baseMhz ??= mhz;
                if (p["L2CacheSize"] is uint l2k && l2k > 0) l2 = (l2 ?? 0) + l2k * 1024L;
                if (p["L3CacheSize"] is uint l3k && l3k > 0) l3 = (l3 ?? 0) + l3k * 1024L;
                if (p["VirtualizationFirmwareEnabled"] is bool v) firmwareVirtualization = v;
            }
            // With Hyper-V running, the firmware flag reads false - but virtualization is plainly on.
            bool? virtualization = hypervisor == true ? true : firmwareVirtualization;

            // ---- Memory ----
            long installed = 0;
            int speed = 0, type = 0, slotsUsed = 0, slotsTotal = 0, formFactor = 0;
            foreach (var m in Query("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
            {
                slotsUsed++;
                if (m["Capacity"] is ulong cap) installed += (long)cap;
                int s = m["ConfiguredClockSpeed"] is uint cs2 && cs2 > 0 ? (int)cs2 : m["Speed"] is uint s1 ? (int)s1 : 0;
                if (s > 0) speed = speed == 0 ? s : Math.Min(speed, s);
                if (type == 0 && m["SMBIOSMemoryType"] is uint ty) type = (int)ty;
                if (formFactor == 0 && m["FormFactor"] is ushort ff) formFactor = ff;   // boxed UInt16: unbox first
            }
            foreach (var arr in Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                if (arr["MemoryDevices"] is ushort slots) slotsTotal += slots;

            // ---- Drives (by number, as in "Disk 0") ----
            var disks = new Dictionary<int, SystemMonitorDiskInfo>();
            foreach (var pd in Query("SELECT DeviceId, FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
            {
                if (!int.TryParse(pd["DeviceId"]?.ToString(), out int number)) continue;
                // WMI hands these back as boxed UInt16: unbox first, then compare.
                string? media = (pd["MediaType"] is ushort mt ? mt : 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
                string? bus = (pd["BusType"] is ushort bt ? bt : 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD", _ => null };
                disks[number] = new SystemMonitorDiskInfo(CleanSpecValue(pd["FriendlyName"]) ?? $"Disk {number}", media, bus, pd["Size"] is ulong sz ? (long)sz : 0);
            }
            if (disks.Count == 0)
                foreach (var dd in Query("SELECT Index, Model, Size, InterfaceType FROM Win32_DiskDrive"))
                    if (dd["Index"] is uint idx)
                        disks[(int)idx] = new SystemMonitorDiskInfo(CleanSpecValue(dd["Model"]) ?? $"Disk {idx}", null, CleanSpecValue(dd["InterfaceType"]), dd["Size"] is ulong s2 ? (long)s2 : 0);

            var volumes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable) volumes[drive.Name.TrimEnd('\\')] = drive.TotalSize;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a drive that went away
                }
            }
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');

            // ---- Graphics drivers (by card name) ----
            var gpus = new Dictionary<string, SystemMonitorGpuInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Query("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
            {
                if (CleanSpecValue(v["Name"]) is not string name) continue;
                DateTime? date = v["DriverDate"] is string dd && TryWmiDate(dd, out var when) ? when : null;
                gpus[name] = new SystemMonitorGpuInfo(CleanSpecValue(v["DriverVersion"]), date);
            }

            return new SystemMonitorSpecs
            {
                Pc = pc,
                CpuName = cpuName,
                CpuBaseMhz = baseMhz,
                CpuSockets = sockets,
                CpuCores = coreCount,
                CpuThreads = threadCount,
                Virtualization = virtualization,
                L2CacheBytes = l2,
                L3CacheBytes = l3,
                InstalledMemoryBytes = installed,
                MemoryType = MemoryTypeName(type),
                MemorySpeedMts = speed,
                MemorySlotsUsed = slotsUsed,
                MemorySlotsTotal = slotsTotal,
                MemoryFormFactor = MemoryFormFactorName(formFactor),
                Disks = disks,
                VolumeSizes = volumes,
                SystemDrive = string.IsNullOrEmpty(systemDrive) ? null : systemDrive,
                Gpus = gpus,
            };
        }

        private static List<string> ReadVideoControllerNames() =>
            Query("SELECT Name FROM Win32_VideoController").Select(v => CleanSpecValue(v["Name"])).OfType<string>().ToList();

        private static object? ReadRegistry(string path, string name)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                return key?.GetValue(name);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        private static bool TryWmiDate(string value, out DateTime date)
        {
            try
            {
                date = ManagementDateTimeConverter.ToDateTime(value);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException)
            {
                date = default;
                return false;
            }
        }

        /// <summary>A WMI query that never throws (a missing class or a broken WMI just gives nothing).</summary>
        private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
        {
            var list = new List<ManagementBaseObject>();
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, wql);
                using var results = searcher.Get();
                foreach (var item in results) list.Add(item);
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Info($"System Monitor: WMI '{wql}' failed: {ex.Message}");
            }
            return list;
        }

        // =================================================================
        //  Performance counters (pdh.dll) - part of Windows
        // =================================================================

        /// <summary>Wildcard performance counters read with PdhGetFormattedCounterArray. Rate counters need two collects.</summary>
        private sealed class PdhCounters : IDisposable
        {
            public const string CpuUtility = @"\Processor Information(*)\% Processor Utility";
            public const string CpuPerformance = @"\Processor Information(_Total)\% Processor Performance";
            public const string Processes = @"\System\Processes";
            public const string StandbyNormal = @"\Memory\Standby Cache Normal Priority Bytes";
            public const string StandbyReserve = @"\Memory\Standby Cache Reserve Bytes";
            public const string StandbyCore = @"\Memory\Standby Cache Core Bytes";
            public const string Modified = @"\Memory\Modified Page List Bytes";
            public const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
            public const string GpuDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
            public const string GpuShared = @"\GPU Adapter Memory(*)\Shared Usage";
            public const string DiskRead = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
            public const string DiskWrite = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
            public const string DiskIdle = @"\PhysicalDisk(*)\% Idle Time";
            public const string DiskResponse = @"\PhysicalDisk(*)\Avg. Disk sec/Transfer";

            private static readonly string[] All =
            {
                CpuUtility, CpuPerformance, Processes, StandbyNormal, StandbyReserve, StandbyCore, Modified,
                GpuEngine, GpuDedicated, GpuShared, DiskRead, DiskWrite, DiskIdle, DiskResponse,
            };

            private IntPtr _query;
            private readonly Dictionary<string, IntPtr> _counters = new();

            public PdhCounters()
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
                if (_query == IntPtr.Zero) return;
                foreach (string path in All)
                    if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out IntPtr counter) == 0) _counters[path] = counter;   // a counter this PC lacks is skipped
                PdhCollectQueryData(_query);
            }

            public void Collect()
            {
                if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
            }

            /// <summary>A single-instance counter's value, or null if this PC doesn't have it.</summary>
            public double? One(string path)
            {
                var values = Read(path);
                return values.Count == 0 ? null : values[0].Value;
            }

            /// <summary>Every instance's value (only instances whose value is valid this time).</summary>
            public List<(string Instance, double Value)> Read(string path)
            {
                var list = new List<(string, double)>();
                if (!_counters.TryGetValue(path, out IntPtr counter)) return list;
                uint size = 0;
                uint status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
                if (status != PDH_MORE_DATA || size == 0) return list;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer) != 0) return list;
                    int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                    for (int i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                        if (item.Value.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA) || !double.IsFinite(item.Value.DoubleValue)) continue;
                        list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                return list;
            }

            public void Dispose()
            {
                if (_query != IntPtr.Zero) PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }

        // =================================================================
        //  Native
        // =================================================================

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint PDH_CSTATUS_VALID_DATA = 0x0;
        private const uint PDH_CSTATUS_NEW_DATA = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double DoubleValue;   // union: aligned to 8, so the struct is 16 bytes
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr Name;
            public PDH_FMT_COUNTERVALUE Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public int dwLowDateTime;
            public int dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        /// <summary>Sizes are in pages (multiply by PageSize).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public nuint CommitTotal;
            public nuint CommitLimit;
            public nuint CommitPeak;
            public nuint PhysicalTotal;
            public nuint PhysicalAvailable;
            public nuint SystemCache;
            public nuint KernelTotal;
            public nuint KernelPaged;
            public nuint KernelNonpaged;
            public nuint PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
        private static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    }
}
