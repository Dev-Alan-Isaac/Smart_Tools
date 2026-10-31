using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Image Viewer data model. Plain data - no WPF types: a thumbnail is
    //  held as object (the view assigns an ImageSource).
    // =====================================================================

    public enum ImageBrowseMode
    {
        Sequential,
        Shuffle,
    }

    /// <summary>One image in the list.</summary>
    public sealed class ImageQueueItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isNowPlaying;
        private object? _thumbnail;
        private int _number;

        public required string FullPath { get; init; }
        public long SizeBytes { get; init; }

        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public string SizeDisplay => ImageViewerText.FormatBytes(SizeBytes);

        /// <summary>The sidebar entry (folder or batch of files) this image came from.</summary>
        public ImageQueueGroup? Group { get; set; }

        /// <summary>1-based place in the list.</summary>
        public int Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        /// <summary>Ticked for "Remove".</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        /// <summary>
        /// The image on screen. Named like Video Replay's flag so the shared
        /// PlaylistRowStyle highlights the row - the old "IsNowShowing" never
        /// matched it, so the current image was never highlighted.
        /// </summary>
        public bool IsNowPlaying
        {
            get => _isNowPlaying;
            set => Set(ref _isNowPlaying, value);
        }

        /// <summary>An ImageSource set by the view, loaded only for rows on screen.</summary>
        public object? Thumbnail
        {
            get => _thumbnail;
            set => Set(ref _thumbnail, value);
        }

        public bool ThumbnailRequested { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// One sidebar row per Add Images / Add Folder / drop. Updates its own
    /// subtitle when images are removed (the old one was removed and
    /// re-inserted into the list to force a redraw).
    /// </summary>
    public sealed class ImageQueueGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }
        public bool IncludeSubfolders { get; init; }
        public List<ImageQueueItem> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = $"{Items.Count:N0} {ImageViewerText.Plural(Items.Count, "image", "images")}";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "Images from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>An image file found on disk.</summary>
    public readonly record struct ImageFileFound(string FullPath, long SizeBytes);

    public sealed class ImageFolderScan
    {
        public List<ImageFileFound> Files { get; } = new();
        public int InaccessibleFolders { get; set; }
        public int LinksSkipped { get; set; }
        public bool Missing { get; set; }
    }

    /// <summary>What's shown about the current image.</summary>
    public sealed record ImageViewInfo(int Width, int Height, string Format, long SizeBytes, bool ShownSmaller, int OriginalWidth, int OriginalHeight);

    /// <summary>Remembered between sessions (image_viewer.json in %AppData%\Smart Tools). No file or folder paths.</summary>
    public sealed class ImageViewerSettings
    {
        public ImageBrowseMode BrowseMode { get; set; } = ImageBrowseMode.Sequential;
        public int SlideshowSeconds { get; set; } = 3;
        public bool IncludeSubfolders { get; set; } = true;
        public bool PlaylistCollapsed { get; set; }
    }

    public static class ImageViewerText
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

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }
}
