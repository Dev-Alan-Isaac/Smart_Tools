using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Spectrogram Generator data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/SpectogramView/SpectogramModels.cs.)
    // =====================================================================

    /// <summary>ffmpeg showspectrumpic colour palettes (all 15 that ffmpeg 6+ offers).</summary>
    public enum SpectrogramColorScheme
    {
        Intensity,
        Rainbow,
        Magma,
        Viridis,
        Plasma,
        Cividis,
        Fire,
        Fiery,
        Fruit,
        Cool,
        Green,
        Terrain,
        Nebulae,
        Moreland,
        Channel,
    }

    /// <summary>How the frequency (vertical) axis is spaced. Logarithmic gives bass and mids more room, like hearing does.</summary>
    public enum SpectrogramFrequencyScale
    {
        Linear,
        Logarithmic,
    }

    /// <summary>Stereo files: one picture for all channels, or one band per channel.</summary>
    public enum SpectrogramChannelMode
    {
        Combined,
        Separate,
    }

    public enum SpectrogramExportFormat
    {
        Png,
        Jpg,
        Bmp,
    }

    public enum SpectrogramMode
    {
        Single,
        Batch,
    }

    public enum SpectrogramStatus
    {
        Waiting,
        Working,
        Done,
        Failed,
        Cancelled,
    }

    /// <summary>Everything one picture needs, read from the sidebar.</summary>
    public sealed class SpectrogramOptions
    {
        /// <summary>Size of the spectrum area. The legend, when shown, adds a frame around it.</summary>
        public int WidthPx { get; init; } = 1024;

        public int HeightPx { get; init; } = 512;

        public SpectrogramColorScheme ColorScheme { get; init; } = SpectrogramColorScheme.Intensity;
        public SpectrogramFrequencyScale FrequencyScale { get; init; } = SpectrogramFrequencyScale.Linear;
        public SpectrogramChannelMode ChannelMode { get; init; } = SpectrogramChannelMode.Combined;

        /// <summary>Bottom of the picture in Hz, or null for 0 Hz.</summary>
        public int? MinFrequencyHz { get; init; }

        /// <summary>Top of the picture in Hz, or null for the highest the file holds (half its sample rate).</summary>
        public int? MaxFrequencyHz { get; init; }

        public bool ShowLegend { get; init; } = true;
        public SpectrogramExportFormat OutputFormat { get; init; } = SpectrogramExportFormat.Png;
    }

    /// <summary>What ffprobe says about a file's first audio track. Null from the probe means the file has no audio.</summary>
    public sealed record SpectrogramMediaInfo(TimeSpan? Duration, int SampleRate, int Channels)
    {
        /// <summary>Highest frequency the file can hold (Nyquist).</summary>
        public int MaxFrequencyHz => SampleRate / 2;

        public string DurationText => SpectrogramText.Duration(Duration);
        public string SampleRateText => SampleRate <= 0 ? "" : SampleRate % 1000 == 0 ? $"{SampleRate / 1000} kHz" : $"{SampleRate / 1000.0:0.#} kHz";
        public string ChannelsText => Channels switch { <= 0 => "", 1 => "Mono", 2 => "Stereo", _ => $"{Channels} channels" };
    }

    /// <summary>One finished picture. <see cref="ImagePath"/> is a working copy in the session's temp folder until saved.</summary>
    public sealed class SpectrogramResult
    {
        public bool Success { get; init; }
        public bool Cancelled { get; init; }
        public string? Error { get; init; }
        public string SourcePath { get; init; } = "";
        public string? ImagePath { get; init; }
        public SpectrogramMediaInfo? Info { get; init; }

        /// <summary>Set when the frequency range was changed to fit the file (e.g. "Top limited to 22 kHz - the file holds nothing higher").</summary>
        public string? RangeNote { get; init; }

        public static SpectrogramResult Failed(string source, string error, SpectrogramMediaInfo? info = null) =>
            new() { SourcePath = source, Error = error, Info = info };
    }

    /// <summary>A row in batch mode: the file, its status and (once made) its picture.</summary>
    public sealed class SpectrogramItem : INotifyPropertyChanged
    {
        public SpectrogramItem(string fullPath)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
            Folder = Path.GetDirectoryName(fullPath) ?? "";
        }

        public string FullPath { get; }
        public string FileName { get; }
        public string Folder { get; }

        private SpectrogramStatus _status = SpectrogramStatus.Waiting;
        public SpectrogramStatus Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        private string _message = "Waiting";
        /// <summary>Short status text, or the plain reason it failed.</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        private string _durationText = "";
        public string DurationText
        {
            get => _durationText;
            set => Set(ref _durationText, value);
        }

        private string? _imagePath;
        /// <summary>The working copy in temp; null until made.</summary>
        public string? ImagePath
        {
            get => _imagePath;
            set => Set(ref _imagePath, value);
        }

        private string? _savedPath;
        /// <summary>Where it was saved, if it was.</summary>
        public string? SavedPath
        {
            get => _savedPath;
            set => Set(ref _savedPath, value);
        }

        private object? _thumbnail;
        /// <summary>An ImageSource set by the view, only for rows on screen (a full picture per row costs gigabytes at 10,000 rows).</summary>
        public object? Thumbnail
        {
            get => _thumbnail;
            set => Set(ref _thumbnail, value);
        }

        public bool ThumbnailRequested { get; set; }

        /// <summary>The picture was made with these settings; a change marks it stale so Generate makes it again.</summary>
        public string? MadeWith { get; set; }

        public SpectrogramResult? Result { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Batch progress: how many are finished and how many failed.</summary>
    public readonly record struct SpectrogramProgress(int Done, int Total, int Failed, string CurrentFile)
    {
        public double Percent => Total <= 0 ? 0 : Math.Clamp(Done * 100.0 / Total, 0, 100);
    }

    /// <summary>Remembered between sessions (spectrogram.json in %AppData%\Smart Tools). No folders or file names.</summary>
    public sealed class SpectrogramSettings
    {
        public SpectrogramMode Mode { get; set; } = SpectrogramMode.Single;
        public int WidthPx { get; set; } = 1024;
        public int HeightPx { get; set; } = 512;
        public SpectrogramColorScheme ColorScheme { get; set; } = SpectrogramColorScheme.Intensity;
        public SpectrogramFrequencyScale FrequencyScale { get; set; } = SpectrogramFrequencyScale.Linear;
        public SpectrogramChannelMode ChannelMode { get; set; } = SpectrogramChannelMode.Combined;
        public int? MinFrequencyHz { get; set; }
        public int? MaxFrequencyHz { get; set; }
        public bool ShowLegend { get; set; } = true;
        public SpectrogramExportFormat OutputFormat { get; set; } = SpectrogramExportFormat.Png;
        public bool AutoSave { get; set; } = true;
    }

    public static class SpectrogramText
    {
        /// <summary>"0:42", "3:25", "1:02:07", "26:00:00" (hours don't wrap at 24 like the old hh format did).</summary>
        public static string Duration(TimeSpan? duration)
        {
            if (duration is not { } d || d < TimeSpan.Zero) return "";
            int hours = (int)d.TotalHours;
            return hours > 0 ? $"{hours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";
        }

        /// <summary>"80 Hz", "8 kHz", "12.5 kHz".</summary>
        public static string Hertz(int hz) => hz >= 1000 ? $"{hz / 1000.0:0.#} kHz" : $"{hz} Hz";

        public static string Plural(int count, string one, string many) => count == 1 ? one : many;
    }
}
