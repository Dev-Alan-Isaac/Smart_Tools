using Smart_Tools.WPF.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Secure File Shredder: overwrites each file's bytes, then deletes it.
    /// No WPF here, so it's tested on real files.
    ///
    /// Fixed against the old ShredderManager / view:
    ///  - A link (symlink, or a folder junction inside an added folder) was
    ///    followed: the file it pointed to - anywhere on the PC - was
    ///    overwritten, and only the link was deleted. Links and cloud
    ///    placeholders (OneDrive) are now left alone, with the reason.
    ///  - Adding a folder walked into linked folders, queueing files that
    ///    aren't in it; an unreadable subfolder threw out of the loop
    ///    (unhandled) and the walk ran on the UI thread, O(n²) for duplicates.
    ///  - After a run, every empty folder under an added folder was deleted -
    ///    including ones that were empty before and had nothing to do with
    ///    the shred, and empty folders inside linked folders elsewhere. Now
    ///    only folders this shred emptied are removed.
    ///  - If a file couldn't be opened, its read-only/hidden flags had
    ///    already been cleared and stayed cleared; now they're put back.
    ///  - A failed delete after the random rename left a file nobody could
    ///    find (the row still showed the old name); the new name is now shown.
    ///  - Progress was per file only (a 50 GB file sat at 0% for an hour), and
    ///    errors had no reason anywhere ("Error"). Stop could only happen
    ///    between files with no feedback; closing the app mid-file left it
    ///    half-overwritten and not deleted.
    ///  - "Gutmann 7-pass" wasn't Gutmann (35 set patterns); it's now named
    ///    for what it is: 7 random passes.
    /// </summary>
    public static partial class ShredderManager
    {
        public const int BufferSize = 1 << 20;   // 1 MB - big files are never loaded whole

        // =================================================================
        //  Methods
        // =================================================================

        public static int PassCount(ShredMethod method) => method switch
        {
            ShredMethod.QuickZero => 1,
            ShredMethod.Dod3Pass => 3,
            ShredMethod.Random7Pass => 7,
            _ => 1,
        };

        /// <summary>The byte a pass writes, or null for random data.</summary>
        public static byte? PassPattern(ShredMethod method, int pass) => method switch
        {
            ShredMethod.QuickZero => 0x00,
            ShredMethod.Dod3Pass => pass switch { 0 => (byte)0x00, 1 => (byte)0xFF, _ => null },
            _ => null,
        };

        public static string MethodHint(ShredMethod method) => method switch
        {
            ShredMethod.QuickZero => "One pass of zeros. Fast, and enough for modern hard drives.",
            ShredMethod.Dod3Pass => "Zeros, then ones, then random data (DoD 5220.22-M). A good balance.",
            _ => "Seven passes of random data. Slowest; for the most cautious.",
        };

        /// <summary>Bytes a run will write: every file's size × the passes.</summary>
        public static long BytesToWrite(IEnumerable<ShredItem> items, ShredMethod method) =>
            items.Sum(i => i.SizeBytes) * PassCount(method);

        // =================================================================
        //  Finding files (off the UI thread, links never followed)
        // =================================================================

        public sealed record ScanResult(List<string> Files, int Inaccessible, int LinksSkipped);

        /// <summary>
        /// Files in a folder (and its subfolders when asked). Folder links and
        /// file links/placeholders are skipped and counted - the old walk
        /// followed links, so a junction inside the folder queued files that
        /// live somewhere else entirely.
        /// </summary>
        public static Task<ScanResult> ScanFolderAsync(string folder, bool includeSubfolders, CancellationToken token) =>
            Task.Run(() =>
            {
                var files = new List<string>();
                int inaccessible = 0, links = 0;
                var pending = new Stack<string>();
                pending.Push(folder);
                var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0 };
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    string dir = pending.Pop();
                    try
                    {
                        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                        {
                            bool link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                            if (entry is DirectoryInfo sub)
                            {
                                if (link) links++;
                                else if (includeSubfolders) pending.Push(sub.FullName);
                            }
                            else if (link) links++;
                            else files.Add(entry.FullName);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        inaccessible++;
                    }
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                return new ScanResult(files, inaccessible, links);
            }, token);

        /// <summary>Queue rows for files that exist and aren't links; returns how many links were skipped.</summary>
        public static Task<(List<ShredItem> Items, int LinksSkipped)> CreateItemsAsync(IEnumerable<string> paths, CancellationToken token) =>
            Task.Run(() =>
            {
                var items = new List<ShredItem>();
                int links = 0;
                foreach (string p in paths)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(p);
                        if (!info.Exists) continue;
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            links++;
                            continue;
                        }
                        items.Add(new ShredItem(info.FullName, info.Length, info.Attributes));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        // unreadable path - leave it out
                    }
                }
                return (items, links);
            }, token);

        // =================================================================
        //  Shredding one file
        // =================================================================

        public enum FileOutcome
        {
            Shredded,
            Failed,
            Skipped,
        }

        public sealed record FileResult(FileOutcome Outcome, string Message);

        private const string LinkMessage = "This is a link or a cloud placeholder (e.g. OneDrive). Shredding it could wipe a different file, so it was left alone.";

        /// <summary>
        /// Overwrites the file with the method's passes, then deletes it.
        /// Every file ends one of two ways: untouched (it couldn't be opened -
        /// its attributes are put back), or overwritten and deleted. On
        /// <paramref name="stopNow"/> (the app is closing, or "Stop now") the
        /// overwrite is cut short but the file is still deleted - it was
        /// confirmed for shredding, and a half-overwritten file left behind
        /// helps nobody. <paramref name="afterPass"/> is for tests.
        /// </summary>
        public static FileResult ShredFile(string path, ShredOptions options, Action<long>? onBytes, CancellationToken stopNow, Action<int>? afterPass = null)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) return new FileResult(FileOutcome.Skipped, "Already gone - nothing to shred.");
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new FileResult(FileOutcome.Skipped, LinkMessage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new FileResult(FileOutcome.Failed, PlainError(ex));
            }

            var original = info.Attributes;
            string note = StorageNote(original);
            try { if ((original & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0) File.SetAttributes(path, FileAttributes.Normal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the open below reports the real problem */ }

            bool interrupted = false;
            string? writeProblem = null;
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RestoreAttributes(path, original);   // untouched - leave it exactly as it was
                return new FileResult(FileOutcome.Failed, PlainError(ex));
            }

            using (stream)
            {
                try
                {
                    long length = stream.Length;
                    if (length > 0)
                    {
                        var buffer = new byte[(int)Math.Min(BufferSize, length)];
                        int passes = PassCount(options.Method);
                        for (int pass = 0; pass < passes && !interrupted; pass++)
                        {
                            byte? pattern = PassPattern(options.Method, pass);
                            if (pattern is { } b) Array.Fill(buffer, b);
                            stream.Position = 0;
                            long remaining = length;
                            while (remaining > 0)
                            {
                                if (stopNow.IsCancellationRequested)
                                {
                                    interrupted = true;
                                    break;
                                }
                                int chunk = (int)Math.Min(buffer.Length, remaining);
                                if (pattern == null) RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
                                stream.Write(buffer, 0, chunk);
                                remaining -= chunk;
                                onBytes?.Invoke(chunk);
                            }
                            stream.Flush(flushToDisk: true);
                            if (!interrupted) afterPass?.Invoke(pass);
                        }
                    }
                    if (options.ScrambleName)
                    {
                        stream.SetLength(0);   // the folder's records no longer show how big it was
                        stream.Flush(flushToDisk: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    writeProblem = PlainError(ex);   // part-way: the file is damaged now, so still delete it below
                }
            }

            string current = path;
            if (options.ScrambleName)
            {
                try
                {
                    var epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);
                    File.SetCreationTime(current, epoch);
                    File.SetLastWriteTime(current, epoch);
                    File.SetLastAccessTime(current, epoch);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // dates are a nicety
                }
                try
                {
                    string renamed = Path.Combine(Path.GetDirectoryName(current)!, RandomName());
                    File.Move(current, renamed);
                    current = renamed;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // keep the original name; still delete
                }
            }

            string? deleteProblem = DeleteWithRetry(current);
            if (deleteProblem != null)
            {
                string where = current != path ? $" It's now called {Path.GetFileName(current)} in the same folder." : "";
                return new FileResult(FileOutcome.Failed, $"Overwritten, but it couldn't be deleted: {deleteProblem}{where}");
            }

            var parts = new List<string> { "Shredded" };
            if (interrupted) parts.Add("the overwrite was cut short when stopped, then the file was deleted");
            if (writeProblem != null) parts.Add($"overwriting stopped part-way ({writeProblem}), then the file was deleted");
            if (note.Length > 0) parts.Add(note);
            return new FileResult(FileOutcome.Shredded, string.Join(" - ", parts) + ".");
        }

        /// <summary>NTFS compression, encryption and sparse files write to new places on disk, so old copies may survive an overwrite.</summary>
        public static string StorageNote(FileAttributes attributes)
        {
            if (attributes.HasFlag(FileAttributes.Compressed)) return "the file was NTFS-compressed, so older copies of its data may remain on the disk";
            if (attributes.HasFlag(FileAttributes.Encrypted)) return "the file was EFS-encrypted, so older copies of its data may remain on the disk";
            if (attributes.HasFlag(FileAttributes.SparseFile)) return "the file was sparse, so some of its old data may remain on the disk";
            return "";
        }

        private static void RestoreAttributes(string path, FileAttributes original)
        {
            try { File.SetAttributes(path, original & ~(FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile | FileAttributes.ReparsePoint)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Antivirus and indexers sometimes hold a just-written file for a moment - try a few times.</summary>
        private static string? DeleteWithRetry(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Delete(path);
                    return null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 3) return PlainError(ex);
                    Thread.Sleep(200);
                }
            }
        }

        private const string NameChars = "abcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>16 random characters, no extension (the old one kept the extension, which tells what the file was).</summary>
        public static string RandomName() => RandomNumberGenerator.GetString(NameChars, 16);

        public static string PlainError(Exception ex) => ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => "It isn't there any more.",
            UnauthorizedAccessException => "Windows didn't allow changing this file - it may be protected or need administrator rights.",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "It's open in another program - close it and try again.",
            IOException io when (io.HResult & 0xFFFF) is 112 => "The disk is full.",
            IOException io when io.Message.Contains("locked", StringComparison.OrdinalIgnoreCase) || io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
                => "It's open in another program - close it and try again.",
            _ => ex.Message,
        };

        // =================================================================
        //  The run
        // =================================================================

        /// <summary>
        /// Shreds the rows one at a time (they share one disk). Rows update
        /// as they go, with a per-file progress bar. <paramref name="stopAfterFile"/>
        /// (Stop) finishes the current file and stops; <paramref name="stopNow"/>
        /// (Stop now / app closing) cuts the current file's overwrite short
        /// and deletes it. Afterwards, folders the run left empty are
        /// removed - only inside the added folders <paramref name="roots"/>.
        /// </summary>
        public static Task<ShredSummary> ShredAsync(IReadOnlyList<ShredItem> items, ShredOptions options, IReadOnlyCollection<string> roots,
            IProgress<ShredProgress>? progress, CancellationToken stopAfterFile, CancellationToken stopNow)
        {
            var context = SynchronizationContext.Current;
            void OnUi(Action action)
            {
                if (context == null) action();
                else context.Post(_ => action(), null);
            }

            return Task.Run(async () =>
            {
                int passes = PassCount(options.Method);
                long total = items.Sum(i => i.SizeBytes) * passes, written = 0;
                int shredded = 0, failed = 0, skipped = 0, done = 0;
                long freed = 0;
                bool stopped = false;
                var emptiedCandidates = new List<string>();
                var throttle = System.Diagnostics.Stopwatch.StartNew();

                foreach (var item in items)
                {
                    if (stopAfterFile.IsCancellationRequested || stopNow.IsCancellationRequested)
                    {
                        stopped = true;
                        break;
                    }

                    OnUi(() =>
                    {
                        item.Status = ShredStatus.Working;
                        item.Message = "Overwriting…";
                        item.Progress = 0;
                    });
                    long fileBytes = item.SizeBytes * passes, fileWritten = 0;

                    var result = ShredFile(item.FullPath, options, chunk =>
                    {
                        fileWritten += chunk;
                        written += chunk;
                        if (throttle.ElapsedMilliseconds < 100) return;
                        throttle.Restart();
                        double pct = fileBytes > 0 ? fileWritten * 100.0 / fileBytes : 0;
                        long w = written;
                        int d = done;
                        bool finishing = stopAfterFile.IsCancellationRequested;
                        OnUi(() => item.Progress = pct);
                        progress?.Report(new ShredProgress(d, items.Count, w, total, item.Name, finishing));
                    }, stopNow);

                    written += Math.Max(0, fileBytes - fileWritten);   // keep the bar honest when a file was cut short or failed
                    done++;
                    switch (result.Outcome)
                    {
                        case FileOutcome.Shredded:
                            shredded++;
                            freed += item.SizeBytes;
                            emptiedCandidates.Add(item.Folder);
                            break;
                        case FileOutcome.Failed: failed++; break;
                        default: skipped++; break;
                    }

                    var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    int doneNow = done;
                    long writtenNow = written;
                    OnUi(() =>
                    {
                        item.Status = result.Outcome switch
                        {
                            FileOutcome.Shredded => ShredStatus.Shredded,
                            FileOutcome.Failed => ShredStatus.Failed,
                            _ => ShredStatus.Skipped,
                        };
                        item.Message = result.Message;
                        item.Progress = result.Outcome == FileOutcome.Shredded ? 100 : 0;
                        progress?.Report(new ShredProgress(doneNow, items.Count, writtenNow, total, item.Name, stopAfterFile.IsCancellationRequested));
                        flushed.TrySetResult();
                    });
                    await flushed.Task.ConfigureAwait(false);
                }

                int removed = options.RemoveEmptiedFolders ? RemoveEmptiedFolders(emptiedCandidates, roots) : 0;
                return new ShredSummary(shredded, failed, skipped, freed, removed, stopped);
            });
        }

        // =================================================================
        //  Folders left empty
        // =================================================================

        /// <summary>
        /// Removes folders the shred emptied: each folder that held a
        /// shredded file, then its parents, while they're empty and inside
        /// one of the added folders (the added folder itself goes too when
        /// empty). Folders that were empty before, folders outside the added
        /// ones and links are never touched - the old cleanup deleted every
        /// empty folder it could find, following links.
        /// </summary>
        public static int RemoveEmptiedFolders(IEnumerable<string> foldersThatHadShreddedFiles, IEnumerable<string> roots)
        {
            var rootList = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToList();
            if (rootList.Count == 0) return 0;
            int removed = 0;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string start in foldersThatHadShreddedFiles.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
                                                               .Distinct(StringComparer.OrdinalIgnoreCase)
                                                               .OrderByDescending(f => f.Length))
            {
                string? dir = start;
                while (dir != null && visited.Add(dir))
                {
                    string? root = rootList.FirstOrDefault(r => IsSameOrInside(dir, r));
                    if (root == null) break;   // outside every added folder (a loose file's folder) - leave it
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) break;
                        if (info.EnumerateFileSystemInfos().Any()) break;
                        info.Delete();
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }
                    if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) break;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return removed;
        }

        /// <summary>
        /// Folders that must never be added: a whole drive, Windows, the
        /// program folders and ProgramData (or anything inside them), and the
        /// user's profile folder itself (its subfolders, like Documents\Old,
        /// are fine).
        /// </summary>
        public static bool IsProtectedFolder(string folder, IEnumerable<string> systemFolders, string? userProfile)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string? root = Path.GetPathRoot(full);
            if (root != null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(userProfile) && string.Equals(Path.TrimEndingDirectorySeparator(userProfile), full, StringComparison.OrdinalIgnoreCase)) return true;
            return systemFolders.Where(p => !string.IsNullOrEmpty(p)).Any(p => IsSameOrInside(full, p));
        }

        /// <summary>Real path ancestry ("Photos2" is not inside "Photos").</summary>
        public static bool IsSameOrInside(string path, string folder)
        {
            string p = Path.TrimEndingDirectorySeparator(path), f = Path.TrimEndingDirectorySeparator(folder);
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // =================================================================
        //  Words
        // =================================================================

        public static string DescribeRun(ShredSummary s)
        {
            var parts = new List<string> { $"Shredded {s.Shredded:N0} {ShredText.Plural(s.Shredded, "file", "files")} ({ShredText.Bytes(s.BytesFreed)})" };
            if (s.Failed > 0) parts.Add($"{s.Failed:N0} couldn't be shredded (hover the row for why)");
            if (s.Skipped > 0) parts.Add($"{s.Skipped:N0} skipped");
            if (s.FoldersRemoved > 0) parts.Add($"{s.FoldersRemoved:N0} emptied {ShredText.Plural(s.FoldersRemoved, "folder", "folders")} removed");
            string text = string.Join(" · ", parts) + ".";
            return s.Stopped ? "Stopped. " + text + " The rest weren't touched." : text;
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\shredder.json - no paths)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "shredder.json");

        public static ShredderSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ShredderSettings();
                var s = JsonSerializer.Deserialize<ShredderSettings>(File.ReadAllText(file), SettingsJson) ?? new ShredderSettings();
                if (!Enum.IsDefined(s.Method)) s.Method = ShredMethod.Dod3Pass;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new ShredderSettings();
            }
        }

        public static void SaveSettings(ShredderSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, SettingsJson);
    }
}
