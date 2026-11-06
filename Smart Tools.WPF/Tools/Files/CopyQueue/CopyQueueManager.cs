using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Copy Queue backend: works out every file first, then copies or moves them one at a time (a disk is the
    /// bottleneck, and one at a time keeps pause exact). The safety rules live in <see cref="SafeFileOps"/>:
    /// nothing is overwritten, every copy goes through a temp file, a move removes the original only after the
    /// copy has been compared to it, and a failed or stopped file leaves the original where it was.
    /// </summary>
    public sealed class CopyQueueManager
    {
        private const string TempMarker = ".queue.tmp";
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);
        private static readonly object NameLock = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ManualResetEventSlim _running = new(true);

        public bool IsPaused => !_running.IsSet;

        public void Pause() => _running.Reset();

        /// <summary>Also lets a paused worker go, so stopping a paused queue doesn't hang.</summary>
        public void Resume() => _running.Set();

        // =================================================================
        //  Planning
        // =================================================================

        /// <summary>
        /// Lists every file under the sources and where each will go: a file straight into the destination, a
        /// folder as a folder of that name. Nothing is read beyond names and sizes. A folder can't be copied into itself.
        /// </summary>
        public static CopyQueuePlan BuildPlan(IEnumerable<string> sources, CopyQueueOptions o, CancellationToken token = default)
        {
            var plan = new CopyQueuePlan();
            string destRoot = Path.GetFullPath(o.DestinationFolder);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,   // hidden and system files are still files of the user's
            };

            foreach (string raw in sources)
            {
                token.ThrowIfCancellationRequested();
                string source = Path.GetFullPath(raw);
                if (Directory.Exists(source))
                {
                    if (IsUnderOrSame(destRoot, source))
                    {
                        plan.Notes.Add(Loc.F("Str_CopyQueue_NoteLeftOut", "{0} was left out: the destination is inside it.", Path.GetFileName(source)));
                        continue;
                    }
                    string targetRoot = Path.Combine(destRoot, Path.GetFileName(source.TrimEnd('\\', '/')));
                    plan.Folders.Add(targetRoot);
                    foreach (string dir in Directory.EnumerateDirectories(source, "*", enumeration))
                        plan.Folders.Add(Path.Combine(targetRoot, Path.GetRelativePath(source, dir)));
                    foreach (string file in Directory.EnumerateFiles(source, "*", enumeration))
                    {
                        token.ThrowIfCancellationRequested();
                        if (file.EndsWith(TempMarker, StringComparison.OrdinalIgnoreCase) || !seen.Add(file)) continue;
                        Add(plan, file, Path.Combine(targetRoot, Path.GetRelativePath(source, file)));
                    }
                }
                else if (File.Exists(source))
                {
                    if (seen.Add(source)) Add(plan, source, Path.Combine(destRoot, Path.GetFileName(source)));
                }
                else
                {
                    plan.Notes.Add(Loc.F("Str_CopyQueue_NoteGone", "{0} is no longer there.", Path.GetFileName(source)));
                }
            }

            bool moveAcrossDrives = o.Mode == CopyQueueMode.Move;
            foreach (var item in plan.Items)
            {
                // The copy pass, plus a verify pass when one will run (a move between drives always verifies).
                bool sameVolumeMove = o.Mode == CopyQueueMode.Move && SafeFileOps.SameVolume(item.Source, o.DestinationFolder);
                bool verifies = !sameVolumeMove && (o.Verify || moveAcrossDrives);
                item.PlannedBytes = sameVolumeMove ? 0 : item.Size * (verifies ? 2 : 1);
                // A file already at the target is compared with it first (one read pass of the data).
                if (TargetHoldsSameSize(item)) item.PlannedBytes += item.Size;
                plan.BytesTotal += item.PlannedBytes;
            }
            return plan;
        }

        private static bool TargetHoldsSameSize(CopyQueueItem item)
        {
            try
            {
                var target = new FileInfo(item.Destination);
                return target.Exists && target.Length == item.Size && !SamePath(item.Destination, item.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        private static void Add(CopyQueuePlan plan, string file, string destination)
        {
            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // listed anyway; the copy reports the real problem
            }
            plan.Items.Add(new CopyQueueItem { Source = file, Destination = destination, Size = size });
        }

        // =================================================================
        //  Running
        // =================================================================

        public async Task<CopyQueueResult> RunAsync(CopyQueuePlan plan, CopyQueueOptions o, IProgress<CopyQueueProgress>? progress, CancellationToken token)
        {
            var result = new CopyQueueResult();
            var clock = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                foreach (string folder in plan.Folders)
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        // a file inside will report the real problem
                    }
                }

                long bytesDone = 0;
                int filesDone = 0;
                long lastReport = -1;
                long copied = 0;

                void Report(string current, bool force = false)
                {
                    if (!force && clock.ElapsedMilliseconds - lastReport < ReportEvery.TotalMilliseconds) return;
                    lastReport = clock.ElapsedMilliseconds;
                    double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                    progress?.Report(new CopyQueueProgress(filesDone, plan.Items.Count, bytesDone, plan.BytesTotal, current, copied / seconds));
                }

                foreach (var item in plan.Items)
                {
                    try
                    {
                        _running.Wait(token);   // a pause holds the queue between files too, not only inside a big one
                    }
                    catch (OperationCanceledException)
                    {
                        // handled right below
                    }
                    if (token.IsCancellationRequested)
                    {
                        item.Status = CopyQueueStatus.Cancelled;
                        result.Cancelled++;
                        continue;
                    }

                    long credited = 0;
                    void OnBytes(long n)
                    {
                        credited += n;
                        bytesDone += n;
                        if (item.Status == CopyQueueStatus.Working) { copied += n; result.BytesCopied += n; }
                        Report(item.Name);
                    }

                    try
                    {
                        ProcessOne(item, o, OnBytes, () => _running.Wait(token), result, token);
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = CopyQueueStatus.Cancelled;
                        item.Message = "Stopped - the original was not touched.";
                        result.Cancelled++;
                    }
                    catch (Exception ex)
                    {
                        item.Status = CopyQueueStatus.Failed;
                        item.Message = Describe(item, ex);
                        result.Failed++;
                    }

                    // Whatever the file ended up needing, count all of its share so the bar reaches 100%.
                    if (credited < item.PlannedBytes) bytesDone += item.PlannedBytes - credited;
                    filesDone++;
                    Report(item.Name, force: true);
                }
            }, CancellationToken.None);

            result.Elapsed = clock.Elapsed;
            return result;
        }

        private void ProcessOne(CopyQueueItem item, CopyQueueOptions o, Action<long> onBytes, Action gate, CopyQueueResult result, CancellationToken token)
        {
            string? temp = null;
            try
            {
                if (!File.Exists(item.Source)) throw new FileNotFoundException(Loc.T("Str_Files_ErrGone", "The file is no longer there."));

                string dest = item.Destination;
                string dir = Path.GetDirectoryName(dest)!;
                Directory.CreateDirectory(dir);
                bool move = o.Mode == CopyQueueMode.Move;

                if (File.Exists(dest))
                {
                    if (SamePath(dest, item.Source))
                    {
                        item.Status = CopyQueueStatus.Skipped;
                        item.Message = Loc.T("Str_CopyQueue_MsgSameFile", "The source and the target are the same file.");
                        result.Skipped++;
                        return;
                    }
                    // Same name: only an identical file (by content) counts as already done.
                    if (SafeFileOps.FilesAreIdentical(item.Source, dest, onBytes, gate, token))
                    {
                        item.Status = CopyQueueStatus.AlreadyThere;
                        result.AlreadyThere++;
                        if (move && SafeFileOps.HasRecycleBin(item.Source))
                        {
                            FileSystem.DeleteFile(item.Source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                            item.Message = Loc.T("Str_CopyQueue_MsgIdenticalRecycled", "An identical file was already there - the original went to the Recycle Bin.");
                        }
                        else
                        {
                            item.Message = move ? Loc.T("Str_CopyQueue_MsgIdenticalKept", "An identical file was already there - the original was kept.") : Loc.T("Str_CopyQueue_MsgIdentical", "An identical file was already there.");
                        }
                        return;
                    }
                    if (o.Conflict == CopyQueueConflict.Skip)
                    {
                        item.Status = CopyQueueStatus.Skipped;
                        item.Message = "A different file with this name is already there.";
                        result.Skipped++;
                        return;
                    }
                }

                string final = dest;
                if (move && SafeFileOps.SameVolume(item.Source, dir))
                {
                    // Same drive: a rename, nothing is read or written.
                    long length = new FileInfo(item.Source).Length;
                    SafeFileOps.RetryIo(() =>
                    {
                        lock (NameLock)
                        {
                            final = SafeFileOps.UniquePath(dest);
                            File.Move(item.Source, final);
                        }
                    });
                    if (!File.Exists(final) || new FileInfo(final).Length != length)
                        throw new IOException(Loc.T("Str_CopyQueue_ErrMoveMissing", "The file isn't where it should be after the move - check the destination."));
                }
                else
                {
                    item.Status = CopyQueueStatus.Working;
                    temp = Path.Combine(dir, $".{Path.GetFileName(dest)}.{Guid.NewGuid().ToString("N")[..6]}{TempMarker}");
                    string sourceHash = "";
                    SafeFileOps.RetryIo(() => sourceHash = SafeFileOps.CopyToTemp(item.Source, temp, onBytes, gate, token));

                    if (new FileInfo(temp).Length != new FileInfo(item.Source).Length)
                        throw new IOException(Loc.T("Str_Files_ErrCopySize", "The copy came out a different size - nothing was changed."));
                    SafeFileOps.CopyTimestamps(item.Source, temp);

                    // A move deletes the original next, so it is always compared first.
                    if (o.Verify || move)
                    {
                        item.Status = CopyQueueStatus.Verifying;
                        string copyHash = SafeFileOps.HashFile(temp, onBytes, gate, token);
                        if (!string.Equals(sourceHash, copyHash, StringComparison.Ordinal))
                            throw new IOException(Loc.T("Str_CopyQueue_ErrCopyMismatch", "The copy didn't match the original - the original was left where it is."));
                        item.Verified = true;
                        result.Verified++;
                    }

                    SafeFileOps.RetryIo(() =>
                    {
                        lock (NameLock)
                        {
                            final = SafeFileOps.UniquePath(dest);
                            File.Move(temp, final);
                        }
                    });
                    temp = null;

                    if (move)
                    {
                        if (!File.Exists(final) || new FileInfo(final).Length != new FileInfo(item.Source).Length)
                            throw new IOException(Loc.T("Str_Files_ErrCopyMissing", "The copy isn't where it should be - the original was left where it is."));
                        SafeFileOps.ClearReadOnly(item.Source);
                        SafeFileOps.RetryIo(() => File.Delete(item.Source));
                    }
                }

                item.Moved = move;
                item.Destination = final;
                item.Status = CopyQueueStatus.Done;
                item.Message = SamePath(final, dest)
                    ? (move ? "Moved" : "Copied")
                    : Loc.F("Str_Files_MsgSavedAs", "A different file named {0} was already there - saved as {1}.", Path.GetFileName(dest), Path.GetFileName(final));
                result.Done++;
            }
            finally
            {
                SafeFileOps.DeleteQuietly(temp);
            }
        }

        private static string Describe(CopyQueueItem item, Exception ex) => ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => Loc.F("Str_CopyQueue_ErrGone", "{0} is no longer there - it was moved, renamed or deleted after the plan was made.", item.Name),
            PathTooLongException => Loc.F("Str_Files_ErrPathTooLong", "{0}: the path is too long for Windows.", item.Name),
            UnauthorizedAccessException => Loc.F("Str_Files_ErrDenied", "{0}: access denied - the file or folder is protected or read-only.", item.Name),
            IOException when ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) =>
                Loc.F("Str_CopyQueue_ErrInUse", "{0} is in use by another program. Find it with Process Killer's locked-file lookup, close it and run again.", item.Name),
            IOException when ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) =>
                Loc.F("Str_Files_ErrDiskFull", "{0}: the destination is full - the original was left where it is.", item.Name),
            _ => $"{item.Name}: {ex.Message}",
        };

        // =================================================================
        //  Undo (moves only - a copy leaves the originals, so there is nothing to put back)
        // =================================================================

        public static CopyQueueJournal BuildJournal(IEnumerable<CopyQueueItem> items) => new()
        {
            When = DateTime.Now,
            Entries = items.Where(i => i is { Moved: true, Status: CopyQueueStatus.Done })
                .Select(i => new CopyQueueJournalEntry { From = i.Source, To = i.Destination }).ToList(),
        };

        /// <summary>The same verified pipeline, run backwards: each file goes from where it landed to where it came from.</summary>
        public static CopyQueuePlan BuildUndoPlan(CopyQueueJournal journal)
        {
            var plan = new CopyQueuePlan();
            foreach (var e in Enumerable.Reverse(journal.Entries))
            {
                if (!File.Exists(e.To))
                {
                    plan.Notes.Add(Loc.F("Str_CopyQueue_NoteNotAt", "{0} is no longer at {1}.", Path.GetFileName(e.To), e.To));
                    continue;
                }
                long size = new FileInfo(e.To).Length;
                bool sameVolume = SafeFileOps.SameVolume(e.To, e.From);
                plan.Items.Add(new CopyQueueItem { Source = e.To, Destination = e.From, Size = size, PlannedBytes = sameVolume ? 0 : size * 2 });
                plan.BytesTotal += sameVolume ? 0 : size * 2;
            }
            return plan;
        }

        // =================================================================
        //  Saved job and journal
        // =================================================================

        private static string DataFile(string name) => Path.Combine(AppPaths.DataRoot, name);

        public static string JobPath => DataFile("copy_queue_job.json");
        public static string JournalPath => DataFile("copy_queue_undo.json");

        public static CopyQueueSavedJob? LoadJob() => Load<CopyQueueSavedJob>(JobPath) is { Sources.Count: > 0 } j ? j : null;
        public static void SaveJob(CopyQueueSavedJob job) => Save(job, JobPath);
        public static void DeleteJob() => SafeFileOps.DeleteQuietly(JobPath);

        public static CopyQueueJournal? LoadJournal() => Load<CopyQueueJournal>(JournalPath) is { Entries.Count: > 0 } j ? j : null;
        public static void SaveJournal(CopyQueueJournal journal) => Save(journal, JournalPath);
        public static void DeleteJournal() => SafeFileOps.DeleteQuietly(JournalPath);

        private static T? Load<T>(string file) where T : class
        {
            try
            {
                return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                if (ex is JsonException or NotSupportedException) JsonSettings.KeepBadCopy(file);
                return null;
            }
        }

        private static void Save<T>(T value, string file) => AtomicFile.WriteAllText(file, JsonSerializer.Serialize(value, JsonOptions));

        // =================================================================
        //  Paths and text
        // =================================================================

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static bool IsUnderOrSame(string path, string root)
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/');
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static string DescribeRun(CopyQueueResult r, CopyQueueMode mode, bool undo = false)
        {
            string head = undo ? Loc.F("Str_CopyQueue_RunPutBack", "Put back {0:N0} in {1:0.0} s", r.Done, r.Elapsed.TotalSeconds)
                : mode == CopyQueueMode.Move ? Loc.F("Str_CopyQueue_RunMoved", "Moved {0:N0} in {1:0.0} s", r.Done, r.Elapsed.TotalSeconds)
                : Loc.F("Str_CopyQueue_RunCopied", "Copied {0:N0} in {1:0.0} s", r.Done, r.Elapsed.TotalSeconds);
            var parts = new List<string> { head };
            if (r.Verified > 0) parts.Add(Loc.F("Str_CopyQueue_RunVerified", "{0:N0} verified", r.Verified));
            if (r.AlreadyThere > 0) parts.Add(Loc.F("Str_CopyQueue_RunAlreadyThere", "{0:N0} already there", r.AlreadyThere));
            if (r.Skipped > 0) parts.Add(Loc.F("Str_CopyQueue_RunSkipped", "{0:N0} skipped", r.Skipped));
            if (r.Failed > 0) parts.Add(Loc.F("Str_CopyQueue_RunFailed", "{0:N0} failed", r.Failed));
            if (r.Cancelled > 0) parts.Add(Loc.F("Str_CopyQueue_RunStopped", "{0:N0} stopped", r.Cancelled));
            return string.Join(" · ", parts);
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes:N0} B" : $"{v:0.#} {units[u]}";
        }
    }
}
