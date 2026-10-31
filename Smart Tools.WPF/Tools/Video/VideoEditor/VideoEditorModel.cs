using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Video Stitcher data model. Plain data - no WPF or ffmpeg. Status
    //  colours live in the view (theme tokens); the old model built fixed
    //  light-theme brushes here by matching status strings.
    // =====================================================================

    public enum EditOutputFormat
    {
        Mp4,
        Mkv,
        Mov,
        Webm,
        Avi,
        Flv,
    }

    /// <summary>Picture size of the result when clips have to be re-encoded.</summary>
    public enum EditOutputSize
    {
        /// <summary>The first clip's size (portrait stays portrait).</summary>
        FirstClip,

        /// <summary>1920 × 1080 (or 1080 × 1920 when the first clip is portrait).</summary>
        P1080,

        /// <summary>1280 × 720 (or 720 × 1280).</summary>
        P720,
    }

    public enum EditClipStatus
    {
        Ready,

        /// <summary>Reading the clip's details before the run.</summary>
        Checking,

        Working,
        Done,
        Failed,
        Cancelled,
    }

    /// <summary>One clip in the list. The list order is the order in the video.</summary>
    public sealed class EditClip : INotifyPropertyChanged
    {
        private bool _isIncluded = true;
        private bool _isNowPlaying;
        private int _number;
        private TimeSpan? _duration;
        private TimeSpan? _trimStart, _trimEnd;
        private EditClipStatus _status;
        private string _message = "";

        public required string FullPath { get; init; }
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }

        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public string Size => EditText.FormatBytes(SizeBytes);

        /// <summary>The sidebar entry this clip came from.</summary>
        public EditClipGroup? Group { get; set; }

        /// <summary>Goes into the video.</summary>
        public bool IsIncluded
        {
            get => _isIncluded;
            set => Set(ref _isIncluded, value);
        }

        /// <summary>Loaded in the player (the shared PlaylistRowStyle highlights it).</summary>
        public bool IsNowPlaying
        {
            get => _isNowPlaying;
            set => Set(ref _isNowPlaying, value);
        }

        /// <summary>Place in the list (the old "#" column didn't update when clips moved).</summary>
        public int Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        public bool DurationRequested { get; set; }

        /// <summary>Full length of the file (null until read).</summary>
        public TimeSpan? Duration
        {
            get => _duration;
            set
            {
                if (_duration == value) return;
                _duration = value;
                OnPropertyChanged();
                OnLengthChanged();
            }
        }

        /// <summary>Where the used part starts (null = the beginning).</summary>
        public TimeSpan? TrimStart
        {
            get => _trimStart;
            set
            {
                if (_trimStart == value) return;
                _trimStart = value;
                OnPropertyChanged();
                OnLengthChanged();
            }
        }

        /// <summary>Where the used part ends (null = the end).</summary>
        public TimeSpan? TrimEnd
        {
            get => _trimEnd;
            set
            {
                if (_trimEnd == value) return;
                _trimEnd = value;
                OnPropertyChanged();
                OnLengthChanged();
            }
        }

        public bool IsTrimmed => _trimStart != null || _trimEnd != null;

        /// <summary>Length that goes into the video (null while the full length is unknown and the end isn't set).</summary>
        public TimeSpan? UsedLength
        {
            get
            {
                var end = _trimEnd ?? _duration;
                if (end == null) return null;
                var used = end.Value - (_trimStart ?? TimeSpan.Zero);
                return used > TimeSpan.Zero ? used : TimeSpan.Zero;
            }
        }

        public string LengthText => UsedLength is TimeSpan t ? EditText.FormatTime(t) : "–";

        /// <summary>"0:05.0 – 1:20.5 of 2:00" when trimmed.</summary>
        public string TrimText => !IsTrimmed
            ? ""
            : $"{EditText.FormatTimePrecise(_trimStart ?? TimeSpan.Zero)} – {(_trimEnd is TimeSpan e ? EditText.FormatTimePrecise(e) : "end")}"
              + (_duration is TimeSpan d ? $" of {EditText.FormatTime(d)}" : "");

        public EditClipStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public string StatusText => _status switch
        {
            EditClipStatus.Checking => "Checking",
            EditClipStatus.Working => "Working",
            EditClipStatus.Done => "Done",
            EditClipStatus.Failed => "Failed",
            EditClipStatus.Cancelled => "Cancelled",
            _ => "Ready",
        };

        /// <summary>The reason (status tooltip).</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        private void OnLengthChanged()
        {
            OnPropertyChanged(nameof(UsedLength));
            OnPropertyChanged(nameof(LengthText));
            OnPropertyChanged(nameof(TrimText));
            OnPropertyChanged(nameof(IsTrimmed));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One sidebar row per Add clips / Add folder / drop. Keeps its count up to date (the old one was removed and re-inserted to refresh).</summary>
    public sealed class EditClipGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }
        private bool _includeSubfolders;

        /// <summary>Changes when "Include subfolders" is ticked or unticked after the folder was added (the folder is read again).</summary>
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set
            {
                _includeSubfolders = value;
                Refresh();
            }
        }

        public List<EditClip> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = $"{Items.Count:N0} {EditText.Plural(Items.Count, "clip", "clips")}";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "Clips from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>A video found on disk.</summary>
    public readonly record struct EditFile(string FullPath, long SizeBytes, DateTime Modified);

    public sealed class EditFolderScan
    {
        public List<EditFile> Files { get; } = new();
        public int InaccessibleFolders { get; set; }
        public bool Missing { get; set; }
    }

    /// <summary>What ffprobe says about a clip.</summary>
    public sealed record VideoProbe(
        double Seconds,
        bool HasVideo,
        string VideoCodec,
        int Width,
        int Height,
        double Fps,
        string PixelFormat,
        int Rotation,
        bool HasAudio,
        string AudioCodec,
        int SampleRate,
        int Channels)
    {
        /// <summary>Size as shown (a phone video stored sideways with a 90° flag is tall).</summary>
        public int DisplayWidth => Math.Abs(Rotation % 180) == 90 ? Height : Width;
        public int DisplayHeight => Math.Abs(Rotation % 180) == 90 ? Width : Height;
    }

    /// <summary>Everything a run needs, read from the sidebar when Stitch is pressed.</summary>
    public sealed class EditOptions
    {
        public required string OutputFolder { get; init; }

        /// <summary>Name without extension (the extension comes from <see cref="Format"/>).</summary>
        public string FileName { get; init; } = "Stitched video";

        /// <summary>True when the file was picked in a Save dialog (replacing it was confirmed).</summary>
        public bool OverwriteAllowed { get; init; }

        public EditOutputFormat Format { get; init; } = EditOutputFormat.Mp4;
        public EditOutputSize Size { get; init; } = EditOutputSize.FirstClip;

        /// <summary>Fade through black at each clip's start and end; 0 = straight cuts.</summary>
        public double FadeSeconds { get; init; }

        /// <summary>Start a new file before one would pass this length; 0 = one file.</summary>
        public double SplitSeconds { get; init; }
    }

    public readonly record struct EditProgress(double Percent, string Text);

    public sealed class EditResult
    {
        public List<string> Files { get; } = new();
        public int ClipsUsed { get; set; }
        public int Failed { get; set; }
        public bool Cancelled { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan Length { get; set; }

        /// <summary>The clips were joined as they are (fast, no quality loss).</summary>
        public bool JoinedWithoutReencoding { get; set; }

        /// <summary>The video encoder used when re-encoding (e.g. h264_nvenc).</summary>
        public string? Encoder { get; set; }

        /// <summary>Why nothing was written, else null.</summary>
        public string? Error { get; set; }
    }

    /// <summary>Remembered between sessions (video_editor.json in %AppData%\Smart Tools). No folders or file names.</summary>
    public sealed class EditorSettings
    {
        public double FadeSeconds { get; set; } = 0.5;
        public EditOutputFormat Format { get; set; } = EditOutputFormat.Mp4;
        public EditOutputSize Size { get; set; } = EditOutputSize.FirstClip;
        public int SplitMinutes { get; set; } = 15;
        public bool IncludeSubfolders { get; set; } = true;
        public bool SaveWithoutAsking { get; set; } = true;
        public double Volume { get; set; } = 1.0;
        public bool Muted { get; set; }
        public bool QueueCollapsed { get; set; }
    }

    public static class EditText
    {
        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }

        /// <summary>"4:05", "1:02:03".</summary>
        public static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        /// <summary>"4:05.3" - for trim points.</summary>
        public static string FormatTimePrecise(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            string tenths = ((t.Milliseconds / 100) % 10).ToString();
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{tenths}"
                : $"{t.Minutes}:{t.Seconds:00}.{tenths}";
        }

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }
}
