namespace Smart_Tools.WPF.Models
{
    /// <summary>A colour pair in CIE 1931 xy chromaticity coordinates.</summary>
    public readonly record struct MonitorHdrXy(double X, double Y);

    /// <summary>How a verdict should be shown: green tick, plain note, or amber warning.</summary>
    public enum MonitorHdrSeverity
    {
        Good,
        Info,
        Warning,
    }

    /// <summary>The colour primaries and white point a screen reports (xy), or all zeros when Windows doesn't say.</summary>
    public sealed record MonitorHdrPrimaries(MonitorHdrXy Red, MonitorHdrXy Green, MonitorHdrXy Blue, MonitorHdrXy White)
    {
        public bool IsKnown => Red.X > 0 && Green.X > 0 && Blue.Y > 0;
    }

    /// <summary>Everything Windows says about one screen's colour: size, refresh, bit depth, HDR and brightness.</summary>
    public sealed record MonitorHdrInfo(
        string DeviceName,
        string Name,
        string FriendlyName,
        int X,
        int Y,
        int Width,
        int Height,
        bool IsPrimary,
        int RefreshHz,
        int CurrentBits,            // bits per colour channel in use now (0 = unknown)
        int PanelMaxBits,           // bits per colour the screen declares in its EDID (0 = unknown)
        string ColorFormat,         // RGB, YCbCr 4:4:4 ... or empty
        bool HdrSupported,
        bool HdrEnabled,
        bool WideColorEnabled,
        double MinNits,
        double PeakNits,
        double FullFrameNits,
        double SdrWhiteNits,        // Windows "SDR content brightness" (0 = unknown)
        MonitorHdrPrimaries Primaries)
    {
        public override string ToString() => IsPrimary ? $"{Name} ★" : Name;
    }

    /// <summary>One conclusion about a screen. The text comes from the language files (<c>Str_MonitorHdr_Verdict_{Key}</c>).</summary>
    public sealed record MonitorHdrVerdict(string Key, MonitorHdrSeverity Severity, object[] Args);

    /// <summary>How much of a standard colour space a screen's primaries cover, and how big its triangle is next to it.</summary>
    public sealed record MonitorHdrGamut(string Name, double CoveragePercent, double SizePercent);

    /// <summary>What a test picture is for; the page groups its tiles by this.</summary>
    public enum MonitorHdrPatternGroup
    {
        Gradients,
        Levels,
        ColorBrightness,
    }

    /// <summary>One full-screen test picture. The name shown comes from the language files (<c>Str_MonitorHdr_Pat_{Id}</c>).</summary>
    public sealed record MonitorHdrPattern(string Id, MonitorHdrPatternGroup Group);

    public sealed class MonitorHdrSettings
    {
        /// <summary>Seconds on each picture in the slideshow; 0 means change by hand.</summary>
        public int SlideshowSeconds { get; set; }
        public bool ShowHints { get; set; } = true;
        public bool SidebarCollapsed { get; set; }
    }
}
