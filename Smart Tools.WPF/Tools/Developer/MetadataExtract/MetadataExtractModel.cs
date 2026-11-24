using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Metadata Extractor data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/MetadataExtractView/MetadataExtractModel.cs plus
    //  MetadataFileItem in the view's code-behind.)
    // =====================================================================

    public enum MetadataKind
    {
        Video,
        Audio,
        Image,
    }

    public enum MetadataStatus
    {
        Waiting,
        Working,
        Done,
        Skipped,
        Failed,
        Cancelled,
    }

    public enum MetadataExportFormat
    {
        Json,
        Csv,
        Xml,
    }

    /// <summary>One line of metadata. <see cref="Group"/> is the section it shows under ("General", "Camera", "Location"…).</summary>
    public sealed record MetadataField(string Group, string Property, string Value);

    /// <summary>A file in the list: what it is, how extraction went, and what was found.</summary>
    public sealed class MetadataFileItem : INotifyPropertyChanged
    {
        public MetadataFileItem(string fullPath, MetadataKind kind, long sizeBytes, DateTime modified)
        {
            FullPath = fullPath;
            Name = Path.GetFileName(fullPath);
            Folder = Path.GetDirectoryName(fullPath) ?? "";
            Kind = kind;
            Format = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();
            SizeBytes = sizeBytes;
            Modified = modified;
        }

        public string FullPath { get; }
        public string Name { get; }
        public string Folder { get; }
        public MetadataKind Kind { get; }
        public string Format { get; }
        public long SizeBytes { get; }
        public DateTime Modified { get; }
        public string SizeText => MetadataText.Bytes(SizeBytes);

        private MetadataStatus _status = MetadataStatus.Waiting;
        public MetadataStatus Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        private string _message = "Waiting";
        /// <summary>Short status text, or the plain reason it failed (shown in the status chip's tooltip).</summary>
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        private IReadOnlyList<MetadataField>? _fields;
        /// <summary>What was found; null until extracted. Raises a change so an open details panel refreshes.</summary>
        public IReadOnlyList<MetadataField>? Fields
        {
            get => _fields;
            set => Set(ref _fields, value);
        }

        private string _summary = "";
        /// <summary>One line for the list: "1920 × 1080 · 2:05 · H.264", "4032 × 3024 · Apple iPhone 13".</summary>
        public string Summary
        {
            get => _summary;
            set => Set(ref _summary, value);
        }

        private double? _latitude;
        public double? Latitude
        {
            get => _latitude;
            set
            {
                Set(ref _latitude, value);
                OnPropertyChanged(nameof(HasLocation));
            }
        }

        private double? _longitude;
        public double? Longitude
        {
            get => _longitude;
            set
            {
                Set(ref _longitude, value);
                OnPropertyChanged(nameof(HasLocation));
            }
        }

        /// <summary>The file says where it was made (GPS) - worth knowing before sharing it.</summary>
        public bool HasLocation => Latitude.HasValue && Longitude.HasValue;

        /// <summary>The sidebar entry that added this row (removing the entry removes its rows).</summary>
        public MetadataQueueGroup? Group { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// A sidebar entry: a folder that was added (with or without its
    /// subfolders), or the loose files added from one folder. Same look as
    /// the Validate tool's queue, but Metadata's own type.
    /// </summary>
    public sealed class MetadataQueueGroup : INotifyPropertyChanged
    {
        public required string FolderPath { get; init; }
        public bool IsFolder { get; init; }

        /// <summary>For folders: whether subfolders were included when it was added.</summary>
        public bool IncludeSubfolders { get; init; }

        private int _count;
        /// <summary>Rows in the list that came from this entry.</summary>
        public int Count
        {
            get => _count;
            set
            {
                if (_count == value) return;
                _count = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
            }
        }

        /// <summary>The single file's name when a files entry holds just one.</summary>
        public string? SingleFileName { get; set; }

        public string Name
        {
            get
            {
                string trimmed = FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? FolderPath : name;   // drive roots
            }
        }

        public string Subtitle
        {
            get
            {
                string files = $"{Count:N0} {MetadataText.Plural(Count, "file", "files")}";
                if (IsFolder) return files + (IncludeSubfolders ? " · with subfolders" : " · this folder only");
                return Count == 1 && SingleFileName != null ? SingleFileName : files;
            }
        }

        public string ToolTip => IsFolder
            ? $"{FolderPath}\n{Subtitle}"
            : $"Files added from {FolderPath}\n{Count:N0} {MetadataText.Plural(Count, "file", "files")}";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Which kinds of file a run reads; the others are marked Skipped.</summary>
    public sealed class MetadataExtractOptions
    {
        public bool IncludeVideo { get; init; } = true;
        public bool IncludeAudio { get; init; } = true;
        public bool IncludeImages { get; init; } = true;

        public bool Includes(MetadataKind kind) => kind switch
        {
            MetadataKind.Video => IncludeVideo,
            MetadataKind.Audio => IncludeAudio,
            _ => IncludeImages,
        };
    }

    public readonly record struct MetadataProgress(int Done, int Total, int Failed, string CurrentFile)
    {
        public double Percent => Total <= 0 ? 0 : Math.Clamp(Done * 100.0 / Total, 0, 100);
    }

    /// <summary>
    /// What the image reader found, in plain values - so the wording is built
    /// (and tested) without Magick.NET. EXIF values are as the reader gives
    /// them: strings, numbers, (numerator, denominator) pairs, arrays, bytes.
    /// </summary>
    public sealed class ImageMetadataRaw
    {
        public string Format { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public int Depth { get; init; }
        public double DensityX { get; init; }
        public double DensityY { get; init; }

        /// <summary>"DPI", "px/cm" or "" when the file doesn't say.</summary>
        public string DensityUnit { get; init; } = "";

        public string Compression { get; init; } = "";

        /// <summary>EXIF orientation 1-8, 0 = not set.</summary>
        public int Orientation { get; init; }

        public string ColorSpace { get; init; } = "";
        public string IccProfile { get; init; } = "";
        public IReadOnlyList<(string Tag, object? Value)> Exif { get; init; } = Array.Empty<(string, object?)>();
    }

    /// <summary>An EXIF fraction (exposure 1/250, aperture 28/10…) without Magick.NET types.</summary>
    public readonly record struct MetadataRational(double Numerator, double Denominator)
    {
        public double Value => Denominator == 0 ? double.NaN : Numerator / Denominator;
    }

    /// <summary>What reading one file produced: the lines, a one-line summary, and where it was made (if it says).</summary>
    public sealed record MetadataReadResult(IReadOnlyList<MetadataField> Fields, string Summary, double? Latitude, double? Longitude)
    {
        public string? Error { get; init; }
        public bool Success => Error == null;

        public static MetadataReadResult Failed(string error) => new(Array.Empty<MetadataField>(), "", null, null) { Error = error };
    }

    /// <summary>Remembered between sessions (metadata_extract.json in %AppData%\Smart Tools). No paths.</summary>
    public sealed class MetadataSettings
    {
        public bool IncludeVideo { get; set; } = true;
        public bool IncludeAudio { get; set; } = true;
        public bool IncludeImages { get; set; } = true;
        public bool IncludeSubfolders { get; set; } = true;
        public MetadataExportFormat ExportFormat { get; set; } = MetadataExportFormat.Json;
        public bool DetailsAsCards { get; set; } = true;
    }

    public static class MetadataText
    {
        /// <summary>Sizes like Explorer shows them (1 KB = 1024 bytes).</summary>
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

        /// <summary>"0:42", "3:25", "1:02:07", "26:00:05" (the old format dropped whole days: 24 h 5 min showed as "05:00").</summary>
        public static string Duration(double seconds)
        {
            if (!(seconds > 0) || !double.IsFinite(seconds)) return "";
            var t = TimeSpan.FromSeconds(seconds);
            int hours = (int)t.TotalHours;
            return hours > 0 ? $"{hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        public static string Plural(int count, string one, string many) => count == 1 ? one : many;
    }
}
