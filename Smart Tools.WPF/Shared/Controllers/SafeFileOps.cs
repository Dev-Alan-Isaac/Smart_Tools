using System.IO;
using System.Security.Cryptography;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The rules every tool that copies or moves the user's files follows: never overwrite, never remove an
    /// original until the copy has been proven identical, and clean up half-written temp files on any failure.
    /// Organize Files and the Copy Queue both go through here.
    /// </summary>
    public static class SafeFileOps
    {
        private const int BufferSize = 1024 * 1024;

        /// <summary>The path itself when it is free, otherwise "name (2).ext", "name (3).ext"...</summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        /// <summary>Byte-level proof (size, then SHA-256) - a matching name and size alone isn't.</summary>
        public static bool FilesAreIdentical(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
                using var sa = File.OpenRead(a);
                using var sb = File.OpenRead(b);
                return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
            }
            catch (Exception)
            {
                return false;   // can't prove it - treat as different (the safe direction)
            }
        }

        /// <summary>
        /// Same proof, read in chunks so a big file can report progress, pause and be cancelled (cancelling throws
        /// <see cref="OperationCanceledException"/>). Stops at the first difference. Any other failure means "can't prove it".
        /// </summary>
        public static bool FilesAreIdentical(string a, string b, Action<long>? onBytes, Action? beforeChunk, CancellationToken token)
        {
            try
            {
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
                using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
                using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
                var bufA = new byte[BufferSize];
                var bufB = new byte[BufferSize];
                int read;
                while ((read = Read(sa, bufA, beforeChunk, token)) > 0)
                {
                    sb.ReadExactly(bufB, 0, read);
                    if (!bufA.AsSpan(0, read).SequenceEqual(bufB.AsSpan(0, read))) return false;
                    onBytes?.Invoke(read);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;   // can't prove it - treat as different (the safe direction)
            }
        }

        /// <summary>SHA-256 of a file as hex, reporting bytes read so a progress bar can follow along.</summary>
        public static string HashFile(string path, Action<long>? onBytes = null, Action? beforeChunk = null, CancellationToken token = default)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = Read(stream, buffer, beforeChunk, token)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                onBytes?.Invoke(read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="temp"/> in chunks and returns the SHA-256 of what was
        /// read from the source. <paramref name="beforeChunk"/> is where a pause blocks; cancelling throws
        /// <see cref="OperationCanceledException"/> and the caller removes the temp file.
        /// </summary>
        public static string CopyToTemp(string source, string temp, Action<long>? onBytes = null, Action? beforeChunk = null, CancellationToken token = default)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
            using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = Read(input, buffer, beforeChunk, token)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                onBytes?.Invoke(read);
            }
            output.Flush(flushToDisk: true);   // so the verify pass reads what is really on the disk
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static int Read(Stream stream, byte[] buffer, Action? beforeChunk, CancellationToken token)
        {
            beforeChunk?.Invoke();
            token.ThrowIfCancellationRequested();
            return stream.Read(buffer, 0, buffer.Length);
        }

        /// <summary>Gives a copy the original's dates (and, for a read-only original, its attribute) once the data is in place.</summary>
        public static void CopyTimestamps(string source, string target)
        {
            var info = new FileInfo(source);
            var attrs = File.GetAttributes(target);
            bool readOnly = attrs.HasFlag(FileAttributes.ReadOnly);
            if (readOnly) File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);   // times can't be set on read-only files
            File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
            try
            {
                File.SetCreationTimeUtc(target, info.CreationTimeUtc);
            }
            catch (Exception)
            {
                // not supported everywhere
            }
            if (readOnly) File.SetAttributes(target, attrs);
        }

        /// <summary>
        /// Runs a file operation, trying again when the file is only busy for a moment (antivirus scanning a fresh
        /// zip, Explorer's preview pane...). Missing files and folders fail at once - waiting won't bring them back.
        /// </summary>
        public static void RetryIo(Action action)
        {
            const int attempts = 5;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < attempts
                    && (ex is UnauthorizedAccessException || ex is IOException && ex is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException))
                {
                    Thread.Sleep(100 << (attempt - 1));   // 100, 200, 400, 800 ms
                }
            }
        }

        /// <summary>Delete a leftover (temp/partial) file; a locked or protected one is left for the next sweep.</summary>
        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        public static bool HasRecycleBin(string path)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool SameVolume(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void ClearReadOnly(string path)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
            catch (Exception)
            {
                // the operation itself will report the real problem
            }
        }

        /// <summary>A leftover temp file is never worth failing over.</summary>
        public static void DeleteQuietly(string? path)
        {
            if (path == null) return;
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}
