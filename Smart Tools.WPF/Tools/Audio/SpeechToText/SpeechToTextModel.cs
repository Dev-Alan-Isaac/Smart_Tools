using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Speech to Text data. Plain data - no WPF, no I/O. SpeechToTextManager
    //  fills these; the view binds.
    // =====================================================================

    /// <summary>A Whisper speech model that can be downloaded: "base" is ggml-base.bin.</summary>
    public sealed record SttModelInfo(string Id, string Name, int ApproxMb, bool EnglishOnly, string Hint)
    {
        public string FileName => $"ggml-{Id}.bin";
    }

    /// <summary>A language Whisper can be told to expect. An empty code means "detect it".</summary>
    public sealed record SttLanguage(string Code, string Name);

    /// <summary>One timed piece of the transcript, in seconds.</summary>
    public sealed record SttSegment(double Start, double End, string Text);

    public enum SttItemStatus { Reading, Ready, Unreadable, Working, Done, Failed, Cancelled }

    /// <summary>A row of the model list in the sidebar: a model, whether it's installed, and its download state.</summary>
    public sealed class SttModelItem : INotifyPropertyChanged
    {
        private bool _isInstalled, _isBusy;
        private double _progress;
        private string _detail = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public SttModelItem(SttModelInfo info) => Info = info;

        public SttModelInfo Info { get; }
        public string Id => Info.Id;
        public string Name => Info.Name;

        public bool IsInstalled { get => _isInstalled; set => Set(ref _isInstalled, value); }
        public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

        /// <summary>0..100 while downloading.</summary>
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        /// <summary>A size like "142 MB", the download percent, or a short error.</summary>
        public string Detail { get => _detail; set => Set(ref _detail, value); }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>One audio or video file in the list.</summary>
    public sealed class SttFileItem : INotifyPropertyChanged
    {
        private SttItemStatus _status = SttItemStatus.Reading;
        private double _progress;
        private double? _duration;
        private string _message = "", _language = "", _text = "";
        private bool _locked;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string FullPath { get; init; } = "";
        public long SizeBytes { get; init; }
        public string Name => System.IO.Path.GetFileName(FullPath);
        public string Folder => System.IO.Path.GetDirectoryName(FullPath) ?? "";

        public SttItemStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                Raise(); Raise(nameof(StatusText)); Raise(nameof(IsWorking)); Raise(nameof(CanRun));
            }
        }

        /// <summary>0..100 while working.</summary>
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        public double? Duration
        {
            get => _duration;
            set { if (Set(ref _duration, value)) Raise(nameof(DurationText)); }
        }

        /// <summary>The tooltip: why a file can't be read, or what happened.</summary>
        public string Message { get => _message; set => Set(ref _message, value); }

        /// <summary>The language Whisper found or was told to use, as a code ("en").</summary>
        public string Language { get => _language; set => Set(ref _language, value); }

        /// <summary>The transcript as shown in the text box.</summary>
        public string Text { get => _text; set => Set(ref _text, value); }

        public IReadOnlyList<SttSegment> Segments { get; set; } = Array.Empty<SttSegment>();
        public List<string> OutputPaths { get; } = new();

        public bool Locked { get => _locked; set => Set(ref _locked, value); }

        public bool IsWorking => Status == SttItemStatus.Working;
        public bool CanRun => Status is SttItemStatus.Ready or SttItemStatus.Failed or SttItemStatus.Cancelled;
        public string DurationText => Duration is double d ? SttText.FormatTime(d) : "";

        public string StatusText => Status switch
        {
            SttItemStatus.Reading => "Reading",
            SttItemStatus.Ready => "Ready",
            SttItemStatus.Unreadable => "Can't read",
            SttItemStatus.Working => "Transcribing",
            SttItemStatus.Done => "Done",
            SttItemStatus.Failed => "Failed",
            _ => "Canceled",
        };

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>What a run does.</summary>
    public sealed record SttOptions
    {
        public string ModelId { get; init; } = "base";

        /// <summary>A language code, or empty to detect it.</summary>
        public string Language { get; init; } = "";

        /// <summary>Translate the speech into English.</summary>
        public bool Translate { get; init; }

        public bool WriteTxt { get; init; } = true;
        public bool WriteSrt { get; init; }
        public bool WriteVtt { get; init; }
        public bool TimestampsInText { get; init; }
    }

    public sealed record SttProgress(int Finished, int Failed, int Total, double Percent, string? CurrentName);

    public sealed record SttRunResult(int Done, int Failed, int Cancelled, IReadOnlyList<string> Files, TimeSpan Elapsed);

    /// <summary>Remembered between sessions (speech_to_text.json). Settings only: never a path or a transcript.</summary>
    public sealed class SttSettings
    {
        public string ModelId { get; set; } = "base";
        public string Language { get; set; } = "";
        public bool Translate { get; set; }
        public bool WriteTxt { get; set; } = true;
        public bool WriteSrt { get; set; }
        public bool WriteVtt { get; set; }
        public bool TimestampsInText { get; set; }
        public bool OpenWhenDone { get; set; }
    }

    public static class SttText
    {
        /// <summary>"01:23" or "1:02:03".</summary>
        public static string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var span = TimeSpan.FromSeconds(Math.Round(seconds));
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
    }
}
