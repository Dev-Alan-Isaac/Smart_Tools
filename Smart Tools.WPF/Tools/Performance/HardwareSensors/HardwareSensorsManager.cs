using LibreHardwareMonitor.Hardware;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Hardware Sensors: reads every sensor LibreHardwareMonitorLib (NuGet,
    /// MPL-2.0) can see - temperatures, loads, clocks, fans, voltages, power,
    /// drive data - on a background loop, and raises one
    /// <see cref="HardwareSnapshot"/> per refresh on the UI thread.
    ///
    /// The library is opened, read and closed only on the loop's own thread
    /// (opening can take seconds), so the page never freezes. Anything the
    /// library throws means "no sensors" with the reason, never a crash.
    /// Create on the UI thread.
    /// </summary>
    public sealed partial class HardwareSensorsManager : IDisposable
    {
        private readonly SynchronizationContext? _ui;
        private CancellationTokenSource? _loopCts;
        private volatile int _intervalMs = 1000;
        private volatile bool _disposed;
        private Computer? _computer;
        private string? _openError;
        private int _readFailures;

        public HardwareSensorsManager()
        {
            _ui = SynchronizationContext.Current;
        }

        /// <summary>A new reading (UI thread).</summary>
        public event Action<HardwareSnapshot>? SnapshotReady;

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
            // A previous loop may still be closing the library (Stop() returns at once). The
            // new loop waits for it, otherwise the old loop's Close() would tear down the
            // freshly opened Computer and the page would report no sensors on the second visit.
            var previous = _loopTask;
            _loopTask = Task.Run(async () =>
            {
                if (previous != null)
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                }
                if (cts.IsCancellationRequested) return;
                await LoopAsync(cts.Token).ConfigureAwait(false);
            });
        }

        private Task? _loopTask;

        /// <summary>Stops reading; returns at once. The library is closed on the loop's thread.</summary>
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
            try
            {
                Open();
                while (!token.IsCancellationRequested)
                {
                    var started = Stopwatch.StartNew();
                    if (_computer == null && DateTime.UtcNow - _closedAt > TimeSpan.FromSeconds(10)) Open();
                    var snapshot = new HardwareSnapshot(DateTime.UtcNow, Read(), _computer == null ? _openError : null);
                    if (!token.IsCancellationRequested) Post(() => SnapshotReady?.Invoke(snapshot));
                    int wait = Math.Max(50, _intervalMs - (int)started.ElapsedMilliseconds);
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            finally
            {
                Close();
            }
        }

        // =================================================================
        //  What decides which sensors appear
        // =================================================================

        public static bool IsAdministrator()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>LibreHardwareMonitor's CPU/motherboard driver (PawnIO) is installed as a Windows service.</summary>
        public static bool IsSensorDriverInstalled()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key != null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        // =================================================================
        //  LibreHardwareMonitor
        // =================================================================

        private void Open()
        {
            if (_computer != null) return;
            try
            {
                // The library is shared with System Monitor (see SharedSensorHost).
                _computer = SharedSensorHost.Acquire();
                _openError = null;
                _readFailures = 0;
            }
            catch (Exception ex)
            {
                // Anything can come out of a hardware library (DLL missing, blocked by security software...).
                Log.Info($"Hardware Sensors: library unavailable: {ex}");
                _openError = ex.Message;
                Close();
            }
        }

        private static IEnumerable<IHardware> AllHardware(IComputer computer)
        {
            // Parents before their sub-parts, at any depth (Super I/O chips sit under the motherboard,
            // and some boards nest an embedded controller one level further down).
            foreach (var hw in computer.Hardware)
                foreach (var part in Flatten(hw)) yield return part;
        }

        private static IEnumerable<IHardware> Flatten(IHardware hw)
        {
            yield return hw;
            foreach (var sub in hw.SubHardware)
                foreach (var inner in Flatten(sub)) yield return inner;
        }

        /// <summary>Parts whose sensors LibreHardwareMonitor reads through the PawnIO driver.</summary>
        private static bool UsesPawnIo(string hardwareType) =>
            hardwareType is "Cpu" or "Motherboard" or "SuperIO" or "EmbeddedController";

        /// <summary>NaN and infinity (a sensor that answers nothing) become "no value" so the page shows a dash.</summary>
        private static float? Clean(float? v) => v is float f && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null;

        private List<HardwareReading> Read()
        {
            var list = new List<HardwareReading>();
            var computer = _computer;
            if (computer == null)
            {
                AddWmiLayer(list);   // the library failed to open: WMI may still know something
                return list;
            }
            bool pawnIo = _pawnIo ??= IsSensorDriverInstalled();
            try
            {
                lock (SharedSensorHost.Gate)
                foreach (var hw in AllHardware(computer))
                {
                    // Update each part on its own (parents before their sub-parts, as the
                    // library expects): one part whose update throws keeps its last values
                    // instead of ending the whole read.
                    try
                    {
                        hw.Update();
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Hardware Sensors: updating {hw.Name} failed: {ex.Message}");
                    }
                    string hwType = hw.HardwareType.ToString();
                    var part = PartFrom(hwType);
                    string partId = hw.Identifier.ToString();
                    var source = pawnIo && UsesPawnIo(hwType) ? HardwareSensorSource.PawnIo : HardwareSensorSource.Lhm;
                    foreach (var sensor in hw.Sensors)
                    {
                        list.Add(new HardwareReading(
                            partId,
                            hw.Name,
                            part,
                            sensor.Identifier.ToString(),
                            sensor.Name ?? "",
                            KindFrom(sensor.SensorType.ToString()),
                            Clean(sensor.Value),
                            Clean(sensor.Min),
                            Clean(sensor.Max),
                            source));
                    }
                }
                _readFailures = 0;
            }
            catch (Exception ex)
            {
                // One bad read shouldn't end it; give up after several in a row (and say why).
                Log.Info($"Hardware Sensors: read failed: {ex.Message}");
                if (++_readFailures >= 5)
                {
                    _openError = Loc.F("Str_HardwareSensors_StoppedAnswering", "The sensors stopped answering: {0}", ex.Message);
                    Close();
                }
            }

            AddWmiLayer(list);
            return list;
        }

        /// <summary>Second layer: WMI / ACPI. Never allowed to break the first layer's readings.</summary>
        private static void AddWmiLayer(List<HardwareReading> list)
        {
            try
            {
                bool libreHasFans = list.Any(r => r.Kind == HardwareSensorKind.Fan && r.Value is > 0);
                list.AddRange(HardwareWmiSource.Read(libreHasFans));
            }
            catch (Exception ex)
            {
                Log.Info($"Hardware Sensors: WMI layer failed: {ex.Message}");
            }
        }

        private bool? _pawnIo;

        private DateTime _closedAt = DateTime.MinValue;

        private void Close()
        {
            _closedAt = DateTime.UtcNow;
            if (_computer == null) return;
            _computer = null;
            SharedSensorHost.Release();
        }
    }
}
