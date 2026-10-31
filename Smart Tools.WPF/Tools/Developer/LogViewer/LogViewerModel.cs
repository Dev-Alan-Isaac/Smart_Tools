using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Log Viewer data. Plain data - no WPF. LogViewerManager follows the
    //  files and fills these; the view binds to them.
    // =====================================================================

    public enum LogViewerFormat
    {
        /// <summary>Lines as they are.</summary>
        PlainText,

        /// <summary>Each line that is a JSON object is shown indented; other lines as they are.</summary>
        JsonLines,
    }

    public enum LogViewerTabState
    {
        Following,
        Paused,

        /// <summary>The file is gone; still watching for it to come back.</summary>
        Missing,
        Error,
    }

    public abstract class LogViewerObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    /// <summary>
    /// A highlight rule: lines matching Pattern (text, or a regular
    /// expression) get ColorHex as their background. The first enabled
    /// matching rule wins, so put specific rules above broad ones.
    /// </summary>
    public sealed class LogViewerRule : LogViewerObservable
    {
        private string _name = "New rule";
        private string _pattern = "";
        private string _colorHex = "#EF4444";
        private bool _enabled = true;
        private bool _isRegex;
        private bool _caseSensitive;

        public string Name { get => _name; set => Set(ref _name, value ?? ""); }
        public string Pattern { get => _pattern; set => Set(ref _pattern, value ?? ""); }
        public string ColorHex { get => _colorHex; set => Set(ref _colorHex, value ?? "#EF4444"); }
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public bool IsRegex { get => _isRegex; set => Set(ref _isRegex, value); }
        public bool CaseSensitive { get => _caseSensitive; set => Set(ref _caseSensitive, value); }

        private string? _problem;

        /// <summary>Why the pattern can't be used (an invalid regular expression), or null. Not saved.</summary>
        [JsonIgnore] public string? Problem { get => _problem; set => Set(ref _problem, value); }
    }

    /// <summary>One line shown in a tab.</summary>
    public sealed class LogViewerLine : LogViewerObservable
    {
        private string _displayText = "";
        private string? _highlightHex;
        private string? _highlightForegroundHex;

        public int LineNumber { get; init; }

        /// <summary>Exactly what was read from the file (or the notice text for a system line).</summary>
        public string RawText { get; init; } = "";

        /// <summary>A note from the viewer itself (file missing, rolled over...), not a line of the file.</summary>
        public bool IsSystem { get; init; }

        public string DisplayText { get => _displayText; set => Set(ref _displayText, value); }

        /// <summary>The matching rule's colour, or null for no highlight.</summary>
        public string? HighlightHex { get => _highlightHex; set => Set(ref _highlightHex, value); }

        /// <summary>Dark or light text, whichever reads on the highlight colour.</summary>
        public string? HighlightForegroundHex { get => _highlightForegroundHex; set => Set(ref _highlightForegroundHex, value); }
    }

    /// <summary>One open file: its follower, its lines, filter and scroll state.</summary>
    public sealed class LogViewerTab : LogViewerObservable
    {
        private LogViewerTabState _state = LogViewerTabState.Paused;
        private string _statusText = "Paused";
        private string _lineCountText = "0 lines";
        private bool _followPausedByScroll;
        private string _filterText = "";
        private bool _filterIsRegex;
        private string? _filterError;
        private LogViewerFormat _format;

        public LogViewerTab(string filePath) => FilePath = filePath;

        public string FilePath { get; }
        public string Title => Path.GetFileName(FilePath);
        public string Folder => Path.GetDirectoryName(FilePath) ?? "";

        public LogViewerFormat Format { get => _format; set => Set(ref _format, value); }

        /// <summary>The lines that pass the filter, newest last.</summary>
        public BulkObservableCollection<LogViewerLine> Lines { get; } = new();

        public LogViewerTabState State
        {
            get => _state;
            set
            {
                if (Set(ref _state, value)) OnPropertyChanged(nameof(IsFollowing));
            }
        }

        /// <summary>True while the file is being watched (also while it's missing and waited for).</summary>
        public bool IsFollowing => State is LogViewerTabState.Following or LogViewerTabState.Missing;

        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
        public string LineCountText { get => _lineCountText; set => Set(ref _lineCountText, value); }

        /// <summary>The user scrolled up: new lines keep arriving but the view doesn't jump to them.</summary>
        public bool FollowPausedByScroll { get => _followPausedByScroll; set => Set(ref _followPausedByScroll, value); }

        private int _unseenLines;

        /// <summary>Lines that arrived below while the user was scrolled up (shown on the "new lines" button).</summary>
        public int UnseenLines { get => _unseenLines; set => Set(ref _unseenLines, value); }

        public string FilterText { get => _filterText; set => Set(ref _filterText, value ?? ""); }
        public bool FilterIsRegex { get => _filterIsRegex; set => Set(ref _filterIsRegex, value); }

        /// <summary>Why the filter's regular expression is invalid (the filter then matches as plain text), or null.</summary>
        public string? FilterError { get => _filterError; set => Set(ref _filterError, value); }

        // ---- runtime only ----

        /// <summary>Every line kept (up to the cap), filtered or not.</summary>
        [JsonIgnore] public List<LogViewerLine> Captured { get; } = new();

        [JsonIgnore] public int NextLineNumber { get; set; } = 1;

        /// <summary>Set by the view: the follower for this tab (kept as object so the model stays free of the manager).</summary>
        [JsonIgnore] public object? Tail { get; set; }
    }

    /// <summary>Remembered between sessions (log_viewer.json in %AppData%\Smart Tools). Holds no file paths.</summary>
    public sealed class LogViewerSettings
    {
        public bool WordWrap { get; set; }
        public bool ShowLineNumbers { get; set; } = true;
        public int InitialLines { get; set; } = 500;
        public List<LogViewerRule> Rules { get; set; } = new();
    }
}
