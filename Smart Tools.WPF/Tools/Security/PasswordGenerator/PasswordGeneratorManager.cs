using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Password Generator logic (was Engines/PasswordGeneratorManager.cs).
    /// All randomness comes from <see cref="RandomNumberGenerator"/> (a
    /// cryptographic generator) - never System.Random. No WPF here; the view
    /// only shows what this returns. The word list is in
    /// PasswordGeneratorManager.Words.cs.
    /// </summary>
    public static partial class PasswordGeneratorManager
    {
        public const string UppercaseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        public const string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
        public const string NumberChars = "0123456789";
        public const string SymbolChars = "!@#$%^&*()-_=+[]{};:,.<>/?";

        /// <summary>Look-alikes left out by "Avoid look-alike characters".</summary>
        public const string AmbiguousChars = "0O1lI|`'\"";

        public const int MinLength = 4, MaxLength = 128;
        public const int MinWords = 3, MaxWords = 12;
        public const int MaxBatch = 50;

        /// <summary>The separators the sidebar offers ("" = none).</summary>
        public static readonly IReadOnlyList<string> Separators = new[] { "-", ".", "_", " ", "" };

        /// <summary>
        /// Attack speed behind "cracked in": 100 billion guesses a second - a
        /// fast offline attack on a leaked, weakly hashed password list. A
        /// website that limits login attempts is far slower.
        /// </summary>
        public const double GuessesPerSecond = 1e11;

        public static int WordListSize => WordList.Length;

        /// <summary>For tests: the passphrase word list (read-only).</summary>
        public static IReadOnlyList<string> Words => WordList;

        // =================================================================
        //  Checking and estimating (live, before anything is generated)
        // =================================================================

        /// <summary>The character groups switched on (look-alikes removed when asked), empty groups dropped.</summary>
        public static List<string> ActivePools(PasswordGenOptions o)
        {
            var pools = new List<string>();
            if (o.IncludeUppercase) pools.Add(Strip(UppercaseChars, o));
            if (o.IncludeLowercase) pools.Add(Strip(LowercaseChars, o));
            if (o.IncludeNumbers) pools.Add(Strip(NumberChars, o));
            if (o.IncludeSymbols) pools.Add(Strip(SymbolChars, o));
            return pools.Where(p => p.Length > 0).ToList();
        }

        private static string Strip(string pool, PasswordGenOptions o) =>
            o.ExcludeAmbiguous ? new string(pool.Where(c => AmbiguousChars.IndexOf(c) < 0).ToArray()) : pool;

        public static int ClampLength(int length) => Math.Clamp(length, MinLength, MaxLength);
        public static int ClampWords(int words) => Math.Clamp(words, MinWords, MaxWords);
        public static int ClampBatch(int count) => Math.Clamp(count, 1, MaxBatch);

        /// <summary>Why nothing can be generated with these options, or null.</summary>
        public static string? Validate(PasswordGenOptions o)
        {
            if (o.Mode == PasswordGenMode.RandomCharacters && ActivePools(o).Count == 0)
                return "Choose at least one character type.";
            if (o.Mode == PasswordGenMode.Passphrase && (o.Separator ?? "").Any(char.IsLetter))
                return "The separator can't contain letters.";
            return null;
        }

        /// <summary>Exactly how strong passwords from these options are (the same rules <see cref="Generate"/> uses).</summary>
        public static PasswordGenEstimate Estimate(PasswordGenOptions o)
        {
            string? error = Validate(o);
            if (error != null) return new PasswordGenEstimate { Error = error, Label = "—" };

            double bits;
            string detail;
            if (o.Mode == PasswordGenMode.Passphrase)
            {
                int words = ClampWords(o.WordCount);
                bits = words * Math.Log2(WordList.Length) + (o.AppendNumber ? Math.Log2(100) : 0);
                detail = $"{WordList.Length:N0} words × {words}" + (o.AppendNumber ? " + 2 digits" : "");
            }
            else
            {
                var pools = ActivePools(o);
                int length = ClampLength(o.Length);
                int poolSize = pools.Sum(p => p.Length);
                bool requireAll = RequiresEveryType(o, pools, length);
                bits = requireAll ? Log2(CountWithEveryType(pools.Select(p => p.Length).ToList(), length)) : length * Math.Log2(poolSize);
                detail = $"{poolSize} characters × {length}" + (requireAll ? " · one of each type" : "");
            }

            var strength = Grade(bits);
            return new PasswordGenEstimate
            {
                Bits = Math.Round(bits, 1),
                Strength = strength,
                Label = StrengthLabel(strength),
                Percent = Math.Clamp(bits * 100 / 128, 2, 100),
                Detail = detail,
                CrackTime = CrackTime(bits),
            };
        }

        private static bool RequiresEveryType(PasswordGenOptions o, List<string> pools, int length) =>
            o.RequireEveryType && pools.Count > 1 && length >= pools.Count;

        /// <summary>
        /// How many strings of <paramref name="length"/> use at least one
        /// character from every group (inclusion-exclusion over the groups
        /// left out). Exact, so the bits shown are exact.
        /// </summary>
        public static BigInteger CountWithEveryType(IReadOnlyList<int> poolSizes, int length)
        {
            int total = poolSizes.Sum();
            BigInteger count = BigInteger.Zero;
            for (int mask = 0; mask < 1 << poolSizes.Count; mask++)
            {
                int left = total, removed = 0;
                for (int i = 0; i < poolSizes.Count; i++)
                    if ((mask & (1 << i)) != 0)
                    {
                        left -= poolSizes[i];
                        removed++;
                    }
                var term = BigInteger.Pow(left, length);
                count += removed % 2 == 0 ? term : -term;
            }
            return count;
        }

        private static double Log2(BigInteger n) => n <= 0 ? 0 : BigInteger.Log(n, 2);

        /// <summary>Common rough thresholds (bits) - an at-a-glance signal, not a full crack model.</summary>
        public static PasswordGenStrength Grade(double bits) => bits switch
        {
            < 35 => PasswordGenStrength.VeryWeak,
            < 60 => PasswordGenStrength.Weak,
            < 80 => PasswordGenStrength.Fair,
            < 100 => PasswordGenStrength.Strong,
            _ => PasswordGenStrength.VeryStrong,
        };

        public static string StrengthLabel(PasswordGenStrength s) => s switch
        {
            PasswordGenStrength.VeryWeak => "Very weak",
            PasswordGenStrength.Weak => "Weak",
            PasswordGenStrength.Fair => "Fair",
            PasswordGenStrength.Strong => "Strong",
            _ => "Very strong",
        };

        /// <summary>Average time to guess it at <see cref="GuessesPerSecond"/> (half the possibilities): "about 3 days".</summary>
        public static string CrackTime(double bits)
        {
            if (bits <= 0) return "instantly";
            double seconds = bits > 1000 ? double.PositiveInfinity : Math.Pow(2, bits - 1) / GuessesPerSecond;
            const double Year = 365.25 * 86400;
            if (seconds < 1) return "less than a second";
            if (seconds < 60) return About(seconds, "second");
            if (seconds < 3600) return About(seconds / 60, "minute");
            if (seconds < 86400) return About(seconds / 3600, "hour");
            if (seconds < Year) return About(seconds / 86400, "day");
            double years = seconds / Year;
            if (years < 1000) return About(years, "year");
            if (years < 1e6) return $"about {years / 1e3:0} thousand years";
            if (years < 1e9) return $"about {years / 1e6:0} million years";
            if (years < 1.4e10) return $"about {years / 1e9:0} billion years";
            return "longer than the age of the universe";
        }

        private static string About(double value, string unit)
        {
            long n = Math.Max(1, (long)Math.Round(value));
            return $"about {n:N0} {unit}{(n == 1 ? "" : "s")}";
        }

        // =================================================================
        //  Generating
        // =================================================================

        /// <summary>One password or passphrase. Throws ArgumentException when <see cref="Validate"/> says no.</summary>
        public static string Generate(PasswordGenOptions o)
        {
            string? error = Validate(o);
            if (error != null) throw new ArgumentException(error, nameof(o));
            return o.Mode == PasswordGenMode.Passphrase ? GeneratePassphrase(o) : GenerateRandom(o);
        }

        /// <summary>
        /// Uniform over every string that qualifies: draw from all the chosen
        /// characters and draw again when "one of each type" isn't met
        /// (rejection sampling). The old way - one of each first, then fill
        /// and shuffle - favoured some mixes, so the bits shown were a bit
        /// higher than real.
        /// </summary>
        private static string GenerateRandom(PasswordGenOptions o)
        {
            var pools = ActivePools(o);
            int length = ClampLength(o.Length);
            string all = string.Concat(pools);
            bool requireAll = RequiresEveryType(o, pools, length);
            var chars = new char[length];

            // Worst case (4 characters, all four types) qualifies about 7% of the time - a few dozen draws at most.
            for (int attempt = 0; attempt < 100_000; attempt++)
            {
                for (int i = 0; i < length; i++)
                    chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
                if (!requireAll || pools.All(p => chars.Any(c => p.IndexOf(c) >= 0)))
                    break;
            }
            string result = new string(chars);
            Array.Clear(chars);
            return result;
        }

        private static string GeneratePassphrase(PasswordGenOptions o)
        {
            int count = ClampWords(o.WordCount);
            string separator = o.Separator ?? "";
            var words = new string[count];
            for (int i = 0; i < count; i++)
            {
                string word = WordList[RandomNumberGenerator.GetInt32(WordList.Length)];
                words[i] = o.CapitalizeWords ? char.ToUpperInvariant(word[0]) + word[1..] : word;
            }
            string phrase = string.Join(separator, words);
            if (o.AppendNumber) phrase += separator + RandomNumberGenerator.GetInt32(0, 100).ToString("D2", CultureInfo.InvariantCulture);
            return phrase;
        }

        /// <summary>A new row for the list.</summary>
        public static PasswordGenItem NewItem(int number, PasswordGenOptions o, PasswordGenEstimate estimate)
        {
            var item = new PasswordGenItem { Number = number };
            Fill(item, o, estimate);
            return item;
        }

        /// <summary>A batch (1 to <see cref="MaxBatch"/>). Throws ArgumentException when <see cref="Validate"/> says no.</summary>
        public static List<PasswordGenItem> GenerateBatch(PasswordGenOptions o)
        {
            var estimate = Estimate(o);
            if (estimate.Error != null) throw new ArgumentException(estimate.Error, nameof(o));
            int count = ClampBatch(o.BatchCount);
            var list = new List<PasswordGenItem>(count);
            for (int i = 1; i <= count; i++) list.Add(NewItem(i, o, estimate));
            return list;
        }

        /// <summary>Puts a fresh password into an existing row (the row's regenerate button).</summary>
        public static void Fill(PasswordGenItem item, PasswordGenOptions o, PasswordGenEstimate? estimate = null)
        {
            estimate ??= Estimate(o);
            if (estimate.Error != null) throw new ArgumentException(estimate.Error, nameof(o));
            string password = Generate(o);
            item.Set(password, Segments(password, o), estimate);
        }

        /// <summary>The password in runs of letters / digits / symbols / separators, for colouring.</summary>
        public static List<PasswordGenSegment> Segments(string password, PasswordGenOptions o)
        {
            string separator = o.Mode == PasswordGenMode.Passphrase ? o.Separator ?? "" : "";
            var result = new List<PasswordGenSegment>();
            var run = new StringBuilder();
            PasswordGenCharKind? current = null;
            foreach (char c in password)
            {
                var kind = char.IsLetter(c) ? PasswordGenCharKind.Letter
                    : char.IsDigit(c) ? PasswordGenCharKind.Digit
                    : separator.Length > 0 && separator.IndexOf(c) >= 0 ? PasswordGenCharKind.Separator
                    : PasswordGenCharKind.Symbol;
                if (current != null && kind != current)
                {
                    result.Add(new PasswordGenSegment(run.ToString(), current.Value));
                    run.Clear();
                }
                current = kind;
                run.Append(c);
            }
            if (current != null) result.Add(new PasswordGenSegment(run.ToString(), current.Value));
            return result;
        }

        // =================================================================
        //  Export
        // =================================================================

        /// <summary>
        /// Text: one password per line, exactly as generated. CSV: with
        /// strength and bits (numbers always with a dot, so a comma-decimal
        /// Windows doesn't split the column); a password starting with
        /// = + - @ gets a leading ' so Excel doesn't run it as a formula.
        /// Written under a temporary name and renamed.
        /// </summary>
        public static Task ExportAsync(IReadOnlyList<PasswordGenItem> items, string path, PasswordGenExportFormat format) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(temp, BuildExport(items, format), format == PasswordGenExportFormat.Csv ? new UTF8Encoding(true) : new UTF8Encoding(false));
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            });

        public static string BuildExport(IReadOnlyList<PasswordGenItem> items, PasswordGenExportFormat format)
        {
            var sb = new StringBuilder();
            if (format == PasswordGenExportFormat.Text)
            {
                foreach (var i in items) sb.Append(i.Password).Append("\r\n");
                return sb.ToString();
            }
            sb.Append("Number,Password,Strength,Entropy bits,Generated\r\n");
            foreach (var i in items)
                sb.Append(string.Join(",",
                        i.Number.ToString(CultureInfo.InvariantCulture),
                        Cell(i.Password),
                        Cell(i.StrengthLabel),
                        i.Estimate.Bits.ToString("0.0", CultureInfo.InvariantCulture),
                        i.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                  .Append("\r\n");
            return sb.ToString();
        }

        /// <summary>CSV cell: formula guard, then quotes when needed.</summary>
        public static string Cell(string value)
        {
            string v = value ?? "";
            if (v.Length > 0 && "=+-@\t\r".IndexOf(v[0]) >= 0) v = "'" + v;
            return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v != v.Trim() ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        public static string PlainError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows didn't allow it (read-only folder, or needs admin rights).",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "The file is open in another program.",
            _ => ex.Message,
        };

        // =================================================================
        //  Settings (%AppData%\Smart Tools\password_generator.json - never a password)
        // =================================================================

        public static readonly IReadOnlyList<int> BatchChoices = new[] { 1, 5, 10, 20 };

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "password_generator.json");

        public static PasswordGenSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new PasswordGenSettings();
                var s = JsonSerializer.Deserialize<PasswordGenSettings>(File.ReadAllText(file), SettingsJson) ?? new PasswordGenSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = PasswordGenMode.RandomCharacters;
                s.Length = ClampLength(s.Length);
                s.WordCount = ClampWords(s.WordCount);
                if (!BatchChoices.Contains(s.BatchCount)) s.BatchCount = 5;
                if (s.Separator == null || !Separators.Contains(s.Separator)) s.Separator = "-";
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new PasswordGenSettings();
            }
        }

        public static void SaveSettings(PasswordGenSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, SettingsJson);
    }
}
