using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  GIF Maker - plain data only. Planning (filters, sizes, estimates,
    //  the image list, settings) is in Controllers/GifMakerManager.cs;
    //  ffmpeg/ffprobe in GifMakerManager.Run.cs; decoding a finished GIF
    //  for the animated preview in GifMakerManager.Render.cs.
    // =====================================================================

    /// <summary>Where the frames come from (mode chips, read by Tag = enum name).</summary>
    public enum GifSourceMode
    {
        Video,
        Images
    }

    /// <summary>Colours and dithering: smaller files or better looks.</summary>
    public enum GifQuality
    {
        /// <summary>64 colours, light ordered dither.</summary>
        Small,

        /// <summary>128 colours, ordered dither.</summary>
        Balanced,

        /// <summary>256 colours, error-diffusion dither.</summary>
        Best
    }

    /// <summary>What fills the space around images of a different shape.</summary>
    public enum GifFill
    {
        Transparent,
        White,
        Black
    }

    /// <summary>The video picked in Video mode, as ffprobe reports it (rotation already applied to the size).</summary>
    public sealed record GifVideoInfo(string FullPath, TimeSpan Duration, int Width, int Height, double FrameRate, long SizeBytes)
    {
        public string Name => Path.GetFileName(FullPath);

        /// <summary>"0:42 · 1920×1080 · 30 fps".</summary>
        public string Details
        {
            get
            {
                var parts = new List<string> { GifText.FormatTime(Duration) };
                if (Width > 0 && Height > 0) parts.Add($"{Width}×{Height}");
                if (FrameRate > 0) parts.Add($"{FrameRate:0.##} fps");
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>Something the user added in Images mode: one file or a whole folder.</summary>
    public sealed class GifImageSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
    }

    /// <summary>One row of the Files card (Images mode).</summary>
    public sealed class GifImageGroup
    {
        public required string FolderPath { get; init; }
        public bool IsFolder { get; init; }
        public List<string> FilePaths { get; init; } = new();
        public bool IncludeSubfolders { get; init; }
        public int ImageCount { get; init; }

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
            ? (IncludeSubfolders ? "Folder and subfolders · " : "Folder · ") + GifText.Plural(ImageCount, "image", "images")
            : FilePaths.Count == 1 ? Path.GetFileName(FilePaths[0]) : GifText.Plural(FilePaths.Count, "image", "images");

        public string ToolTip => IsFolder
            ? FolderPath
            : string.Join(Environment.NewLine, FilePaths.Take(15)) + (FilePaths.Count > 15 ? $"{Environment.NewLine}+{FilePaths.Count - 15} more" : "");
    }

    /// <summary>One frame in Images mode (one row in the Frames list).</summary>
    public sealed class GifImageItem : INotifyPropertyChanged
    {
        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public long SizeBytes { get; init; }
        public string Size => GifText.FormatBytes(SizeBytes);
        public DateTime Modified { get; init; }

        private int _number;
        /// <summary>1-based place in the GIF.</summary>
        public int Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        private int _width;
        public int Width
        {
            get => _width;
            set
            {
                if (Set(ref _width, value)) OnPropertyChanged(nameof(DimensionsText));
            }
        }

        private int _height;
        public int Height
        {
            get => _height;
            set
            {
                if (Set(ref _height, value)) OnPropertyChanged(nameof(DimensionsText));
            }
        }

        public string DimensionsText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "–";

        private string? _problem;
        /// <summary>Why the image can't be used (ffprobe couldn't read it), else null.</summary>
        public string? Problem
        {
            get => _problem;
            set
            {
                if (Set(ref _problem, value)) OnPropertyChanged(nameof(HasProblem));
            }
        }

        public bool HasProblem => Problem != null;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Everything one GIF needs from the sidebar and the trim bar.</summary>
    public sealed record GifOptions
    {
        public GifSourceMode Mode { get; init; } = GifSourceMode.Video;

        /// <summary>Video mode: frames per second in the GIF.</summary>
        public int Fps { get; init; } = 15;

        /// <summary>Largest width in pixels (0 = keep the source width). Never upscales.</summary>
        public int MaxWidth { get; init; } = 480;
        public GifQuality Quality { get; init; } = GifQuality.Balanced;

        /// <summary>How many times it plays: 0 = forever.</summary>
        public int Plays { get; init; }

        /// <summary>Video mode: playback speed (0.5 = slow motion, 2 = twice as fast).</summary>
        public double Speed { get; init; } = 1;

        /// <summary>Video mode: the selection in the source video.</summary>
        public double StartSeconds { get; init; }
        public double EndSeconds { get; init; }

        /// <summary>Images mode: how long each image stays on screen.</summary>
        public double SecondsPerImage { get; init; } = 0.5;

        /// <summary>Images mode: what's around images of another shape.</summary>
        public GifFill Fill { get; init; } = GifFill.Transparent;
    }

    /// <summary>A stage of making a GIF and how far along it is (0-100).</summary>
    public sealed record GifProgress(string Stage, double Percent);

    /// <summary>A finished GIF.</summary>
    public sealed record GifResult(string Path, long Bytes, int Width, int Height, int Frames, TimeSpan Length);

    /// <summary>Saved choices (%AppData%\Smart Tools\gif_maker.json). No folders.</summary>
    public sealed class GifSettings
    {
        public GifSourceMode Mode { get; set; } = GifSourceMode.Video;
        public int Fps { get; set; } = 15;
        public int MaxWidth { get; set; } = 480;
        public GifQuality Quality { get; set; } = GifQuality.Balanced;
        public int Plays { get; set; }
        public double Speed { get; set; } = 1;
        public double SecondsPerImage { get; set; } = 0.5;
        public GifFill Fill { get; set; } = GifFill.Transparent;
        public bool IncludeSubfolders { get; set; } = true;
        public bool AskEachTime { get; set; }
    }

    /// <summary>Small text helpers for this tool.</summary>
    public static class GifText
    {
        public static string FormatBytes(long bytes) => Smart_Tools.WPF.Controllers.ByteFormat.Format(bytes);

        /// <summary>"0:42", "1:02:03".</summary>
        public static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        /// <summary>"0:04.2" - the trim bar needs tenths.</summary>
        public static string FormatPrecise(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
                : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
        }

        public static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
    }
}
