namespace Smart_Tools.WPF.Models
{
    /// <summary>A camera DirectShow knows by name.</summary>
    public sealed record WebcamDevice(string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>A picture size a camera offers, with the frame rate to ask for (never above 30, to keep the preview light).</summary>
    public sealed record WebcamMode(int Width, int Height, int Fps)
    {
        public string Key => $"{Width}x{Height}";

        public override string ToString() => $"{Width} × {Height}  ·  {Fps} fps";
    }

    /// <summary>Numbers about the running preview.</summary>
    public sealed record WebcamStats(long Frames, double Fps, double BrightnessPercent);

    /// <summary>How the picture's average brightness reads.</summary>
    public enum WebcamBrightness
    {
        Dark,
        Good,
        Bright,
    }

    /// <summary>Remembered between visits. A name and a size, never a path.</summary>
    public sealed class WebcamTestSettings
    {
        public string DeviceName { get; set; } = "";
        public string ModeKey { get; set; } = "";
        public bool Mirror { get; set; } = true;
    }
}
