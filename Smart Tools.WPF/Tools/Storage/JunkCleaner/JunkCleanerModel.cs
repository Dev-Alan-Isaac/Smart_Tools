using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Junk File Cleaner data model. Plain data - no WPF.
    // =====================================================================

    public enum JunkCategory
    {
        WindowsTemp,
        BrowserCache,
        ThumbnailCache,
        RecycleBin,
        WindowsUpdateCache,
        LogFiles,

        /// <summary>Crash dumps and Windows Error Reporting files.</summary>
        ErrorReports,
    }

    /// <summary>One place to look: a category, the folders, which files count, and how old they must be.</summary>
    public sealed class JunkLocation
    {
        public required JunkCategory Category { get; init; }

        /// <summary>"Windows temporary files", "Google Chrome", …</summary>
        public required string Title { get; init; }

        public List<string> Roots { get; init; } = new();

        /// <summary>File name patterns ("thumbcache_*.db"); empty = every file.</summary>
        public List<string> Patterns { get; init; } = new();

        /// <summary>Files changed more recently than this are left alone (a running installer's temp files).</summary>
        public TimeSpan MinAge { get; init; }

        /// <summary>Windows only lets an administrator delete here.</summary>
        public bool NeedsAdmin { get; init; }

        /// <summary>Folders emptied by the clean are removed too (temp folders fill with empty ones).</summary>
        public bool RemoveEmptyFolders { get; init; }

        /// <summary>Browser process name - while it runs, some cache files are in use.</summary>
        public string? ProcessName { get; init; }
    }

    public enum JunkFileStatus
    {
        Ready,
        Deleted,

        /// <summary>Open in another program - skipped.</summary>
        InUse,

        /// <summary>Changed after the scan - skipped to be safe.</summary>
        Changed,

        /// <summary>Windows needs an administrator to delete it.</summary>
        NeedsAdmin,

        Failed,
    }

    /// <summary>One file found. Ticked files are deleted by Clean.</summary>
    public sealed class JunkFile : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private JunkFileStatus _status;
        private string _message = "";

        public required string FullPath { get; init; }

        /// <summary>Path inside its root ("Cache\data_1"), shown in the list.</summary>
        public required string RelativePath { get; init; }

        public required string Root { get; init; }
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }
        public string Size => JunkText.FormatBytes(SizeBytes);
        public string ModifiedText => Modified == default ? "" : Modified.ToString("g");

        public JunkGroup? Group { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public JunkFileStatus Status
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
            JunkFileStatus.Deleted => "Deleted",
            JunkFileStatus.InUse => "In use",
            JunkFileStatus.Changed => "Changed",
            JunkFileStatus.NeedsAdmin => "Needs admin",
            JunkFileStatus.Failed => "Failed",
            _ => "Ready",
        };

        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One result row: a location's files with totals. Totals are cached and
    /// refreshed with <see cref="Refresh"/> - the old header summed every
    /// file each time it was drawn, and the "reclaimable" pill counted
    /// unticked files too.
    /// </summary>
    public sealed class JunkGroup : INotifyPropertyChanged
    {
        private int _count, _selectedCount;
        private long _bytes, _selectedBytes;
        private double _share;
        private string _note = "";

        public required JunkLocation Location { get; init; }
        public JunkCategory Category => Location.Category;
        public string Title => Location.Title;

        /// <summary>Where it looked ("%TEMP% and C:\Windows\Temp").</summary>
        public string Where { get; init; } = "";

        public List<JunkFile> Items { get; set; } = new();

        /// <summary>Recycle Bin: Windows reports a count and size, not files.</summary>
        public bool IsRecycleBin => Category == JunkCategory.RecycleBin;

        public long RecycleBinItems { get; set; }
        public long RecycleBinBytes { get; set; }
        private bool _recycleBinSelected = true;

        /// <summary>Folders Windows wouldn't open while scanning.</summary>
        public int BlockedFolders { get; set; }

        /// <summary>Files left alone because they changed recently.</summary>
        public int RecentSkipped { get; set; }

        public int Count => _count;
        public long Bytes => _bytes;
        public int SelectedCount => _selectedCount;
        public long SelectedBytes => _selectedBytes;

        public string CountText => IsRecycleBin
            ? $"{RecycleBinItems:N0} {JunkText.Plural((int)Math.Min(int.MaxValue, RecycleBinItems), "item", "items")}"
            : $"{_count:N0} {JunkText.Plural(_count, "file", "files")}";

        public string SizeText => JunkText.FormatBytes(_bytes);

        /// <summary>Share of everything found (0-1), for the bar.</summary>
        public double Share
        {
            get => _share;
            set => Set(ref _share, value);
        }

        /// <summary>Something to know ("Chrome is open - files it's using are skipped").</summary>
        public string Note
        {
            get => _note;
            set => Set(ref _note, value);
        }

        /// <summary>Tick box: true = all, false = none, null = some.</summary>
        public bool? IsSelected
        {
            get
            {
                if (IsRecycleBin) return _recycleBinSelected;
                if (_count == 0) return false;
                return _selectedCount == _count ? true : _selectedCount == 0 ? false : null;
            }
            set
            {
                bool select = value == true;
                if (IsRecycleBin) _recycleBinSelected = select;
                else foreach (var f in Items) if (f.Status != JunkFileStatus.Deleted) f.IsSelected = select;
                Refresh();
            }
        }

        /// <summary>Recomputes the totals (after ticking files or cleaning).</summary>
        public void Refresh()
        {
            if (IsRecycleBin)
            {
                _count = RecycleBinItems > 0 ? 1 : 0;
                _bytes = RecycleBinBytes;
                _selectedCount = _recycleBinSelected && _count > 0 ? 1 : 0;
                _selectedBytes = _selectedCount > 0 ? _bytes : 0;
            }
            else
            {
                int count = 0, selected = 0;
                long bytes = 0, selectedBytes = 0;
                foreach (var f in Items)
                {
                    if (f.Status == JunkFileStatus.Deleted) continue;
                    count++;
                    bytes += f.SizeBytes;
                    if (f.IsSelected)
                    {
                        selected++;
                        selectedBytes += f.SizeBytes;
                    }
                }
                (_count, _bytes, _selectedCount, _selectedBytes) = (count, bytes, selected, selectedBytes);
            }
            foreach (string name in new[] { nameof(Count), nameof(Bytes), nameof(SelectedCount), nameof(SelectedBytes), nameof(CountText), nameof(SizeText), nameof(IsSelected) })
                OnPropertyChanged(name);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>The folders a scan starts from. Real ones by default; tests point them at a scratch folder.</summary>
    public sealed record JunkEnvironment(string LocalAppData, string Windows, string Temp, string ProgramData)
    {
        public static JunkEnvironment Current => new(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    public sealed class JunkScanOptions
    {
        public IReadOnlyCollection<JunkCategory> Categories { get; init; } = Array.Empty<JunkCategory>();

        /// <summary>Leave files changed in the last day (temp files of programs that are running or installing).</summary>
        public bool SkipRecent { get; init; } = true;
    }

    public readonly record struct JunkProgress(double Percent, string Text, bool Indeterminate = false);

    public sealed class JunkCleanResult
    {
        public int Deleted { get; set; }
        public long BytesFreed { get; set; }
        public int InUse { get; set; }
        public int Changed { get; set; }
        public int NeedsAdmin { get; set; }
        public int Failed { get; set; }
        public int FoldersRemoved { get; set; }
        public bool RecycleBinEmptied { get; set; }
        public string? RecycleBinError { get; set; }
        public bool Cancelled { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>Remembered between sessions (junk_cleaner.json in %AppData%\Smart Tools). No paths.</summary>
    public sealed class JunkSettings
    {
        public List<JunkCategory> Categories { get; set; } = new() { JunkCategory.WindowsTemp, JunkCategory.BrowserCache, JunkCategory.ThumbnailCache, JunkCategory.ErrorReports };
        public bool SkipRecent { get; set; } = true;
    }

    public static class JunkText
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

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }
}
