using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// One PDF in the Input card's list. Only its name and path are kept here: the PDF itself is opened when it is picked and
    /// let go of when another one is picked, so a folder with hundreds of PDFs costs nothing in memory.
    /// </summary>
    public sealed class PdfReaderFile : INotifyPropertyChanged
    {
        private bool _isCurrent;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path { get; init; } = "";
        public string Name { get; init; } = "";

        /// <summary>The PDF that is open on the page right now (highlighted in the list).</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
    }

    /// <summary>What a mark on a page is.</summary>
    public enum PdfMarkKind { Text, Signature, Check, Cross, Line, Highlight }

    /// <summary>What the tool remembers between runs (%AppData%\Smart Tools\pdf-reader.json). Settings only: never a file path.</summary>
    public sealed class PdfReaderSettings
    {
        /// <summary>Size in points of text added to a page.</summary>
        public int TextSize { get; set; } = 14;
    }

    /// <summary>
    /// One thing placed on a page: typed text, a date, a signature picture, a check mark, a cross, a line or a highlight. Position and size are in PDF points
    /// measured from the top-left of the page as it is shown, so they do not depend on the zoom.
    /// </summary>
    public sealed class PdfMark : INotifyPropertyChanged
    {
        private double _x, _y, _size;
        private string _text = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public PdfMarkKind Kind { get; init; }

        /// <summary>The page (0-based) this mark is on.</summary>
        public int PageIndex { get; init; }

        /// <summary>Left edge, in points.</summary>
        public double X { get => _x; set => Set(ref _x, value); }

        /// <summary>Top edge, in points.</summary>
        public double Y { get => _y; set => Set(ref _y, value); }

        /// <summary>Text: font size in points. Signature, line and highlight: width in points (the height follows). Check and cross: side in points.</summary>
        public double Size { get => _size; set => Set(ref _size, value); }

        public string Text { get => _text; set => Set(ref _text, value); }

        /// <summary>The signature picture (PNG with transparency). Empty for the other kinds.</summary>
        public byte[] Image { get; init; } = Array.Empty<byte>();

        /// <summary>Height / width of the signature picture, so resizing keeps its shape.</summary>
        public double AspectRatio { get; init; } = 0.4;

        /// <summary>Thickness of a line mark, in points (also the height of the box you grab).</summary>
        public const double LineBox = 8;

        /// <summary>Height of a highlight as a share of its width.</summary>
        public const double HighlightRatio = 0.18;

        /// <summary>Height in points.</summary>
        public double Height => Kind switch
        {
            PdfMarkKind.Signature => Size * AspectRatio,
            PdfMarkKind.Check or PdfMarkKind.Cross => Size,
            PdfMarkKind.Line => LineBox,
            PdfMarkKind.Highlight => Size * HighlightRatio,
            _ => Size * 1.3,
        };

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(Size)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Height)));
        }
    }

    /// <summary>One page of the open PDF: its size as shown (points), its picture once drawn, and the marks on it.</summary>
    public sealed class PdfReaderPage : INotifyPropertyChanged
    {
        private object? _source;
        private double _displayWidth, _displayHeight;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Index { get; init; }

        /// <summary>Page size as shown, in points.</summary>
        public double WidthPt { get; init; }

        public double HeightPt { get; init; }

        /// <summary>Size on screen (device-independent pixels) at the current zoom.</summary>
        public double DisplayWidth { get => _displayWidth; set => Set(ref _displayWidth, value); }

        public double DisplayHeight { get => _displayHeight; set => Set(ref _displayHeight, value); }

        /// <summary>The drawn page (kept as object so this file stays free of WPF types). Null until it is drawn, and again once the page is far from the screen.</summary>
        public object? Source { get => _source; set => Set(ref _source, value); }

        public ObservableCollection<PdfMark> Marks { get; } = new();

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
