using System.IO;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Disk Space Analyzer data model. Plain data - no WPF, no I/O.
    //  Names carry a "Space" prefix because every tool shares this
    //  namespace (the old ScanOptions / ScanResult were too generic).
    // =====================================================================

    public enum SpaceItemKind
    {
        Folder,
        File,

        /// <summary>"N smaller items" - the rows hidden by the minimum-size filter, rolled into one.</summary>
        Others,
    }

    /// <summary>What a file is, for icons, colors and the "by type" breakdown.</summary>
    public enum FileCategory
    {
        Folder,
        Video,
        Image,
        Audio,
        Document,
        Archive,
        Code,
        Program,
        Other,
    }

    /// <summary>Settings for one scan, captured from the sidebar.</summary>
    public sealed class SpaceScanOptions
    {
        public string RootPath { get; init; } = "";
        public bool IncludeSubfolders { get; init; } = true;

        /// <summary>Skip System-attribute items and folders like $Recycle.Bin (not walked, not counted).</summary>
        public bool IgnoreSystemFiles { get; init; } = true;

        /// <summary>When false, Hidden items are skipped (not walked, not counted).</summary>
        public bool ShowHiddenItems { get; init; }

        /// <summary>Directory listings in flight at once, across the whole scan.</summary>
        public int MaxDegreeOfParallelism { get; init; } = Math.Max(2, Environment.ProcessorCount);

        /// <summary>For the progress bar on whole-drive scans: bytes in use on the drive (0 = unknown).</summary>
        public long ExpectedBytes { get; init; }
    }

    public readonly record struct SpaceScanProgress(long ItemsScanned, long BytesScanned, long ExpectedBytes, string CurrentPath)
    {
        /// <summary>0-100 when the drive's used space is known, otherwise null (indeterminate).</summary>
        public double? Percent => ExpectedBytes > 0 ? Math.Min(99.9, BytesScanned * 100.0 / ExpectedBytes) : null;
    }

    public sealed class SpaceScanResult
    {
        public SpaceNode? Root { get; init; }
        public long TotalItemsScanned { get; init; }

        /// <summary>Folders that couldn't be read (permissions) - their contents aren't in the totals.</summary>
        public int InaccessibleFolders { get; init; }

        /// <summary>Junctions / symbolic links that were not followed (avoids double counting and loops).</summary>
        public int SkippedLinks { get; init; }

        public TimeSpan Elapsed { get; init; }
        public string? ErrorMessage { get; init; }
        public bool Success => ErrorMessage == null && Root != null;
    }

    /// <summary>
    /// One folder or file in the scanned tree. Kept lean because a drive
    /// scan can hold millions: only the Name is stored and FullPath is
    /// rebuilt from the Parent chain on demand (the old per-node full path
    /// string was most of the memory on big scans). Files share one empty
    /// Children list.
    /// </summary>
    public sealed class SpaceNode
    {
        private static readonly List<SpaceNode> NoChildren = new(0);

        public SpaceNode(string name, SpaceItemKind kind)
        {
            Name = name;
            Kind = kind;
            Children = kind == SpaceItemKind.Folder ? new List<SpaceNode>() : NoChildren;
        }

        /// <summary>File/folder name; for the scan root, its full path.</summary>
        public string Name { get; }
        public SpaceItemKind Kind { get; }
        public DateTime Modified { get; init; }

        /// <summary>Bytes on this PC: a folder's recursive total, a file's length (0 for online-only cloud files).</summary>
        public long SizeBytes { get; set; }

        /// <summary>Online-only cloud bytes (OneDrive "free up space" files) - not stored on this PC.</summary>
        public long CloudBytes { get; set; }

        /// <summary>Descendant files + folders (not counting itself); 0 for files.</summary>
        public int ItemCount { get; set; }

        public List<SpaceNode> Children { get; }

        /// <summary>Null for the scan root.</summary>
        public SpaceNode? Parent { get; set; }

        public bool IsCloudOnly => Kind == SpaceItemKind.File && CloudBytes > 0 && SizeBytes == 0;

        public string FullPath => Parent == null ? Name : Path.Combine(Parent.FullPath, Name);

        /// <summary>Name for breadcrumbs: "C:\" for a drive root, the folder name otherwise.</summary>
        public string DisplayName
        {
            get
            {
                if (Parent != null) return Name;
                string trimmed = Name.TrimEnd('\\', '/');
                string last = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(last) ? Name : last;
            }
        }

        public string Extension => Kind == SpaceItemKind.File ? Path.GetExtension(Name).ToLowerInvariant() : "";
    }

    /// <summary>One row in the list / one tile in the treemap for the folder being viewed.</summary>
    public sealed class SpaceListItem
    {
        public required string Name { get; init; }
        public SpaceItemKind Kind { get; init; }
        public FileCategory Category { get; init; }
        public long SizeBytes { get; init; }
        public required string SizeText { get; init; }

        /// <summary>Share of the folder being viewed, 0-100.</summary>
        public double Percent { get; init; }
        public string PercentText => Percent >= 0.1 ? $"{Percent:0.#}%" : "<0.1%";

        public required string ItemCountText { get; init; }
        public required string ModifiedText { get; init; }

        public bool IsCloudOnly { get; init; }

        /// <summary>The underlying node; null for the "smaller items" row.</summary>
        public SpaceNode? Node { get; init; }

        /// <summary>For the "smaller items" row: the nodes it stands for.</summary>
        public IReadOnlyList<SpaceNode> Grouped { get; init; } = Array.Empty<SpaceNode>();

        public string FullPath => Node?.FullPath ?? "";
        public bool CanOpen => Node != null;
        public bool IsFolder => Kind == SpaceItemKind.Folder;
    }

    /// <summary>A laid-out treemap rectangle.</summary>
    public readonly record struct TreemapTile(SpaceListItem Item, double X, double Y, double Width, double Height);

    /// <summary>One segment of the "space by file type" bar.</summary>
    public sealed class TypeBreakdownEntry
    {
        public FileCategory Category { get; init; }
        public long Bytes { get; init; }
        public int FileCount { get; init; }
        public double Percent { get; init; }
        public required string Label { get; init; }

        /// <summary>Display color for this category (data color, same in both themes).</summary>
        public required string ColorHex { get; init; }
    }

    public sealed class DriveSummary
    {
        public required string RootPath { get; init; }
        public required string Label { get; init; }
        public long TotalBytes { get; init; }
        public long FreeBytes { get; init; }
        public DriveType DriveType { get; init; }
        public string FreeText { get; init; } = "";

        public bool IsFixed => DriveType == DriveType.Fixed;
        public long UsedBytes => TotalBytes - FreeBytes;
        public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
        public required string DisplayName { get; init; }
        public override string ToString() => DisplayName;
    }

    /// <summary>Result of deleting several items.</summary>
    public sealed class SpaceDeleteResult
    {
        public List<SpaceNode> Deleted { get; } = new();
        public List<(SpaceNode Node, string Error)> Failed { get; } = new();
        public long BytesFreed { get; set; }
    }

    /// <summary>Remembered between sessions (space_analyzer.json in %AppData%\Smart Tools).</summary>
    public sealed class SpaceAnalyzerSettings
    {
        public string? LastFolder { get; set; }
        public string? LastDrive { get; set; }
        public bool ScopeDrive { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool IgnoreSystemFiles { get; set; } = true;
        public bool ShowHiddenItems { get; set; }
        public long MinItemSizeBytes { get; set; } = 10L * 1024 * 1024;
        public bool TreemapView { get; set; }
    }
}
