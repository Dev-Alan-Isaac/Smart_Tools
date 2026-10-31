using Smart_Tools.WPF.Models;
using System.Security.Cryptography;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One-time codes (RFC 6238 TOTP over RFC 4226 HOTP). Portable: no WPF.</summary>
    public sealed partial class PasswordVaultManager
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        /// <summary>Accepts a bare base32 secret ("JBSW Y3DP-EHPK3PXP") or an otpauth://totp/... URI. Null if it isn't usable.</summary>
        public static VaultTotpParams? ParseTotp(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string text = input.Trim();
            string secret = text, algorithm = "SHA1";
            int digits = 6, period = 30;

            if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
                foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = part[..eq].ToLowerInvariant(), v = Uri.UnescapeDataString(part[(eq + 1)..]);
                    switch (k)
                    {
                        case "secret": secret = v; break;
                        case "algorithm": algorithm = v.ToUpperInvariant(); break;
                        case "digits": if (int.TryParse(v, out int d)) digits = d; break;
                        case "period": if (int.TryParse(v, out int p)) period = p; break;
                    }
                }
                if (secret == text) return null;   // no secret= parameter
            }

            if (algorithm is not ("SHA1" or "SHA256" or "SHA512")) return null;
            if (digits is < 6 or > 8 || period is < 5 or > 300) return null;
            byte[]? key = DecodeBase32(secret);
            return key is { Length: > 0 } ? new VaultTotpParams(key, algorithm, digits, period) : null;
        }

        /// <summary>Base32 (RFC 4648), case-insensitive, ignores spaces, dashes and "=" padding. Null for any other character.</summary>
        public static byte[]? DecodeBase32(string text)
        {
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (char raw in text)
            {
                if (raw is ' ' or '-' or '=') continue;
                int val = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw));
                if (val < 0) return null;
                buffer = (buffer << 5) | val;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)((buffer >> bits) & 0xFF));
                }
            }
            return bytes.ToArray();
        }

        /// <summary>The code for <paramref name="now"/>; <paramref name="secondsLeft"/> is how long it stays valid. Null if the secret isn't usable.</summary>
        public static string? TotpCode(string? secret, DateTimeOffset now, out int secondsLeft)
        {
            secondsLeft = 0;
            var p = ParseTotp(secret);
            if (p == null) return null;
            long unix = now.ToUnixTimeSeconds();
            secondsLeft = p.PeriodSeconds - (int)(unix % p.PeriodSeconds);
            return Hotp(p, unix / p.PeriodSeconds);
        }

        public static string Hotp(VaultTotpParams p, long counter)
        {
            Span<byte> msg = stackalloc byte[8];
            for (int i = 7; i >= 0; i--)
            {
                msg[i] = (byte)(counter & 0xFF);
                counter >>= 8;
            }
            Span<byte> mac = stackalloc byte[64];
            int len = p.Algorithm switch
            {
                "SHA256" => HMACSHA256.HashData(p.Secret, msg, mac),
                "SHA512" => HMACSHA512.HashData(p.Secret, msg, mac),
                _ => HMACSHA1.HashData(p.Secret, msg, mac),
            };
            int offset = mac[len - 1] & 0x0F;
            int binary = ((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
            int mod = 1;
            for (int i = 0; i < p.Digits; i++) mod *= 10;
            return (binary % mod).ToString().PadLeft(p.Digits, '0');
        }
    }
}
