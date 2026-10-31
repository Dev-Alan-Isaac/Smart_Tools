using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    public enum ProcessKillerFilter { All, Apps, Background, Windows }

    /// <summary>App = has a window on the desktop; Windows = runs as a system/service account.</summary>
    public enum ProcessKillerKind { App, Background, Windows }

    /// <summary>Why a process can't be ended from here (None = it can).</summary>
    public enum ProcessKillerBlock { None, Critical, Self, SystemCore }

    public enum ProcessKillerEndOutcome { Ended, AccessDenied, AlreadyExited, Blocked, Failed }

    /// <summary>One process as read in one refresh (immutable; merged into <see cref="ProcessKillerRow"/>s).</summary>
    public sealed record ProcessKillerSample(
        int Pid,
        int ParentPid,
        long CreationTicks,
        string Name,
        string? Path,
        string? Owner,
        string? WindowTitle,
        int Threads,
        int SessionId,
        double CpuPercent,
        long MemoryBytes,
        bool IsCritical,
        ProcessKillerKind Kind);

    public sealed record ProcessKillerSnapshot(
        IReadOnlyList<ProcessKillerSample> Processes,
        double TotalCpuPercent,
        long MemoryUsedBytes,
        long MemoryTotalBytes);

    public sealed record ProcessKillerEndResult(int Pid, string Name, ProcessKillerEndOutcome Outcome, string? Message);

    /// <summary>A process holding a file open (from Windows' Restart Manager).</summary>
    public sealed record ProcessKillerLocker(int Pid, long CreationTicks, string AppName);

    /// <summary>How the tool is set up. Never holds paths or process names.</summary>
    public sealed class ProcessKillerSettings
    {
        /// <summary>0 = paused.</summary>
        public int RefreshSeconds { get; set; } = 2;
        public ProcessKillerFilter Filter { get; set; } = ProcessKillerFilter.All;
        public string SortColumn { get; set; } = nameof(ProcessKillerRow.Name);
        public bool SortDescending { get; set; }
        public bool SidebarCollapsed { get; set; }
    }

    /// <summary>A row in the list. Identity is (Pid, CreationTicks), so a reused PID is a new row.</summary>
    public sealed class ProcessKillerRow : INotifyPropertyChanged
    {
        private int _parentPid;
        private string? _path;
        private string? _owner;
        private string? _windowTitle;
        private int _threads;
        private double _cpuPercent;
        private long _memoryBytes;
        private bool _isCritical;
        private ProcessKillerKind _kind;
        private string? _parentName;

        public ProcessKillerRow(int pid, long creationTicks, string name)
        {
            Pid = pid;
            CreationTicks = creationTicks;
            Name = name;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Pid { get; }
        public long CreationTicks { get; }
        public string Name { get; }

        public bool IsSelf { get; init; }

        public int ParentPid { get => _parentPid; set => Set(ref _parentPid, value); }
        public string? Path { get => _path; set => Set(ref _path, value); }
        public string? Owner { get => _owner; set { if (Set(ref _owner, value)) Raise(nameof(OwnerText)); } }
        public string? WindowTitle { get => _windowTitle; set { if (Set(ref _windowTitle, value)) Raise(nameof(HasWindow)); } }
        public int Threads { get => _threads; set => Set(ref _threads, value); }
        public double CpuPercent { get => _cpuPercent; set { if (Set(ref _cpuPercent, value)) Raise(nameof(CpuText)); } }
        public long MemoryBytes { get => _memoryBytes; set { if (Set(ref _memoryBytes, value)) Raise(nameof(MemoryText)); } }
        public bool IsCritical { get => _isCritical; set { if (Set(ref _isCritical, value)) { Raise(nameof(Block)); Raise(nameof(CanEnd)); } } }
        public ProcessKillerKind Kind { get => _kind; set => Set(ref _kind, value); }
        public string? ParentName { get => _parentName; set => Set(ref _parentName, value); }

        public bool HasWindow => !string.IsNullOrEmpty(WindowTitle);

        public DateTime? StartedLocal => CreationTicks > 0 ? DateTime.FromFileTime(CreationTicks) : null;

        public ProcessKillerBlock Block =>
            IsSelf ? ProcessKillerBlock.Self
            : Pid is 0 or 4 ? ProcessKillerBlock.SystemCore
            : IsCritical ? ProcessKillerBlock.Critical
            : ProcessKillerBlock.None;

        public bool CanEnd => Block == ProcessKillerBlock.None;

        public string CpuText => CpuPercent < 0.05 ? "0" : CpuPercent.ToString(CpuPercent < 10 ? "0.0" : "0");

        public string MemoryText => MemoryBytes < 0 ? "—" : FormatBytes(MemoryBytes);

        public string OwnerText => string.IsNullOrEmpty(Owner) ? "—" : Owner!;

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:0} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:0.0} MB";
            return $"{mb / 1024.0:0.00} GB";
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
