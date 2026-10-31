using LibreHardwareMonitor.Hardware;
using System.Diagnostics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The one LibreHardwareMonitor <see cref="Computer"/> for the whole process, shared by
    /// Hardware Sensors and System Monitor. The library keeps process-wide state (its kernel
    /// driver handle, CPU opcode helpers) that every Computer.Close() tears down, so two
    /// Computers opened and closed from different threads knock each other out - sensors
    /// vanish, or the runtime dies with an ExecutionEngineException. Users take a reference
    /// with <see cref="Acquire"/>, give it back with <see cref="Release"/>, and do every
    /// open, update and read while holding <see cref="Gate"/>.
    /// </summary>
    internal static class SharedSensorHost
    {
        /// <summary>Held around every Open/Update/read/Close of the shared Computer.</summary>
        public static readonly object Gate = new();

        private static Computer? _computer;
        private static int _users;

        /// <summary>Takes a reference, opening the library for the first user. Throws if it can't open.</summary>
        public static Computer Acquire()
        {
            lock (Gate)
            {
                if (_computer == null)
                {
                    // Open with nothing enabled, then switch each group on by itself, so one
                    // group that throws costs only that group. Network stays off - the
                    // network tools cover adapters.
                    var computer = new Computer();
                    computer.Open();
                    Enable(computer, "CPU", c => c.IsCpuEnabled = true);
                    Enable(computer, "GPU", c => c.IsGpuEnabled = true);
                    Enable(computer, "memory", c => c.IsMemoryEnabled = true);
                    Enable(computer, "motherboard", c => c.IsMotherboardEnabled = true);
                    Enable(computer, "controllers", c => c.IsControllerEnabled = true);
                    Enable(computer, "storage", c => c.IsStorageEnabled = true);
                    Enable(computer, "battery", c => c.IsBatteryEnabled = true);
                    Enable(computer, "power supply", c => c.IsPsuEnabled = true);
                    _computer = computer;
                }
                _users++;
                return _computer;
            }
        }

        /// <summary>Gives a reference back; the library closes when the last user leaves.</summary>
        public static void Release()
        {
            lock (Gate)
            {
                if (_users == 0) return;
                if (--_users > 0) return;
                var computer = _computer;
                _computer = null;
                try
                {
                    computer?.Close();
                }
                catch (Exception ex)
                {
                    Log.Info($"Sensor host: closing failed: {ex.Message}");
                }
            }
        }

        private static void Enable(Computer computer, string name, Action<Computer> enable)
        {
            try
            {
                enable(computer);
            }
            catch (Exception ex)
            {
                Log.Info($"Sensor host: {name} sensors unavailable: {ex}");
            }
        }
    }
}
