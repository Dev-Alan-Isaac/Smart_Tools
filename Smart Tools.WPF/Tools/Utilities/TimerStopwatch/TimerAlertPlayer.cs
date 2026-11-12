using System.IO;
using System.Media;
using System.Windows.Media;
using System.Windows.Threading;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One choice in the "alert sound" list.</summary>
    public sealed record TimerSoundChoice(string Id, string LanguageKey, string Fallback);

    /// <summary>
    /// The sound a timer plays when it ends: a Windows sound, a short ringtone made here (no sound files shipped),
    /// or any audio file the user picks (mp3, wav, wma, m4a, ...). Create and use it on the UI thread
    /// (<see cref="MediaPlayer"/> needs it); <see cref="Stop"/> silences whatever is playing.
    /// </summary>
    public sealed class TimerAlertPlayer
    {
        public const string FileId = "file";
        public const string DefaultId = "system";

        public static readonly IReadOnlyList<TimerSoundChoice> Choices = new[]
        {
            new TimerSoundChoice(DefaultId, "Str_Timer_SndSystem", "Windows alert"),
            new TimerSoundChoice("chime", "Str_Timer_SndChime", "Soft chime"),
            new TimerSoundChoice("ring", "Str_Timer_SndRing", "Phone ring"),
            new TimerSoundChoice("melody", "Str_Timer_SndMelody", "Little melody"),
            new TimerSoundChoice("alarm", "Str_Timer_SndAlarm", "Alarm beeps"),
            new TimerSoundChoice("bell", "Str_Timer_SndBell", "Bell"),
            new TimerSoundChoice(FileId, "Str_Timer_SndFile", "My own sound file..."),
        };

        public const string FileFilter = "Audio|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac;*.ogg|All files|*.*";

        private static readonly Dictionary<string, byte[]> Cache = new();
        private SoundPlayer? _player;
        private MediaPlayer? _media;
        private readonly DispatcherTimer _endTimer = new();

        /// <summary>Raised on the UI thread when the sound finishes by itself (not when <see cref="Stop"/> is called).</summary>
        public event Action? Ended;

        public TimerAlertPlayer() => _endTimer.Tick += (_, _) => { _endTimer.Stop(); Ended?.Invoke(); };

        /// <summary>Plays the choice. A missing or unreadable file falls back to the Windows alert so the end is never silent.</summary>
        public void Play(string id, string? file)
        {
            Stop();
            try
            {
                if (id == FileId)
                {
                    if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) { PlayFile(file); return; }
                    SystemSounds.Exclamation.Play();
                    EndAfter(1.2);
                    return;
                }
                byte[]? wav = Wave(id);
                if (wav == null) { SystemSounds.Exclamation.Play(); EndAfter(1.2); return; }
                _player = new SoundPlayer(new MemoryStream(wav));
                _player.Play();
                EndAfter((wav.Length - 44) / (double)(Rate * 2) + 0.2);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or UriFormatException)
            {
                SystemSounds.Exclamation.Play();
                EndAfter(1.2);
            }
        }

        private void EndAfter(double seconds)
        {
            _endTimer.Interval = TimeSpan.FromSeconds(seconds);
            _endTimer.Start();
        }

        private void PlayFile(string file)
        {
            _media = new MediaPlayer();
            _media.MediaFailed += (_, _) => { SystemSounds.Exclamation.Play(); EndAfter(1.2); };
            _media.MediaEnded += (_, _) => Ended?.Invoke();
            _media.Open(new Uri(file));
            _media.Play();
        }

        public void Stop()
        {
            _endTimer.Stop();
            try { _player?.Stop(); } catch (InvalidOperationException) { }
            _player = null;
            if (_media != null)
            {
                try { _media.Stop(); _media.Close(); } catch (InvalidOperationException) { }
                _media = null;
            }
        }

        // ---- built-in ringtones: sine notes with a soft attack and a fading tail, as a 16-bit mono WAV ----

        private const int Rate = 22050;

        private static byte[]? Wave(string id)
        {
            if (Cache.TryGetValue(id, out var hit)) return hit;
            // (frequency Hz, milliseconds); frequency 0 is a rest
            (double f, int ms)[]? notes = id switch
            {
                "chime" => new[] { (1046.5, 260), (1318.5, 260), (1568.0, 520) },
                "ring" => new[] { (880.0, 90), (659.3, 90), (880.0, 90), (659.3, 90), (880.0, 90), (659.3, 90), (0.0, 280), (880.0, 90), (659.3, 90), (880.0, 90), (659.3, 90), (880.0, 90), (659.3, 90) },
                "melody" => new[] { (523.3, 180), (587.3, 180), (659.3, 180), (784.0, 180), (659.3, 180), (784.0, 360), (1046.5, 520) },
                "alarm" => new[] { (1000.0, 160), (0.0, 90), (1000.0, 160), (0.0, 90), (1000.0, 160), (0.0, 90), (1000.0, 160) },
                "bell" => new[] { (880.0, 1400) },
                _ => null,
            };
            if (notes == null) return null;

            var samples = new List<short>();
            foreach (var (f, ms) in notes)
            {
                int n = Rate * ms / 1000;
                for (int i = 0; i < n; i++)
                {
                    if (f <= 0) { samples.Add(0); continue; }
                    double t = i / (double)Rate;
                    double attack = Math.Min(1, i / (Rate * 0.01));          // 10 ms in
                    double tail = Math.Pow(1 - i / (double)n, id == "bell" ? 2.2 : 0.6);   // fades out
                    double tone = Math.Sin(2 * Math.PI * f * t) + (id == "bell" ? 0.35 * Math.Sin(2 * Math.PI * f * 2.76 * t) : 0.0);
                    samples.Add((short)(tone * attack * tail * 0.45 * short.MaxValue));
                }
            }

            using var ms2 = new MemoryStream();
            using (var w = new BinaryWriter(ms2))
            {
                int dataBytes = samples.Count * 2;
                w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
                w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write("data"u8.ToArray()); w.Write(dataBytes);
                foreach (short s in samples) w.Write(s);
            }
            return Cache[id] = ms2.ToArray();
        }
    }
}
