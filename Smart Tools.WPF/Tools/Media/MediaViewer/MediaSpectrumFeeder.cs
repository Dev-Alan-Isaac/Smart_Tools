using NAudio.Wave;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Spectrum for the Media Viewer's music view. MediaElement plays the sound but gives no access to it, so this reads the
    /// same file a second time (NAudio, never played) on its own thread and keeps that reader at the player's position:
    /// the samples it reads are run through the Music page's FFT tap, so the bars follow the music. A reader that cannot
    /// open the file (or a seek that fails) just leaves the bars still; playback is never affected.
    /// </summary>
    public sealed class MediaSpectrumFeeder : IDisposable
    {
        private readonly string _path;
        private readonly Thread _thread;
        private readonly SpectrumLevelMapper _mapper = new();
        private readonly CancellationTokenSource _stop = new();

        private volatile float[]? _magnitudes;
        private long _positionTicks;
        private volatile bool _playing;
        private int _sampleRate = 44100;

        public MediaSpectrumFeeder(string path)
        {
            _path = path;
            _thread = new Thread(Run) { IsBackground = true, Name = "Media spectrum", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        /// <summary>Tells the feeder where the player is. Cheap; call from the UI timer.</summary>
        public void Sync(TimeSpan position, bool playing)
        {
            Interlocked.Exchange(ref _positionTicks, position.Ticks);
            _playing = playing;
        }

        /// <summary>Fills the bar levels (0..1). False when nothing is playing or the file could not be read (levels zeroed).</summary>
        public bool GetLevels(float[] levels)
        {
            var magnitudes = _magnitudes;
            if (magnitudes == null || !_playing)
            {
                Array.Clear(levels);
                return false;
            }
            _mapper.Fill(magnitudes, AudioReplayManager.SpectrumSampleProvider.FftLength, _sampleRate, levels);
            return true;
        }

        private void Run()
        {
            try
            {
                using var reader = new AudioFileReader(_path);
                _sampleRate = reader.WaveFormat.SampleRate;
                int channels = Math.Max(1, reader.WaveFormat.Channels);
                var tap = new AudioReplayManager.SpectrumSampleProvider(reader);
                tap.FftCalculated += m => _magnitudes = m;

                var buffer = new float[_sampleRate * channels / 2];   // at most half a second per read
                var token = _stop.Token;

                while (!token.IsCancellationRequested)
                {
                    if (!_playing)
                    {
                        token.WaitHandle.WaitOne(30);
                        continue;
                    }

                    var target = TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));
                    var at = reader.CurrentTime;
                    double ahead = (target - at).TotalSeconds;

                    if (ahead < -0.15 || ahead > 0.5)
                    {
                        // Jumped (seek, next track not yet noticed, big stall): put the reader where the player is.
                        reader.CurrentTime = target < reader.TotalTime ? target : TimeSpan.Zero;
                    }
                    else if (ahead > 0)
                    {
                        int wanted = (int)Math.Min(buffer.Length, ahead * _sampleRate) * channels;
                        wanted = Math.Max(channels, wanted - wanted % channels);
                        if (tap.Read(buffer.AsSpan(0, wanted)) == 0) _magnitudes = null;   // end of file
                    }
                    token.WaitHandle.WaitOne(15);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
            {
                Log.Info($"Media spectrum: {ex.Message}");
                _magnitudes = null;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join(500);
            _stop.Dispose();
        }
    }
}
