using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Models
{
    /// <summary>Which characters the picture is drawn with.</summary>
    public enum AsciiCharSet
    {
        /// <summary>The short classic ramp: @ % # * + = - : . and a space.</summary>
        Classic,
        /// <summary>The long classic ramp (70 characters): finer tones.</summary>
        ClassicDetailed,
        /// <summary>A to Z, a to z.</summary>
        Letters,
        /// <summary>Punctuation and symbols only.</summary>
        Special,
        /// <summary>Letters, digits and symbols together.</summary>
        Mixed,
        /// <summary>Whatever the user typed.</summary>
        Custom
    }

    public enum AsciiColorMode
    {
        BlackWhite,
        Colored
    }

    public enum AsciiMode
    {
        Convert,
        Draw
    }

    public enum AsciiDrawTool
    {
        Pencil,
        Eraser,
        Fill,
        Line,
        Rectangle,
        Text,
        Picker
    }

    /// <summary>What the tool remembers between runs: how it is set up, never which files or folders were used.</summary>
    public sealed class AsciiSettings
    {
        public int Columns { get; set; } = 120;
        /// <summary>When on, the width in characters follows the picture's own width, so a big picture keeps its detail.</summary>
        public bool MatchOriginal { get; set; }
        public AsciiCharSet CharSet { get; set; } = AsciiCharSet.Classic;
        public string CustomChars { get; set; } = "";
        public bool UseSpace { get; set; } = true;
        public string FontName { get; set; } = "Consolas";
        public AsciiColorMode Color { get; set; } = AsciiColorMode.BlackWhite;
        public bool DarkBackground { get; set; } = true;
        public int Contrast { get; set; }
        public int Brightness { get; set; }
        public bool AutoLevels { get; set; } = true;
        public bool Sharpen { get; set; } = true;
        public bool Invert { get; set; }
        /// <summary>Pick each character by its shape as well as its darkness, so edges and lines stay sharp.</summary>
        public bool MatchShapes { get; set; } = true;
        public double Zoom { get; set; } = 1.0;
        public int DrawCols { get; set; } = 80;
        public int DrawRows { get; set; } = 40;
        public string DrawChar { get; set; } = "#";
        public bool Symmetry { get; set; }
    }

    /// <summary>The character picture: one character and one colour per cell (colour 0 = the default text colour).</summary>
    public sealed class AsciiGrid
    {
        public int Cols { get; }
        public int Rows { get; }
        public char[] Chars { get; }
        /// <summary>0xAARRGGBB per cell; 0 means "default colour".</summary>
        public uint[] Colors { get; }

        public AsciiGrid(int cols, int rows)
        {
            Cols = Math.Max(1, cols);
            Rows = Math.Max(1, rows);
            Chars = new char[Cols * Rows];
            Colors = new uint[Cols * Rows];
            Array.Fill(Chars, ' ');
        }

        public char this[int x, int y] => Chars[y * Cols + x];
        public uint ColorAt(int x, int y) => Colors[y * Cols + x];
        public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Cols && y < Rows;

        public AsciiGrid Clone()
        {
            var g = new AsciiGrid(Cols, Rows);
            Array.Copy(Chars, g.Chars, Chars.Length);
            Array.Copy(Colors, g.Colors, Colors.Length);
            return g;
        }

        /// <summary>A copy resized to a new size: the old picture stays at the top left, the rest is blank.</summary>
        public AsciiGrid Resized(int cols, int rows)
        {
            var g = new AsciiGrid(cols, rows);
            int w = Math.Min(Cols, g.Cols), h = Math.Min(Rows, g.Rows);
            for (int y = 0; y < h; y++)
            {
                Array.Copy(Chars, y * Cols, g.Chars, y * g.Cols, w);
                Array.Copy(Colors, y * Cols, g.Colors, y * g.Cols, w);
            }
            return g;
        }

        public bool IsBlank
        {
            get
            {
                foreach (char c in Chars) if (c != ' ') return false;
                return true;
            }
        }
    }

    public enum AsciiItemStatus
    {
        Waiting,
        Working,
        Done,
        Failed
    }

    /// <summary>One picture in the list on the right: its name, a small thumbnail and how saving it went.</summary>
    public sealed class AsciiItem : INotifyPropertyChanged
    {
        private AsciiItemStatus _status;
        private string _message = "";
        private BitmapSource? _thumbnail;

        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);

        public AsciiItemStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnChanged(); OnChanged(nameof(StatusText)); } }
        }

        public string Message
        {
            get => _message;
            set { if (_message != value) { _message = value; OnChanged(); OnChanged(nameof(StatusText)); } }
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnChanged(); }
        }

        /// <summary>The second line of the row (the folder it came from until it is saved).</summary>
        public string StatusText => _status switch
        {
            AsciiItemStatus.Working => Loc.T("Str_AsciiArt_ItemWorking", "Working..."),
            AsciiItemStatus.Done => _message.Length > 0 ? _message : Loc.T("Str_AsciiArt_ItemSaved", "Saved"),
            AsciiItemStatus.Failed => _message.Length > 0 ? _message : Loc.T("Str_AsciiArt_ItemFailed", "Could not be made"),
            _ => Path.GetDirectoryName(FullPath) ?? "",
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
    }

    /// <summary>What the user sees in the "how it was made" line after a conversion.</summary>
    public sealed record AsciiConvertInfo(int Cols, int Rows, int SourceWidth, int SourceHeight, int CharCount, int IgnoredChars);
}
