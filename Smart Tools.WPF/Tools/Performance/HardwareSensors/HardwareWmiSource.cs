using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Second layer of the sensor cascade: Windows WMI / ACPI. Used where LibreHardwareMonitor
    /// sees nothing (typically a laptop whose fans sit behind the embedded controller).
    /// What it reads:
    ///  - ACPI thermal zones (root\WMI MSAcpi_ThermalZoneTemperature, and the Win32 thermal-zone
    ///    performance class, which needs no administrator),
    ///  - Win32_Fan (only the few makers that fill DesiredSpeed),
    ///  - battery charge / discharge rate (root\WMI BatteryStatus),
    ///  - a discovery pass over root\WMI classes whose name mentions fan / thermal / temperature
    ///    (Lenovo, HP, Dell, ASUS publish theirs there): numeric properties named like RPM or
    ///    "fan speed" become Fan sensors; every other candidate is only listed in the sensor dump.
    /// Every query is isolated: a missing class or denied access costs that probe only, and the
    /// answers are cached (WMI is slow), so the page's refresh loop never waits on it.
    /// Call <see cref="Read"/> from a background thread.
    /// </summary>
    internal static class HardwareWmiSource
    {
        private const string PartId = "/wmi/acpi";
        private const string PartName = "ACPI / WMI";
        private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
        private static readonly object Sync = new();

        private static List<HardwareReading> _cache = new();
        private static List<string> _candidates = new();
        private static List<string> _notes = new();
        private static DateTime _lastRun = DateTime.MinValue;
        private static bool _running;
        private static List<(string Scope, string Class)>? _discovered;

        /// <summary>Candidate properties found in vendor classes, one line each (for the dump).</summary>
        public static IReadOnlyList<string> Candidates { get { lock (Sync) return _candidates.ToList(); } }

        /// <summary>What each probe did ("ok", "no such class", "access denied"...), for the dump.</summary>
        public static IReadOnlyList<string> Notes { get { lock (Sync) return _notes.ToList(); } }

        /// <summary>The cached readings (refreshed at most every few seconds). Fan readings are dropped when LibreHardwareMonitor already has fans.</summary>
        public static List<HardwareReading> Read(bool libreHasFans)
        {
            if (!OperatingSystem.IsWindows()) return new List<HardwareReading>();
            // The first pass can take seconds (class discovery): run it beside the page's loop and
            // answer from the cache meanwhile, so the first snapshot is never held up.
            lock (Sync)
            {
                if (!_running && DateTime.UtcNow - _lastRun >= Every)
                {
                    _running = true;
                    Task.Run(() =>
                    {
                        try { Refresh(); }
                        finally { lock (Sync) _running = false; }
                    });
                }
            }
            lock (Sync)
                return _cache.Where(r => !(libreHasFans && r.Kind == HardwareSensorKind.Fan)).ToList();
        }

        private static void Refresh()
        {
            var readings = new List<HardwareReading>();
            var candidates = new List<string>();
            var notes = new List<string>();

            Probe(notes, "root\\WMI MSAcpi_ThermalZoneTemperature", () => ReadAcpiZones(readings));
            Probe(notes, "Win32 thermal zone counters", () => ReadZoneCounters(readings));
            Probe(notes, "Win32_Fan", () => ReadWin32Fan(readings));
            Probe(notes, "root\\WMI BatteryStatus", () => ReadBatteryPower(readings));
            Probe(notes, "vendor classes in root\\WMI", () => ReadVendorClasses(readings, candidates, notes));

            lock (Sync)
            {
                _cache = readings;
                _candidates = candidates;
                _notes = notes;
                _lastRun = DateTime.UtcNow;
            }
        }

        private static void Probe(List<string> notes, string name, Action run)
        {
            try
            {
                run();
                notes.Add($"{name}: ok");
            }
            catch (ManagementException ex)
            {
                notes.Add($"{name}: {ex.ErrorCode} ({ex.Message})");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or System.IO.IOException)
            {
                notes.Add($"{name}: {ex.GetType().Name} ({ex.Message})");
            }
        }

        private static IEnumerable<ManagementBaseObject> Query(string scope, string wql)
        {
            using var searcher = new ManagementObjectSearcher(scope, wql) { Options = { Timeout = TimeSpan.FromSeconds(4) } };
            foreach (ManagementBaseObject o in searcher.Get()) yield return o;
        }

        private static HardwareReading Make(string id, string name, HardwareSensorKind kind, double value) =>
            new(PartId, PartName, HardwarePartKind.Other, PartId + "/" + id, name, kind, (float)value, null, null, HardwareSensorSource.Wmi);

        /// <summary>A number from a WMI property, or null for missing / NaN / absurd values.</summary>
        private static double? Num(object? raw)
        {
            if (raw == null) return null;
            try
            {
                double v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }

        private static string ShortName(string? instance, string fallback)
        {
            if (string.IsNullOrWhiteSpace(instance)) return fallback;
            string s = instance.Trim();
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0 && slash < s.Length - 1) s = s[(slash + 1)..];
            return s;
        }

        // ---- fixed probes ----------------------------------------------------

        private static void ReadAcpiZones(List<HardwareReading> into)
        {
            foreach (var o in Query("root\\WMI", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
            {
                // tenths of a kelvin; 0 or out-of-range means the zone has no real sensor
                if (Num(o["CurrentTemperature"]) is not double tenth) continue;
                double c = tenth / 10.0 - 273.15;
                if (c < -20 || c > 150) continue;
                into.Add(Make("acpi/" + ShortName(o["InstanceName"] as string, "zone"), "ACPI thermal zone " + ShortName(o["InstanceName"] as string, "?"), HardwareSensorKind.Temperature, c));
            }
        }

        private static void ReadZoneCounters(List<HardwareReading> into)
        {
            foreach (var o in Query("root\\CIMV2", "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            {
                if (Num(o["Temperature"]) is not double kelvin) continue;
                double c = kelvin - 273.15;
                if (c < -20 || c > 150) continue;
                string name = ShortName(o["Name"] as string, "zone");
                if (into.Any(r => r.Kind == HardwareSensorKind.Temperature && r.SensorId.EndsWith("/acpi/" + name, StringComparison.OrdinalIgnoreCase))) continue;   // same zone as the ACPI probe
                into.Add(Make("zone/" + name, "Thermal zone " + name, HardwareSensorKind.Temperature, c));
            }
        }

        private static void ReadWin32Fan(List<HardwareReading> into)
        {
            int n = 0;
            foreach (var o in Query("root\\CIMV2", "SELECT Name, DesiredSpeed FROM Win32_Fan"))
            {
                n++;
                if (Num(o["DesiredSpeed"]) is double rpm && rpm > 0 && rpm < 100_000)
                    into.Add(Make("fan/" + n, (o["Name"] as string)?.Trim() is { Length: > 0 } nm ? nm : "Fan " + n, HardwareSensorKind.Fan, rpm));
            }
        }

        private static void ReadBatteryPower(List<HardwareReading> into)
        {
            foreach (var o in Query("root\\WMI", "SELECT ChargeRate, DischargeRate FROM BatteryStatus"))
            {
                double charge = Num(o["ChargeRate"]) ?? 0, discharge = Num(o["DischargeRate"]) ?? 0;
                double mw = charge > 0 ? charge : discharge;     // either one is the rate in mW; the other is 0
                if (mw < 0 || mw > 500_000) continue;           // a bogus value must not reach the page
                into.Add(Make("battery-power", charge > 0 ? "Battery charge rate" : "Battery discharge rate", HardwareSensorKind.Power, mw / 1000.0));
                break;
            }
        }

        // ---- discovery of vendor classes -------------------------------------

        private static readonly Regex ClassName = new(@"fan|thermal|therm|temperature|cooling", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SkipClass = new(@"^(__|MSAcpi_|MSFT_|Win32_|CIM_|WmiMonitor|MSNdis|MSStor|MSPower|Processor|WMIBinary)|Event$|Method$|Notification|Policy|Settings$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FanProp = new(@"rpm|fan.?speed|speed.?rpm|fan.?rate|tach", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void ReadVendorClasses(List<HardwareReading> into, List<string> candidates, List<string> notes)
        {
            if (_discovered == null)
            {
                var found = new List<(string, string)>();
                foreach (var o in Query("root\\WMI", "SELECT * FROM meta_class"))
                {
                    string? cls = o["__CLASS"] as string;
                    if (cls != null && ClassName.IsMatch(cls) && !SkipClass.IsMatch(cls)) found.Add(("root\\WMI", cls));
                }
                _discovered = found;
                notes.Add($"discovery: {found.Count} candidate class(es): {string.Join(", ", found.Select(f => f.Item2))}");
            }

            int index = 0;
            foreach (var (scope, cls) in _discovered)
            {
                try
                {
                    foreach (var inst in Query(scope, "SELECT * FROM " + cls))
                    {
                        index++;
                        string instance = ShortName(inst["InstanceName"] as string, cls + "#" + index);
                        foreach (PropertyData p in inst.Properties)
                        {
                            if (p.IsArray || Num(p.Value) is not double v) continue;
                            candidates.Add($"{cls} | {instance} | {p.Name} = {v.ToString("0.###", CultureInfo.InvariantCulture)}");
                            if (FanProp.IsMatch(p.Name) && v > 0 && v < 100_000)
                                into.Add(Make($"vendor/{cls}/{instance}/{p.Name}", $"{cls} {p.Name}", HardwareSensorKind.Fan, v));
                        }
                    }
                }
                catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    notes.Add($"{cls}: {ex.Message}");
                }
            }
        }
    }
}
