using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Secure File Shredder data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/ShredderView/ShredderModels.cs, which held WPF
    //  brushes with fixed colours.)
    // =====================================================================

    /// <summary>How each file is overwritten before it's deleted.</summary>
    public enum ShredMethod
    {
        /// <summary>One pass of zeros - quick, enough for modern hard drives.</summary>
        QuickZero,

        /// <summary>Zeros, ones, then random (the classic DoD 5220.22-M sequence).</summary>
        Dod3Pass,

        /// <summary>Seven passes of random data. (The old view called this "Gutmann"; real Gutmann is 35 set patterns.)</summary>
        Random7Pass,
    }

    public enum ShredStatus
    {
        Waiting,
        Working,
        Shredded,
        Failed,
        Skipped,
    }

    /// <summary>What the sidebar says, captured when Shred is clicked.</summary>
    public sealed class ShredOptions
    {
        public ShredMethod Method { get; init; } = ShredMethod.Dod3Pass;

        /// <summary>Before deleting: cut the file to 0 bytes, reset its dates and give it a random name, so the folder's records don't give away what it was.</summary>
        public bool ScrambleName { get; init; }

        /// <summary>Remove folders that this shred left empty (never folders that were empty before).</summary>
        public bool RemoveEmptiedFolders { get; init; } = true;
    }

    /// <summary>A file in the queue.</summary>
    public sealed class ShredItem : INotifyPropertyChanged
    {
        public ShredItem(string fullPath, long sizeBytes, FileAttributes attributes)
        {
            FullPath = fullPath;
            Name = Path.GetFileName(fullPath);
            Folder = Path.GetDirectoryName(fullPath) ?? "";
            SizeBytes = sizeBytes;
            Attributes = attributes;
        }

        public string FullPath { get; }
        public string Name { get; }
        public string Folder { get; }
        public long SizeBytes { get; }
        public FileAttributes Attributes { get; }
        public string SizeText => ShredText.Bytes(SizeBytes);

        private bool _isChecked = true;
        /// <summary>Ticked rows are the ones Shred works on.</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }

        private ShredStatus _status = ShredStatus.Waiting;
        public ShredStatus Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        private string _message = "Waiting";
        /// <summary>Short status text, or the plain reason (shown in the status chip's tooltip).</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        private double _progress;
        /// <summary>0-100 while this file is being overwritten.</summary>
        public double Progress
        {
            get => _progress;
            set => Set(ref _progress, value);
        }

        /// <summary>The sidebar entry that added this row.</summary>
        public ShredQueueGroup? Group { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>A sidebar entry: an added folder (with or without subfolders), or loose files from one folder.</summary>
    public sealed class ShredQueueGroup : INotifyPropertyChanged
    {
        public required string FolderPath { get; init; }
        public bool IsFolder { get; init; }
        public bool IncludeSubfolders { get; init; }

        private int _count;
        public int Count
        {
            get => _count;
            set
            {
                if (_count == value) return;
                _count = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
            }
        }

        public string? SingleFileName { get; set; }

        public string Name
        {
            get
            {
                string trimmed = FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? FolderPath : name;
            }
        }

        public string Subtitle
        {
            get
            {
                string files = $"{Count:N0} {ShredText.Plural(Count, "file", "files")}";
                if (IsFolder) return files + (IncludeSubfolders ? " · whole folder" : " · this folder only");
                return Count == 1 && SingleFileName != null ? SingleFileName : files;
            }
        }

        public string ToolTip => IsFolder ? FolderPath : $"Files added from {FolderPath}";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Progress of a run: files finished, bytes written across all passes.</summary>
    public readonly record struct ShredProgress(int FilesDone, int FilesTotal, long BytesWritten, long BytesTotal, string CurrentFile, bool Finishing)
    {
        public double Percent => BytesTotal > 0 ? Math.Clamp(BytesWritten * 100.0 / BytesTotal, 0, 100)
                               : FilesTotal > 0 ? FilesDone * 100.0 / FilesTotal : 0;
    }

    public sealed record ShredSummary(int Shredded, int Failed, int Skipped, long BytesFreed, int FoldersRemoved, bool Stopped);

    /// <summary>Remembered between sessions (shredder.json in %AppData%\Smart Tools). No paths.</summary>
    public sealed class ShredderSettings
    {
        public ShredMethod Method { get; set; } = ShredMethod.Dod3Pass;
        public bool ScrambleName { get; set; } = true;
        public bool RemoveEmptiedFolders { get; set; } = true;
        public bool ConfirmBeforeShredding { get; set; } = true;
        public bool IncludeSubfolders { get; set; } = true;
    }

    public static class ShredText
    {
        public static string Bytes(long bytes)
        {
            if (bytes < 0) return "";
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

        public static string Plural(int count, string one, string many) => count == 1 ? one : many;
    }
}
