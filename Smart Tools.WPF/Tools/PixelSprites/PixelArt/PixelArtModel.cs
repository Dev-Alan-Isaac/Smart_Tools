using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    // Pixel Art Editor - plain data only (no WPF types, no I/O).
    // Colours are packed ARGB in a uint (0xAARRGGBB). Fully transparent is always 0.

    public enum PixelArtTool { Pencil, Eraser, Fill, Line, Rectangle, Ellipse, Picker }

    /// <summary>Mirrors every stroke across the canvas centre.</summary>
    public enum PixelArtSymmetry { None, Horizontal, Vertical, Both }

    /// <summary>Where the old drawing sits when the canvas gets a new size.</summary>
    public enum PixelArtAnchor { TopLeft, Center }

    /// <summary>The picture: one or more frames of equal size.</summary>
    public sealed class PixelArtDocument
    {
        public const int MaxSide = 256;
        public const int MaxFrames = 64;

        public int Width { get; }
        public int Height { get; }
        public List<uint[]> Frames { get; }

        public PixelArtDocument(int width, int height, List<uint[]>? frames = null)
        {
            Width = Math.Clamp(width, 1, MaxSide);
            Height = Math.Clamp(height, 1, MaxSide);
            Frames = frames ?? new List<uint[]> { new uint[Width * Height] };
        }

        public PixelArtDocument Clone() => new(Width, Height, Frames.Select(f => (uint[])f.Clone()).ToList());
    }

    /// <summary>A colour in the palette strip. <see cref="Brush"/> is set by the view (a frozen brush), so this stays WPF-free.</summary>
    public sealed class PixelArtSwatch : INotifyPropertyChanged
    {
        public uint Argb { get; init; }
        public string Hex { get; init; } = "";
        public object? Brush { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One thumbnail in the frame strip. <see cref="Thumbnail"/> is an image source set by the view.</summary>
    public sealed class PixelArtFrameItem : INotifyPropertyChanged
    {
        private int _number;
        private bool _isCurrent;
        private object? _thumbnail;

        /// <summary>1-based, as shown.</summary>
        public int Number { get => _number; set => Set(ref _number, value, nameof(Number)); }
        public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value, nameof(IsCurrent)); }
        public object? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value, nameof(Thumbnail)); }

        private void Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>A named set of colours offered in the sidebar.</summary>
    public sealed record PixelArtPalette(string Name, uint[] Colors);

    /// <summary>How the PNG is made.</summary>
    public sealed record PixelArtExportOptions
    {
        /// <summary>Whole-number enlargement (nearest neighbour, so pixels stay sharp).</summary>
        public int Scale { get; init; } = 8;

        /// <summary>All frames in one picture instead of just the current frame.</summary>
        public bool Sheet { get; init; }

        /// <summary>Frames per row of the sheet.</summary>
        public int Columns { get; init; } = 1;
    }

    public sealed class PixelArtExportResult
    {
        public string ProblemCode { get; init; } = "";
        public bool Ok => ProblemCode.Length == 0;
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Png { get; init; } = Array.Empty<byte>();
    }

    /// <summary>The setup that is remembered between sessions. Never the artwork, and never a file path.</summary>
    public sealed class PixelArtSettings
    {
        public PixelArtTool Tool { get; set; } = PixelArtTool.Pencil;
        public int BrushSize { get; set; } = 1;
        public PixelArtSymmetry Symmetry { get; set; } = PixelArtSymmetry.None;
        public bool FilledShapes { get; set; }
        public bool ShowGrid { get; set; } = true;
        public bool OnionSkin { get; set; }
        public int ExportScale { get; set; } = 8;
        public int Fps { get; set; } = 8;
        public int NewWidth { get; set; } = 32;
        public int NewHeight { get; set; } = 32;
        public string PaletteName { get; set; } = "PICO-8";
        public string? Color { get; set; }
        public List<string>? CustomColors { get; set; }
        public bool OptionsCollapsed { get; set; }
    }
}
