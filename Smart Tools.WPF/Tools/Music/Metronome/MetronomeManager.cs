namespace Smart_Tools.WPF.Controllers
{
    public sealed class MetronomeSettings
    {
        public int Bpm { get; set; } = 100;
        public int BeatsPerBar { get; set; } = 4;
        public bool AccentFirst { get; set; } = true;
        public int Volume { get; set; } = 70;
    }

    /// <summary>
    /// Metronome logic: tempo limits, tap tempo, and one bar of click sound as 16-bit mono PCM (a whole number of
    /// samples per beat, so the loop never drifts). The audio device and the screen are the view's. No WPF types.
    /// </summary>
    public static class MetronomeManager
    {
        public const string SettingsName = "metronome";
        public const int MinBpm = 20, MaxBpm = 300;
        public const int SampleRate = 44100;
        public static readonly int[] BeatChoices = { 1, 2, 3, 4, 5, 6, 7, 8 };

        public static MetronomeSettings Clamp(MetronomeSettings s)
        {
            s.Bpm = Math.Clamp(s.Bpm, MinBpm, MaxBpm);
            s.BeatsPerBar = BeatChoices.Contains(s.BeatsPerBar) ? s.BeatsPerBar : 4;
            s.Volume = Math.Clamp(s.Volume, 0, 100);
            return s;
        }

        /// <summary>Samples in one beat (rounded, so the bar is exactly beats x this).</summary>
        public static int SamplesPerBeat(int bpm) => (int)Math.Round(SampleRate * 60.0 / Math.Clamp(bpm, MinBpm, MaxBpm));

        /// <summary>The italian word for a tempo, to show next to the number.</summary>
        public static string TempoName(int bpm) => bpm switch
        {
            < 40 => "Grave",
            < 60 => "Largo",
            < 66 => "Larghetto",
            < 76 => "Adagio",
            < 108 => "Andante",
            < 120 => "Moderato",
            < 156 => "Allegro",
            < 176 => "Vivace",
            < 200 => "Presto",
            _ => "Prestissimo",
        };

        /// <summary>One bar of clicks: a higher, louder click on beat 1 when <paramref name="accentFirst"/> is on.</summary>
        public static byte[] BuildBar(int bpm, int beats, bool accentFirst, int volumePercent)
        {
            beats = Math.Clamp(beats, 1, 16);
            int perBeat = SamplesPerBeat(bpm);
            double gain = Math.Clamp(volumePercent, 0, 100) / 100.0;
            var pcm = new byte[perBeat * beats * 2];
            int clickLength = Math.Min(perBeat, SampleRate * 45 / 1000);   // 45 ms of sound, then silence

            for (int beat = 0; beat < beats; beat++)
            {
                bool accent = accentFirst && beat == 0;
                double frequency = accent ? 1568 : 1046;   // G6 / C6
                double level = (accent ? 0.95 : 0.65) * gain;
                for (int n = 0; n < clickLength; n++)
                {
                    double envelope = Math.Exp(-n / (SampleRate * 0.008));      // quick fade, sounds like a tick
                    double sample = Math.Sin(2 * Math.PI * frequency * n / SampleRate) * envelope * level;
                    short value = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                    int index = (beat * perBeat + n) * 2;
                    pcm[index] = (byte)(value & 0xFF);
                    pcm[index + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
            return pcm;
        }

        /// <summary>Which beat (0-based) a playback position falls on.</summary>
        public static int BeatAt(long positionBytes, int bpm, int beats)
        {
            int bytesPerBeat = SamplesPerBeat(bpm) * 2;
            if (bytesPerBeat <= 0 || beats <= 0 || positionBytes < 0) return 0;
            return (int)(positionBytes / bytesPerBeat % beats);
        }

        /// <summary>
        /// Tap tempo: call <see cref="Tap"/> on each tap. Taps more than 2.5 s apart start over; the tempo is the
        /// average of up to the last 8 gaps, so one slow tap doesn't throw it off.
        /// </summary>
        public sealed class TapTempo
        {
            private readonly List<long> _ticks = new();
            private const int MaxGaps = 8;
            private static readonly long ResetAfter = TimeSpan.FromSeconds(2.5).Ticks;

            public int TapCount => _ticks.Count;

            /// <summary>The tempo after this tap, or null until there are two taps.</summary>
            public int? Tap(long nowTicks)
            {
                if (_ticks.Count > 0 && nowTicks - _ticks[^1] > ResetAfter) _ticks.Clear();
                _ticks.Add(nowTicks);
                if (_ticks.Count > MaxGaps + 1) _ticks.RemoveAt(0);
                if (_ticks.Count < 2) return null;
                double averageGap = (double)(_ticks[^1] - _ticks[0]) / (_ticks.Count - 1);
                if (averageGap <= 0) return null;
                return Math.Clamp((int)Math.Round(TimeSpan.TicksPerMinute / averageGap), MinBpm, MaxBpm);
            }

            public void Reset() => _ticks.Clear();
        }
    }
}
