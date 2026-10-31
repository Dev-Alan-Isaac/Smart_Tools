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
            foreach (var hw in computer.Hardware)
            {
                yield return hw;
                foreach (var sub in hw.SubHardware) yield return sub;
            }
        }

        private List<HardwareReading> Read()
        {
            var list = new List<HardwareReading>();
            var computer = _computer;
            if (computer == null) return list;
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
                    var part = PartFrom(hw.HardwareType.ToString());
                    string partId = hw.Identifier.ToString();
                    foreach (var sensor in hw.Sensors)
                    {
                        list.Add(new HardwareReading(
                            partId,
                            hw.Name,
                            part,
                            sensor.Identifier.ToString(),
                            sensor.Name,
                            KindFrom(sensor.SensorType.ToString()),
                            sensor.Value,
                            sensor.Min,
                            sensor.Max));
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
                    _openError = "The sensors stopped answering: " + ex.Message;
                    Close();
                }
            }
            return list;
        }

        private void Close()
        {
            if (_computer == null) return;
            _computer = null;
            SharedSensorHost.Release();
        }
    }
}
