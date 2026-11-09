using Smart_Tools.WPF.Models;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// JWT Decoder - reads the header, payload, and times of a JSON Web Token, and checks its signature when the key is
    /// given (HS256/384/512 with a secret; RS, PS and ES 256/384/512 with a PEM public key, a certificate or a JWK).
    /// Pure and offline: nothing is sent anywhere or stored. Portable: no WPF, no I/O.
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

        // =================================================================
        //  Signature
        // =================================================================

        /// <summary>
        /// Checks the signature of <paramref name="input"/> against <paramref name="keyText"/>. The key is a secret for HS algorithms
        /// (plain text, or Base64 when <paramref name="secretIsBase64"/>), otherwise a PEM public key, a PEM certificate, or a JWK / JWKS.
        /// A key of the wrong kind for the algorithm is refused rather than tried (this stops "algorithm confusion", where a public
        /// key is used as an HMAC secret), and alg "none" is never accepted.
        /// </summary>
        public static JwtVerification Verify(string? input, string? keyText, bool secretIsBase64)
        {
            var parts = Clean(input).Split('.');
            if (parts.Length != 3 || !TryBase64Url(parts[0], out byte[] headerBytes) || !TryBase64Url(parts[1], out _) || !TryBase64Url(parts[2], out byte[] signature)
                || !TryParse(headerBytes, out JsonElement header) || header.ValueKind != JsonValueKind.Object)
                return new JwtVerification(JwtVerifyState.NoToken);

            string alg = Text(header, "alg"), kid = Text(header, "kid");
            if (alg.Equals("none", StringComparison.OrdinalIgnoreCase)) return new JwtVerification(JwtVerifyState.Unsecured, Algorithm: alg);

            string key = (keyText ?? "").Trim('\r', '\n');
            if (key.Trim().Length == 0) return new JwtVerification(JwtVerifyState.NoKey, Algorithm: alg);

            byte[] data = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            try
            {
                if (alg is "HS256" or "HS384" or "HS512") return VerifyHmac(alg, key, secretIsBase64, kid, data, signature);
                if (alg.Length == 5 && alg[..2] is "RS" or "PS" && alg[2..] is "256" or "384" or "512") return VerifyRsa(alg, key.Trim(), kid, data, signature);
                if (alg is "ES256" or "ES384" or "ES512") return VerifyEc(alg, key.Trim(), kid, data, signature);
            }
            catch (CryptographicException)
            {
                // A key the platform refuses (too short, odd parameters) is a key problem, not a verdict on the token.
                return new JwtVerification(JwtVerifyState.KeyProblem, JwtKeyProblem.NotRecognized, alg);
            }
            return new JwtVerification(JwtVerifyState.Unsupported, Algorithm: alg);
        }

        private static JwtVerification Result(string alg, bool ok) => new(ok ? JwtVerifyState.Valid : JwtVerifyState.Invalid, JwtKeyProblem.None, alg);

        private static JwtVerification Problem(string alg, JwtKeyProblem problem) => new(JwtVerifyState.KeyProblem, problem, alg);

        private static bool LooksLikePem(string s) => s.Contains("-----BEGIN", StringComparison.Ordinal);

        private static bool LooksLikeJson(string s) => s.TrimStart().StartsWith('{');

        private static JwtVerification VerifyHmac(string alg, string key, bool base64, string kid, byte[] data, byte[] signature)
        {
            byte[] secret;
            if (LooksLikePem(key)) return Problem(alg, JwtKeyProblem.PublicKeyForHmac);
            if (LooksLikeJson(key))
            {
                // A JWK of kind "oct" carries the secret; anything else is a public key and must not be used as one.
                var jwk = SelectJwk(key, kid, out bool kidMissing);
                if (kidMissing) return Problem(alg, JwtKeyProblem.KidNotFound);
                if (jwk is not { } j) return Problem(alg, JwtKeyProblem.BadJwk);
                if (Text(j, "kty") != "oct") return Problem(alg, JwtKeyProblem.PublicKeyForHmac);
                if (!TryBase64Url(Text(j, "k"), out secret) || secret.Length == 0) return Problem(alg, JwtKeyProblem.BadJwk);
            }
            else if (base64)
            {
                if (!TryBase64Any(key.Trim(), out secret)) return Problem(alg, JwtKeyProblem.BadBase64);
            }
            else
            {
                secret = Encoding.UTF8.GetBytes(key);
            }

            byte[] expected = alg switch
            {
                "HS256" => HMACSHA256.HashData(secret, data),
                "HS384" => HMACSHA384.HashData(secret, data),
                _ => HMACSHA512.HashData(secret, data),
            };
            CryptographicOperations.ZeroMemory(secret);
            return Result(alg, CryptographicOperations.FixedTimeEquals(expected, signature));
        }

        private static JwtVerification VerifyRsa(string alg, string key, string kid, byte[] data, byte[] signature)
        {
            if (!LooksLikePem(key) && !LooksLikeJson(key)) return Problem(alg, JwtKeyProblem.SecretForPublicKey);
            using RSA? rsa = LoadRsa(key, kid, out JwtKeyProblem problem);
            if (rsa == null) return Problem(alg, problem);
            var hash = HashFor(alg);
            var padding = alg.StartsWith("PS", StringComparison.Ordinal) ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
            return Result(alg, rsa.VerifyData(data, signature, hash, padding));
        }

        private static JwtVerification VerifyEc(string alg, string key, string kid, byte[] data, byte[] signature)
        {
            if (!LooksLikePem(key) && !LooksLikeJson(key)) return Problem(alg, JwtKeyProblem.SecretForPublicKey);
            using ECDsa? ec = LoadEc(key, kid, out JwtKeyProblem problem);
            if (ec == null) return Problem(alg, problem);
            int wanted = alg switch { "ES256" => 256, "ES384" => 384, _ => 521 };
            if (ec.KeySize != wanted) return Problem(alg, JwtKeyProblem.WrongCurve);
            // JWS signatures are the raw r||s numbers (IEEE P1363), not the DER form .NET uses by default.
            return Result(alg, ec.VerifyData(data, signature, HashFor(alg), DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        private static HashAlgorithmName HashFor(string alg) => alg[2..] switch
        {
            "256" => HashAlgorithmName.SHA256,
            "384" => HashAlgorithmName.SHA384,
            _ => HashAlgorithmName.SHA512,
        };

        private static RSA? LoadRsa(string key, string kid, out JwtKeyProblem problem)
        {
            problem = JwtKeyProblem.None;
            if (LooksLikeJson(key))
            {
                var jwk = SelectJwk(key, kid, out bool kidMissing);
                if (kidMissing) { problem = JwtKeyProblem.KidNotFound; return null; }
                if (jwk is not { } j) { problem = JwtKeyProblem.BadJwk; return null; }
                if (Text(j, "kty") != "RSA") { problem = JwtKeyProblem.WrongKeyType; return null; }
                if (!TryBase64Url(Text(j, "n"), out byte[] n) || !TryBase64Url(Text(j, "e"), out byte[] e) || n.Length == 0 || e.Length == 0) { problem = JwtKeyProblem.BadJwk; return null; }
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = n, Exponent = e });
                return rsa;
            }

            try
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(key);
                return rsa;
            }
            catch (ArgumentException) { /* not an RSA key block: maybe a certificate or another kind of key */ }
            catch (CryptographicException) { }

            try
            {
                using var cert = X509Certificate2.CreateFromPem(key);
                var fromCert = cert.GetRSAPublicKey();
                if (fromCert != null) return fromCert;
                problem = JwtKeyProblem.WrongKeyType;
                return null;
            }
            catch (CryptographicException) { }

            problem = IsEcPem(key) ? JwtKeyProblem.WrongKeyType : JwtKeyProblem.NotRecognized;
            return null;
        }

        private static ECDsa? LoadEc(string key, string kid, out JwtKeyProblem problem)
        {
            problem = JwtKeyProblem.None;
            if (LooksLikeJson(key))
            {
                var jwk = SelectJwk(key, kid, out bool kidMissing);
                if (kidMissing) { problem = JwtKeyProblem.KidNotFound; return null; }
                if (jwk is not { } j) { problem = JwtKeyProblem.BadJwk; return null; }
                if (Text(j, "kty") != "EC") { problem = JwtKeyProblem.WrongKeyType; return null; }
                ECCurve? curve = Text(j, "crv") switch
                {
                    "P-256" => ECCurve.NamedCurves.nistP256,
                    "P-384" => ECCurve.NamedCurves.nistP384,
                    "P-521" => ECCurve.NamedCurves.nistP521,
                    _ => null,
                };
                if (curve is not { } c) { problem = JwtKeyProblem.WrongCurve; return null; }
                if (!TryBase64Url(Text(j, "x"), out byte[] x) || !TryBase64Url(Text(j, "y"), out byte[] y) || x.Length == 0 || y.Length == 0) { problem = JwtKeyProblem.BadJwk; return null; }
                var ec = ECDsa.Create();
                ec.ImportParameters(new ECParameters { Curve = c, Q = new ECPoint { X = x, Y = y } });
                return ec;
            }

            try
            {
                var ec = ECDsa.Create();
                ec.ImportFromPem(key);
                return ec;
            }
            catch (ArgumentException) { }
            catch (CryptographicException) { }

            try
            {
                using var cert = X509Certificate2.CreateFromPem(key);
                var fromCert = cert.GetECDsaPublicKey();
                if (fromCert != null) return fromCert;
                problem = JwtKeyProblem.WrongKeyType;
                return null;
            }
            catch (CryptographicException) { }

            problem = IsRsaPem(key) ? JwtKeyProblem.WrongKeyType : JwtKeyProblem.NotRecognized;
            return null;
        }

        private static bool IsRsaPem(string key)
        {
            try { using var rsa = RSA.Create(); rsa.ImportFromPem(key); return true; }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { return false; }
        }

        private static bool IsEcPem(string key)
        {
            try { using var ec = ECDsa.Create(); ec.ImportFromPem(key); return true; }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { return false; }
        }

        /// <summary>The JWK to use: the key itself, or in a key set (JWKS) the one with the token's kid (else the only key).</summary>
        private static JsonElement? SelectJwk(string json, string kid, out bool kidMissing)
        {
            kidMissing = false;
            if (!TryParse(Encoding.UTF8.GetBytes(json), out JsonElement root) || root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) return root;

            var list = keys.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.Object).ToList();
            if (list.Count == 0) return null;
            if (kid.Length > 0)
            {
                foreach (var k in list) if (Text(k, "kid") == kid) return k;
                kidMissing = true;
                return null;
            }
            if (list.Count == 1) return list[0];
            kidMissing = true;   // several keys and nothing says which one
            return null;
        }

        /// <summary>Standard or URL-safe Base64, with or without padding.</summary>
        private static bool TryBase64Any(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            string s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd('=');
            if (s.Length == 0) return false;
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 1: return false;
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            try { bytes = Convert.FromBase64String(s); return bytes.Length > 0; }
            catch (FormatException) { return false; }
        }

    }
}
