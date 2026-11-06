using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Notes logic (was Controllers/NotesView/NotesManager.cs): the live list,
    /// loading and saving %AppData%\Smart Tools\notes.json, ordering, search,
    /// export. No WPF here.
    ///
    /// Notes are the one thing in the app the person can't get back, so:
    /// a damaged file is kept aside and the last good copy (notes.json.bak)
    /// is used - never overwritten with an empty list; a file that can't be
    /// read at all pauses saving; every save goes to a temporary file that
    /// replaces the old one in one step and keeps the previous copy as .bak.
    /// </summary>
    public static class NotesManager
    {
        public const string DefaultColorHex = "#FFF59D";

        /// <summary>The swatch row: soft sticky-note colours (dark text stays readable on all of them, in both themes).</summary>
        public static readonly IReadOnlyList<NoteColor> Palette = new[]
        {
            new NoteColor("#FFF59D", "Yellow"),
            new NoteColor("#FFCC80", "Orange"),
            new NoteColor("#EF9A9A", "Pink"),
            new NoteColor("#CE93D8", "Purple"),
            new NoteColor("#90CAF9", "Blue"),
            new NoteColor("#A5D6A7", "Green"),
            new NoteColor("#E0E0E0", "Grey"),
        };

        /// <summary>The live list (pinned first, then most recently edited). The view binds to it.</summary>
        public static ObservableCollection<NoteItem> Notes { get; } = new();

        /// <summary>Where notes.json lives. Changeable for tests only.</summary>
        public static string DataFolder { get; set; } =
            AppPaths.DataRoot;

        public static string NotesFile => Path.Combine(DataFolder, "notes.json");
        public static string BackupFile => NotesFile + ".bak";

        public static bool IsLoaded { get; private set; }

        /// <summary>True when the file couldn't be read - saving is refused so it isn't overwritten.</summary>
        public static bool SavingPaused { get; private set; }

        public static NotesLoadResult? LastLoad { get; private set; }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        // =================================================================
        //  Load
        // =================================================================

        /// <summary>Loads once (the view calls this); later calls return the first result.</summary>
        public static NotesLoadResult EnsureLoaded() => LastLoad ?? Load();

        /// <summary>
        /// Reads notes.json (the old format too). Damaged → tries notes.json.bak,
        /// and the damaged file is kept as notes.damaged-&lt;time&gt;.json. Can't
        /// read (locked, no permission) → empty list and saving paused.
        /// </summary>
        public static NotesLoadResult Load()
        {
            Notes.Clear();
            SavingPaused = false;
            NotesLoadResult result;

            if (Directory.Exists(NotesFile))
            {
                // Something else took the name - don't guess, don't write.
                SavingPaused = true;
                result = new NotesLoadResult { ReadFailed = true, Message = $"\"{NotesFile}\" is a folder, so your notes can't be opened or saved. Rename it and restart Smart Tools." };
            }
            else if (!File.Exists(NotesFile) && !File.Exists(BackupFile))
            {
                result = new NotesLoadResult();
            }
            else
            {
                var (main, mainError) = TryRead(NotesFile);
                if (main != null)
                {
                    Fill(main);
                    result = new NotesLoadResult { Count = main.Count };
                }
                else if (mainError is IOException or UnauthorizedAccessException && File.Exists(NotesFile))
                {
                    // Not damaged - just unreadable right now. Don't touch it, and don't save over it.
                    SavingPaused = true;
                    result = new NotesLoadResult
                    {
                        ReadFailed = true,
                        Message = Loc.F("Str_Notes_MsgOpenFailed", "Your notes couldn't be opened ({0}). Nothing will be saved until Smart Tools is restarted, so they can't be overwritten.", PlainError(mainError)),
                    };
                }
                else
                {
                    string? keptAside = File.Exists(NotesFile) ? KeepAside(NotesFile) : null;
                    var (backup, _) = TryRead(BackupFile);
                    if (backup != null)
                    {
                        Fill(backup);
                        result = new NotesLoadResult
                        {
                            Count = backup.Count,
                            FromBackup = true,
                            KeptAside = keptAside,
                            Message = $"Your notes file was damaged, so the last good copy was loaded ({backup.Count} {(backup.Count == 1 ? "note" : "notes")})." + (keptAside != null ? $" The damaged file was kept as {Path.GetFileName(keptAside)}." : ""),
                        };
                    }
                    else
                    {
                        result = new NotesLoadResult
                        {
                            KeptAside = keptAside,
                            Message = keptAside != null
                                ? Loc.F("Str_Notes_MsgDamaged", "Your notes file was damaged and couldn't be read. It was kept as {0} in {1}, so nothing in it is lost.", Path.GetFileName(keptAside), DataFolder)
                                : null,
                        };
                    }
                }
            }

            IsLoaded = true;
            LastLoad = result;
            return result;
        }

        private static (List<NoteItem>? Notes, Exception? Error) TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return (null, new FileNotFoundException());
                var records = JsonSerializer.Deserialize<List<NoteRecord>>(File.ReadAllText(path), Json);
                if (records == null) return (null, new JsonException("empty"));
                return (records.Where(r => r != null).Select(ToItem).ToList(), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return (null, ex);
            }
        }

        private static NoteItem ToItem(NoteRecord r) => new()
        {
            Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
            CreatedAt = r.CreatedAt == default ? DateTime.Now : r.CreatedAt,
            Title = r.Title ?? "",
            Text = r.Text ?? "",
            ColorHex = IsColor(r.ColorHex) ? r.ColorHex!.ToUpperInvariant() : DefaultColorHex,
            Pinned = r.Pinned,
            ModifiedAt = r.ModifiedAt == default ? (r.CreatedAt == default ? DateTime.Now : r.CreatedAt) : r.ModifiedAt,
        };

        private static NoteRecord ToRecord(NoteItem n) => new()
        {
            Id = n.Id,
            CreatedAt = n.CreatedAt,
            Title = n.Title,
            Text = n.Text,
            ColorHex = n.ColorHex,
            Pinned = n.Pinned,
            ModifiedAt = n.ModifiedAt,
        };

        /// <summary>"#RRGGBB" or "#AARRGGBB".</summary>
        public static bool IsColor(string? hex) =>
            hex != null && hex.Length is 7 or 9 && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit);

        private static void Fill(IEnumerable<NoteItem> notes)
        {
            foreach (var n in notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.ModifiedAt)) Notes.Add(n);
        }

        private static string? KeepAside(string path)
        {
            try
            {
                string target = Path.Combine(Path.GetDirectoryName(path)!, $"notes.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                for (int n = 2; File.Exists(target); n++)
                    target = Path.Combine(Path.GetDirectoryName(path)!, $"notes.damaged-{DateTime.Now:yyyyMMdd-HHmmss}-{n}.json");
                File.Move(path, target);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SavingPaused = true;   // couldn't move it - don't overwrite it either
                return null;
            }
        }

        // =================================================================
        //  Save
        // =================================================================

        /// <summary>
        /// Writes every note. Returns null when saved, or why not (shown in
        /// the editor's status line - the old version failed silently while
        /// the page said "Saved automatically").
        /// </summary>
        public static string? Save()
        {
            if (!IsLoaded) EnsureLoaded();
            if (SavingPaused) return Loc.T("Str_Notes_MsgSavingPausedLong", "Saving is paused because your notes file couldn't be opened. Restart Smart Tools to try again.");

            string temp = NotesFile + ".tmp";
            try
            {
                Directory.CreateDirectory(DataFolder);
                File.WriteAllText(temp, JsonSerializer.Serialize(Notes.Select(ToRecord).ToList(), Json), new UTF8Encoding(false));
                if (File.Exists(NotesFile))
                    File.Replace(temp, NotesFile, BackupFile, ignoreMetadataErrors: true);   // one step, previous copy kept as .bak
                else
                    File.Move(temp, NotesFile);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return Loc.F("Str_Notes_MsgCantSave", "Couldn't save: {0}", PlainError(ex));
            }
        }

        public static string PlainError(Exception? ex) => ex switch
        {
            UnauthorizedAccessException => Loc.T("Str_Notes_ErrDenied", "Windows didn't allow it"),
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => Loc.T("Str_Notes_ErrInUse", "the file is open in another program"),
            IOException io when (io.HResult & 0xFFFF) is 39 or 112 => Loc.T("Str_Notes_ErrDiskFull", "the disk is full"),
            null => Loc.T("Str_Notes_ErrUnknown", "unknown error"),
            _ => ex.Message,
        };

        // =================================================================
        //  Add / delete / pin
        // =================================================================

        /// <summary>
        /// A new note at the top of the unpinned notes. If there's already a
        /// blank one (made with New note and never typed in), that one is
        /// returned instead of piling up empty notes.
        /// </summary>
        public static NoteItem AddNote(string? colorHex = null)
        {
            EnsureLoaded();
            var blank = Notes.FirstOrDefault(n => n.IsBlank);
            if (blank != null) return blank;

            var note = new NoteItem { ColorHex = IsColor(colorHex) ? colorHex! : DefaultColorHex };
            Notes.Insert(FirstUnpinnedIndex(), note);
            return note;
        }

        private static int FirstUnpinnedIndex()
        {
            int i = 0;
            while (i < Notes.Count && Notes[i].Pinned) i++;
            return i;
        }

        /// <summary>Removes the note and returns where it was (for Undo).</summary>
        public static int DeleteNote(NoteItem note)
        {
            int index = Notes.IndexOf(note);
            if (index >= 0) Notes.RemoveAt(index);
            return index;
        }

        /// <summary>Puts a deleted note back (Undo).</summary>
        public static void RestoreNote(NoteItem note, int index)
        {
            if (Notes.Contains(note)) return;
            Notes.Insert(Math.Clamp(index, 0, Notes.Count), note);
            MoveToPlace(note);
        }

        /// <summary>
        /// Moves one note to where the order says (pinned first, then newest)
        /// with Move - the old code cleared and refilled the list, which
        /// deselected the note and reset the editor.
        /// </summary>
        public static void MoveToPlace(NoteItem note)
        {
            int from = Notes.IndexOf(note);
            if (from < 0) return;
            var others = Notes.Where(n => !ReferenceEquals(n, note)).ToList();
            int to = others.TakeWhile(n => Compare(n, note) < 0).Count();
            if (to != from) Notes.Move(from, to);
        }

        /// <summary>Pinned first, then most recently edited.</summary>
        private static int Compare(NoteItem a, NoteItem b)
        {
            int pin = b.Pinned.CompareTo(a.Pinned);
            return pin != 0 ? pin : b.ModifiedAt.CompareTo(a.ModifiedAt);
        }

        public static void SetPinned(NoteItem note, bool pinned)
        {
            note.Pinned = pinned;
            MoveToPlace(note);
        }

        /// <summary>Removes blank notes (New note, never typed in) - when leaving the page or closing.</summary>
        public static int RemoveBlankNotes(NoteItem? keep = null)
        {
            var blanks = Notes.Where(n => n.IsBlank && !ReferenceEquals(n, keep)).ToList();
            foreach (var b in blanks) Notes.Remove(b);
            return blanks.Count;
        }

        // =================================================================
        //  Search, stats, export
        // =================================================================

        /// <summary>Every word typed appears in the title or text (case and accents ignored).</summary>
        public static bool Matches(NoteItem note, string? query)
        {
            var words = Fold(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return true;
            string hay = Fold(note.Title + " " + note.Text);
            return words.All(w => hay.Contains(w, StringComparison.Ordinal));
        }

        public static string Fold(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
            return sb.ToString();
        }

        /// <summary>"120 words · 640 characters".</summary>
        public static string Stats(NoteItem note)
        {
            int words = NoteText.Words(note.Text), chars = note.Text.Length;
            return $"{words:N0} {(words == 1 ? "word" : "words")} · {chars:N0} {(chars == 1 ? "character" : "characters")}";
        }

        /// <summary>A file name from the title ("Shopping list.txt"), Windows-safe.</summary>
        public static string SuggestedFileName(NoteItem note)
        {
            string name = new string(note.DisplayTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '/' or '\\' ? ' ' : c).ToArray()).Trim().TrimEnd('.');
            if (name.Length > 60) name = name[..60].Trim();
            return (name.Length == 0 ? Loc.T("Str_Notes_FileNameFallback", "Note") : name) + ".txt";
        }

        /// <summary>Saves one note as a text file (title, blank line, text), under a temporary name first.</summary>
        public static Task ExportAsync(NoteItem note, string path) =>
            Task.Run(() =>
            {
                string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    string body = note.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    File.WriteAllText(temp, note.DisplayTitle + "\r\n\r\n" + body, new UTF8Encoding(true));
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            });
    }
}
