using Smart_Tools.WPF.Models;
using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Password health audit and a quick strength estimate. Portable: no WPF.</summary>
    public sealed partial class PasswordVaultManager
    {
        public const double WeakBelowBits = 45;
        public static readonly TimeSpan OldAfter = TimeSpan.FromDays(365);

        private static readonly string[] CommonParts =
        {
            "password", "passw0rd", "qwerty", "azerty", "letmein", "welcome", "admin", "iloveyou", "monkey", "dragon",
            "123456", "654321", "111111", "000000", "abc123", "contraseña", "football", "login", "master",
        };

        /// <summary>
        /// Rough guessing strength in bits: length x log2(alphabet size), cut down for repeated characters,
        /// runs and well-known words. A signal for the user, not a proof - random generated passwords score high, "Password1!" low.
        /// </summary>
        public static double EstimateBits(ReadOnlySpan<char> password)
        {
            if (password.IsEmpty) return 0;
            bool lower = false, upper = false, digit = false, symbol = false, other = false;
            foreach (char c in password)
            {
                if (c is >= 'a' and <= 'z') lower = true;
                else if (c is >= 'A' and <= 'Z') upper = true;
                else if (c is >= '0' and <= '9') digit = true;
                else if (c < 128) symbol = true;
                else other = true;
            }
            int pool = (lower ? 26 : 0) + (upper ? 26 : 0) + (digit ? 10 : 0) + (symbol ? 33 : 0) + (other ? 100 : 0);
            var distinct = new HashSet<char>();
            int runs = 0;
            for (int i = 0; i < password.Length; i++)
            {
                distinct.Add(password[i]);
                if (i > 0 && (password[i] == password[i - 1] || password[i] == password[i - 1] + 1)) runs++;
            }
            double bits = distinct.Count * Math.Log2(Math.Max(pool, 2)) + (password.Length - distinct.Count) * 0.5;
            bits -= runs * Math.Log2(Math.Max(pool, 2)) * 0.6;
            string lowered = password.ToString().ToLowerInvariant();
            foreach (string part in CommonParts)
                if (lowered.Contains(part, StringComparison.Ordinal)) { bits = Math.Min(bits, 22) ; break; }
            return Math.Max(0, bits);
        }

        public static PasswordGenStrength GradePassword(ReadOnlySpan<char> password) =>
            PasswordGeneratorManager.Grade(EstimateBits(password));

        /// <summary>Weak, reused and old passwords. Reuse is found by comparing keyed hashes (key random per call, nothing kept).</summary>
        public static List<VaultHealthIssue> AnalyzeHealth(IEnumerable<VaultEntry> entries, DateTime? nowUtc = null)
        {
            var list = entries.Where(e => e.Password.Length > 0).ToList();
            var issues = new List<VaultHealthIssue>();
            DateTime now = nowUtc ?? DateTime.UtcNow;

            byte[] key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var groups = list.GroupBy(e => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(e.Password))));
                foreach (var g in groups)
                    if (g.Count() > 1)
                        foreach (var e in g) issues.Add(new VaultHealthIssue(e.Id, VaultHealthKind.Reused));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            foreach (var e in list)
            {
                if (EstimateBits(e.Password) < WeakBelowBits) issues.Add(new VaultHealthIssue(e.Id, VaultHealthKind.Weak));
                if (now - e.PasswordChangedUtc > OldAfter) issues.Add(new VaultHealthIssue(e.Id, VaultHealthKind.Old));
            }
            return issues;
        }
    }
}
