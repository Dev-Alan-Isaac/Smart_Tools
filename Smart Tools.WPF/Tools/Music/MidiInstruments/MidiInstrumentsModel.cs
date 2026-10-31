using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  MIDI Instruments data. Plain data - no WPF, no winmm. The view binds
    //  to these; MidiInstrumentsManager plays them.
    // =====================================================================

    /// <summary>A General MIDI instrument family (8 instruments each), or the drum kits.</summary>
    public sealed record MidiInstrumentFamily(int Index, string Name, string Icon)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// One playable sound. Melodic instruments play on channel 1 with their
    /// GM program number; drum kits play on channel 10, where every key is a
    /// different drum and the program picks the kit.
    /// </summary>
    public sealed record MidiInstrument(int Program, string Name, MidiInstrumentFamily Family, bool IsDrumKit = false)
    {
        /// <summary>0-based MIDI channel: 0 for instruments, 9 (shown as 10) for drums.</summary>
        public int Channel => IsDrumKit ? MidiInstrumentCatalog.DrumChannel : 0;

        /// <summary>Saved in settings - a name, not a number, so a reorder can't change the sound.</summary>
        public string Key => (IsDrumKit ? "drums:" : "gm:") + Program;

        /// <summary>"001" for Acoustic Grand Piano, like a synth's display.</summary>
        public string NumberText => IsDrumKit ? "Kit" : (Program + 1).ToString("000");

        public override string ToString() => Name;
    }

    public static class MidiInstrumentCatalog
    {
        public const int DrumChannel = 9;

        public static readonly IReadOnlyList<MidiInstrumentFamily> Families = new[]
        {
            new MidiInstrumentFamily(0, "Piano", "Piano"),
            new MidiInstrumentFamily(1, "Chromatic percussion", "BellOutline"),
            new MidiInstrumentFamily(2, "Organ", "MusicBoxOutline"),
            new MidiInstrumentFamily(3, "Guitar", "GuitarAcoustic"),
            new MidiInstrumentFamily(4, "Bass", "GuitarElectric"),
            new MidiInstrumentFamily(5, "Strings", "Violin"),
            new MidiInstrumentFamily(6, "Ensemble", "AccountGroupOutline"),
            new MidiInstrumentFamily(7, "Brass", "Trumpet"),
            new MidiInstrumentFamily(8, "Reed", "Saxophone"),
            new MidiInstrumentFamily(9, "Pipe", "Bugle"),
            new MidiInstrumentFamily(10, "Synth lead", "SineWave"),
            new MidiInstrumentFamily(11, "Synth pad", "Waveform"),
            new MidiInstrumentFamily(12, "Synth effects", "Shimmer"),
            new MidiInstrumentFamily(13, "World", "Earth"),
            new MidiInstrumentFamily(14, "Percussive", "MusicNoteEighth"),
            new MidiInstrumentFamily(15, "Sound effects", "WeatherLightning"),
            new MidiInstrumentFamily(16, "Drum kits", "Metronome"),
        };

        public static MidiInstrumentFamily DrumFamily => Families[16];

        private static readonly string[] GmNames =
        {
            "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano", "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
            "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
            "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
            "Nylon Guitar", "Steel Guitar", "Jazz Guitar", "Clean Electric Guitar", "Muted Electric Guitar", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics",
            "Acoustic Bass", "Fingered Bass", "Picked Bass", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
            "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
            "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
            "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
            "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
            "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
            "Square Lead", "Sawtooth Lead", "Calliope Lead", "Chiff Lead", "Charang Lead", "Voice Lead", "Fifths Lead", "Bass + Lead",
            "New Age Pad", "Warm Pad", "Polysynth Pad", "Choir Pad", "Bowed Pad", "Metallic Pad", "Halo Pad", "Sweep Pad",
            "Rain", "Soundtrack", "Crystal", "Atmosphere", "Brightness", "Goblins", "Echoes", "Sci-fi",
            "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
            "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
            "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot",
        };

        /// <summary>GS drum kits (program on channel 10) - all built into Microsoft GS Wavetable Synth.</summary>
        private static readonly (int Program, string Name)[] DrumKits =
        {
            (0, "Standard Kit"), (8, "Room Kit"), (16, "Power Kit"), (24, "Electronic Kit"),
            (25, "TR-808 Kit"), (32, "Jazz Kit"), (40, "Brush Kit"), (48, "Orchestra Kit"),
        };

        /// <summary>All 128 GM instruments, then the 8 drum kits.</summary>
        public static readonly IReadOnlyList<MidiInstrument> All =
            GmNames.Select((n, i) => new MidiInstrument(i, n, Families[i / 8]))
                .Concat(DrumKits.Select(k => new MidiInstrument(k.Program, k.Name, Families[16], IsDrumKit: true)))
                .ToList();

        public static MidiInstrument Default => All[0];

        /// <summary>General MIDI percussion: what each key plays on a drum kit (35-81).</summary>
        private static readonly string[] DrumNames =
        {
            "Acoustic Bass Drum", "Bass Drum", "Side Stick", "Snare", "Hand Clap", "Electric Snare", "Low Floor Tom", "Closed Hi-hat",
            "High Floor Tom", "Pedal Hi-hat", "Low Tom", "Open Hi-hat", "Low-Mid Tom", "High-Mid Tom", "Crash Cymbal 1", "High Tom",
            "Ride Cymbal 1", "Chinese Cymbal", "Ride Bell", "Tambourine", "Splash Cymbal", "Cowbell", "Crash Cymbal 2", "Vibraslap",
            "Ride Cymbal 2", "High Bongo", "Low Bongo", "Mute High Conga", "Open High Conga", "Low Conga", "High Timbale", "Low Timbale",
            "High Agogo", "Low Agogo", "Cabasa", "Maracas", "Short Whistle", "Long Whistle", "Short Guiro", "Long Guiro",
            "Claves", "High Woodblock", "Low Woodblock", "Mute Cuica", "Open Cuica", "Mute Triangle", "Open Triangle",
        };

        public const int FirstDrumNote = 35;

        /// <summary>The drum on this key of a kit, or null where the GM kit has none.</summary>
        public static string? DrumName(int note) =>
            note >= FirstDrumNote && note < FirstDrumNote + DrumNames.Length ? DrumNames[note - FirstDrumNote] : null;
    }

    /// <summary>Who pressed a key - the screen, the computer keyboard, a MIDI keyboard, or playback.</summary>
    public enum MidiNoteSource
    {
        Mouse,
        ComputerKeyboard,
        MidiKeyboard,
        Playback,
    }

    /// <summary>How hard a mouse click plays: always the Velocity slider, or harder lower down the key (like pressing nearer the edge).</summary>
    public enum MidiTouchMode
    {
        Fixed,
        ByClickHeight,
    }

    /// <summary>One key on the on-screen piano.</summary>
    public sealed class MidiPianoKey : INotifyPropertyChanged
    {
        public required int Note { get; init; }
        public required bool IsBlack { get; init; }

        /// <summary>For white keys: 0, 1, 2... left to right. For black keys: the white key on their left.</summary>
        public required int WhiteIndex { get; init; }

        public required string NoteName { get; init; }

        /// <summary>The computer key that plays it ("Z", "2", ","), or "" when none does.</summary>
        public string ComputerKey { get; init; } = "";

        /// <summary>For drum kits: the drum on this key.</summary>
        public string? DrumName { get; init; }

        private bool _isDown;
        public bool IsDown
        {
            get => _isDown;
            set { if (_isDown != value) { _isDown = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A channel message sent while recording, with its time from the start.</summary>
    public readonly record struct MidiRecordedEvent(long TimeMs, byte Status, byte Data1, byte Data2)
    {
        public bool IsNoteOn => (Status & 0xF0) == 0x90 && Data2 > 0;
    }

    public sealed class MidiRecording
    {
        public List<MidiRecordedEvent> Events { get; } = new();
        public long DurationMs { get; set; }
        public string InstrumentName { get; init; } = "";
        public int NoteCount => Events.Count(e => e.IsNoteOn);
        public bool IsEmpty => NoteCount == 0;
    }

    /// <summary>Remembered between sessions (midi_instruments.json in %AppData%\Smart Tools). No paths; enums by name.</summary>
    public sealed class MidiInstrumentsSettings
    {
        public string Instrument { get; set; } = "gm:0";
        public int Octave { get; set; } = 4;
        public int Velocity { get; set; } = 100;
        public int Volume { get; set; } = 110;
        public int Reverb { get; set; } = 40;
        public MidiTouchMode Touch { get; set; } = MidiTouchMode.Fixed;
        public bool ShowNoteNames { get; set; } = true;
        public bool ShowComputerKeys { get; set; } = true;

        /// <summary>Device names (not paths), so the right synth / keyboard is picked again even if Windows renumbers them.</summary>
        public string OutputDevice { get; set; } = "";
        public string InputDevice { get; set; } = "";
    }
}
