namespace Smart_Tools.WPF.Models
{
    /// <summary>What the Microphone test makes of the sound so far. The view turns each value into a message.</summary>
    public enum MicTestVerdict
    {
        Waiting,
        Silent,
        TooQuiet,
        Good,
        TooLoud,
        Clipping,
    }

    /// <summary>A reading of the running test. Levels are linear, 0-100 (100 is the loudest the microphone can send).</summary>
    public sealed record MicTestSnapshot(
        double CurrentPercent,
        double PeakPercent,
        double QuietestPercent,
        int ClippedBuffers,
        TimeSpan Elapsed);

    /// <summary>Remembered between visits: the microphone and whether to hear it. Never a path.</summary>
    public sealed class MicTestSettings
    {
        public string DeviceName { get; set; } = "";
        public bool HearYourself { get; set; }
        /// <summary>Which picture fills with the voice: Stand, Handheld, Headset or Earbuds.</summary>
    }
}
