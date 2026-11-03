namespace Smart_Tools.WPF.Models
{
    /// <summary>What the Audio Output Tester plays.</summary>
    public enum AudioTestKind
    {
        /// <summary>Each channel in turn, N beeps for channel N: shows every channel works and which is which.</summary>
        Channels,
        /// <summary>Same signal on both sides, or one side flipped: shows polarity / phase wiring.</summary>
        Phase,
        /// <summary>A steady tone of any pitch and shape.</summary>
        Tone,
        /// <summary>A slow glide from low to high pitch: finds dropouts, rattles and weak ranges.</summary>
        Sweep,
        /// <summary>Hiss, independent on every channel: compares loudness and balance.</summary>
        Noise,
    }

    public enum AudioWaveform { Sine, Square, Triangle, Saw }

    public enum AudioNoiseColor { Pink, White }

    public enum AudioPhaseMode
    {
        /// <summary>Every channel the same: the sound sits in the middle.</summary>
        InPhase,
        /// <summary>Right (every second channel) upside down: the sound is diffuse and thin in the bass.</summary>
        OutOfPhase,
        /// <summary>Switches between the two every few seconds.</summary>
        Alternate,
        /// <summary>A click that is positive on both sides, then positive left / negative right: the click should be the same direction.</summary>
        Click,
    }

    /// <summary>Everything the generator needs to know; replaced as a whole when the user changes anything.</summary>
    public sealed record AudioTestConfig(
        AudioTestKind Kind,
        uint ChannelMask,
        double LevelDb,
        double Frequency,
        AudioWaveform Waveform,
        double SweepSeconds,
        AudioPhaseMode Phase,
        AudioNoiseColor Noise)
    {
        public static AudioTestConfig Default { get; } = new(AudioTestKind.Channels, uint.MaxValue, -30, 1000, AudioWaveform.Sine, 12, AudioPhaseMode.Alternate, AudioNoiseColor.Pink);
    }

    /// <summary>An output device Windows has active.</summary>
    public sealed record AudioOutputDevice(string Id, string Name, int Channels, int SampleRate, uint SpeakerMask, bool IsDefault)
    {
        public override string ToString() => IsDefault ? Name + " ★" : Name;
    }

    public sealed class AudioOutputTestSettings
    {
        public AudioTestKind Kind { get; set; } = AudioTestKind.Channels;
        public double LevelDb { get; set; } = -30;
        public double Frequency { get; set; } = 1000;
        public AudioWaveform Waveform { get; set; } = AudioWaveform.Sine;
        public double SweepSeconds { get; set; } = 12;
        public AudioPhaseMode Phase { get; set; } = AudioPhaseMode.Alternate;
        public AudioNoiseColor Noise { get; set; } = AudioNoiseColor.Pink;
        public bool SidebarCollapsed { get; set; }
        /// <summary>What you listen with: Speakers, Headphones, Earbuds or Headset (only changes the picture).</summary>
        public string Look { get; set; } = "Speakers";
    }
}
