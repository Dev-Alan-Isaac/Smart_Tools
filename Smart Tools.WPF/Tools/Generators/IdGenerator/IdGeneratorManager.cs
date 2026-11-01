using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    public enum IdKind { Uuid4, Uuid7, Ulid, Hex, Lorem }

    public sealed class IdGeneratorSettings
    {
        public IdKind Kind { get; set; } = IdKind.Uuid4;
        public int Count { get; set; } = 10;
        public bool Uppercase { get; set; }
        public bool NoHyphens { get; set; }
        public int HexBytes { get; set; } = 16;
        public int LoremParagraphs { get; set; } = 3;
    }

    /// <summary>
    /// Unique IDs (UUID v4 / v7, ULID), random hex and lorem ipsum. Everything comes from the system's secure random
    /// numbers. No WPF types.
    /// </summary>
    public static class IdGeneratorManager
    {
        public const string SettingsName = "id_generator";
        public const int MaxCount = 1000;

        private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        private static readonly string[] LoremWords =
        {
            "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor",
            "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim", "ad", "minim", "veniam", "quis", "nostrud",
            "exercitation", "ullamco", "laboris", "nisi", "aliquip", "ex", "ea", "commodo", "consequat", "duis", "aute", "irure",
            "in", "reprehenderit", "voluptate", "velit", "esse", "cillum", "fugiat", "nulla", "pariatur", "excepteur", "sint",
            "occaecat", "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia", "deserunt", "mollit", "anim", "id", "est", "laborum",
        };

        public static IdGeneratorSettings Clamp(IdGeneratorSettings s)
        {
            if (!Enum.IsDefined(s.Kind)) s.Kind = IdKind.Uuid4;
            s.Count = Math.Clamp(s.Count, 1, MaxCount);
            s.HexBytes = Math.Clamp(s.HexBytes, 1, 128);
            s.LoremParagraphs = Math.Clamp(s.LoremParagraphs, 1, 50);
            return s;
        }

        /// <summary>The generated lines (lorem gives one paragraph per line).</summary>
        public static List<string> Generate(IdGeneratorSettings s, DateTimeOffset? now = null)
        {
            s = Clamp(s);
            var time = now ?? DateTimeOffset.UtcNow;
            var list = new List<string>();
            int count = s.Kind == IdKind.Lorem ? s.LoremParagraphs : s.Count;
            for (int i = 0; i < count; i++)
            {
                string text = s.Kind switch
                {
                    IdKind.Uuid4 => Uuid4().ToString("D"),
                    IdKind.Uuid7 => Uuid7(time).ToString("D"),
                    IdKind.Ulid => Ulid(time),
                    IdKind.Hex => Convert.ToHexString(RandomNumberGenerator.GetBytes(s.HexBytes)),
                    _ => LoremParagraph(i == 0),
                };
                if (s.Kind is IdKind.Uuid4 or IdKind.Uuid7 && s.NoHyphens) text = text.Replace("-", "");
                text = s.Kind == IdKind.Lorem ? text : (s.Uppercase ? text.ToUpperInvariant() : text.ToLowerInvariant());
                list.Add(text);
            }
            return list;
        }

        /// <summary>Random UUID (version 4).</summary>
        public static Guid Uuid4()
        {
            var b = RandomNumberGenerator.GetBytes(16);
            b[6] = (byte)((b[6] & 0x0F) | 0x40);
            b[8] = (byte)((b[8] & 0x3F) | 0x80);
            return FromRfcBytes(b);
        }

        /// <summary>Time-ordered UUID (version 7): 48-bit millisecond timestamp, then random bits.</summary>
        public static Guid Uuid7(DateTimeOffset time)
        {
            long ms = time.ToUnixTimeMilliseconds();
            var b = RandomNumberGenerator.GetBytes(16);
            for (int i = 0; i < 6; i++) b[i] = (byte)(ms >> (8 * (5 - i)));
            b[6] = (byte)((b[6] & 0x0F) | 0x70);
            b[8] = (byte)((b[8] & 0x3F) | 0x80);
            return FromRfcBytes(b);
        }

        /// <summary>Guid from bytes in the order they are written (RFC 4122), not .NET's mixed-endian order.</summary>
        private static Guid FromRfcBytes(byte[] b) => new(b, bigEndian: true);

        /// <summary>26-character ULID: 48-bit time then 80 random bits, in Crockford base32 (sorts by time).</summary>
        public static string Ulid(DateTimeOffset time)
        {
            long ms = time.ToUnixTimeMilliseconds();
            var random = RandomNumberGenerator.GetBytes(10);
            var chars = new char[26];
            for (int i = 9; i >= 0; i--)
            {
                chars[i] = Crockford[(int)(ms & 31)];
                ms >>= 5;
            }
            // 80 random bits -> 16 characters
            System.Numerics.BigInteger r = new(random, isUnsigned: true, isBigEndian: true);
            for (int i = 25; i >= 10; i--)
            {
                chars[i] = Crockford[(int)(r & 31)];
                r >>= 5;
            }
            return new string(chars);
        }

        private static string LoremParagraph(bool first)
        {
            var sb = new StringBuilder();
            int sentences = 4 + RandomNumberGenerator.GetInt32(3);
            for (int s = 0; s < sentences; s++)
            {
                int words = 7 + RandomNumberGenerator.GetInt32(9);
                for (int w = 0; w < words; w++)
                {
                    string word = first && s == 0 && w < 5
                        ? new[] { "lorem", "ipsum", "dolor", "sit", "amet" }[w]
                        : LoremWords[RandomNumberGenerator.GetInt32(LoremWords.Length)];
                    if (w == 0) word = char.ToUpperInvariant(word[0]) + word[1..];
                    else sb.Append(w == 5 && RandomNumberGenerator.GetInt32(4) == 0 ? ", " : " ");
                    sb.Append(word);
                }
                sb.Append(s < sentences - 1 ? ". " : ".");
            }
            return sb.ToString().Replace(" ,", ",").Replace(" .", ".");
        }
    }
}
