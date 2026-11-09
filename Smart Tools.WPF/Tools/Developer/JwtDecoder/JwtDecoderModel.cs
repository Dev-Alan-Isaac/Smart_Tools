namespace Smart_Tools.WPF.Models
{
    /// <summary>Why a token could not be read. <see cref="None"/> = it decoded.</summary>
    public enum JwtProblem { None, Empty, WrongPartCount, Encrypted, BadEncoding, HeaderNotJson, PayloadNotJson }

    /// <summary>Where the token stands against the clock (and its exp / nbf claims).</summary>
    public enum JwtTimeState { NoExpiry, Valid, Expired, NotYetValid }

    /// <summary>What checking the signature came to.</summary>
    public enum JwtVerifyState
    {
        /// <summary>There is no readable token to check.</summary>
        NoToken,
        /// <summary>No key was given.</summary>
        NoKey,
        /// <summary>The signature matches the key.</summary>
        Valid,
        /// <summary>The key is fine but the signature does not match it: the token was changed or signed by someone else.</summary>
        Invalid,
        /// <summary>alg "none": there is no signature to check, and such a token is never accepted.</summary>
        Unsecured,
        /// <summary>The key could not be used (see <see cref="JwtKeyProblem"/>).</summary>
        KeyProblem,
        /// <summary>An algorithm this tool cannot check (for example EdDSA).</summary>
        Unsupported
    }

    /// <summary>Why a key could not be used. The view turns it into a sentence in the user's language.</summary>
    public enum JwtKeyProblem
    {
        None,
        /// <summary>Not a PEM block, a certificate or a JWK.</summary>
        NotRecognized,
        /// <summary>A PEM / certificate / JWK was given for an HS algorithm: a secret is needed (and a public key must never be used as one).</summary>
        PublicKeyForHmac,
        /// <summary>A secret was given for an RS / PS / ES algorithm: a public key or certificate is needed.</summary>
        SecretForPublicKey,
        /// <summary>The key is of another kind than the algorithm (an EC key for RS256, an RSA key for ES256...).</summary>
        WrongKeyType,
        /// <summary>An EC key on another curve than the algorithm needs (ES256 needs P-256).</summary>
        WrongCurve,
        /// <summary>The Base64 secret is not valid Base64.</summary>
        BadBase64,
        /// <summary>A key set (JWKS) has no key with the token's kid.</summary>
        KidNotFound,
        /// <summary>A JWK with missing or broken fields.</summary>
        BadJwk
    }

    /// <summary>The answer of <see cref="Controllers.JwtDecoderManager.Verify"/>.</summary>
    public sealed record JwtVerification(JwtVerifyState State, JwtKeyProblem Problem = JwtKeyProblem.None, string Algorithm = "");

    /// <summary>One claim of the payload. <see cref="Time"/> is set for exp, nbf, and iat (and any other numeric claim with those names).</summary>
    public sealed class JwtClaim
    {
        public string Name { get; init; } = "";

        /// <summary>The value as text (strings without quotes; objects and arrays as JSON).</summary>
        public string Value { get; init; } = "";

        public DateTimeOffset? Time { get; init; }

        /// <summary>What the claim means (sub, iss, ...), or for a time claim the local date and how far away it is. Filled by the view (it knows the language).</summary>
        public string Meaning { get; set; } = "";
    }

    /// <summary>The result of reading a token. Nothing here says the token is genuine: the signature is checked separately by Verify, and only when a key is given.</summary>
    public sealed class JwtDecoded
    {
        public JwtProblem Problem { get; init; }

        public string HeaderJson { get; init; } = "";
        public string PayloadJson { get; init; } = "";

        /// <summary>The signature part as it appears in the token (base64url), empty for an unsecured token.</summary>
        public string Signature { get; init; } = "";

        public int SignatureBytes { get; init; }

        public string Algorithm { get; init; } = "";
        public string TokenType { get; init; } = "";
        public string KeyId { get; init; } = "";

        /// <summary>alg "none": anyone can create such a token, so it proves nothing.</summary>
        public bool IsUnsecured { get; init; }

        public DateTimeOffset? IssuedAt { get; init; }
        public DateTimeOffset? NotBefore { get; init; }
        public DateTimeOffset? Expires { get; init; }
        public JwtTimeState TimeState { get; init; }

        public List<JwtClaim> Claims { get; init; } = new();

        public bool Ok => Problem == JwtProblem.None;
    }
}
