using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Instrument Tuner: the pure parts - pitch detection (YIN), note
    /// naming, the analysis window, needle smoothing, "in tune" detection,
    /// presets and settings. No NAudio, no WPF, so it's unit-tested with
    /// generated tones.
    /// </summary>
    public sealed partial class InstrumentTunerManager
    {
        public const int SampleRate = 44100;
        public const int WindowSize = 4096;

        /// <summary>Analyse every 2048 new samples (~21 times a second) - the old engine re-analysed after every single sample.</summary>
        public const int HopSize = 2048;

        public const double MinFrequency = 30;     // below a 5-string bass's low B (30.9 Hz)
        public const double MaxFrequency = 1500;   // above a violin's top string's high positions
        public const double MinA4 = 415, MaxA4 = 466;

        private static readonly string[] Letters = { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" };
        private static readonly string[] Solfeges = { "Do", "Do♯", "Re", "Re♯", "Mi", "Fa", "Fa♯", "Sol", "Sol♯", "La", "La♯", "Si" };

        // =================================================================
        //  Naming
        // =================================================================

        public static double FrequencyOf(int midiNote, double a4Hz) => a4Hz * Math.Pow(2, (midiNote - 69) / 12.0);

        public static string LetterName(int midiNote) => Letters[((midiNote % 12) + 12) % 12] + (midiNote / 12 - 1);

        public static string SolfegeName(int midiNote) => Solfeges[((midiNote % 12) + 12) % 12] + (midiNote / 12 - 1);

        public static string NoteName(int midiNote, InstrumentTunerNotation notation) =>
            notation == InstrumentTunerNotation.Solfege ? SolfegeName(midiNote) : LetterName(midiNote);

        /// <summary>The nearest equal-tempered note to a frequency, and how far off it is in cents (-50..+50).</summary>
        public static InstrumentTunerNote Name(double frequencyHz, double a4Hz)
        {
            if (frequencyHz <= 0 || !double.IsFinite(frequencyHz)) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
            double midi = 69 + 12 * Math.Log2(frequencyHz / a4Hz);
            int nearest = (int)Math.Round(midi, MidpointRounding.AwayFromZero);
            int pitchClass = ((nearest % 12) + 12) % 12;
            return new InstrumentTunerNote(
                nearest,
                Letters[pitchClass],
                Solfeges[pitchClass],
                (int)Math.Floor(nearest / 12.0) - 1,
                (midi - nearest) * 100,
                frequencyHz,
                FrequencyOf(nearest, a4Hz));
        }

        /// <summary>Cents from one frequency to another (positive = sharp).</summary>
        public static double CentsBetween(double frequencyHz, double targetHz) => 1200 * Math.Log2(frequencyHz / targetHz);

        // =================================================================
        //  Pitch detection (YIN)
        // =================================================================

        /// <summary>
        /// YIN: difference function, cumulative-mean normalisation, the first
        /// dip under the threshold (falling back to the deepest dip if it's
        /// still convincing), then parabolic interpolation. Searches 30-1500
        /// Hz - the old search stopped at 60 Hz, so a bass's E1 (41 Hz) and A1
        /// (55 Hz) were never found. Returns null when there's no clear note.
        /// </summary>
        public static double? DetectPitch(float[] buffer, int sampleRate = SampleRate, double threshold = 0.15)
        {
            int half = buffer.Length / 2;
            int minLag = Math.Max(2, (int)(sampleRate / MaxFrequency));
            int maxLag = Math.Min(half - 2, (int)Math.Ceiling(sampleRate / MinFrequency));
            if (maxLag <= minLag) return null;

            var diff = new double[maxLag + 2];
            for (int lag = 1; lag <= maxLag + 1; lag++)
            {
                double sum = 0;
                for (int i = 0; i < half; i++)
                {
                    double d = buffer[i] - buffer[i + lag];
                    sum += d * d;
                }
                diff[lag] = sum;
            }

            var cmnd = new double[maxLag + 2];
            cmnd[0] = 1;
            double running = 0;
            for (int lag = 1; lag <= maxLag + 1; lag++)
            {
                running += diff[lag];
                cmnd[lag] = running <= 0 ? 1 : diff[lag] * lag / running;
            }

            int best = -1;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                if (cmnd[lag] < threshold)
                {
                    while (lag + 1 <= maxLag && cmnd[lag + 1] < cmnd[lag]) lag++;
                    best = lag;
                    break;
                }
            }
            if (best < 0)
            {
                // No dip under the threshold: accept the deepest one only if it's still fairly clear.
                int deepest = minLag;
                for (int lag = minLag + 1; lag <= maxLag; lag++) if (cmnd[lag] < cmnd[deepest]) deepest = lag;
                if (cmnd[deepest] > 0.3) return null;
                best = deepest;
            }

            double refined = best;
            double s0 = cmnd[best - 1], s1 = cmnd[best], s2 = cmnd[best + 1];
            double denom = s0 - 2 * s1 + s2;
            if (Math.Abs(denom) > 1e-12) refined = best + (s0 - s2) / (2 * denom);
            if (refined <= 0) return null;

            double f = sampleRate / refined;
            return f is >= MinFrequency * 0.97 and <= MaxFrequency * 1.03 ? f : null;
        }

        public static double Rms(float[] buffer)
        {
            double sum = 0;
            foreach (float s in buffer) sum += s * (double)s;
            return buffer.Length == 0 ? 0 : Math.Sqrt(sum / buffer.Length);
        }

        /// <summary>The quietest level (RMS) the tuner reacts to.</summary>
        public static double SilenceThreshold(InstrumentTunerSensitivity s) => s switch
        {
            InstrumentTunerSensitivity.Low => 0.02,
            InstrumentTunerSensitivity.High => 0.003,
            _ => 0.008,
        };

        // =================================================================
        //  Presets and strings
        // =================================================================

        private static InstrumentTunerPreset P(string name, string icon, params int[] midi) =>
            new(name, icon, midi.Select(m => new InstrumentTunerString(LetterName(m), m)).ToList());

        public static readonly IReadOnlyList<InstrumentTunerPreset> Presets = new[]
        {
            P("Chromatic", "MusicNote"),
            P("Guitar (standard)", "GuitarAcoustic", 40, 45, 50, 55, 59, 64),
            P("Guitar (drop D)", "GuitarAcoustic", 38, 45, 50, 55, 59, 64),
            P("Bass (4 strings)", "GuitarElectric", 28, 33, 38, 43),
            P("Bass (5 strings)", "GuitarElectric", 23, 28, 33, 38, 43),
            P("Ukulele", "GuitarAcoustic", 67, 60, 64, 69),
            P("Violin", "Violin", 55, 62, 69, 76),
            P("Viola", "Violin", 48, 55, 62, 69),
            P("Cello", "Violin", 36, 43, 50, 57),
            P("Mandolin", "GuitarAcoustic", 55, 62, 69, 76),
            P("Banjo (open G)", "GuitarAcoustic", 67, 50, 55, 59, 62),
        };

        public static InstrumentTunerPreset FindPreset(string? name) =>
            Presets.FirstOrDefault(p => p.Name == name) ?? Presets[0];

        /// <summary>
        /// The string a frequency is closest to (in semitones, so it's fair
        /// across octaves) and how many cents off that string it is. -1 for a
        /// chromatic preset.
        /// </summary>
        public static (int Index, double Cents) NearestString(InstrumentTunerPreset preset, double frequencyHz, double a4Hz)
        {
            int best = -1;
            double bestDistance = double.MaxValue, bestCents = 0;
            for (int i = 0; i < preset.Strings.Count; i++)
            {
                double cents = CentsBetween(frequencyHz, FrequencyOf(preset.Strings[i].MidiNote, a4Hz));
                if (Math.Abs(cents) < bestDistance)
                {
                    bestDistance = Math.Abs(cents);
                    best = i;
                    bestCents = cents;
                }
            }
            return (best, bestCents);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "instrument_tuner.json");

        public static InstrumentTunerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<InstrumentTunerSettings>(File.ReadAllText(file), JsonOptions) : null;
                return Sanitize(s ?? new InstrumentTunerSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new InstrumentTunerSettings();
            }
        }

        public static InstrumentTunerSettings Sanitize(InstrumentTunerSettings s)
        {
            s.A4Hz = double.IsFinite(s.A4Hz) ? Math.Clamp(Math.Round(s.A4Hz), MinA4, MaxA4) : 440;
            if (!Enum.IsDefined(s.Notation)) s.Notation = InstrumentTunerNotation.Letters;
            if (!Enum.IsDefined(s.Sensitivity)) s.Sensitivity = InstrumentTunerSensitivity.Normal;
            s.Preset = FindPreset(s.Preset).Name;
            s.DeviceName ??= "";
            return s;
        }

        public static void SaveSettings(InstrumentTunerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(Sanitize(settings), JsonOptions));
        }

        public static InstrumentTunerDevice? PreferredDevice(IReadOnlyList<InstrumentTunerDevice> devices, string? savedName) =>
            devices.FirstOrDefault(d => d.Name == savedName) ?? devices.FirstOrDefault();
    }

    /// <summary>
    /// Collects incoming samples in a rolling window and analyses it every
    /// <see cref="InstrumentTunerManager.HopSize"/> samples. The old engine
    /// shifted the whole 4096-sample window and re-ran YIN for every new
    /// sample - about 44,000 analyses a second, each raising an event that
    /// blocked on the UI thread.
    /// </summary>
    public sealed class InstrumentTunerAnalyzer
    {
        private readonly float[] _ring;
        private readonly float[] _linear;
        private readonly int _hop;
        private int _write;
        private int _filled;
        private int _sinceLast;

        public InstrumentTunerAnalyzer(int windowSize = InstrumentTunerManager.WindowSize, int hop = InstrumentTunerManager.HopSize)
        {
            _ring = new float[windowSize];
            _linear = new float[windowSize];
            _hop = hop;
        }

        public InstrumentTunerSensitivity Sensitivity { get; set; } = InstrumentTunerSensitivity.Normal;

        /// <summary>Adds samples; returns one reading per completed hop (usually 0 or 1 per audio buffer).</summary>
        public List<InstrumentTunerReading> Push(ReadOnlySpan<float> samples)
        {
            var readings = new List<InstrumentTunerReading>();
            foreach (float s in samples)
            {
                _ring[_write] = s;
                if (++_write == _ring.Length) _write = 0;
                if (_filled < _ring.Length) _filled++;
                if (++_sinceLast >= _hop && _filled == _ring.Length)
                {
                    _sinceLast = 0;
                    readings.Add(Analyze());
                }
            }
            return readings;
        }

        /// <summary>16-bit little-endian mono PCM, as the microphone delivers it.</summary>
        public List<InstrumentTunerReading> PushPcm16(byte[] buffer, int count)
        {
            var floats = new float[count / 2];
            for (int i = 0; i < floats.Length; i++) floats[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            return Push(floats);
        }

        public void Reset()
        {
            Array.Clear(_ring);
            _write = _filled = _sinceLast = 0;
        }

        private InstrumentTunerReading Analyze()
        {
            int n = _ring.Length;
            Array.Copy(_ring, _write, _linear, 0, n - _write);   // oldest first
            Array.Copy(_ring, 0, _linear, n - _write, _write);
            double rms = InstrumentTunerManager.Rms(_linear);
            if (rms < InstrumentTunerManager.SilenceThreshold(Sensitivity)) return new InstrumentTunerReading(null, rms);
            return new InstrumentTunerReading(InstrumentTunerManager.DetectPitch(_linear), rms);
        }
    }

    /// <summary>
    /// Smooths the needle so it doesn't jitter, but starts fresh when the
    /// note changes - the old smoothing carried on across notes, so moving
    /// from a sharp A to a flat B swept the needle across the whole dial.
    /// </summary>
    public sealed class InstrumentTunerNeedle
    {
        private const double Alpha = 0.35;
        private int? _note;
        private double _cents;

        public double Update(int midiNote, double cents)
        {
            cents = Math.Clamp(cents, -50, 50);
            if (_note != midiNote) _cents = cents;
            else _cents = _cents * (1 - Alpha) + cents * Alpha;
            _note = midiNote;
            return _cents;
        }

        public void Reset() => _note = null;
    }

    /// <summary>
    /// "In tune" that doesn't flicker: on after 3 readings in a row within
    /// ±5 cents (about a seventh of a second), off once it drifts past ±8.
    /// </summary>
    public sealed class InstrumentTunerInTune
    {
        public const double OnCents = 5, OffCents = 8;
        public const int ReadingsNeeded = 3;
        private int _streak;

        public bool IsInTune { get; private set; }

        public bool Update(double cents)
        {
            double a = Math.Abs(cents);
            if (IsInTune)
            {
                if (a > OffCents)
                {
                    IsInTune = false;
                    _streak = 0;
                }
            }
            else
            {
                _streak = a <= OnCents ? _streak + 1 : 0;
                if (_streak >= ReadingsNeeded) IsInTune = true;
            }
            return IsInTune;
        }

        public void Reset()
        {
            IsInTune = false;
            _streak = 0;
        }
    }
}
