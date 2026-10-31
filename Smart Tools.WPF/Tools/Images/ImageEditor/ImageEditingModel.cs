using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Image Editor data model. Plain data - no WPF types (the thumbnail is
    //  held as object; the view assigns an ImageSource). Status colours come
    //  from DataTriggers in the view.
    // =====================================================================

    public enum ImageOutputFormat { Jpg, Png, Webp, Bmp, Gif, Tiff }

    /// <summary>
    /// How an image is turned: rotate clockwise by <see cref="Rotation"/>
    /// (0/90/180/270), then mirror left-right if <see cref="Mirror"/>. Every
    /// mix of rotate and flip is one of these eight. The preview and the
    /// export both apply it in this order - the old version previewed
    /// flip-then-rotate but exported rotate-then-flip, so a flipped and
    /// rotated photo came out different from what was shown.
    /// </summary>
    public readonly record struct ImageOrientation(int Rotation, bool Mirror)
    {
        public static readonly ImageOrientation Identity = new(0, false);
        public bool IsIdentity => Rotation == 0 && !Mirror;
        public bool SwapsSides => Rotation is 90 or 270;
    }

    /// <summary>
    /// Crop as fractions (0..1) of the image the way it's stored on disk
    /// after its camera rotation (EXIF) is applied, but before this editor's
    /// rotate/flip. The view converts what you draw on the rotated preview
    /// into this, so cropping works on the picture as you see it.
    /// </summary>
    public readonly record struct CropRect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;

        /// <summary>False for "the whole image" or a slip of the mouse.</summary>
        public bool IsMeaningful => Width > 0.01 && Height > 0.01 &&
            !(Left <= 0.001 && Top <= 0.001 && Width >= 0.999 && Height >= 0.999);
    }

    public enum ImageItemStatus { Ready, Loading, Unreadable, Exporting, Done, Failed, Cancelled }

    /// <summary>One row in the image list.</summary>
    public sealed class ImageItem : INotifyPropertyChanged
    {
        private int _pixelWidth;
        private int _pixelHeight;
        private object? _thumbnail;
        private bool _isSelected = true;
        private bool _isNowShowing;
        private ImageOrientation _orientation;
        private CropRect? _crop;
        private ImageItemStatus _status = ImageItemStatus.Ready;
        private string _message = "";
        private string? _outputPath;

        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public required long SizeBytes { get; init; }
        public string Size => ImageEditText.FormatBytes(SizeBytes);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";

        /// <summary>The Files card row this image came in with (a folder, or loose files).</summary>
        public ImageEditGroup? Group { get; set; }

        /// <summary>Size as the photo is meant to be seen (camera rotation applied), before edits.</summary>
        public int PixelWidth
        {
            get => _pixelWidth;
            set
            {
                _pixelWidth = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Dimensions));
            }
        }

        public int PixelHeight
        {
            get => _pixelHeight;
            set
            {
                _pixelHeight = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Dimensions));
            }
        }

        public string Dimensions => _pixelWidth > 0 && _pixelHeight > 0 ? $"{_pixelWidth} × {_pixelHeight}" : "-";

        /// <summary>A frozen ImageSource, set by the view's loader (object so the model stays WPF-free).</summary>
        public object? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Included in the export.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public bool IsNowShowing
        {
            get => _isNowShowing;
            set
            {
                if (_isNowShowing == value) return;
                _isNowShowing = value;
                OnPropertyChanged();
            }
        }

        public ImageOrientation Orientation
        {
            get => _orientation;
            set
            {
                if (_orientation == value) return;
                _orientation = value;
                OnPropertyChanged();
                OnEditsChanged();
            }
        }

        public CropRect? Crop
        {
            get => _crop;
            set
            {
                _crop = value is { IsMeaningful: true } ? value : null;
                OnPropertyChanged();
                OnEditsChanged();
            }
        }

        public bool HasEdits => !_orientation.IsIdentity || _crop != null;

        /// <summary>"Rotated 90° · mirrored · cropped" (tooltip / preview caption).</summary>
        public string EditSummary
        {
            get
            {
                var parts = new List<string>();
                if (_orientation.Rotation != 0) parts.Add($"rotated {_orientation.Rotation}°");
                if (_orientation.Mirror) parts.Add("mirrored");
                if (_crop != null) parts.Add("cropped");
                if (parts.Count == 0) return "No edits";
                string text = string.Join(" · ", parts);
                return char.ToUpperInvariant(text[0]) + text[1..];
            }
        }

        public ImageItemStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasStatus));
            }
        }

        public string Message
        {
            get => _message;
            set
            {
                _message = value;
                OnPropertyChanged();
            }
        }

        public string? OutputPath
        {
            get => _outputPath;
            set
            {
                _outputPath = value;
                OnPropertyChanged();
            }
        }

        public bool HasStatus => _status is not (ImageItemStatus.Ready or ImageItemStatus.Loading);

        public string StatusText => _status switch
        {
            ImageItemStatus.Unreadable => "Can't read",
            ImageItemStatus.Exporting => "Saving...",
            ImageItemStatus.Done => "Saved",
            ImageItemStatus.Failed => "Failed",
            ImageItemStatus.Cancelled => "Cancelled",
            _ => "",
        };

        private void OnEditsChanged()
        {
            OnPropertyChanged(nameof(HasEdits));
            OnPropertyChanged(nameof(EditSummary));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ImageEditOptions
    {
        public ImageOutputFormat Format { get; init; } = ImageOutputFormat.Jpg;

        /// <summary>1-100, used by JPG and WEBP.</summary>
        public int Quality { get; init; } = 90;

        public int? ResizeWidth { get; init; }
        public int? ResizeHeight { get; init; }

        /// <summary>On: fit inside Width x Height keeping the shape. Off: stretch to exactly what's typed.</summary>
        public bool KeepAspect { get; init; } = true;

        /// <summary>Off: images already smaller than the size are left as they are.</summary>
        public bool AllowEnlarge { get; init; }

        public string OutputFolder { get; init; } = "";

        /// <summary>Added to each file name, e.g. "holiday_edited.jpg". Empty keeps the name.</summary>
        public string NameSuffix { get; init; } = "_edited";

        /// <summary>Images exported at once.</summary>
        public int MaxDegreeOfParallelism { get; init; }
    }

    public readonly record struct ImageEditProgress(int Processed, int Total, string CurrentFile);

    public sealed class ImageEditResult
    {
        public int Exported { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public long OutputBytes { get; set; }
        public List<string> OutputFiles { get; } = new();
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// One row on the Files card: a folder that was added (with or without
    /// its subfolders), or a set of loose files. Taking the row off removes
    /// its images from the list; changing "Include subfolders" reads a
    /// folder row again.
    /// </summary>
    public sealed class ImageEditGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }

        private bool _includeSubfolders;
        /// <summary>Changes when "Include subfolders" is ticked or unticked after the folder was added.</summary>
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set { _includeSubfolders = value; Refresh(); }
        }

        public List<ImageItem> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = Items.Count == 1 ? "1 image" : $"{Items.Count:N0} images";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "Images from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Remembered between sessions (image_editor.json in %AppData%\Smart Tools). Holds no paths.</summary>
    public sealed class ImageEditorSettings
    {
        public ImageOutputFormat Format { get; set; } = ImageOutputFormat.Jpg;
        public int Quality { get; set; } = 90;
        public int ResizeWidth { get; set; }
        public int ResizeHeight { get; set; }
        public bool KeepAspect { get; set; } = true;
        public bool AllowEnlarge { get; set; }
        // No OutputFolder: a folder the user picked is kept for this session
        // only (the old settings wrote the path into image_editor.json).
        public string NameSuffix { get; set; } = "_edited";
        public bool IncludeSubfolders { get; set; } = true;
        public bool ApplyToAllChecked { get; set; }
        public string CropAspect { get; set; } = "Free";
        public bool QueueCollapsed { get; set; }
    }

    public static class ImageEditText
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
    }
}
