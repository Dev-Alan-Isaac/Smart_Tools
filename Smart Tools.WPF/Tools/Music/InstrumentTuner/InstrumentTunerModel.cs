namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Instrument Tuner data. Plain data - no NAudio, no WPF.
    // =====================================================================

    public enum InstrumentTunerNotation
    {
        /// <summary>C D E F G A B</summary>
        Letters,

        /// <summary>Do Re Mi Fa Sol La Si</summary>
        Solfege,
    }

    /// <summary>How quiet a sound the tuner still listens to.</summary>
    public enum InstrumentTunerSensitivity
    {
        /// <summary>Ignores background noise - for loud instruments or noisy rooms.</summary>
        Low,
        Normal,

        /// <summary>Hears quiet instruments and far-away microphones.</summary>
        High,
    }

    /// <summary>A microphone Windows lists (remembered by name).</summary>
    public sealed record InstrumentTunerDevice(int Number, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>One string of an instrument: its name (e.g. "E2") and MIDI note (frequency follows the A4 reference).</summary>
    public sealed record InstrumentTunerString(string Name, int MidiNote);

    /// <summary>A tuning: an instrument and its strings, low to high as you hold it. No strings = chromatic.</summary>
    public sealed record InstrumentTunerPreset(string Name, string Icon, IReadOnlyList<InstrumentTunerString> Strings)
    {
        public bool IsChromatic => Strings.Count == 0;
        public override string ToString() => Name;
    }

    /// <summary>A frequency named against the A4 reference.</summary>
    public sealed record InstrumentTunerNote(
        int MidiNote,
        string Letter,
        string Solfege,
        int Octave,
        double Cents,
        double FrequencyHz,
        double TargetFrequencyHz)
    {
        public string Name(InstrumentTunerNotation notation) => notation == InstrumentTunerNotation.Solfege ? Solfege : Letter;
    }

    /// <summary>One analysis of the latest audio: a frequency (null when no clear note) and how loud it was (0..1 RMS).</summary>
    public readonly record struct InstrumentTunerReading(double? FrequencyHz, double Level);

    /// <summary>Remembered between sessions (instrument_tuner.json in %AppData%\Smart Tools). No paths; enums by name.</summary>
    public sealed class InstrumentTunerSettings
    {
        public string DeviceName { get; set; } = "";
        public double A4Hz { get; set; } = 440;
        public InstrumentTunerNotation Notation { get; set; } = InstrumentTunerNotation.Letters;
        public string Preset { get; set; } = "Chromatic";
        public InstrumentTunerSensitivity Sensitivity { get; set; } = InstrumentTunerSensitivity.Normal;
    }
}
