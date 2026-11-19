using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Management;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The adapters that are not network ones: display (GPU), audio, storage
    /// controllers and USB controllers, read from WMI. Read-only: the page
    /// shows what Windows reports (driver, state, maker), nothing is changed.
    /// </summary>
    public static partial class NetworkToolsManager
    {
        /// <summary>Category ids used by the page's filter; "Network" and "Bluetooth" come from the other readers.</summary>
        public static readonly string[] HardwareCategories = { "Display", "Audio", "Storage", "USB" };

        /// <summary>One WMI class that lists a kind of adapter, and the words that go with it.</summary>
        private sealed record HardwareSource(string WmiClass, string Category, string Kind);

        private static readonly HardwareSource[] HardwareSources =
        {
            new("Win32_VideoController", "Display", "Display adapter"),
            new("Win32_SoundDevice", "Audio", "Audio adapter"),
            new("Win32_SCSIController", "Storage", "Storage controller"),
            new("Win32_IDEController", "Storage", "Storage controller"),
            new("Win32_USBController", "USB", "USB controller"),
        };

        /// <summary>Win32 problem code: 0 = working, 22 = disabled by the user, anything else = Windows reports a problem.</summary>
        public static AdapterState HardwareStateFrom(int? configManagerErrorCode) => configManagerErrorCode switch
        {
            0 => AdapterState.Connected,
            22 => AdapterState.Disabled,
            null => AdapterState.Unknown,
            _ => AdapterState.Disconnected,
        };

        public static string HardwareStatusText(AdapterState state, int? code) => state switch
        {
            AdapterState.Connected => "Working",
            AdapterState.Disabled => "Disabled",
            AdapterState.Disconnected => $"Problem (code {code})",
            _ => "Unknown",
        };

        /// <summary>Every display, audio, storage and USB adapter. A class that WMI can't answer is skipped.</summary>
        public static List<AdapterInfo> GetHardwareAdapters()
        {
            var list = new List<AdapterInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in HardwareSources)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher($"SELECT * FROM {source.WmiClass}");
                    foreach (ManagementBaseObject obj in searcher.Get())
                    {
                        using (obj)
                        {
                            var a = ReadHardware(obj, source);
                            if (a != null && seen.Add(a.Id)) list.Add(a);
                        }
                    }
                }
                catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
                {
                    Log.Info($"Adapters: {source.WmiClass} not read: {ex.Message}");
                }
            }
            return list;
        }

        private static string Text(ManagementBaseObject o, string prop) => o[prop]?.ToString()?.Trim() ?? "";

        private static AdapterInfo? ReadHardware(ManagementBaseObject o, HardwareSource source)
        {
            string name = Text(o, "Name");
            if (name.Length == 0) return null;
            string pnp = Text(o, "PNPDeviceID");
            int? code = o["ConfigManagerErrorCode"] is null ? null : Convert.ToInt32(o["ConfigManagerErrorCode"], CultureInfo.InvariantCulture);
            var state = HardwareStateFrom(code);

            var rows = new List<NetworkInfoRow>();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) rows.Add(new NetworkInfoRow(label, value));
            }
            Add("Type", source.Kind);
            Add("Maker", Text(o, source.Category == "Display" ? "AdapterCompatibility" : "Manufacturer"));
            if (source.Category == "Display")
            {
                Add("Graphics chip", Text(o, "VideoProcessor"));
                // AdapterRAM is a 32-bit number: it tops out at 4 GB, so a bigger card shows 4 GB here.
                if (o["AdapterRAM"] is not null && Convert.ToUInt32(o["AdapterRAM"], CultureInfo.InvariantCulture) > 0)
                    Add("Video memory", FormatBytes(Convert.ToUInt32(o["AdapterRAM"], CultureInfo.InvariantCulture)));
                if (o["CurrentHorizontalResolution"] is not null && o["CurrentVerticalResolution"] is not null)
                {
                    string refresh = o["CurrentRefreshRate"] is not null ? $" @ {Convert.ToInt32(o["CurrentRefreshRate"], CultureInfo.InvariantCulture)} Hz" : "";
                    Add("Current mode", $"{o["CurrentHorizontalResolution"]} x {o["CurrentVerticalResolution"]}{refresh}");
                }
                Add("Driver version", Text(o, "DriverVersion"));
            }
            Add("Status", HardwareStatusText(state, code));
            Add("Device id", pnp);

            return new AdapterInfo
            {
                Name = name,
                Id = (pnp.Length > 0 ? pnp : source.Category + ":" + name),
                Description = source.Kind,
                Kind = source.Kind,
                HardwareCategory = source.Category,
                State = state,
                StatusOverride = HardwareStatusText(state, code),
                ExtraRows = rows,
            };
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
            return $"{bytes:0.#} {units[i]}";
        }
    }
}
