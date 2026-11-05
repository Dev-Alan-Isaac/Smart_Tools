using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    // Numbers are fixed: they are what older settings files stored.
    public enum IdKind { Uuid4 = 0, Uuid7 = 1, Ulid = 2, Hex = 3, Lorem = 4, NanoId = 5, ShortCode = 6 }

    /// <summary>What goes between the values when the list is plain.</summary>
    public enum IdSeparator { NewLine, Comma, CommaSpace, Space, Semicolon, Tab, None }

    /// <summary>How the list is written out.</summary>
    public enum IdWrap { Plain, JsonArray, SqlIn, Csv }

    /// <summary>What the inspector found out about a pasted ID.</summary>
    public sealed record IdInspection(string Title, IReadOnlyList<string> Lines);

    public sealed class IdGeneratorSettings
    {
        public IdKind Kind { get; set; } = IdKind.Uuid4;
        public int Count { get; set; } = 10;
        public bool Uppercase { get; set; }
        public bool NoHyphens { get; set; }
        public int HexBytes { get; set; } = 16;
        public int LoremParagraphs { get; set; } = 3;
        public int NanoLength { get; set; } = 21;
        public int ShortLength { get; set; } = 8;
        public string Prefix { get; set; } = "";
        public string Suffix { get; set; } = "";
        public IdSeparator Separator { get; set; } = IdSeparator.NewLine;
        public IdWrap Wrap { get; set; } = IdWrap.Plain;
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
            s.NanoLength = Math.Clamp(s.NanoLength, 4, 64);
            s.ShortLength = Math.Clamp(s.ShortLength, 4, 32);
            s.Prefix = (s.Prefix ?? "").Length > 40 ? s.Prefix![..40] : s.Prefix ?? "";
            s.Suffix = (s.Suffix ?? "").Length > 40 ? s.Suffix![..40] : s.Suffix ?? "";
            if (!Enum.IsDefined(s.Separator)) s.Separator = IdSeparator.NewLine;
            if (!Enum.IsDefined(s.Wrap)) s.Wrap = IdWrap.Plain;
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
                    IdKind.NanoId => Random(NanoAlphabet, s.NanoLength),
                    IdKind.ShortCode => Random(ShortAlphabet, s.ShortLength),
                    _ => LoremParagraph(i == 0),
                };
                if (s.Kind is IdKind.Uuid4 or IdKind.Uuid7 && s.NoHyphens) text = text.Replace("-", "");
                if (s.Kind is IdKind.Uuid4 or IdKind.Uuid7 or IdKind.Ulid or IdKind.Hex)   // NanoID is case-sensitive; short codes are already upper case
                    text = s.Uppercase ? text.ToUpperInvariant() : text.ToLowerInvariant();
                list.Add(text);
            }
            return list;
        }

        private const string NanoAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";   // 64 symbols, the NanoID default
        private const string ShortAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";                                  // no 0/O, 1/I/L: easy to read out

        private static string Random(string alphabet, int length)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            return new string(chars);
        }

        // =================================================================
        //  Writing the list out
        // =================================================================

        /// <summary>The text shown, copied and saved: prefix and suffix on each value, then the separator or the chosen wrapper.</summary>
        public static string Format(IReadOnlyList<string> lines, IdGeneratorSettings s)
        {
            if (s.Kind == IdKind.Lorem) return string.Join(Environment.NewLine + Environment.NewLine, lines);

            var values = lines.Select(l => s.Prefix + l + s.Suffix).ToList();
            switch (s.Wrap)
            {
                case IdWrap.JsonArray:
                    return JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

                case IdWrap.SqlIn:
                    return "(" + Environment.NewLine + string.Join("," + Environment.NewLine, values.Select(v => "  '" + v.Replace("'", "''") + "'")) + Environment.NewLine + ")";

                case IdWrap.Csv:
                    return "value" + Environment.NewLine + string.Join(Environment.NewLine, values.Select(CsvCell));

                default:
                    string sep = s.Separator switch
                    {
                        IdSeparator.Comma => ",",
                        IdSeparator.CommaSpace => ", ",
                        IdSeparator.Space => " ",
                        IdSeparator.Semicolon => ";",
                        IdSeparator.Tab => "\t",
                        IdSeparator.None => "",
                        _ => Environment.NewLine,
                    };
                    return string.Join(sep, values);
            }
        }

        private static string CsvCell(string v)
        {
            if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0) v = "'" + v;   // would run as a formula in Excel
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        // =================================================================
        //  Inspecting an ID
        // =================================================================

        private static readonly DateTime GregorianStart = new(1582, 10, 15, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Says what a pasted value is (UUID and its version, ULID, hex, NanoID-like) and reads the time out of the ones that carry it. Null for empty input.</summary>
        public static IdInspection? Inspect(string? text)
        {
            string t = (text ?? "").Trim().Trim('{', '}');
            if (t.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) t = t[9..];
            if (t.Length == 0) return null;

            var lines = new List<string>();
            string compact = t.Replace("-", "");

            bool dashed = t.Length == 36 && t[8] == '-' && t[13] == '-' && t[18] == '-' && t[23] == '-';
            if (compact.Length == 32 && compact.All(Uri.IsHexDigit) && (dashed || LooksLikeBareUuid(compact)))
            {
                var b = Convert.FromHexString(compact);
                if (b.All(x => x == 0)) return new IdInspection(Loc.T("Str_IdGen_InspNil", "Nil UUID"), new[] { Loc.T("Str_IdGen_InspNilNote", "All zeros: the \"no value\" UUID.") });
                if (b.All(x => x == 0xFF)) return new IdInspection(Loc.T("Str_IdGen_InspMax", "Max UUID"), new[] { Loc.T("Str_IdGen_InspMaxNote", "All ones: the largest possible UUID.") });

                int version = b[6] >> 4;
                int variantBits = b[8] >> 6;
                lines.Add(Loc.F("Str_IdGen_InspVersion", "Version {0}: {1}", version, VersionName(version)));
                lines.Add(variantBits == 2
                    ? Loc.T("Str_IdGen_InspVariantRfc", "Variant: RFC 4122 / 9562 (the standard one)")
                    : Loc.T("Str_IdGen_InspVariantOther", "Variant: not the standard one (old Microsoft or reserved)"));

                if (version == 7)
                {
                    long ms = 0;
                    for (int i = 0; i < 6; i++) ms = (ms << 8) | b[i];
                    lines.Add(Loc.F("Str_IdGen_InspTime", "Made at: {0}", Stamp(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime)));
                }
                else if (version == 1)
                {
                    long ticks = ((long)(b[6] & 0x0F) << 56) | ((long)b[7] << 48) | ((long)b[4] << 40) | ((long)b[5] << 32) | ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
                    lines.Add(Loc.F("Str_IdGen_InspTime", "Made at: {0}", Stamp(GregorianStart.AddTicks(ticks))));
                }
                else if (version == 6)
                {
                    long ticks = ((long)b[0] << 52) | ((long)b[1] << 44) | ((long)b[2] << 36) | ((long)b[3] << 28) | ((long)b[4] << 20) | ((long)b[5] << 12) | ((long)(b[6] & 0x0F) << 8) | b[7];
                    lines.Add(Loc.F("Str_IdGen_InspTime", "Made at: {0}", Stamp(GregorianStart.AddTicks(ticks))));
                }
                else if (version == 4)
                {
                    lines.Add(Loc.T("Str_IdGen_InspRandom", "122 of its bits are random; it carries no time."));
                }
                return new IdInspection(Loc.T("Str_IdGen_InspUuid", "UUID"), lines);
            }

            if (t.Length == 26 && t.All(c => Crockford.Contains(char.ToUpperInvariant(c))) && char.ToUpperInvariant(t[0]) <= '7')
            {
                long ms = 0;
                foreach (char c in t[..10]) ms = (ms << 5) | (long)Crockford.IndexOf(char.ToUpperInvariant(c));
                lines.Add(Loc.F("Str_IdGen_InspTime", "Made at: {0}", Stamp(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime)));
                lines.Add(Loc.T("Str_IdGen_InspUlidRest", "The other 16 characters are random (80 bits)."));
                return new IdInspection(Loc.T("Str_IdGen_InspUlid", "ULID"), lines);
            }

            if (t.Length % 2 == 0 && t.Length >= 4 && t.All(Uri.IsHexDigit))
            {
                lines.Add(Loc.F("Str_IdGen_InspHexBytes", "{0} bytes ({1} bits)", t.Length / 2, t.Length * 4));
                string? hash = (t.Length / 2) switch { 16 => "MD5", 20 => "SHA-1", 32 => "SHA-256", 48 => "SHA-384", 64 => "SHA-512", _ => null };
                if (hash != null) lines.Add(Loc.F("Str_IdGen_InspHexHash", "The same size as an {0} hash.", hash));
                return new IdInspection(Loc.T("Str_IdGen_InspHex", "Hexadecimal"), lines);
            }

            if (t.Length is >= 8 and <= 64 && t.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                lines.Add(Loc.F("Str_IdGen_InspChars", "{0} characters from A-Z, a-z, 0-9, _ and -", t.Length));
                lines.Add(Loc.T("Str_IdGen_InspNanoNote", "No time or structure inside: random text, like a NanoID."));
                return new IdInspection(t.Length == 21 ? Loc.T("Str_IdGen_InspNano", "Looks like a NanoID") : Loc.T("Str_IdGen_InspRandomText", "Random-looking text"), lines);
            }

            return new IdInspection(Loc.T("Str_IdGen_InspUnknown", "Not a format I know"), new[] { Loc.F("Str_IdGen_InspLength", "{0} characters", t.Length) });
        }

        /// <summary>32 hex digits with no dashes are only called a UUID when the version and variant bits fit (otherwise it is just hex).</summary>
        private static bool LooksLikeBareUuid(string hex)
        {
            var b = Convert.FromHexString(hex);
            int version = b[6] >> 4;
            return version is >= 1 and <= 8 && (b[8] >> 6) == 2;
        }

        private static string VersionName(int v) => v switch
        {
            1 => Loc.T("Str_IdGen_InspV1", "time and machine based"),
            2 => Loc.T("Str_IdGen_InspV2", "DCE security"),
            3 => Loc.T("Str_IdGen_InspV3", "name-based (MD5)"),
            4 => Loc.T("Str_IdGen_InspV4", "random"),
            5 => Loc.T("Str_IdGen_InspV5", "name-based (SHA-1)"),
            6 => Loc.T("Str_IdGen_InspV6", "reordered time-based"),
            7 => Loc.T("Str_IdGen_InspV7", "sortable by Unix time"),
            8 => Loc.T("Str_IdGen_InspV8", "custom"),
            _ => Loc.T("Str_IdGen_InspVUnknown", "unknown"),
        };

        private static string Stamp(DateTime utc) =>
            string.Create(CultureInfo.InvariantCulture, $"{utc:yyyy-MM-dd HH:mm:ss.fff} UTC  ·  {utc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local");

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
