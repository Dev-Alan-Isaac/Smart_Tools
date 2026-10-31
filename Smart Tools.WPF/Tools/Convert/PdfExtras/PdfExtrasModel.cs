using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  PDF Extras data model - plain data only (no WPF, no I/O).
    //  Every type carries a "PdfExtra" prefix because all tools share this namespace (PDF Toolkit owns Pdf*).
    // =====================================================================

    public enum PdfExtraOperation
    {
        Rotate,
        Pages,
        PageNumbers,
        Properties,
        Protect,
        Unlock,
        Crop,
        PageSize,
        Arrange,
    }

    /// <summary>Paper sizes a PDF can be fitted onto. Portrait sizes in points (1/72 inch); a landscape page gets the same size turned.</summary>
    public enum PdfExtraPaper
    {
        A5,
        A4,
        A3,
        Letter,
        Legal,
    }

    /// <summary>Remove the typed pages, or keep only them.</summary>
    public enum PdfExtraPagesMode
    {
        Remove,
        KeepOnly,
    }

    public enum PdfExtraNumberPosition
    {
        BottomCenter,
        BottomRight,
        BottomLeft,
        TopCenter,
        TopRight,
        TopLeft,
    }

    /// <summary>How the page-number text is built. {n} is the page number and {total} the last number.</summary>
    public enum PdfExtraNumberFormat
    {
        Plain,        // 3
        Page,         // Page 3
        PageOfTotal,  // Page 3 of 12
        Slash,        // 3 / 12
    }

    public enum PdfExtraOutcome
    {
        Done,
        NothingToDo,       // the options leave the PDF as it is
        BadPages,          // the typed pages are empty, wrong, or outside the PDF
        NeedsPassword,
        WrongPassword,
        NeedsOwnerPassword,   // opens for reading, but changing it needs the owner password
        NoPageLeft,
        CropTooBig,           // the margins would leave a page with nothing (Detail holds the page number)        // removing those pages would leave nothing
        Failed,
        Canceled,
    }

    /// <summary>
    /// One page of an arranged PDF: page <see cref="PageIndex"/> (0-based) of source <see cref="Source"/> (0 is the PDF
    /// being arranged, 1 and up are PDFs pages were inserted from), turned a further <see cref="ExtraRotation"/> degrees,
    /// or a blank page of <see cref="Width"/> by <see cref="Height"/> points when <see cref="Source"/> is -1.
    /// </summary>
    public sealed record PdfPagePlan(int Source, int PageIndex, int ExtraRotation = 0, double Width = 0, double Height = 0)
    {
        public bool IsBlank => Source < 0;
    }

    /// <summary>One page tile in the page editor. Plain data: the picture is held as an object and drawn by the view.</summary>
    public sealed class PdfPageTile : INotifyPropertyChanged
    {
        private int _extraRotation;
        private int _number;
        private object? _thumb;
        private bool _markedBlank;

        /// <summary>0 is the PDF being arranged; 1 and up are PDFs pages were inserted from; -1 is a blank page.</summary>
        public int Source { get; init; }
        public int PageIndex { get; init; }

        /// <summary>The page as shown before any extra turn, in points.</summary>
        public double Width { get; init; }
        public double Height { get; init; }

        public string Caption { get; init; } = "";
        public string Tip { get; init; } = "";

        public int ExtraRotation
        {
            get => _extraRotation;
            set { if (_extraRotation == value) return; _extraRotation = value; Raise(nameof(ExtraRotation)); }
        }

        /// <summary>Place in the new order, from 1.</summary>
        public int Number
        {
            get => _number;
            set { if (_number == value) return; _number = value; Raise(nameof(Number)); }
        }

        public object? Thumb
        {
            get => _thumb;
            set { if (ReferenceEquals(_thumb, value)) return; _thumb = value; Raise(nameof(Thumb)); Raise(nameof(HasThumb)); }
        }

        public bool HasThumb => _thumb != null;

        /// <summary>The blank-page finder thinks this page is empty.</summary>
        public bool IsMarkedBlank
        {
            get => _markedBlank;
            set { if (_markedBlank == value) return; _markedBlank = value; Raise(nameof(IsMarkedBlank)); }
        }

        public bool IsBlank => Source < 0;

        /// <summary>The size once the extra turn is applied: a page turned a quarter swaps its sides.</summary>
        public (double Width, double Height) ShownSize => ((_extraRotation % 180) + 180) % 180 == 0 ? (Width, Height) : (Height, Width);

        public PdfPagePlan ToPlan() => new(Source, PageIndex, ExtraRotation, Width, Height);

        /// <summary>A copy that shares the picture (for "duplicate").</summary>
        public PdfPageTile Clone() => new()
        {
            Source = Source, PageIndex = PageIndex, Width = Width, Height = Height, Caption = Caption, Tip = Tip,
            _extraRotation = _extraRotation, _thumb = _thumb,
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>How sure a page has to be empty to be marked as blank.</summary>
    public enum PdfBlankSensitivity
    {
        /// <summary>Only pages with next to nothing on them.</summary>
        Strict,
        Normal,

        /// <summary>Also pages with a stamp, a small logo, or only a page number.</summary>
        Loose,
    }

    /// <summary>What was read from the PDF when it was chosen.</summary>
    public sealed class PdfExtraInfo
    {
        public required string Path { get; init; }
        public long SizeBytes { get; init; }
        public int PageCount { get; init; }

        /// <summary>The PDF asks for a password before it can be opened.</summary>
        public bool NeedsPassword { get; init; }

        /// <summary>The PDF is encrypted (a password to open it, restrictions on printing or copying, or both).</summary>
        public bool IsEncrypted { get; init; }

        /// <summary>The PDF can be read, but changing it needs the owner password.</summary>
        public bool NeedsOwnerPassword { get; init; }

        public string Title { get; init; } = "";
        public string Author { get; init; } = "";
        public string Subject { get; init; } = "";
        public string Keywords { get; init; } = "";

        /// <summary>The first page's size in points as shown (turned for a rotated page); 0 when unknown.</summary>
        public double PageWidth { get; init; }
        public double PageHeight { get; init; }

        /// <summary>A sentence for the person when the PDF can't be read at all; empty when it can.</summary>
        public string Problem { get; init; } = "";
    }

    /// <summary>Everything a run needs, read from the sidebar when Apply is pressed. Passwords live only in this object.</summary>
    public sealed class PdfExtraRequest
    {
        public required PdfExtraOperation Operation { get; init; }
        public required string SourcePath { get; init; }
        public required string OutputFolder { get; init; }

        /// <summary>The password that opens the PDF, when it asks for one.</summary>
        public string OpenPassword { get; init; } = "";

        // Rotate
        public int Degrees { get; init; } = 90;        // 90, 180 or 270 clockwise

        // Rotate, Pages: pages as typed ("1-3, 5, 8-"); blank = every page for Rotate
        public string Pages { get; init; } = "";
        public PdfExtraPagesMode PagesMode { get; init; }

        // Crop: margins to hide, in millimeters, as the page is shown
        public double CropTop { get; init; }
        public double CropRight { get; init; }
        public double CropBottom { get; init; }
        public double CropLeft { get; init; }

        // Page size
        public PdfExtraPaper Paper { get; init; } = PdfExtraPaper.A4;

        // Arrange: the new page order (see PdfPagePlan), and the other PDFs pages come from
        public List<PdfPagePlan> Plan { get; init; } = new();
        public List<string> ExtraSources { get; init; } = new();

        // Page numbers
        public PdfExtraNumberPosition Position { get; init; } = PdfExtraNumberPosition.BottomCenter;
        public PdfExtraNumberFormat Format { get; init; } = PdfExtraNumberFormat.PageOfTotal;
        public int StartNumber { get; init; } = 1;
        public double FontSize { get; init; } = 10;
        public bool SkipFirstPage { get; init; }

        /// <summary>The words printed in "Page 3 of 12", in the language of the app.</summary>
        public string PageWord { get; init; } = "Page";
        public string OfWord { get; init; } = "of";

        // Properties (a blank value clears the entry); CleanAll removes every detail, hidden metadata included
        public bool CleanAll { get; init; }
        public string Title { get; init; } = "";
        public string Author { get; init; } = "";
        public string Subject { get; init; } = "";
        public string Keywords { get; init; } = "";

        // Protect
        public string NewUserPassword { get; init; } = "";
        public string NewOwnerPassword { get; init; } = "";
        public bool AllowPrint { get; init; } = true;
        public bool AllowCopy { get; init; } = true;
        public bool AllowEdit { get; init; } = true;
    }

    public sealed class PdfExtraResult
    {
        public PdfExtraOutcome Outcome { get; init; }
        public string OutputPath { get; init; } = "";
        public long SizeBefore { get; init; }
        public long SizeAfter { get; init; }

        /// <summary>How many pages the change touched (rotated, removed, numbered).</summary>
        public int PagesAffected { get; init; }
        public int PagesAfter { get; init; }

        /// <summary>The original had a password or restrictions and the new file does not (only Protect sets new ones).</summary>
        public bool ProtectionDropped { get; init; }

        /// <summary>Typed page ranges past the end of the PDF; mentioned next to the result.</summary>
        public List<string> OutsideRanges { get; init; } = new();

        /// <summary>A sentence for the person; empty when the outcome says it all.</summary>
        public string Detail { get; init; } = "";
    }

    /// <summary>What PDF Extras remembers: how it is set up, never a file, a folder, a password, or a document's text.</summary>
    public sealed class PdfExtraSettings
    {
        public PdfExtraOperation Operation { get; set; } = PdfExtraOperation.Rotate;
        public PdfExtraPaper Paper { get; set; } = PdfExtraPaper.A4;
        public PdfBlankSensitivity Blank { get; set; } = PdfBlankSensitivity.Normal;
        public bool IncludeSubfolders { get; set; }
        public int Degrees { get; set; } = 90;
        public PdfExtraPagesMode PagesMode { get; set; }
        public PdfExtraNumberPosition Position { get; set; } = PdfExtraNumberPosition.BottomCenter;
        public PdfExtraNumberFormat Format { get; set; } = PdfExtraNumberFormat.PageOfTotal;
        public int StartNumber { get; set; } = 1;
        public double FontSize { get; set; } = 10;
        public bool SkipFirstPage { get; set; }
        public bool AllowPrint { get; set; } = true;
        public bool AllowCopy { get; set; } = true;
        public bool AllowEdit { get; set; } = true;
    }
}
