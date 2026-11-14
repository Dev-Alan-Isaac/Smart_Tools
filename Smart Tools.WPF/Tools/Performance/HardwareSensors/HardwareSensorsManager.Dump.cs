using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Sensor dump: every sensor of the last reading, whatever its value, with its source, so a
    /// PC of any make can be compared with what the page shows. The text is meant for support
    /// and debugging, so it is not translated.
    /// </summary>
    public sealed partial class HardwareSensorsManager
    {
        public const string DumpFileName = "sensors_dump.txt";

        public static string SourceName(HardwareSensorSource s) => s switch
        {
            HardwareSensorSource.PawnIo => "PawnIO (via LHM)",
            HardwareSensorSource.Wmi => "WMI",
            _ => "LHM",
        };

        /// <summary>The full dump text: environment header, one line per sensor, the WMI notes and candidates.</summary>
        public static string BuildDump(IReadOnlyList<HardwareReading> readings, DateTime now)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("Smart Tools - sensor dump");
            sb.AppendLine("Taken:        " + now.ToString("yyyy-MM-dd HH:mm:ss", inv));
            sb.AppendLine("Windows:      " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : ""));
            sb.AppendLine("Administrator: " + (IsAdministrator() ? "yes" : "no"));
            sb.AppendLine("PawnIO driver: " + DescribePawnIo());
            sb.AppendLine("LHM library:  " + (typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version?.ToString() ?? "?"));
            sb.AppendLine("Sources:      LHM = LibreHardwareMonitorLib, PawnIO (via LHM) = LHM parts that read through the PawnIO driver (CPU, motherboard, Super I/O, EC), WMI = Windows WMI/ACPI");
            sb.AppendLine();

            string current = "";
            foreach (var r in readings.OrderBy(r => r.Source).ThenBy(r => r.Part).ThenBy(r => r.PartId, StringComparer.Ordinal))
            {
                string header = SourceName(r.Source) + " / " + r.PartName + " [" + r.PartId + "]";
                if (header != current)
                {
                    current = header;
                    sb.AppendLine();
                    sb.AppendLine("== " + header);
                }
                string value = r.Value is float v ? v.ToString("0.###", inv) : "null";
                sb.AppendLine($"  {SourceName(r.Source),-16} | {r.SensorId} | {(string.IsNullOrWhiteSpace(r.Name) ? "(no name)" : r.Name)} | {r.Kind} | {value}");
            }
            if (readings.Count == 0) sb.AppendLine("(no sensors read yet)");

            sb.AppendLine();
            sb.AppendLine("== WMI probes");
            var notes = HardwareWmiSource.Notes;
            if (notes.Count == 0) sb.AppendLine("  (not run yet)");
            foreach (var n in notes) sb.AppendLine("  " + n);

            sb.AppendLine();
            sb.AppendLine("== WMI candidates (vendor classes in root\\WMI, every numeric property; only RPM-like ones become fan sensors)");
            var cands = HardwareWmiSource.Candidates;
            if (cands.Count == 0) sb.AppendLine("  (none)");
            foreach (var c in cands) sb.AppendLine("  " + c);
            return sb.ToString();
        }

        /// <summary>One line on the PawnIO driver: installed or not, whether it is running, where it lives.</summary>
        public static string DescribePawnIo()
        {
            if (!OperatingSystem.IsWindows()) return "not applicable";
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                if (key == null) return "not installed (CPU/motherboard/EC sensors need it: https://pawnio.eu)";
                string image = key.GetValue("ImagePath") as string ?? "?";
                object? start = key.GetValue("Start");
                return $"installed, start type {start} (3 = on demand), {image}";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return "unknown (" + ex.Message + ")";
            }
        }
    }
}
