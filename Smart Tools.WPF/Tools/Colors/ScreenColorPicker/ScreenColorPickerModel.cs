namespace Smart_Tools.WPF.Models
{
    /// <summary>A picture of the whole screen: Bgra32 pixels, top row first.</summary>
    public sealed record ScreenColorImage(byte[] Pixels, int Width, int Height)
    {
        public int Stride => Width * 4;
    }

    /// <summary>How a picked colour is written when copied.</summary>
    public enum ScreenColorFormat { Hex, Rgb, Hsl, CssRgb }

    /// <summary>How many screen pixels are averaged into one pick (smooths dithering and anti-aliasing).</summary>
    public enum ScreenColorSample { Pixel, Average3, Average5 }

    /// <summary>One colour taken from the screen, and where it came from.</summary>
    public sealed record ScreenPick(RgbColor Color, int X, int Y, DateTime At);

    public sealed class ScreenColorPickerSettings
    {
        public ScreenColorFormat CopyFormat { get; set; } = ScreenColorFormat.Hex;
        public ScreenColorSample Sample { get; set; } = ScreenColorSample.Pixel;
        public bool AutoCopy { get; set; } = true;
        public bool HideWhilePicking { get; set; } = true;
        public bool SidebarCollapsed { get; set; }
    }
}
