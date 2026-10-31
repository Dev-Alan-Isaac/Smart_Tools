using Smart_Tools.WPF.Models;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// JWT Decoder - reads the header, payload, and times of a JSON Web Token. Pure and offline: nothing is sent anywhere,
    /// nothing is stored, and the signature is never verified (that needs the key). Portable: no WPF, no I/O.
    /// </summary>
    public sealed class JwtDecoderManager
    {
        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] TimeClaims = { "exp", "nbf", "iat" };

        /// <summary>Cleans up what people paste: a "Bearer " prefix, quotes, spaces, and line breaks inside the token.</summary>
        public static string Clean(string? input)
        {
            string text = (input ?? "").Trim().Trim('"', '\'');
            if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) text = text[7..];
            return string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        }

        public static JwtDecoded Decode(string? input, DateTimeOffset now)
        {
            string token = Clean(input);
            if (token.Length == 0) return Fail(JwtProblem.Empty);

            var parts = token.Split('.');
            if (parts.Length == 5) return Fail(JwtProblem.Encrypted);   // JWE: the payload is encrypted, there is nothing to read
            if (parts.Length != 3) return Fail(JwtProblem.WrongPartCount);

            if (!TryBase64Url(parts[0], out byte[] headerBytes) || !TryBase64Url(parts[1], out byte[] payloadBytes) || !TryBase64Url(parts[2], out byte[] signature))
                return Fail(JwtProblem.BadEncoding);

            if (!TryParse(headerBytes, out JsonElement header) || header.ValueKind != JsonValueKind.Object) return Fail(JwtProblem.HeaderNotJson);
            if (!TryParse(payloadBytes, out JsonElement payload) || payload.ValueKind != JsonValueKind.Object) return Fail(JwtProblem.PayloadNotJson);

            string alg = Text(header, "alg");
            DateTimeOffset? exp = TimeOf(payload, "exp"), nbf = TimeOf(payload, "nbf"), iat = TimeOf(payload, "iat");

            var claims = new List<JwtClaim>();
            foreach (var property in payload.EnumerateObject())
            {
                claims.Add(new JwtClaim
                {
                    Name = property.Name,
                    Value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText(),
                    Time = Array.IndexOf(TimeClaims, property.Name) >= 0 ? TimeOf(payload, property.Name) : null,
                });
            }

            return new JwtDecoded
            {
                HeaderJson = JsonSerializer.Serialize(header, Pretty),
                PayloadJson = JsonSerializer.Serialize(payload, Pretty),
                Signature = parts[2],
                SignatureBytes = signature.Length,
                Algorithm = alg,
                TokenType = Text(header, "typ"),
                KeyId = Text(header, "kid"),
                IsUnsecured = alg.Equals("none", StringComparison.OrdinalIgnoreCase),
                IssuedAt = iat,
                NotBefore = nbf,
                Expires = exp,
                TimeState = StateAt(now, nbf, exp),
                Claims = claims,
            };
        }

        public static JwtTimeState StateAt(DateTimeOffset now, DateTimeOffset? notBefore, DateTimeOffset? expires)
        {
            if (expires is { } e && now >= e) return JwtTimeState.Expired;
            if (notBefore is { } n && now < n) return JwtTimeState.NotYetValid;
            return expires == null ? JwtTimeState.NoExpiry : JwtTimeState.Valid;
        }

        /// <summary>Decodes base64url (RFC 7515): - and _ instead of + and /, no padding. An empty string decodes to nothing.</summary>
        public static bool TryBase64Url(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text.Length == 0) return true;
            if (text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
            string padded = text.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 1: return false;
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            try
            {
                bytes = Convert.FromBase64String(padded);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool TryParse(byte[] utf8, out JsonElement element)
        {
            try
            {
                using var doc = JsonDocument.Parse(utf8);
                element = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                element = default;
                return false;
            }
        }

        private static string Text(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        /// <summary>A numeric date (seconds since 1970). Values outside the supported range are treated as missing.</summary>
        private static DateTimeOffset? TimeOf(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
            if (!value.TryGetDouble(out double seconds) || double.IsNaN(seconds) || seconds < -62135596800 || seconds > 253402300799) return null;
            return DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(seconds));
        }

        private static JwtDecoded Fail(JwtProblem problem) => new() { Problem = problem };
    }
}
