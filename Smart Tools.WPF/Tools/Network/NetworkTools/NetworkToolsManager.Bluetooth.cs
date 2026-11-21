using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Bluetooth for the Adapters page: the radios (on/off), what the chip can
    /// do, and the paired devices (type, connected, battery, last seen, how well
    /// the pairing is protected). Read-only: nothing is paired, removed or
    /// switched. Uses the Windows Runtime APIs that every Windows 10+ PC has,
    /// so there is no package and no administrator prompt. Every call has a
    /// time limit because a stopped Bluetooth service can leave Windows
    /// waiting for a long time. Battery and last-seen are optional extras: a
    /// device that does not report them simply shows nothing.
    /// </summary>
    public static partial class NetworkToolsManager
    {
        private static readonly TimeSpan BluetoothTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan BluetoothPerDeviceTimeout = TimeSpan.FromSeconds(2);
        private const string PropConnected = "System.Devices.Aep.IsConnected";
        private const string PropAddress = "System.Devices.Aep.DeviceAddress";
        private const string PropLastSeen = "System.Devices.Aep.Bluetooth.LastSeenTime";
        private const string PropBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";   // DEVPKEY_Bluetooth_Battery: percent, only for devices that report it

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

                return new BluetoothInfo { Radios = radios, Devices = unique, Adapter = await ReadAdapterAsync(radios[0].Name, limit.Token) };
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
            var found = await DeviceInformation.FindAllAsync(selector, new[] { PropConnected, PropAddress, PropLastSeen, PropBattery }).AsTask(ct);
            foreach (var d in found)
            {
                bool connected = d.Properties.TryGetValue(PropConnected, out var c) && c is bool b && b;
                string address = d.Properties.TryGetValue(PropAddress, out var a) ? a?.ToString() ?? "" : "";
                string name = string.IsNullOrWhiteSpace(d.Name) ? address : d.Name;
                if (name.Length == 0) continue;

                int? battery = d.Properties.TryGetValue(PropBattery, out var bat) && bat is IConvertible conv && TryPercent(conv, out int pct) ? pct : null;
                DateTimeOffset? lastSeen = d.Properties.TryGetValue(PropLastSeen, out var ls) && ls is DateTimeOffset dto && dto.Year > 2000 ? dto : null;
                var protection = d.Pairing?.ProtectionLevel switch
                {
                    DevicePairingProtectionLevel.None => BluetoothProtection.None,
                    DevicePairingProtectionLevel.Encryption => BluetoothProtection.Encrypted,
                    DevicePairingProtectionLevel.EncryptionAndAuthentication => BluetoothProtection.EncryptedAndVerified,
                    _ => BluetoothProtection.Unknown,
                };
                string kind = await ClassifyAsync(d.Id, lowEnergy, name, ct);
                into.Add(new BluetoothDeviceInfo(d.Id, name, address.ToUpperInvariant(), lowEnergy, connected, kind, battery, lastSeen, protection));
            }
        }

        private static bool TryPercent(IConvertible value, out int percent)
        {
            percent = 0;
            try
            {
                percent = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
            {
                return false;
            }
            return percent is >= 0 and <= 100;
        }

        // =================================================================
        //  What kind of device it is
        // =================================================================

        /// <summary>
        /// Opens the device for a moment to read its class (classic) or appearance
        /// (Low Energy). Each device has its own short limit, so one that is away
        /// costs two seconds, not the whole read. "" when Windows would not say.
        /// </summary>
        private static async Task<string> ClassifyAsync(string id, bool lowEnergy, string name, CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(BluetoothPerDeviceTimeout);
            try
            {
                if (!lowEnergy)
                {
                    using var dev = await BluetoothDevice.FromIdAsync(id).AsTask(limit.Token);
                    if (dev == null) return KindFromName(name);
                    var cod = dev.ClassOfDevice;
                    return KindFromClass(cod.MajorClass.ToString(), cod.MinorClass.ToString(), name);
                }
                using var le = await BluetoothLEDevice.FromIdAsync(id).AsTask(limit.Token);
                if (le == null) return KindFromName(name);
                return KindFromAppearance(le.Appearance.Category, le.Appearance.SubCategory, name);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return KindFromName(name);   // this one device took too long
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
            {
                return KindFromName(name);
            }
        }

        /// <summary>Ids: Headphones, Speaker, Microphone, Phone, Computer, Keyboard, Mouse, Gamepad, Input, Watch, Display, Camera, Car, Health, Tag, Other.</summary>
        public static string KindFromClass(string major, string minor, string name)
        {
            bool Has(string w) => minor.Contains(w, StringComparison.OrdinalIgnoreCase);
            if (Has("Headphones") || Has("WearableHeadset") || Has("HandsFree")) return "Headphones";
            if (Has("Loudspeaker") && !Has("VideoDisplay")) return "Speaker";
            if (Has("Microphone")) return "Microphone";
            if (Has("CarAudio")) return "Car";
            if (Has("Gamepad") || Has("Joystick") || Has("GamingOrToy")) return "Gamepad";
            if (Has("Camera") || Has("Camcorder")) return "Camera";
            if (Has("VideoMonitor") || Has("VideoDisplay")) return "Display";
            if (Has("Wristwatch")) return "Watch";
            if (major.Contains("Phone", StringComparison.OrdinalIgnoreCase) || Has("Phone")) return "Phone";
            if (major.Contains("Computer", StringComparison.OrdinalIgnoreCase)) return "Computer";
            if (major.Contains("Health", StringComparison.OrdinalIgnoreCase)) return "Health";
            string byName = KindFromName(name);
            if (byName.Length > 0) return byName;
            if (major.Contains("Peripheral", StringComparison.OrdinalIgnoreCase)) return "Input";   // keyboards and mice share one class value
            if (major.Contains("AudioVideo", StringComparison.OrdinalIgnoreCase)) return "Speaker";
            return "";
        }

        /// <summary>Low Energy appearance: the Bluetooth "assigned numbers" category (0x0F = human interface device: 1 keyboard, 2 mouse, 3-4 game controllers).</summary>
        public static string KindFromAppearance(int category, int sub, string name)
        {
            switch (category)
            {
                case 1: return "Phone";
                case 2: return "Computer";
                case 3: return "Watch";
                case 5: return "Display";
                case 8 or 9: return "Tag";
                case 12 or 13 or 14 or 16 or 49 or 51: return "Health";
                case 15:
                    return sub switch { 1 => "Keyboard", 2 => "Mouse", 3 or 4 => "Gamepad", _ => KindFromName(name) is { Length: > 0 } n ? n : "Input" };
            }
            return KindFromName(name);
        }

        /// <summary>Last resort: a device that names itself ("MX Keys", "AirPods", "JBL Flip").</summary>
        public static string KindFromName(string name)
        {
            bool Has(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (Has("keyboard", "keys")) return "Keyboard";
            if (Has("mouse", "trackpad", "mx master")) return "Mouse";
            if (Has("headphone", "earbud", "buds", "airpods", "headset", "earphone")) return "Headphones";
            if (Has("speaker", "soundbar", "boombox", "flip", "charge ")) return "Speaker";
            if (Has("controller", "gamepad", "joy-con")) return "Gamepad";
            if (Has("watch", "band")) return "Watch";
            return "";
        }

        // =================================================================
        //  The chip itself
        // =================================================================

        private static async Task<BluetoothAdapterDetails?> ReadAdapterAsync(string radioName, CancellationToken ct)
        {
            string address = "";
            bool classic = false, le = false, secureLe = false, central = false, peripheral = false;
            try
            {
                var ad = await BluetoothAdapter.GetDefaultAsync().AsTask(ct);
                if (ad != null)
                {
                    address = FormatBluetoothAddress(ad.BluetoothAddress);
                    classic = ad.IsClassicSupported;
                    le = ad.IsLowEnergySupported;
                    secureLe = ad.AreLowEnergySecureConnectionsSupported;
                    central = ad.IsCentralRoleSupported;
                    peripheral = ad.IsPeripheralRoleSupported;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
            {
                Log.Info($"Bluetooth adapter details: {ex.Message}");
            }
            var (maker, version, date) = await Task.Run(() => ReadBluetoothDriver(radioName), ct);
            if (address.Length == 0 && maker.Length == 0 && version.Length == 0 && !classic && !le) return null;
            return new BluetoothAdapterDetails(address, classic, le, secureLe, central, peripheral, maker, version, date);
        }

        private static string FormatBluetoothAddress(ulong value) =>
            value == 0 ? "" : string.Join(":", Enumerable.Range(0, 6).Select(i => ((value >> (8 * (5 - i))) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)));

        /// <summary>The radio's driver: maker, version, date (the signed-driver list; the entry named like the radio, else the first that isn't an enumerator).</summary>
        private static (string Maker, string Version, string Date) ReadBluetoothDriver(string radioName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DeviceName, Manufacturer, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass='BLUETOOTH'");
                ManagementBaseObject? best = null;
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    string dn = o["DeviceName"]?.ToString() ?? "";
                    if (dn.Equals(radioName, StringComparison.OrdinalIgnoreCase)) { best?.Dispose(); best = o; break; }
                    if (best == null && !dn.Contains("Enumerator", StringComparison.OrdinalIgnoreCase) && !dn.Contains("Service", StringComparison.OrdinalIgnoreCase)) best = o;
                    else o.Dispose();
                }
                if (best == null) return ("", "", "");
                using (best)
                {
                    string date = "";
                    if (best["DriverDate"]?.ToString() is { Length: > 0 } raw)
                    {
                        try { date = ManagementDateTimeConverter.ToDateTime(raw).ToString("d"); }
                        catch (ArgumentOutOfRangeException) { }
                    }
                    return (best["Manufacturer"]?.ToString()?.Trim() ?? "", best["DriverVersion"]?.ToString()?.Trim() ?? "", date);
                }
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                Log.Info($"Bluetooth driver not read: {ex.Message}");
                return ("", "", "");
            }
        }
    }
}
