using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The test signals for the Audio Output Tester: pure maths, no audio API, so it compiles and is
    /// tested in a plain console project. The audio thread calls <see cref="Fill"/>; the UI thread calls
    /// <see cref="Configure"/> and reads the meters and the "what is playing now" properties.
    /// The output always starts and stops through a short fade, so switching a test never pops.
    /// </summary>
    public sealed class AudioSignalGenerator
    {
        public const double ChannelStepSeconds = 2.6;
        public const double BeepSeconds = 0.12;
        public const double AlternateSeconds = 3.0;
        public const double ClickPeriodSeconds = 1.0;
        public const double SweepLow = 20, SweepHigh = 20000;
        public const double PhaseToneHz = 300;
        private const double FadeSeconds = 0.03;

        private readonly int _rate;
        private readonly int _channels;
        private readonly Random _random = new(12345);
        private readonly float[] _peaks;
        private readonly float[] _localPeaks;
        private readonly PinkState[] _pink;

        private AudioTestConfig _config = AudioTestConfig.Default;
        private volatile bool _playing;
        private double _gain;
        private double _phase;
        private double _sweepTime;
        private long _frame;          // frames since the current test began
        private volatile int _activeChannel = -1;
        private volatile int _phaseState;   // 0 = in phase, 1 = out of phase
        private AudioTestKind _lastKind = AudioTestKind.Channels;

        public AudioSignalGenerator(int sampleRate, int channels)
        {
            _rate = sampleRate;
            _channels = Math.Max(1, channels);
            _peaks = new float[_channels];
            _localPeaks = new float[_channels];
            _pink = new PinkState[_channels];
        }

        public int Channels => _channels;

        /// <summary>The channel being identified right now (Channels test), or -1.</summary>
        public int ActiveChannel => _activeChannel;

        /// <summary>0 = in phase, 1 = out of phase / the second click type (Phase test).</summary>
        public int PhaseState => _phaseState;

        public bool IsPlaying
        {
            get => _playing;
            set => _playing = value;
        }

        /// <summary>The newest settings; safe to call from any thread.</summary>
        public void Configure(AudioTestConfig config) => Volatile.Write(ref _config, config);

        /// <summary>The loudest sample on a channel since the last time this was asked; resets it.</summary>
        public float TakePeak(int channel)
        {
            if (channel < 0 || channel >= _channels) return 0;
            return Interlocked.Exchange(ref _peaks[channel], 0f);
        }

        // =================================================================
        //  The audio thread
        // =================================================================

        /// <summary>Fills <paramref name="buffer"/> with interleaved frames.</summary>
        public void Fill(Span<float> buffer)
        {
            var cfg = Volatile.Read(ref _config);
            if (cfg.Kind != _lastKind)
            {
                _lastKind = cfg.Kind;
                _frame = 0;
                _sweepTime = 0;
                _phase = 0;
            }

            double amp = LevelToAmplitude(cfg.LevelDb);
            double fadeStep = 1.0 / (FadeSeconds * _rate);
            int frames = buffer.Length / _channels;
            var localPeaks = _localPeaks;
            Array.Clear(localPeaks);

            for (int f = 0; f < frames; f++)
            {
                double target = _playing ? 1 : 0;
                _gain += Math.Clamp(target - _gain, -fadeStep, fadeStep);

                double time = _frame / (double)_rate;
                int o = f * _channels;
                for (int ch = 0; ch < _channels; ch++) buffer[o + ch] = 0;

                if (_gain > 0 || _playing)
                {
                    switch (cfg.Kind)
                    {
                        case AudioTestKind.Tone:
                            WriteTone(buffer, o, cfg, amp);
                            break;
                        case AudioTestKind.Sweep:
                            WriteSweep(buffer, o, cfg, amp);
                            break;
                        case AudioTestKind.Noise:
                            WriteNoise(buffer, o, cfg, amp);
                            break;
                        case AudioTestKind.Phase:
                            WritePhase(buffer, o, cfg, amp, time);
                            break;
                        default:
                            WriteChannels(buffer, o, cfg, amp, time);
                            break;
                    }
                }

                for (int ch = 0; ch < _channels; ch++)
                {
                    float v = (float)Math.Clamp(buffer[o + ch] * _gain, -1.0, 1.0);
                    buffer[o + ch] = v;
                    float a = Math.Abs(v);
                    if (a > localPeaks[ch]) localPeaks[ch] = a;
                }
                _frame++;
            }

            for (int ch = 0; ch < _channels; ch++)
                if (localPeaks[ch] > Volatile.Read(ref _peaks[ch])) Volatile.Write(ref _peaks[ch], localPeaks[ch]);

            if (!_playing && _gain <= 0)
            {
                _activeChannel = -1;
                _frame = 0;
            }
        }

        // =================================================================
        //  Signals
        // =================================================================

        private static bool On(AudioTestConfig cfg, int ch) => ch < 32 && (cfg.ChannelMask & (1u << ch)) != 0;

        private void WriteTone(Span<float> b, int o, AudioTestConfig cfg, double amp)
        {
            double s = Oscillator(cfg.Waveform, _phase) * amp;
            _phase += 2 * Math.PI * cfg.Frequency / _rate;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            for (int ch = 0; ch < _channels; ch++) if (On(cfg, ch)) b[o + ch] = (float)s;
        }

        private void WriteSweep(Span<float> b, int o, AudioTestConfig cfg, double amp)
        {
            double length = Math.Max(2, cfg.SweepSeconds);
            double pause = 0.6;
            double t = _sweepTime;
            double s = 0;
            if (t < length)
            {
                double freq = SweepLow * Math.Pow(SweepHigh / SweepLow, t / length);
                _phase += 2 * Math.PI * freq / _rate;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                s = Math.Sin(_phase) * amp;
            }
            _sweepTime += 1.0 / _rate;
            if (_sweepTime > length + pause) _sweepTime = 0;
            for (int ch = 0; ch < _channels; ch++) if (On(cfg, ch)) b[o + ch] = (float)s;
        }

        private void WriteNoise(Span<float> b, int o, AudioTestConfig cfg, double amp)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                if (!On(cfg, ch)) continue;
                double white = _random.NextDouble() * 2 - 1;
                double v = cfg.Noise == AudioNoiseColor.White ? white * WhiteScale : _pink[ch].Next(white) * PinkScale;
                b[o + ch] = (float)(v * amp);
            }
        }

        private void WriteChannels(Span<float> b, int o, AudioTestConfig cfg, double amp, double time)
        {
            // The channels that are switched on, in order. Each gets a step; channel N beeps N times.
            Span<int> order = stackalloc int[_channels];
            int n = 0;
            for (int ch = 0; ch < _channels; ch++) if (On(cfg, ch)) order[n++] = ch;
            if (n == 0) { _activeChannel = -1; return; }

            int step = (int)(time / ChannelStepSeconds) % n;
            double within = time - Math.Floor(time / ChannelStepSeconds) * ChannelStepSeconds;
            int ch0 = order[step];
            int beeps = ch0 + 1;
            _activeChannel = ch0;

            double slot = BeepSeconds * 2;
            int k = (int)(within / slot);
            double inSlot = within - k * slot;
            if (k >= beeps || inSlot >= BeepSeconds) return;

            double env = Math.Sin(Math.PI * inSlot / BeepSeconds);   // smooth in and out
            double phase = 2 * Math.PI * 1000 * (within);
            b[o + ch0] = (float)(Math.Sin(phase) * env * amp);
        }

        private void WritePhase(Span<float> b, int o, AudioTestConfig cfg, double amp, double time)
        {
            int state = cfg.Phase switch
            {
                AudioPhaseMode.InPhase => 0,
                AudioPhaseMode.OutOfPhase => 1,
                _ => (int)(time / AlternateSeconds) % 2,
            };

            if (cfg.Phase == AudioPhaseMode.Click)
            {
                // One click a second, alternating the two wirings; each click is a 1 ms half-sine pulse.
                long period = (long)(ClickPeriodSeconds * _rate);
                long index = _frame / period;
                long into = _frame % period;
                int clickState = (int)(index % 2);
                _phaseState = clickState;
                int pulse = Math.Max(1, _rate / 1000);
                if (into < pulse)
                {
                    double p = Math.Sin(Math.PI * into / pulse) * amp;
                    for (int ch = 0; ch < _channels; ch++)
                        if (On(cfg, ch)) b[o + ch] = (float)(clickState == 1 && ch % 2 == 1 ? -p : p);
                }
                return;
            }

            _phaseState = state;
            double s = Math.Sin(_phase) * amp;
            _phase += 2 * Math.PI * PhaseToneHz / _rate;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            for (int ch = 0; ch < _channels; ch++)
                if (On(cfg, ch)) b[o + ch] = (float)(state == 1 && ch % 2 == 1 ? -s : s);
        }

        // =================================================================
        //  Helpers
        // =================================================================

        public static double LevelToAmplitude(double db) => Math.Pow(10, Math.Clamp(db, -80, -3) / 20.0);

        private static double Oscillator(AudioWaveform shape, double phase)
        {
            double x = phase / (2 * Math.PI);          // 0..1
            return shape switch
            {
                AudioWaveform.Square => x < 0.5 ? 0.7 : -0.7,           // lower peak: a square is much louder than a sine
                AudioWaveform.Triangle => (x < 0.5 ? 4 * x - 1 : 3 - 4 * x),
                AudioWaveform.Saw => (2 * x - 1) * 0.8,
                _ => Math.Sin(phase),
            };
        }

        /// <summary>Scales so that the noise has about the loudness of a sine at the same level.</summary>
        public const double WhiteScale = 1.2247;
        public const double PinkScale = 3.67;

        /// <summary>Paul Kellet's refined pink-noise filter; one per channel so the channels are independent.</summary>
        private struct PinkState
        {
            private double _b0, _b1, _b2, _b3, _b4, _b5, _b6;

            public double Next(double white)
            {
                _b0 = 0.99886 * _b0 + white * 0.0555179;
                _b1 = 0.99332 * _b1 + white * 0.0750759;
                _b2 = 0.96900 * _b2 + white * 0.1538520;
                _b3 = 0.86650 * _b3 + white * 0.3104856;
                _b4 = 0.55000 * _b4 + white * 0.5329522;
                _b5 = -0.7616 * _b5 - white * 0.0168980;
                double pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362;
                _b6 = white * 0.115926;
                return pink * 0.11;
            }
        }
    }
}
