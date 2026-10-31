using Smart_Tools.WPF.Models;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The encrypted file format. Both versions are AES-256-CBC with
    /// encrypt-then-MAC (HMAC-SHA256), and the tag is always checked over
    /// the whole file BEFORE anything is decrypted (no padding oracle, and
    /// a changed or damaged file is refused instead of half-decrypted).
    ///
    /// STF2 (older files, still decrypted, no longer written):
    ///   [4 "STF2"][16 salt][16 IV][32 tag][ciphertext]
    ///   PBKDF2-SHA256, 100,000 rounds, 64 bytes = AES key + MAC key.
    ///   Tag = HMAC(salt + IV + ciphertext).
    ///
    /// STF3 (written now):
    ///   [4 "STF3"][4 rounds, little-endian][16 password salt][16 file salt]
    ///   [16 IV][16 password check][32 tag][ciphertext]
    ///   master = PBKDF2-SHA256(password, password salt, rounds (600,000), 32 bytes)
    ///   AES key / MAC key / check = HKDF-SHA256(master, file salt, "STF3 aes|mac|check")
    ///   Tag = HMAC(the 72 header bytes before the tag + ciphertext).
    ///
    /// Why STF3: 600,000 rounds is today's OWASP figure for PBKDF2-SHA256
    /// (STF2's 100,000 is well below it). Paying that per file would add
    /// hours to a 10,000-file folder, so the slow step runs once per batch
    /// (one password salt per run) and each file still gets its own keys
    /// from its own random file salt. Guessing a password still costs the
    /// full 600,000 rounds. HKDF (not a longer PBKDF2 output) means the
    /// check value doesn't give a cheaper way to test guesses. The check
    /// lets a wrong password fail at once instead of after reading a whole
    /// multi-GB file. The round count is stored so it can be raised later
    /// without breaking old files.
    /// </summary>
    public sealed partial class EncryptManager
    {
        private static readonly byte[] MagicV2 = Encoding.ASCII.GetBytes("STF2");
        private static readonly byte[] MagicV3 = Encoding.ASCII.GetBytes("STF3");

        private const int SaltSize = 16;
        private const int IvSize = 16;
        private const int KeySize = 32;       // AES-256
        private const int TagSize = 32;       // HMAC-SHA256
        private const int CheckSize = 16;
        private const int BlockSize = 16;
        private const int CopyBufferSize = 1 << 20; // 1 MB - also how quickly Cancel takes effect

        /// <summary>STF3 bytes before the tag (all of them are authenticated).</summary>
        internal const int V3AuthHeaderSize = 4 + 4 + SaltSize + SaltSize + IvSize + CheckSize; // 72
        internal const int V3HeaderSize = V3AuthHeaderSize + TagSize;                          // 104
        internal const int V2HeaderSize = 4 + SaltSize + IvSize + TagSize;                     // 68

        public const int LegacyIterations = 100_000;
        public const int DefaultIterations = 600_000;

        /// <summary>A file asking for more rounds than this is refused (it would hang the app).</summary>
        public const int MaxIterations = 10_000_000;

        // =================================================================
        //  Errors the user can act on
        // =================================================================

        public enum CryptoFailure
        {
            NotEncrypted,
            WrongPassword,
            Damaged,

            /// <summary>STF2 can't tell these apart.</summary>
            WrongPasswordOrDamaged
        }

        public sealed class EncryptionFailedException : Exception
        {
            public CryptoFailure Failure { get; }

            public EncryptionFailedException(CryptoFailure failure) : base(Describe(failure)) => Failure = failure;

            public static string Describe(CryptoFailure failure) => failure switch
            {
                CryptoFailure.NotEncrypted => "Not a file encrypted by Smart Tools.",
                CryptoFailure.WrongPassword => "Wrong password.",
                CryptoFailure.Damaged => "The file is damaged or was changed after it was encrypted - nothing was decrypted.",
                _ => "Wrong password, or the file is damaged - nothing was decrypted.",
            };
        }

        // =================================================================
        //  Keys
        // =================================================================

        /// <summary>
        /// Holds the password for one run and remembers the slow PBKDF2
        /// results, so a batch derives once per password salt. Wiped when
        /// the run ends.
        /// </summary>
        public sealed class KeyRing : IDisposable
        {
            private readonly string _password;
            private readonly object _gate = new();
            private readonly Dictionary<string, Lazy<byte[]>> _masters = new();

            /// <summary>Salt and rounds for files this run encrypts.</summary>
            public byte[] EncryptSalt { get; }
            public int EncryptIterations { get; }

            public KeyRing(string password, int encryptIterations = DefaultIterations)
            {
                if (string.IsNullOrEmpty(password)) throw new ArgumentException("A password is needed.", nameof(password));
                _password = password;
                EncryptIterations = Math.Clamp(encryptIterations, 1, MaxIterations);
                EncryptSalt = RandomNumberGenerator.GetBytes(SaltSize);
            }

            internal string Password => _password;

            /// <summary>PBKDF2 once per (salt, rounds) - other threads asking for the same one wait for it.</summary>
            public byte[] Master(byte[] salt, int iterations)
            {
                string key = iterations + ":" + Convert.ToHexString(salt);
                Lazy<byte[]> lazy;
                lock (_gate)
                {
                    if (!_masters.TryGetValue(key, out lazy!))
                    {
                        byte[] saltCopy = (byte[])salt.Clone();
                        lazy = new Lazy<byte[]>(() => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(_password), saltCopy, iterations, HashAlgorithmName.SHA256, KeySize));
                        _masters[key] = lazy;
                    }
                }
                return lazy.Value;
            }

            public int CachedKeys
            {
                get
                {
                    lock (_gate) return _masters.Count(m => m.Value.IsValueCreated);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    foreach (var m in _masters.Values.Where(m => m.IsValueCreated))
                        CryptographicOperations.ZeroMemory(m.Value);
                    _masters.Clear();
                }
            }
        }

        private readonly record struct V3Keys(byte[] Aes, byte[] Mac, byte[] Check) : IDisposable
        {
            public void Dispose()
            {
                CryptographicOperations.ZeroMemory(Aes);
                CryptographicOperations.ZeroMemory(Mac);
            }
        }

        private static V3Keys DeriveV3(byte[] master, byte[] fileSalt) => new(
            HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeySize, fileSalt, Encoding.ASCII.GetBytes("STF3 aes")),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeySize, fileSalt, Encoding.ASCII.GetBytes("STF3 mac")),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, master, CheckSize, fileSalt, Encoding.ASCII.GetBytes("STF3 check")));

        /// <summary>STF2's derivation, unchanged: one 64-byte PBKDF2 output split in two.</summary>
        private static (byte[] Aes, byte[] Mac) DeriveV2(string password, byte[] salt)
        {
            byte[] material = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, LegacyIterations, HashAlgorithmName.SHA256, KeySize * 2);
            var keys = (material[..KeySize], material[KeySize..]);
            CryptographicOperations.ZeroMemory(material);
            return keys;
        }

        // =================================================================
        //  Reading the header
        // =================================================================

        /// <summary>STF2/STF3 from the first 4 bytes (None if unreadable or too short).</summary>
        public static EncryptedFormat PeekFormat(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16);
                Span<byte> magic = stackalloc byte[4];
                return stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 ? FormatOf(magic) : EncryptedFormat.None;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return EncryptedFormat.None;
            }
        }

        public static EncryptedFormat FormatOf(ReadOnlySpan<byte> magic) =>
            magic.SequenceEqual(MagicV3) ? EncryptedFormat.Stf3 :
            magic.SequenceEqual(MagicV2) ? EncryptedFormat.Stf2 : EncryptedFormat.None;

        /// <summary>Encrypted size for a plain file: header + PKCS7-padded ciphertext.</summary>
        public static long EncryptedLength(long plainLength) => V3HeaderSize + (plainLength / BlockSize + 1) * BlockSize;

        // =================================================================
        //  Encrypt (always STF3)
        // =================================================================

        /// <summary>
        /// Encrypts <paramref name="input"/> into <paramref name="output"/>
        /// (which must be seekable: the tag is written last into the space
        /// kept for it, so GB files aren't read twice).
        /// </summary>
        public static void EncryptStream(Stream input, Stream output, KeyRing keys, Action<long>? onBytes, CancellationToken token)
        {
            byte[] fileSalt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] iv = RandomNumberGenerator.GetBytes(IvSize);
            byte[] master = keys.Master(keys.EncryptSalt, keys.EncryptIterations);
            using var k = DeriveV3(master, fileSalt);

            byte[] header = new byte[V3AuthHeaderSize];
            MagicV3.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), keys.EncryptIterations);
            keys.EncryptSalt.CopyTo(header, 8);
            fileSalt.CopyTo(header, 8 + SaltSize);
            iv.CopyTo(header, 8 + SaltSize * 2);
            k.Check.CopyTo(header, 8 + SaltSize * 2 + IvSize);

            long start = output.Position;
            output.Write(header);
            output.Write(new byte[TagSize]);   // filled in at the end

            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, k.Mac);
            hmac.AppendData(header);

            using (var aes = Aes.Create())
            {
                aes.Key = k.Aes;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using var tee = new HmacTeeStream(output, hmac);
                using var encryptor = aes.CreateEncryptor();
                using var crypto = new CryptoStream(tee, encryptor, CryptoStreamMode.Write, leaveOpen: true);
                Copy(input, crypto, onBytes, token);
                crypto.FlushFinalBlock();
            }

            byte[] tag = hmac.GetHashAndReset();
            long end = output.Position;
            output.Position = start + V3AuthHeaderSize;
            output.Write(tag);
            output.Position = end;
        }

        // =================================================================
        //  Decrypt (STF2 and STF3)
        // =================================================================

        /// <summary>
        /// Checks the tag over the whole file first, then decrypts into
        /// <paramref name="output"/>. <paramref name="onBytes"/> counts
        /// bytes read by both passes (so a file is 2 x its length).
        /// <paramref name="input"/> must be seekable and should be opened
        /// without write sharing so it can't change between the passes.
        /// </summary>
        public static EncryptedFormat DecryptStream(Stream input, Stream output, KeyRing keys, Action<long>? onBytes, CancellationToken token)
        {
            Span<byte> magic = stackalloc byte[4];
            if (input.ReadAtLeast(magic, 4, throwOnEndOfStream: false) != 4)
                throw new EncryptionFailedException(CryptoFailure.NotEncrypted);
            var format = FormatOf(magic);

            switch (format)
            {
                case EncryptedFormat.Stf3:
                    DecryptV3(input, output, keys, onBytes, token);
                    break;
                case EncryptedFormat.Stf2:
                    DecryptV2(input, output, keys.Password, onBytes, token);
                    break;
                default:
                    throw new EncryptionFailedException(CryptoFailure.NotEncrypted);
            }
            return format;
        }

        private static void DecryptV3(Stream input, Stream output, KeyRing keys, Action<long>? onBytes, CancellationToken token)
        {
            byte[] header = new byte[V3AuthHeaderSize];
            MagicV3.CopyTo(header, 0);
            ReadExact(input, header.AsSpan(4));
            byte[] storedTag = new byte[TagSize];
            ReadExact(input, storedTag);

            int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (iterations < 1 || iterations > MaxIterations) throw new EncryptionFailedException(CryptoFailure.Damaged);

            byte[] passwordSalt = header.AsSpan(8, SaltSize).ToArray();
            byte[] fileSalt = header.AsSpan(8 + SaltSize, SaltSize).ToArray();
            byte[] iv = header.AsSpan(8 + SaltSize * 2, IvSize).ToArray();
            byte[] storedCheck = header.AsSpan(8 + SaltSize * 2 + IvSize, CheckSize).ToArray();

            long cipherStart = input.Position;
            long cipherLength = input.Length - cipherStart;
            if (cipherLength < BlockSize || cipherLength % BlockSize != 0) throw new EncryptionFailedException(CryptoFailure.Damaged);

            using var k = DeriveV3(keys.Master(passwordSalt, iterations), fileSalt);

            // Quick password check - no need to read a 4 GB file to say "wrong password".
            if (!CryptographicOperations.FixedTimeEquals(k.Check, storedCheck))
                throw new EncryptionFailedException(CryptoFailure.WrongPassword);

            using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, k.Mac))
            {
                hmac.AppendData(header);
                HashRest(input, hmac, onBytes, token);
                if (!CryptographicOperations.FixedTimeEquals(hmac.GetHashAndReset(), storedTag))
                    throw new EncryptionFailedException(CryptoFailure.Damaged);
            }

            input.Position = cipherStart;
            DecryptRest(input, output, k.Aes, iv, onBytes, token);
        }

        private static void DecryptV2(Stream input, Stream output, string password, Action<long>? onBytes, CancellationToken token)
        {
            byte[] salt = new byte[SaltSize];
            byte[] iv = new byte[IvSize];
            byte[] storedTag = new byte[TagSize];
            ReadExact(input, salt);
            ReadExact(input, iv);
            ReadExact(input, storedTag);

            long cipherStart = input.Position;
            long cipherLength = input.Length - cipherStart;
            if (cipherLength < BlockSize || cipherLength % BlockSize != 0) throw new EncryptionFailedException(CryptoFailure.Damaged);

            var (aesKey, macKey) = DeriveV2(password, salt);
            try
            {
                using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey))
                {
                    hmac.AppendData(salt);
                    hmac.AppendData(iv);
                    HashRest(input, hmac, onBytes, token);
                    if (!CryptographicOperations.FixedTimeEquals(hmac.GetHashAndReset(), storedTag))
                        throw new EncryptionFailedException(CryptoFailure.WrongPasswordOrDamaged);
                }

                input.Position = cipherStart;
                DecryptRest(input, output, aesKey, iv, onBytes, token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(aesKey);
                CryptographicOperations.ZeroMemory(macKey);
            }
        }

        /// <summary>
        /// Re-reads an encrypted file and checks its tag (and password check)
        /// without writing anything - used before deleting an original, so
        /// the only copy is never removed on the strength of a bad write.
        /// </summary>
        public static void VerifyEncrypted(string path, KeyRing keys, Action<long>? onBytes, CancellationToken token)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Span<byte> magic = stackalloc byte[4];
            if (input.ReadAtLeast(magic, 4, throwOnEndOfStream: false) != 4 || FormatOf(magic) != EncryptedFormat.Stf3)
                throw new EncryptionFailedException(CryptoFailure.Damaged);

            byte[] header = new byte[V3AuthHeaderSize];
            MagicV3.CopyTo(header, 0);
            ReadExact(input, header.AsSpan(4));
            byte[] storedTag = new byte[TagSize];
            ReadExact(input, storedTag);
            int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (iterations < 1 || iterations > MaxIterations) throw new EncryptionFailedException(CryptoFailure.Damaged);

            using var k = DeriveV3(keys.Master(header.AsSpan(8, SaltSize).ToArray(), iterations), header.AsSpan(8 + SaltSize, SaltSize).ToArray());
            if (!CryptographicOperations.FixedTimeEquals(k.Check, header.AsSpan(8 + SaltSize * 2 + IvSize, CheckSize)))
                throw new EncryptionFailedException(CryptoFailure.Damaged);

            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, k.Mac);
            hmac.AppendData(header);
            HashRest(input, hmac, onBytes, token);
            if (!CryptographicOperations.FixedTimeEquals(hmac.GetHashAndReset(), storedTag))
                throw new EncryptionFailedException(CryptoFailure.Damaged);
        }

        // =================================================================
        //  Streaming helpers
        // =================================================================

        private static void HashRest(Stream input, IncrementalHash hmac, Action<long>? onBytes, CancellationToken token)
        {
            byte[] buffer = new byte[CopyBufferSize];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hmac.AppendData(buffer, 0, read);
                onBytes?.Invoke(read);
            }
        }

        private static void DecryptRest(Stream input, Stream output, byte[] key, byte[] iv, Action<long>? onBytes, CancellationToken token)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            using var counted = new CountingReadStream(input, onBytes);
            using var crypto = new CryptoStream(counted, decryptor, CryptoStreamMode.Read, leaveOpen: true);
            Copy(crypto, output, null, token);
        }

        private static void Copy(Stream source, Stream destination, Action<long>? onBytes, CancellationToken token)
        {
            byte[] buffer = new byte[CopyBufferSize];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                onBytes?.Invoke(read);
            }
        }

        private static void ReadExact(Stream stream, Span<byte> buffer)
        {
            if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
                throw new EncryptionFailedException(CryptoFailure.Damaged);
        }

        /// <summary>Passes writes through while feeding the same bytes to the HMAC (no second pass over the ciphertext).</summary>
        private sealed class HmacTeeStream : Stream
        {
            private readonly Stream _inner;
            private readonly IncrementalHash _hmac;

            public HmacTeeStream(Stream inner, IncrementalHash hmac)
            {
                _inner = inner;
                _hmac = hmac;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _hmac.AppendData(buffer, offset, count);
                _inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _hmac.AppendData(buffer);
                _inner.Write(buffer);
            }

            public override void Flush() => _inner.Flush();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>Counts ciphertext read during decryption, for the progress bar.</summary>
        private sealed class CountingReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly Action<long>? _onBytes;

            public CountingReadStream(Stream inner, Action<long>? onBytes)
            {
                _inner = inner;
                _onBytes = onBytes;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                if (read > 0) _onBytes?.Invoke(read);
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                int read = _inner.Read(buffer);
                if (read > 0) _onBytes?.Invoke(read);
                return read;
            }

            public override void Flush() { }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
