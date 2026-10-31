using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Behind InstrumentTunerView: listens to a microphone through NAudio,
    /// feeds the analyzer (InstrumentTunerManager.Pitch.cs) and raises one
    /// reading about 20 times a second. Also plays a reference tone so a
    /// string can be tuned by ear.
    ///
    /// Readings are raised on NAudio's capture thread - the view marshals
    /// with BeginInvoke (the old view used Invoke, which waits on the UI
    /// thread and can deadlock while stopping).
    /// </summary>
    public sealed partial class InstrumentTunerManager : IDisposable
    {
        private readonly InstrumentTunerAnalyzer _analyzer = new();
        private WaveInEvent? _waveIn;
        private WaveOutEvent? _toneOut;

        public bool IsListening => _waveIn != null;

        /// <summary>A new analysis (capture thread).</summary>
        public event Action<InstrumentTunerReading>? ReadingAvailable;

        /// <summary>The microphone stopped by itself (unplugged, driver error). Raised on the UI thread.</summary>
        public event Action<string>? Failed;

        public InstrumentTunerSensitivity Sensitivity
        {
            get => _analyzer.Sensitivity;
            set => _analyzer.Sensitivity = value;
        }

        public static IReadOnlyList<InstrumentTunerDevice> Devices()
        {
            var list = new List<InstrumentTunerDevice>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                list.Add(new InstrumentTunerDevice(i, WaveInEvent.GetCapabilities(i).ProductName));
            return list;
        }

        /// <summary>Starts listening. Throws (e.g. MmException) if the microphone won't open; nothing is left running then.</summary>
        public void Start(InstrumentTunerDevice device)
        {
            if (IsListening) return;
            _analyzer.Reset();
            var waveIn = new WaveInEvent
            {
                DeviceNumber = device.Number,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 40,
            };
            waveIn.DataAvailable += WaveIn_DataAvailable;
            waveIn.RecordingStopped += WaveIn_RecordingStopped;
            try
            {
                waveIn.StartRecording();
            }
            catch
            {
                waveIn.DataAvailable -= WaveIn_DataAvailable;
                waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                waveIn.Dispose();
                throw;
            }
            _waveIn = waveIn;
        }

        /// <summary>
        /// Stops at once: the device is let go here, not later when the driver
        /// reports back. (The old Stop waited for that report, so pressing
        /// Start again straight away was silently ignored while the page said
        /// "Listening".)
        /// </summary>
        public void Stop()
        {
            var waveIn = _waveIn;
            _waveIn = null;
            if (waveIn == null) return;
            waveIn.DataAvailable -= WaveIn_DataAvailable;
            waveIn.RecordingStopped -= WaveIn_RecordingStopped;
            try { waveIn.StopRecording(); } catch (NAudio.MmException) { /* already gone */ }
            waveIn.Dispose();
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            foreach (var reading in _analyzer.PushPcm16(e.Buffer, e.BytesRecorded))
                ReadingAvailable?.Invoke(reading);
        }

        private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (!ReferenceEquals(sender, _waveIn)) return;
            Stop();
            Failed?.Invoke(e.Exception == null ? "The microphone stopped." : "The microphone stopped: " + e.Exception.Message);
        }

        // =================================================================
        //  Reference tone
        // =================================================================

        public bool IsPlayingTone => _toneOut != null;

        /// <summary>Plays a soft tone at a frequency until StopTone (a new tone replaces the old one).</summary>
        public void PlayTone(double frequencyHz)
        {
            StopTone();
            var tone = new SignalGenerator(SampleRate, 1)
            {
                Type = SignalGeneratorType.Sin,
                Frequency = frequencyHz,
                Gain = 0.18,
            };
            var output = new WaveOutEvent { BufferMilliseconds = 60 };   // NAudio 3: per buffer (2 x 60 ms ≈ the old 120 ms)
            try
            {
                output.Init(tone);
                output.Play();
            }
            catch
            {
                output.Dispose();
                throw;
            }
            _toneOut = output;
        }

        public void StopTone()
        {
            var output = _toneOut;
            _toneOut = null;
            output?.Stop();
            output?.Dispose();
        }

        public void Dispose()
        {
            Stop();
            StopTone();
        }
    }
}
