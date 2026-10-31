using System.Globalization;
using System.IO;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Large File Finder data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/LargeFinderView/LargeFinderModels.cs.) Every type
    //  starts with "LargeFile" so it can't clash with another tool's model
    //  (the old SearchOptions / SearchProgress / SearchResult names were
    //  also used by File Search).
    // =====================================================================

    public enum LargeFileScope
    {
        Folder,
        Drive,
    }

    /// <summary>What kind of file it is, for the icon, the colour in the size bar and the filter chips.</summary>
    public enum LargeFileCategory
    {
        Video,
        DiskImage,
        Archive,
        Audio,
        Image,
        Document,
        Program,
        Database,
        Other,
    }

    /// <summary>Everything a scan needs, read from the sidebar.</summary>
    public sealed class LargeFileOptions
    {
        public string RootPath { get; init; } = "";
        public bool IncludeSubfolders { get; init; } = true;

        /// <summary>Skip files and folders marked System, plus $Recycle.Bin, System Volume Information...</summary>
        public bool IgnoreSystemFiles { get; init; } = true;

        /// <summary>Also look in hidden files and folders (AppData is hidden, and often holds big files).</summary>
        public bool ShowHiddenFiles { get; init; }

        public long MinSizeBytes { get; init; } = 100L * 1024 * 1024;

        /// <summary>Keep only the N largest (0 = every match).</summary>
        public int ResultLimit { get; init; } = 50;

        public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 6);
    }

    /// <summary>One file in the results.</summary>
    public sealed class LargeFileItem
    {
        public required string FullPath { get; init; }
        public required long SizeBytes { get; init; }
        public DateTime Modified { get; init; }
        public LargeFileCategory Category { get; init; }

        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public string SizeDisplay => LargeFileText.Bytes(SizeBytes);
        public string TypeLabel => Path.GetExtension(FullPath) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() : "—";
        public string ModifiedDisplay => Modified == default ? "" : Modified.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        public string ModifiedAgo => LargeFileText.Ago(Modified, DateTime.Now);

        /// <summary>0-100 of the biggest file in the list (the bar in the size column).</summary>
        public double BarPercent { get; set; }
    }

    /// <summary>About 7 times a second while scanning; <see cref="Top"/> is a fresh snapshot of the biggest files so far (about twice a second).</summary>
    public sealed class LargeFileProgress
    {
        public long FilesScanned { get; init; }
        public long FoldersScanned { get; init; }
        public int Matches { get; init; }
        public long MatchedBytes { get; init; }
        public string CurrentFolder { get; init; } = "";
        public IReadOnlyList<LargeFileItem>? Top { get; init; }
    }

    public sealed class LargeFileScanResult
    {
        /// <summary>Largest first (ties by path), at most the result limit.</summary>
        public IReadOnlyList<LargeFileItem> Files { get; init; } = Array.Empty<LargeFileItem>();

        /// <summary>Every file at or above the minimum size, even past the limit.</summary>
        public int TotalMatches { get; init; }

        public long TotalMatchedBytes { get; init; }
        public long FilesScanned { get; init; }
        public long FoldersScanned { get; init; }
        public int InaccessibleFolders { get; init; }
        public bool Cancelled { get; init; }
        public TimeSpan Elapsed { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>A drive for the drive list (read off the UI thread - a sleeping network drive can take seconds).</summary>
    public sealed class LargeFileDrive
    {
        public required string RootPath { get; init; }
        public string Label { get; init; } = "";
        public long FreeBytes { get; init; }
        public long TotalBytes { get; init; }
        public bool IsFixed { get; init; }

        public string DisplayName => (string.IsNullOrWhiteSpace(Label) ? RootPath : $"{RootPath} ({Label})")
            + (TotalBytes > 0 ? $" — {LargeFileText.Bytes(FreeBytes)} free of {LargeFileText.Bytes(TotalBytes)}" : "");

        public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp((TotalBytes - FreeBytes) * 100.0 / TotalBytes, 0, 100);
    }

    /// <summary>Progress of a move (bytes copied when the files go to another drive).</summary>
    public readonly record struct LargeFileMoveProgress(int Done, int Total, string CurrentFile, long BytesDone, long BytesTotal)
    {
        public double Percent => BytesTotal <= 0 ? (Total <= 0 ? 0 : Done * 100.0 / Total) : Math.Clamp(BytesDone * 100.0 / BytesTotal, 0, 100);
    }

    public sealed class LargeFileActionResult
    {
        public List<LargeFileItem> Succeeded { get; } = new();
        public List<(LargeFileItem Item, string Error)> Failed { get; } = new();

        /// <summary>Moved files that were copied but whose original couldn't be removed (both copies are there).</summary>
        public List<(LargeFileItem Item, string Error)> Warnings { get; } = new();

        public long Bytes { get; set; }
        public bool Cancelled { get; set; }
        public bool Permanent { get; set; }
    }

    /// <summary>
    /// Remembered between sessions (large_file_finder.json in %AppData%\Smart
    /// Tools). How to search - never which folder or drive.
    /// </summary>
    public sealed class LargeFileSettings
    {
        public LargeFileScope Scope { get; set; } = LargeFileScope.Folder;
        public bool IncludeSubfolders { get; set; } = true;
        public bool IgnoreSystemFiles { get; set; } = true;
        public bool ShowHiddenFiles { get; set; }
        public string MinSize { get; set; } = "100";
        public int MinUnit { get; set; } = 1; // index into Units: 0 KB, 1 MB, 2 GB
        public int ResultLimit { get; set; } = 50;
    }

    public static class LargeFileText
    {
        public static string Plural(long count, string one, string many) => count == 1 ? one : many;

        public static string Bytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes:N0} B";
            string[] units = { "KB", "MB", "GB", "TB" };
            double size = bytes / 1024.0;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.#} {units[unit]}";
        }

        /// <summary>"today", "3 days ago", "5 months ago", "2 years ago".</summary>
        public static string Ago(DateTime when, DateTime now)
        {
            if (when == default || when > now) return "";
            var span = now - when;
            if (span.TotalDays < 1) return "today";
            if (span.TotalDays < 2) return "yesterday";
            if (span.TotalDays < 31) return $"{(int)span.TotalDays} days ago";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30.44)} {Plural((int)(span.TotalDays / 30.44), "month", "months")} ago";
            int years = (int)(span.TotalDays / 365.25);
            return $"{years} {Plural(years, "year", "years")} ago";
        }
    }
}
