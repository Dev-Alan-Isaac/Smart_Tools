using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Notes data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/NotesView/NoteItem.cs.) NoteItem is what the list
    //  and editor bind to; NoteRecord is what goes into notes.json (only
    //  the real fields - the old file also stored Snippet and ModifiedLabel).
    // =====================================================================

    /// <summary>One sticky note, as the list and editor see it.</summary>
    public sealed class NoteItem : INotifyPropertyChanged
    {
        public const string UntitledLabel = "Untitled note";

        private string _title = "";
        private string _text = "";
        private string _colorHex = "#FFF59D";
        private bool _pinned;
        private DateTime _modifiedAt = DateTime.Now;

        public Guid Id { get; init; } = Guid.NewGuid();
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>As typed - may be empty (the list then shows "Untitled note").</summary>
        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                _title = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(Snippet));
            }
        }

        /// <summary>Sticky-note colour, e.g. "#FFF59D".</summary>
        public string ColorHex
        {
            get => _colorHex;
            set
            {
                if (_colorHex == value) return;
                _colorHex = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Pinned notes stay at the top.</summary>
        public bool Pinned
        {
            get => _pinned;
            set
            {
                if (_pinned == value) return;
                _pinned = value;
                OnPropertyChanged();
            }
        }

        public DateTime ModifiedAt
        {
            get => _modifiedAt;
            set
            {
                _modifiedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModifiedLabel));
            }
        }

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Loc.T("Str_Notes_Untitled", UntitledLabel) : Title.Trim();

        /// <summary>The first two non-empty lines of the text (for the card).</summary>
        public string Snippet
        {
            get
            {
                var lines = Text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2);
                string s = string.Join(" · ", lines);
                return s.Length > 140 ? s[..140] + "…" : s;
            }
        }

        /// <summary>"Edited just now" / "Edited 5 min ago" / "Edited yesterday" ... (refreshed by the view every minute).</summary>
        public string ModifiedLabel => "Edited " + NoteText.Ago(ModifiedAt, DateTime.Now);

        /// <summary>Nothing typed yet (a fresh "New note" - reused instead of piling up empty notes).</summary>
        public bool IsBlank => (string.IsNullOrWhiteSpace(Title) || Title.Trim() == UntitledLabel) && string.IsNullOrWhiteSpace(Text);

        /// <summary>Re-reads the clock for <see cref="ModifiedLabel"/>.</summary>
        public void RefreshTimeLabel() => OnPropertyChanged(nameof(ModifiedLabel));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One note in notes.json. Same field names as the old file, so old notes load as they are.</summary>
    public sealed class NoteRecord
    {
        public Guid Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Title { get; set; }
        public string? Text { get; set; }
        public string? ColorHex { get; set; }
        public bool Pinned { get; set; }
        public DateTime ModifiedAt { get; set; }
    }

    /// <summary>What happened when the notes were loaded.</summary>
    public sealed class NotesLoadResult
    {
        public int Count { get; init; }

        /// <summary>Loaded from notes.json.bak because notes.json was damaged.</summary>
        public bool FromBackup { get; init; }

        /// <summary>A damaged file kept aside (never overwritten), or null.</summary>
        public string? KeptAside { get; init; }

        /// <summary>The file couldn't be read at all (in use, no permission) - saving is paused so nothing is overwritten.</summary>
        public bool ReadFailed { get; init; }

        /// <summary>Something the person should know, or null.</summary>
        public string? Message { get; init; }
    }

    /// <summary>A colour in the swatch row.</summary>
    public sealed record NoteColor(string Hex, string Name);

    public static class NoteText
    {
        /// <summary>"just now", "5 min ago", "2 h ago", "yesterday", "Mon", "Mar 3", "Mar 3, 2024".</summary>
        public static string Ago(DateTime when, DateTime now)
        {
            var span = now - when;
            if (span < TimeSpan.FromMinutes(1) && span > TimeSpan.FromMinutes(-1)) return "just now";
            if (span < TimeSpan.Zero) return when.ToString("MMM d", CultureInfo.CurrentCulture);
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (when.Date == now.Date) return $"{(int)span.TotalHours} h ago";
            if (when.Date == now.Date.AddDays(-1)) return "yesterday";
            if (span.TotalDays < 7) return when.ToString("ddd", CultureInfo.CurrentCulture);
            return when.Year == now.Year
                ? when.ToString("MMM d", CultureInfo.CurrentCulture)
                : when.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }

        public static int Words(string text) =>
            (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
