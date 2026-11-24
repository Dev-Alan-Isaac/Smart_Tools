using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Video Replay - plain data only. Play order, file lists and settings
    //  are in Controllers/VideoReplayManager.cs; folders, ffprobe durations
    //  and preview copies in VideoReplayManager.Media.cs.
    // =====================================================================

    /// <summary>The mode tiles (read by Tag = enum name).</summary>
    public enum ReplayMode
    {
        Sequential,
        Shuffle,

        /// <summary>Repeat the current clip.</summary>
        Loop
    }

    /// <summary>Row status - a themed chip in the playlist.</summary>
    public enum ClipStatus
    {
        Ready,
        Playing,

        /// <summary>Making a preview copy Windows can play (ffmpeg).</summary>
        Converting,
        Failed
    }

    /// <summary>Something the user added: one file or a whole folder.</summary>
    public sealed class ReplaySource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
    }

    /// <summary>One sidebar row: an added folder, or added files grouped by folder.</summary>
    public sealed class ReplayQueueGroup
    {
        public required string FolderPath { get; init; }
        public bool IsFolder { get; init; }
        public List<string> FilePaths { get; init; } = new();
        public bool IncludeSubfolders { get; init; }
        public int ClipCount { get; init; }

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
            ? $"{ClipCount:N0} clip{(ClipCount == 1 ? "" : "s")}" + (IncludeSubfolders ? " · with subfolders" : " · this folder only")
            : FilePaths.Count == 1 ? Path.GetFileName(FilePaths[0]) : $"{FilePaths.Count} clips";

        public string ToolTip => IsFolder
            ? FolderPath
            : string.Join(Environment.NewLine, FilePaths.Take(15)) + (FilePaths.Count > 15 ? $"{Environment.NewLine}+{FilePaths.Count - 15} more" : "");
    }

    /// <summary>One row in the playlist.</summary>
    public sealed class PlaylistItem : INotifyPropertyChanged
    {
        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public long SizeBytes { get; init; }
        public string Size => ReplayText.FormatBytes(SizeBytes);

        private int _number;
        /// <summary>1-based position in the playlist (shown in the first column).</summary>
        public int Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        private TimeSpan? _duration;
        /// <summary>From ffprobe in the background, or when the clip first opens.</summary>
        public TimeSpan? Duration
        {
            get => _duration;
            set
            {
                if (Set(ref _duration, value)) OnPropertyChanged(nameof(DurationText));
            }
        }

        public string DurationText => Duration is TimeSpan d ? ReplayText.FormatTime(d) : "–";

        private bool _isSelected;
        /// <summary>The row's checkbox (for Remove). Unticked by default - the old default was ticked, so "Remove" cleared everything.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        private ClipStatus _status;
        public ClipStatus Status
        {
            get => _status;
            set
            {
                if (!Set(ref _status, value)) return;
                OnPropertyChanged(nameof(IsNowPlaying));
            }
        }

        /// <summary>Highlights the row (PlaylistRowStyle).</summary>
        public bool IsNowPlaying => Status is ClipStatus.Playing or ClipStatus.Converting;

        private string _message = "";
        /// <summary>Why it failed (row tooltip).</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
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

    /// <summary>Remembered in %AppData%\Smart Tools\video_replay.json.</summary>
    public sealed class ReplaySettings
    {
        public ReplayMode Mode { get; set; } = ReplayMode.Sequential;
        public bool RepeatPlaylist { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public double Volume { get; set; } = 1.0;
        public bool Muted { get; set; }
        public double Speed { get; set; } = 1.0;
        public bool PlaylistCollapsed { get; set; }
    }

    public static class ReplayText
    {
        public static string FormatBytes(long bytes) => Smart_Tools.WPF.Controllers.ByteFormat.Format(bytes);

        /// <summary>"4:05", "1:02:03". The old formatter checked Hours (0-23), so a 24-hour recording showed "0:00".</summary>
        public static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
