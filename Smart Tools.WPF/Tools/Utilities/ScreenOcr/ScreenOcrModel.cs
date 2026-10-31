namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  OCR Text Extractor (Screen to Text) data. Plain data - no WPF, no
    //  Windows Runtime types. ScreenOcrManager fills these; the view binds.
    // =====================================================================

    /// <summary>A picture to read: 32-bit BGRA pixels, top row first.</summary>
    public sealed record ScreenOcrImage(byte[] Pixels, int Width, int Height)
    {
        public int Stride => Width * 4;
    }

    /// <summary>An OCR language Windows has installed ("en-US", "English (United States)").</summary>
    public sealed record ScreenOcrLanguage(string Tag, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>One recognised word and where it is, in the pixels of the image that was read.</summary>
    public sealed record ScreenOcrWord(string Text, double X, double Y, double Width, double Height);

    /// <summary>What reading one image produced.</summary>
    public sealed class ScreenOcrResult
    {
        /// <summary>The text with the original line breaks.</summary>
        public string Text { get; init; } = "";

        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ScreenOcrWord> Words { get; init; } = Array.Empty<ScreenOcrWord>();
        public string LanguageName { get; init; } = "";

        /// <summary>The image was scaled by this before reading (small captures are enlarged, huge ones shrunk); word boxes are already mapped back.</summary>
        public double Scale { get; init; } = 1;

        public TimeSpan Elapsed { get; init; }
        public int WordCount => Words.Count;
        public int LineCount => Lines.Count;
        public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>How the lines are put together in the text box.</summary>
    public enum ScreenOcrLayout
    {
        /// <summary>One line of text per line on screen.</summary>
        KeepLines,

        /// <summary>Lines joined with spaces (a paragraph that wrapped on screen becomes one line).</summary>
        JoinLines,
    }

    /// <summary>One row of the Tesseract language list in Settings: a language, whether it's installed, and its download state.</summary>
    public sealed class TesseractLanguageItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isInstalled, _isBusy;
        private double _progress;
        private string _detail = "";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public TesseractLanguageItem(string code, string display) { Code = code; Display = display; }

        public string Code { get; }
        public string Display { get; }

        /// <summary>English ships with the app, so it can't be removed.</summary>
        public bool CanRemove => !Code.Equals("eng", StringComparison.OrdinalIgnoreCase);

        public bool IsInstalled { get => _isInstalled; set => Set(ref _isInstalled, value); }
        public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

        /// <summary>0..100 while downloading.</summary>
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        /// <summary>A size like "2.1 MB", the download percent, or a short error.</summary>
        public string Detail { get => _detail; set => Set(ref _detail, value); }

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Which program reads the text.</summary>
    public enum ScreenOcrEngineKind
    {
        /// <summary>Windows' built-in OCR. Languages come from Windows language packs.</summary>
        Windows,

        /// <summary>The bundled Tesseract. Languages are downloaded in Settings.</summary>
        Tesseract,
    }

    /// <summary>Remembered between sessions (ocr_text_extractor.json in %AppData%\Smart Tools). Holds no text and no paths.</summary>
    public sealed class ScreenOcrSettings
    {
        /// <summary>Language tag, "auto" for every installed language, or empty for "same as Windows".</summary>
        public string Language { get; set; } = "";

        public ScreenOcrEngineKind Engine { get; set; } = ScreenOcrEngineKind.Windows;

        /// <summary>Tesseract language code (a downloaded one, like "eng"), or "auto" for every downloaded language.</summary>
        public string TesseractLanguage { get; set; } = "eng";

        /// <summary>Clean up blurry, low-contrast, or dark pictures and retry when the first reading is poor.</summary>
        public bool Enhance { get; set; } = true;

        public ScreenOcrLayout Layout { get; set; } = ScreenOcrLayout.KeepLines;
        public bool CopyWhenDone { get; set; } = true;
        public bool HideWhileCapturing { get; set; } = true;
    }
}
