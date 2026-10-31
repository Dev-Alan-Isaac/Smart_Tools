using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Password Generator data model. Plain data - no WPF, no I/O.
    //  (Was Controllers/PasswordGeneratorView/PasswordGeneratorModels.cs.)
    //  Every type starts with "PasswordGen" so it can't clash with another
    //  tool's model.
    // =====================================================================

    public enum PasswordGenMode
    {
        RandomCharacters,
        Passphrase,
    }

    /// <summary>Grade from the entropy (see PasswordGeneratorManager.Grade for the bit thresholds).</summary>
    public enum PasswordGenStrength
    {
        VeryWeak,
        Weak,
        Fair,
        Strong,
        VeryStrong,
    }

    /// <summary>What a run of characters is, for colouring the password (letters, digits, symbols, separators).</summary>
    public enum PasswordGenCharKind
    {
        Letter,
        Digit,
        Symbol,
        Separator,
    }

    /// <summary>Every option from the sidebar. Both modes live here so one object describes the whole sidebar.</summary>
    public sealed class PasswordGenOptions
    {
        public PasswordGenMode Mode { get; init; } = PasswordGenMode.RandomCharacters;

        // ---- Random characters ----
        public int Length { get; init; } = 16;
        public bool IncludeUppercase { get; init; } = true;
        public bool IncludeLowercase { get; init; } = true;
        public bool IncludeNumbers { get; init; } = true;
        public bool IncludeSymbols { get; init; } = true;

        /// <summary>Leave out characters that look alike (0 O, 1 l I, and quotes).</summary>
        public bool ExcludeAmbiguous { get; init; }

        /// <summary>At least one of every chosen type (the password is picked evenly among those that qualify).</summary>
        public bool RequireEveryType { get; init; } = true;

        // ---- Passphrase ----
        public int WordCount { get; init; } = 6;

        /// <summary>Between words; "" = no separator (fine - no word in the list is the start of another).</summary>
        public string Separator { get; init; } = "-";

        public bool CapitalizeWords { get; init; } = true;
        public bool AppendNumber { get; init; } = true;

        // ---- Both ----
        public int BatchCount { get; init; } = 5;
    }

    /// <summary>A run of same-kind characters, so digits and symbols can be coloured.</summary>
    public sealed record PasswordGenSegment(string Text, PasswordGenCharKind Kind);

    /// <summary>How strong the current settings are - shown live before anything is generated.</summary>
    public sealed class PasswordGenEstimate
    {
        public double Bits { get; init; }
        public PasswordGenStrength Strength { get; init; }
        public string Label { get; init; } = "";

        /// <summary>0-100 for a meter (128 bits = full).</summary>
        public double Percent { get; init; }

        /// <summary>"88 characters × 16" / "7,776 words × 6 + 2 digits".</summary>
        public string Detail { get; init; } = "";

        /// <summary>"about 3 billion years" at the attack speed in PasswordGeneratorManager.</summary>
        public string CrackTime { get; init; } = "";

        /// <summary>Why nothing can be generated (no character type chosen...), or null.</summary>
        public string? Error { get; init; }
    }

    /// <summary>One generated password in the list.</summary>
    public sealed class PasswordGenItem : INotifyPropertyChanged
    {
        private string _password = "";
        private bool _isVisible;
        private bool _justCopied;
        private PasswordGenEstimate _estimate = new();
        private IReadOnlyList<PasswordGenSegment> _segments = Array.Empty<PasswordGenSegment>();

        public int Number { get; init; }

        public string Password
        {
            get => _password;
            private set
            {
                _password = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MaskedPassword));
                OnPropertyChanged(nameof(LengthLabel));
            }
        }

        public IReadOnlyList<PasswordGenSegment> Segments
        {
            get => _segments;
            private set
            {
                _segments = value;
                OnPropertyChanged();
            }
        }

        public PasswordGenEstimate Estimate
        {
            get => _estimate;
            private set
            {
                _estimate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Strength));
                OnPropertyChanged(nameof(StrengthLabel));
                OnPropertyChanged(nameof(StrengthPercent));
                OnPropertyChanged(nameof(EntropyLabel));
                OnPropertyChanged(nameof(CrackTimeLabel));
            }
        }

        public DateTime GeneratedAt { get; private set; } = DateTime.Now;

        /// <summary>Plain text shown (the eye button); hidden by default.</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MaskedPassword));
            }
        }

        /// <summary>True for a moment after Copy (the copy icon turns into a tick).</summary>
        public bool JustCopied
        {
            get => _justCopied;
            set
            {
                if (_justCopied == value) return;
                _justCopied = value;
                OnPropertyChanged();
            }
        }

        public string MaskedPassword => IsVisible ? Password : new string('•', Math.Min(Password.Length, 40));
        public PasswordGenStrength Strength => Estimate.Strength;
        public string StrengthLabel => Estimate.Label;
        public double StrengthPercent => Estimate.Percent;
        public string EntropyLabel => $"{Estimate.Bits:0} bits";
        public string CrackTimeLabel => Estimate.CrackTime.Length == 0 ? "" : "Cracked in " + Estimate.CrackTime;
        public string LengthLabel => $"{Password.Length} {(Password.Length == 1 ? "character" : "characters")}";

        /// <summary>Swaps in a new password (regenerate one row) - keeps the row and its show/hide state.</summary>
        public void Set(string password, IReadOnlyList<PasswordGenSegment> segments, PasswordGenEstimate estimate)
        {
            Password = password;
            Segments = segments;
            Estimate = estimate;
            GeneratedAt = DateTime.Now;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum PasswordGenExportFormat
    {
        Text,
        Csv,
    }

    /// <summary>
    /// Remembered between sessions (password_generator.json in %AppData%\Smart
    /// Tools). Only how to generate - never a password.
    /// </summary>
    public sealed class PasswordGenSettings
    {
        public PasswordGenMode Mode { get; set; } = PasswordGenMode.RandomCharacters;
        public int Length { get; set; } = 16;
        public bool IncludeUppercase { get; set; } = true;
        public bool IncludeLowercase { get; set; } = true;
        public bool IncludeNumbers { get; set; } = true;
        public bool IncludeSymbols { get; set; } = true;
        public bool ExcludeAmbiguous { get; set; }
        public bool RequireEveryType { get; set; } = true;
        public int WordCount { get; set; } = 6;
        public string Separator { get; set; } = "-";
        public bool CapitalizeWords { get; set; } = true;
        public bool AppendNumber { get; set; } = true;
        public int BatchCount { get; set; } = 5;
        public bool ShowPasswords { get; set; }
    }
}
