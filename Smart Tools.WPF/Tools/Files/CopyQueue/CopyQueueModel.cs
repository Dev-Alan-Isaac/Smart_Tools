using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    public enum CopyQueueMode { Copy, Move }

    /// <summary>What to do when a different file is already at the target. Nothing is ever overwritten.</summary>
    public enum CopyQueueConflict { KeepBoth, Skip }

    public enum CopyQueueStatus { Waiting, Working, Verifying, Done, AlreadyThere, Skipped, Failed, Cancelled }

    /// <summary>A file or folder the user added to the queue.</summary>
    public sealed class CopyQueueSource
    {
        public CopyQueueSource(string path)
        {
            Path = path;
            IsFolder = Directory.Exists(path);
            Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (Name.Length == 0) Name = path;
            Parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? "";
        }

        public string Path { get; }
        public bool IsFolder { get; }
        public string Name { get; }
        public string Parent { get; }
    }

    public sealed class CopyQueueItem : INotifyPropertyChanged
    {
        private CopyQueueStatus _status = CopyQueueStatus.Waiting;
        private string _message = "";
        private string _destination = "";

        public required string Source { get; init; }
        public string Destination { get => _destination; set => Set(ref _destination, value); }
        public long Size { get; init; }
        public string Name => System.IO.Path.GetFileName(Source);

        public CopyQueueStatus Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(StatusText)); } }
        public string Message { get => _message; set => Set(ref _message, value); }

        public string StatusText => Status switch
        {
            CopyQueueStatus.Waiting => Loc.T("Str_Files_StatusWaiting", "Waiting"),
            CopyQueueStatus.Working => Loc.T("Str_Files_StatusCopying", "Copying"),
            CopyQueueStatus.Verifying => Loc.T("Str_Files_StatusVerifying", "Verifying"),
            CopyQueueStatus.Done => Loc.T("Str_Files_StatusDone", "Done"),
            CopyQueueStatus.AlreadyThere => Loc.T("Str_Files_StatusAlreadyThere", "Already there"),
            CopyQueueStatus.Skipped => Loc.T("Str_Files_StatusSkipped", "Skipped"),
            CopyQueueStatus.Failed => Loc.T("Str_Files_StatusFailed", "Failed"),
            _ => Loc.T("Str_Files_StatusStopped", "Stopped"),
        };

        /// <summary>Bytes of work this file adds to the progress bar (copy pass, plus the verify pass).</summary>
        public long PlannedBytes { get; set; }

        /// <summary>True once the file is at its target and (for a move) gone from the source - what Undo walks back.</summary>
        public bool Moved { get; set; }

        /// <summary>True once the copy was compared to the original and matched.</summary>
        public bool Verified { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name!);
            return true;
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class CopyQueueOptions
    {
        public CopyQueueMode Mode { get; set; } = CopyQueueMode.Copy;
        public CopyQueueConflict Conflict { get; set; } = CopyQueueConflict.KeepBoth;
        /// <summary>Compare every copy to its original. A move between drives is always verified, whatever this says.</summary>
        public bool Verify { get; set; } = true;
        public string DestinationFolder { get; set; } = "";
    }

    /// <summary>Every file the queue will handle, worked out before anything is touched.</summary>
    public sealed class CopyQueuePlan
    {
        public List<CopyQueueItem> Items { get; } = new();
        /// <summary>Folders to create at the target even when empty, so an empty folder isn't lost.</summary>
        public List<string> Folders { get; } = new();
        public List<string> Notes { get; } = new();
        public long BytesTotal { get; set; }
    }

    public sealed record CopyQueueProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile, double BytesPerSecond);

    public sealed class CopyQueueResult
    {
        public int Done { get; set; }
        public int AlreadyThere { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public int Verified { get; set; }
        public long BytesCopied { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>The job in progress, kept on disk only until it finishes so a restart can pick it up.</summary>
    public sealed class CopyQueueSavedJob
    {
        public List<string> Sources { get; set; } = new();
        public string DestinationFolder { get; set; } = "";
        public CopyQueueMode Mode { get; set; }
        public CopyQueueConflict Conflict { get; set; }
        public bool Verify { get; set; } = true;
    }

    /// <summary>What the last move did, so it can be put back - saved to disk like Organize's undo.</summary>
    public sealed class CopyQueueJournal
    {
        public DateTime When { get; set; }
        public List<CopyQueueJournalEntry> Entries { get; set; } = new();
    }

    public sealed class CopyQueueJournalEntry
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }
}
