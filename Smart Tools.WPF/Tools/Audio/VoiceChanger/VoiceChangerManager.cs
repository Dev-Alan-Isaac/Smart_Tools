using NAudio.Wave;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Behind VoiceChangerView: renders audio files through the effect chain
    /// (VoiceChangerDsp), runs the live
    /// microphone path (WaveIn -> buffer -> pitch -> robot -> echo -> out),
    /// and plays results back. The sound processing itself is in
    /// VoiceChangerDsp.cs (plain C#, unit-tested).
    ///
    /// Threads: rendering runs on the thread pool; live audio is pulled on
    /// NAudio's playback thread, so the live effect settings are pushed into
    /// the providers from the UI thread (never read from controls there).
    /// </summary>
    public sealed partial class VoiceChangerManager : IDisposable
    {
        // live mic
        private WaveInEvent? _liveIn;
        private BufferedWaveProvider? _liveBuffer;
        private WaveOutEvent? _liveOut;
        private VoiceChangerChain? _liveChain;
        private WaveOutEvent? _monitorOut;
        private bool _stoppingLive;

        // playback
        private WaveOutEvent? _playOut;
        private AudioFileReader? _playReader;
        private VoiceChangerTake? _playing;

        public bool IsLive => _liveOut != null;

        /// <summary>Playback devices: Windows' default first, then each one by name (virtual cables included).</summary>
        public static IReadOnlyList<VoiceChangerDevice> OutputDevices()
        {
            var list = new List<VoiceChangerDevice> { DefaultOutput };
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                list.Add(new VoiceChangerDevice(i, WaveOut.GetCapabilities(i).ProductName));
            return list;
        }

        /// <summary>The live path ended by itself (device unplugged, driver error). UI thread.</summary>
        public event Action<string?>? LiveStopped;

        public event Action<VoiceChangerTake>? PlaybackEnded;

        public static IReadOnlyList<VoiceChangerDevice> Devices()
        {
            var list = new List<VoiceChangerDevice>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                list.Add(new VoiceChangerDevice(i, WaveInEvent.GetCapabilities(i).ProductName));
            return list;
        }

        // =================================================================
        //  Effects
        // =================================================================

        /// <summary>A quick voice (values in VoiceChangerDsp.Preset).</summary>
        public static VoiceChangerEffect FromPreset(VoiceChangerPreset preset) => VoiceChangerDsp.Preset(preset);

        // =================================================================
        //  Rendering files
        // =================================================================

        public sealed record RenderFailure(string Path, string Reason);

        /// <summary>
        /// Changes the voice in each file, one result WAV per file, off the
        /// UI thread. One bad file is reported and skipped; the rest carry on.
        /// Cancelling keeps the results already made.
        /// </summary>
        public static async Task<(List<VoiceChangerTake> Results, List<RenderFailure> Failures)> RenderAsync(
            IReadOnlyList<string> paths,
            VoiceChangerEffect effect,
            IProgress<(int Done, int Total, string Current)>? progress,
            CancellationToken token)
        {
            var results = new List<VoiceChangerTake>();
            var failures = new List<RenderFailure>();
            string summary = Summarize(effect);

            for (int i = 0; i < paths.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                string path = paths[i];
                progress?.Report((i, paths.Count, Path.GetFileName(path)));
                try
                {
                    var take = await Task.Run(() => RenderOne(path, effect, summary, token), token);
                    results.Add(take);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                               or InvalidDataException or FormatException or ArgumentException or NAudio.MmException
                                               or System.Runtime.InteropServices.COMException or OutOfMemoryException)
                {
                    failures.Add(new RenderFailure(path, ex is OutOfMemoryException ? "too long to process in memory" : ex.Message));
                }
            }
            progress?.Report((paths.Count, paths.Count, ""));
            return (results, failures);
        }

        private static VoiceChangerTake RenderOne(string path, VoiceChangerEffect effect, string summary, CancellationToken token)
        {
            var (samples, format) = Decode(path, token);
            token.ThrowIfCancellationRequested();
            float[] processed = VoiceChangerDsp.Apply(samples, format.SampleRate, format.Channels, effect);
            token.ThrowIfCancellationRequested();
            double gain = LimitPeak(processed);

            string output = NewResultPath(path, effect);
            try
            {
                WriteWav16(output, processed, format.SampleRate, format.Channels);
            }
            catch
            {
                DeleteIfTemp(output);
                throw;
            }
            return new VoiceChangerTake
            {
                FilePath = output,
                SourceName = Path.GetFileName(path),
                EffectSummary = summary,
                Duration = TimeSpan.FromSeconds(processed.Length / (double)(format.SampleRate * format.Channels)),
                WasLimited = gain < 1,
            };
        }

        /// <summary>Writes floats (already limited to ±1) as a 16-bit PCM WAV.</summary>
        private static void WriteWav16(string path, float[] samples, int sampleRate, int channels)
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
            var bytes = new byte[8192 * 2];
            int n = 0;
            foreach (float s in samples)
            {
                short v = (short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue);
                bytes[n++] = (byte)v;
                bytes[n++] = (byte)(v >> 8);
                if (n == bytes.Length)
                {
                    writer.Write(bytes, 0, n);
                    n = 0;
                }
            }
            if (n > 0) writer.Write(bytes, 0, n);
        }

        /// <summary>Any format Windows can decode (wav, mp3, wma, aac, flac...) to interleaved floats at its own rate.</summary>
        private static (float[] Samples, WaveFormat Format) Decode(string path, CancellationToken token)
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider samples = reader;   // NAudio 3: AudioFileReader is also a Stream, whose Read takes bytes
            var format = WaveFormat.CreateIeeeFloatWaveFormat(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
            var chunk = new float[format.SampleRate * format.Channels];
            var all = new List<float>(reader.Length > 0 ? (int)Math.Min(int.MaxValue / 2, reader.Length / 4) : chunk.Length * 8);
            int read;
            while ((read = samples.Read(chunk)) > 0)
            {
                token.ThrowIfCancellationRequested();
                for (int i = 0; i < read; i++) all.Add(chunk[i]);
            }
            return (all.ToArray(), format);
        }

        // =================================================================
        //  Live microphone
        // =================================================================

        /// <summary>
        /// Starts the live voice. Mono at 44.1 kHz (the pitch shifter keeps
        /// one delay line). Throws if the device won't open; nothing is left
        /// running then.
        /// </summary>
        /// <param name="output">Where the changed voice plays: the default speakers, or a virtual cable other apps use as their microphone.</param>
        /// <param name="hearYourself">With a cable as output, also play it on the default speakers/headphones.</param>
        public void StartLive(VoiceChangerDevice device, VoiceChangerDevice output, VoiceChangerEffect effect, bool hearYourself)
        {
            if (IsLive) return;
            var format = new WaveFormat(44100, 16, 1);
            try
            {
                _liveIn = new WaveInEvent { DeviceNumber = device.Number, WaveFormat = format, BufferMilliseconds = 30 };
                // NAudio 3: the buffer's length is set when it's made (BufferDuration is read-only).
                _liveBuffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(2)) { DiscardOnBufferOverflow = true };
                var buffer = _liveBuffer;
                _liveIn.DataAvailable += (_, args) => buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                _liveIn.RecordingStopped += LiveIn_RecordingStopped;

                _liveChain = new VoiceChangerChain(format.SampleRate, format.Channels, effect);
                // Hearing yourself while the voice goes into a cable: the chain
                // copies what it makes into a second buffer the default output plays.
                BufferedWaveProvider? monitor = null;
                if (hearYourself && output.Number != DefaultOutputNumber)
                {
                    monitor = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels), TimeSpan.FromMilliseconds(500))
                    {
                        DiscardOnBufferOverflow = true,
                    };
                }
                var chainProvider = new ChainSampleProvider(_liveBuffer.ToSampleProvider(), _liveChain, monitor);

                // NAudio 3: BufferMilliseconds is per buffer (2 x 50 ms ≈ the old DesiredLatency of 100 ms).
                _liveOut = new WaveOutEvent { DeviceNumber = output.Number, BufferMilliseconds = 50 };
                _liveOut.Init(chainProvider);
                _liveOut.Play();
                if (monitor != null)
                {
                    _monitorOut = new WaveOutEvent { DeviceNumber = DefaultOutputNumber, BufferMilliseconds = 50 };
                    _monitorOut.Init(monitor);
                    _monitorOut.Play();
                }
                _liveIn.StartRecording();
            }
            catch
            {
                StopLive();
                throw;
            }
        }

        /// <summary>Pushes new effect settings into the running chain - dragging a slider changes the voice at once.</summary>
        public void UpdateLive(VoiceChangerEffect effect) => _liveChain?.Update(effect);

        /// <summary>Runs the effect chain on the live stream as NAudio pulls it (on its playback thread).</summary>
        private sealed class ChainSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly VoiceChangerChain _chain;
            private readonly BufferedWaveProvider? _monitor;
            private byte[] _monitorBytes = Array.Empty<byte>();

            public ChainSampleProvider(ISampleProvider source, VoiceChangerChain chain, BufferedWaveProvider? monitor)
            {
                _source = source;
                _chain = chain;
                _monitor = monitor;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(Span<float> buffer)
            {
                int read = _source.Read(buffer);
                var samples = buffer[..read];
                _chain.Process(samples);
                for (int i = 0; i < samples.Length; i++) samples[i] = Math.Clamp(samples[i], -1f, 1f);
                if (_monitor != null && read > 0)
                {
                    var bytes = MemoryMarshal.AsBytes(samples);
                    if (_monitorBytes.Length < bytes.Length) _monitorBytes = new byte[bytes.Length];
                    bytes.CopyTo(_monitorBytes);
                    _monitor.AddSamples(_monitorBytes, 0, bytes.Length);
                }
                return read;
            }
        }

        /// <summary>
        /// Stops the live voice. StopRecording can raise RecordingStopped
        /// synchronously, which would call back in here - the guard keeps the
        /// teardown to one pass.
        /// </summary>
        public void StopLive()
        {
            if (_stoppingLive) return;
            _stoppingLive = true;
            try
            {
                var input = _liveIn;
                _liveIn = null;
                if (input != null)
                {
                    input.RecordingStopped -= LiveIn_RecordingStopped;
                    try { input.StopRecording(); } catch (NAudio.MmException) { /* already stopped */ }
                    input.Dispose();
                }
                var output = _liveOut;
                _liveOut = null;
                output?.Stop();
                output?.Dispose();
                var monitorOut = _monitorOut;
                _monitorOut = null;
                monitorOut?.Stop();
                monitorOut?.Dispose();
                _liveBuffer = null;
                _liveChain = null;
            }
            finally
            {
                _stoppingLive = false;
            }
        }

        private void LiveIn_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (_stoppingLive || !IsLive) return;
            StopLive();
            LiveStopped?.Invoke(e.Exception?.Message);
        }

        // =================================================================
        //  Playback
        // =================================================================

        public void Play(VoiceChangerTake take)
        {
            StopPlayback();
            var reader = new AudioFileReader(take.FilePath);
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
            output.PlaybackStopped += (_, _) =>
            {
                if (!ReferenceEquals(_playOut, output)) return;   // a late event from a replaced player
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

        public void Release(VoiceChangerTake take)
        {
            if (ReferenceEquals(_playing, take)) StopPlayback();
        }

        public void Dispose()
        {
            StopLive();
            StopPlayback();
        }
    }
}
