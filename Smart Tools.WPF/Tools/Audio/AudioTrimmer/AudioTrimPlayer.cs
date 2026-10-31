using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Audio Trimmer &amp; Joiner - the preview player. Plays a list of stretches (a file, from here to
    /// there, with fades) one after the other, always as 44.1 kHz stereo, so clips of different files
    /// join without a gap. Opened lazily, one file at a time. Create it on the UI thread (events come
    /// back there). Seeking is done by playing a new list.
    ///
    /// The preview plays the cuts and the fades; peak and loudness normalizing are only in the export.
    /// </summary>
    public sealed class AudioTrimPlayer : IDisposable
    {
        private const int OutputRate = 44_100;

        /// <summary>The sound card is a little ahead of what has been read; the shown position is moved back by about this much.</summary>
        private const double OutputLatencySeconds = 0.12;

        private WaveOutEvent? _output;
        private Sequence? _sequence;
        private bool _manualStop;
        private bool _disposed;

        /// <summary>The list ran to its end.</summary>
        public event Action? Ended;

        /// <summary>Something went wrong (a file couldn't be opened or played). The text is a plain sentence.</summary>
        public event Action<string>? Failed;

        public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
        public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
        public bool IsActive => _output != null;

        /// <summary>Where the sound is now, in seconds of the file being played. Safe to read from the UI timer.</summary>
        public double SourceTime
        {
            get
            {
                var s = _sequence;
                if (s == null) return 0;
                double t = s.SourceTime - (IsPlaying ? OutputLatencySeconds : 0);
                return Math.Max(s.ItemStart, t);
            }
        }

        /// <summary>The <see cref="AudioTrimPlayItem.ClipIndex"/> of the item that is playing, or -1.</summary>
        public int ItemIndex => _sequence?.ItemIndex ?? -1;

        /// <summary>
        /// Plays the list from its start. <paramref name="fadeIn"/> and <paramref name="fadeOut"/> are the fades of the
        /// whole joined stream (they can span several items); each item's own fades are for the small ones at joins.
        /// </summary>
        public void Play(IReadOnlyList<AudioTrimPlayItem> items, double fadeIn = 0, double fadeOut = 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Stop();
            if (items.Count == 0) return;

            _sequence = new Sequence(items, fadeIn, fadeOut);
            _manualStop = false;
            try
            {
                _output = new WaveOutEvent { BufferMilliseconds = 60 };
                _output.PlaybackStopped += Output_PlaybackStopped;
                _output.Init(_sequence.ToWaveProvider());
                _output.Play();
            }
            catch (Exception ex)
            {
                DisposeChain();
                Failed?.Invoke(PlainError(ex));
            }
        }

        public void Pause()
        {
            if (IsPlaying) _output!.Pause();
        }

        public void Resume()
        {
            if (IsPaused) _output!.Play();
        }

        public void Stop()
        {
            if (_output == null) return;
            _manualStop = true;        // Stop() raises the same event as a natural end
            DisposeChain();
        }

        private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (!ReferenceEquals(sender, _output)) return;       // an output that has since been replaced
            if (_manualStop) { _manualStop = false; return; }

            string? problem = _sequence?.Failure ?? (e.Exception != null ? PlainError(e.Exception) : null);
            DisposeChain();
            if (problem != null) Failed?.Invoke(problem);
            else Ended?.Invoke();
        }

        private void DisposeChain()
        {
            var output = _output;
            _output = null;
            if (output != null)
            {
                output.PlaybackStopped -= Output_PlaybackStopped;
                try { output.Stop(); } catch (Exception) { /* already stopped */ }
                output.Dispose();
            }
            _sequence?.Dispose();
            _sequence = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }

        private static string PlainError(Exception ex) => ex switch
        {
            FileNotFoundException => "A file isn't there any more.",
            NotSupportedException => "This audio can't be played here. You can still edit it and export it.",
            _ => "The audio couldn't be played: " + ex.Message,
        };

        // =================================================================
        //  The list as one stream
        // =================================================================

        private sealed class Sequence : ISampleProvider, IDisposable
        {
            private readonly IReadOnlyList<AudioTrimPlayItem> _items;
            private int _index = -1;
            private ItemStream? _current;
            private volatile string? _failure;
            private double _sourceTime;
            private double _itemStart;
            private readonly long _totalFrames;
            private readonly long _fadeInFrames, _fadeOutFrames;
            private long _globalFrames;

            public Sequence(IReadOnlyList<AudioTrimPlayItem> items, double fadeIn, double fadeOut)
            {
                _items = items;
                if (items.Count > 0) { _itemStart = items[0].Start; _sourceTime = items[0].Start; }

                _totalFrames = items.Sum(i => Math.Max(1, (long)Math.Round((i.End - i.Start) * OutputRate)));
                double total = _totalFrames / (double)OutputRate;
                var (fin, fout) = AudioTrimmerManager.EffectiveFades(total, fadeIn, fadeOut);
                _fadeInFrames = (long)Math.Round(fin * OutputRate);
                _fadeOutFrames = (long)Math.Round(fout * OutputRate);
            }

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 2);

            public string? Failure => _failure;
            public double SourceTime => Volatile.Read(ref _sourceTime);
            public double ItemStart => Volatile.Read(ref _itemStart);

            /// <summary>The item (index in the list given to Play) being read.</summary>
            public int ItemIndex => _index < 0 || _index >= _items.Count ? -1 : _items[_index].ClipIndex;

            public int Read(Span<float> buffer)
            {
                int written = 0;
                try
                {
                    while (written < buffer.Length)
                    {
                        if (_current == null && !OpenNext()) break;

                        int n = _current!.Read(buffer.Slice(written));
                        if (n == 0)
                        {
                            _current.Dispose();
                            _current = null;
                            continue;
                        }
                        ApplyGlobalFades(buffer.Slice(written, n));
                        written += n;
                        Volatile.Write(ref _sourceTime, _current.SourceTime);
                    }
                }
                catch (Exception ex)
                {
                    _failure = ex is FileNotFoundException or DirectoryNotFoundException ? "A file isn't there any more."
                        : ex is NotSupportedException ? "This audio can't be played here. You can still edit it and export it."
                        : "The audio couldn't be played: " + ex.Message;
                    _current?.Dispose();
                    _current = null;
                    _index = _items.Count;
                }
                return written;
            }

            /// <summary>The fade in / out of the whole result, by the number of frames played so far across all items.</summary>
            private void ApplyGlobalFades(Span<float> chunk)
            {
                int frames = chunk.Length / 2;
                if (_fadeInFrames > 0 || _fadeOutFrames > 0)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        long frame = _globalFrames + i;
                        float gain = 1f;
                        if (_fadeInFrames > 0 && frame < _fadeInFrames) gain = frame / (float)_fadeInFrames;
                        long toEnd = _totalFrames - frame;
                        if (_fadeOutFrames > 0 && toEnd < _fadeOutFrames) gain = Math.Min(gain, Math.Max(0, toEnd) / (float)_fadeOutFrames);
                        if (gain < 1f)
                        {
                            chunk[i * 2] *= gain;
                            chunk[i * 2 + 1] *= gain;
                        }
                    }
                }
                _globalFrames += frames;
            }

            private bool OpenNext()
            {
                if (++_index >= _items.Count) return false;
                var item = _items[_index];
                _current = new ItemStream(item);
                Volatile.Write(ref _itemStart, item.Start);
                Volatile.Write(ref _sourceTime, item.Start);
                return true;
            }

            public void Dispose()
            {
                _current?.Dispose();
                _current = null;
            }
        }

        /// <summary>One stretch of one file: opened, moved to its start, brought to 44.1 kHz stereo, faded and cut off at its end.</summary>
        private sealed class ItemStream : IDisposable
        {
            private readonly AudioFileReader _reader;
            private readonly ISampleProvider _chain;
            private readonly long _totalFrames;
            private readonly long _fadeInFrames, _fadeOutFrames;
            private readonly double _start;
            private long _frames;

            public ItemStream(AudioTrimPlayItem item)
            {
                _reader = new AudioFileReader(item.Path);
                _start = item.Start;
                _reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, item.Start));

                ISampleProvider p = _reader;
                int channels = _reader.WaveFormat.Channels;
                if (channels == 1) p = new MonoToStereoSampleProvider(p);
                else if (channels != 2) throw new NotSupportedException();
                if (_reader.WaveFormat.SampleRate != OutputRate) p = new WdlResamplingSampleProvider(p, OutputRate);
                _chain = p;

                _totalFrames = Math.Max(1, (long)Math.Round((item.End - item.Start) * OutputRate));
                _fadeInFrames = (long)Math.Round(Math.Max(0, item.FadeIn) * OutputRate);
                _fadeOutFrames = (long)Math.Round(Math.Max(0, item.FadeOut) * OutputRate);
            }

            /// <summary>Seconds of the file at the end of what has been read so far.</summary>
            public double SourceTime => _start + _frames / (double)OutputRate;

            public int Read(Span<float> buffer)
            {
                long left = _totalFrames - _frames;
                if (left <= 0) return 0;
                int want = (int)Math.Min(buffer.Length / 2, left) * 2;
                if (want <= 0) return 0;

                int got = _chain.Read(buffer[..want]);
                if (got <= 0) return 0;

                if (_fadeInFrames > 0 || _fadeOutFrames > 0)
                {
                    for (int i = 0; i + 1 < got; i += 2)
                    {
                        long frame = _frames + i / 2;
                        float gain = 1f;
                        if (_fadeInFrames > 0 && frame < _fadeInFrames) gain = frame / (float)_fadeInFrames;
                        long toEnd = _totalFrames - frame;
                        if (_fadeOutFrames > 0 && toEnd < _fadeOutFrames) gain = Math.Min(gain, Math.Max(0, toEnd) / (float)_fadeOutFrames);
                        if (gain < 1f)
                        {
                            buffer[i] *= gain;
                            buffer[i + 1] *= gain;
                        }
                    }
                }
                _frames += got / 2;
                return got;
            }

            public void Dispose() => _reader.Dispose();
        }
    }
}
