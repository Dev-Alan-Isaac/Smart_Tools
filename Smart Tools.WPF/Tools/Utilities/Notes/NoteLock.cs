using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The part of a note that a password hides: its text, its formatted version and its source.</summary>
    public sealed class NotePayload
    {
        public string Text { get; set; } = "";
        public string Rich { get; set; } = "";
        public string Source { get; set; } = "";
    }

    /// <summary>
    /// Password lock for one note (no WPF, no I/O).
    /// Key: PBKDF2-SHA256 over the password with a random 16-byte salt per note, 600,000 rounds (OWASP's figure for this hash),
    /// 32 bytes. Cipher: AES-256-GCM with a fresh 12-byte nonce for every save. The stored text is
    /// base64( version 1 | nonce 12 | tag 16 | ciphertext ); a wrong password fails the GCM tag check, so there is no separate
    /// "password hash" to attack. The title is NOT encrypted (the list needs it); pictures are separate files and are not covered.
    /// </summary>
    public static class NoteLock
    {
        public const int SaltSize = 16;
        public const int KeySize = 32;
        public const int Iterations = 600_000;
        private const byte Version = 1;
        private const int NonceSize = 12, TagSize = 16;

        public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

        /// <summary>The key for a password and a note's salt. The caller wipes <paramref name="password"/>.</summary>
        public static byte[] DeriveKey(ReadOnlySpan<char> password, byte[] salt)
        {
            byte[] bytes = new byte[Encoding.UTF8.GetByteCount(password)];
            try
            {
                Encoding.UTF8.GetBytes(password, bytes);
                return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public static string Encrypt(NotePayload payload, byte[] key)
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(payload);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] tag = new byte[TagSize];
            byte[] cipher = new byte[plain.Length];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plain, cipher, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
            byte[] all = new byte[1 + NonceSize + TagSize + cipher.Length];
            all[0] = Version;
            nonce.CopyTo(all, 1);
            tag.CopyTo(all, 1 + NonceSize);
            cipher.CopyTo(all, 1 + NonceSize + TagSize);
            return Convert.ToBase64String(all);
        }

        /// <summary>The note's content, or null when the key is wrong (or the data is damaged).</summary>
        public static NotePayload? TryDecrypt(string? sealedData, byte[] key)
        {
            if (string.IsNullOrEmpty(sealedData)) return null;
            byte[] plain = Array.Empty<byte>();
            try
            {
                byte[] all = Convert.FromBase64String(sealedData);
                if (all.Length < 1 + NonceSize + TagSize || all[0] != Version) return null;
                var nonce = all.AsSpan(1, NonceSize);
                var tag = all.AsSpan(1 + NonceSize, TagSize);
                var cipher = all.AsSpan(1 + NonceSize + TagSize);
                plain = new byte[cipher.Length];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, cipher, tag, plain);
                return JsonSerializer.Deserialize<NotePayload>(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
            {
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }
}
