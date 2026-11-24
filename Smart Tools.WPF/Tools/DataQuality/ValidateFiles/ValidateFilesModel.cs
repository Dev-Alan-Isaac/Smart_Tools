using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Validate Files - plain data only. Planning, signatures, reports and
    //  settings are in Controllers/ValidateManager.cs; the checks in
    //  ValidateManager.Checks.cs, ffmpeg in .Media.cs, pictures in
    //  .Images.cs, and running a scan in .Run.cs.
    // =====================================================================

    /// <summary>What to check (the tiles, read by Tag = enum name). Auto picks per file by extension.</summary>
    public enum ValidationType
    {
        Auto,
        Video,
        Image,
        Audio,
        Document,
        Archive
    }

    /// <summary>What kind of file a row is.</summary>
    public enum ValidationKind
    {
        Other,
        Video,
        Image,
        Audio,
        Document,
        Archive
    }

    /// <summary>Row status - shown as a themed chip by DataTriggers (the old converter used fixed colours).</summary>
    public enum ValidationStatus
    {
        Pending,
        Checking,
        Valid,
        Warning,
        Corrupted,

        /// <summary>Couldn't be checked (in use, access denied, no codec) - says nothing about the file.</summary>
        Unknown,

        /// <summary>Left out on purpose (over the size limit).</summary>
        Skipped,
        Cancelled
    }

    /// <summary>How much of a video is decoded when looking for damaged frames.</summary>
    public enum VideoScanDepth
    {
        /// <summary>A few seconds at the start and end.</summary>
        Quick,

        /// <summary>Short samples spread through the file.</summary>
        Standard,

        /// <summary>Every frame (slowest, most thorough).</summary>
        Full
    }

    public enum SizeUnit
    {
        KB,
        MB,
        GB,
        TB
    }

    /// <summary>Something the user added: one file or a whole folder.</summary>
    public sealed class ValidationSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
    }

    /// <summary>One sidebar row: an added folder, or added files grouped by folder.</summary>
    public sealed class ValidationQueueGroup
    {
        public required string FolderPath { get; init; }
        public bool IsFolder { get; init; }
        public List<string> FilePaths { get; init; } = new();
        public bool IncludeSubfolders { get; init; }

        public string Name
        {
            get
            {
                string trimmed = FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? FolderPath : name;   // drive roots
            }
        }

        public string Subtitle => IsFolder
            ? (IncludeSubfolders ? Loc.T("Str_Validate_SrcWholeFolder", "Whole folder, with subfolders") : Loc.T("Str_Validate_SrcThisFolder", "This folder only"))
            : FilePaths.Count == 1 ? Path.GetFileName(FilePaths[0]) : Loc.F("Str_Validate_SrcNFiles", "{0} files", FilePaths.Count);

        public string ToolTip => IsFolder
            ? FolderPath
            : string.Join(Environment.NewLine, FilePaths.Take(15)) + (FilePaths.Count > 15 ? $"{Environment.NewLine}+{FilePaths.Count - 15} more" : "");
    }

    /// <summary>One row in the results grid (listed before the scan, filled in live).</summary>
    public sealed class FileValidationResult : INotifyPropertyChanged
    {
        public required string FullPath { get; init; }
        public string FileName => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public long SizeBytes { get; init; }
        public string Size => ValidateText.FormatBytes(SizeBytes);
        public ValidationKind Category { get; init; }

        /// <summary>"Video", "Image"... or the extension for other files.</summary>
        public string FileType => Category == ValidationKind.Other
            ? (Path.GetExtension(FullPath) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() : "File")
            : Category.ToString();

        private ValidationStatus _status = ValidationStatus.Pending;
        public ValidationStatus Status
        {
            get => _status;
            set
            {
                if (!Set(ref _status, value)) return;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsProblem));
            }
        }

        public string StatusText => ValidateText.StatusLabel(Status);

        /// <summary>Corrupted, warning or couldn't-check - what "Only problems" shows.</summary>
        public bool IsProblem => Status is ValidationStatus.Corrupted or ValidationStatus.Warning or ValidationStatus.Unknown;

        private string _details = "";
        public string Details
        {
            get => _details;
            set => Set(ref _details, value);
        }

        private bool _isSelected;
        /// <summary>The row's checkbox - what Quarantine and Remove act on.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        private string? _quarantinedTo;
        /// <summary>Where Quarantine moved the file (null = still in place).</summary>
        public string? QuarantinedTo
        {
            get => _quarantinedTo;
            set
            {
                if (Set(ref _quarantinedTo, value)) OnPropertyChanged(nameof(CurrentPath));
            }
        }

        public string CurrentPath => QuarantinedTo ?? FullPath;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Everything a scan needs, read from the sidebar.</summary>
    public sealed class ValidationOptions
    {
        public ValidationType Type { get; set; } = ValidationType.Auto;
        public bool IncludeSubfolders { get; set; } = true;

        /// <summary>0 = no limit.</summary>
        public long SkipFilesLargerThanBytes { get; set; }

        /// <summary>Files checked at once; 0 = automatic.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        // Auto: which kinds get format checks (the rest get the basic checks).
        public bool AutoVideo { get; set; } = true;
        public bool AutoImage { get; set; } = true;
        public bool AutoAudio { get; set; } = true;
        public bool AutoDocument { get; set; } = true;
        public bool AutoArchive { get; set; } = true;

        // Video
        public bool VideoCheckContainer { get; set; } = true;
        public bool VideoCheckStream { get; set; } = true;
        public bool VideoCheckAudioStream { get; set; } = true;
        public bool VideoCheckDuration { get; set; }
        public bool VideoCheckFrames { get; set; }
        public VideoScanDepth VideoScanDepth { get; set; } = VideoScanDepth.Standard;

        // Image
        public bool ImageCheckHeader { get; set; } = true;
        public bool ImageFullDecode { get; set; } = true;
        public bool ImageCheckTruncated { get; set; } = true;
        public bool ImageCheckExif { get; set; }
        public bool ImageFlagSmall { get; set; }

        // Audio
        public bool AudioCheckHeader { get; set; } = true;
        public bool AudioFullDecode { get; set; } = true;
        public bool AudioCheckTruncated { get; set; } = true;
        public bool AudioCheckDuration { get; set; }
        public bool AudioFlagSilent { get; set; }

        // Document
        public bool DocCheckStructure { get; set; } = true;
        public bool DocVerifyOpens { get; set; } = true;
        public bool DocCheckEmbeddedImages { get; set; }
        public bool DocCheckEmbeddedFonts { get; set; }
        public bool DocFlagPassword { get; set; } = true;

        // Archive
        public bool ArchiveVerifyCrc { get; set; } = true;
        public bool ArchiveTestExtract { get; set; } = true;
        public bool ArchiveFlagPassword { get; set; } = true;
        public bool ArchiveCheckBomb { get; set; }

        public ValidationOptions Clone() => (ValidationOptions)MemberwiseClone();
    }

    /// <summary>What the run bar shows (reported about every 100 ms).</summary>
    public readonly record struct ValidationProgress(int Done, int Total, string CurrentFile, double Percent);

    /// <summary>Totals for a finished (or cancelled) scan.</summary>
    public sealed class ValidationRunResult
    {
        public int Valid { get; set; }
        public int Warning { get; set; }
        public int Corrupted { get; set; }
        public int Unknown { get; set; }
        public int Cancelled { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int Checked => Valid + Warning + Corrupted + Unknown;
    }

    /// <summary>Remembered in %AppData%\Smart Tools\validate_files.json.</summary>
    public sealed class ValidationSettings
    {
        public ValidationOptions Options { get; set; } = new();
        public int SkipSizeValue { get; set; }
        public SizeUnit SkipSizeUnit { get; set; } = SizeUnit.MB;
        public bool QuarantineAfterScan { get; set; }
        public string QuarantineFolder { get; set; } = "";
        public bool ReportAfterScan { get; set; }
        public bool OnlyProblems { get; set; }
    }

    public static class ValidateText
    {
        public static string FormatBytes(long bytes) => Smart_Tools.WPF.Controllers.ByteFormat.Format(bytes);

        public static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        public static string StatusLabel(ValidationStatus s) => s switch
        {
            ValidationStatus.Checking => Loc.T("Str_Validate_StChecking", "Checking"),
            ValidationStatus.Valid => Loc.T("Str_Validate_StValid", "Valid"),
            ValidationStatus.Warning => Loc.T("Str_Validate_StWarning", "Warning"),
            ValidationStatus.Corrupted => Loc.T("Str_Validate_StCorrupted", "Corrupted"),
            ValidationStatus.Unknown => Loc.T("Str_Validate_StUnknown", "Couldn't check"),
            ValidationStatus.Skipped => Loc.T("Str_Validate_StSkipped", "Skipped"),
            ValidationStatus.Cancelled => Loc.T("Str_Validate_StCancelled", "Cancelled"),
            _ => Loc.T("Str_Validate_StWaiting", "Waiting"),
        };
    }
}
