namespace Smart_Tools.WPF.Models
{
    /// <summary>Whether a Bluetooth radio is switched on (Windows' Radio API).</summary>
    public enum BluetoothRadioState
    {
        On,
        Off,
        Disabled,
        Unknown
    }

    /// <summary>Why the Bluetooth section is empty; the view turns it into a sentence in the user's language.</summary>
    public enum BluetoothProblem
    {
        None,
        NoAdapter,
        Unavailable
    }

    /// <summary>How well a pairing is protected (Windows' pairing protection level).</summary>
    public enum BluetoothProtection
    {
        Unknown,
        None,
        Encrypted,
        EncryptedAndVerified
    }

    /// <summary>One Bluetooth radio (the chip in the PC or a USB dongle).</summary>
    public sealed record BluetoothRadioInfo(string Name, BluetoothRadioState State);

    /// <summary>
    /// One paired device. <c>LowEnergy</c> is true for BLE-only devices (most trackers, watches, sensors).
    /// <c>Kind</c> is an id such as "Headphones" or "Keyboard" ("" when Windows would not say), <c>Battery</c> is a
    /// percentage only when the device reports one, <c>LastSeen</c> only when Windows has recorded it.
    /// </summary>
    public sealed record BluetoothDeviceInfo(
        string Id,
        string Name,
        string Address,
        bool LowEnergy,
        bool Connected,
        string Kind = "",
        int? Battery = null,
        DateTimeOffset? LastSeen = null,
        BluetoothProtection Protection = BluetoothProtection.Unknown);

    /// <summary>What the PC's Bluetooth chip can do, and its driver. Any part Windows would not answer is empty/false.</summary>
    public sealed record BluetoothAdapterDetails(
        string Address,
        bool Classic,
        bool LowEnergy,
        bool SecureLowEnergy,
        bool CentralRole,
        bool PeripheralRole,
        string DriverMaker,
        string DriverVersion,
        string DriverDate);

    /// <summary>What Windows reports about Bluetooth: radios and paired devices. Read-only.</summary>
    public sealed class BluetoothInfo
    {
        public List<BluetoothRadioInfo> Radios { get; init; } = new();
        public List<BluetoothDeviceInfo> Devices { get; init; } = new();
        public BluetoothAdapterDetails? Adapter { get; init; }
        public BluetoothProblem Problem { get; init; }
    }
}
