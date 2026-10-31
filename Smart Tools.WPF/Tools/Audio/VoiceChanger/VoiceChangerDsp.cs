using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  Voice Changer sound processing - plain C#, no NAudio, no WPF.
    //  The same per-sample effects serve both paths: a whole file at once
    //  (VoiceChangerDsp.Apply) and the live microphone (VoiceChangerChain,
    //  wrapped in an NAudio sample provider by VoiceChangerManager).
    //  Order: pitch -> robot -> echo.
    // =====================================================================

    /// <summary>
    /// Pitch shift without changing speed: a short delay line read by two
    /// taps whose delay sweeps at (1 - ratio) per sample, half a window
    /// apart, cross-faded with triangle windows that always add up to 1.
    /// One instance per channel.
    /// </summary>
    public sealed class VoiceChangerPitchShifter
    {
        private readonly float[] _buffer;
        private readonly double _window;
        private int _write;
        private double _phase;
        private double _ratio = 1;

        public VoiceChangerPitchShifter(int sampleRate, double windowMs = 60)
        {
            _window = Math.Max(64, sampleRate * windowMs / 1000.0);
            _buffer = new float[(int)_window * 2 + 4];
        }

        /// <summary>Semitones, -12..+12 (12 = an octave). 0 passes the sound through untouched.</summary>
        public double Semitones
        {
            set => _ratio = Math.Pow(2, Math.Clamp(value, -24, 24) / 12.0);
        }

        public float Process(float x)
        {
            _buffer[_write] = x;
            float y;
            if (Math.Abs(_ratio - 1) < 1e-6)
            {
                y = x;
            }
            else
            {
                double p2 = _phase + 0.5;
                if (p2 >= 1) p2 -= 1;
                double g1 = 1 - Math.Abs(2 * _phase - 1);   // 0 at the jump, 1 in the middle
                double g2 = 1 - Math.Abs(2 * p2 - 1);
                y = (float)(Read(_phase * _window) * g1 + Read(p2 * _window) * g2);

                _phase += (1 - _ratio) / _window;
                _phase -= Math.Floor(_phase);   // keep in [0, 1) both ways
            }
            if (++_write == _buffer.Length) _write = 0;
            return y;
        }

        /// <summary>The sample <paramref name="delay"/> samples ago, linearly interpolated.</summary>
        private float Read(double delay)
        {
            double pos = _write - delay;
            while (pos < 0) pos += _buffer.Length;
            int i = (int)pos;
            double frac = pos - i;
            int j = i + 1 == _buffer.Length ? 0 : i + 1;
            // j is one sample newer than i; reading "delay" back means between i and i+1
            return (float)(_buffer[i] * (1 - frac) + _buffer[j] * frac);
        }
    }

    /// <summary>Robot voice: ring modulation against a low sine - the classic buzzy, metallic sound.</summary>
    public sealed class VoiceChangerRobot
    {
        public const double CarrierHz = 50;
        private readonly double _step;
        private double _angle;

        public VoiceChangerRobot(int sampleRate) => _step = 2 * Math.PI * CarrierHz / sampleRate;

        public bool Enabled { get; set; }

        public float Process(float x)
        {
            if (!Enabled) return x;
            float y = (float)(x * Math.Sin(_angle) * 1.4);   // ring mod halves the energy; bring it back up
            _angle += _step;
            if (_angle > 2 * Math.PI) _angle -= 2 * Math.PI;
            return y;
        }
    }

    /// <summary>Echo: a feedback delay - each repeat is the last one times Feedback; Mix sets how loud the repeats are.</summary>
    public sealed class VoiceChangerEcho
    {
        public const double MaxDelayMs = 600;
        private readonly float[] _buffer;
        private readonly int _sampleRate;
        private int _index;
        private int _delay;

        public VoiceChangerEcho(int sampleRate)
        {
            _sampleRate = sampleRate;
            _buffer = new float[(int)(sampleRate * MaxDelayMs / 1000) + 1];
            DelayMs = 220;
        }

        public bool Enabled { get; set; }
        public double Feedback { get; set; } = 0.35;
        public double Mix { get; set; } = 0.35;

        public double DelayMs
        {
            set => _delay = Math.Clamp((int)Math.Round(_sampleRate * Math.Clamp(value, 1, MaxDelayMs) / 1000), 1, _buffer.Length);
        }

        public float Process(float x)
        {
            if (!Enabled) return x;
            if (_index >= _delay) _index = 0;   // delay was shortened
            float delayed = _buffer[_index];
            _buffer[_index] = (float)(x + Math.Clamp(Feedback, 0, 0.95) * delayed);
            if (++_index >= _delay) _index = 0;
            return (float)(x + Math.Clamp(Mix, 0, 1) * delayed);
        }
    }

    /// <summary>
    /// The three effects for every channel of a stream. Settings can be
    /// changed from another thread while it runs (the live sliders): they're
    /// copied in under a lock and picked up at the next block.
    /// </summary>
    public sealed class VoiceChangerChain
    {
        private readonly object _gate = new();
        private readonly VoiceChangerPitchShifter[] _pitch;
        private readonly VoiceChangerRobot[] _robot;
        private readonly VoiceChangerEcho[] _echo;
        private VoiceChangerEffect? _pending;

        public int Channels { get; }

        public VoiceChangerChain(int sampleRate, int channels, VoiceChangerEffect effect)
        {
            Channels = Math.Max(1, channels);
            _pitch = Enumerable.Range(0, Channels).Select(_ => new VoiceChangerPitchShifter(sampleRate)).ToArray();
            _robot = Enumerable.Range(0, Channels).Select(_ => new VoiceChangerRobot(sampleRate)).ToArray();
            _echo = Enumerable.Range(0, Channels).Select(_ => new VoiceChangerEcho(sampleRate)).ToArray();
            ApplySettings(effect);
        }

        /// <summary>Any thread. Takes effect from the next block processed.</summary>
        public void Update(VoiceChangerEffect effect)
        {
            lock (_gate) _pending = effect;
        }

        private void ApplySettings(VoiceChangerEffect e)
        {
            for (int c = 0; c < Channels; c++)
            {
                _pitch[c].Semitones = e.PitchSemitones;
                _robot[c].Enabled = e.Robot;
                _echo[c].Enabled = e.Echo;
                _echo[c].DelayMs = e.EchoDelayMs;
                _echo[c].Feedback = e.EchoFeedback;
                _echo[c].Mix = e.EchoMix;
            }
        }

        /// <summary>Processes interleaved samples in place.</summary>
        public void Process(float[] samples, int offset, int count) => Process(samples.AsSpan(offset, count));

        /// <summary>Processes the samples in place (interleaved channels). The span form is what NAudio 3 hands a provider.</summary>
        public void Process(Span<float> samples)
        {
            VoiceChangerEffect? pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }
            if (pending != null) ApplySettings(pending);

            for (int i = 0; i < samples.Length; i++)
            {
                int c = i % Channels;
                float x = samples[i];
                x = _pitch[c].Process(x);
                x = _robot[c].Process(x);
                x = _echo[c].Process(x);
                samples[i] = x;
            }
        }
    }

    public static class VoiceChangerDsp
    {
        /// <summary>
        /// Changes a whole file's interleaved samples. With echo on, the
        /// result runs a little longer so the last repeats aren't cut off
        /// (until they fade below about -60 dB, at most 5 seconds).
        /// </summary>
        public static float[] Apply(float[] samples, int sampleRate, int channels, VoiceChangerEffect effect)
        {
            channels = Math.Max(1, channels);
            int tailFrames = EchoTailFrames(sampleRate, effect);
            var output = new float[samples.Length + tailFrames * channels];
            Array.Copy(samples, output, samples.Length);
            if (effect.IsNeutral) return output;
            new VoiceChangerChain(sampleRate, channels, effect).Process(output, 0, output.Length);
            return output;
        }

        /// <summary>How long the echo keeps ringing after the sound ends, in frames.</summary>
        public static int EchoTailFrames(int sampleRate, VoiceChangerEffect e)
        {
            if (!e.Echo || e.EchoMix <= 0) return 0;
            double delay = Math.Clamp(e.EchoDelayMs, 1, VoiceChangerEcho.MaxDelayMs) / 1000.0;
            double fb = Math.Clamp(e.EchoFeedback, 0, 0.95);
            int repeats = fb <= 0.001 ? 1 : (int)Math.Ceiling(Math.Log(0.001) / Math.Log(fb)) + 1;
            double seconds = Math.Min(5, delay * repeats);
            return (int)(seconds * sampleRate);
        }

        /// <summary>The quick voices.</summary>
        public static VoiceChangerEffect Preset(VoiceChangerPreset preset) => preset switch
        {
            VoiceChangerPreset.Chipmunk => new VoiceChangerEffect(7, false, false, 220, 0.35, 0.35),
            VoiceChangerPreset.DeepVoice => new VoiceChangerEffect(-5, false, false, 220, 0.35, 0.35),
            VoiceChangerPreset.Robot => new VoiceChangerEffect(0, true, false, 220, 0.35, 0.35),
            VoiceChangerPreset.CaveEcho => new VoiceChangerEffect(-1, false, true, 380, 0.55, 0.5),
            // Pitch-only voices: the shifter moves the whole sound, so these are friendly approximations, not true voice swaps.
            VoiceChangerPreset.Girl => new VoiceChangerEffect(4, false, false, 220, 0.35, 0.35),
            VoiceChangerPreset.Man => new VoiceChangerEffect(-4, false, false, 220, 0.35, 0.35),
            VoiceChangerPreset.Grandma => new VoiceChangerEffect(2, false, false, 220, 0.35, 0.35),
            VoiceChangerPreset.Grandpa => new VoiceChangerEffect(-7, false, false, 220, 0.35, 0.35),
            _ => VoiceChangerEffect.Neutral,
        };
    }
}
