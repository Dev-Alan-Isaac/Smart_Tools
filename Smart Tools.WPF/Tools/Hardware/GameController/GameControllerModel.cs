namespace Smart_Tools.WPF.Models
{
    /// <summary>How Windows talks to the controller.</summary>
    public enum ControllerApi
    {
        /// <summary>Xbox-style pads: two sticks, two analog triggers, vibration.</summary>
        XInput,
        /// <summary>Everything else (DirectInput-style HID devices: joysticks, wheels, flight sticks, many PlayStation or generic pads).</summary>
        Joystick,
    }

    public enum DeadzoneMode
    {
        /// <summary>Each axis on its own: a square dead area. The stick can't move along one axis only a little.</summary>
        Axial,
        /// <summary>The stick's distance from the middle: a round dead area. What most games use.</summary>
        Radial,
        /// <summary>Round dead area, then the rest stretched back to 0..1 so there is no jump when it ends.</summary>
        ScaledRadial,
    }

    /// <summary>A controller Windows reports right now.</summary>
    public sealed record ControllerInfo(
        string Id,
        ControllerApi Api,
        int Slot,
        string Name,
        string Detail,
        int Axes,
        int Buttons,
        bool HasPov,
        bool IsWireless,
        int BatteryPercent)
    {
        public override string ToString() => Name;
    }

    /// <summary>One reading. Axes are -1..1 with up positive; triggers are 0..1.</summary>
    public sealed record ControllerState(
        bool Connected,
        uint Packet,
        double[] Axes,
        double LeftTrigger,
        double RightTrigger,
        ulong Buttons,
        int Pov)
    {
        public static ControllerState Disconnected { get; } = new(false, 0, Array.Empty<double>(), 0, 0, 0, -1);

        public double Axis(int index) => index >= 0 && index < Axes.Length ? Axes[index] : 0;

        public bool IsDown(int button) => button >= 0 && button < 64 && (Buttons & (1UL << button)) != 0;
    }

    /// <summary>A reading and the moment it arrived (seconds since the poller started).</summary>
    public readonly record struct TimedControllerState(double Time, ControllerState State);

    /// <summary>What the rest test found on one stick.</summary>
    public sealed record StickRestResult(
        double CenterX,
        double CenterY,
        double MaxRadius,
        double NoiseRadius,
        int Samples,
        int SuggestedDeadzonePercent);

    /// <summary>What the range test found on one stick.</summary>
    public sealed record StickRangeResult(
        double[] Envelope,
        double MaxRadius,
        double MinRadius,
        double Coverage,
        double Roundness,
        int Samples);

    /// <summary>What the trigger test found.</summary>
    public sealed record TriggerResult(
        double FirstActivation,
        double Maximum,
        double Linearity,
        int Samples,
        double[] Curve);

    public sealed class GameControllerSettings
    {
        public DeadzoneMode Mode { get; set; } = DeadzoneMode.ScaledRadial;
        public int LeftDeadzonePercent { get; set; } = 24;
        public int RightDeadzonePercent { get; set; } = 27;
        public int TriggerThresholdPercent { get; set; } = 12;
        public bool SidebarCollapsed { get; set; }
    }
}
