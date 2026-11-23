using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Audio Recorder (MicRecorderView) data. Plain data - no NAudio, no WPF.
    // =====================================================================

    public enum MicRecorderState
    {
        Idle,
        Recording,
        Paused,

        /// <summary>Stop was pressed; waiting for the driver to hand over the last buffers.</summary>
        Finishing,
    }

    /// <summary>A microphone Windows lists. The number can change when devices come and go; the name is what's remembered.</summary>
    public sealed record MicRecorderDevice(int Number, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>The WAV format to record in.</summary>
    public sealed record MicRecorderFormat(int SampleRate, int Channels, int BitsPerSample)
    {
        public int BlockAlign => Channels * BitsPerSample / 8;
        public int BytesPerSecond => SampleRate * BlockAlign;

        /// <summary>"44.1 kHz · mono · 16-bit"</summary>
        public string Describe() =>
            $"{SampleRate / 1000.0:0.#} kHz · {(Channels == 1 ? "mono" : "stereo")} · {BitsPerSample}-bit";
    }

    /// <summary>One finished recording in the list.</summary>
    public sealed class MicRecorderTake : INotifyPropertyChanged
    {
        private string _filePath = "";
        private bool _isSaved;
        private bool _isPlaying;

        public required DateTime RecordedAt { get; init; }
        public required TimeSpan Duration { get; init; }
        public required MicRecorderFormat Format { get; init; }

        /// <summary>Where the WAV is now: the temporary folder until it's saved, then the chosen folder.</summary>
        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Folder));
            }
        }

        public string DisplayName => Path.GetFileName(FilePath);
        public string Folder => Path.GetDirectoryName(FilePath) ?? "";

        /// <summary>True once it's in a folder the user chose (else it only lives until the app closes).</summary>
        public bool IsSaved
        {
            get => _isSaved;
            set { _isSaved = value; OnPropertyChanged(); OnPropertyChanged(nameof(Details)); }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (_isPlaying != value) { _isPlaying = value; OnPropertyChanged(); } }
        }

        public long SizeBytes { get; init; }

        /// <summary>The loudness shape of the take, <see cref="AudioTrimmerPeaks.PerSecond"/> slices a second (0-255), for the waveform. Null for a take read back without it.</summary>
        public byte[]? Peaks { get; set; }

        /// <summary>"0:42 · 44.1 kHz · mono · 16-bit · 3.6 MB · not saved yet"</summary>
        public string Details =>
            $"{MicRecorderText.Duration(Duration)} · {Format.Describe()} · {MicRecorderText.Bytes(SizeBytes)}" + (IsSaved ? "" : " · not saved yet");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Remembered between sessions (mic_recorder.json in %AppData%\Smart Tools). No folders - the save folder is per session.</summary>
    public sealed class MicRecorderSettings
    {
        public string DeviceName { get; set; } = "";
        public int SampleRate { get; set; } = 44100;
        public int Channels { get; set; } = 1;
        public int BitsPerSample { get; set; } = 16;
        public bool MonitorInput { get; set; }
        public bool SaveWithoutAsking { get; set; } = true;

        /// <summary>The name typed in "File name" (not a path). Empty = "Recording" + date and time.</summary>
        public string FileName { get; set; } = "";
    }

    public static class MicRecorderText
    {
        public static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        public static string Bytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }
}
