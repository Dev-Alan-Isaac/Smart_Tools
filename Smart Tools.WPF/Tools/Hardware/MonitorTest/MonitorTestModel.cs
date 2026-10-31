namespace Smart_Tools.WPF.Models
{
    /// <summary>What a test pattern is for; the Monitor Test groups its tiles by this.</summary>
    public enum MonitorPatternGroup
    {
        DeadPixels,
        Backlight,
        Gradients,
        Calibration,
        Retention,
        Motion,
    }

    /// <summary>One full-screen test picture. The name shown comes from the language files (<c>Str_Monitor_Pat_{Id}</c>).</summary>
    public sealed record MonitorPattern(string Id, MonitorPatternGroup Group, bool Animated = false, bool Flashing = false);

    /// <summary>A screen as Windows describes it.</summary>
    public sealed record MonitorScreenInfo(
        string DeviceName,
        string Name,
        int X,
        int Y,
        int Width,
        int Height,
        bool IsPrimary,
        int RefreshHz,
        int BitsPerPixel)
    {
        public override string ToString() => IsPrimary ? $"{Name} ★" : Name;
    }

    /// <summary>A spot the person clicked during a test because a pixel looked wrong.</summary>
    public sealed record MonitorMark(int X, int Y, string PatternId, DateTime At);

    /// <summary>What the refresh-rate measurement found.</summary>
    public sealed record FrameTimingResult(
        double Hz,
        double AverageMs,
        double JitterMs,
        double WorstMs,
        int Frames,
        int Missed,
        double MissedPercent,
        double LowOnePercentHz);

    public sealed class MonitorTestSettings
    {
        /// <summary>Seconds on each picture in the slideshow; 0 means change by hand.</summary>
        public int SlideshowSeconds { get; set; }
        public bool ShowHints { get; set; } = true;
        public bool SidebarCollapsed { get; set; }
    }
}
