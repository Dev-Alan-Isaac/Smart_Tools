using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>What to take out of each picture.</summary>
    public enum CleanMode
    {
        /// <summary>Camera, date, location, software, comments, thumbnails: all of it (the color profile stays).</summary>
        Everything,

        /// <summary>Only where the picture was taken (GPS). The rest of the details stay.</summary>
        LocationOnly,
    }

    public enum CleanStatus { Pending, Working, Done, Skipped, Failed }

    /// <summary>What the tool remembers between runs (%AppData%\Smart Tools\metadata-cleaner.json). Settings only: never a file path.</summary>
    public sealed class MetaCleanSettings
    {
        public CleanMode Mode { get; set; } = CleanMode.Everything;
        public string Suffix { get; set; } = "-clean";
        public bool IncludeSubfolders { get; set; } = true;
    }

    /// <summary>What was found inside a picture before cleaning.</summary>
    public sealed record MetaFound(bool HasLocation, string Camera, string Taken, bool HasOther);

    /// <summary>The batch's progress, reported at most a few times a second.</summary>
    public sealed record CleanProgress(int Done, int Total, int Failed, int Skipped);

    /// <summary>One picture in the batch.</summary>
    public sealed class MetaItem : INotifyPropertyChanged
    {
        private CleanStatus _status;
        private string _message = "";
        private string _newPath = "";
        private long _newBytes;
        private bool _hasLocation, _hasOther, _read;
        private string _camera = "", _taken = "";
        private object? _thumbnail;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public long OriginalBytes { get; init; }

        /// <summary>The picture has GPS coordinates inside.</summary>
        public bool HasLocation { get => _hasLocation; private set => Set(ref _hasLocation, value); }

        public string Camera { get => _camera; private set => Set(ref _camera, value); }

        public string Taken { get => _taken; private set => Set(ref _taken, value); }

        /// <summary>Other hidden data: software, comments, XMP, IPTC.</summary>
        public bool HasOther { get => _hasOther; private set => Set(ref _hasOther, value); }

        /// <summary>True once the picture's details have been read.</summary>
        public bool IsRead { get => _read; private set => Set(ref _read, value); }

        /// <summary>No hidden details were found, so cleaning would change nothing.</summary>
        public bool IsAlreadyClean => _read && !_hasLocation && _camera.Length == 0 && _taken.Length == 0 && !_hasOther;

        /// <summary>"Location · Canon EOS R6 · 2024-05-01 12:30": what is inside, in a line.</summary>
        public string FoundText
        {
            get
            {
                if (!_read) return "";
                if (IsAlreadyClean) return "";
                var parts = new List<string>();
                if (_hasLocation) parts.Add("GPS");
                if (_camera.Length > 0) parts.Add(_camera);
                if (_taken.Length > 0) parts.Add(_taken);
                if (parts.Count == 0 && _hasOther) parts.Add("…");
                return string.Join(" · ", parts);
            }
        }

        public void SetFound(MetaFound found)
        {
            HasLocation = found.HasLocation;
            Camera = found.Camera;
            Taken = found.Taken;
            HasOther = found.HasOther;
            IsRead = true;
            Raise(nameof(FoundText));
            Raise(nameof(IsAlreadyClean));
        }

        public CleanStatus Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(HasResult)); } }

        /// <summary>Why a picture was skipped or failed, or a note about the result, in plain words.</summary>
        public string Message { get => _message; set => Set(ref _message, value); }

        public string NewPath { get => _newPath; set => Set(ref _newPath, value); }

        public long NewBytes { get => _newBytes; set { if (Set(ref _newBytes, value)) Raise(nameof(HasResult)); } }

        public bool HasResult => Status == CleanStatus.Done && NewBytes > 0;

        /// <summary>A small preview picture, set by the view (kept as object so this file stays free of WPF types).</summary>
        public object? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }

        /// <summary>Back to "not done yet", for running the batch again.</summary>
        public void Reset()
        {
            Status = CleanStatus.Pending;
            Message = "";
            NewBytes = 0;
            NewPath = "";
        }

        private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
