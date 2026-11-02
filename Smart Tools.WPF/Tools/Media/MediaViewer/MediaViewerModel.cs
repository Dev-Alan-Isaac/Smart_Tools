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
