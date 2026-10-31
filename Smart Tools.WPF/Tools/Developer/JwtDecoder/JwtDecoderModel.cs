namespace Smart_Tools.WPF.Models
{
    /// <summary>Why a token could not be read. <see cref="None"/> = it decoded.</summary>
    public enum JwtProblem { None, Empty, WrongPartCount, Encrypted, BadEncoding, HeaderNotJson, PayloadNotJson }

    /// <summary>Where the token stands against the clock (and its exp / nbf claims).</summary>
    public enum JwtTimeState { NoExpiry, Valid, Expired, NotYetValid }

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

    /// <summary>The result of reading a token. Nothing here says the token is genuine: the signature is never checked.</summary>
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
