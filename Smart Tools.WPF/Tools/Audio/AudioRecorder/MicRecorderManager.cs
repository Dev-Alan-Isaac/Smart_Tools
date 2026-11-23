using NAudio;
using NAudio.Wave;
using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Behind MicRecorderView: records the microphone to a WAV through NAudio
    /// (WaveInEvent -> WaveFileWriter), pauses without closing the device,
    /// optionally lets you hear the input, and plays takes back. The view
    /// only shows what's happening.
    ///
    /// Threads: buffers arrive on NAudio's capture thread - writing is behind
    /// a lock and LevelChanged is raised from there (the view marshals).
    /// WaveInEvent and WaveOutEvent are created on the UI thread, so their
    /// Stopped events come back on it.
    /// </summary>
    public sealed partial class MicRecorderManager : IDisposable
    {
        private readonly object _gate = new();

        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
        private string? _tempPath;
        private MicRecorderFormat? _format;
        private DateTime _startedAt;
        private long _dataBytes;
        private volatile bool _paused;

        // Waveform of the take: the loudest sample of every 1/250 s (0-255), grown while recording and handed to the finished take.
        private byte[] _livePeaks = new byte[AudioTrimmerPeaks.PerSecond * 120];
        private int _livePeakCount;
        private double _sliceFrames, _framesPerSlice = 1;
        private int _sliceMax;

        private BufferedWaveProvider? _monitorBuffer;
        private WaveOutEvent? _monitorOut;

        private WaveOutEvent? _playOut;
        private AudioFileReader? _playReader;
        private MicRecorderTake? _playing;

        public MicRecorderState State { get; private set; } = MicRecorderState.Idle;

        /// <summary>Input level for the meter, 0-100 (on a decibel scale). Raised on the capture thread, also while paused.</summary>
        public event Action<double>? LevelChanged;

        /// <summary>
        /// The driver has stopped: the finished take (null if nothing was
        /// recorded) and, if the device failed or was unplugged, why.
        /// Raised on the UI thread.
        /// </summary>
        public event Action<MicRecorderTake?, string?>? RecordingFinished;

        /// <summary>A take finished playing on its own. UI thread.</summary>
        public event Action<MicRecorderTake>? PlaybackEnded;

        public static IReadOnlyList<MicRecorderDevice> Devices()
        {
            var list = new List<MicRecorderDevice>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                list.Add(new MicRecorderDevice(i, WaveInEvent.GetCapabilities(i).ProductName));
            return list;
        }

        /// <summary>How much has been written so far (pauses don't count) - from bytes, not the clock.</summary>
        public TimeSpan Recorded
        {
            get
            {
                lock (_gate) return _format == null ? TimeSpan.Zero : DurationOf(_dataBytes, _format);
            }
        }

        /// <summary>The growing waveform of the take being recorded: the buffer (only the first <c>Count</c> slices are real) and how many slices there are.</summary>
        public (byte[] Peaks, int Count) LivePeaks
        {
            get { lock (_gate) return (_livePeaks, _livePeakCount); }
        }

        /// <summary>Called under the lock with the bytes just written: folds them into 1/250 s loudness slices.</summary>
        private void AddPeaks(byte[] buffer, int count)
        {
            var format = _format;
            if (format == null) return;
            int bytesPerSample = format.BitsPerSample / 8;
            int channels = Math.Max(1, format.Channels);
            int frameBytes = bytesPerSample * channels;
            for (int i = 0; i + frameBytes <= count; i += frameBytes)
            {
                for (int c = 0; c < channels; c++)
                {
                    int at = i + c * bytesPerSample;
                    int v = bytesPerSample == 2
                        ? Math.Abs((short)(buffer[at] | (buffer[at + 1] << 8))) * 255 / 32768
                        : Math.Abs((buffer[at] | (buffer[at + 1] << 8) | ((sbyte)buffer[at + 2] << 16))) * 255 / 8388608;
                    if (v > _sliceMax) _sliceMax = v;
                }
                if (++_sliceFrames >= _framesPerSlice)
                {
                    _sliceFrames -= _framesPerSlice;
                    if (_livePeakCount == _livePeaks.Length) Array.Resize(ref _livePeaks, _livePeaks.Length * 2);
                    _livePeaks[_livePeakCount++] = (byte)Math.Min(255, _sliceMax);
                    _sliceMax = 0;
                }
            }
        }

        // =================================================================
        //  Recording
        // =================================================================

        /// <summary>Starts recording into a new temporary WAV. Throws (e.g. MmException) if the device won't open; nothing is left behind then.</summary>
        public void Start(MicRecorderDevice device, MicRecorderFormat format, bool monitor)
        {
            if (State != MicRecorderState.Idle) throw new InvalidOperationException("Already recording.");
            Directory.CreateDirectory(TempFolder);
            var waveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
            string path = NewTempPath();
            var waveIn = new WaveInEvent { DeviceNumber = device.Number, WaveFormat = waveFormat, BufferMilliseconds = 50 };
            try
            {
                lock (_gate)
                {
                    _writer = new WaveFileWriter(path, waveFormat);
                    _dataBytes = 0;
                    _limitReached = false;
                    _format = format;
                    _livePeakCount = 0;
                    _sliceFrames = 0;
                    _sliceMax = 0;
                    _framesPerSlice = format.SampleRate / (double)AudioTrimmerPeaks.PerSecond;
                }
                _paused = false;
                waveIn.DataAvailable += WaveIn_DataAvailable;
                waveIn.RecordingStopped += WaveIn_RecordingStopped;
                if (monitor) StartMonitor(waveFormat);
                waveIn.StartRecording();
            }
            catch
            {
                waveIn.DataAvailable -= WaveIn_DataAvailable;
                waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                waveIn.Dispose();
                StopMonitor();
                lock (_gate)
                {
                    _writer?.Dispose();
                    _writer = null;
                    _format = null;
                }
                DeleteIfTemp(path);
                throw;
            }
            _waveIn = waveIn;
            _tempPath = path;
            _startedAt = DateTime.Now;
            State = MicRecorderState.Recording;
        }

        /// <summary>
        /// Pausing keeps the microphone open and just stops writing (the old
        /// pause closed the device and reopened it on resume, which could
        /// fail or pick up a click). The meter keeps moving while paused.
        /// </summary>
        public void Pause()
        {
            if (State != MicRecorderState.Recording) return;
            _paused = true;
            State = MicRecorderState.Paused;
        }

        public void Resume()
        {
            if (State != MicRecorderState.Paused) return;
            _paused = false;
            State = MicRecorderState.Recording;
        }

        /// <summary>Asks the driver to stop; RecordingFinished follows once every buffer is written (stopping straight away could cut the end off).</summary>
        public void Stop()
        {
            if (State is MicRecorderState.Idle or MicRecorderState.Finishing || _waveIn == null) return;
            State = MicRecorderState.Finishing;
            StopMonitor();
            _waveIn.StopRecording();
        }

        /// <summary>
        /// Closing the app: stops now and finishes the file at once (there's
        /// no time to wait for the driver), so the WAV is valid. Returns the
        /// take, or null.
        /// </summary>
        public MicRecorderTake? StopNow()
        {
            if (State == MicRecorderState.Idle || _waveIn == null) return null;
            var waveIn = _waveIn;
            waveIn.DataAvailable -= WaveIn_DataAvailable;
            waveIn.RecordingStopped -= WaveIn_RecordingStopped;
            try { waveIn.StopRecording(); } catch (MmException) { /* already gone */ }
            StopMonitor();
            var take = FinishTake();
            waveIn.Dispose();
            return take;
        }

        // A classic RIFF WAV holds at most 4 GiB; stop a little short of that so the header stays valid.
        private const long MaxWavDataBytes = 4_000_000_000;
        private bool _limitReached;

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            bool hitLimit = false;
            lock (_gate)
            {
                if (!_paused && _writer != null && !_limitReached)
                {
                    int count = (int)Math.Min(e.BytesRecorded, MaxWavDataBytes - _dataBytes);
                    if (count > 0)
                    {
                        _writer.Write(e.Buffer, 0, count);
                        _dataBytes += count;
                        AddPeaks(e.Buffer, count);
                    }
                    if (_dataBytes >= MaxWavDataBytes) { _limitReached = true; hitLimit = true; }
                }
            }
            if (hitLimit) System.Threading.Tasks.Task.Run(Stop);
            if (!_paused) _monitorBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            int bits = (sender as WaveInEvent)?.WaveFormat.BitsPerSample ?? 16;
            LevelChanged?.Invoke(MeterPercent(PeakLevel(e.Buffer, e.BytesRecorded, bits)));
        }

        private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (sender is WaveInEvent waveIn)
            {
                waveIn.DataAvailable -= WaveIn_DataAvailable;
                waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                waveIn.Dispose();
            }
            StopMonitor();
            var take = FinishTake();
            // The old view ignored e.Exception: an unplugged mic ended the take with no word why.
            string? error = e.Exception == null ? null : "The microphone stopped: " + e.Exception.Message;
            if (_limitReached)
            {
                error ??= "Recording stopped at the 4 GB limit of a WAV file. Your recording so far is saved; start a new one to continue.";
                _limitReached = false;
            }
            RecordingFinished?.Invoke(take, error);
        }

        /// <summary>Closes the WAV (writing its final sizes) and makes the take; an empty one is deleted.</summary>
        private MicRecorderTake? FinishTake()
        {
            long bytes;
            byte[] peaks;
            MicRecorderFormat? format;
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
                bytes = _dataBytes;
                format = _format;
                _format = null;
                peaks = _livePeaks.AsSpan(0, _livePeakCount).ToArray();
            }
            _waveIn = null;
            State = MicRecorderState.Idle;
            string? path = _tempPath;
            _tempPath = null;
            if (path == null || format == null) return null;
            if (bytes == 0)
            {
                DeleteIfTemp(path);
                return null;
            }
            return new MicRecorderTake
            {
                FilePath = path,
                RecordedAt = _startedAt,
                Duration = DurationOf(bytes, format),
                Format = format,
                SizeBytes = new FileInfo(path).Length,
                Peaks = peaks,
            };
        }

        // =================================================================
        //  Hearing the input
        // =================================================================

        /// <summary>Turns "hear the input" on or off, also mid-recording.</summary>
        public void SetMonitor(bool on)
        {
            if (!on) StopMonitor();
            else if (_monitorOut == null && _waveIn != null) StartMonitor(_waveIn.WaveFormat);
        }

        private void StartMonitor(WaveFormat format)
        {
            // NAudio 3: buffer length is set in the constructor; WaveOut latency is per buffer.
            var buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(1)) { DiscardOnBufferOverflow = true };
            var output = new WaveOutEvent { BufferMilliseconds = 60 };
            output.Init(buffer);
            output.Play();
            _monitorBuffer = buffer;
            _monitorOut = output;
        }

        private void StopMonitor()
        {
            var output = _monitorOut;
            _monitorOut = null;
            _monitorBuffer = null;
            output?.Stop();
            output?.Dispose();
        }

        // =================================================================
        //  Playback
        // =================================================================

        /// <summary>Where the take being played is, in seconds; NaN when nothing is playing.</summary>
        public double PlaybackSeconds => _playReader is { } r ? r.CurrentTime.TotalSeconds : double.NaN;

        /// <summary>Plays the take from <paramref name="fromSeconds"/> (0 = the start).</summary>
        public void Play(MicRecorderTake take, double fromSeconds = 0)
        {
            StopPlayback();
            var reader = new AudioFileReader(take.FilePath);
            if (fromSeconds > 0 && fromSeconds < reader.TotalTime.TotalSeconds) reader.CurrentTime = TimeSpan.FromSeconds(fromSeconds);
            var output = new WaveOutEvent();
            try
            {
                output.Init(reader);
            }
            catch
            {
                output.Dispose();
                reader.Dispose();
                throw;
            }
            // Only react if this is still the current player - a late event
            // from a player already replaced used to stop the new one.
            output.PlaybackStopped += (_, _) =>
            {
                if (!ReferenceEquals(_playOut, output)) return;
                var ended = _playing;
                StopPlayback();
                if (ended != null) PlaybackEnded?.Invoke(ended);
            };
            _playOut = output;
            _playReader = reader;
            _playing = take;
            take.IsPlaying = true;
            output.Play();
        }

        public void StopPlayback()
        {
            var output = _playOut;
            var reader = _playReader;
            _playOut = null;
            _playReader = null;
            if (_playing != null) _playing.IsPlaying = false;
            _playing = null;
            output?.Stop();
            output?.Dispose();
            reader?.Dispose();
        }

        /// <summary>Stops playback first if it's this take (a playing file can't be moved or deleted).</summary>
        public void Release(MicRecorderTake take)
        {
            if (ReferenceEquals(_playing, take)) StopPlayback();
        }

        public void Dispose()
        {
            StopPlayback();
            StopNow();
        }
    }
}
