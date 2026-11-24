using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Duplicate Finder data model. No WPF types (thumbnails are held as
    //  object so the view can put an ImageSource there); items and groups
    //  raise PropertyChanged because the grid and drawer edit them live.
    // =====================================================================

    /// <summary>How two files are decided to be "the same".</summary>
    public enum DuplicateMatchMode
    {
        /// <summary>Same file name only - contents may differ.</summary>
        FileName,

        /// <summary>Same name and size - contents may still differ.</summary>
        NameAndSize,

        /// <summary>Contents compared byte by byte (no hashing).</summary>
        ByteByByte,

        Crc32,
        Md5,
        Sha1,
        Sha256,

        /// <summary>
        /// Exact copies first (SHA-256), then pictures, videos and songs whose bytes differ
        /// (converted, re-encoded, resized) are matched by what they look or sound like.
        /// </summary>
        SameContent,
    }

    /// <summary>How much a copy may have been changed and still count as "the same content".</summary>
    public enum SameContentLevel
    {
        /// <summary>Only a different file type / container / codec (mp4 to mkv, jpg to png, wav to flac).</summary>
        FormatOnly,

        /// <summary>Also resized, recompressed, colour or grayscale changes.</summary>
        Small,

        /// <summary>Also lightly cropped, watermarked, trimmed edges.</summary>
        Medium,

        /// <summary>Also heavily cropped or rotated pictures (feature matching, slow, capped).</summary>
        Heavy,
    }

    /// <summary>Whether every file in a group is byte-identical or only the same content.</summary>
    public enum DuplicateGroupKind
    {
        Exact,
        SameContent,
    }

    /// <summary>Shortcut file-type filters for the sidebar chips.</summary>
    public enum FileTypeFilter
    {
        All,
        Images,
        Videos,
        Audio,
        Documents,
    }

    /// <summary>Which copy in each group to keep when auto-selecting.</summary>
    public enum KeepRule
    {
        /// <summary>Oldest modified date - copies are usually newer than the original.</summary>
        Oldest,
        Newest,

        /// <summary>Shortest full path - usually the "home" location rather than a nested backup.</summary>
        ShortestPath,

        /// <summary>The copy in the folder listed first in the sidebar.</summary>
        FolderOrder,

        /// <summary>The copy with the most pixels, then the largest file (usually the lossless one).</summary>
        HighestQuality,
    }

    public enum DuplicateScanPhase
    {
        Listing,
        Comparing,
        Verifying,
        Fingerprinting,
        Done,
    }

    public sealed class DuplicateScanOptions
    {
        public List<string> Folders { get; set; } = new();
        public bool IncludeSubfolders { get; set; } = true;
        public DuplicateMatchMode MatchMode { get; set; } = DuplicateMatchMode.Sha256;

        /// <summary>Smaller files are skipped (0-byte files would all "match" each other).</summary>
        public long MinFileSizeBytes { get; set; } = 1;

        public bool IgnoreHiddenFiles { get; set; }
        public bool IgnoreSystemFiles { get; set; } = true;

        /// <summary>After a hash match, confirm with a real byte comparison before trusting the group.</summary>
        public bool VerifyWithByteCompare { get; set; } = true;

        public FileTypeFilter FileTypes { get; set; } = FileTypeFilter.All;

        /// <summary>Extensions to skip, with or without the dot (".tmp", "log").</summary>
        public string[] ExcludedExtensions { get; set; } = Array.Empty<string>();

        /// <summary>When set, only these extensions are scanned (on top of <see cref="FileTypes"/>).</summary>
        public string[] IncludedExtensions { get; set; } = Array.Empty<string>();

        /// <summary>Files read at once. Keep low (2) for hard drives, where parallel reads make seeking thrash.</summary>
        public int MaxDegreeOfParallelism { get; set; } = Math.Max(2, Environment.ProcessorCount);

        /// <summary>Only used by <see cref="DuplicateMatchMode.SameContent"/>.</summary>
        public SameContentLevel SameContentLevel { get; set; } = SameContentLevel.FormatOnly;

        public bool IsContentMode => MatchMode is not (DuplicateMatchMode.FileName or DuplicateMatchMode.NameAndSize);
    }

    /// <summary>Posted about ten times a second.</summary>
    public readonly record struct DuplicateScanProgress(
        DuplicateScanPhase Phase,
        long FilesFound,
        long BytesProcessed,
        long BytesTotal,
        string CurrentPath)
    {
        /// <summary>0-100 while comparing contents; null while listing (unknown total).</summary>
        public double? Percent => Phase switch
        {
            DuplicateScanPhase.Listing => null,
            DuplicateScanPhase.Done => 100,
            _ => BytesTotal > 0 ? Math.Min(100, BytesProcessed * 100.0 / BytesTotal) : 100,
        };
    }

    public sealed class DuplicateScanResult
    {
        public List<DuplicateGroup> Groups { get; init; } = new();
        public long FilesScanned { get; init; }
        public int InaccessibleFolders { get; init; }
        public int SkippedLinks { get; init; }
        public int UnreadableFiles { get; init; }
        public TimeSpan Elapsed { get; init; }
        public DuplicateMatchMode MatchMode { get; init; }

        /// <summary>Same-content mode: videos and songs were only compared byte for byte because ffmpeg is missing.</summary>
        public bool MediaSkippedNoFfmpeg { get; init; }

        /// <summary>Heavy-edits level: more pictures than the feature matcher can pair, so only the first ones took part.</summary>
        public bool HeavyLimited { get; init; }

        /// <summary>More songs than one scan can index; only the first ones (by path) were compared by sound.</summary>
        public bool AudioLimited { get; init; }

        /// <summary>False for name-based modes: files in a group may not be identical.</summary>
        public bool IsContentVerified { get; init; }

        public int DuplicateFileCount => Groups.Sum(g => g.Files.Count);

        /// <summary>Space freed by keeping one copy per group.</summary>
        public long ReclaimableBytes => Groups.Sum(g => g.PotentialBytes);
    }

    /// <summary>A set of files considered duplicates of each other.</summary>
    public sealed class DuplicateGroup : INotifyPropertyChanged
    {
        public int Number { get; set; }
        public List<DuplicateFileItem> Files { get; } = new();
        public string Hash { get; init; } = "";
        public bool IsContentVerified { get; init; }

        /// <summary>Exact copies, or the same content in different formats / encodings.</summary>
        public DuplicateGroupKind Kind { get; init; }

        /// <summary>For <see cref="DuplicateGroupKind.SameContent"/>: how alike the files are, 0-100.</summary>
        public int SimilarityPercent { get; init; }

        /// <summary>Alternating shading in the grid.</summary>
        public bool IsEven => Number % 2 == 0;

        public int FileCount => Files.Count;

        /// <summary>Everything except the largest copy - what keeping one copy would free.</summary>
        public long PotentialBytes => Files.Count < 2 ? 0 : Files.Sum(f => f.SizeBytes) - Files.Max(f => f.SizeBytes);

        /// <summary>What the current selection in this group would free.</summary>
        public long SelectedBytes => Files.Where(f => f.IsSelected).Sum(f => f.SizeBytes);
        public int SelectedCount => Files.Count(f => f.IsSelected);

        /// <summary>Every copy is selected - acting would leave none behind.</summary>
        public bool AllSelected => Files.Count > 0 && Files.All(f => f.IsSelected);

        public string Title => Loc.F("Str_DataQuality_MsgGroupN", "Group {0}", Number);

        public string Subtitle
        {
            get
            {
                string sizes = Files.Select(f => f.SizeBytes).Distinct().Count() == 1
                    ? Loc.F("Str_Dup_MsgSizeEach", "{0} each", DuplicateText.FormatBytes(Files[0].SizeBytes))
                    : Loc.T("Str_Dup_MsgSizesDiffer", "different sizes");
                string text = Loc.F("Str_Dup_MsgGroupSubtitle", "{0} files · {1}", FileCount, sizes);
                if (Kind == DuplicateGroupKind.SameContent)
                    text += " · " + Loc.F("Str_Dup_MsgSameContentGroup", "same content, different format · {0}% alike", SimilarityPercent);
                return text;
            }
        }

        public string SelectionText => SelectedCount == 0
            ? Loc.T("Str_DataQuality_MsgNothingSelected", "nothing selected")
            : Loc.F("Str_DataQuality_MsgSelectedBytes", "{0} selected · {1}", SelectedCount, DuplicateText.FormatBytes(SelectedBytes));

        public string ShortHash => Hash.Length > 12 ? Hash[..12] : Hash;

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

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Call after files' selection or membership changes.</summary>
        public void Refresh()
        {
            foreach (string name in new[] { nameof(SelectedBytes), nameof(SelectedCount), nameof(AllSelected), nameof(SelectionText),
                                             nameof(FileCount), nameof(Subtitle), nameof(PotentialBytes), nameof(Title), nameof(IsEven) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class DuplicateFileItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isKeep;
        private object? _thumbnail;

        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public required string Folder { get; init; }
        public required long SizeBytes { get; init; }
        public string Size => DuplicateText.FormatBytes(SizeBytes);
        public DateTime ModifiedTime { get; init; }
        public string Modified => ModifiedTime == default ? "" : ModifiedTime.ToString("yyyy-MM-dd HH:mm");
        public string Hash { get; init; } = "";
        public string ShortHash => Hash.Length > 12 ? Hash[..12] : Hash;

        /// <summary>Width x height (pictures, video); 0 when unknown. Used by the "highest quality" keep rule.</summary>
        public long Pixels { get; init; }

        public string Extension => Path.GetExtension(Name).ToLowerInvariant();

        /// <summary>Set once groups are numbered.</summary>
        public DuplicateGroup? GroupInfo { get; set; }
        public int Group => GroupInfo?.Number ?? 0;

        /// <summary>Marked for Move/Delete.</summary>
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

        /// <summary>The copy to keep (the "original").</summary>
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

        /// <summary>An ImageSource set by the view (object keeps this model WPF-free).</summary>
        public object? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }

        public bool HasThumbnail => _thumbnail != null;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Old helper kept for callers elsewhere.
        public static string FormatBytes(long bytes) => DuplicateText.FormatBytes(bytes);
    }

    /// <summary>One entry in the "Folders to search" list.</summary>
    /// <remarks>Moved here from the Views namespace; setters kept so other views that build these keep compiling.</remarks>
    public sealed class FolderListEntry
    {
        public string FullPath { get; set; } = "";
        public bool IsFolder { get; set; } = true;

        /// <summary>Old combined label, kept for existing callers.</summary>
        public string DisplayName => IsFolder ? $"{Name}  (folder)" : Name;

        public string Name
        {
            get
            {
                string trimmed = FullPath.TrimEnd('\\', '/');
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? FullPath : name;   // "C:\" stays "C:\"
            }
        }
    }

    /// <summary>Result of moving or deleting a batch.</summary>
    public sealed class DuplicateActionResult
    {
        public List<DuplicateFileItem> Succeeded { get; } = new();
        public List<(DuplicateFileItem Item, string Error)> Failed { get; } = new();
        public long Bytes { get; set; }
    }

    /// <summary>Remembered between sessions (duplicate_finder.json in %AppData%\Smart Tools).</summary>
    public sealed class DuplicateFinderSettings
    {
        public List<string> Folders { get; set; } = new();
        public bool IncludeSubfolders { get; set; } = true;
        public bool IgnoreHiddenFiles { get; set; }
        public bool IgnoreSystemFiles { get; set; } = true;
        public bool VerifyWithByteCompare { get; set; } = true;
        public DuplicateMatchMode MatchMode { get; set; } = DuplicateMatchMode.Sha256;
        public FileTypeFilter FileTypes { get; set; } = FileTypeFilter.All;
        public long MinFileSizeBytes { get; set; } = 1;
        public string ExcludedExtensions { get; set; } = "";
        public KeepRule KeepRule { get; set; } = KeepRule.Oldest;
        public SameContentLevel SameContentLevel { get; set; } = SameContentLevel.FormatOnly;
        public bool SendToRecycleBin { get; set; } = true;

        [JsonIgnore] public bool IsEmpty => Folders.Count == 0;
    }

    public static class DuplicateText
    {
        public static string FormatBytes(long bytes) => Smart_Tools.WPF.Controllers.ByteFormat.Format(bytes);
    }
}
