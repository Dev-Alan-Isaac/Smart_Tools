namespace Smart_Tools.WPF.Models
{
    /// <summary>What a capture takes a picture of.</summary>
    public enum ScreenshotMode
    {
        /// <summary>An area you drag out.</summary>
        Region,
        /// <summary>The window that is in front when the timer ends.</summary>
        Window,
        /// <summary>Every monitor.</summary>
        FullScreen,
    }

    /// <summary>The marking tools in the editor.</summary>
    public enum ScreenshotInk
    {
        Pen,
        Arrow,
        Rectangle,
        Highlight,
        Text,
        Pixelate,
    }

    /// <summary>Remembered between visits. Never holds a folder or a file name.</summary>
    public sealed class ScreenshotSettings
    {
        public ScreenshotMode Mode { get; set; } = ScreenshotMode.Region;
        public int DelaySeconds { get; set; }
        public bool HideApp { get; set; } = true;
        public bool CopyAfterCapture { get; set; }
        public int ColorIndex { get; set; }
        public int Thickness { get; set; } = 4;
    }
}
