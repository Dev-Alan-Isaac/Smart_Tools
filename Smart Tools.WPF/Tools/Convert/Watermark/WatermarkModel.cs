using System.ComponentModel;
using System.IO;

namespace Smart_Tools.WPF.Models
{
    // Batch Watermarker - plain data only (no WPF types, no I/O).

    public enum WatermarkKind { Text, Image }

    /// <summary>Which engine handles a file.</summary>
    public enum WatermarkFileKind { Image, Video, Pdf }

    /// <summary>Where one mark goes: a spot on a 3 x 3 grid, or repeated across the whole page (Tiled).</summary>
    public enum WatermarkPlacement
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, Center, MiddleRight,
        BottomLeft, BottomCenter, BottomRight,
        Tiled,
    }

    public enum WatermarkStatus { Pending, Processing, Done, Skipped, Failed }

    /// <summary>
    /// How the mark looks and where it goes. Sizes are percentages of the file, so one setup suits a phone photo, a 4K video and an A4 page alike.
    /// </summary>
    public sealed class WatermarkSettings
    {
        public WatermarkKind Kind { get; set; } = WatermarkKind.Text;
        public string Text { get; set; } = "© Your name";
        public string FontFamily { get; set; } = "Segoe UI";
        public bool Bold { get; set; } = true;
        public bool Italic { get; set; }

        /// <summary>#RRGGBB.</summary>
        public string Color { get; set; } = "#FFFFFF";

        /// <summary>A thin contrasting edge around the text, so it stays readable on any picture.</summary>
        public bool Outline { get; set; } = true;

        /// <summary>Text: height as a share of the shorter side of the file. Image: width as a share of the file's width.</summary>
        public double SizePercent { get; set; } = 6;

        public double OpacityPercent { get; set; } = 60;

        /// <summary>Degrees, clockwise.</summary>
        public double Rotation { get; set; }

        public WatermarkPlacement Placement { get; set; } = WatermarkPlacement.BottomRight;

        /// <summary>Space between the mark and the edge, as a share of the shorter side.</summary>
        public double MarginPercent { get; set; } = 3;

        /// <summary>Tiled: space between marks, as a share of the mark's height.</summary>
        public double TileGapPercent { get; set; } = 120;

        /// <summary>Image marks: the name of the app-owned copy of the logo (never the user's own path).</summary>
        public string? LogoFile { get; set; }

        public WatermarkSettings Clone() => (WatermarkSettings)MemberwiseClone();
    }

    /// <summary>A named setup. Built-in ones can't be changed or removed.</summary>
    public sealed class WatermarkPreset
    {
        public string Name { get; set; } = "";
        public WatermarkSettings Settings { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        public bool BuiltIn { get; set; }
    }

    /// <summary>One file in the list.</summary>
    public sealed class WatermarkItem : INotifyPropertyChanged
    {
        public string FullPath { get; init; } = "";
        public string Name { get; init; } = "";
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public WatermarkFileKind Kind { get; init; }
        public long SizeBytes { get; init; }
        public WatermarkGroup? Group { get; set; }

        private WatermarkStatus _status;
        private string _statusText = "";
        private string _info = "";
        private double _progress;
        private string _outputPath = "";

        public WatermarkStatus Status { get => _status; set { _status = value; Notify(nameof(Status), nameof(IsBusy)); } }
        public string StatusText { get => _statusText; set { _statusText = value; Notify(nameof(StatusText)); } }

        /// <summary>"1920 × 1080", "12 pages" or "0:32" once it has been read.</summary>
        public string Info { get => _info; set { _info = value; Notify(nameof(Info)); } }

        /// <summary>0-100 while this file is being marked.</summary>
        public double Progress { get => _progress; set { _progress = value; Notify(nameof(Progress)); } }

        public string OutputPath { get => _outputPath; set { _outputPath = value; Notify(nameof(OutputPath)); } }

        public bool IsBusy => Status == WatermarkStatus.Processing;

        public string SizeText => FormatBytes(SizeBytes);

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return unit == 0 ? $"{bytes:N0} B" : $"{size:0.#} {units[unit]}";
        }

        private void Notify(params string[] names)
        {
            foreach (string n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One row on the Files card: a folder (with or without subfolders) or a set of loose files.</summary>
    public sealed class WatermarkGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }

        private bool _includeSubfolders;
        public bool IncludeSubfolders { get => _includeSubfolders; set { _includeSubfolders = value; Refresh(); } }

        public List<WatermarkItem> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = Items.Count == 1 ? "1 file" : $"{Items.Count:N0} files";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "Files from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Live numbers of a running batch.</summary>
    public sealed record WatermarkProgress(int Done, int Total, int Failed, int Skipped, string Current, double Percent);

    public sealed class WatermarkRunResult
    {
        public int Done { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public bool Cancelled { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<string> OutputFiles { get; } = new();
    }

    /// <summary>The setup remembered between sessions: how the mark looks, and the sidebar. Never a file, a folder or a logo path.</summary>
    public sealed class WatermarkViewSettings
    {
        public WatermarkSettings Mark { get; set; } = new();
        public string? PresetName { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool OptionsCollapsed { get; set; }
    }
}
