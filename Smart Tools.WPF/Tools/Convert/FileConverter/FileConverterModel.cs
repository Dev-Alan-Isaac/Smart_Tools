using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  File Converter data model. Plain data - no WPF, no ffmpeg/Magick.
    //  The view maps its controls by Tag name, not by combo index, so the
    //  enum order no longer has to mirror the XAML.
    // =====================================================================

    /// <summary>What kind of files are being converted. Document stays for saved settings/other code but isn't offered (no engine).</summary>
    public enum ConvertFileType { Video, Image, Audio, Document }

    public enum ConvertVideoFormat { Mp4, Mkv, Avi, Mov, Webm, Flv }
    public enum ConvertVideoResolution { Original, R2160p, R1080p, R720p, R480p }
    public enum ConvertVideoCodec { H264, H265, Vp9, Av1 }
    public enum ConvertVideoQuality { Highest, Balanced, Smaller }

    public enum ConvertImageFormat { Jpg, Png, Webp, Bmp, Tiff, Gif, Ico, Avif }
    public enum ConvertImageQuality { Highest, High, Medium, Low }
    public enum ConvertImageResize { Original, R4K, R1080p, R720p, Custom }

    public enum ConvertAudioFormat { Mp3, Wav, Flac, Aac, Ogg, M4a }
    public enum ConvertAudioBitrate { K128, K192, K256, K320 }

    /// <summary>Original keeps the source rate (the old version always resampled, 44.1 kHz by default).</summary>
    public enum ConvertAudioSampleRate { Hz22050, Hz44100, Hz48000, Hz96000, Original }

    public enum ConvertDocumentFormat { Pdf, Docx, Txt, Rtf, Odt, Html }

    /// <summary>Where converted files go.</summary>
    public enum ConvertOutputMode
    {
        /// <summary>Into the chosen output folder (the old "Save folder" box - which the old engine ignored).</summary>
        Folder,

        /// <summary>Next to each original.</summary>
        NextToOriginal,
    }

    public enum ConvertStatus
    {
        Ready,

        /// <summary>Same format, nothing to change.</summary>
        Skipped,

        /// <summary>Not a file of the selected type.</summary>
        Unsupported,

        Converting,
        Converted,
        Failed,
        Cancelled,
    }

    public sealed class ConvertOptions
    {
        public ConvertFileType FileType { get; init; }

        public ConvertVideoFormat VideoFormat { get; init; }
        public ConvertVideoResolution VideoResolution { get; init; }
        public ConvertVideoCodec VideoCodec { get; init; }
        public ConvertVideoQuality VideoQuality { get; init; } = ConvertVideoQuality.Balanced;

        public ConvertImageFormat ImageFormat { get; init; }
        public ConvertImageQuality ImageQuality { get; init; } = ConvertImageQuality.High;
        public ConvertImageResize ImageResize { get; init; }
        public int? ImageCustomWidth { get; init; }
        public int? ImageCustomHeight { get; init; }

        public ConvertAudioFormat AudioFormat { get; init; }
        public ConvertAudioBitrate AudioBitrate { get; init; } = ConvertAudioBitrate.K256;
        public ConvertAudioSampleRate AudioSampleRate { get; init; } = ConvertAudioSampleRate.Original;

        public ConvertDocumentFormat DocumentFormat { get; init; }

        public bool IncludeSubfolders { get; init; } = true;

        /// <summary>Only queue files of the selected type (otherwise they're listed as unsupported).</summary>
        public bool MatchSelectedTypeOnly { get; init; } = true;

        public bool OverwriteExisting { get; init; }
        public bool DeleteOriginalAfterConversion { get; init; }

        /// <summary>Originals go to the Recycle Bin (the old version deleted them permanently).</summary>
        public bool DeleteToRecycleBin { get; init; } = true;

        public ConvertOutputMode OutputMode { get; init; } = ConvertOutputMode.Folder;
        public string OutputFolder { get; init; } = "";

        /// <summary>
        /// When a folder is converted, recreate its subfolders under the
        /// output folder (so two "cover.jpg" from different albums don't collide).
        /// </summary>
        public bool KeepFolderStructure { get; init; } = true;

        /// <summary>Files converted at once. Set by the manager from the file type when 0.</summary>
        public int MaxDegreeOfParallelism { get; init; }
    }

    /// <summary>Posted a few times a second while converting.</summary>
    public readonly record struct ConvertProgress(int Completed, int Total, string CurrentFile, double CurrentFilePercent, double OverallPercent);

    /// <summary>A file or folder the user added (folders are expanded when the list is built).</summary>
    public sealed class ConvertSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
    }

    /// <summary>One row in the file grid.</summary>
    public sealed class ConvertFileItem : INotifyPropertyChanged
    {
        private ConvertStatus _status = ConvertStatus.Ready;
        private string _message = "";
        private double _progress;
        private long _outputBytes;

        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public required string CurrentFormat { get; init; }
        public required long SizeBytes { get; init; }
        public string Size => ConvertText.FormatBytes(SizeBytes);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public MediaKind Kind { get; init; }

        /// <summary>The added folder this file came from (for keeping folder structure), or null.</summary>
        public string? SourceRoot { get; init; }

        public string TargetFormat { get; init; } = "";

        /// <summary>Where the file will be written (planned) / was written.</summary>
        public string? OutputPath { get; set; }

        public ConvertStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsConverting));
            }
        }

        /// <summary>Reason or error detail (tooltip).</summary>
        public string Message
        {
            get => _message;
            set
            {
                _message = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>0-100 for the file being converted.</summary>
        public double Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public long OutputBytes
        {
            get => _outputBytes;
            set
            {
                _outputBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResultText));
                OnPropertyChanged(nameof(ResultIsSmaller));
            }
        }

        public bool IsConverting => _status == ConvertStatus.Converting;

        public string StatusText => _status switch
        {
            ConvertStatus.Ready => "Ready",
            ConvertStatus.Skipped => "Already " + TargetFormat,
            ConvertStatus.Unsupported => "Not supported",
            ConvertStatus.Converting => $"{_progress:0}%",
            ConvertStatus.Converted => "Converted",
            ConvertStatus.Failed => "Failed",
            ConvertStatus.Cancelled => "Cancelled",
            _ => "",
        };

        /// <summary>"4.2 MB (-63%)" once converted.</summary>
        public string ResultText
        {
            get
            {
                if (_outputBytes <= 0) return "";
                string size = ConvertText.FormatBytes(_outputBytes);
                if (SizeBytes <= 0) return size;
                double change = (_outputBytes - SizeBytes) * 100.0 / SizeBytes;
                return $"{size} ({(change >= 0 ? "+" : "")}{change:0}%)";
            }
        }

        public bool ResultIsSmaller => _outputBytes > 0 && _outputBytes < SizeBytes;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static string FormatBytes(long bytes) => ConvertText.FormatBytes(bytes);
    }

    /// <summary>
    /// One row in the sidebar's queue: an added folder, or all the files
    /// added one by one from the same folder ("3 files").
    /// </summary>
    public sealed class ConvertQueueGroup
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

        public string ToolTip => IsFolder ? FolderPath : string.Join(Environment.NewLine, FilePaths.Take(15)) + (FilePaths.Count > 15 ? $"{Environment.NewLine}+{FilePaths.Count - 15} more" : "");
    }

    /// <summary>Result of a conversion run.</summary>
    public sealed class ConvertRunResult
    {
        public int Converted { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public long InputBytes { get; set; }
        public long OutputBytes { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>Remembered between sessions (file_converter.json in %AppData%\Smart Tools).</summary>
    public sealed class ConverterSettings
    {
        public ConvertFileType FileType { get; set; } = ConvertFileType.Video;
        public ConvertVideoFormat VideoFormat { get; set; } = ConvertVideoFormat.Mp4;
        public ConvertVideoResolution VideoResolution { get; set; } = ConvertVideoResolution.Original;
        public ConvertVideoCodec VideoCodec { get; set; } = ConvertVideoCodec.H264;
        public ConvertVideoQuality VideoQuality { get; set; } = ConvertVideoQuality.Balanced;
        public ConvertImageFormat ImageFormat { get; set; } = ConvertImageFormat.Jpg;
        public ConvertImageQuality ImageQuality { get; set; } = ConvertImageQuality.High;
        public ConvertImageResize ImageResize { get; set; } = ConvertImageResize.Original;
        public int ImageCustomWidth { get; set; } = 1920;
        public int ImageCustomHeight { get; set; } = 1080;
        public ConvertAudioFormat AudioFormat { get; set; } = ConvertAudioFormat.Mp3;
        public ConvertAudioBitrate AudioBitrate { get; set; } = ConvertAudioBitrate.K256;
        public ConvertAudioSampleRate AudioSampleRate { get; set; } = ConvertAudioSampleRate.Original;
        public bool IncludeSubfolders { get; set; } = true;
        public bool MatchSelectedTypeOnly { get; set; } = true;
        public bool OverwriteExisting { get; set; }
        public bool DeleteOriginal { get; set; }
        public ConvertOutputMode OutputMode { get; set; } = ConvertOutputMode.Folder;

        /// <summary>Empty = the per-type default (Videos\Smart Tools\Convert, Pictures\Smart Tools\Convert, Music\Smart Tools\Convert).</summary>
        public string OutputFolder { get; set; } = "";
        public bool KeepFolderStructure { get; set; } = true;
        public bool OpenFolderWhenDone { get; set; }
    }

    public static class ConvertText
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

        public static string ExtensionLabel(string extension) =>
            string.IsNullOrEmpty(extension) ? "-" : extension.TrimStart('.').ToUpperInvariant();

        public static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
