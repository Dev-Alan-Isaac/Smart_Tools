using LibreHardwareMonitor.Hardware;
using Smart_Tools.WPF.Models;
using System.Diagnostics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// System Monitor's temperatures - CPU package, each graphics card, each
    /// drive - read through LibreHardwareMonitorLib (the same library the
    /// Hardware Sensors page uses). Separate from that page: nothing here is
    /// shared with HardwareSensorsManager.
    /// </summary>
    public sealed partial class SystemMonitorManager
    {
        /// <summary>
        /// Opens LibreHardwareMonitorLib once and reads it every refresh.
        /// Opening (and a stuck read) can take seconds, so it's only ever
        /// touched from the read loop's own thread - never the UI thread.
        /// Any failure just means no temperatures, exactly like Hardware
        /// Sensors: System Monitor's other readings never depend on this.
        /// </summary>
        private sealed class TemperatureReader : IDisposable
        {
            private Computer? _computer;
            private int _failures;
            private readonly UpdateVisitor _visitor = new();

            public void Open()
            {
                if (_computer != null) return;
                try
                {
                    _computer = SharedSensorHost.Acquire();
                    _failures = 0;
                }
                catch (Exception ex)
                {
                    Log.Info($"System Monitor: temperature sensors unavailable: {ex.Message}");
                    Close();
                }
            }

            public SystemMonitorTemps Read()
            {
                var computer = _computer;
                if (computer == null) return SystemMonitorTemps.Empty;
                try
                {
                    double? cpu = null;
                    var gpus = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    var disks = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    // Shared with Hardware Sensors: hold the gate while updating and reading.
                    lock (SharedSensorHost.Gate)
                    {
                    computer.Accept(_visitor);
                    foreach (var hw in AllHardware(computer))
                    {
                        var readings = hw.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature && sensor.Value is float v && !float.IsNaN(v)).ToList();
                        if (readings.Count == 0) continue;
                        switch (hw.HardwareType)
                        {
                            case HardwareType.Cpu:
                                var package = readings.FirstOrDefault(r => r.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                                           ?? readings.FirstOrDefault(r => r.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase));
                                double value = package?.Value ?? readings.Max(r => r.Value!.Value);
                                cpu = cpu is double existing ? Math.Max(existing, value) : value;
                                break;
                            case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                                var core = readings.FirstOrDefault(r => r.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) ?? readings[0];
                                gpus[hw.Name] = core.Value!.Value;
                                break;
                            case HardwareType.Storage:
                                disks[hw.Name] = readings[0].Value!.Value;
                                break;
                        }
                    }
                    }
                    _failures = 0;
                    return new SystemMonitorTemps(cpu, gpus, disks);
                }
                catch (Exception ex)
                {
                    // One bad read shouldn't end it; give up after several in a row.
                    Log.Info($"System Monitor: temperature read failed: {ex.Message}");
                    if (++_failures >= 5) Close();
                    return SystemMonitorTemps.Empty;
                }
            }

            private static IEnumerable<IHardware> AllHardware(IComputer computer)
            {
                foreach (var hw in computer.Hardware)
                {
                    yield return hw;
                    foreach (var sub in hw.SubHardware) yield return sub;
                }
            }

            public void Close()
            {
                if (_computer == null) return;
                _computer = null;
                SharedSensorHost.Release();
            }

            public void Dispose() => Close();

            private sealed class UpdateVisitor : IVisitor
            {
                public void VisitComputer(IComputer computer) => computer.Traverse(this);

                public void VisitHardware(IHardware hardware)
                {
                    hardware.Update();
                    foreach (var sub in hardware.SubHardware) sub.Accept(this);
                }

                public void VisitSensor(ISensor sensor)
                {
                }

                public void VisitParameter(IParameter parameter)
                {
                }
            }
        }
    }
}
