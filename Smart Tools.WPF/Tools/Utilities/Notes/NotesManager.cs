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

        /// <summary>The pictures pasted or added into notes (one PNG each); the note text refers to them by file name.</summary>
        public static string ImagesFolder => Path.Combine(DataFolder, "note-images");

        public static bool IsLoaded { get; private set; }

        /// <summary>A note another window (a pinned widget) wants the Notes page to show.</summary>
        public static Guid? PendingSelect { get; set; }

        /// <summary>Raised after <see cref="RequestSelect"/>, for a Notes page that is already open.</summary>
        public static event Action? SelectRequested;

        public static void RequestSelect(Guid id)
        {
            PendingSelect = id;
            SelectRequested?.Invoke();
        }

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

        private static NoteItem ToItem(NoteRecord r)
        {
            // A protected note comes in locked: only the title is readable until the password is given.
            bool locked = r.Locked && !string.IsNullOrEmpty(r.Sealed) && !string.IsNullOrEmpty(r.Salt);
            byte[]? salt = null;
            if (locked)
            {
                try { salt = Convert.FromBase64String(r.Salt!); }
                catch (FormatException) { locked = false; }
            }
            return new NoteItem
            {
                Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
                CreatedAt = r.CreatedAt == default ? DateTime.Now : r.CreatedAt,
                Title = r.Title ?? "",
                Text = locked ? "" : r.Text ?? "",
                Rich = locked ? "" : r.Rich ?? "",
                Source = locked ? "" : r.Source ?? "",
                ColorHex = IsColor(r.ColorHex) ? r.ColorHex!.ToUpperInvariant() : DefaultColorHex,
                Pinned = r.Pinned,
                ModifiedAt = r.ModifiedAt == default ? (r.CreatedAt == default ? DateTime.Now : r.CreatedAt) : r.ModifiedAt,
                IsProtected = locked,
                SealedData = locked ? r.Sealed : null,
                LockSalt = salt,
            };
        }

        private static NoteRecord ToRecord(NoteItem n)
        {
            var record = new NoteRecord
            {
                Id = n.Id,
                CreatedAt = n.CreatedAt,
                Title = n.Title,
                ColorHex = n.ColorHex,
                Pinned = n.Pinned,
                ModifiedAt = n.ModifiedAt,
            };
            if (n.IsProtected && n.LockSalt != null)
            {
                // Nothing readable goes to disk: the content is written encrypted, from the session key while unlocked, unchanged while locked.
                if (n.IsUnlocked && n.SessionKey != null)
                    n.SealedData = NoteLock.Encrypt(new NotePayload { Text = n.Text, Rich = n.Rich, Source = n.Source }, n.SessionKey);
                record.Locked = true;
                record.Salt = Convert.ToBase64String(n.LockSalt);
                record.Sealed = n.SealedData;
                return record;
            }
            record.Text = n.Text;
            record.Rich = string.IsNullOrEmpty(n.Rich) ? null : n.Rich;
            record.Source = string.IsNullOrEmpty(n.Source) ? null : n.Source;
            return record;
        }

        // =================================================================
        //  Password lock (per note; the crypto is NoteLock)
        // =================================================================

        public enum LockResult { Ok, HasPictures, SaveFailed }

        /// <summary>Gives the note a password (or a new one). The note stays open; the old password is not needed here, the view asks for it first.</summary>
        public static LockResult SetPassword(NoteItem note, ReadOnlySpan<char> password)
        {
            if (note.IsLocked) return LockResult.SaveFailed;
            if (note.HasPicture) return LockResult.HasPictures;   // pictures are separate files and would stay readable

            byte[] salt = NoteLock.NewSalt();
            byte[] key = NoteLock.DeriveKey(password, salt);
            if (note.SessionKey != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(note.SessionKey);
            note.LockSalt = salt;
            note.SessionKey = key;
            note.IsProtected = true;
            note.IsUnlocked = true;
            return SaveAndForgetPlainBackup();
        }

        /// <summary>Takes the password off an unlocked note: the content is stored plainly again.</summary>
        public static LockResult RemovePassword(NoteItem note)
        {
            if (!note.IsProtected || note.IsLocked) return LockResult.SaveFailed;
            WipeKey(note);
            note.IsProtected = false;
            note.IsUnlocked = false;
            note.SealedData = null;
            note.LockSalt = null;
            return SaveAndForgetPlainBackup();
        }

        /// <summary>True and the note's content is back when the password is right.</summary>
        public static bool TryUnlock(NoteItem note, ReadOnlySpan<char> password)
        {
            if (!note.IsLocked || note.LockSalt == null) return false;
            byte[] key = NoteLock.DeriveKey(password, note.LockSalt);
            var payload = NoteLock.TryDecrypt(note.SealedData, key);
            if (payload == null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                return false;
            }
            note.SessionKey = key;
            note.Text = payload.Text;
            note.Rich = payload.Rich;
            note.Source = payload.Source;
            note.IsUnlocked = true;
            return true;
        }

        /// <summary>Hides the content again and forgets the key (the encrypted copy is brought up to date first).</summary>
        public static void LockNow(NoteItem note)
        {
            if (!note.IsProtected || note.IsLocked) return;
            Smart_Tools.WPF.Views.NoteWidgetManager.Close(note);   // a widget would keep showing the text, and its edits would be lost
            if (note.SessionKey != null && note.LockSalt != null)
                note.SealedData = NoteLock.Encrypt(new NotePayload { Text = note.Text, Rich = note.Rich, Source = note.Source }, note.SessionKey);
            WipeKey(note);
            note.IsUnlocked = false;
            note.Text = "";
            note.Rich = "";
            note.Source = "";
        }

        /// <summary>Locks every unlocked note (leaving the page, closing the app).</summary>
        public static void LockAll(bool keepWidgets = false)
        {
            // Leaving the page keeps a note unlocked while its desktop widget is open; closing the app locks everything.
            foreach (var note in Notes.Where(n => n.IsProtected && !n.IsLocked && !(keepWidgets && Smart_Tools.WPF.Views.NoteWidgetManager.IsOpen(n))).ToList()) LockNow(note);
        }

        private static void WipeKey(NoteItem note)
        {
            if (note.SessionKey != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(note.SessionKey);
            note.SessionKey = null;
        }

        /// <summary>
        /// Saves, then deletes notes.json.bak (it still holds the version from before the change, which may be readable) and
        /// saves again so the backup is the new, protected version.
        /// </summary>
        private static LockResult SaveAndForgetPlainBackup()
        {
            if (Save() != null) return LockResult.SaveFailed;
            try { if (File.Exists(BackupFile)) File.Delete(BackupFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return LockResult.SaveFailed; }
            return Save() == null ? LockResult.Ok : LockResult.SaveFailed;
        }

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
            string hay = Fold(note.IsLocked ? note.Title : note.Title + " " + note.Text + " " + note.Source);   // a locked note is found by its title only
            return words.All(w => hay.Contains(w, StringComparison.Ordinal));
        }

        /// <summary>Same folding as <see cref="Fold"/> but exactly one character per character, so positions in the result are positions in <paramref name="text"/>.</summary>
        private static string FoldSameLength(string text)
        {
            var chars = new char[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char r = ' ';
                if (c < 128) r = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ';
                else if (char.IsLetterOrDigit(c))
                {
                    char b = c.ToString().Normalize(NormalizationForm.FormD)[0];   // "é" → "e"
                    r = char.IsLetterOrDigit(b) ? char.ToLowerInvariant(b) : ' ';
                }
                chars[i] = r;
            }
            return new string(chars);
        }

        /// <summary>Where the search words are in a text (case and accents ignored), sorted and without overlaps.</summary>
        public static List<(int Start, int Length)> FindMatches(string? text, string? query)
        {
            var found = new List<(int Start, int Length)>();
            var words = Fold(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            if (words.Length == 0 || string.IsNullOrEmpty(text)) return found;

            string folded = FoldSameLength(text);
            foreach (string word in words)
            {
                for (int at = folded.IndexOf(word, StringComparison.Ordinal); at >= 0; at = folded.IndexOf(word, at + word.Length, StringComparison.Ordinal))
                    found.Add((at, word.Length));
            }
            found.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<(int Start, int Length)>();
            foreach (var m in found)
            {
                if (merged.Count > 0 && m.Start <= merged[^1].Start + merged[^1].Length)
                {
                    var last = merged[^1];
                    merged[^1] = (last.Start, Math.Max(last.Length, m.Start + m.Length - last.Start));
                }
                else merged.Add(m);
            }
            return merged;
        }

        /// <summary>
        /// The passage of a note's text to show on its card while searching: the start of the text when the word is near it,
        /// otherwise a piece around the first match ("…" at the cut ends). Null when the text doesn't contain the words.
        /// </summary>
        public static string? ContextSnippet(string text, string? query)
        {
            string flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            var hits = FindMatches(flat, query);
            if (hits.Count == 0) return null;

            int start = Math.Max(0, hits[0].Start - 40);
            if (start > 0)
            {
                int space = flat.IndexOf(' ', start);   // begin at a word
                if (space >= 0 && space < hits[0].Start) start = space + 1;
            }
            string piece = flat.Substring(start, Math.Min(140, flat.Length - start));
            return (start > 0 ? "…" : "") + piece + (start + piece.Length < flat.Length ? "…" : "");
        }

        /// <summary>Tells every note what is being searched (cards show the matching passage and highlight the words).</summary>
        public static void ApplyQuery(string? query)
        {
            string q = (query ?? "").Trim();
            foreach (var note in Notes) note.Query = q;
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

        /// <summary>The rich text as Markdown: **bold**, *italic*, ~~strikethrough~~, "- " list items. Pictures become a short [picture] mark.</summary>
        public static string ToMarkdown(string rich)
        {
            System.Xml.Linq.XElement root;
            try
            {
                root = System.Xml.Linq.XElement.Parse("<n>" + rich + "</n>", System.Xml.Linq.LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException)
            {
                return rich;
            }

            var lines = new List<string>();
            foreach (var block in root.Elements())
            {
                if (block.Name.LocalName == "ul") lines.AddRange(block.Elements("li").Select(li => "- " + Inline(li)));
                else lines.Add(Inline(block));
            }
            return string.Join("\n", lines);

            static string Inline(System.Xml.Linq.XElement parent)
            {
                var sb = new StringBuilder();
                foreach (var node in parent.Nodes())
                {
                    if (node is System.Xml.Linq.XText t) { sb.Append(t.Value); continue; }
                    if (node is not System.Xml.Linq.XElement e) continue;
                    string inner = e.Name.LocalName == "img" ? "" : Inline(e);
                    sb.Append(e.Name.LocalName switch
                    {
                        "b" => "**" + inner + "**",
                        "i" => "*" + inner + "*",
                        "s" => "~~" + inner + "~~",
                        "u" => "<u>" + inner + "</u>",
                        "br" => "  \n",
                        "img" => "[" + Loc.T("Str_Notes_ExportPicture", "picture") + "]",
                        _ => inner,
                    });
                }
                return sb.ToString();
            }
        }

        /// <summary>Saves one note as a text file (title, blank line, text), under a temporary name first.</summary>
        public static Task ExportAsync(NoteItem note, string path) =>
            Task.Run(() =>
            {
                string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    bool markdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                    string body = (markdown && note.Rich.Length > 0 ? ToMarkdown(note.Rich) : note.Text).Replace("\r\n", "\n").Replace("\n", "\r\n");
                    string source = note.Source.Length > 0 ? "\r\n\r\n" + Loc.F("Str_Notes_ExportSource", "Source: {0}", note.Source) : "";
                    File.WriteAllText(temp, (markdown ? "# " : "") + note.DisplayTitle + "\r\n\r\n" + body + source, new UTF8Encoding(true));
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            });
    }
}
