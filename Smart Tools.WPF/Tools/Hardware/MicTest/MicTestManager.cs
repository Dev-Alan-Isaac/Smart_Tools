using NAudio.Wave;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Behind MicTestView: opens the microphone, measures the level, and optionally plays it back so
    /// you can hear it. Nothing is recorded or saved. The view reads <see cref="Snapshot"/> on a timer.
    ///
    /// Threads: buffers arrive on NAudio's capture thread and update the numbers behind a lock.
    /// WaveInEvent is created on the UI thread, so <see cref="Stopped"/> comes back on it.
    /// </summary>
    public sealed partial class MicTestManager : IDisposable
    {
        private static readonly WaveFormat Format = new(44100, 16, 1);

        private readonly object _gate = new();

        private WaveInEvent? _waveIn;
        private BufferedWaveProvider? _hearBuffer;
        private WaveOutEvent? _hearOut;
        private volatile bool _hear;

        private double _current;
        private double _peak;
        private double _quietest;
        private int _clipped;
        private int _buffers;
        private DateTime _startedAt;

        public bool IsRunning => _waveIn != null;

        /// <summary>The microphone stopped: null when you stopped it, else why (unplugged, taken by another app). UI thread.</summary>
        public event Action<string?>? Stopped;

        /// <summary>Opens the microphone. Throws (e.g. MmException) if it won't open; nothing is left running then.</summary>
        public void Start(MicRecorderDevice device, bool hearYourself)
        {
            if (_waveIn != null) throw new InvalidOperationException("Already running.");
            var waveIn = new WaveInEvent { DeviceNumber = device.Number, WaveFormat = Format, BufferMilliseconds = 50 };
            lock (_gate)
            {
                _current = _peak = 0;
                _quietest = 100;
                _clipped = _buffers = 0;
                _startedAt = DateTime.Now;
            }
            _hear = hearYourself;
            try
            {
                waveIn.DataAvailable += WaveIn_DataAvailable;
                waveIn.RecordingStopped += WaveIn_RecordingStopped;
                if (hearYourself) StartHearing();
                waveIn.StartRecording();
            }
            catch
            {
                waveIn.DataAvailable -= WaveIn_DataAvailable;
                waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                waveIn.Dispose();
                StopHearing();
                throw;
            }
            _waveIn = waveIn;
        }

        /// <summary>Asks the driver to stop; <see cref="Stopped"/> follows.</summary>
        public void Stop()
        {
            try { _waveIn?.StopRecording(); }
            catch (NAudio.MmException) { /* already gone */ }
        }

        /// <summary>Forgets the loudest sound so far, so you can test again without stopping.</summary>
        public void ResetPeak()
        {
            lock (_gate)
            {
                _peak = 0;
                _quietest = 100;
                _clipped = 0;
                _buffers = 0;
                _startedAt = DateTime.Now;
            }
        }

        public MicTestSnapshot Snapshot()
        {
            lock (_gate)
            {
                // Until a few buffers have arrived there is no "quietest moment" to report.
                double quietest = _buffers < 5 ? 0 : _quietest;
                return new MicTestSnapshot(_current, _peak, quietest, _clipped,
                    _waveIn == null ? TimeSpan.Zero : DateTime.Now - _startedAt);
            }
        }

        public void SetHear(bool on)
        {
            _hear = on;
            if (!on) StopHearing();
            else if (_waveIn != null && _hearOut == null) StartHearing();
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            double percent = MicRecorderManager.PeakLevel(e.Buffer, e.BytesRecorded, Format.BitsPerSample);
            lock (_gate)
            {
                _current = percent;
                _buffers++;
                if (percent > _peak) _peak = percent;
                if (_buffers >= 5 && percent < _quietest) _quietest = percent;
                if (percent >= ClipPercent) _clipped++;
            }
            if (_hear) _hearBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }

        private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (sender is WaveInEvent waveIn)
            {
                waveIn.DataAvailable -= WaveIn_DataAvailable;
                waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                waveIn.Dispose();
            }
            StopHearing();
            _waveIn = null;
            lock (_gate) _current = 0;
            Stopped?.Invoke(e.Exception?.Message);
        }

        private void StartHearing()
        {
            var buffer = new BufferedWaveProvider(Format, TimeSpan.FromSeconds(1)) { DiscardOnBufferOverflow = true };
            var output = new WaveOutEvent { BufferMilliseconds = 60 };
            output.Init(buffer);
            output.Play();
            _hearBuffer = buffer;
            _hearOut = output;
        }

        private void StopHearing()
        {
            var output = _hearOut;
            _hearOut = null;
            _hearBuffer = null;
            output?.Stop();
            output?.Dispose();
        }

        /// <summary>Closing the app or leaving the tool: stops at once and lets go of the microphone.</summary>
        public void Dispose()
        {
            var waveIn = _waveIn;
            if (waveIn != null)
            {
                waveIn.DataAvailable -= WaveIn_DataAvailable;
                waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                try { waveIn.StopRecording(); }
                catch (NAudio.MmException) { /* already gone */ }
                waveIn.Dispose();
                _waveIn = null;
            }
            StopHearing();
        }
    }
}
