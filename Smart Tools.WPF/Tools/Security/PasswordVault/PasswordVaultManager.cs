using Smart_Tools.WPF.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Password Vault logic: create / unlock / lock the encrypted vault, the entries, health, TOTP, files.
    /// No WPF controls. Created on the UI thread; async methods resume there. While unlocked it holds the
    /// derived key and the decrypted entries in memory; <see cref="Lock"/> wipes the key and drops the entries.
    /// The master password is never stored - callers pass a char[] and wipe it afterwards.
    /// </summary>
    public sealed partial class PasswordVaultManager : IDisposable
    {
        private readonly string _path;
        private readonly List<VaultEntry> _entries = new();
        private byte[]? _key;
        private VaultHeader? _header;
        private int _failedAttempts;
        private bool _disposed;

        /// <summary>The vault was unlocked or created.</summary>
        public event Action? Unlocked;

        /// <summary>The vault was locked (by the user, a timer, or Windows).</summary>
        public event Action? Locked;

        /// <summary>Entries were added, changed or removed.</summary>
        public event Action? EntriesChanged;

        public PasswordVaultManager(string? path = null)
        {
            _path = path ?? DefaultVaultPath;
        }

        public static string DefaultVaultPath => Path.Combine(AppPaths.DataRoot, "vault.svlt");

        public bool VaultExists => File.Exists(_path);
        public bool IsUnlocked => _key != null;
        public IReadOnlyList<VaultEntry> Entries => _entries;

        /// <summary>How long the next unlock attempt is delayed after wrong passwords (0 at first, then 0.25 s doubling up to 8 s).</summary>
        public TimeSpan NextAttemptDelay => _failedAttempts == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(8000, 250 * Math.Pow(2, _failedAttempts - 1)));

        // =================================================================
        //  Create / unlock / lock
        // =================================================================

        /// <summary>Makes a new, empty vault protected by <paramref name="password"/> (replaces nothing: fails if one exists).</summary>
        public async Task CreateAsync(char[] password)
        {
            if (VaultExists) throw new InvalidOperationException("A vault already exists.");
            var header = NewHeader();
            byte[] key = await Task.Run(() => DeriveKey(password, header));
            SetUnlocked(key, header, new List<VaultEntry>());
            Save();
            Unlocked?.Invoke();
        }

        public async Task<VaultOpenResult> UnlockAsync(char[] password)
        {
            if (!VaultExists) return VaultOpenResult.FileMissing;
            byte[] file;
            try { file = await File.ReadAllBytesAsync(_path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VaultOpenResult.NotAVault; }

            var header = ParseHeader(file);
            if (header == null) return VaultOpenResult.NotAVault;

            var delay = NextAttemptDelay;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);

            byte[] key = await Task.Run(() => DeriveKey(password, header));
            byte[] plain;
            try
            {
                plain = Open(file, key);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(key);
                _failedAttempts++;
                return VaultOpenResult.WrongPasswordOrTampered;
            }

            List<VaultEntry>? entries = ReadEntries(plain);
            CryptographicOperations.ZeroMemory(plain);
            if (entries == null)
            {
                CryptographicOperations.ZeroMemory(key);
                return VaultOpenResult.NotAVault;
            }

            _failedAttempts = 0;
            SetUnlocked(key, header, entries);
            Unlocked?.Invoke();
            return VaultOpenResult.Ok;
        }

        /// <summary>Wipes the key and forgets the entries. Safe to call when already locked.</summary>
        public void Lock()
        {
            if (_key == null) return;
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _header = null;
            foreach (var e in _entries)
            {
                e.Password = "";
                e.TotpSecret = "";
                e.Notes = "";
            }
            _entries.Clear();
            Locked?.Invoke();
            EntriesChanged?.Invoke();
        }

        /// <summary>Re-encrypts the vault under a new master password (and a new salt). False if <paramref name="current"/> is wrong.</summary>
        public async Task<bool> ChangeMasterPasswordAsync(char[] current, char[] replacement)
        {
            if (_key == null || _header == null) return false;
            var oldHeader = _header;
            byte[] check = await Task.Run(() => DeriveKey(current, oldHeader));
            bool ok = CryptographicOperations.FixedTimeEquals(check, _key);
            CryptographicOperations.ZeroMemory(check);
            if (!ok) return false;

            var header = NewHeader();
            byte[] key = await Task.Run(() => DeriveKey(replacement, header));
            CryptographicOperations.ZeroMemory(_key);
            _key = key;
            _header = header;
            Save();
            // The .bak is the previous save, still locked with the OLD password - drop it, so the old password stops opening anything.
            try { File.Delete(_path + ".bak"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }

        private void SetUnlocked(byte[] key, VaultHeader header, List<VaultEntry> entries)
        {
            _key = key;
            _header = header;
            _entries.Clear();
            _entries.AddRange(entries);
        }

        private static List<VaultEntry>? ReadEntries(byte[] plainJson)
        {
            try { return JsonSerializer.Deserialize<VaultData>(plainJson)?.Entries; }
            catch (JsonException) { return null; }
        }

        // =================================================================
        //  Entries
        // =================================================================

        public VaultEntry NewEntry() => new();

        /// <summary>Adds the entry, or replaces the one with the same Id.</summary>
        public void Upsert(VaultEntry entry)
        {
            RequireUnlocked();
            int i = _entries.FindIndex(e => e.Id == entry.Id);
            var now = DateTime.UtcNow;
            entry.ModifiedUtc = now;
            if (i < 0)
            {
                _entries.Add(entry);
            }
            else
            {
                if (_entries[i].Password != entry.Password) entry.PasswordChangedUtc = now;
                _entries[i] = entry;
            }
            Save();
            EntriesChanged?.Invoke();
        }

        public bool Remove(Guid id)
        {
            RequireUnlocked();
            int i = _entries.FindIndex(e => e.Id == id);
            if (i < 0) return false;
            _entries.RemoveAt(i);
            Save();
            EntriesChanged?.Invoke();
            return true;
        }

        public IEnumerable<VaultEntry> Search(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return _entries;
            string q = text.Trim();
            return _entries.Where(e =>
                e.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                e.Username.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                e.Url.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                e.Tags.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                e.Notes.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        }

        public List<VaultHealthIssue> Health() => AnalyzeHealth(_entries);

        private void RequireUnlocked()
        {
            if (_key == null) throw new InvalidOperationException("The vault is locked.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Lock();
        }
    }
}
