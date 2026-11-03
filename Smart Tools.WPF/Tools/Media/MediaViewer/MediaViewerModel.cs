using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Media Viewer data model. Plain data - no WPF, no disk access.
    // =====================================================================

    public enum ViewerMediaKind
    {
        Image,
        Video,
        Audio,
    }

    /// <summary>What plays after the current item.</summary>
    public enum ViewerPlayOrder
    {
        /// <summary>Down the list, one after the other.</summary>
        Sequential,

        /// <summary>Every item once in a random order.</summary>
        Shuffle,

        /// <summary>The same item again and again until another one is picked.</summary>
        RepeatOne,
    }

    /// <summary>A file or folder added to the list, as saved between sessions.</summary>
    public sealed class MediaSavedSource
    {
        public string Path { get; set; } = "";
        public bool IsFolder { get; set; }
    }

    /// <summary>
    /// Everything the Media Viewer remembers (media_viewer.json). The tool's own
    /// page and the Settings page both write it, each only its own fields - see
    /// MediaViewerManager.UpdateSettings.
    /// </summary>
    public sealed class MediaViewerSettings
    {
        // The viewer's options (saved by the tool).
        public ViewerPlayOrder Order { get; set; } = ViewerPlayOrder.Sequential;
        public bool RepeatPlaylist { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public double Volume { get; set; } = 0.8;
        public bool Muted { get; set; }
        public double Speed { get; set; } = 1;
        public int SlideshowSeconds { get; set; } = 4;
        public bool PlaylistCollapsed { get; set; }

        // The list (saved by the tool; RememberPlaylist is set on the Settings page).
        public bool RememberPlaylist { get; set; } = true;
        public List<MediaSavedSource> Sources { get; set; } = new();
        public string LastItem { get; set; } = "";

        // The widget (position and compact mode saved by the tool; the toggles are on the Settings page).
        public bool ShowWidgetAtStartup { get; set; }
        public bool WidgetCompact { get; set; }
        public double? WidgetLeft { get; set; }
        public double? WidgetTop { get; set; }
        public bool KeepPlayingWithWidget { get; set; } = true;
    }

    /// <summary>What the floating widget shows. The tool's view builds it; the widget only displays it.</summary>
    public sealed record MediaWidgetState(
        bool HasItem,
        string Title,
        string Subtitle,
        ViewerMediaKind Kind,
        bool IsRunning,
        double PositionSeconds,
        double DurationSeconds,
        double Volume,
        bool Muted,
        string SpeedLabel,
        ViewerPlayOrder Order);

    /// <summary>Something the user added: one file or a whole folder.</summary>
    public sealed class MediaSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
    }

    /// <summary>One row in the sidebar's input card: an added folder, or added files grouped by folder.</summary>
    public sealed class MediaQueueGroup
    {
        public required string FolderPath { get; init; }
        public bool IsFolder { get; init; }
        public List<string> FilePaths { get; init; } = new();
        public int ItemCount { get; init; }

        /// <summary>The second line of the row ("12 items · with subfolders"), worded by the view so it can be translated.</summary>
        public string Subtitle { get; set; } = "";

        public string Name
        {
            get
            {
                string trimmed = FolderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string name = System.IO.Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? FolderPath : name;   // drive roots
            }
        }

        public string ToolTip => IsFolder
            ? FolderPath
            : string.Join(Environment.NewLine, FilePaths.Take(15)) + (FilePaths.Count > 15 ? $"{Environment.NewLine}+{FilePaths.Count - 15} more" : "");
    }

    /// <summary>One entry in the Media Viewer list.</summary>
    public sealed class MediaItem : INotifyPropertyChanged
    {
        private bool _isNowPlaying;

        public MediaItem(string path, ViewerMediaKind kind)
        {
            Path = path;
            Kind = kind;
            Name = System.IO.Path.GetFileName(path);
            Folder = System.IO.Path.GetDirectoryName(path) ?? "";
            Icon = kind switch
            {
                ViewerMediaKind.Image => "ImageOutline",
                ViewerMediaKind.Video => "MovieOpenOutline",
                _ => "MusicNote",
            };
        }

        public string Path { get; }
        public string Name { get; }
        public string Folder { get; }
        public ViewerMediaKind Kind { get; }

        /// <summary>Name of a Material Design icon (bound to the list's icon).</summary>
        public string Icon { get; }

        /// <summary>True for the item on the stage - the playlist highlights its row (PlaylistRowStyle).</summary>
        public bool IsNowPlaying
        {
            get => _isNowPlaying;
            set
            {
                if (_isNowPlaying == value) return;
                _isNowPlaying = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNowPlaying)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
