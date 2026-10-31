using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>File Encryption - reading folders and running a batch.</summary>
    public sealed partial class EncryptManager
    {
        // =================================================================
        //  Queue expansion
        // =================================================================

        public sealed class QueueResult
        {
            public List<QueuedFile> Files { get; } = new();

            /// <summary>Subfolders that couldn't be opened (the old view dropped the whole folder on the first one).</summary>
            public int Inaccessible { get; set; }

            /// <summary>Junctions/linked folders not followed.</summary>
            public int Links { get; set; }

            /// <summary>Added files/folders that no longer exist.</summary>
            public int Missing { get; set; }
        }

        /// <summary>
        /// Lists the added files and folders off the UI thread (the old view
        /// did it on the UI thread), reading each file's first 4 bytes so
        /// the list can tell encrypted files from plain ones by content,
        /// not by name.
        /// </summary>
        public static Task<QueueResult> ExpandQueueAsync(IEnumerable<EncryptSource> sources, bool includeSubfolders, CancellationToken token = default)
        {
            var list = sources.ToList();
            return Task.Run(() =>
            {
                var result = new QueueResult();
                foreach (var source in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (source.IsFolder)
                    {
                        if (!Directory.Exists(source.FullPath))
                        {
                            result.Missing++;
                            continue;
                        }
                        WalkFolder(source.FullPath, includeSubfolders, result, token);
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(source.FullPath);
                        if (!info.Exists)
                        {
                            result.Missing++;
                            continue;
                        }
                        result.Files.Add(new QueuedFile(info.FullName, null, info.Length, PeekFormat(info.FullName)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        result.Missing++;
                    }
                }
                return result;
            }, token);
        }

        private static void WalkFolder(string root, bool includeSubfolders, QueueResult result, CancellationToken token)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            string rootFull = Path.GetFullPath(root);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = dir.GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    result.Inaccessible++;
                    continue;
                }

                foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    FileAttributes attrs;
                    try
                    {
                        attrs = entry.Attributes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    if (attrs.HasFlag(FileAttributes.System)) continue;

                    if (entry is DirectoryInfo sub)
                    {
                        if (!includeSubfolders) continue;
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            result.Links++;
                            continue;
                        }
                        pending.Push(sub);
                    }
                    else if (entry is FileInfo file)
                    {
                        if (IsLeftover(file.Name)) continue;   // our own work files
                        long length;
                        try
                        {
                            length = file.Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }
                        result.Files.Add(new QueuedFile(file.FullName, rootFull, length, PeekFormat(file.FullName)));
                    }
                }
            }
        }

        // =================================================================
        //  Running
        // =================================================================

        /// <summary>Guards "is the final name free? -> move there" so two files aiming at one name don't race.</summary>
        private static readonly object FinalPathLock = new();

        /// <summary>
        /// Files at once. Encryption is fast per byte (AES-NI); what helps
        /// is overlapping many small files. Big files are disk-bound.
        /// </summary>
        public static int DefaultParallelism() => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

        /// <summary>
        /// Encrypts or decrypts every pending row. Each file is independent:
        /// a failure is recorded on its row and the batch carries on;
        /// cancelling marks the unfinished rows Cancelled. Output is written
        /// to a hidden work file and renamed into place only when complete.
        /// </summary>
        public async Task<EncryptRunResult> RunAsync(
            IReadOnlyList<EncryptItem> items,
            EncryptOptions options,
            IProgress<EncryptProgress>? progress = null,
            CancellationToken token = default)
        {
            var started = Stopwatch.StartNew();
            var work = items.Where(IsPending).ToList();
            var result = new EncryptRunResult { Skipped = items.Count(i => i.Status == EncryptStatus.Skipped) };
            int total = work.Count;
            if (total == 0) return result;
            if (string.IsNullOrEmpty(options.Password)) throw new ArgumentException("A password is needed.", nameof(options));

            foreach (var item in work)
            {
                item.Status = EncryptStatus.Ready;
                item.Progress = 0;
                item.Message = "";
            }

            // Progress = bytes done across the batch (a 4 GB file no longer sits at 0% until it's finished).
            // Decrypting (and verifying before deleting) reads each file twice, so it counts double.
            int passes = options.Mode == EncryptMode.Decrypt || options.DeleteOriginal ? 2 : 1;
            long totalUnits = Math.Max(1, work.Sum(i => Math.Max(1, i.SizeBytes)) * passes);
            long doneUnits = 0;
            int completed = 0;
            long lastReport = -1000;
            string current = "";
            var gate = new object();
            var inFlight = new Dictionary<EncryptItem, double>();

            void Report(bool force)
            {
                lock (gate)
                {
                    long now = started.ElapsedMilliseconds;
                    if (!force && now - lastReport < 100) return;
                    lastReport = now;
                    double overall = Math.Min(100, doneUnits * 100.0 / totalUnits);
                    double currentPercent = inFlight.Count > 0 ? inFlight.Values.Max() : 0;
                    progress?.Report(new EncryptProgress(completed, total, current, currentPercent, overall));
                }
            }

            using var keys = new KeyRing(options.Password, options.Iterations);
            int parallel = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : DefaultParallelism();
            using var throttle = new SemaphoreSlim(parallel);

            var tasks = work.Select(async item =>
            {
                try
                {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    item.Status = EncryptStatus.Cancelled;
                    item.Message = "Not started - the run was cancelled.";
                    return;
                }

                long itemUnits = Math.Max(1, item.SizeBytes) * passes;
                long counted = 0;
                try
                {
                    lock (gate)
                    {
                        inFlight[item] = 0;
                        current = item.Name;
                    }
                    Report(true);

                    await Task.Run(() => ProcessOne(item, options, keys, bytes =>
                    {
                        // Clamp: the ciphertext is a little longer than the plain file.
                        long add = Math.Min(bytes, itemUnits - counted);
                        if (add <= 0) return;
                        counted += add;
                        double percent = Math.Min(100, counted * 100.0 / itemUnits);
                        lock (gate)
                        {
                            doneUnits += add;
                            inFlight[item] = percent;
                        }
                        item.Progress = percent;
                        Report(false);
                    }, token), CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        inFlight.Remove(item);
                        doneUnits += itemUnits - counted;   // skipped/failed files count as finished work
                        completed++;
                        switch (item.Status)
                        {
                            case EncryptStatus.Done:
                                result.Done++;
                                result.Bytes += item.SizeBytes;
                                break;
                            case EncryptStatus.Cancelled:
                                result.Cancelled++;
                                break;
                            case EncryptStatus.Skipped:
                                result.Skipped++;
                                break;
                            default:
                                result.Failed++;
                                break;
                        }
                    }
                    Report(true);
                    throttle.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Includes rows that never got a slot.
            result.Cancelled = work.Count(i => i.Status == EncryptStatus.Cancelled);
            result.Elapsed = started.Elapsed;
            return result;
        }

        private static void ProcessOne(EncryptItem item, EncryptOptions o, KeyRing keys, Action<long> onBytes, CancellationToken token)
        {
            string? temp = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(item.FullPath)) throw new FileNotFoundException("The file no longer exists.");

                // Checked again at run time - the file may have changed since it was listed.
                var format = PeekFormat(item.FullPath);
                if ((o.Mode == EncryptMode.Encrypt) == (format != EncryptedFormat.None))
                {
                    item.Status = EncryptStatus.Skipped;
                    item.Message = SkipReason(o.Mode);
                    return;
                }

                item.Status = EncryptStatus.Working;
                string folder = Path.GetDirectoryName(Path.GetFullPath(item.FullPath))!;
                temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N")[..12] + PartExtension);

                // No write sharing on the input: it can't change between checking the tag and decrypting.
                using (var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                {
                    TryHide(temp);
                    if (o.Mode == EncryptMode.Encrypt)
                        EncryptStream(input, output, keys, onBytes, token);
                    else
                        DecryptStream(input, output, keys, onBytes, token);
                    output.Flush(flushToDisk: true);
                }

                // Before the only other copy goes, make sure what was written reads back correctly.
                if (o.Mode == EncryptMode.Encrypt && o.DeleteOriginal)
                    VerifyEncrypted(temp, keys, onBytes, token);

                token.ThrowIfCancellationRequested();

                string final;
                lock (FinalPathLock)
                {
                    final = UniqueOutputPath(folder, item.Name, o.Mode, File.Exists);
                    File.Move(temp, final);
                }
                temp = null;
                TryUnhide(final);

                item.OutputPath = final;
                item.OutputName = Path.GetFileName(final);
                item.Progress = 100;
                item.Status = EncryptStatus.Done;
                item.Message = "Saved as " + final;

                if (o.DeleteOriginal)
                {
                    try
                    {
                        bool bin = o.DeleteToRecycleBin && HasRecycleBin(item.FullPath);
                        DeleteOriginal(item.FullPath, bin);
                        item.Message = $"Saved as {final} - original {(bin ? "moved to the Recycle Bin" : "deleted")}.";
                    }
                    catch (Exception ex)
                    {
                        item.Message = $"Saved as {final}, but the original couldn't be deleted: {ex.Message}";
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                item.Status = EncryptStatus.Cancelled;
                item.Message = "Cancelled - nothing was saved for this file.";
            }
            catch (EncryptionFailedException ex)
            {
                item.Status = EncryptStatus.Failed;
                item.Message = ex.Message;
            }
            catch (Exception ex)
            {
                item.Status = EncryptStatus.Failed;
                item.Message = ex switch
                {
                    UnauthorizedAccessException => "Access denied - the file or folder is read-only or protected.",
                    IOException io when IsDiskFull(io) => "The disk is full.",
                    IOException io when IsInUse(io) => "The file is open in another program.",
                    _ => ex.Message,
                };
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // best effort - the listing ignores leftovers
                    }
                }
            }
        }

        private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27;   // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        private static bool IsInUse(IOException ex) => (ex.HResult & 0xFFFF) is 0x20 or 0x21;      // sharing / lock violation

        private static void TryHide(string path)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
            catch (Exception)
            {
                // cosmetic
            }
        }

        private static void TryUnhide(string path)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
            }
            catch (Exception)
            {
                // cosmetic
            }
        }

        // =================================================================
        //  Deleting originals
        // =================================================================

        /// <summary>Fixed local drives have a Recycle Bin; network and removable drives don't.</summary>
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

        private static void DeleteOriginal(string path, bool toRecycleBin)
        {
            if (!File.Exists(path)) return;
            if (toRecycleBin)
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }
    }
}
