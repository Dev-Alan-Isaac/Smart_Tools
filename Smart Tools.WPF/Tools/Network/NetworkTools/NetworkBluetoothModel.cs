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

    /// <summary>One Bluetooth radio (the chip in the PC or a USB dongle).</summary>
    public sealed record BluetoothRadioInfo(string Name, BluetoothRadioState State);

    /// <summary>One paired device. <c>LowEnergy</c> is true for BLE-only devices (most trackers, watches, sensors).</summary>
    public sealed record BluetoothDeviceInfo(string Id, string Name, string Address, bool LowEnergy, bool Connected);

    /// <summary>What Windows reports about Bluetooth: radios and paired devices. Read-only.</summary>
    public sealed class BluetoothInfo
    {
        public List<BluetoothRadioInfo> Radios { get; init; } = new();
        public List<BluetoothDeviceInfo> Devices { get; init; } = new();
        public BluetoothProblem Problem { get; init; }
    }
}
