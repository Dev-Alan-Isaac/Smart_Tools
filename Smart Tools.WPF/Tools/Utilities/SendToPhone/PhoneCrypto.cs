using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The end-to-end encryption of Send to Phone. The session key (32 random bytes) travels only in the
    /// QR code's "#" part, which a browser never sends to a server, so it reaches the phone out of band.
    /// From it, HKDF-SHA256 makes one AES-256-GCM key per file and direction. A file is cut into 1 MiB
    /// chunks; every chunk is sealed with the chunk number as nonce and a header (file id, chunk number,
    /// last-chunk flag, total size) as authenticated data, so chunks cannot be swapped, repeated, dropped
    /// or cut short without the check failing. The phone page (app.js) implements the same layout
    /// with WebCrypto; keep both in step.
    /// </summary>
    public static class PhoneCrypto
    {
        public const int ChunkSize = 1 << 20;
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int TagSize = 16;

        public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);
        public static string NewHexId(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

        public static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string text)
        {
            string s = text.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Convert.FromBase64String(s);
        }

        /// <summary>The key for one file in one direction ("dl" = PC to phone, "ul" = phone to PC).</summary>
        public static byte[] FileKey(byte[] sessionKey, string direction, byte[] fileId) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, KeySize, fileId, Encoding.UTF8.GetBytes("stp-v1-" + direction));

        /// <summary>The key that seals the list of what the PC offers (names included).</summary>
        public static byte[] StateKey(byte[] sessionKey) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, KeySize, Encoding.UTF8.GetBytes("state"), Encoding.UTF8.GetBytes("stp-v1-state"));

        private static byte[] Nonce(long index)
        {
            var n = new byte[NonceSize];
            for (int i = 0; i < 8; i++) n[NonceSize - 1 - i] = (byte)(index >> (8 * i));
            return n;
        }

        /// <summary>file id (16) + chunk number (8, big-endian) + last-chunk flag (1) + total size (8, big-endian).</summary>
        public static byte[] ChunkHeader(byte[] fileId, long index, bool last, long totalSize)
        {
            var h = new byte[fileId.Length + 17];
            Array.Copy(fileId, h, fileId.Length);
            int p = fileId.Length;
            for (int i = 0; i < 8; i++) h[p + 7 - i] = (byte)(index >> (8 * i));
            h[p + 8] = last ? (byte)1 : (byte)0;
            for (int i = 0; i < 8; i++) h[p + 16 - i] = (byte)(totalSize >> (8 * i));
            return h;
        }

        /// <summary>Ciphertext followed by the 16-byte tag.</summary>
        public static byte[] SealChunk(byte[] fileKey, byte[] fileId, long index, bool last, long totalSize, ReadOnlySpan<byte> plain)
        {
            var output = new byte[plain.Length + TagSize];
            using var aes = new AesGcm(fileKey, TagSize);
            aes.Encrypt(Nonce(index), plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length, TagSize), ChunkHeader(fileId, index, last, totalSize));
            return output;
        }

        /// <summary>The plain chunk, or null when it was changed, is out of place or the key is wrong.</summary>
        public static byte[]? OpenChunk(byte[] fileKey, byte[] fileId, long index, bool last, long totalSize, ReadOnlySpan<byte> sealedChunk)
        {
            if (sealedChunk.Length < TagSize) return null;
            int n = sealedChunk.Length - TagSize;
            var plain = new byte[n];
            try
            {
                using var aes = new AesGcm(fileKey, TagSize);
                aes.Decrypt(Nonce(index), sealedChunk[..n], sealedChunk[n..], plain, ChunkHeader(fileId, index, last, totalSize));
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>A small message with a random nonce in front: nonce (12) + ciphertext + tag (16).</summary>
        public static byte[] SealBlob(byte[] key, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad)
        {
            var output = new byte[NonceSize + plain.Length + TagSize];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(output.AsSpan(0, NonceSize), plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize), aad);
            return output;
        }

        public static byte[]? OpenBlob(byte[] key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> aad)
        {
            if (blob.Length < NonceSize + TagSize) return null;
            int n = blob.Length - NonceSize - TagSize;
            var plain = new byte[n];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(blob[..NonceSize], blob.Slice(NonceSize, n), blob[(NonceSize + n)..], plain, aad);
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        public static bool SameBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);

        public static bool SameText(string a, string b) =>
            a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
