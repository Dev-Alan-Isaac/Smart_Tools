using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Images to PDF data model. Plain data - no WPF, PdfSharp or Magick.
    //  Choices are enums saved by name (the old view passed combo text like
    //  "Small File" around as strings).
    // =====================================================================

    public enum ImagePdfPageSize
    {
        A4,
        Letter,
        Legal,
        A3,
        A5,

        /// <summary>Each page is the size of its image (plus margins).</summary>
        MatchImage,
    }

    public enum ImagePdfOrientation
    {
        Portrait,
        Landscape,

        /// <summary>Landscape pages for wide images, portrait for tall ones.</summary>
        Auto,
    }

    public enum ImagePdfMargins
    {
        None,
        Narrow,
        Normal,
        Wide,
    }

    public enum ImagePdfQuality
    {
        /// <summary>JPEG 95, full size.</summary>
        High,

        /// <summary>JPEG 75, at most 3,500 px on the long side (about 300 dpi on A4).</summary>
        Balanced,

        /// <summary>JPEG 55, at most 2,000 px - for email.</summary>
        Small,
    }

    public enum ImagePdfStatus
    {
        Ready,
        Working,
        Added,
        Failed,
        Cancelled,
    }

    /// <summary>One image in the list (one page in the PDF).</summary>
    public sealed class ImagePdfItem : INotifyPropertyChanged
    {
        private bool _isIncluded = true;
        private object? _thumbnail;
        private int _width, _height;
        private int _number;
        private ImagePdfStatus _status;
        private string _message = "";

        public required string FullPath { get; init; }
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }

        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public string Format => Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant() is { Length: > 0 } e ? e : "—";
        public string Size => ImagePdfText.FormatBytes(SizeBytes);

        /// <summary>The sidebar entry this image came from.</summary>
        public ImagePdfGroup? Group { get; set; }

        /// <summary>
        /// Goes into the PDF. Only this - the old list had every image ticked
        /// with no tick box shown, so "Remove Selected" removed everything.
        /// </summary>
        public bool IsIncluded
        {
            get => _isIncluded;
            set => Set(ref _isIncluded, value);
        }

        /// <summary>Place in the list (the page order).</summary>
        public int Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        /// <summary>An ImageSource set by the view (only for rows on screen).</summary>
        public object? Thumbnail
        {
            get => _thumbnail;
            set => Set(ref _thumbnail, value);
        }

        public bool DetailsRequested { get; set; }

        /// <summary>Size the right way up (0 until read).</summary>
        public void SetSize(int width, int height)
        {
            _width = width;
            _height = height;
            OnPropertyChanged(nameof(Dimensions));
        }

        public int Width => _width;
        public int Height => _height;
        public string Dimensions => _width > 0 && _height > 0 ? $"{_width} × {_height}" : "—";

        public ImagePdfStatus Status
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

        /// <summary>Page number or the reason it failed (status tooltip).</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        public string StatusText => _status switch
        {
            ImagePdfStatus.Working => "Adding",
            ImagePdfStatus.Added => "Added",
            ImagePdfStatus.Failed => "Failed",
            ImagePdfStatus.Cancelled => "Cancelled",
            _ => "Ready",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One sidebar row per Add images / Add folder / drop. Keeps its count up to date (the old one went stale).</summary>
    public sealed class ImagePdfGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }
        private bool _includeSubfolders;

        /// <summary>Changes when "Include subfolders" is ticked or unticked after the folder was added (the folder is read again).</summary>
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set
            {
                _includeSubfolders = value;
                Refresh();
            }
        }
        public List<ImagePdfItem> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = $"{Items.Count:N0} {ImagePdfText.Plural(Items.Count, "image", "images")}";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "Images from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>An image file found on disk.</summary>
    public readonly record struct ImagePdfFile(string FullPath, long SizeBytes, DateTime Modified);

    public sealed class ImagePdfScan
    {
        public List<ImagePdfFile> Files { get; } = new();
        public int InaccessibleFolders { get; set; }
        public int LinksSkipped { get; set; }
        public bool Missing { get; set; }
    }

    /// <summary>Everything a run needs, read from the sidebar when Create is pressed.</summary>
    public sealed class ImagePdfOptions
    {
        public required string OutputPath { get; init; }

        /// <summary>True when the user picked the file in a Save dialog (and confirmed any overwrite); otherwise a taken name gets " (2)".</summary>
        public bool OverwriteAllowed { get; init; }

        public ImagePdfPageSize PageSize { get; init; } = ImagePdfPageSize.A4;
        public ImagePdfOrientation Orientation { get; init; } = ImagePdfOrientation.Portrait;
        public ImagePdfMargins Margins { get; init; } = ImagePdfMargins.Normal;
        public ImagePdfQuality Quality { get; init; } = ImagePdfQuality.Balanced;
        public bool FitImageToPage { get; init; } = true;
        public bool AddPageNumbers { get; init; }
        public bool AddTitlePage { get; init; }
        public string Title { get; init; } = "";

        /// <summary>Start a new file before one would pass this size. 0 = one file.</summary>
        public int SplitMegabytes { get; init; }
    }

    /// <summary>An image ready to go on a page: JPEG bytes plus its size.</summary>
    public sealed record ImagePdfPrepared(byte[] Jpeg, int Width, int Height, double DpiX, double DpiY);

    public readonly record struct ImagePdfProgress(int Processed, int Total, string CurrentFile)
    {
        public double Percent => Total > 0 ? Processed * 100.0 / Total : 0;
    }

    public sealed class ImagePdfResult
    {
        public List<string> Files { get; } = new();
        public int Pages { get; set; }
        public int Failed { get; set; }
        public bool Cancelled { get; set; }
        public TimeSpan Elapsed { get; set; }

        /// <summary>Why nothing was written (every image failed), else null.</summary>
        public string? Error { get; set; }
    }

    /// <summary>Remembered between sessions (image_to_pdf.json in %AppData%\Smart Tools). No folders.</summary>
    public sealed class ImagePdfSettings
    {
        public ImagePdfPageSize PageSize { get; set; } = ImagePdfPageSize.A4;
        public ImagePdfOrientation Orientation { get; set; } = ImagePdfOrientation.Auto;
        public ImagePdfMargins Margins { get; set; } = ImagePdfMargins.Normal;
        public ImagePdfQuality Quality { get; set; } = ImagePdfQuality.Balanced;
        public bool FitImageToPage { get; set; } = true;
        public bool AddPageNumbers { get; set; }
        public bool AddTitlePage { get; set; }
        public int SplitMegabytes { get; set; } = 30;
        public bool IncludeSubfolders { get; set; } = true;
        public bool SaveWithoutAsking { get; set; } = true;
    }

    public static class ImagePdfText
    {
        public static string FormatBytes(long bytes) => Smart_Tools.WPF.Controllers.ByteFormat.Format(bytes);

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }
}
