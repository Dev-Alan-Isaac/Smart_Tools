using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Models
{
    /// <summary>What is wrong with a saved password (Password Vault health audit).</summary>
    public enum VaultHealthKind
    {
        Weak,
        Reused,
        Old,
    }

    /// <summary>Outcome of unlocking (or opening a backup). Wrong password and a tampered file are deliberately the same answer.</summary>
    public enum VaultOpenResult
    {
        Ok,
        WrongPasswordOrTampered,
        NotAVault,
        FileMissing,
    }

    /// <summary>One problem found on one entry.</summary>
    public sealed record VaultHealthIssue(Guid EntryId, VaultHealthKind Kind);

    /// <summary>Header of the vault file. Everything in it is public by design (it is also the AEAD "associated data", so tampering is detected).</summary>
    public sealed record VaultHeader(int MemoryKiB, int Iterations, int Lanes, byte[] Salt);

    /// <summary>A parsed TOTP secret (RFC 6238).</summary>
    public sealed record VaultTotpParams(byte[] Secret, string Algorithm, int Digits, int PeriodSeconds);

    /// <summary>The vault's contents as they are encrypted (JSON inside the ciphertext).</summary>
    public sealed class VaultData
    {
        public int Version { get; set; } = 1;
        public List<VaultEntry> Entries { get; set; } = new();
    }

    /// <summary>Settings of the tool. Saved in plain JSON, so it must never hold anything secret.</summary>
    public sealed class VaultSettings
    {
        public int AutoLockMinutes { get; set; } = 5;
        public bool LockOnMinimize { get; set; } = true;
        public bool LockOnSessionLock { get; set; } = true;
        public int ClipboardSeconds { get; set; } = 30;
        public bool WidgetEnabled { get; set; }
        public double? WidgetLeft { get; set; }
        public double? WidgetTop { get; set; }
    }

    /// <summary>A group header in the entry list (one level of a tag path such as Games/Launchers). Display only, never saved.</summary>
    public sealed class VaultGroupRow : INotifyPropertyChanged
    {
        public const double IndentPerLevel = 16;

        private bool _isExpanded = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; init; } = "";
        /// <summary>Lower-cased path used to remember collapsed groups, for example "games/launchers". Empty = the Ungrouped section.</summary>
        public string PathKey { get; init; } = "";
        public int Depth { get; init; }
        public int Count { get; set; }
        public bool IsGroupHeader => true;
        public double Indent => Depth * IndentPerLevel;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    /// <summary>One saved login.</summary>
    public sealed class VaultEntry : INotifyPropertyChanged
    {
        private string _title = "";
        private string _username = "";
        private string _password = "";
        private string _url = "";
        private string _notes = "";
        private string _tags = "";
        private string _totpSecret = "";
        private bool _favorite;
        private string _healthNote = "";
        private bool _passwordVisible;
        private int _listDepth;
        private int _healthSeverity;

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get => _title; set { if (Set(ref _title, value)) { Raise(nameof(Initial)); } } }
        public string Username { get => _username; set { if (Set(ref _username, value)) Raise(nameof(Subtitle)); } }
        public string Password { get => _password; set { if (Set(ref _password, value)) Raise(nameof(MaskedPassword)); } }
        public string Url { get => _url; set { if (Set(ref _url, value)) Raise(nameof(Subtitle)); } }
        public string Notes { get => _notes; set => Set(ref _notes, value); }
        /// <summary>Comma-separated.</summary>
        public string Tags { get => _tags; set => Set(ref _tags, value); }
        /// <summary>Base32 secret or an otpauth:// URI.</summary>
        public string TotpSecret { get => _totpSecret; set { if (Set(ref _totpSecret, value)) Raise(nameof(HasTotp)); } }
        public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
        public DateTime PasswordChangedUtc { get; set; } = DateTime.UtcNow;

        // ---- display only (never saved) ----

        [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1].ToUpperInvariant();
        [JsonIgnore] public string Subtitle => !string.IsNullOrWhiteSpace(Username) ? Username : Url;
        [JsonIgnore] public bool HasTotp => !string.IsNullOrWhiteSpace(TotpSecret);
        [JsonIgnore] public string MaskedPassword => new('•', Math.Min(Password.Length, 14));
        [JsonIgnore] public string HealthNote { get => _healthNote; set { if (Set(ref _healthNote, value)) Raise(nameof(HasHealthNote)); } }
        [JsonIgnore] public bool HasHealthNote => _healthNote.Length > 0;
        /// <summary>The worst problem, for the badge color: 3 weak, 2 reused, 1 old, 0 none.</summary>
        [JsonIgnore] public int HealthSeverity { get => _healthSeverity; set => Set(ref _healthSeverity, value); }
        [JsonIgnore] public bool PasswordVisible { get => _passwordVisible; set => Set(ref _passwordVisible, value); }

        /// <summary>How deep the entry's tag group is in the list (0 = ungrouped or top level). Set when the list is built.</summary>
        [JsonIgnore] public int ListDepth { get => _listDepth; set { if (Set(ref _listDepth, value)) Raise(nameof(Indent)); } }
        [JsonIgnore] public double Indent => ListDepth * VaultGroupRow.IndentPerLevel;
        [JsonIgnore] public bool IsGroupHeader => false;

        public VaultEntry Clone() => new()
        {
            Id = Id, Title = Title, Username = Username, Password = Password, Url = Url, Notes = Notes, Tags = Tags,
            TotpSecret = TotpSecret, Favorite = Favorite, CreatedUtc = CreatedUtc, ModifiedUtc = ModifiedUtc, PasswordChangedUtc = PasswordChangedUtc,
        };

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
