using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // Audio Trimmer & Joiner - plain data only. Reading files, drawing, playing and
    // exporting live in Controllers/AudioTrimmerManager*.cs and AudioTrimPlayer.cs.
    // Types start with "AudioTrim" because every tool shares this namespace.

    public enum AudioTrimFormat { Mp3, Wav, Flac, M4a, Ogg, Opus }

    public enum AudioTrimNormalize { Off, Peak, Loudness }

    public enum AudioTrimChannels { Auto, Stereo, Mono }

    /// <summary>Short time text: 0:07.5, 3:45.0, 1:02:03.4.</summary>
    public static class AudioTrimTime
    {
        public static string Format(double seconds, int decimals = 1)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "-";
            if (seconds < 0) seconds = 0;
            // Round first, so 59.96 s reads 1:00.0 and not 0:60.0.
            double factor = Math.Pow(10, decimals);
            double rounded = Math.Round(seconds * factor, MidpointRounding.AwayFromZero) / factor;
            long whole = (long)rounded;
            int h = (int)(whole / 3600), m = (int)(whole / 60 % 60), s = (int)(whole % 60);
            string frac = decimals > 0 ? "." + ((long)Math.Round((rounded - whole) * factor)).ToString(new string('0', decimals), CultureInfo.InvariantCulture) : "";
            return h > 0
                ? $"{h}:{m:00}:{s:00}{frac}"
                : $"{m}:{s:00}{frac}";
        }
    }

    /// <summary>One audio file that has been added. Read once; every clip made from it shares this.</summary>
    public sealed class AudioTrimSource
    {
        public required string Path { get; init; }
        public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>Length in seconds (from ffprobe, else counted while the waveform was read).</summary>
        public double Duration { get; set; }

        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string Codec { get; set; } = "";

        /// <summary>Loudness per slice of the file, 0-255, <see cref="AudioTrimmerPeaks.PerSecond"/> slices a second. Null until read.</summary>
        public byte[]? Peaks { get; set; }

        /// <summary>The file the preview plays: the original, or a converted copy for formats Windows can't play.</summary>
        public string? PlayPath { get; set; }
    }

    /// <summary>Shared constants of the waveform data.</summary>
    public static class AudioTrimmerPeaks
    {
        public const int PerSecond = 250;
    }

    /// <summary>A piece of a source that is part of the result. Ranges are in seconds of the source.</summary>
    public sealed class AudioTrimClip : INotifyPropertyChanged
    {
        private double _start;
        private double _end;
        private int _number;
        private bool _isSelected;
        private bool _isPlaying;

        public event PropertyChangedEventHandler? PropertyChanged;

        public AudioTrimClip(AudioTrimSource source, double start, double end)
        {
            Source = source;
            _start = start;
            _end = end;
        }

        public AudioTrimSource Source { get; }

        public double Start
        {
            get => _start;
            set { if (Set(ref _start, value)) RangeChanged(); }
        }

        public double End
        {
            get => _end;
            set { if (Set(ref _end, value)) RangeChanged(); }
        }

        public void SetRange(double start, double end)
        {
            _start = start;
            _end = end;
            RangeChanged();
        }

        public double Duration => Math.Max(0, _end - _start);

        public int Number { get => _number; set => Set(ref _number, value); }

        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        /// <summary>The preview is playing this clip right now.</summary>
        public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

        public string Name => Source.FileName;

        public string RangeText => AudioTrimTime.Format(_start) + " – " + AudioTrimTime.Format(_end);

        public string DurationText => AudioTrimTime.Format(Duration);

        /// <summary>The clip is the whole file (nothing trimmed off).</summary>
        public bool IsWholeFile => _start <= 0.0005 && _end >= Source.Duration - 0.0005;

        private void RangeChanged()
        {
            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(End));
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(RangeText));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(IsWholeFile));
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A clip as undo remembers it.</summary>
    public readonly record struct AudioTrimClipState(AudioTrimSource Source, double Start, double End)
    {
        public double Duration => Math.Max(0, End - Start);
    }

    /// <summary>One stretch of audio for the preview player. Fades are in seconds.</summary>
    public sealed record AudioTrimPlayItem(string Path, double Start, double End, double FadeIn, double FadeOut, int ClipIndex);

    /// <summary>How the result is written.</summary>
    public sealed record AudioTrimExportOptions(
        AudioTrimFormat Format,
        int BitrateKbps,
        AudioTrimNormalize Normalize,
        double FadeInSeconds,
        double FadeOutSeconds,
        int SampleRate,                   // 0 = the highest of the files
        AudioTrimChannels Channels);

    /// <summary>What an export did. <see cref="GainDb"/> is the gain peak-normalize applied (null when it wasn't asked for); <see cref="WasSilent"/> says there was nothing to lift.</summary>
    public sealed record AudioTrimExportResult(string Path, long Bytes, double Seconds, double? GainDb, bool WasSilent);

    /// <summary>What the file probe found. <see cref="Error"/> is empty when the file is usable.</summary>
    public sealed record AudioTrimProbe(double Duration, int SampleRate, int Channels, string Codec, string Error);

    /// <summary>Remembered between sessions: how the tool is set up. Never files, folders or clips.</summary>
    public sealed class AudioTrimSettings
    {
        public AudioTrimFormat Format { get; set; } = AudioTrimFormat.Mp3;
        public int BitrateKbps { get; set; } = 192;
        public AudioTrimNormalize Normalize { get; set; } = AudioTrimNormalize.Off;
        public double FadeInSeconds { get; set; }
        public double FadeOutSeconds { get; set; }
        public int SampleRate { get; set; }
        public AudioTrimChannels Channels { get; set; } = AudioTrimChannels.Auto;
        public bool OptionsCollapsed { get; set; }
        public bool ClipsCollapsed { get; set; }
    }
}
