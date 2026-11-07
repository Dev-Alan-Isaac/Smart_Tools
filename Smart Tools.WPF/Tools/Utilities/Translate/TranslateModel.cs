namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// One translation direction, like English to Spanish. The model is an Opus-MT export hosted on Hugging Face
    /// (repo "Xenova/opus-mt-{from}-{to}"). Language names are shown from the string table (Str_Translate_Lang_{code}).
    /// </summary>
    public sealed record TranslateLanguagePair(string FromCode, string ToCode, int ApproxMb)
    {
        public string Id => $"{FromCode}-{ToCode}";
        public string Repo => $"Xenova/opus-mt-{FromCode}-{ToCode}";
    }

    /// <summary>One row of the Translation languages list in Settings (a pair, its state, and where its files are).</summary>
    public sealed class TranslateModelItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isInstalled, _isBusy;
        private double _progress;
        private string _detail = "";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public TranslateModelItem(TranslateLanguagePair pair, string display, string folderPath, bool isCustom)
        {
            Pair = pair;
            Display = display;
            FolderPath = folderPath;
            IsCustom = isCustom;
        }

        public TranslateLanguagePair Pair { get; }
        public string Display { get; }

        /// <summary>The folder the model's files are (or will be) in.</summary>
        public string FolderPath { get; }

        /// <summary>Added by the user rather than built in, so it can be taken off the list.</summary>
        public bool IsCustom { get; }

        public bool IsInstalled { get => _isInstalled; set => Set(ref _isInstalled, value); }
        public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

        /// <summary>0..100 while downloading.</summary>
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        /// <summary>A size like "110 MB", the download percent, or a short error.</summary>
        public string Detail { get => _detail; set => Set(ref _detail, value); }

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    /// <summary>The outcome of one translation. When canceled, <see cref="Text"/> holds what was finished so far.</summary>
    public sealed record TranslateResult(string Text, bool Canceled, int FailedParts, int TotalParts);

    /// <summary>
    /// The outcome of translating one file. <see cref="OutputPath"/> is the new file (null when nothing was saved);
    /// <see cref="ErrorKey"/> is a language key for the reason when the file could not be translated.
    /// </summary>
    public sealed record TranslateFileResult(string? OutputPath, bool Canceled, int FailedParts, int TotalParts, string? ErrorKey = null);

    public enum TranslateFileState { Waiting, Running, Done, Failed, Stopped }

    /// <summary>One file in Translate's Files mode: what the list shows and how far it is.</summary>
    public sealed class TranslateFileItem : System.ComponentModel.INotifyPropertyChanged
    {
        private TranslateFileState _state;
        private double _percent;
        private string _status = "";
        private string? _outputPath;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public TranslateFileItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            try { SizeText = $"{new System.IO.FileInfo(path).Length / 1024.0:N0} KB"; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { SizeText = ""; }
        }

        public string Path { get; }
        public string Name { get; }
        public string SizeText { get; }
        public string ToolTipText => Path;

        public TranslateFileState State { get => _state; set { if (Set(ref _state, value)) { Raise(nameof(IsRunning)); Raise(nameof(IsDone)); } } }
        public double Percent { get => _percent; set => Set(ref _percent, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public string? OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }

        public bool IsRunning => _state == TranslateFileState.Running;
        public bool IsDone => _state == TranslateFileState.Done;

        private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name!);
            return true;
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
