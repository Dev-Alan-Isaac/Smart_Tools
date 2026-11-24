using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Video to Audio - plain data only. Planning (formats, ffmpeg
    //  arguments, size estimates, the file list, settings) is in
    //  Controllers/VideoToAudioManager.cs; probing and running ffmpeg in
    //  VideoToAudioManager.Run.cs.
    // =====================================================================

    /// <summary>Output format chips (read by Tag = enum name).</summary>
    public enum VtaFormat
    {
        Mp3,

        /// <summary>AAC in an .m4a file.</summary>
        M4a,
        Wav,
        Flac
    }

    /// <summary>Sample rate choice (read by Tag = enum name).</summary>
    public enum VtaSampleRate
    {
        /// <summary>Keep the video's own rate.</summary>
        Original,
        Hz44100,
        Hz48000
    }

    /// <summary>Which sound tracks become files.</summary>
    public enum VtaTrackMode
    {
        /// <summary>The track picked on each row (the first one by default).</summary>
        Chosen,

        /// <summary>One file per track ("Movie (eng).mp3", "Movie (spa).mp3").</summary>
        Every
    }

    /// <summary>Row status - a themed chip in the grid.</summary>
    public enum VtaItemStatus
    {
        /// <summary>ffprobe is reading the file.</summary>
        Reading,
        Ready,

        /// <summary>The video has no sound track - left out of the run.</summary>
        NoAudio,

        /// <summary>ffprobe couldn't read it (damaged or not a video).</summary>
        Unreadable,
        Extracting,
        Done,
        Failed,
        Cancelled
    }

    /// <summary>Something the user added: one file or a whole folder.</summary>
    public sealed class VtaSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
    }

    /// <summary>One row of the Files card: an added folder, or added files grouped by folder.</summary>
    public sealed class VtaQueueGroup
    {
        public required string FolderPath { get; init; }
        public bool IsFolder { get; init; }
        public List<string> FilePaths { get; init; } = new();
        public bool IncludeSubfolders { get; init; }
        public int VideoCount { get; init; }

        public string Name
        {
            get
            {
                string trimmed = FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? FolderPath : name;   // drive roots
            }
        }

        public string Subtitle => IsFolder
            ? (IncludeSubfolders ? "Folder and subfolders · " : "Folder · ") + VtaText.Plural(VideoCount, "video", "videos")
            : FilePaths.Count == 1 ? Path.GetFileName(FilePaths[0]) : VtaText.Plural(FilePaths.Count, "video", "videos");

        public string ToolTip => IsFolder
            ? FolderPath
            : string.Join(Environment.NewLine, FilePaths.Take(15)) + (FilePaths.Count > 15 ? $"{Environment.NewLine}+{FilePaths.Count - 15} more" : "");
    }

    /// <summary>One sound track of a video, as ffprobe reports it.</summary>
    public sealed record VtaAudioTrack(int Number, string Codec, int Channels, int SampleRate, long? BitRate, string? Language, string? Title)
    {
        /// <summary>"Track 2 · spa · AAC stereo · 48 kHz".</summary>
        public string Label
        {
            get
            {
                var parts = new List<string> { $"Track {Number + 1}" };
                if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title.Trim());
                else if (!string.IsNullOrWhiteSpace(Language) && Language != "und") parts.Add(Language);
                string layout = Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", > 0 => $"{Channels} ch", _ => "" };
                parts.Add((Codec.ToUpperInvariant() + " " + layout).Trim());
                if (SampleRate > 0) parts.Add($"{SampleRate / 1000.0:0.#} kHz");
                return string.Join(" · ", parts);
            }
        }

        /// <summary>The bit used in file names when every track is saved: language, else "track 2".</summary>
        public string FileTag => !string.IsNullOrWhiteSpace(Language) && Language != "und" ? Language! : $"track {Number + 1}";

        public override string ToString() => Label;
    }

    /// <summary>What ffprobe found in a file.</summary>
    public sealed record VtaProbe(TimeSpan? Duration, IReadOnlyList<VtaAudioTrack> Tracks, string? Error);

    /// <summary>One row in the grid.</summary>
    public sealed class VtaFileItem : INotifyPropertyChanged
    {
        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public long SizeBytes { get; init; }
        public string Size => VtaText.FormatBytes(SizeBytes);

        private TimeSpan? _duration;
        public TimeSpan? Duration
        {
            get => _duration;
            set
            {
                if (Set(ref _duration, value)) OnPropertyChanged(nameof(DurationText));
            }
        }

        public string DurationText => Duration is TimeSpan d ? VtaText.FormatTime(d) : "–";

        private IReadOnlyList<VtaAudioTrack> _tracks = Array.Empty<VtaAudioTrack>();
        public IReadOnlyList<VtaAudioTrack> Tracks
        {
            get => _tracks;
            set
            {
                if (!Set(ref _tracks, value)) return;
                OnPropertyChanged(nameof(HasSeveralTracks));
                OnPropertyChanged(nameof(CanPickTrack));
                OnPropertyChanged(nameof(TrackText));
            }
        }

        private VtaAudioTrack? _selectedTrack;
        /// <summary>The track this row extracts (Chosen mode). Picked in the grid's combo.</summary>
        public VtaAudioTrack? SelectedTrack
        {
            get => _selectedTrack;
            set
            {
                if (Set(ref _selectedTrack, value)) OnPropertyChanged(nameof(TrackText));
            }
        }

        public bool HasSeveralTracks => Tracks.Count > 1;

        private bool _locked;
        /// <summary>Set while a run is going: the track combo can't change.</summary>
        public bool Locked
        {
            get => _locked;
            set
            {
                if (Set(ref _locked, value)) OnPropertyChanged(nameof(CanPickTrack));
            }
        }

        public bool CanPickTrack => HasSeveralTracks && !Locked;

        /// <summary>Single-track rows show the track as text instead of a combo.</summary>
        public string TrackText => Tracks.Count == 0 ? "–" : (SelectedTrack ?? Tracks[0]).Label;

        private VtaItemStatus _status = VtaItemStatus.Reading;
        public VtaItemStatus Status
        {
            get => _status;
            set
            {
                if (!Set(ref _status, value)) return;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(CanRun));
            }
        }

        public string StatusText => Status switch
        {
            VtaItemStatus.Reading => "Reading",
            VtaItemStatus.Ready => "Ready",
            VtaItemStatus.NoAudio => "No sound",
            VtaItemStatus.Unreadable => "Can't read",
            VtaItemStatus.Extracting => Progress > 0 ? $"{Progress:0}%" : "Extracting",
            VtaItemStatus.Done => "Done",
            VtaItemStatus.Failed => "Failed",
            VtaItemStatus.Cancelled => "Cancelled",
            _ => Status.ToString()
        };

        public bool IsWorking => Status == VtaItemStatus.Extracting;

        /// <summary>Has sound and isn't finished: goes into the next run.</summary>
        public bool CanRun => Status is VtaItemStatus.Ready or VtaItemStatus.Failed or VtaItemStatus.Cancelled;

        private double _progress;
        public double Progress
        {
            get => _progress;
            set
            {
                if (!Set(ref _progress, value)) return;
                if (Status == VtaItemStatus.Extracting) OnPropertyChanged(nameof(StatusText));
            }
        }

        private string _message = "";
        /// <summary>The reason behind the chip (its tooltip), or where the result went.</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value ?? "");
        }

        private string? _estimate;
        /// <summary>"≈ 4.2 MB" before a run, the real size after.</summary>
        public string? ResultText
        {
            get => _estimate;
            set => Set(ref _estimate, value);
        }

        private IReadOnlyList<string> _outputPaths = Array.Empty<string>();
        public IReadOnlyList<string> OutputPaths
        {
            get => _outputPaths;
            set => Set(ref _outputPaths, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Everything a run needs from the sidebar.</summary>
    public sealed record VtaOptions
    {
        public VtaFormat Format { get; init; } = VtaFormat.Mp3;
        public int BitrateKbps { get; init; } = 192;
        public VtaSampleRate SampleRate { get; init; } = VtaSampleRate.Original;
        public VtaTrackMode TrackMode { get; init; } = VtaTrackMode.Chosen;
        public bool KeepTags { get; init; } = true;
        public bool TitleFromFileName { get; init; }
        public string Artist { get; init; } = "";
        public string Album { get; init; } = "";
    }

    /// <summary>Live progress of a run (posted to the UI thread).</summary>
    public sealed record VtaProgress(int Finished, int Failed, int Total, double Percent, string? CurrentName);

    /// <summary>How a run went.</summary>
    public sealed class VtaRunResult
    {
        public int Done { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public long OutputBytes { get; set; }
        public List<string> Files { get; } = new();
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>Saved choices (%AppData%\Smart Tools\video_to_audio.json). No folders.</summary>
    public sealed class VtaSettings
    {
        public VtaFormat Format { get; set; } = VtaFormat.Mp3;
        public int BitrateKbps { get; set; } = 192;
        public VtaSampleRate SampleRate { get; set; } = VtaSampleRate.Original;
        public VtaTrackMode TrackMode { get; set; } = VtaTrackMode.Chosen;
        public bool KeepTags { get; set; } = true;
        public bool TitleFromFileName { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool OpenWhenDone { get; set; }
    }

    /// <summary>Small text helpers for this tool's rows.</summary>
    public static class VtaText
    {
        public static string FormatBytes(long bytes) => Smart_Tools.WPF.Controllers.ByteFormat.Format(bytes);

        public static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        public static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
    }
}
