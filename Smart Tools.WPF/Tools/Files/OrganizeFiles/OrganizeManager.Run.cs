using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Scanning, metadata, moving/copying and undo.</summary>
    public sealed partial class OrganizeManager
    {
        /// <summary>Metadata readers - swapped out in tests. Default: ffprobe (Media.cs) and Magick.NET (Images.cs).</summary>
        public Func<string, OrganizeMediaFacts?> ProbeMedia { get; set; } = ProbeWithFfprobe;
        public Func<string, OrganizeImageFacts?> ReadImage { get; set; } = ReadImageFacts;

        /// <summary>Metadata already read, keyed by path + size + date, so changing a rule doesn't re-run ffprobe on every file.</summary>
        private readonly ConcurrentDictionary<string, OrganizeMediaFacts?> _mediaCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, OrganizeImageFacts?> _imageCache = new(StringComparer.OrdinalIgnoreCase);

        private static string CacheKey(OrganizeScannedFile f) => $"{f.FullPath}|{f.SizeBytes}|{f.Modified.Ticks}";

        private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

        // =================================================================
        //  Scan
        // =================================================================

        /// <summary>
        /// Lists the source folder off the UI thread. One unreadable subfolder
        /// no longer fails the whole scan (the old one-shot
        /// Directory.EnumerateFiles did), junctions aren't followed, hidden and
        /// system files are left alone, and when the destination is inside
        /// the source its files are skipped so a second run doesn't re-sort
        /// its own output.
        /// </summary>
        public static Task<OrganizeScanResult> ScanAsync(OrganizeOptions o, IProgress<OrganizeProgress>? progress = null, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new OrganizeScanResult();
                if (string.IsNullOrWhiteSpace(o.SourceFolder) || !Directory.Exists(o.SourceFolder)) return result;

                bool excludeDestination = !string.IsNullOrWhiteSpace(o.DestinationFolder)
                    && !SamePath(o.DestinationFolder, o.SourceFolder)
                    && IsUnder(o.DestinationFolder, o.SourceFolder);

                var clock = Stopwatch.StartNew();
                var last = TimeSpan.Zero;
                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(o.SourceFolder));

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
                        result.InaccessibleFolders++;
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

                        bool hidden = attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System) || IsDotName(entry);
                        if (o.SkipHiddenAndSystem && hidden)
                        {
                            result.HiddenSkipped++;
                            continue;
                        }

                        if (entry is DirectoryInfo sub)
                        {
                            if (!o.IncludeSubfolders) continue;
                            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                            {
                                result.LinksSkipped++;
                                continue;
                            }
                            if (excludeDestination && IsUnder(sub.FullName, o.DestinationFolder)) continue;
                            pending.Push(sub);
                        }
                        else if (entry is FileInfo file)
                        {
                            if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;
                            // Leftovers from an interrupted run aren't user files.
                            if (file.Name.EndsWith(".organizing.tmp", StringComparison.OrdinalIgnoreCase)) continue;
                            if (excludeDestination && IsUnder(file.FullName, o.DestinationFolder))
                            {
                                result.InDestinationSkipped++;
                                continue;
                            }
                            try
                            {
                                result.Files.Add(new OrganizeScannedFile(file.FullName, file.Length, file.CreationTime, file.LastWriteTime));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                continue;
                            }

                            if (clock.Elapsed - last >= ReportEvery)
                            {
                                last = clock.Elapsed;
                                progress?.Report(new OrganizeProgress(OrganizePhase.Scanning, result.Files.Count, 0, file.Name));
                            }
                        }
                    }
                }
                return result;
            }, token);

        /// <summary>
        /// Unix-style hidden names: folders like ".git", and files like ".gitignore" / ".env".
        /// A file such as ".NET 4.0 Framework.zip" has a real extension after the first dot,
        /// so it's an ordinary file that just happens to start with one.
        /// </summary>
        private static bool IsDotName(FileSystemInfo entry) =>
            entry.Name.StartsWith('.') && (entry is DirectoryInfo || entry.Name.IndexOf('.', 1) < 0);

        // =================================================================
        //  Plan (reads metadata only for the files a rule needs)
        // =================================================================

        public async Task<List<OrganizeFileItem>> PlanAsync(
            IReadOnlyList<OrganizeScannedFile> files,
            OrganizeOptions o,
            IProgress<OrganizeProgress>? progress = null,
            CancellationToken token = default)
        {
            bool rules = o.Layout == OrganizeLayout.Rules;
            bool needProbe = rules && o.Steps.Any(s => NeedsProbe(s.Criterion));
            bool needImage = rules && o.Steps.Any(s => NeedsImageRead(s.Criterion));

            var probeKinds = new HashSet<MediaKind>(o.Steps.Select(s => KindFor(s.Criterion)).OfType<MediaKind>());
            var toRead = files.Where(f =>
            {
                var kind = FileTypeDetector.Detect(f.FullPath);
                if (!probeKinds.Contains(kind)) return false;
                return kind == MediaKind.Image
                    ? needImage && !_imageCache.ContainsKey(CacheKey(f))
                    : needProbe && !_mediaCache.ContainsKey(CacheKey(f));
            }).ToList();

            if (toRead.Count > 0)
            {
                if (needProbe && ProbeMedia == ProbeWithFfprobe && FfprobeExe == null && toRead.Any(f => FileTypeDetector.Detect(f.FullPath) != MediaKind.Image))
                    throw new InvalidOperationException($"Video and audio rules need ffprobe.exe in {FfmpegFolder}. It wasn't found there.");

                int done = 0;
                var clock = Stopwatch.StartNew();
                long lastTicks = 0;
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = token };
                await Parallel.ForEachAsync(toRead, parallel, (f, ct) =>
                {
                    string key = CacheKey(f);
                    if (FileTypeDetector.Detect(f.FullPath) == MediaKind.Image) _imageCache[key] = SafeRead(ReadImage, f.FullPath);
                    else _mediaCache[key] = SafeRead(ProbeMedia, f.FullPath);

                    int n = Interlocked.Increment(ref done);
                    long now = clock.ElapsedTicks;
                    long prev = Interlocked.Read(ref lastTicks);
                    if (n == toRead.Count || (now - prev > Stopwatch.Frequency / 10 && Interlocked.CompareExchange(ref lastTicks, now, prev) == prev))
                        progress?.Report(new OrganizeProgress(OrganizePhase.Planning, n, toRead.Count, Path.GetFileName(f.FullPath)));
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            }

            var byPath = files.ToDictionary(f => f.FullPath, CacheKey, StringComparer.OrdinalIgnoreCase);
            return await Task.Run(() => BuildPlan(files, o,
                path => byPath.TryGetValue(path, out var k) && _mediaCache.TryGetValue(k, out var m) ? m : null,
                path => byPath.TryGetValue(path, out var k) && _imageCache.TryGetValue(k, out var i) ? i : null), token).ConfigureAwait(false);
        }

        private static T? SafeRead<T>(Func<string, T?> read, string path) where T : class
        {
            try
            {
                return read(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // =================================================================
        //  Run
        // =================================================================

        /// <summary>Guards "is the name free? -> put the file there" so parallel files never race for one name.</summary>
        private static readonly object NameLock = new();

        public static bool IsPending(OrganizeFileItem item) =>
            item.Status is OrganizeStatus.Ready or OrganizeStatus.Failed or OrganizeStatus.Cancelled;

        /// <summary>
        /// Moves or copies every pending row. Returns what happened plus an
        /// undo journal (save it with <see cref="SaveJournal"/>). Settings
        /// are the ones the preview was built with - the view rebuilds the
        /// preview whenever a setting changes (the old one kept a stale
        /// preview and ran it with the new options).
        /// </summary>
        public async Task<(OrganizeRunResult Result, OrganizeJournal Journal)> RunAsync(
            IReadOnlyList<OrganizeFileItem> items,
            OrganizeOptions o,
            IProgress<OrganizeProgress>? progress = null,
            CancellationToken token = default)
        {
            var clock = Stopwatch.StartNew();
            var work = items.Where(IsPending).ToList();
            var result = new OrganizeRunResult();
            var journal = new OrganizeJournal { When = DateTime.Now, Moved = o.MoveFiles, SourceFolder = o.SourceFolder };
            if (work.Count == 0) return (result, journal);

            int parallel = o.MaxDegreeOfParallelism > 0 ? o.MaxDegreeOfParallelism
                : o.MoveFiles && SameVolume(o.SourceFolder, o.DestinationFolder) ? 4 : 2;

            var gate = new object();
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            long lastReport = 0;

            foreach (var item in work)
            {
                item.Status = OrganizeStatus.Ready;
                item.Message = "";
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = parallel };
            await Parallel.ForEachAsync(work, options, (item, _) =>
            {
                if (token.IsCancellationRequested)
                {
                    item.Status = OrganizeStatus.Cancelled;
                    item.Message = "Not started - the run was cancelled.";
                }
                else
                {
                    ProcessOne(item, o, created, gate);
                }

                lock (gate)
                {
                    done++;
                    switch (item.Status)
                    {
                        case OrganizeStatus.Moved:
                            result.Moved++;
                            result.Bytes += item.SizeBytes;
                            journal.Entries.Add(new OrganizeJournalEntry { From = item.FullPath, To = item.Destination });
                            break;
                        case OrganizeStatus.Copied:
                            result.Copied++;
                            result.Bytes += item.SizeBytes;
                            journal.Entries.Add(new OrganizeJournalEntry { From = item.FullPath, To = item.Destination });
                            break;
                        case OrganizeStatus.Duplicate:
                            result.Duplicates++;
                            break;
                        case OrganizeStatus.Cancelled:
                            result.Cancelled++;
                            break;
                        default:
                            result.Failed++;
                            break;
                    }
                    long now = clock.ElapsedMilliseconds;
                    if (done == work.Count || now - lastReport >= 100)
                    {
                        lastReport = now;
                        progress?.Report(new OrganizeProgress(OrganizePhase.Organizing, done, work.Count, item.Name));
                    }
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            // Only folders a moved file came out of, walking up to (never including) the source folder.
            if (o.MoveFiles && o.RemoveEmptyFolders && Directory.Exists(o.SourceFolder))
            {
                var touched = work
                    .Where(i => i.Status is OrganizeStatus.Moved or OrganizeStatus.Duplicate)
                    .Select(i => Path.GetDirectoryName(i.FullPath) ?? "")
                    .Where(d => d.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(d => d.Length);
                foreach (string folder in touched)
                    result.FoldersRemoved += RemoveEmptyFoldersUpward(folder, o.SourceFolder, o.DestinationFolder);

                // Flatten drops the whole folder tree, so also clear folders the walk
                // above can't reach (already empty, or holding only desktop.ini / Thumbs.db).
                if (o.Layout == OrganizeLayout.Flatten)
                    result.FoldersRemoved += SweepEmptyFolders(o.SourceFolder, o.SourceFolder, o.DestinationFolder);
            }

            journal.CreatedFolders = created.OrderByDescending(f => f.Length).ToList();
            result.Elapsed = clock.Elapsed;
            return (result, journal);
        }

        private static void ProcessOne(OrganizeFileItem item, OrganizeOptions o, HashSet<string> created, object gate)
        {
            string? temp = null;
            try
            {
                if (!File.Exists(item.FullPath)) throw new FileNotFoundException("The file is no longer there.");

                string dest = item.Destination;
                string dir = Path.GetDirectoryName(dest)!;
                EnsureFolder(dir, o.DestinationFolder, created, gate);

                // Same name already there: only an identical file (checked by
                // content, not just size) counts as "already done".
                if (File.Exists(dest) && !SamePath(dest, item.FullPath) && FilesAreIdentical(item.FullPath, dest))
                {
                    item.Status = OrganizeStatus.Duplicate;
                    if (o.MoveFiles)
                    {
                        DeleteToRecycleBin(item.FullPath);
                        item.Message = $"An identical file was already at {dest} - the original went to the Recycle Bin.";
                    }
                    else
                    {
                        item.Message = $"An identical file is already at {dest}.";
                    }
                    return;
                }

                string final = dest;
                if (o.MoveFiles && SameVolume(item.FullPath, dir))
                {
                    // Same drive: a rename, nothing to copy. The lock is taken
                    // per attempt so a locked file waiting to retry never blocks the others.
                    RetryIo(() =>
                    {
                        lock (NameLock)
                        {
                            final = UniquePath(dest);
                            File.Move(item.FullPath, final);
                        }
                    });
                }
                else
                {
                    // Other drive or copy: write a temp file next to the target,
                    // check it, then rename it into place.
                    temp = Path.Combine(dir, $".{Path.GetFileName(dest)}.{Guid.NewGuid().ToString("N")[..6]}.organizing.tmp");
                    RetryIo(() => File.Copy(item.FullPath, temp, overwrite: true));
                    var source = new FileInfo(item.FullPath);
                    if (new FileInfo(temp).Length != source.Length)
                        throw new IOException("The copy came out a different size - nothing was changed.");
                    // A move deletes the original next, so prove the copy is identical first (size alone can't).
                    if (o.MoveFiles && !FilesAreIdentical(item.FullPath, temp))
                        throw new IOException("The copy didn't match the original byte for byte - the original was left where it is.");

                    // A read-only original gives a read-only copy, and times can't be set on those.
                    var tempAttrs = File.GetAttributes(temp);
                    if (tempAttrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(temp, tempAttrs & ~FileAttributes.ReadOnly);
                    File.SetLastWriteTimeUtc(temp, source.LastWriteTimeUtc);
                    try
                    {
                        File.SetCreationTimeUtc(temp, source.CreationTimeUtc);
                    }
                    catch (Exception)
                    {
                        // not supported everywhere
                    }
                    if (tempAttrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(temp, tempAttrs);

                    RetryIo(() =>
                    {
                        lock (NameLock)
                        {
                            final = UniquePath(dest);
                            File.Move(temp, final);
                        }
                    });
                    temp = null;
                    if (o.MoveFiles)
                    {
                        // Only remove the original once the verified copy is really at its final place.
                        if (!File.Exists(final) || new FileInfo(final).Length != source.Length)
                            throw new IOException("The copy isn't where it should be - the original was left where it is.");
                        ClearReadOnly(item.FullPath);
                        RetryIo(() => File.Delete(item.FullPath));
                    }
                }

                item.Destination = final;
                item.Status = o.MoveFiles ? OrganizeStatus.Moved : OrganizeStatus.Copied;
                item.Message = SamePath(final, dest)
                    ? $"{(o.MoveFiles ? "Moved" : "Copied")} to {final}"
                    : $"A different file named {Path.GetFileName(dest)} was already there - saved as {Path.GetFileName(final)}.";
            }
            catch (Exception ex)
            {
                item.Status = OrganizeStatus.Failed;
                item.Message = DescribeFailure(item, ex);
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
                        // best effort
                    }
                }
            }
        }

        /// <summary>
        /// Runs a file operation, trying again when the file is only busy for a
        /// moment (antivirus scanning a fresh zip, Explorer's preview pane...).
        /// Missing files and folders fail at once - waiting won't bring them back.
        /// </summary>
        private static void RetryIo(Action action) => SafeFileOps.RetryIo(action);

        private static void ClearReadOnly(string path) => SafeFileOps.ClearReadOnly(path);

        /// <summary>A row message that names the cause instead of a bare framework error.</summary>
        private static string DescribeFailure(OrganizeFileItem item, Exception ex)
        {
            string parent = Path.GetDirectoryName(item.FullPath) ?? "";
            switch (ex)
            {
                case FileNotFoundException or DirectoryNotFoundException:
                    return parent.Length > 0 && !Directory.Exists(parent)
                        ? $"{item.Name} is gone: its folder no longer exists."
                        : $"{item.Name} is no longer there - it was moved, renamed or deleted after the scan.";
                case PathTooLongException:
                    return $"{item.Name}: the path is too long for Windows.";
                case UnauthorizedAccessException:
                    return $"{item.Name}: access denied - the file or folder is protected or read-only.";
                case IOException when ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021):
                    return $"{item.Name} is in use by another program (antivirus, Explorer, an unzip tool...). Close it and run again.";
                default:
                    return $"{item.Name}: {ex.Message}";
            }
        }

        /// <summary>Creates the folder and records which levels (under the destination) didn't exist, so undo can remove them.</summary>
        private static void EnsureFolder(string dir, string root, HashSet<string> created, object gate)
        {
            if (Directory.Exists(dir)) return;
            lock (gate)
            {
                if (Directory.Exists(dir)) return;
                var missing = new List<string>();
                for (string? d = dir; d != null && !Directory.Exists(d) && IsUnder(d, root) && !SamePath(d, root); d = Path.GetDirectoryName(d))
                    missing.Add(d);
                Directory.CreateDirectory(dir);
                foreach (string d in missing) created.Add(Path.GetFullPath(d));
            }
        }

        private static bool SameVolume(string a, string b)
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

        /// <summary>Byte-level proof (size, then SHA-256) - a matching name and size alone isn't.</summary>
        public static bool FilesAreIdentical(string a, string b) => SafeFileOps.FilesAreIdentical(a, b);

        public static bool HasRecycleBin(string path) => SafeFileOps.HasRecycleBin(path);

        private static void DeleteToRecycleBin(string path)
        {
            if (!File.Exists(path)) return;
            if (HasRecycleBin(path)) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else File.Delete(path);
        }

        /// <summary>
        /// Deletes <paramref name="folder"/> and its parents while they're
        /// empty, stopping at the source folder (never deleted) and never
        /// touching the destination tree.
        /// </summary>
        private static int RemoveEmptyFoldersUpward(string folder, string sourceRoot, string destinationRoot)
        {
            int removed = 0;
            string current = Path.GetFullPath(folder);
            while (IsUnder(current, sourceRoot) && !SamePath(current, sourceRoot) && !IsUnder(destinationRoot, current))
            {
                try
                {
                    if (!Directory.Exists(current) || !IsEmptyOrJunkOnly(current)) break;
                    DeleteFolderAndJunk(current);
                    removed++;
                }
                catch (Exception)
                {
                    break;
                }
                string? parent = Path.GetDirectoryName(current);
                if (parent == null) break;
                current = parent;
            }
            return removed;
        }

        /// <summary>Files Windows and Explorer drop into folders on their own - not worth keeping a folder for.</summary>
        private static readonly HashSet<string> JunkFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "desktop.ini", "thumbs.db", ".ds_store",
        };

        private static bool IsEmptyOrJunkOnly(string folder) =>
            Directory.EnumerateFileSystemEntries(folder).All(e => !Directory.Exists(e) && JunkFileNames.Contains(Path.GetFileName(e)));

        private static void DeleteFolderAndJunk(string folder)
        {
            foreach (string junk in Directory.EnumerateFiles(folder))
            {
                File.SetAttributes(junk, FileAttributes.Normal);   // desktop.ini is hidden + system, often read-only
                File.Delete(junk);
            }
            Directory.Delete(folder);
        }

        /// <summary>
        /// Deletes every folder under <paramref name="folder"/> (deepest first) that is empty or
        /// holds only junk files, then the folder itself unless it is the source root. Skips links,
        /// the destination tree and any folder that contains the destination.
        /// </summary>
        private static int SweepEmptyFolders(string folder, string sourceRoot, string destinationRoot)
        {
            int removed = 0;
            try
            {
                // Flattening in place (destination == source) is fine; only a destination
                // nested inside the source has to be left alone, with the folders leading to it.
                bool isRoot = SamePath(folder, sourceRoot);
                bool destNested = !SamePath(destinationRoot, sourceRoot) && IsUnder(destinationRoot, sourceRoot);
                if (!isRoot && destNested && (IsUnder(folder, destinationRoot) || IsUnder(destinationRoot, folder))) return 0;
                foreach (string sub in Directory.EnumerateDirectories(folder))
                {
                    if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    removed += SweepEmptyFolders(sub, sourceRoot, destinationRoot);
                }
                if (!isRoot && IsEmptyOrJunkOnly(folder))
                {
                    DeleteFolderAndJunk(folder);
                    removed++;
                }
            }
            catch (Exception)
            {
                // locked or protected folder: leave it
            }
            return removed;
        }

        // =================================================================
        //  Undo
        // =================================================================

        /// <summary>
        /// Reverses the last run from its journal (kept on disk, so it works
        /// after a restart). Moved files go back - their folders are
        /// recreated first (the old undo failed for every file whose folder
        /// "Remove empty folders" had deleted). Copies are removed only if
        /// still identical to the original. Folders the run created are
        /// removed again once empty.
        /// </summary>
        public static Task<OrganizeUndoResult> UndoAsync(OrganizeJournal journal, IProgress<OrganizeProgress>? progress = null, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new OrganizeUndoResult();
                var clock = Stopwatch.StartNew();
                long last = 0;
                int done = 0;

                foreach (var entry in Enumerable.Reverse(journal.Entries))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        UndoOne(entry, journal.Moved, result);
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Errors.Add($"{Path.GetFileName(entry.To)}: {ex.Message}");
                    }
                    done++;
                    if (done == journal.Entries.Count || clock.ElapsedMilliseconds - last >= 100)
                    {
                        last = clock.ElapsedMilliseconds;
                        progress?.Report(new OrganizeProgress(OrganizePhase.Undoing, done, journal.Entries.Count, Path.GetFileName(entry.From)));
                    }
                }

                foreach (string folder in journal.CreatedFolders.OrderByDescending(f => f.Length))
                {
                    try
                    {
                        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                    }
                    catch (Exception)
                    {
                        // still in use - leave it
                    }
                }
                return result;
            }, token);

        private static void UndoOne(OrganizeJournalEntry entry, bool wasMove, OrganizeUndoResult result)
        {
            if (!File.Exists(entry.To))
            {
                result.Missing++;
                return;
            }

            if (!wasMove)
            {
                if (FilesAreIdentical(entry.From, entry.To))
                {
                    File.Delete(entry.To);
                    result.Restored++;
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"{Path.GetFileName(entry.To)}: changed since it was copied (or the original is gone) - kept.");
                }
                return;
            }

            if (File.Exists(entry.From))
            {
                if (FilesAreIdentical(entry.From, entry.To))
                {
                    File.Delete(entry.To);
                    result.Restored++;
                    return;
                }
                string alt = UniquePath(entry.From);
                MoveFile(entry.To, alt);
                result.Renamed++;
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entry.From)!);
            MoveFile(entry.To, entry.From);
            result.Restored++;
        }

        private static void MoveFile(string from, string to)
        {
            if (SameVolume(from, to))
            {
                File.Move(from, to);
                return;
            }
            string temp = to + ".organizing.tmp";
            File.Copy(from, temp, overwrite: true);
            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(from));
            File.Move(temp, to);
            File.Delete(from);
        }
    }
}
