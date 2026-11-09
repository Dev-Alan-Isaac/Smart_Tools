using Smart_Tools.WPF.Models;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Bluetooth for My IP &amp; Adapters: the radios (on/off) and the paired
    /// devices (connected or not). Read-only: nothing is paired, removed or
    /// switched. Uses the Windows Runtime APIs that every Windows 10+ PC has,
    /// so there is no package and no administrator prompt. Every call has a
    /// time limit because a stopped Bluetooth service can leave Windows
    /// waiting for a long time.
    /// </summary>
    public static partial class NetworkToolsManager
    {
        private static readonly TimeSpan BluetoothTimeout = TimeSpan.FromSeconds(8);
        private const string PropConnected = "System.Devices.Aep.IsConnected";
        private const string PropAddress = "System.Devices.Aep.DeviceAddress";

        public static async Task<BluetoothInfo> GetBluetoothAsync(CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(BluetoothTimeout);
            try
            {
                var radios = new List<BluetoothRadioInfo>();
                var all = await Radio.GetRadiosAsync().AsTask(limit.Token);
                foreach (var r in all)
                {
                    if (r.Kind != RadioKind.Bluetooth) continue;
                    radios.Add(new BluetoothRadioInfo(
                        string.IsNullOrWhiteSpace(r.Name) ? "Bluetooth" : r.Name,
                        r.State switch
                        {
                            RadioState.On => BluetoothRadioState.On,
                            RadioState.Off => BluetoothRadioState.Off,
                            RadioState.Disabled => BluetoothRadioState.Disabled,
                            _ => BluetoothRadioState.Unknown,
                        }));
                }
                if (radios.Count == 0) return new BluetoothInfo { Problem = BluetoothProblem.NoAdapter };

                var devices = new List<BluetoothDeviceInfo>();
                await AddPairedAsync(devices, BluetoothDevice.GetDeviceSelectorFromPairingState(true), lowEnergy: false, limit.Token);
                await AddPairedAsync(devices, BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), lowEnergy: true, limit.Token);

                // A device that is both classic and LE shows once (same address): keep the connected / classic row.
                var unique = devices
                    .GroupBy(d => d.Address.Length > 0 ? d.Address : d.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(d => d.Connected).ThenBy(d => d.LowEnergy).First())
                    .OrderByDescending(d => d.Connected)
                    .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                return new BluetoothInfo { Radios = radios, Devices = unique };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or COMException or InvalidOperationException
                                          or UnauthorizedAccessException or System.IO.FileNotFoundException or NotSupportedException)
            {
                Log.Info($"Bluetooth not read: {ex.Message}");
                return new BluetoothInfo { Problem = BluetoothProblem.Unavailable };
            }
        }

        private static async Task AddPairedAsync(List<BluetoothDeviceInfo> into, string selector, bool lowEnergy, CancellationToken ct)
        {
            var found = await DeviceInformation.FindAllAsync(selector, new[] { PropConnected, PropAddress }).AsTask(ct);
            foreach (var d in found)
            {
                bool connected = d.Properties.TryGetValue(PropConnected, out var c) && c is bool b && b;
                string address = d.Properties.TryGetValue(PropAddress, out var a) ? a?.ToString() ?? "" : "";
                string name = string.IsNullOrWhiteSpace(d.Name) ? address : d.Name;
                if (name.Length == 0) continue;
                into.Add(new BluetoothDeviceInfo(d.Id, name, address.ToUpperInvariant(), lowEnergy, connected));
            }
        }
    }
}
