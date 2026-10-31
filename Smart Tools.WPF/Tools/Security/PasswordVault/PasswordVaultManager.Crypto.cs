using Konscious.Security.Cryptography;
using Smart_Tools.WPF.Models;
using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Vault file format "SVLT" v1 (portable: no WPF, so it can be tested in a plain console project).
    ///
    ///   magic "SVLT" (4) | version (1) | memory KiB (4, LE) | iterations (4, LE) | lanes (1) | salt (32)   = 46-byte header
    ///   nonce (12) | AES-GCM tag (16) | ciphertext
    ///
    /// Key = HKDF-SHA256(Argon2id(password, salt, params)), a 256-bit AES-GCM key. The whole header is the
    /// AEAD "associated data", so changing the parameters or salt makes decryption fail. A fresh random nonce
    /// is used for every save. A wrong password and a tampered file both end in the same CryptographicException.
    /// </summary>
    public sealed partial class PasswordVaultManager
    {
        private static readonly byte[] Magic = "SVLT"u8.ToArray();
        private const byte FormatVersion = 1;
        private const int SaltSize = 32, NonceSize = 12, TagSize = 16, KeySize = 32;
        private const int HeaderSize = 4 + 1 + 4 + 4 + 1 + SaltSize;

        // What a new vault uses. Stored in the header, so they can be raised later without breaking old vaults.
        public const int DefaultMemoryKiB = 131_072;   // 128 MiB
        public const int DefaultIterations = 3;
        public const int DefaultLanes = 4;

        // Limits when READING a header, so a damaged/hostile file can't make us allocate GBs or spin for minutes.
        private const int MinMemoryKiB = 32_768, MaxMemoryKiB = 524_288;
        private const int MinIterations = 2, MaxIterations = 12;
        private const int MinLanes = 1, MaxLanes = 16;

        private static readonly byte[] KeyInfo = "SmartTools.PasswordVault.v1.aes-gcm"u8.ToArray();

        public static VaultHeader NewHeader() =>
            new(DefaultMemoryKiB, DefaultIterations, DefaultLanes, RandomNumberGenerator.GetBytes(SaltSize));

        /// <summary>Password (UTF-8, no normalisation) -> 256-bit key. Slow on purpose (memory-hard). Wipes its temporaries.</summary>
        public static byte[] DeriveKey(ReadOnlySpan<char> password, VaultHeader h)
        {
            byte[] pw = new byte[Encoding.UTF8.GetMaxByteCount(password.Length)];
            int n = Encoding.UTF8.GetBytes(password, pw);
            byte[] exact = pw.AsSpan(0, n).ToArray();
            CryptographicOperations.ZeroMemory(pw);
            byte[]? raw = null;
            try
            {
                using var argon = new Argon2id(exact)
                {
                    Salt = h.Salt,
                    MemorySize = h.MemoryKiB,
                    Iterations = h.Iterations,
                    DegreeOfParallelism = h.Lanes,
                };
                raw = argon.GetBytes(KeySize);
                return HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, KeySize, salt: h.Salt, info: KeyInfo);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exact);
                if (raw != null) CryptographicOperations.ZeroMemory(raw);
            }
        }

        private static byte[] WriteHeader(VaultHeader h)
        {
            var b = new byte[HeaderSize];
            Magic.CopyTo(b, 0);
            b[4] = FormatVersion;
            BitConverter.TryWriteBytes(b.AsSpan(5, 4), h.MemoryKiB);
            BitConverter.TryWriteBytes(b.AsSpan(9, 4), h.Iterations);
            b[13] = (byte)h.Lanes;
            h.Salt.CopyTo(b, 14);
            return b;
        }

        /// <summary>Reads and range-checks the header. Null when it isn't a vault file (or the parameters are out of range).</summary>
        public static VaultHeader? ParseHeader(ReadOnlySpan<byte> file)
        {
            if (file.Length < HeaderSize + NonceSize + TagSize) return null;
            if (!file[..4].SequenceEqual(Magic) || file[4] != FormatVersion) return null;
            int mem = BitConverter.ToInt32(file.Slice(5, 4));
            int iter = BitConverter.ToInt32(file.Slice(9, 4));
            int lanes = file[13];
            if (mem is < MinMemoryKiB or > MaxMemoryKiB || iter is < MinIterations or > MaxIterations || lanes is < MinLanes or > MaxLanes) return null;
            return new VaultHeader(mem, iter, lanes, file.Slice(14, SaltSize).ToArray());
        }

        /// <summary>Encrypts <paramref name="plaintext"/> into a complete vault file.</summary>
        public static byte[] Seal(ReadOnlySpan<byte> plaintext, byte[] key, VaultHeader header)
        {
            byte[] head = WriteHeader(header);
            byte[] result = new byte[HeaderSize + NonceSize + TagSize + plaintext.Length];
            head.CopyTo(result, 0);
            var nonce = result.AsSpan(HeaderSize, NonceSize);
            var tag = result.AsSpan(HeaderSize + NonceSize, TagSize);
            var cipher = result.AsSpan(HeaderSize + NonceSize + TagSize);
            RandomNumberGenerator.Fill(nonce);
            using var gcm = new AesGcm(key, TagSize);
            gcm.Encrypt(nonce, plaintext, cipher, tag, head);
            return result;
        }

        /// <summary>Decrypts a vault file with an already derived key. Throws <see cref="CryptographicException"/> for a wrong key or any change to the file.</summary>
        public static byte[] Open(byte[] file, byte[] key)
        {
            if (ParseHeader(file) == null) throw new CryptographicException("Not a vault file.");
            var head = file.AsSpan(0, HeaderSize);
            var nonce = file.AsSpan(HeaderSize, NonceSize);
            var tag = file.AsSpan(HeaderSize + NonceSize, TagSize);
            var cipher = file.AsSpan(HeaderSize + NonceSize + TagSize);
            byte[] plain = new byte[cipher.Length];
            try
            {
                using var gcm = new AesGcm(key, TagSize);
                gcm.Decrypt(nonce, cipher, tag, plain, head);
                return plain;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plain);
                throw;
            }
        }
    }
}
