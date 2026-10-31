using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Background Remover data model.
    //
    //  Plain data only - no Magick.NET, no WPF controls, no file I/O. The
    //  view binds to these; BackgroundRemoverManager creates and updates
    //  them. Same split as ApiModel / AudioReplayModel.
    // =====================================================================

    /// <summary>
    /// What replaces the removed background. This is chroma-key removal
    /// (colour tolerance + Transparent()), not AI segmentation: it removes a
    /// specific background COLOUR, so it suits solid / near-solid backdrops
    /// (product shots, green or white screens), not busy photographic ones.
    /// </summary>
    public enum BackgroundRemoverMode
    {
        Transparent = 0,
        SolidColor = 1,
        ImageReplacement = 2,
    }

    public enum BackgroundRemoverOutputFormat
    {
        Png = 0,
        Jpg = 1,
        WebP = 2,
    }

    /// <summary>
    /// Per-file state. The view colours its status chip from THIS (via theme
    /// resources), not from hard-coded hex strings - the old StatusBg/StatusFg
    /// were light-theme pastels that glared in Dark.
    /// </summary>
    public enum BackgroundRemoverStatus
    {
        Pending,
        Processing,
        Removed,
        Skipped,
        Failed,
    }

    public sealed class BackgroundRemoverOptions
    {
        public BackgroundRemoverMode Mode { get; init; } = BackgroundRemoverMode.Transparent;

        /// <summary>The background colour being keyed OUT of the source image.</summary>
        public string TargetColorHex { get; init; } = "#FFFFFF";

        /// <summary>
        /// 0-100. Higher catches more near-target shades (JPEG noise, soft
        /// shadows on a seamless backdrop) but can eat into the subject's edges.
        /// </summary>
        public double Tolerance { get; init; } = 10;

        /// <summary>
        /// Soft matte + colour decontamination instead of a hard on/off key.
        /// Edge pixels (anti-aliasing, JPEG blur) get partial transparency and
        /// have the background colour un-mixed, so no white/black halo shows on
        /// the new background. False = the old hard ImageMagick key.
        /// </summary>
        public bool CleanEdges { get; init; } = true;

        /// <summary>0-100. How wide the partial-transparency band is above Tolerance (CleanEdges only).</summary>
        public double EdgeSoftness { get; init; } = 35;

        /// <summary>0-3 px. Shrinks the cut-out's alpha to trim the last fringe ring (CleanEdges only).</summary>
        public double EdgeChoke { get; init; }

        public bool TrimEdges { get; init; } = true;

        /// <summary>Used when Mode == SolidColor.</summary>
        public string ReplacementColorHex { get; init; } = "#FFFFFF";

        /// <summary>Used when Mode == ImageReplacement.</summary>
        public string? ReplacementImagePath { get; init; }

        public BackgroundRemoverOutputFormat OutputFormat { get; init; } = BackgroundRemoverOutputFormat.Png;

        /// <summary>1-100, applies to JPG and WebP.</summary>
        public int Quality { get; init; } = 90;

        public bool IncludeSubfolders { get; init; }
        public bool OverwriteExisting { get; init; }
        public string NamingSuffix { get; init; } = "_nobg";

        /// <summary>Empty = write next to each source file.</summary>
        public string OutputFolder { get; init; } = "";
    }

    /// <summary>
    /// One queued file - shown in both the results grid and the queue grid
    /// (two views over one collection). Property changes always happen on
    /// the UI thread (see BackgroundRemoverProgress).
    /// </summary>
    public sealed class BackgroundRemoverItem : INotifyPropertyChanged
    {
        public required string FullPath { get; init; }
        public string DisplayName { get; init; } = "";
        public long SizeBytes { get; init; }
        public string SizeDisplay { get; init; } = "";

        /// <summary>The Files card row this image came in with (a folder, or loose files).</summary>
        public BackgroundRemoverGroup? Group { get; set; }

        private object? _thumbnail;
        /// <summary>An ImageSource set by the view, loaded only for rows scrolled into view.</summary>
        public object? Thumbnail
        {
            get => _thumbnail;
            set { if (!ReferenceEquals(_thumbnail, value)) { _thumbnail = value; OnPropertyChanged(); } }
        }

        public bool ThumbnailRequested { get; set; }

        private bool _isSelected = true;
        /// <summary>Included in the next run (and the target of the queue's bulk Remove).</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        private BackgroundRemoverStatus _status = BackgroundRemoverStatus.Pending;
        public BackgroundRemoverStatus Status
        {
            get => _status;
            private set { if (_status != value) { _status = value; OnPropertyChanged(); } }
        }

        private string _statusText = "Pending";
        /// <summary>Human-readable status ("Removed", "Skipped (exists)", "Error: ...").</summary>
        public string StatusText
        {
            get => _statusText;
            private set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        private string _outputPath = "";
        public string OutputPath
        {
            get => _outputPath;
            private set { if (_outputPath != value) { _outputPath = value; OnPropertyChanged(); } }
        }

        public void SetStatus(BackgroundRemoverStatus status, string text, string? outputPath = null)
        {
            Status = status;
            StatusText = text;
            if (outputPath != null) OutputPath = outputPath;
        }

        public void ResetStatus() => SetStatus(BackgroundRemoverStatus.Pending, "Pending", "");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One row on the Files card: a folder that was added (with or without
    /// its subfolders), or a set of loose files. Taking the row off removes
    /// its images from the queue; changing "Include subfolders" reads a
    /// folder row again.
    /// </summary>
    public sealed class BackgroundRemoverGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }

        private bool _includeSubfolders;
        /// <summary>Changes when "Include subfolders" is ticked or unticked after the folder was added.</summary>
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set { _includeSubfolders = value; Refresh(); }
        }

        public List<BackgroundRemoverItem> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = Items.Count == 1 ? "1 image" : $"{Items.Count:N0} images";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "Images from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Reported when a file starts and when it finishes. The WORKER never
    /// touches the item; Progress&lt;T&gt; marshals this to the UI thread and
    /// the view applies it there (setting a bound property from a worker
    /// thread would crash WPF).
    /// </summary>
    public sealed class BackgroundRemoverProgress
    {
        public required BackgroundRemoverItem Item { get; init; }
        public required BackgroundRemoverStatus Status { get; init; }
        public required string StatusText { get; init; }
        public string OutputPath { get; init; } = "";

        public required int Processed { get; init; }
        public required int Total { get; init; }
        public int Succeeded { get; init; }
        public int Skipped { get; init; }
        public int Failed { get; init; }

        public double Percent => Total == 0 ? 0 : 100.0 * Processed / Total;
    }

    public sealed class BackgroundRemoverResult
    {
        public int SucceededCount { get; init; }
        public int SkippedCount { get; init; }
        public int FailedCount { get; init; }
        public TimeSpan Elapsed { get; init; }
        public int Total => SucceededCount + SkippedCount + FailedCount;
    }

    /// <summary>Before/after thumbnails for the live preview (PNG-encoded).</summary>
    public sealed record BackgroundRemoverPreview(
        byte[] OriginalPng,
        byte[] ResultPng,
        int SourceWidth,
        int SourceHeight);
}
