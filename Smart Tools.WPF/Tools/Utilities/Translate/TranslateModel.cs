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
}
