using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>What Windows reports about one battery. Capacities are in mWh; anything it doesn't tell us stays null.</summary>
    public sealed record BatteryInfo(
        string Name, string? Manufacturer, string? Chemistry,
        int? DesignCapacity, int? FullChargeCapacity, int? RemainingCapacity,
        int? CycleCount, int? ChargePercent, double? Volts, bool? Charging, bool? PluggedIn)
    {
        /// <summary>Full-charge capacity as a share of the design capacity, 0-100+; null when either is unknown.</summary>
        public double? HealthPercent => BatteryHealthManager.Health(DesignCapacity, FullChargeCapacity);
    }

    public sealed record BatteryReading(List<BatteryInfo> Batteries, string? Problem);

    /// <summary>Battery Health logic: wear level from the design and full-charge capacity, read from WMI. No WPF types.</summary>
    public static class BatteryHealthManager
    {
        /// <summary>100 x full / design, one decimal. Null when either is missing or zero.</summary>
        public static double? Health(int? design, int? full)
        {
            if (design is not > 0 || full is not > 0) return null;
            return Math.Round(100.0 * full.Value / design.Value, 1);
        }

        public static string Describe(double? health) => health switch
        {
            null => "Windows doesn't report the capacity of this battery.",
            >= 90 => "Excellent - close to new.",
            >= 80 => "Good - normal wear.",
            >= 60 => "Worn - it holds noticeably less than when new.",
            >= 40 => "Poor - consider replacing it soon.",
            _ => "Very worn - replacement recommended.",
        };

        public static string Mwh(int? value) => value is { } v ? (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Wh" : "unknown";

        /// <summary>Reads every battery. An empty list (no problem) means this PC has no battery, like most desktops.</summary>
        public static BatteryReading Read()
        {
            try
            {
                var basic = new List<(string Name, string? Chem, int? Percent, bool? Charging, bool? Plugged)>();
                using (var s = new ManagementObjectSearcher("SELECT Name, Chemistry, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery"))
                    foreach (ManagementObject b in s.Get())
                    {
                        int? status = Int(b["BatteryStatus"]);
                        basic.Add((
                            (b["Name"] as string) ?? "Battery",
                            ChemistryName(Int(b["Chemistry"])),
                            Int(b["EstimatedChargeRemaining"]),
                            status is null ? null : status is 6 or 7 or 8 or 9,                    // 6-9 = charging
                            status is null ? null : status is 2 or 3 or 6 or 7 or 8 or 9 or 11));   // on mains power
                    }
                if (basic.Count == 0) return new BatteryReading(new List<BatteryInfo>(), null);

                var design = Wmi("BatteryStaticData", "DesignedCapacity");
                var full = Wmi("BatteryFullChargedCapacity", "FullChargedCapacity");
                var cycles = Wmi("BatteryCycleCount", "CycleCount");
                var remaining = Wmi("BatteryStatus", "RemainingCapacity");
                var voltage = Wmi("BatteryStatus", "Voltage");
                var maker = WmiText("BatteryStaticData", "ManufactureName");

                var list = new List<BatteryInfo>();
                for (int i = 0; i < basic.Count; i++)
                {
                    var b = basic[i];
                    list.Add(new BatteryInfo(
                        b.Name, At(maker, i), b.Chem,
                        Positive(At(design, i)), Positive(At(full, i)), Positive(At(remaining, i)),
                        Positive(At(cycles, i)), b.Percent,
                        At(voltage, i) is > 0 and var mv ? mv / 1000.0 : null,
                        b.Charging, b.Plugged));
                }
                return new BatteryReading(list, null);
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
            {
                return new BatteryReading(new List<BatteryInfo>(), "Windows wouldn't tell us about the battery: " + ex.Message);
            }
        }

        private static T? At<T>(List<T?> list, int i) where T : struct => i < list.Count ? list[i] : null;
        private static string? At(List<string?> list, int i) => i < list.Count ? list[i] : null;
        private static int? Positive(int? v) => v is > 0 ? v : null;

        private static int? Int(object? o) => o switch
        {
            null => null,
            int i => i,
            uint u => u <= int.MaxValue ? (int)u : null,
            ushort us => us,
            short s => s,
            long l => l is >= int.MinValue and <= int.MaxValue ? (int)l : null,
            _ => int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out int v) ? v : null,
        };

        /// <summary>One value per battery from the root\WMI namespace; an empty list when the class isn't there.</summary>
        private static List<int?> Wmi(string cls, string property)
        {
            var list = new List<int?>();
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", $"SELECT {property} FROM {cls}");
                foreach (ManagementBaseObject o in s.Get()) list.Add(Int(o[property]));
            }
            catch (ManagementException) { }
            return list;
        }

        private static List<string?> WmiText(string cls, string property)
        {
            var list = new List<string?>();
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", $"SELECT {property} FROM {cls}");
                foreach (ManagementBaseObject o in s.Get()) list.Add((o[property] as string)?.Trim());
            }
            catch (ManagementException) { }
            return list;
        }

        private static string? ChemistryName(int? code) => code switch
        {
            1 => "Other", 3 => "Lead acid", 4 => "Nickel cadmium", 5 => "Nickel metal hydride",
            6 => "Lithium-ion", 7 => "Zinc air", 8 => "Lithium polymer", _ => null,
        };

        /// <summary>Asks Windows for its own full battery report (an HTML page) and returns its path, or null with the reason.</summary>
        public static string? CreateWindowsReport(out string? problem)
        {
            problem = null;
            try
            {
                string path = Path.Combine(Path.GetTempPath(), $"battery-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
                var info = new ProcessStartInfo("powercfg.exe", $"/batteryreport /output \"{path}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(info);
                if (p == null) { problem = "Couldn't start powercfg."; return null; }
                if (!p.WaitForExit(20_000)) { try { p.Kill(); } catch (InvalidOperationException) { } problem = "powercfg took too long."; return null; }
                if (p.ExitCode != 0 || !File.Exists(path))
                {
                    problem = (p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd()).Trim();
                    if (problem.Length == 0) problem = "Windows couldn't make a battery report on this PC.";
                    return null;
                }
                return path;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                problem = ex.Message;
                return null;
            }
        }
    }
}
