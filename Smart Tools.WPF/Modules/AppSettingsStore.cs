using Smart_Tools.WPF.Controllers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Modules
{
    /// <summary>
    /// A file-type group used by Organize Files' "Category" rule
    /// (e.g. Name="Images", Extensions=".jpg,.png,.gif"). Categories are
    /// checked top to bottom and the first match wins.
    /// </summary>
    public class FileCategory : INotifyPropertyChanged
    {
        private string _name = "";
        private string _extensions = "";
        private string _warning = "";

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>Comma-separated, e.g. ".jpg,.jpeg,.png,.gif".</summary>
        public string Extensions
        {
            get => _extensions;
            set
            {
                _extensions = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExtensionList));
                OnPropertyChanged(nameof(ExtensionCount));
            }
        }

        /// <summary>The extensions as chips for the Settings list.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> ExtensionList => SettingsManager.ParseExtensions(_extensions);

        [JsonIgnore]
        public string ExtensionCount => ExtensionList.Count switch
        {
            0 => "no extensions",
            1 => "1 extension",
            int n => $"{n} extensions",
        };

        /// <summary>Set by the Settings view (overlap with another category, or empty).</summary>
        [JsonIgnore]
        public string Warning
        {
            get => _warning;
            set
            {
                if (_warning == value) return;
                _warning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasWarning));
            }
        }

        [JsonIgnore]
        public bool HasWarning => _warning.Length > 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// A named list of values the user keeps for the Mock Data Generator
    /// (e.g. "Departments": Sales, Support, Finance). Picked on a list column.
    /// </summary>
    public class MockDataList : INotifyPropertyChanged
    {
        private string _name = "";
        private string _values = "";
        private string _warning = "";

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>One value per line (commas and semicolons also separate when typed).</summary>
        public string Values
        {
            get => _values;
            set
            {
                _values = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(ValueList));
                OnPropertyChanged(nameof(ValueCount));
            }
        }

        /// <summary>The values as chips for the Settings list.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> ValueList => MockDataManager.SplitList(_values);

        [JsonIgnore]
        public string ValueCount => ValueList.Count switch
        {
            0 => "no values",
            1 => "1 value",
            int n => $"{n} values",
        };

        /// <summary>Set by the Settings view (empty list, bad name).</summary>
        [JsonIgnore]
        public string Warning
        {
            get => _warning;
            set
            {
                if (_warning == value) return;
                _warning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasWarning));
            }
        }

        [JsonIgnore]
        public bool HasWarning => _warning.Length > 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// An event day or holiday for the Date &amp; Time Toolkit (e.g. "Christmas Day", 25 Dec, every year, non-working).
    /// The calendar view names it on its day and lists the next ones; non-working days are skipped when counting
    /// or adding business days.
    /// </summary>
    public class EventDay : INotifyPropertyChanged
    {
        public const string DateFormat = "yyyy-MM-dd";

        private string _name = "";
        private string _date = "";
        private bool _repeats = true;
        private bool _nonWorking = true;
        private string _warning = "";

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>yyyy-MM-dd. For an event that repeats every year only the month and day count.</summary>
        public string Date
        {
            get => _date;
            set
            {
                _date = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(ParsedDate));
            }
        }

        public bool Repeats
        {
            get => _repeats;
            set
            {
                _repeats = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Business-day counts skip this day.</summary>
        public bool NonWorking
        {
            get => _nonWorking;
            set
            {
                _nonWorking = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public DateTime? ParsedDate =>
            DateTime.TryParseExact(_date.Trim(), DateFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

        /// <summary>Set by the Settings view (no name, an unreadable date, a repeated event).</summary>
        [JsonIgnore]
        public string Warning
        {
            get => _warning;
            set
            {
                if (_warning == value) return;
                _warning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasWarning));
            }
        }

        [JsonIgnore]
        public bool HasWarning => _warning.Length > 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A reusable named pattern, offered as a quick pick in File Search.</summary>
    public class SavedSearchPattern : INotifyPropertyChanged
    {
        private string _name = "";
        private string _pattern = "";
        private string _type = SettingsManager.PatternRegex;
        private string _error = "";
        private string _testResult = "";

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? "";
                OnPropertyChanged();
            }
        }

        public string Pattern
        {
            get => _pattern;
            set
            {
                _pattern = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>"Regex" or "Wildcard".</summary>
        public string Type
        {
            get => _type;
            set
            {
                _type = SettingsManager.NormalizePatternType(value);
                OnPropertyChanged();
            }
        }

        /// <summary>Why the pattern doesn't work ("" when it does). Set by the Settings view.</summary>
        [JsonIgnore]
        public string Error
        {
            get => _error;
            set
            {
                if (_error == value) return;
                _error = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        [JsonIgnore]
        public bool HasError => _error.Length > 0;

        /// <summary>"3 matches" against the Settings test box.</summary>
        [JsonIgnore]
        public string TestResult
        {
            get => _testResult;
            set
            {
                if (_testResult == value) return;
                _testResult = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTestResult));
            }
        }

        [JsonIgnore]
        public bool HasTestResult => _testResult.Length > 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// A "if a log line matches this, color it like this" rule used by the
    /// Log Viewer. First match wins, so specific rules go above broad ones.
    /// </summary>
    public class LogHighlightRule : INotifyPropertyChanged
    {
        private string _name = "";
        private string _pattern = "";
        private string _colorHex = "#EF4444";
        private bool _isRegex;
        private bool _caseSensitive;
        private bool _enabled = true;

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? "";
                OnPropertyChanged();
            }
        }

        public string Pattern
        {
            get => _pattern;
            set
            {
                _pattern = value ?? "";
                OnPropertyChanged();
            }
        }

        public string ColorHex
        {
            get => _colorHex;
            set
            {
                _colorHex = value ?? "#EF4444";
                OnPropertyChanged();
            }
        }

        public bool IsRegex
        {
            get => _isRegex;
            set
            {
                _isRegex = value;
                OnPropertyChanged();
            }
        }

        public bool CaseSensitive
        {
            get => _caseSensitive;
            set
            {
                _caseSensitive = value;
                OnPropertyChanged();
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// App-wide settings shared across tools, kept in
    /// %AppData%\Smart Tools\settings.json. Loaded on first use, saved after
    /// each change (and once more on shutdown from App.xaml.cs).
    /// </summary>
    public static class AppSettingsStore
    {
        public static ObservableCollection<FileCategory> FileCategories { get; } = new();
        public static ObservableCollection<SavedSearchPattern> SavedPatterns { get; } = new();
        public static ObservableCollection<LogHighlightRule> LogHighlightRules { get; } = new();
        public static ObservableCollection<MockDataList> MockLists { get; } = new();
        public static ObservableCollection<EventDay> EventDays { get; } = new();

        public static string Theme { get; set; } = SettingsManager.ThemeLight;
        public static string Language { get; set; } = "en";
        public static bool ConfirmBeforeDeleting { get; set; } = true;
        public static bool ShowDesktopNotifications { get; set; } = true;

        // Keyboard shortcuts: only what differs from each action's default is stored, keyed by action id.
        // A value of "" means the user cleared the key.
        public static Dictionary<string, string> Shortcuts { get; private set; } = new();
        public static HashSet<string> DisabledShortcuts { get; private set; } = new();
        public static bool GlobalHotkeysEnabled { get; set; } = true;
        public static bool CloseToTray { get; set; }
        public static bool StartWithWindows { get; set; }

        /// <summary>Clipboard History records copies from startup, even if its tool was never opened.</summary>
        public static bool ClipboardBackgroundRecording { get; set; }

        /// <summary>Clipboard History is saved (encrypted) and put back the next time Smart Tools starts.</summary>
        public static bool ClipboardKeepHistory { get; set; }

        public static string SettingsDirectory { get; } = AppPaths.DataRoot;

        public static string SettingsFilePath { get; } = Path.Combine(SettingsDirectory, "settings.json");

        /// <summary>Set when settings.json couldn't be read and was set aside (shown in Settings).</summary>
        public static string? LoadProblem { get; private set; }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly object SaveLock = new();

        static AppSettingsStore() => Reload();

        /// <summary>Reads settings.json again (defaults when there's none).</summary>
        internal static void Reload()
        {
            LoadProblem = null;
            if (!Load())
            {
                Theme = SettingsManager.ThemeLight;
                Language = "en";
                ConfirmBeforeDeleting = true;
                ShowDesktopNotifications = true;
                Shortcuts = new();
                DisabledShortcuts = new();
                GlobalHotkeysEnabled = true;
                CloseToTray = false;
                StartWithWindows = false;
                ClipboardBackgroundRecording = false;
                ClipboardKeepHistory = false;
                ResetCategories(save: false);
                ResetPatterns(save: false);
                ResetLogRules();
                ResetMockLists(save: false);
                ResetEventDays(save: false);
            }
        }

        /// <summary>
        /// Writes settings.json via a temp file, so a crash mid-save can't
        /// leave a half-written file (the old direct write could, and the
        /// next start then replaced everything with the defaults).
        /// </summary>
        public static void Save()
        {
            try
            {
                lock (SaveLock)
                {
                    var dto = new PersistedSettings
                    {
                        Theme = Theme,
                        Language = Language,
                        ConfirmBeforeDeleting = ConfirmBeforeDeleting,
                        ShowDesktopNotifications = ShowDesktopNotifications,
                        Shortcuts = new Dictionary<string, string>(Shortcuts),
                        DisabledShortcuts = DisabledShortcuts.ToList(),
                        GlobalHotkeysEnabled = GlobalHotkeysEnabled,
                        CloseToTray = CloseToTray,
                        StartWithWindows = StartWithWindows,
                        ClipboardBackgroundRecording = ClipboardBackgroundRecording,
                        ClipboardKeepHistory = ClipboardKeepHistory,
                        FileCategories = FileCategories.ToList(),
                        SavedPatterns = SavedPatterns.ToList(),
                        LogHighlightRules = LogHighlightRules.ToList(),
                        MockLists = MockLists.ToList(),
                        EventDays = EventDays.ToList(),
                    };
                    AtomicFile.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(dto, JsonOptions));
                }
            }
            catch (Exception ex)
            {
                // Best effort: a failed save (locked AppData, full disk) must not crash the app,
                // but it is logged so lost settings leave a trace. AtomicFile removes its own temp file.
                Log.Warn("Couldn't save settings.json", ex);
            }
        }

        private static bool Load()
        {
            if (!File.Exists(SettingsFilePath)) return false;
            try
            {
                var dto = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(SettingsFilePath), JsonOptions)
                          ?? throw new JsonException("The file is empty.");

                Theme = SettingsManager.NormalizeTheme(dto.Theme);
                Language = SettingsManager.NormalizeLanguage(dto.Language);
                // Missing keys (older files) keep the defaults instead of turning into false.
                ConfirmBeforeDeleting = dto.ConfirmBeforeDeleting ?? true;
                ShowDesktopNotifications = dto.ShowDesktopNotifications ?? true;
                Shortcuts = dto.Shortcuts != null ? new Dictionary<string, string>(dto.Shortcuts) : new();
                DisabledShortcuts = dto.DisabledShortcuts != null ? new HashSet<string>(dto.DisabledShortcuts) : new();
                GlobalHotkeysEnabled = dto.GlobalHotkeysEnabled ?? true;
                CloseToTray = dto.CloseToTray ?? false;
                StartWithWindows = dto.StartWithWindows ?? false;
                ClipboardBackgroundRecording = dto.ClipboardBackgroundRecording ?? false;
                ClipboardKeepHistory = dto.ClipboardKeepHistory ?? false;

                FileCategories.Clear();
                foreach (var c in dto.FileCategories ?? DefaultCategories())
                {
                    if (c == null) continue;
                    c.Extensions = SettingsManager.NormalizeExtensions(c.Extensions);
                    FileCategories.Add(c);
                }

                SavedPatterns.Clear();
                foreach (var p in dto.SavedPatterns ?? DefaultPatterns())
                    if (p != null) SavedPatterns.Add(p);

                if (dto.LogHighlightRules != null)
                {
                    LogHighlightRules.Clear();
                    foreach (var r in dto.LogHighlightRules)
                        if (r != null) LogHighlightRules.Add(r);
                }
                else
                {
                    ResetLogRules();
                }

                // Older files have no lists yet: start with the built-in ones (an empty saved list stays empty).
                MockLists.Clear();
                foreach (var l in dto.MockLists ?? DefaultMockLists())
                    if (l != null) MockLists.Add(l);

                EventDays.Clear();
                foreach (var e in dto.EventDays ?? DefaultEventDays())
                    if (e != null) EventDays.Add(e);

                return true;
            }
            catch (Exception ex)
            {
                // Any failure (bad JSON, unreadable file, a malformed entry) must fall back to defaults:
                // this runs in the static constructor, so a leaked exception would kill the app at startup.
                Log.Warn("Couldn't read settings.json", ex);
                // Keep the broken file instead of silently overwriting the
                // user's categories and patterns with the defaults on the next save.
                try
                {
                    string backup = Path.Combine(SettingsDirectory, $"settings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(SettingsFilePath, backup, overwrite: true);
                    LoadProblem = $"settings.json couldn't be read, so defaults were loaded. The old file was kept as {Path.GetFileName(backup)}.";
                }
                catch (Exception)
                {
                    LoadProblem = "settings.json couldn't be read, so defaults were loaded.";
                }
                return false;
            }
        }

        private sealed class PersistedSettings
        {
            public string? Theme { get; set; }
            public string? Language { get; set; }
            public bool? ConfirmBeforeDeleting { get; set; }
            public bool? ShowDesktopNotifications { get; set; }
            public Dictionary<string, string>? Shortcuts { get; set; }
            public List<string>? DisabledShortcuts { get; set; }
            public bool? GlobalHotkeysEnabled { get; set; }
            public bool? CloseToTray { get; set; }
            public bool? StartWithWindows { get; set; }
            public bool? ClipboardBackgroundRecording { get; set; }
            public bool? ClipboardKeepHistory { get; set; }
            public List<FileCategory>? FileCategories { get; set; }
            public List<SavedSearchPattern>? SavedPatterns { get; set; }
            public List<LogHighlightRule>? LogHighlightRules { get; set; }
            public List<MockDataList>? MockLists { get; set; }
            public List<EventDay>? EventDays { get; set; }
        }

        // =================================================================
        //  Defaults
        // =================================================================

        public static List<FileCategory> DefaultCategories() => new()
        {
            new FileCategory { Name = "Documents", Extensions = ".pdf,.doc,.docx,.txt,.rtf,.odt" },
            new FileCategory { Name = "Images", Extensions = ".jpg,.jpeg,.png,.gif,.bmp,.webp,.svg" },
            new FileCategory { Name = "Videos", Extensions = ".mp4,.mkv,.avi,.mov,.webm,.flv" },
            new FileCategory { Name = "Audio", Extensions = ".mp3,.wav,.flac,.aac,.ogg,.m4a" },
            new FileCategory { Name = "Archives", Extensions = ".zip,.rar,.7z,.tar,.gz" },
            new FileCategory { Name = "Other", Extensions = "" },
        };

        public static List<SavedSearchPattern> DefaultPatterns() => new()
        {
            new SavedSearchPattern { Name = "Email Address", Pattern = @"[\w\.-]+@[\w\.-]+\.\w+", Type = "Regex" },
            new SavedSearchPattern { Name = "URL", Pattern = @"(?<scheme>https?)://(?<host>[^\s/:?#]+)(?<rest>[^\s]*)", Type = "Regex" },
            new SavedSearchPattern { Name = "Phone Number", Pattern = @"\+?\d{1,4}?[-.\s]?\(?\d{1,3}?\)?[-.\s]?\d{1,4}[-.\s]?\d{1,4}[-.\s]?\d{1,9}", Type = "Regex" },
            new SavedSearchPattern { Name = "IPv4 Address", Pattern = @"\b(?:\d{1,3}\.){3}\d{1,3}\b", Type = "Regex" },
            new SavedSearchPattern { Name = "IPv6 Address", Pattern = @"\b(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\b", Type = "Regex" },
            new SavedSearchPattern { Name = "Date (YYYY-MM-DD)", Pattern = @"\b(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})\b", Type = "Regex" },
            new SavedSearchPattern { Name = "Windows File Path", Pattern = @"(?<drive>[a-zA-Z]):\\(?<folders>(?:[^\\/:*?""<>|\r\n]+\\)*)(?<name>[^\\/:*?""<>|\r\n]*)", Type = "Regex" },
            new SavedSearchPattern { Name = "MD5 / SHA Hash", Pattern = @"\b[a-fA-F0-9]{32,64}\b", Type = "Regex" },
        };

        public static List<MockDataList> DefaultMockLists() => new()
        {
            new MockDataList { Name = "Departments", Values = "Sales\nSupport\nFinance\nMarketing\nEngineering\nHuman Resources" },
            new MockDataList { Name = "Order status", Values = "pending\nshipped\ndelivered\ncanceled" },
            new MockDataList { Name = "Payment method", Values = "Credit card\nDebit card\nPayPal\nBank transfer\nCash" },
            new MockDataList { Name = "Priority", Values = "Low\nMedium\nHigh\nUrgent" },
        };

        /// <summary>Fixed-date days most countries share; the year is only a placeholder because they repeat every year.</summary>
        public static List<EventDay> DefaultEventDays() => new()
        {
            new EventDay { Name = "New Year's Day", Date = "2025-01-01", Repeats = true, NonWorking = true },
            new EventDay { Name = "Labor Day", Date = "2025-05-01", Repeats = true, NonWorking = true },
            new EventDay { Name = "Christmas Day", Date = "2025-12-25", Repeats = true, NonWorking = true },
        };

        public static void ResetEventDays(bool save = true)
        {
            EventDays.Clear();
            foreach (var e in DefaultEventDays()) EventDays.Add(e);
            if (save) Save();
        }

        public static void ResetMockLists(bool save = true)
        {
            MockLists.Clear();
            foreach (var l in DefaultMockLists()) MockLists.Add(l);
            if (save) Save();
        }

        public static void ResetCategories(bool save = true)
        {
            FileCategories.Clear();
            foreach (var c in DefaultCategories()) FileCategories.Add(c);
            if (save) Save();
        }

        public static void ResetPatterns(bool save = true)
        {
            SavedPatterns.Clear();
            foreach (var p in DefaultPatterns()) SavedPatterns.Add(p);
            if (save) Save();
        }

        private static void ResetLogRules()
        {
            LogHighlightRules.Clear();
            // Most severe first, so "FATAL ... error ..." matches Fatal.
            LogHighlightRules.Add(new LogHighlightRule { Name = "Fatal", Pattern = "FATAL", ColorHex = "#DC2626" });
            LogHighlightRules.Add(new LogHighlightRule { Name = "Error", Pattern = "ERROR", ColorHex = "#F97316" });
            LogHighlightRules.Add(new LogHighlightRule { Name = "Warning", Pattern = "WARN", ColorHex = "#EAB308" });
            LogHighlightRules.Add(new LogHighlightRule { Name = "Info", Pattern = "INFO", ColorHex = "#3B82F6" });
            LogHighlightRules.Add(new LogHighlightRule { Name = "Debug", Pattern = "DEBUG", ColorHex = "#6B7280" });
        }
    }
}
