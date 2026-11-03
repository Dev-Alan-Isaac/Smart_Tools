using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Find Similar Files data model. No WPF types: thumbnails are held as
    //  object (the view assigns an ImageSource) and the old Visibility
    //  properties are gone - the XAML uses DataTriggers on IsKeep instead.
    // =====================================================================

    /// <summary>
    /// How similarity is measured. Every value the old combo box had is kept
    /// so saved settings and other code still parse, but only the ones
    /// <see cref="IsImplemented"/> reports are offered in the sidebar (the
    /// old view listed nine methods that just threw "not implemented").
    /// </summary>
    public enum SimilarDetectionMethod
    {
        // Images
        PHash,
        WHash,
        DHash,
        Ssim,
        FeatureMatching,

        // Video
        VideoFrameHash,
        VideoFrameSsim,
        VideoPerceptualHash,
        VideoSceneDetection,
        VideoMotionFlow,

        // Audio
        AudioChromaprint,
        AudioMfcc,
        AudioSpectrogramSsim,
        AudioShazam,
    }

    public enum MediaKind
    {
        Image,
        Video,
        Audio,
        Unsupported,
    }

    /// <summary>Which copy in each group to keep when auto-selecting.</summary>
    public enum SimilarKeepRule
    {
        /// <summary>Highest resolution, then largest file - usually the original.</summary>
        BestQuality,
        LargestFile,
        Oldest,
        Newest,
    }

    public enum SimilarScanPhase
    {
        Listing,
        Fingerprinting,
        Grouping,
        Done,
    }

    public sealed class SimilarScanOptions
    {
        public List<string> Folders { get; init; } = new();
        public bool IncludeSubfolders { get; init; } = true;
        public bool IgnoreSystemFiles { get; init; } = true;
        public SimilarDetectionMethod Method { get; init; } = SimilarDetectionMethod.PHash;

        /// <summary>50-100: how alike files must be to be grouped.</summary>
        public double ThresholdPercent { get; init; } = 85;

        /// <summary>Files fingerprinted at once (also bounds concurrent ffmpeg processes).</summary>
        public int MaxDegreeOfParallelism { get; set; } = Math.Max(2, Environment.ProcessorCount);
    }

    /// <summary>Posted about ten times a second.</summary>
    public readonly record struct SimilarScanProgress(SimilarScanPhase Phase, int Processed, int Total, string CurrentPath)
    {
        /// <summary>0-100 while fingerprinting; null while listing.</summary>
        public double? Percent => Phase switch
        {
            SimilarScanPhase.Listing => null,
            SimilarScanPhase.Fingerprinting => Total > 0 ? Processed * 100.0 / Total : 100,
            _ => 100,
        };
    }

    public sealed class SimilarScanResult
    {
        public List<SimilarGroup> Groups { get; init; } = new();
        public MediaKind Kind { get; init; }
        public int FilesScanned { get; init; }
        public int UnreadableFiles { get; init; }
        public int InaccessibleFolders { get; init; }
        public int SkippedLinks { get; init; }
        public TimeSpan Elapsed { get; init; }

        public int FileCount => Groups.Sum(g => g.ItemCount);
        public long ReclaimableBytes => Groups.Sum(g => g.PotentialBytes);
    }

    /// <summary>One file in a similarity group.</summary>
    public sealed class SimilarFileItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isKeep;
        private object? _thumbnail;

        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public required string Folder { get; init; }
        public required long SizeBytes { get; init; }
        public string Size => SimilarText.FormatBytes(SizeBytes);
        public MediaKind Kind { get; init; }
        public DateTime ModifiedTime { get; init; }
        public string Modified => ModifiedTime == default ? "" : ModifiedTime.ToString("yyyy-MM-dd HH:mm");

        /// <summary>Fingerprint as hex (shown in the Hash column).</summary>
        public string Hash { get; init; } = "";

        /// <summary>Seconds, for video and audio (0 = unknown).</summary>
        public double DurationSeconds { get; init; }
        public string Duration => DurationSeconds > 0 ? SimilarText.FormatDuration(DurationSeconds) : "";

        public int Width { get; init; }
        public int Height { get; init; }
        public string Resolution => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "";
        public long PixelCount => (long)Width * Height;

        /// <summary>"4032x3024 · 3.1 MB" / "0:42 · 1920x1080 · 12 MB" - the compare cards' detail line.</summary>
        public string Details => string.Join(" · ", new[] { Duration, Resolution, Size }.Where(s => s.Length > 0));

        public bool HasDuration => Duration.Length > 0;
        public bool HasResolution => Resolution.Length > 0;

        /// <summary>Set once groups are numbered.</summary>
        public SimilarGroup? GroupInfo { get; set; }

        /// <summary>Old group id, kept for callers that read it.</summary>
        public int Group => GroupInfo?.Id ?? 0;

        /// <summary>Marked for Move / Delete.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
                GroupInfo?.Refresh();
            }
        }

        /// <summary>The copy to keep.</summary>
        public bool IsKeep
        {
            get => _isKeep;
            set
            {
                if (_isKeep == value) return;
                _isKeep = value;
                OnPropertyChanged();
            }
        }

        /// <summary>An ImageSource set by the view.</summary>
        public object? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static string FormatBytes(long bytes) => SimilarText.FormatBytes(bytes);
    }

    /// <summary>Files judged similar to each other - always one media kind.</summary>
    public sealed class SimilarGroup : INotifyPropertyChanged
    {
        /// <summary>Position in the results (1-based, renumbered after files are removed).</summary>
        public int Id { get; set; }
        public List<SimilarFileItem> Items { get; init; } = new();
        public int SimilarityPercent { get; set; }
        public MediaKind Kind { get; init; }

        public int ItemCount => Items.Count;

        /// <summary>What keeping only the keeper would free.</summary>
        public long PotentialBytes => Items.Where(i => !i.IsKeep).Sum(i => i.SizeBytes);

        /// <summary>Old name, kept for callers.</summary>
        public long ReclaimableBytes => PotentialBytes;

        public long SelectedReclaimableBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        public int SelectedCount => Items.Count(i => i.IsSelected);
        public bool AllSelected => Items.Count > 0 && Items.All(i => i.IsSelected);

        public string Title => $"Group {Id}";
        public string Subtitle => $"{ItemCount} files · {SimilarityPercent}% alike";
        public string SelectionText => SelectedCount == 0
            ? "nothing selected"
            : $"{SelectedCount} selected · {SimilarText.FormatBytes(SelectedReclaimableBytes)}";

        private bool _isExpanded = true;

        /// <summary>Whether the group's rows are showing. Lives on the group, not the grid row, so scrolling and recycling can't mix states up.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        /// <summary>Makes <paramref name="chosen"/> the keeper and selects every other copy.</summary>
        public void SetKeep(SimilarFileItem chosen)
        {
            foreach (var item in Items)
            {
                bool keep = ReferenceEquals(item, chosen);
                item.IsKeep = keep;
                item.IsSelected = !keep;
            }
            Refresh();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            foreach (string name in new[] { nameof(SelectedReclaimableBytes), nameof(SelectedCount), nameof(AllSelected), nameof(SelectionText),
                                             nameof(ItemCount), nameof(Subtitle), nameof(PotentialBytes), nameof(Title) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Remembered between sessions (similar_files.json in %AppData%\Smart Tools).</summary>
    public sealed class SimilarFinderSettings
    {
        public List<string> Folders { get; set; } = new();
        public SimilarDetectionMethod Method { get; set; } = SimilarDetectionMethod.PHash;
        public double ThresholdPercent { get; set; } = 85;
        public bool IncludeSubfolders { get; set; } = true;
        public bool IgnoreSystemFiles { get; set; } = true;
        public SimilarKeepRule KeepRule { get; set; } = SimilarKeepRule.BestQuality;
        public bool SendToRecycleBin { get; set; } = true;
    }

    public static class SimilarText
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

        /// <summary>"m:ss", or "h:mm:ss" past an hour - like media players.</summary>
        public static string FormatDuration(double totalSeconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }
    }

    /// <summary>Media kind by extension.</summary>
    public static class FileTypeDetector
    {
        private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif",
        };

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ts", ".mts", ".m2ts",
        };

        private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff", ".aif",
        };

        public static MediaKind Detect(string path)
        {
            string ext = Path.GetExtension(path);
            if (ImageExt.Contains(ext)) return MediaKind.Image;
            if (VideoExt.Contains(ext)) return MediaKind.Video;
            if (AudioExt.Contains(ext)) return MediaKind.Audio;
            return MediaKind.Unsupported;
        }
    }
}
