using System.ComponentModel;
using System.IO;

namespace Smart_Tools.WPF.Models
{
    // Image Sprite Sheet Packer - plain data only (no WPF types, no I/O).

    public enum SpriteMode { Pack, Slice }

    /// <summary>The file that says where each sprite is on the sheet.</summary>
    public enum SpriteCoordFormat { Json, Css, Xml, None }

    /// <summary>Slice by how many cells there are, or by how big each cell is.</summary>
    public enum SpriteSplitMode { ByCount, ByCellSize }

    public enum SpriteItemStatus { Pending, Ready, Failed, DoesNotFit }

    /// <summary>One image in the list. Sizes and the thumbnail arrive after it is added (read off the UI thread).</summary>
    public sealed class SpriteItem : INotifyPropertyChanged
    {
        public string FullPath { get; init; } = "";
        public string Name { get; init; } = "";
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public long SizeBytes { get; init; }
        public SpriteGroup? Group { get; set; }

        private int _width, _height, _trimWidth, _trimHeight;
        private bool _isChecked = true;
        private SpriteItemStatus _status;
        private string _statusText = "";
        private object? _thumbnail;

        public int Width { get => _width; set { _width = value; Notify(nameof(Width), nameof(SizeText)); } }
        public int Height { get => _height; set { _height = value; Notify(nameof(Height), nameof(SizeText)); } }

        /// <summary>The size actually packed when blank borders are trimmed (0 = not known or not trimmed).</summary>
        public int TrimWidth { get => _trimWidth; set { _trimWidth = value; Notify(nameof(TrimWidth), nameof(TrimText), nameof(HasTrim)); } }
        public int TrimHeight { get => _trimHeight; set { _trimHeight = value; Notify(nameof(TrimHeight), nameof(TrimText), nameof(HasTrim)); } }

        /// <summary>The tick box: only ticked images go on the sheet.</summary>
        public bool IsChecked { get => _isChecked; set { if (_isChecked == value) return; _isChecked = value; Notify(nameof(IsChecked)); } }

        public SpriteItemStatus Status { get => _status; set { _status = value; Notify(nameof(Status)); } }
        public string StatusText { get => _statusText; set { _statusText = value; Notify(nameof(StatusText)); } }
        public object? Thumbnail { get => _thumbnail; set { _thumbnail = value; Notify(nameof(Thumbnail)); } }

        public string SizeText => Width > 0 ? $"{Width} × {Height}" : "…";
        public bool HasTrim => TrimWidth > 0 && (TrimWidth != Width || TrimHeight != Height);
        public string TrimText => HasTrim ? $"→ {TrimWidth} × {TrimHeight}" : "";

        private void Notify(params string[] names)
        {
            foreach (string n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One row on the Files card: a folder (with or without subfolders) or a set of loose files.</summary>
    public sealed class SpriteGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }

        private bool _includeSubfolders;
        public bool IncludeSubfolders { get => _includeSubfolders; set { _includeSubfolders = value; Refresh(); } }

        public List<SpriteItem> Items { get; } = new();

        public string Name => FolderPath == null
            ? Loc.T("Str_SpritePacker_MsgSeveralFolders", "Several folders")
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = Items.Count == 1 ? Loc.T("Str_SpritePacker_MsgImageOne", "1 image") : Loc.F("Str_SpritePacker_MsgImageMany", "{0:N0} images", Items.Count);
                if (!IsFolder) return count;
                return IncludeSubfolders ? Loc.F("Str_SpritePacker_MsgFolderAndSub", "Folder and subfolders · {0}", count) : Loc.F("Str_SpritePacker_MsgFolderOnly", "Folder · {0}", count);
            }
        }

        public string ToolTip => FolderPath ?? Loc.T("Str_SpritePacker_MsgMoreThanOne", "Images from more than one folder");

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ---- packing

    public sealed record SpritePackOptions
    {
        /// <summary>Empty pixels kept between sprites (stops neighbours bleeding into each other when scaled).</summary>
        public int Padding { get; init; } = 2;

        /// <summary>The biggest a sheet may be on either side; more sprites than fit make another sheet.</summary>
        public int MaxSide { get; init; } = 2048;

        /// <summary>Sheet sides are 16, 32, 64 ... instead of as small as possible.</summary>
        public bool PowerOfTwo { get; init; } = true;
    }

    /// <summary>What the packer needs to know about one sprite. The packed size is the trimmed size.</summary>
    public sealed record SpritePackInput(int Index, string Name, int SourceWidth, int SourceHeight, int TrimX, int TrimY, int TrimWidth, int TrimHeight);

    /// <summary>Where one sprite went. X, Y, Width, Height are on the sheet; Trim* is the part of the source image that was kept.</summary>
    public sealed record SpritePlacement(int Index, string Name, int Page, int X, int Y, int Width, int Height,
        int TrimX, int TrimY, int SourceWidth, int SourceHeight)
    {
        public bool Trimmed => TrimX != 0 || TrimY != 0 || Width != SourceWidth || Height != SourceHeight;
    }

    public sealed class SpritePage
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public List<SpritePlacement> Placements { get; init; } = new();

        /// <summary>The share of the sheet that holds sprites (not padding or blank space).</summary>
        public double UsedPercent => Width * Height == 0 ? 0 : 100.0 * Placements.Sum(p => (long)p.Width * p.Height) / ((long)Width * Height);
    }

    public sealed class SpritePackResult
    {
        public List<SpritePage> Pages { get; init; } = new();

        /// <summary>Indices of sprites that are bigger than the biggest allowed sheet.</summary>
        public List<int> TooBig { get; init; } = new();
    }

    // ---- slicing

    public sealed record SpriteSliceOptions
    {
        public SpriteSplitMode Mode { get; init; } = SpriteSplitMode.ByCount;
        public int Columns { get; init; } = 4;
        public int Rows { get; init; } = 4;
        public int CellWidth { get; init; } = 32;
        public int CellHeight { get; init; } = 32;

        /// <summary>Pixels around the whole sheet that belong to no cell.</summary>
        public int Margin { get; init; }

        /// <summary>Pixels between neighbouring cells.</summary>
        public int Spacing { get; init; }
    }

    public sealed record SpriteCell(int Index, int Row, int Column, int X, int Y, int Width, int Height);

    public sealed class SpriteSliceResult
    {
        public string ProblemCode { get; init; } = "";
        public bool Ok => ProblemCode.Length == 0;
        public int Columns { get; init; }
        public int Rows { get; init; }
        public int CellWidth { get; init; }
        public int CellHeight { get; init; }

        /// <summary>Pixels at the right and bottom that did not fit a whole cell.</summary>
        public int LeftoverX { get; init; }
        public int LeftoverY { get; init; }
        public List<SpriteCell> Cells { get; init; } = new();
    }

    /// <summary>The setup that is remembered between sessions. Never an image, a name or a path.</summary>
    public sealed class SpritePackerSettings
    {
        public SpriteMode Mode { get; set; } = SpriteMode.Pack;
        public int Padding { get; set; } = 2;
        public int MaxSide { get; set; } = 2048;
        public bool PowerOfTwo { get; set; } = true;
        public bool Trim { get; set; } = true;
        public SpriteCoordFormat Format { get; set; } = SpriteCoordFormat.Json;
        public bool IncludeSubfolders { get; set; } = true;
        public SpriteSplitMode SplitMode { get; set; } = SpriteSplitMode.ByCount;
        public int Columns { get; set; } = 4;
        public int Rows { get; set; } = 4;
        public int CellWidth { get; set; } = 32;
        public int CellHeight { get; set; } = 32;
        public int Margin { get; set; }
        public int Spacing { get; set; }
        public bool SkipEmpty { get; set; } = true;
        public string NamePrefix { get; set; } = "sprite";
        public bool OptionsCollapsed { get; set; }
    }
}
