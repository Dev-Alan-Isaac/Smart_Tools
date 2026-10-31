using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // Hash & Checksum Verifier - plain data only (no WPF types, no I/O beyond reading a path's name).

    public enum HashAlgorithmKind
    {
        Md5,
        Sha1,
        Sha256,
        Sha384,
        Sha512,
        Crc32,
    }

    /// <summary>What the run should do with the computed hashes.</summary>
    public enum HashVerifyMode
    {
        /// <summary>Just compute and show the hashes.</summary>
        Compute,

        /// <summary>Compare every file with one pasted hash.</summary>
        Expected,

        /// <summary>Compare with a checksum list (SHA256SUMS, .md5, .sfv ...): matches, differences, missing and extra files.</summary>
        ChecksumList,
    }

    public enum HashVerdict
    {
        Pending,
        Hashing,
        /// <summary>Hashes computed, nothing to compare with.</summary>
        Computed,
        Match,
        Mismatch,
        /// <summary>Listed in the checksum list but not on disk.</summary>
        Missing,
        /// <summary>On disk but not in the checksum list.</summary>
        Extra,
        Unreadable,
        Cancelled,
    }

    /// <summary>One line of a checksum list. <see cref="Algorithm"/> is null when only the hash length says which one it is.</summary>
    public sealed record HashChecksumEntry(string Hash, string? Path, HashAlgorithmKind? Algorithm);

    public sealed record HashProgress(int Done, int Total, string CurrentFile, long BytesDone, long BytesTotal)
    {
        public double Percent => BytesTotal > 0
            ? Math.Min(100, BytesDone * 100.0 / BytesTotal)
            : Total > 0 ? Done * 100.0 / Total : 0;
    }

    public sealed class HashRunOptions
    {
        public HashSet<HashAlgorithmKind> Algorithms { get; set; } = new() { HashAlgorithmKind.Sha256 };
        public HashVerifyMode Mode { get; set; } = HashVerifyMode.Compute;

        /// <summary>The pasted hash (Mode = Expected).</summary>
        public string Expected { get; set; } = "";

        /// <summary>The parsed list (Mode = ChecksumList).</summary>
        public IReadOnlyList<HashChecksumEntry> Entries { get; set; } = Array.Empty<HashChecksumEntry>();

        /// <summary>Listed paths are relative to this folder (the checksum file's folder unless changed).</summary>
        public string BaseFolder { get; set; } = "";
    }

    public sealed class HashRunResult
    {
        public int Total { get; set; }
        public int Matched { get; set; }
        public int Mismatched { get; set; }
        public int Missing { get; set; }
        public int Extra { get; set; }
        public int Unreadable { get; set; }
        public int Cancelled { get; set; }
        public int Computed { get; set; }
        public TimeSpan Elapsed { get; set; }
        public bool WasCancelled => Cancelled > 0;
    }

    public sealed class HashVerifierSettings
    {
        public List<HashAlgorithmKind> Algorithms { get; set; } = new() { HashAlgorithmKind.Sha256 };
        public HashVerifyMode Mode { get; set; } = HashVerifyMode.Compute;
        public bool IncludeSubfolders { get; set; } = true;
        public bool UpperCase { get; set; }
        public bool OnlyProblems { get; set; }
    }

    /// <summary>A file (or a listed-but-missing path) and what the run found out about it.</summary>
    public sealed class HashFileResult : INotifyPropertyChanged
    {
        private HashVerdict _verdict = HashVerdict.Pending;
        private string _details = "";
        private string _expected = "";
        private bool _upperCase;
        private Dictionary<HashAlgorithmKind, string> _hashes = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public string FullPath { get; init; } = "";
        public string FileName => System.IO.Path.GetFileName(FullPath);
        public string Folder => System.IO.Path.GetDirectoryName(FullPath) ?? "";
        public long SizeBytes { get; set; }
        public string Size { get; set; } = "";

        public HashVerdict Verdict
        {
            get => _verdict;
            set
            {
                if (_verdict == value) return;
                _verdict = value;
                Raise();
                Raise(nameof(VerdictText));
                Raise(nameof(IsProblem));
            }
        }

        public string VerdictText => Verdict switch
        {
            HashVerdict.Pending => "Waiting",
            HashVerdict.Hashing => "Hashing",
            HashVerdict.Computed => "Hashed",
            HashVerdict.Match => "Match",
            HashVerdict.Mismatch => "Different",
            HashVerdict.Missing => "Missing",
            HashVerdict.Extra => "Extra",
            HashVerdict.Unreadable => "Unreadable",
            _ => "Cancelled",
        };

        public string Details
        {
            get => _details;
            set { if (_details != value) { _details = value; Raise(); } }
        }

        /// <summary>The hash this file was compared with (empty when nothing was compared).</summary>
        public string Expected
        {
            get => _expected;
            set { if (_expected != value) { _expected = value; Raise(); } }
        }

        public bool IsProblem => Verdict is HashVerdict.Mismatch or HashVerdict.Missing or HashVerdict.Extra
            or HashVerdict.Unreadable or HashVerdict.Cancelled;

        public bool UpperCase
        {
            get => _upperCase;
            set
            {
                if (_upperCase == value) return;
                _upperCase = value;
                RaiseAllHashes();
            }
        }

        public void SetHashes(Dictionary<HashAlgorithmKind, string> hashes)
        {
            _hashes = hashes;
            RaiseAllHashes();
        }

        public bool TryGetHash(HashAlgorithmKind kind, out string hash) => _hashes.TryGetValue(kind, out hash!);

        /// <summary>The hash as shown (case follows the option).</summary>
        public string GetHash(HashAlgorithmKind kind) =>
            _hashes.TryGetValue(kind, out var h) ? (_upperCase ? h.ToUpperInvariant() : h) : "";

        public string Md5 => GetHash(HashAlgorithmKind.Md5);
        public string Sha1 => GetHash(HashAlgorithmKind.Sha1);
        public string Sha256 => GetHash(HashAlgorithmKind.Sha256);
        public string Sha384 => GetHash(HashAlgorithmKind.Sha384);
        public string Sha512 => GetHash(HashAlgorithmKind.Sha512);
        public string Crc32 => GetHash(HashAlgorithmKind.Crc32);

        /// <summary>One-line hash summary for the grid: the strongest computed hash.</summary>
        public string MainHash =>
            _hashes.Count == 0 ? "" :
            GetHash(_hashes.Keys.OrderByDescending(k => (int)k == (int)HashAlgorithmKind.Crc32 ? -1 : (int)k).First());

        public IReadOnlyDictionary<HashAlgorithmKind, string> Hashes => _hashes;

        /// <summary>The search box: true when every word typed is found in any value of the row (path, size, status, details, expected hash, any computed hash), ignoring case.</summary>
        public bool Matches(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return true;
            var haystack = new List<string> { FullPath, Size, VerdictText, Details, Expected };
            haystack.AddRange(_hashes.Values);
            foreach (string word in term.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!haystack.Any(v => v != null && v.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }

        public void ResetForRun()
        {
            _hashes = new();
            Verdict = HashVerdict.Pending;
            Details = "";
            Expected = "";
            RaiseAllHashes();
        }

        private void RaiseAllHashes()
        {
            Raise(nameof(Md5));
            Raise(nameof(Sha1));
            Raise(nameof(Sha256));
            Raise(nameof(Sha384));
            Raise(nameof(Sha512));
            Raise(nameof(Crc32));
            Raise(nameof(MainHash));
        }

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One row of the Files card: a file or a folder the user added.</summary>
    public sealed class HashQueueGroup
    {
        public string Name { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string ToolTip { get; init; } = "";
        public bool IsFolder { get; init; }
        public string FolderPath { get; init; } = "";
        public List<string> FilePaths { get; init; } = new();
    }

    /// <summary>A path added with Add files / Add folder or dropped.</summary>
    public sealed class HashSource
    {
        public string FullPath { get; init; } = "";
        public bool IsFolder { get; init; }
    }
}
