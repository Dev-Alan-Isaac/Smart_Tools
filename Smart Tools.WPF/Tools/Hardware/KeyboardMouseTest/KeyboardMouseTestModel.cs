namespace Smart_Tools.WPF.Models
{
    /// <summary>One key of the on-screen keyboard. Positions and sizes are in key units (a normal key is 1 x 1).</summary>
    public sealed record KeyDef(string Id, string Label, double X, double Y, double Width = 1, double Height = 1);

    /// <summary>A key going down or up, as the keyboard reported it. <see cref="Time"/> is seconds on a steady clock.</summary>
    public readonly record struct RawKeyEvent(double Time, string Id, bool Down);

    [Flags]
    public enum MouseButtonFlags : ushort
    {
        None = 0,
        LeftDown = 0x0001, LeftUp = 0x0002,
        RightDown = 0x0004, RightUp = 0x0008,
        MiddleDown = 0x0010, MiddleUp = 0x0020,
        Button4Down = 0x0040, Button4Up = 0x0080,
        Button5Down = 0x0100, Button5Up = 0x0200,
        Wheel = 0x0400, HWheel = 0x0800,
    }

    /// <summary>One mouse report: how far it moved (in the mouse's own counts), which buttons changed and how far the wheel turned.</summary>
    public readonly record struct RawMouseEvent(double Time, int Dx, int Dy, MouseButtonFlags Buttons, int WheelDelta, bool Absolute);

    /// <summary>The five buttons a mouse can report.</summary>
    public enum MouseButtonKind { Left, Right, Middle, Back, Forward }

    /// <summary>A press of a mouse button or key that came right after the last one, too fast to be a person.</summary>
    public sealed record ChatterEvent(string Id, double GapMs);

    /// <summary>How often the mouse reports, from the spacing of its reports.</summary>
    public sealed record PollingResult(
        double Hz,
        double MedianMs,
        double MinMs,
        double MaxMs,
        double JitterMs,
        double LatePercent,
        int Reports,
        int[] Histogram);

    /// <summary>One reaction-time round.</summary>
    public sealed record ReactionResult(double Milliseconds, string Device);

    public sealed class KeyboardMouseTestSettings
    {
        /// <summary>A key or button pressed again sooner than this (ms) after being released is flagged as chatter.</summary>
        public int ChatterMs { get; set; } = 30;
        public bool BlockKeys { get; set; } = true;
        /// <summary>How far the mouse is moved for the DPI measurement, in centimetres.</summary>
        public int DpiDistanceCm { get; set; } = 10;
        /// <summary>Mouse speed (counts per second) that puts the movement indicator's puck on the edge of its ring.</summary>
        public int MotionRange { get; set; } = 3000;
        public bool SidebarCollapsed { get; set; }
    }
}
