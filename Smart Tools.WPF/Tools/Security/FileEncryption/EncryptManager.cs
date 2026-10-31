using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// File Encryption - planning only (no disk writes except settings):
    /// output names, which files fit the chosen mode, the grid rows, the
    /// password meter, wording and settings. The file format is in
    /// EncryptManager.Crypto.cs; folders and running a batch are in
    /// EncryptManager.Run.cs.
    /// </summary>
    public sealed partial class EncryptManager
    {
        public const string EncryptedExtension = ".enc";

        /// <summary>Hidden work file written next to the output and renamed when finished.</summary>
        public const string PartExtension = ".stf-part";

        /// <summary>Leftover work files from older versions (never offered as inputs).</summary>
        private static readonly string[] LeftoverMarkers = { PartExtension, ".encrypting", ".decrypting" };

        public static bool IsLeftover(string name) =>
            LeftoverMarkers.Any(m => name.EndsWith(m, StringComparison.OrdinalIgnoreCase));

        // =================================================================
        //  Names
        // =================================================================

        /// <summary>
        /// Encrypt adds ".enc"; decrypt takes it off, or adds ".dec" when an
        /// encrypted file was renamed (so it never lands on its own name).
        /// </summary>
        public static string ComputeOutputName(string originalName, EncryptMode mode)
        {
            if (mode == EncryptMode.Encrypt)
                return originalName + EncryptedExtension;

            if (originalName.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase) && originalName.Length > EncryptedExtension.Length)
                return originalName[..^EncryptedExtension.Length];
            return originalName + ".dec";
        }

        /// <summary>"report.pdf" + 2 = "report (2).pdf".</summary>
        public static string WithNumber(string name, int n)
        {
            string ext = Path.GetExtension(name);
            string stem = name[..^ext.Length];
            if (stem.Length == 0) return $"{name} ({n})";   // ".gitignore"
            return $"{stem} ({n}){ext}";
        }

        /// <summary>
        /// A free output path. The number goes before the real extension, so
        /// encrypting "a.txt" next to an existing "a.txt.enc" gives
        /// "a (2).txt.enc" - which decrypts back to "a (2).txt" (the old
        /// version made "a.txt (2).enc", which decrypted to a file with
        /// no extension).
        /// </summary>
        public static string UniqueOutputPath(string folder, string sourceName, EncryptMode mode, Func<string, bool> exists)
        {
            string name = ComputeOutputName(sourceName, mode);
            string path = Path.Combine(folder, name);
            if (!exists(path)) return path;

            for (int n = 2; n < 100_000; n++)
            {
                string candidate = mode == EncryptMode.Encrypt
                    ? WithNumber(sourceName, n) + EncryptedExtension
                    : WithNumber(name, n);
                path = Path.Combine(folder, candidate);
                if (!exists(path)) return path;
            }
            throw new IOException("Couldn't find a free file name.");
        }

        // =================================================================
        //  The file list
        // =================================================================

        /// <summary>A listed file: where it came from and what its header says.</summary>
        public sealed record QueuedFile(string FullPath, string? Root, long Length, EncryptedFormat Format);

        public sealed class ListResult
        {
            public List<EncryptItem> Items { get; } = new();

            /// <summary>Encrypt mode: files in added folders that are already encrypted.</summary>
            public int LeftOutEncrypted { get; set; }

            /// <summary>Decrypt mode: files in added folders that aren't encrypted.</summary>
            public int LeftOutPlain { get; set; }
        }

        /// <summary>Whether a run will process a row (retries failed and cancelled ones).</summary>
        public static bool IsPending(EncryptItem item) =>
            item.Status is EncryptStatus.Ready or EncryptStatus.Failed or EncryptStatus.Cancelled;

        /// <summary>
        /// Grid rows for the mode. From folders, only files that fit the mode
        /// are listed (the old view re-encrypted .enc files and offered every
        /// file for decryption). Files added one by one are always shown -
        /// as Skipped, with the reason, when they don't fit.
        /// </summary>
        public static ListResult BuildList(IEnumerable<QueuedFile> files, EncryptMode mode, ISet<string>? hidden = null)
        {
            var result = new ListResult();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (!seen.Add(f.FullPath)) continue;
                if (hidden != null && hidden.Contains(f.FullPath)) continue;

                bool encrypted = f.Format != EncryptedFormat.None;
                bool fits = mode == EncryptMode.Encrypt ? !encrypted : encrypted;
                if (!fits && f.Root != null)
                {
                    if (encrypted) result.LeftOutEncrypted++;
                    else result.LeftOutPlain++;
                    continue;
                }

                string name = Path.GetFileName(f.FullPath);
                var item = new EncryptItem
                {
                    FullPath = f.FullPath,
                    Name = name,
                    SizeBytes = f.Length,
                    Format = f.Format,
                    OutputName = fits ? ComputeOutputName(name, mode) : "",
                };
                if (!fits)
                {
                    item.Status = EncryptStatus.Skipped;
                    item.Message = SkipReason(mode);
                }
                result.Items.Add(item);
            }
            return result;
        }

        public static string SkipReason(EncryptMode mode) => mode == EncryptMode.Encrypt
            ? "Already encrypted - switch to Decrypt to open it."
            : "Not a file encrypted by Smart Tools.";

        /// <summary>Sidebar rows: each added folder, then added files grouped by folder.</summary>
        public static List<EncryptQueueGroup> BuildQueueGroups(IEnumerable<EncryptSource> sources, bool includeSubfolders)
        {
            var list = sources.ToList();
            var groups = list.Where(s => s.IsFolder)
                .Select(s => new EncryptQueueGroup { FolderPath = s.FullPath, IsFolder = true, IncludeSubfolders = includeSubfolders })
                .ToList();
            groups.AddRange(list.Where(s => !s.IsFolder)
                .GroupBy(s => Path.GetDirectoryName(s.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new EncryptQueueGroup { FolderPath = g.Key, FilePaths = g.Select(s => s.FullPath).ToList() }));
            return groups;
        }

        // =================================================================
        //  Password meter
        // =================================================================

        private static readonly string[] CommonPasswords =
        {
            "password", "123456", "12345678", "123456789", "qwerty", "abc123", "111111", "iloveyou",
            "letmein", "admin", "welcome", "monkey", "dragon", "football", "passw0rd", "password1",
        };

        /// <summary>
        /// 0-6: length 8/12/16, upper + lower, digit, symbol. Common or
        /// one-character passwords score 1. A hint, not an entropy count.
        /// </summary>
        public static int ScorePasswordStrength(string password)
        {
            if (string.IsNullOrEmpty(password)) return 0;
            if (IsTooCommon(password)) return 1;

            int score = 0;
            if (password.Length >= 8) score++;
            if (password.Length >= 12) score++;
            if (password.Length >= 16) score++;
            if (password.Any(char.IsUpper) && password.Any(char.IsLower)) score++;
            if (password.Any(char.IsDigit)) score++;
            if (password.Any(c => !char.IsLetterOrDigit(c))) score++;
            return Math.Max(1, score);   // anything typed is at least "weak", never "empty"
        }

        private static bool IsTooCommon(string password) =>
            password.Distinct().Count() == 1 || CommonPasswords.Contains(password.ToLowerInvariant());

        public static PasswordRating RatePassword(string password)
        {
            int score = ScorePasswordStrength(password);
            if (score == 0) return new(PasswordStrength.Empty, 0, "Choose a password you'll remember - it can't be recovered.");

            var strength = score switch
            {
                <= 2 => PasswordStrength.Weak,
                3 => PasswordStrength.Fair,
                4 => PasswordStrength.Good,
                _ => PasswordStrength.Strong,
            };

            string hint =
                IsTooCommon(password) ? "Too easy to guess - avoid common words and repeats." :
                password.Length < 12 ? "Use 12 or more characters." :
                !(password.Any(char.IsUpper) && password.Any(char.IsLower)) ? "Mix upper and lower case." :
                !password.Any(char.IsDigit) ? "Add a number." :
                !password.Any(c => !char.IsLetterOrDigit(c)) ? "Add a symbol." :
                password.Length < 16 ? "Longer is even better - try a short sentence." :
                "Strong password. Keep it safe - it can't be recovered.";
            return new(strength, score, hint);
        }

        public static string StrengthLabel(PasswordStrength s) => s switch
        {
            PasswordStrength.Weak => "Weak",
            PasswordStrength.Fair => "Fair",
            PasswordStrength.Good => "Good",
            PasswordStrength.Strong => "Strong",
            _ => "",
        };

        /// <summary>Theme key for the meter colour.</summary>
        public static string StrengthBrushKey(PasswordStrength s) => s switch
        {
            PasswordStrength.Weak => "StatusErrorFg",
            PasswordStrength.Fair => "StatusWarningFg",
            PasswordStrength.Good => "StatusInfoFg",
            PasswordStrength.Strong => "StatusSuccessFg",
            _ => "SidebarInputBorder",
        };

        /// <summary>Why the run can't start yet (null = it can).</summary>
        public static string? PasswordProblem(EncryptMode mode, string password, string confirm)
        {
            if (string.IsNullOrEmpty(password)) return mode == EncryptMode.Encrypt ? "Enter a password first." : "Enter the password the files were encrypted with.";
            if (mode == EncryptMode.Encrypt && password != confirm) return "The two passwords don't match.";
            return null;
        }

        // =================================================================
        //  Wording
        // =================================================================

        /// <summary>"Encrypted 11 of 12 in 0:42 · 3.4 GB · 1 failed".</summary>
        public static string DescribeRun(EncryptRunResult r, EncryptMode mode)
        {
            int total = r.Done + r.Failed + r.Cancelled;
            string verb = mode == EncryptMode.Encrypt ? "Encrypted" : "Decrypted";
            var parts = new List<string> { $"{verb} {r.Done:N0} of {total:N0} in {EncryptText.FormatDuration(r.Elapsed)}" };
            if (r.Bytes > 0) parts.Add(EncryptText.FormatBytes(r.Bytes));
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed");
            if (r.Cancelled > 0) parts.Add($"{r.Cancelled:N0} cancelled");
            return string.Join(" · ", parts);
        }

        public static string DescribeTarget(EncryptMode mode) => mode == EncryptMode.Encrypt
            ? "AES-256 -> .enc"
            : "Decrypt -> original";

        // =================================================================
        //  Settings (never the password)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "file_encryption.json");

        public static EncryptSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new EncryptSettings();
                var s = JsonSerializer.Deserialize<EncryptSettings>(File.ReadAllText(file), JsonOptions) ?? new EncryptSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = EncryptMode.Encrypt;
                return s;
            }
            catch (Exception)
            {
                return new EncryptSettings();
            }
        }

        public static void SaveSettings(EncryptSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
