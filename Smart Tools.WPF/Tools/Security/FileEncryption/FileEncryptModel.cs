using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  File Encryption - plain data only. Planning, naming and settings are
    //  in Controllers/EncryptManager.cs, the file format in
    //  EncryptManager.Crypto.cs and running a batch in EncryptManager.Run.cs.
    // =====================================================================

    /// <summary>The two mode tiles (read by Tag = enum name).</summary>
    public enum EncryptMode
    {
        Encrypt,
        Decrypt
    }

    /// <summary>Row status - shown as a themed chip by DataTriggers (no brushes in code).</summary>
    public enum EncryptStatus
    {
        Ready,
        Working,
        Done,
        Failed,
        Cancelled,

        /// <summary>Doesn't fit the mode (already encrypted, or not an encrypted file).</summary>
        Skipped
    }

    /// <summary>What a file's first bytes say it is.</summary>
    public enum EncryptedFormat
    {
        /// <summary>Not a file this tool encrypted.</summary>
        None,

        /// <summary>Older files: PBKDF2 100,000 per file, no quick password check. Still decrypted.</summary>
        Stf2,

        /// <summary>Current files: PBKDF2 600,000 per batch + HKDF per file, quick password check.</summary>
        Stf3
    }

    /// <summary>Password meter levels (the four segments light up to this level).</summary>
    public enum PasswordStrength
    {
        Empty,
        Weak,
        Fair,
        Good,
        Strong
    }

    /// <summary>Meter level plus the one thing that would help most.</summary>
    public readonly record struct PasswordRating(PasswordStrength Strength, int Score, string Hint);

    /// <summary>Something the user added: one file or a whole folder.</summary>
    public sealed class EncryptSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
    }

    /// <summary>One sidebar row: an added folder, or added files grouped by folder.</summary>
    public sealed class EncryptQueueGroup
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
            ? (IncludeSubfolders ? "Whole folder, with subfolders" : "This folder only")
            : FilePaths.Count == 1 ? Path.GetFileName(FilePaths[0]) : $"{FilePaths.Count} files";

        public string ToolTip => IsFolder
            ? FolderPath
            : string.Join(Environment.NewLine, FilePaths.Take(15)) + (FilePaths.Count > 15 ? $"{Environment.NewLine}+{FilePaths.Count - 15} more" : "");
    }

    /// <summary>One row in the file grid.</summary>
    public sealed class EncryptItem : INotifyPropertyChanged
    {
        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public required long SizeBytes { get; init; }
        public string Size => EncryptText.FormatBytes(SizeBytes);

        /// <summary>From the file's header when it was listed.</summary>
        public EncryptedFormat Format { get; init; }
        public bool IsEncrypted => Format != EncryptedFormat.None;

        private string _outputName = "";
        /// <summary>Planned name (the run may add " (2)" if it's taken by then).</summary>
        public string OutputName
        {
            get => _outputName;
            set => Set(ref _outputName, value);
        }

        private string? _outputPath;
        public string? OutputPath
        {
            get => _outputPath;
            set => Set(ref _outputPath, value);
        }

        private EncryptStatus _status = EncryptStatus.Ready;
        public EncryptStatus Status
        {
            get => _status;
            set
            {
                if (!Set(ref _status, value)) return;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsWorking));
            }
        }

        public string StatusText => Status switch
        {
            EncryptStatus.Working => Progress >= 1 ? $"{Progress:0}%" : "Working",
            EncryptStatus.Done => "Done",
            EncryptStatus.Failed => "Failed",
            EncryptStatus.Cancelled => "Cancelled",
            EncryptStatus.Skipped => "Skipped",
            _ => "Ready",
        };

        public bool IsWorking => Status == EncryptStatus.Working;

        private double _progress;
        /// <summary>0-100 for the file being worked on.</summary>
        public double Progress
        {
            get => _progress;
            set
            {
                if (!Set(ref _progress, value)) return;
                if (Status == EncryptStatus.Working) OnPropertyChanged(nameof(StatusText));
            }
        }

        private string _message = "";
        /// <summary>Why it was skipped or failed, or where it was saved (status tooltip).</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

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

    /// <summary>What the run bar shows (reported about every 100 ms).</summary>
    public readonly record struct EncryptProgress(int Completed, int Total, string CurrentFile, double CurrentFilePercent, double OverallPercent);

    /// <summary>Everything a run needs. The password is never saved anywhere.</summary>
    public sealed class EncryptOptions
    {
        public EncryptMode Mode { get; init; } = EncryptMode.Encrypt;
        public required string Password { get; init; }

        /// <summary>After a file is done, remove the original.</summary>
        public bool DeleteOriginal { get; init; }

        /// <summary>Recycle Bin where the drive has one; otherwise deleted permanently (the view warns first).</summary>
        public bool DeleteToRecycleBin { get; init; } = true;

        /// <summary>PBKDF2 rounds for new files. Tests lower it; files store their own count.</summary>
        public int Iterations { get; init; } = 600_000;

        /// <summary>Files at once; 0 = automatic.</summary>
        public int MaxDegreeOfParallelism { get; init; }
    }

    /// <summary>Result of a run.</summary>
    public sealed class EncryptRunResult
    {
        public int Done { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public int Skipped { get; set; }
        public long Bytes { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>Remembered between sessions in %AppData%\Smart Tools\file_encryption.json - never the password.</summary>
    public sealed class EncryptSettings
    {
        public EncryptMode Mode { get; set; } = EncryptMode.Encrypt;
        public bool IncludeSubfolders { get; set; } = true;
        public bool DeleteOriginal { get; set; }
    }

    public static class EncryptText
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

        public static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
