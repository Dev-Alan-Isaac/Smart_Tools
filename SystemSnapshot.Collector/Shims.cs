// Small stand-ins for the parts of Smart Tools that the linked System Snapshot code calls, so the collector needs none of the app.
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Xml.Linq;
using Microsoft.Win32;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF
{
    /// <summary>Logging is not needed in the collector; the scan already turns every failure into a table with a note.</summary>
    public static class Log
    {
        public static void Info(string message) { }
    }

    public static class AppPaths
    {
        public static string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "SystemSnapshot");
    }

    /// <summary>
    /// Same API as the app Loc, but the strings come from the app's own en.xaml / es.xaml (embedded in the exe),
    /// so the report is worded exactly as the tool words it. Only the Str_SysSnap_* and Str_Common_* keys are read.
    /// </summary>
    public static class Loc
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Languages = new Dictionary<string, Dictionary<string, string>>();
        private static Dictionary<string, string> _current = new Dictionary<string, string>();

        public static string Language { get; private set; } = "en";

        public static IReadOnlyList<string> Available { get; } = new[] { "en", "es" };

        public static void Use(string language)
        {
            if (!Available.Contains(language)) language = "en";
            Language = language;
            if (!Languages.TryGetValue(language, out var table)) Languages[language] = table = Load(language);
            _current = table;
        }

        private static Dictionary<string, string> Load(string language)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Language." + language + ".xaml");
            if (stream == null) return table;
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var element in XDocument.Load(stream).Root!.Elements())
            {
                string? key = (string?)element.Attribute(x + "Key");
                if (key != null && (key.StartsWith("Str_SysSnap_", StringComparison.Ordinal) || key.StartsWith("Str_Common_", StringComparison.Ordinal)))
                    table[key] = element.Value;
            }
            return table;
        }

        public static string T(string key, string fallback) => _current.TryGetValue(key, out var text) ? text : fallback;

        public static string F(string key, string fallback, params object?[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, T(key, fallback), args); }
            catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, fallback, args); }
        }
    }
}

namespace Smart_Tools.WPF.Models
{
    public sealed record SystemMonitorStat(string Label, string Value);
    public sealed record SystemMonitorDiskInfo(string Model, string? Media, string? Bus, long SizeBytes);
    public sealed record SystemMonitorGpuInfo(string? DriverVersion);

    public sealed class SystemMonitorSpecs
    {
        public List<SystemMonitorStat> Pc { get; set; } = new List<SystemMonitorStat>();
        public string? CpuName { get; set; }
        public int CpuCores { get; set; }
        public int CpuThreads { get; set; }
        public bool? Virtualization { get; set; }
        public long InstalledMemoryBytes { get; set; }
        public string MemoryType { get; set; } = "";
        public int MemorySpeedMts { get; set; }
        public Dictionary<int, SystemMonitorDiskInfo> Disks { get; set; } = new Dictionary<int, SystemMonitorDiskInfo>();
        public Dictionary<string, SystemMonitorGpuInfo> Gpus { get; set; } = new Dictionary<string, SystemMonitorGpuInfo>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record AdapterAddress(string Address, int Prefix);

    public sealed class AdapterInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Kind { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string Mac { get; set; } = "";
        public List<AdapterAddress> IPv4 { get; set; } = new List<AdapterAddress>();
        public List<string> IPv6 { get; set; } = new List<string>();
        public List<string> Gateways { get; set; } = new List<string>();
        public List<string> Dns { get; set; } = new List<string>();
        public bool DnsAutomatic { get; set; }
        public bool DhcpEnabled { get; set; }
        public string? DhcpServer { get; set; }
        public long SpeedBitsPerSecond { get; set; }
        public bool IsVirtual { get; set; }
    }
}

namespace Smart_Tools.WPF.Controllers
{
    using Smart_Tools.WPF.Models;

    /// <summary>What the snapshot reads from the system monitor: the PC, processor, memory, drives and graphics cards, through WMI.</summary>
    public static class SystemMonitorManager
    {
        public static Task<SystemMonitorSpecs> ReadSpecsAsync() => Task.Run(ReadSpecs);

        private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
        {
            var list = new List<ManagementBaseObject>();
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, wql);
                foreach (ManagementBaseObject o in searcher.Get()) list.Add(o);
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            return list;
        }

        private static string? Clean(object? value)
        {
            string? text = value?.ToString()?.Trim();
            return string.IsNullOrEmpty(text) || text.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) || text.Equals("Default string", StringComparison.OrdinalIgnoreCase) ? null : text;
        }

        private static long ToLong(object? v) => v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

        private static string MemoryTypeName(int code) => code switch
        {
            20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 29 => "LPDDR2", 30 => "LPDDR3", 35 => "LPDDR4", 36 => "LPDDR5", _ => "",
        };

        private static SystemMonitorSpecs ReadSpecs()
        {
            var specs = new SystemMonitorSpecs();
            void Pc(string label, object? value) { if (Clean(value) is string v) specs.Pc.Add(new SystemMonitorStat(label, v)); }

            Pc("Name", Environment.MachineName);
            bool? hypervisor = null;
            foreach (var cs in Query("SELECT Manufacturer, Model, HypervisorPresent FROM Win32_ComputerSystem"))
            {
                Pc("Maker", cs["Manufacturer"]);
                Pc("Model", cs["Model"]);
                if (cs["HypervisorPresent"] is bool hp) hypervisor = hp;
            }
            foreach (var os in Query("SELECT Caption, OSArchitecture FROM Win32_OperatingSystem"))
                Pc("System type", os["OSArchitecture"]);
            foreach (var b in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                Pc("Motherboard", ((Clean(b["Manufacturer"]) ?? "") + " " + (Clean(b["Product"]) ?? "")).Trim());
            foreach (var bios in Query("SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS"))
                Pc("BIOS", ((Clean(bios["Manufacturer"]) ?? "") + " " + (Clean(bios["SMBIOSBIOSVersion"]) ?? "")).Trim());

            bool? firmwareVirtualization = null;
            foreach (var p in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, VirtualizationFirmwareEnabled FROM Win32_Processor"))
            {
                specs.CpuName ??= Clean(p["Name"]);
                specs.CpuCores += (int)ToLong(p["NumberOfCores"]);
                specs.CpuThreads += (int)ToLong(p["NumberOfLogicalProcessors"]);
                if (p["VirtualizationFirmwareEnabled"] is bool v) firmwareVirtualization = v;
            }
            specs.Virtualization = hypervisor == true ? true : firmwareVirtualization;

            int speed = 0, type = 0;
            foreach (var m in Query("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                specs.InstalledMemoryBytes += ToLong(m["Capacity"]);
                int s = (int)(ToLong(m["ConfiguredClockSpeed"]) > 0 ? ToLong(m["ConfiguredClockSpeed"]) : ToLong(m["Speed"]));
                if (s > 0) speed = speed == 0 ? s : Math.Min(speed, s);
                if (type == 0) type = (int)ToLong(m["SMBIOSMemoryType"]);
            }
            specs.MemorySpeedMts = speed;
            specs.MemoryType = MemoryTypeName(type);

            // Windows 8 and later have MSFT_PhysicalDisk (SSD / NVMe known); Windows 7 only has Win32_DiskDrive.
            foreach (var pd in Query("SELECT DeviceId, FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
            {
                if (!int.TryParse(pd["DeviceId"]?.ToString(), out int number)) continue;
                string? media = ToLong(pd["MediaType"]) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
                string? bus = ToLong(pd["BusType"]) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD", _ => null };
                specs.Disks[number] = new SystemMonitorDiskInfo(Clean(pd["FriendlyName"]) ?? "Disk " + number, media, bus, ToLong(pd["Size"]));
            }
            if (specs.Disks.Count == 0)
            {
                foreach (var dd in Query("SELECT Index, Model, Size, InterfaceType FROM Win32_DiskDrive"))
                    specs.Disks[(int)ToLong(dd["Index"])] = new SystemMonitorDiskInfo(Clean(dd["Model"]) ?? "Disk", null, Clean(dd["InterfaceType"]), ToLong(dd["Size"]));
            }

            foreach (var v in Query("SELECT Name, DriverVersion FROM Win32_VideoController"))
                if (Clean(v["Name"]) is string name) specs.Gpus[name] = new SystemMonitorGpuInfo(Clean(v["DriverVersion"]));

            return specs;
        }
    }

    /// <summary>The network adapters with their addresses, from the .NET network classes (no extra permissions needed).</summary>
    public static class NetworkToolsManager
    {
        public static List<AdapterInfo> GetAdapters(bool includeAll)
        {
            var list = new List<AdapterInfo>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var info = new AdapterInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Kind = nic.NetworkInterfaceType.ToString(),
                    StatusText = nic.OperationalStatus == OperationalStatus.Up ? "Connected" : nic.OperationalStatus == OperationalStatus.Down ? "Disconnected" : nic.OperationalStatus.ToString(),
                    Mac = string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture))),
                    SpeedBitsPerSecond = nic.OperationalStatus == OperationalStatus.Up && nic.Speed > 0 ? nic.Speed : 0,
                    IsVirtual = IsVirtualAdapter(nic.Description),
                };
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (NetworkInformationException) { list.Add(info); continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        info.IPv4.Add(new AdapterAddress(ua.Address.ToString(), PrefixLength(ua)));
                    else if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !ua.Address.IsIPv6LinkLocal)
                        info.IPv6.Add(ua.Address.ToString());
                }
                info.Gateways = props.GatewayAddresses.Select(g => g.Address.ToString()).ToList();
                info.Dns = props.DnsAddresses.Select(d => d.ToString()).ToList();
                try
                {
                    if (nic.Supports(NetworkInterfaceComponent.IPv4))
                    {
                        info.DhcpEnabled = props.GetIPv4Properties().IsDhcpEnabled;
                        info.DhcpServer = props.DhcpServerAddresses.FirstOrDefault()?.ToString();
                    }
                }
                catch (NetworkInformationException) { }
                info.DnsAutomatic = DnsIsAutomatic(nic.Id);
                if (includeAll || info.StatusText == "Connected") list.Add(info);
            }
            return list;
        }

        private static int PrefixLength(UnicastIPAddressInformation ua)
        {
            try
            {
                return ua.PrefixLength;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException)
            {
                return ua.IPv4Mask == null ? 0 : ua.IPv4Mask.GetAddressBytes().Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
            }
        }

        private static bool DnsIsAutomatic(string id)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + id);
                return string.IsNullOrEmpty(key?.GetValue("NameServer") as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }

        private static bool IsVirtualAdapter(string description) =>
            new[] { "virtual", "vmware", "hyper-v", "vbox", "tap-", "tunnel", "pseudo", "wsl", "vethernet", "loopback", "miniport" }
                .Any(word => description.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
