using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    public enum TotpAlgorithm { Sha1, Sha256, Sha512 }

    /// <summary>What an otpauth:// link (the text behind a 2FA QR code) says.</summary>
    public sealed record TotpLink(string Secret, string Label, string Issuer, int Digits, int Period, TotpAlgorithm Algorithm);

    /// <summary>
    /// One-time codes as in RFC 6238 (TOTP) / RFC 4226 (HOTP), the same as Google Authenticator and friends.
    /// The secret is never stored by the tool. No WPF types.
    /// </summary>
    public static class TotpCodeManager
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        /// <summary>Reads a base32 secret (spaces, dashes, lower case and "=" padding are fine).</summary>
        public static bool TryDecodeBase32(string? text, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(text)) return false;
            var clean = new StringBuilder();
            foreach (char c in text)
            {
                if (c is ' ' or '-' or '\t' or '=') continue;
                clean.Append(char.ToUpperInvariant(c));
            }
            if (clean.Length < 8) return false;

            var bytes = new List<byte>();
            int buffer = 0, bitsLeft = 0;
            foreach (char c in clean.ToString())
            {
                int v = Base32Alphabet.IndexOf(c);
                if (v < 0) return false;
                buffer = (buffer << 5) | v;
                bitsLeft += 5;
                if (bitsLeft >= 8)
                {
                    bitsLeft -= 8;
                    bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
                }
            }
            data = bytes.ToArray();
            return data.Length > 0;
        }

        /// <summary>The code for a moment in time. Digits 6-8, period in seconds.</summary>
        public static string Generate(byte[] secret, DateTimeOffset time, int digits = 6, int period = 30, TotpAlgorithm algorithm = TotpAlgorithm.Sha1)
        {
            long counter = time.ToUnixTimeSeconds() / Math.Max(1, period);
            return GenerateHotp(secret, counter, digits, algorithm);
        }

        public static string GenerateHotp(byte[] secret, long counter, int digits, TotpAlgorithm algorithm)
        {
            digits = Math.Clamp(digits, 6, 8);
            var message = new byte[8];
            for (int i = 7; i >= 0; i--)
            {
                message[i] = (byte)(counter & 0xFF);
                counter >>= 8;
            }

            byte[] hash = algorithm switch
            {
                TotpAlgorithm.Sha256 => HMACSHA256.HashData(secret, message),
                TotpAlgorithm.Sha512 => HMACSHA512.HashData(secret, message),
                _ => HMACSHA1.HashData(secret, message),
            };

            int offset = hash[^1] & 0x0F;
            int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            long mod = 1;
            for (int i = 0; i < digits; i++) mod *= 10;
            return (binary % mod).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        }

        /// <summary>Whole seconds until the code changes (1 to period).</summary>
        public static int SecondsLeft(DateTimeOffset time, int period)
        {
            period = Math.Max(1, period);
            return period - (int)(time.ToUnixTimeSeconds() % period);
        }

        /// <summary>otpauth://totp/Issuer:name?secret=...&amp;issuer=...&amp;digits=6&amp;period=30&amp;algorithm=SHA1</summary>
        public static bool TryParseLink(string? text, out TotpLink link)
        {
            link = new TotpLink("", "", "", 6, 30, TotpAlgorithm.Sha1);
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "otpauth" || !uri.Host.Equals("totp", StringComparison.OrdinalIgnoreCase))
                return false;

            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                string value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                query[key] = value;
            }
            if (!query.TryGetValue("secret", out string? secret) || !TryDecodeBase32(secret, out _)) return false;

            string label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            query.TryGetValue("issuer", out string? issuer);
            if (string.IsNullOrEmpty(issuer) && label.Contains(':')) issuer = label[..label.IndexOf(':')];

            int digits = query.TryGetValue("digits", out string? d) && int.TryParse(d, out int dn) ? Math.Clamp(dn, 6, 8) : 6;
            int period = query.TryGetValue("period", out string? p) && int.TryParse(p, out int pn) && pn > 0 ? Math.Min(pn, 300) : 30;
            var algorithm = query.TryGetValue("algorithm", out string? a)
                ? a.ToUpperInvariant() switch { "SHA256" => TotpAlgorithm.Sha256, "SHA512" => TotpAlgorithm.Sha512, _ => TotpAlgorithm.Sha1 }
                : TotpAlgorithm.Sha1;

            link = new TotpLink(secret, label, issuer ?? "", digits, period, algorithm);
            return true;
        }

        /// <summary>"123456" as "123 456" (or "1234 5678") to make it easy to read and type.</summary>
        public static string Group(string code) => code.Length switch
        {
            6 => code[..3] + " " + code[3..],
            8 => code[..4] + " " + code[4..],
            7 => code[..3] + " " + code[3..],
            _ => code,
        };
    }
}
