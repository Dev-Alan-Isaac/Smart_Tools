using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>One channel message of a song, on the song's own tick timeline.</summary>
    public readonly record struct MidiSongEvent(long Tick, int Track, byte Status, byte Data1, byte Data2);

    /// <summary>One track of a song, as the list shows it. Mute and Solo are read by the player while it runs.</summary>
    public sealed class MidiSongTrack : INotifyPropertyChanged
    {
        private volatile bool _muted;
        private volatile bool _solo;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Index { get; init; }
        public string Name { get; init; } = "";

        /// <summary>"Overdriven Guitar", "Drums": the first instrument the track selects.</summary>
        public string Instrument { get; init; } = "";
        public string ChannelsText { get; init; } = "";
        public int NoteCount { get; init; }
        public bool IsDrums { get; init; }

        public bool IsMuted
        {
            get => _muted;
            set { if (_muted == value) return; _muted = value; Raise(nameof(IsMuted)); }
        }

        public bool IsSolo
        {
            get => _solo;
            set { if (_solo == value) return; _solo = value; Raise(nameof(IsSolo)); }
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A .mid file read with its tracks kept apart, so each one can be silenced.</summary>
    public sealed class MidiSong
    {
        public string Name { get; init; } = "";
        public int TicksPerQuarter { get; init; } = 480;
        public IReadOnlyList<MidiSongEvent> Events { get; init; } = Array.Empty<MidiSongEvent>();
        public IReadOnlyList<(long Tick, int Micros)> Tempos { get; init; } = Array.Empty<(long, int)>();
        public IReadOnlyList<(long Tick, int Numerator, int Denominator)> TimeSignatures { get; init; } = Array.Empty<(long, int, int)>();
        public IReadOnlyList<MidiSongTrack> Tracks { get; init; } = Array.Empty<MidiSongTrack>();
        public long LengthTicks { get; init; }

        public int FirstBpm => (int)Math.Round(60_000_000.0 / (Tempos.Count > 0 ? Tempos[0].Micros : 500_000));
    }

    /// <summary>One .mid file in the list on the left.</summary>
    public sealed class MidiPlayAlongFile : INotifyPropertyChanged
    {
        private bool _isCurrent;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Path { get; init; } = "";
        public string Name => System.IO.Path.GetFileName(Path);
        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (_isCurrent == value) return; _isCurrent = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent))); }
        }
    }

    /// <summary>Remembered between sessions (midi_playalong.json in %AppData%\Smart Tools). Device name only, no paths.</summary>
    public sealed class MidiPlayAlongSettings
    {
        public string OutputDevice { get; set; } = "";
        public bool Metronome { get; set; } = true;
        public int ClickVolume { get; set; } = 100;
        public int CountInBars { get; set; } = 1;
        public int TempoPercent { get; set; } = 100;
        public bool Loop { get; set; }
    }
}
