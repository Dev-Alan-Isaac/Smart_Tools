using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  File Generator data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/FileGeneratorView/FileGeneratorModels.cs.) Types
    //  start with "FileGen" so they can't clash with another tool's model.
    // =====================================================================

    /// <summary>The kind of file to make (the chips in the sidebar, by name).</summary>
    public enum FileGenFormat
    {
        Txt,
        Csv,
        Json,
        Xml,
        Pdf,
        Docx,
        Bin,
        Dat,
        Jpg,
        Png,
        Mp4,
        Custom,
    }

    public enum FileGenSizeMode
    {
        Exact,
        RandomRange,
    }

    public enum FileGenFill
    {
        Zeroes,
        RandomBytes,
        RepeatedPattern,
        PlaceholderText,
    }

    public enum FileGenNaming
    {
        Sequential,
        PrefixSuffix,
    }

    public enum FileGenArchive
    {
        None,
        Zip,
        Tar,
    }

    public enum FileGenReportFormat
    {
        Json,
        Csv,
        Xml,
    }

    public enum FileGenStatus
    {
        Waiting,
        Creating,
        Created,
        Failed,
        Cancelled,
    }

    /// <summary>Everything a run needs, read from the sidebar.</summary>
    public sealed class FileGenOptions
    {
        public FileGenFormat Format { get; init; } = FileGenFormat.Txt;
        public string CustomExtension { get; init; } = "dat";

        public FileGenSizeMode SizeMode { get; init; } = FileGenSizeMode.Exact;
        public long ExactSizeBytes { get; init; } = 1024 * 1024;
        public long MinSizeBytes { get; init; } = 1024;
        public long MaxSizeBytes { get; init; } = 1024 * 1024;

        public FileGenFill Fill { get; init; } = FileGenFill.RandomBytes;
        public string PatternText { get; init; } = "SmartTools";

        public bool InjectMetadata { get; init; }
        public string MetadataTag { get; init; } = "";

        public FileGenNaming Naming { get; init; } = FileGenNaming.Sequential;
        public string BaseName { get; init; } = "file";
        public string Prefix { get; init; } = "";
        public string Suffix { get; init; } = "";
        public int StartIndex { get; init; } = 1;

        /// <summary>file001, file002 … so names sort in order (file10 no longer lands before file2).</summary>
        public bool PadNumbers { get; init; } = true;

        public int Count { get; init; } = 1;
        public FileGenArchive Archive { get; init; } = FileGenArchive.None;

        /// <summary>With an archive: keep the separate files too (off = only the archive is left).</summary>
        public bool KeepFilesAfterArchive { get; init; } = true;

        public required string OutputFolder { get; init; }
    }

    /// <summary>About ten times a second while generating.</summary>
    public readonly record struct FileGenProgress(int FileIndex, int FileCount, string CurrentFile, long FileBytesDone, long FileBytesTotal, long BytesDone, long BytesTotal)
    {
        public double Percent => BytesTotal <= 0 ? (FileCount <= 0 ? 0 : FileIndex * 100.0 / FileCount) : Math.Clamp(BytesDone * 100.0 / BytesTotal, 0, 100);
    }

    /// <summary>One row in the results grid and the report.</summary>
    public sealed class FileGenItem : INotifyPropertyChanged
    {
        private FileGenStatus _status = FileGenStatus.Waiting;
        private long _sizeBytes;
        private string _message = "";

        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public required long TargetBytes { get; init; }
        public FileGenFormat Format { get; init; }

        public long SizeBytes
        {
            get => _sizeBytes;
            set { _sizeBytes = value; OnChanged(); OnChanged(nameof(SizeDisplay)); OnChanged(nameof(SizeNote)); }
        }

        public FileGenStatus Status
        {
            get => _status;
            set { _status = value; OnChanged(); OnChanged(nameof(SizeDisplay)); OnChanged(nameof(SizeNote)); }
        }

        /// <summary>Why it failed, or a note (for the tooltip).</summary>
        public string Message
        {
            get => _message;
            set { _message = value ?? ""; OnChanged(); }
        }

        public string SizeDisplay => _status == FileGenStatus.Created ? FileGenText.Bytes(_sizeBytes) : "–";
        public string TargetDisplay => FileGenText.Bytes(TargetBytes);

        /// <summary>"exact", or how far off and why (a real PDF/image has a minimum size).</summary>
        public string SizeNote => _status != FileGenStatus.Created ? ""
            : _sizeBytes == TargetBytes ? "exact"
            : _sizeBytes > TargetBytes ? $"smallest possible ({FileGenText.Bytes(TargetBytes)} asked)"
            : $"{FileGenText.Bytes(TargetBytes - _sizeBytes)} under";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class FileGenSummary
    {
        public IReadOnlyList<FileGenItem> Items { get; init; } = Array.Empty<FileGenItem>();
        public string? ArchivePath { get; init; }
        public string? ArchiveError { get; init; }
        public bool Cancelled { get; init; }
        public TimeSpan Elapsed { get; init; }

        public int Created => Items.Count(i => i.Status == FileGenStatus.Created);
        public int Failed => Items.Count(i => i.Status == FileGenStatus.Failed);
        public long TotalBytes => Items.Where(i => i.Status == FileGenStatus.Created).Sum(i => i.SizeBytes);
    }

    /// <summary>The live check under "Save to": names, total size and free space - or why it can't run.</summary>
    public sealed class FileGenPlan
    {
        public string? Error { get; init; }
        public string? Warning { get; init; }
        public string FirstName { get; init; } = "";
        public string LastName { get; init; } = "";
        public long TotalBytesMax { get; init; }
        public long FreeBytes { get; init; } = -1;
        public bool CanRun => Error == null;
    }

    /// <summary>
    /// Remembered between sessions (file_generator.json in %AppData%\Smart
    /// Tools). How files are made - never the output folder.
    /// </summary>
    public sealed class FileGenSettings
    {
        public FileGenFormat Format { get; set; } = FileGenFormat.Txt;
        public string CustomExtension { get; set; } = "dat";
        public FileGenSizeMode SizeMode { get; set; } = FileGenSizeMode.Exact;
        public string ExactSize { get; set; } = "1";
        public int ExactUnit { get; set; } = 2;
        public string MinSize { get; set; } = "1";
        public int MinUnit { get; set; } = 2;
        public string MaxSize { get; set; } = "10";
        public int MaxUnit { get; set; } = 2;
        public FileGenFill Fill { get; set; } = FileGenFill.RandomBytes;
        public string PatternText { get; set; } = "SmartTools";
        public bool InjectMetadata { get; set; }
        public string MetadataTag { get; set; } = "";
        public FileGenNaming Naming { get; set; } = FileGenNaming.Sequential;
        public string BaseName { get; set; } = "file";
        public string Prefix { get; set; } = "";
        public string Suffix { get; set; } = "";
        public int StartIndex { get; set; } = 1;
        public bool PadNumbers { get; set; } = true;
        public int Count { get; set; } = 1;
        public FileGenArchive Archive { get; set; } = FileGenArchive.None;
        public bool KeepFilesAfterArchive { get; set; } = true;
        public FileGenReportFormat ReportFormat { get; set; } = FileGenReportFormat.Json;
    }

    public static class FileGenText
    {
        public static string Plural(long count, string one, string many) => count == 1 ? one : many;

        /// <summary>1024-based, like Windows Explorer ("1 MB" = 1,048,576 bytes).</summary>
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
            return $"{size:0.##} {units[unit]}";
        }
    }
}
