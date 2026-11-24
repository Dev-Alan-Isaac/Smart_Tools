using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  PDF Toolkit data model. Plain data - no WPF or PdfSharp. Colours for
    //  statuses live in the view (theme tokens); the old model built fixed
    //  light-theme brushes here by matching the start of status strings.
    // =====================================================================

    public enum PdfToolOperation
    {
        /// <summary>All ticked PDFs into one, in list order.</summary>
        Merge,

        /// <summary>Each ticked PDF into several.</summary>
        Split,

        /// <summary>A smaller copy of each ticked PDF.</summary>
        Compress,
    }

    public enum PdfSplitMode
    {
        /// <summary>One file per page.</summary>
        EveryPage,

        /// <summary>A new file every N pages (the last may be shorter).</summary>
        EveryNPages,

        /// <summary>The pages typed, e.g. "1-3, 5, 8-".</summary>
        Ranges,
    }

    public enum PdfCompressLevel
    {
        /// <summary>JPEG 85, pictures up to 3,000 px.</summary>
        Light,

        /// <summary>JPEG 72, pictures up to 2,200 px.</summary>
        Balanced,

        /// <summary>JPEG 55, pictures up to 1,500 px.</summary>
        Strong,
    }

    public enum PdfItemStatus
    {
        Ready,
        Working,
        Done,
        Failed,
        Cancelled,

        /// <summary>Nothing to do (e.g. the PDF couldn't be made smaller).</summary>
        Skipped,
    }

    /// <summary>
    /// One row on the Files card: a folder that was added (with or without
    /// its subfolders), or a set of loose PDFs. Taking the row off removes
    /// its PDFs from the list; changing "Include subfolders" reads a folder
    /// row again.
    /// </summary>
    public sealed class PdfToolkitGroup : INotifyPropertyChanged
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

        public List<PdfToolItem> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = $"{Items.Count:N0} {PdfToolText.Plural(Items.Count, "PDF", "PDFs")}";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "PDFs from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One PDF in the list.</summary>
    public sealed class PdfToolItem : INotifyPropertyChanged
    {
        private bool _isIncluded = true;
        private int _number;
        private int _pageCount;
        private PdfItemStatus _status;
        private string _message = "";
        private string _result = "";

        public required string FullPath { get; init; }
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }

        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public string Size => PdfToolText.FormatBytes(SizeBytes);

        /// <summary>The Files card row this PDF came in with (a folder, or loose files).</summary>
        public PdfToolkitGroup? Group { get; set; }

        /// <summary>Goes into the run.</summary>
        public bool IsIncluded
        {
            get => _isIncluded;
            set => Set(ref _isIncluded, value);
        }

        /// <summary>Place in the list (the merge order).</summary>
        public int Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        /// <summary>0 = not read yet, -1 = couldn't be read (see <see cref="Message"/>).</summary>
        public int PageCount
        {
            get => _pageCount;
            set
            {
                if (_pageCount == value) return;
                _pageCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Pages));
            }
        }

        public string Pages => _pageCount switch
        {
            > 0 => _pageCount.ToString("N0"),
            < 0 => "?",
            _ => "…",
        };

        public bool InfoRequested { get; set; }

        public PdfItemStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public string StatusText => _status switch
        {
            PdfItemStatus.Working => "Working",
            PdfItemStatus.Done => "Done",
            PdfItemStatus.Failed => "Failed",
            PdfItemStatus.Cancelled => "Cancelled",
            PdfItemStatus.Skipped => "Skipped",
            _ => "Ready",
        };

        /// <summary>The reason (status tooltip).</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        /// <summary>Short outcome for the Result column ("12 files", "4.2 MB → 1.3 MB").</summary>
        public string Result
        {
            get => _result;
            set => Set(ref _result, value);
        }

        /// <summary>Files this PDF produced in the last run.</summary>
        public List<string> Outputs { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A PDF found on disk.</summary>
    public readonly record struct PdfFileFound(string FullPath, long SizeBytes, DateTime Modified);

    public sealed class PdfFolderScan
    {
        public List<PdfFileFound> Files { get; } = new();
        public int InaccessibleFolders { get; set; }
        public bool Missing { get; set; }
    }

    /// <summary>Pages <c>Start</c> to <c>End</c>, 1-based. <c>End</c> = 0 means "to the last page" ("8-").</summary>
    public readonly record struct PdfPageRange(int Start, int End)
    {
        public bool ToEnd => End == 0;

        public override string ToString() => Start == End ? Start.ToString() : ToEnd ? $"{Start}-" : $"{Start}-{End}";
    }

    /// <summary>What was typed in the page box, read once: the ranges, and anything that couldn't be understood.</summary>
    public sealed class PdfRangeParse
    {
        public List<PdfPageRange> Ranges { get; } = new();
        public List<string> Problems { get; } = new();
        public bool IsValid => Ranges.Count > 0 && Problems.Count == 0;
    }

    /// <summary>Everything a run needs, read from the sidebar when Run is pressed.</summary>
    public sealed class PdfToolOptions
    {
        public PdfToolOperation Operation { get; init; }
        public required string OutputFolder { get; init; }

        // Merge
        public string MergeFileName { get; init; } = "Merged.pdf";

        /// <summary>The merged file was picked in a Save dialog (replacing it was confirmed).</summary>
        public bool OverwriteAllowed { get; init; }

        /// <summary>A bookmark at the first page of each merged file.</summary>
        public bool AddBookmarks { get; init; } = true;

        // Split
        public PdfSplitMode SplitMode { get; init; }
        public int EveryNPages { get; init; } = 2;
        public string Ranges { get; init; } = "";

        /// <summary>Ranges mode: all the typed pages into one file (extract) instead of one file per range.</summary>
        public bool RangesInOneFile { get; init; }

        // Compress
        public PdfCompressLevel CompressLevel { get; init; } = PdfCompressLevel.Balanced;
    }

    public readonly record struct PdfToolProgress(int Processed, int Total, string CurrentFile)
    {
        public double Percent => Total > 0 ? Processed * 100.0 / Total : 0;
    }

    public sealed class PdfToolResult
    {
        public List<string> Files { get; } = new();
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int Pages { get; set; }
        public bool Cancelled { get; set; }
        public TimeSpan Elapsed { get; set; }

        /// <summary>Compress: total size before and after (only files that got smaller).</summary>
        public long BytesBefore { get; set; }
        public long BytesAfter { get; set; }

        /// <summary>Why nothing was written, else null.</summary>
        public string? Error { get; set; }
    }

    /// <summary>What the engine did to one PDF when compressing.</summary>
    public readonly record struct PdfCompressOutcome(int ImagesRecompressed, int ImagesKept);

    /// <summary>Remembered between sessions (pdf_toolkit.json in %AppData%\Smart Tools). No folders or file names.</summary>
    public sealed class PdfToolSettings
    {
        public PdfToolOperation Operation { get; set; } = PdfToolOperation.Merge;
        public PdfSplitMode SplitMode { get; set; } = PdfSplitMode.EveryPage;
        public int EveryNPages { get; set; } = 2;
        public bool RangesInOneFile { get; set; }
        public PdfCompressLevel CompressLevel { get; set; } = PdfCompressLevel.Balanced;
        public bool AddBookmarks { get; set; } = true;
        public bool IncludeSubfolders { get; set; } = true;
        public bool SaveWithoutAsking { get; set; } = true;
    }

    public static class PdfToolText
    {
        public static string FormatBytes(long bytes) => Smart_Tools.WPF.Controllers.ByteFormat.Format(bytes);

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }
}
