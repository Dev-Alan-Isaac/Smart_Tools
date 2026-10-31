using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>How the new size is worked out. Width and height are the limits you type; the picture always keeps its proportions.</summary>
    public enum BulkResizeMode { Percent, FitWithin, Width, Height }

    /// <summary>The picture format to save. <see cref="Keep"/> = the same as each original (formats that can't be saved fall back to JPG, or PNG for SVG).</summary>
    public enum ResizeFormat { Keep, Jpg, Png, WebP }

    public enum ResizeStatus { Pending, Working, Done, Skipped, Failed }

    /// <summary>Everything that decides how a picture is changed. Plain data: the same options apply to every picture in the batch.</summary>
    public sealed record ResizeOptions
    {
        public BulkResizeMode Mode { get; init; } = BulkResizeMode.FitWithin;

        /// <summary>For <see cref="BulkResizeMode.Percent"/>: 1 to 400.</summary>
        public int Percent { get; init; } = 50;

        /// <summary>The limits for the other modes, in pixels. 0 = no limit on that side.</summary>
        public int Width { get; init; } = 1920;

        public int Height { get; init; } = 1080;

        /// <summary>Never enlarge: a picture already smaller than the limit is only recompressed.</summary>
        public bool OnlyShrink { get; init; } = true;

        /// <summary>1 to 100, for JPG and WebP.</summary>
        public int Quality { get; init; } = 85;

        /// <summary>Largest file size wanted, in KB, for JPG and WebP (the quality is lowered until it fits). 0 = off.</summary>
        public int TargetKb { get; init; }

        public ResizeFormat Format { get; init; } = ResizeFormat.Keep;

        /// <summary>Remove camera, location, and editing details (the color profile stays).</summary>
        public bool StripMetadata { get; init; } = true;

        /// <summary>Added to the file name, so originals are never touched.</summary>
        public string Suffix { get; init; } = "-small";
    }

    /// <summary>What the tool remembers between runs (%AppData%\Smart Tools\bulk-image-resizer.json). Settings only: never a file path.</summary>
    public sealed class BulkResizeSettings
    {
        public BulkResizeMode Mode { get; set; } = BulkResizeMode.FitWithin;
        public int Percent { get; set; } = 50;
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public bool OnlyShrink { get; set; } = true;
        public int Quality { get; set; } = 85;
        public bool TargetEnabled { get; set; }
        public int TargetKb { get; set; } = 500;
        public ResizeFormat Format { get; set; } = ResizeFormat.Keep;
        public bool StripMetadata { get; set; } = true;
        public string Suffix { get; set; } = "-small";
        public bool IncludeSubfolders { get; set; } = true;

        public ResizeOptions ToOptions() => new()
        {
            Mode = Mode,
            Percent = Percent,
            Width = Width,
            Height = Height,
            OnlyShrink = OnlyShrink,
            Quality = Quality,
            TargetKb = TargetEnabled ? TargetKb : 0,
            Format = Format,
            StripMetadata = StripMetadata,
            Suffix = Suffix,
        };
    }

    /// <summary>One picture in the batch.</summary>
    public sealed class BulkImageItem : INotifyPropertyChanged
    {
        private ResizeStatus _status;
        private string _message = "";
        private long _newBytes;
        private int _width, _height, _newWidth, _newHeight, _quality;
        private string _newPath = "";
        private object? _thumbnail;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public long OriginalBytes { get; init; }

        /// <summary>Pixels of the original; 0 until they have been read.</summary>
        public int Width { get => _width; set { if (Set(ref _width, value)) Raise(nameof(DimensionsText)); } }
        public int Height { get => _height; set { if (Set(ref _height, value)) Raise(nameof(DimensionsText)); } }

        /// <summary>"4000 × 3000" before the run, "4000 × 3000 → 1920 × 1440" after. Empty until the size is known.</summary>
        public string DimensionsText => _width <= 0 ? "" : _newWidth > 0 ? $"{_width} × {_height} → {_newWidth} × {_newHeight}" : $"{_width} × {_height}";

        /// <summary>Back to "not done yet", for running the batch again.</summary>
        public void Reset()
        {
            Status = ResizeStatus.Pending;
            Message = "";
            NewBytes = 0;
            NewWidth = 0;
            NewHeight = 0;
            NewPath = "";
            Quality = 0;
        }

        public ResizeStatus Status { get => _status; set => Set(ref _status, value); }

        /// <summary>Why a picture was skipped or failed, in plain words.</summary>
        public string Message { get => _message; set => Set(ref _message, value); }

        public long NewBytes { get => _newBytes; set { if (Set(ref _newBytes, value)) { Raise(nameof(SavedPercent)); Raise(nameof(HasResult)); } } }
        public int NewWidth { get => _newWidth; set { if (Set(ref _newWidth, value)) Raise(nameof(DimensionsText)); } }
        public int NewHeight { get => _newHeight; set { if (Set(ref _newHeight, value)) Raise(nameof(DimensionsText)); } }
        public string NewPath { get => _newPath; set => Set(ref _newPath, value); }

        /// <summary>The quality the file was finally saved at (only when a target size was used), else 0.</summary>
        public int Quality { get => _quality; set => Set(ref _quality, value); }

        /// <summary>A small preview picture, set by the view (kept as object so this file stays free of WPF types).</summary>
        public object? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }

        public bool HasResult => Status == ResizeStatus.Done && NewBytes > 0;

        /// <summary>How much smaller the file got, as a whole percent (negative = larger).</summary>
        public int SavedPercent => OriginalBytes <= 0 || NewBytes <= 0 ? 0 : (int)Math.Round((1.0 - (double)NewBytes / OriginalBytes) * 100);

        private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            if (name == nameof(Status)) Raise(nameof(HasResult));
            return true;
        }

        private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>The batch's progress, reported at most a few times a second.</summary>
    public sealed record ResizeProgress(int Done, int Total, int Failed, int Skipped, long BytesBefore, long BytesAfter);
}
