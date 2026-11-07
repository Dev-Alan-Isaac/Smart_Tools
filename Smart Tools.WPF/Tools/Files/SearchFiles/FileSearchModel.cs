using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  File Search data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/SearchView/SearchModels.cs plus SearchFolderEntry in
    //  the view's code-behind.) Every type starts with "FileSearch" so it
    //  can't clash with another tool's model (SearchOptions, SearchProgress
    //  are common names).
    // =====================================================================

    /// <summary>How the search text is read. One at a time (chips in the sidebar).</summary>
    /// <summary>How Export writes the results.</summary>
    public enum FileSearchExportFormat
    {
        /// <summary>A spreadsheet: every column (opens in Excel).</summary>
        Csv,

        /// <summary>Just the values, one per line, each once - grouped by pattern when several were ticked.</summary>
        TextValues,

        /// <summary>Readable report: file, line, pattern and the match for every row.</summary>
        TextReport,
    }

    public enum FileSearchMode
    {
        Text,
        Wildcard,
        Regex,
    }

    public enum FileSearchMatchKind
    {
        FileName,
        Content,
    }

    /// <summary>One pattern to search with: a saved one from Settings, one typed here, or the text box.</summary>
    public sealed record FileSearchPattern(string Name, string Pattern, bool IsWildcard = false);

    /// <summary>Everything the search needs, read from the sidebar when Search is clicked.</summary>
    public sealed class FileSearchOptions
    {
        public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();
        public FileSearchMode Mode { get; init; } = FileSearchMode.Text;

        /// <summary>Text / Wildcard mode.</summary>
        public string SearchText { get; init; } = "";

        /// <summary>Regex mode: every pattern is used together (a line can match several).</summary>
        public IReadOnlyList<FileSearchPattern> Patterns { get; init; } = Array.Empty<FileSearchPattern>();

        public bool SearchFileNames { get; init; } = true;
        public bool SearchFileContents { get; init; } = true;
        public bool IgnoreCase { get; init; } = true;
        public bool WholeWord { get; init; }
        public bool IncludeSubfolders { get; init; } = true;

        /// <summary>
        /// Each different match is listed once, wherever it is found: the
        /// first place (in folder, file and line order) is shown, and the row
        /// counts how many more times and in how many files it was seen.
        /// </summary>
        public bool UniqueValues { get; init; }

        /// <summary>At most one row per file. With UniqueValues: the file's first match that isn't already listed.</summary>
        public bool FirstMatchPerFile { get; init; }

        public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    /// <summary>One row in the results grid.</summary>
    public sealed class FileSearchResult : INotifyPropertyChanged
    {
        private int _occurrences = 1;
        private int _fileCount = 1;
        private IReadOnlyList<string> _otherFiles = Array.Empty<string>();

        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";

        /// <summary>Just the text that matched (not the whole line).</summary>
        public required string MatchText { get; init; }

        /// <summary>The whole line, trimmed and shortened, for the tooltip.</summary>
        public string LineText { get; init; } = "";

        /// <summary>Which pattern(s) found it ("Email", "Email, URL"); empty in Text mode.</summary>
        public string PatternName { get; init; } = "";

        public static readonly IReadOnlyDictionary<string, string> NoSegments = new Dictionary<string, string>();

        /// <summary>The named parts of the match (group name → text), e.g. year=2026. Empty when the pattern has no named groups.</summary>
        public IReadOnlyDictionary<string, string> Segments { get; init; } = NoSegments;

        /// <summary>The parts as one line, "year=2026, month=10, day=31"; empty when there are none.</summary>
        public string SegmentsText => string.Join(", ", Segments.Select(kv => $"{kv.Key}={kv.Value}"));

        /// <summary>1-based; 0 for a file-name match.</summary>
        public int LineNumber { get; init; }

        public int Column { get; init; }
        public FileSearchMatchKind Kind { get; init; }

        public string LineLabel => LineNumber > 0 ? $"{LineNumber}:{Column}" : "";
        public string KindLabel => Kind == FileSearchMatchKind.FileName ? Loc.T("Str_SearchFiles_KindName", "Name") : Loc.T("Str_SearchFiles_KindContent", "Content");

        /// <summary>Unique mode: how many times this value was found in total.</summary>
        public int Occurrences
        {
            get => _occurrences;
            set { if (_occurrences != value) { _occurrences = value; OnChanged(); OnChanged(nameof(SeenLabel)); OnChanged(nameof(SeenMore)); } }
        }

        /// <summary>Unique mode: in how many different files.</summary>
        public int FileCount
        {
            get => _fileCount;
            set { if (_fileCount != value) { _fileCount = value; OnChanged(); OnChanged(nameof(SeenLabel)); } }
        }

        /// <summary>Unique mode: a few of the other files it was also found in (for the tooltip).</summary>
        public IReadOnlyList<string> OtherFiles
        {
            get => _otherFiles;
            set { _otherFiles = value; OnChanged(); OnChanged(nameof(SeenTooltip)); }
        }

        /// <summary>Sets the unique-mode totals (call on the UI thread once the row is shown).</summary>
        public void ApplySeen(int occurrences, int fileCount, IReadOnlyList<string> otherFiles)
        {
            Occurrences = occurrences;
            FileCount = fileCount;
            OtherFiles = otherFiles;
        }

        public bool SeenMore => _occurrences > 1;

        public string SeenLabel => _occurrences <= 1 ? Loc.T("Str_SearchFiles_SeenOnce", "once") : _fileCount <= 1 ? $"×{_occurrences:N0}" : $"×{_occurrences:N0} · {Smart_Tools.WPF.Controllers.SearchCounts.Files(_fileCount)}";

        public string SeenTooltip => _otherFiles.Count == 0
            ? Loc.T("Str_SearchFiles_FoundOnlyHere", "Found only here")
            : Loc.T("Str_SearchFiles_AlsoFoundIn", "Also found in:") + "\n" + string.Join("\n", _otherFiles) + (_fileCount - 1 > _otherFiles.Count ? "\n" + Loc.F("Str_SearchFiles_AndMoreFiles", "… and {0:N0} more files", _fileCount - 1 - _otherFiles.Count) : "");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A unique value seen again: new totals for a row already shown (applied on the UI thread).</summary>
    public readonly record struct FileSearchSeenUpdate(FileSearchResult Row, int Occurrences, int FileCount, IReadOnlyList<string> OtherFiles);

    public readonly record struct FileSearchSkip(string Path, string Reason);

    /// <summary>Sent about 10 times a second: counts plus the rows found since the last report.</summary>
    public sealed class FileSearchProgress
    {
        public long FilesScanned { get; init; }
        public long TotalFiles { get; init; }
        public int Results { get; init; }
        public long MatchesSeen { get; init; }
        public string CurrentFile { get; init; } = "";
        public bool StillListing { get; init; }
        public IReadOnlyList<FileSearchResult> NewResults { get; init; } = Array.Empty<FileSearchResult>();
        public IReadOnlyList<FileSearchSeenUpdate> SeenUpdates { get; init; } = Array.Empty<FileSearchSeenUpdate>();

        public double Percent => TotalFiles <= 0 ? 0 : Math.Clamp(FilesScanned * 100.0 / TotalFiles, 0, 100);
    }

    public sealed class FileSearchSummary
    {
        public IReadOnlyList<FileSearchResult> Results { get; init; } = Array.Empty<FileSearchResult>();
        public long FilesScanned { get; init; }
        public long TotalFiles { get; init; }
        public int FilesWithMatches { get; init; }

        /// <summary>Every match, counting repeats (Results has one row per value in unique mode).</summary>
        public long MatchesSeen { get; init; }

        public IReadOnlyList<FileSearchSkip> Skipped { get; init; } = Array.Empty<FileSearchSkip>();
        public int InaccessibleFolders { get; init; }

        /// <summary>Files with a line longer than the read limit (searched in pieces: a match across a cut can be missed).</summary>
        public int LongLineFiles { get; init; }
        public bool Truncated { get; init; }
        public bool Cancelled { get; init; }
        public TimeSpan Elapsed { get; init; }
    }

    /// <summary>A root folder in the sidebar list (kept for this session only).</summary>
    public sealed class FileSearchFolder
    {
        public FileSearchFolder(string fullPath)
        {
            FullPath = fullPath;
            string name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Name = string.IsNullOrEmpty(name) ? fullPath : name;   // a drive root has no name
        }

        public string FullPath { get; }
        public string Name { get; }
    }

    /// <summary>A pattern in the sidebar list that can be ticked; several can be ticked at once.</summary>
    public sealed class FileSearchPatternChoice : INotifyPropertyChanged
    {
        private bool _isChecked;
        private string _name = "";
        private string _pattern = "";
        private string? _error;

        /// <summary>From Settings (shared, can't be removed here) or typed in this tool (has a remove button).</summary>
        public bool IsSaved { get; init; }

        public bool IsWildcard { get; init; }

        public string Name
        {
            get => _name;
            set { _name = value ?? ""; OnChanged(); }
        }

        public string Pattern
        {
            get => _pattern;
            set { _pattern = value ?? ""; OnChanged(); OnChanged(nameof(Tooltip)); }
        }

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnChanged(); } }
        }

        /// <summary>Why it can't be used (null when it can).</summary>
        public string? Error
        {
            get => _error;
            set { _error = value; OnChanged(); OnChanged(nameof(HasError)); OnChanged(nameof(Tooltip)); }
        }

        public bool HasError => _error != null;

        public string Tooltip => (IsWildcard ? Loc.T("Str_SearchFiles_TipWildcard", "Wildcard (whole file name or line): ") : "") + _pattern + (_error != null ? "\n⚠ " + _error : "");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Remembered between sessions (file_search.json in %AppData%\Smart Tools).
    /// How the search is set up - never the folders. Saved patterns are
    /// remembered by name (they live in Settings).
    /// </summary>
    public sealed class FileSearchSettings
    {
        public FileSearchMode Mode { get; set; } = FileSearchMode.Text;
        public string SearchText { get; set; } = "";
        public bool SearchFileNames { get; set; } = true;
        public bool SearchFileContents { get; set; } = true;
        public bool IgnoreCase { get; set; } = true;
        public bool WholeWord { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool UniqueValues { get; set; }
        public bool FirstMatchPerFile { get; set; }
        public List<string> CheckedSavedPatterns { get; set; } = new();
        public List<string> CustomPatterns { get; set; } = new();
        public List<string> CheckedCustomPatterns { get; set; } = new();

        /// <summary>The last Export choice, offered first next time.</summary>
        public FileSearchExportFormat ExportFormat { get; set; } = FileSearchExportFormat.Csv;
    }

    public static class FileSearchText
    {
        public static string Plural(long count, string one, string many) => count == 1 ? one : many;
    }
}
