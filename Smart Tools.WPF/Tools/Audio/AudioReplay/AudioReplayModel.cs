using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Audio Replay data model.
    //
    //  Plain data only - no NAudio, no WPF controls, no file I/O. The
    //  control binds to these; AudioReplayManager creates and updates them.
    //  Same split as ApiModel / ApiManager.
    // =====================================================================

    /// <summary>
    /// How the playlist advances. Order matches CmbReplayMode's items.
    /// (Named AudioReplayMode, not ReplayMode, so it can't collide with the
    /// video tool's ReplayMode if both namespaces are ever imported together.)
    /// </summary>
    public enum AudioReplayMode
    {
        /// <summary>Every track once, in list order, then stop.</summary>
        Sequential = 0,

        /// <summary>Every track once, in a random order, then stop.</summary>
        Shuffle = 1,

        /// <summary>Repeat the current track until the user skips or stops.</summary>
        Loop = 2,
    }

    public enum AudioPlaybackState
    {
        /// <summary>Nothing loaded.</summary>
        Idle,
        Playing,
        Paused,

        /// <summary>Track still loaded but rewound to 0:00 (Stop button).</summary>
        Stopped,
    }

    /// <summary>One row in the playlist grid.</summary>
    public sealed class AudioPlaylistItem : INotifyPropertyChanged
    {
        public const string UnknownDuration = "--:--";

        public required string FilePath { get; init; }
        public required string Name { get; init; }

        /// <summary>Filled in by the background probe; null until known (or if unreadable).</summary>
        public TimeSpan? TotalTime { get; private set; }

        private string _duration = UnknownDuration;
        public string Duration
        {
            get => _duration;
            private set { if (_duration != value) { _duration = value; OnPropertyChanged(); } }
        }

        private bool _isSelected;
        /// <summary>The "flag for removal" checkbox.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        private bool _isNowPlaying;
        public bool IsNowPlaying
        {
            get => _isNowPlaying;
            set { if (_isNowPlaying != value) { _isNowPlaying = value; OnPropertyChanged(); } }
        }

        /// <summary>Sets the probed length (null = couldn't be read).</summary>
        public void SetDuration(TimeSpan? totalTime, string formatted)
        {
            TotalTime = totalTime;
            Duration = totalTime.HasValue ? formatted : UnknownDuration;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One Add Files / Add Folder / drag-drop batch, shown as a row in the
    /// sidebar queue list. Removing the row removes exactly these items.
    /// Subtitle now tracks the live count (it used to keep saying
    /// "120 file(s)" after tracks were removed individually).
    /// </summary>
    public sealed class AudioQueueGroup : INotifyPropertyChanged
    {
        private bool _includesSubfolders;

        public required string FolderName { get; init; }
        public string FolderPath { get; init; } = "";

        /// <summary>Changes when "Include subfolders" is ticked or unticked after the folder was added (it is read again).</summary>
        public bool IncludesSubfolders
        {
            get => _includesSubfolders;
            set
            {
                _includesSubfolders = value;
                OnPropertyChanged(nameof(Subtitle));
            }
        }

        public bool IsFolder => FolderPath.Length > 0;

        public List<AudioPlaylistItem> Items { get; } = new();

        public string Subtitle =>
            $"{Items.Count:N0} file(s)" + (IncludesSubfolders ? ", incl. subfolders" : "");

        /// <summary>Call after Items changes so the bound subtitle updates.</summary>
        public void NotifyItemsChanged() => OnPropertyChanged(nameof(Subtitle));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// What re-reading a folder changes: which of its tracks are gone and
    /// which files are new (not already anywhere in the playlist). Pure
    /// calculation - AudioReplayManager.RescanFolderAsync applies it.
    /// </summary>
    public sealed record AudioRescanPlan(IReadOnlyList<AudioPlaylistItem> Gone, IReadOnlyList<string> New)
    {
        public static AudioRescanPlan Create(IEnumerable<AudioPlaylistItem> groupItems, IEnumerable<string> foundFiles, ISet<string> pathsInPlaylist)
        {
            var found = foundFiles.ToList();
            var foundSet = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
            var gone = groupItems.Where(i => !foundSet.Contains(i.FilePath)).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fresh = found.Where(f => !pathsInPlaylist.Contains(f) && seen.Add(f)).ToList();
            return new AudioRescanPlan(gone, fresh);
        }
    }

    /// <summary>Outcome of an add operation, for the view to report.</summary>
    public sealed record AudioAddResult(
        int Added,
        int Duplicates,
        AudioQueueGroup? Group,
        string? Error = null)
    {
        public bool Succeeded => Error == null;
    }
}
