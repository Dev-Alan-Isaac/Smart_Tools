using Smart_Tools.WPF.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Saving the vault file, backups, CSV import / export and the tool's settings.</summary>
    public sealed partial class PasswordVaultManager
    {
        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "password_vault.json");

        // =================================================================
        //  Vault file (temp file, then replace - keeps the last good copy as .bak)
        // =================================================================

        public bool HasBackupFile => File.Exists(_path + ".bak");

        private byte[] SerializeEntries() => JsonSerializer.SerializeToUtf8Bytes(new VaultData { Entries = _entries });

        private void Save()
        {
            if (_key == null || _header == null) return;
            byte[] plain = SerializeEntries();
            try
            {
                WriteAtomically(_path, Seal(plain, _key, _header), keepBackup: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static void WriteAtomically(string file, byte[] bytes, bool keepBackup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(file))
                {
                    if (keepBackup) File.Replace(temp, file, file + ".bak");
                    else File.Move(temp, file, overwrite: true);
                }
                else
                {
                    File.Move(temp, file);
                }
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }

        /// <summary>Moves an unreadable vault aside (never deleted) and puts the last good .bak copy in its place.</summary>
        public bool RestoreFromBackupFile()
        {
            string bak = _path + ".bak";
            if (!File.Exists(bak)) return false;
            try
            {
                if (File.Exists(_path))
                    File.Move(_path, Path.ChangeExtension(_path, null) + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}.svlt");
                File.Copy(bak, _path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // =================================================================
        //  Encrypted backup (same format, its own password)
        // =================================================================

        public async Task ExportBackupAsync(string path, char[] backupPassword)
        {
            RequireUnlocked();
            var header = NewHeader();
            byte[] key = await Task.Run(() => DeriveKey(backupPassword, header));
            byte[] plain = SerializeEntries();
            try
            {
                WriteAtomically(path, Seal(plain, key, header), keepBackup: false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>Adds the backup's entries that aren't here yet (same Id = skipped). Returns how many were added, or null for a wrong password / bad file.</summary>
        public async Task<int?> ImportBackupAsync(string path, char[] backupPassword)
        {
            RequireUnlocked();
            byte[] file = await File.ReadAllBytesAsync(path);
            var header = ParseHeader(file);
            if (header == null) return null;
            byte[] key = await Task.Run(() => DeriveKey(backupPassword, header));
            byte[] plain;
            try { plain = Open(file, key); }
            catch (CryptographicException) { return null; }
            finally { CryptographicOperations.ZeroMemory(key); }

            var incoming = ReadEntries(plain);
            CryptographicOperations.ZeroMemory(plain);
            if (incoming == null) return null;
            return AddEntries(incoming.Where(i => _entries.All(e => e.Id != i.Id)));
        }

        private int AddEntries(IEnumerable<VaultEntry> entries)
        {
            int added = 0;
            foreach (var e in entries)
            {
                _entries.Add(e);
                added++;
            }
            if (added > 0)
            {
                Save();
                EntriesChanged?.Invoke();
            }
            return added;
        }

        // =================================================================
        //  CSV (plain text - the caller warns the user)
        // =================================================================

        public static string BuildCsv(IEnumerable<VaultEntry> entries)
        {
            var sb = new StringBuilder("name,url,username,password,totp,notes,tags\r\n");
            foreach (var e in entries)
                sb.Append(string.Join(',', new[] { e.Title, e.Url, e.Username, e.Password, e.TotpSecret, e.Notes, e.Tags }.Select(CsvCell))).Append("\r\n");
            return sb.ToString();
        }

        public void ExportCsv(string path) => File.WriteAllText(path, BuildCsv(_entries), new UTF8Encoding(false));

        /// <summary>Imports a CSV from the usual managers/browsers (Bitwarden, Chrome, LastPass, this tool). Returns how many were added.</summary>
        public int ImportCsv(string path)
        {
            RequireUnlocked();
            var rows = ParseCsv(File.ReadAllText(path));
            if (rows.Count < 2) return 0;

            var cols = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            int Col(params string[] names) => cols.FindIndex(c => names.Contains(c));
            int title = Col("name", "title", "login_name"), url = Col("url", "login_uri", "website", "web site", "origin"),
                user = Col("username", "login_username", "user", "login"), pass = Col("password", "login_password"),
                totp = Col("totp", "login_totp", "otpauth"), notes = Col("notes", "extra", "note"),
                tags = Col("tags", "folder", "grouping", "group");

            // Undo the Excel-formula guard added by CsvCell.
            static string Unguard(string s) => s.Length > 1 && s[0] == '\'' && s[1] is '=' or '+' or '-' or '@' ? s[1..] : s;
            string Cell(List<string> row, int i) => i >= 0 && i < row.Count ? Unguard(row[i].Trim()) : "";

            var fresh = new List<VaultEntry>();
            foreach (var row in rows.Skip(1))
            {
                var e = new VaultEntry
                {
                    Title = Cell(row, title), Url = Cell(row, url), Username = Cell(row, user), Password = pass >= 0 && pass < row.Count ? Unguard(row[pass]) : "",
                    TotpSecret = Cell(row, totp), Notes = Cell(row, notes), Tags = Cell(row, tags),
                };
                if (e.Password.Length == 0 && e.Username.Length == 0) continue;
                if (e.Title.Length == 0) e.Title = e.Url.Length > 0 ? e.Url : e.Username;
                bool dup = _entries.Concat(fresh).Any(x => x.Title == e.Title && x.Username == e.Username && x.Url == e.Url && x.Password == e.Password);
                if (!dup) fresh.Add(e);
            }
            return AddEntries(fresh);
        }

        /// <summary>
        /// Quotes a cell, and prefixes a leading = + - @ with an apostrophe so Excel never runs it as a formula
        /// (the apostrophe is stripped again on import).
        /// </summary>
        public static string CsvCell(string value)
        {
            if (value.Length > 0 && value[0] is '=' or '+' or '@' || (value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1])))
                value = "'" + value;
            bool quote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        public static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') { quoted = true; any = true; }
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); any = true; }
                else if (c is '\r' or '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
                    row = new List<string>();
                    cell.Clear();
                    any = false;
                }
                else { cell.Append(c); any = true; }
            }
            if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\password_vault.json - never a secret)
        // =================================================================

        /// <summary>Auto-lock choices in minutes (0 = never), shared by the vault view and the Settings page.</summary>
        public static readonly int[] AutoLockChoices = { 0, 1, 2, 5, 10, 15, 30, 60 };

        /// <summary>How long a copied secret stays on the clipboard, in seconds.</summary>
        public static readonly int[] ClipboardChoices = { 10, 20, 30, 60, 120 };

        /// <summary>Raised after the default settings file was written, so an open vault view can re-read it.</summary>
        public static event Action? SettingsSaved;

        public static VaultSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new VaultSettings();
                var s = JsonSerializer.Deserialize<VaultSettings>(File.ReadAllText(file), SettingsJson) ?? new VaultSettings();
                s.AutoLockMinutes = Math.Clamp(s.AutoLockMinutes, 0, 120);
                s.ClipboardSeconds = Math.Clamp(s.ClipboardSeconds, 5, 300);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new VaultSettings();
            }
        }

        public static void SaveSettings(VaultSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJson));
                File.Move(temp, file, overwrite: true);
                if (path == null) SettingsSaved?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        public static string PlainError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows didn't allow access to that file.",
            IOException => "The file couldn't be read or written: " + ex.Message,
            _ => ex.Message,
        };
    }
}
