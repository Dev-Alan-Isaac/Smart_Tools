using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Reading the folders, renaming and undo.</summary>
    public static partial class RenameManager
    {
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

        /// <summary>Marks the short-lived name a file takes during a swap. Such files are never listed.</summary>
        public const string TempMarker = ".renaming-";

        public static bool IsTempName(string path) =>
            Path.GetFileName(path).Contains(TempMarker, StringComparison.OrdinalIgnoreCase) && path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        // =================================================================
        //  Scan
        // =================================================================

        /// <summary>
        /// Lists the files in the chosen folders off the UI thread. One
        /// unreadable subfolder no longer ends the whole scan (the old
        /// EnumerateFiles threw halfway and crashed the view), junctions
        /// aren't followed, hidden and system files are left alone, and a
        /// folder added twice - or inside another one - lists each file once.
        /// </summary>
        public static Task<RenameScanResult> ScanAsync(IEnumerable<string> folders, RenameOptions o, IProgress<RenameProgress>? progress = null, CancellationToken token = default)
        {
            var roots = folders.ToList();
            return Task.Run(() =>
            {
                var result = new RenameScanResult();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var clock = Stopwatch.StartNew();
                var last = TimeSpan.Zero;

                foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root))
                    {
                        result.MissingFolders++;
                        continue;
                    }
                    var pending = new Stack<DirectoryInfo>();
                    pending.Push(new DirectoryInfo(root));
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

                        foreach (var entry in entries)
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

                            if (o.SkipHiddenAndSystem && (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)))
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
                                pending.Push(sub);
                            }
                            else if (entry is FileInfo file)
                            {
                                if (IsTempName(file.FullName) || !seen.Add(file.FullName)) continue;
                                try
                                {
                                    result.Files.Add(new RenameSourceFile(file.FullName, file.LastWriteTime));
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    continue;
                                }
                                if (clock.Elapsed - last >= ReportEvery)
                                {
                                    last = clock.Elapsed;
                                    progress?.Report(new RenameProgress(RenamePhase.Scanning, result.Files.Count, 0, file.Name));
                                }
                            }
                        }
                    }
                }
                return result;
            }, token);
        }

        // =================================================================
        //  Run
        // =================================================================

        private sealed class Work
        {
            public required RenamePreviewItem Item { get; init; }
            public required string Original { get; init; }
            public required string Target { get; init; }
            public string Current { get; set; } = "";
            public bool Done { get; set; }
        }

        /// <summary>Test hook: runs before each move (e.g. to simulate a file appearing).</summary>
        internal static Action<string, string>? BeforeMove { get; set; }

        /// <summary>
        /// Renames every "Ready" row, in an order that works for swaps and
        /// chains: a file waits until its new name is free, and a circle
        /// (a→b, b→a) is broken by parking one file under a temporary name.
        /// Cancelling stops between files and never leaves one parked. Each
        /// row gets its outcome; the journal records every move for Undo.
        /// </summary>
        public static Task<(RenameRunResult Result, RenameJournal Journal)> RunAsync(
            IReadOnlyList<RenamePreviewItem> items,
            IProgress<RenameProgress>? progress = null,
            CancellationToken token = default) =>
            Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var result = new RenameRunResult();
                var journal = new RenameJournal { When = DateTime.Now };

                var work = items.Where(i => i.Status == RenameStatus.Ready)
                    .Select(i => new Work { Item = i, Original = i.FullPath, Target = i.NewFullPath, Current = i.FullPath })
                    .ToList();
                int total = work.Count;
                if (total == 0) return (result, journal);

                // Who sits on a path right now, and who wants it (names are unique per plan).
                var bySource = new Dictionary<string, Work>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in work) bySource.TryAdd(w.Current, w);
                var byTarget = new Dictionary<string, Work>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in work) byTarget.TryAdd(w.Target, w);

                bool Blocked(Work w) => bySource.TryGetValue(w.Target, out var other) && !ReferenceEquals(other, w);

                var queue = new Queue<Work>(work.Where(w => !Blocked(w)));
                var toSettle = new Queue<(string Path, Work Cause)>();   // a queue, not recursion: a 10,000-file chain can't overflow the stack
                int done = 0;
                long lastReport = 0;

                List<RenameJournalStep> steps = journal.Steps;
                void Record(string from, string to) => steps.Add(new RenameJournalStep { From = from, To = to });

                void Move(Work w, string to)
                {
                    BeforeMove?.Invoke(w.Current, to);
                    bool caseOnly = string.Equals(w.Current, to, StringComparison.OrdinalIgnoreCase);
                    if (!caseOnly && (File.Exists(to) || Directory.Exists(to)))
                        throw new IOException(Loc.F("Str_Rename_ErrAppeared", "A file named {0} appeared in the folder after the preview.", Path.GetFileName(to)));
                    File.Move(w.Current, to);
                    Record(w.Current, to);
                    w.Current = to;
                }

                void Succeed(Work w)
                {
                    w.Item.Status = RenameStatus.Renamed;
                    w.Item.Message = Loc.F("Str_Rename_MsgRenamedTo", "Renamed to {0}", Path.GetFileName(w.Target));
                    result.Renamed++;
                }

                void Settle(Work w, RenameStatus status, string message)
                {
                    w.Item.Status = status;
                    w.Item.Message = message;
                    if (status == RenameStatus.Failed) result.Failed++;
                    else result.Cancelled++;
                }

                // A file parked under a temporary name goes on to its new name if that's free, else back to its old one.
                string? Unpark(Work w)
                {
                    if (!IsTempName(w.Current) || !File.Exists(w.Current)) return null;
                    foreach (string home in new[] { w.Target, w.Original })
                    {
                        if (File.Exists(home)) continue;
                        try
                        {
                            File.Move(w.Current, home);
                            Record(w.Current, home);
                            w.Current = home;
                            return home;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            // try the other name
                        }
                    }
                    return null;
                }

                string StillParked(Work w) => IsTempName(w.Current)
                    ? Loc.F("Str_Rename_NoteStillCalled", " It's still called {0} - rename it back by hand.", Path.GetFileName(w.Current))
                    : "";

                void Complete(Work w, string from)
                {
                    w.Done = true;
                    done++;
                    long now = clock.ElapsedMilliseconds;
                    if (done == total || now - lastReport >= ReportEvery.TotalMilliseconds)
                    {
                        lastReport = now;
                        progress?.Report(new RenameProgress(RenamePhase.Renaming, done, total, w.Item.OriginalName));
                    }

                    bySource.Remove(from);
                    if (!string.Equals(w.Current, from, StringComparison.OrdinalIgnoreCase))
                    {
                        // from is free now: whoever wanted it can go.
                        if (byTarget.TryGetValue(from, out var waiting) && !waiting.Done) queue.Enqueue(waiting);
                    }
                    else
                    {
                        toSettle.Enqueue((from, w));
                    }
                }

                void DrainSettles()
                {
                    while (toSettle.Count > 0)
                    {
                        var (path, cause) = toSettle.Dequeue();
                        SettleWaiters(path, cause);
                    }
                }

                // w is staying where it is, so whoever wanted its name can't have it - nor can anyone waiting on them.
                void SettleWaiters(string path, Work cause)
                {
                    if (!byTarget.TryGetValue(path, out var waiting) || waiting.Done) return;
                    string from = waiting.Current;
                    string? home = Unpark(waiting);
                    if (home != null && string.Equals(home, waiting.Target, StringComparison.OrdinalIgnoreCase))
                        Succeed(waiting);
                    else if (cause.Item.Status == RenameStatus.Cancelled)
                        Settle(waiting, RenameStatus.Cancelled, Loc.T("Str_Rename_MsgNotRenamed", "Not renamed - the run was cancelled.") + StillParked(waiting));
                    else
                        Settle(waiting, RenameStatus.Failed,
                            Loc.F("Str_Rename_ErrStillTaken", "{0} is still taken, because {1} couldn't be renamed first.", Path.GetFileName(path), cause.Item.OriginalName) + StillParked(waiting));
                    Complete(waiting, from);
                }

                void Finish(Work w)
                {
                    string from = w.Current;
                    try
                    {
                        if (!File.Exists(from)) throw new FileNotFoundException(Loc.T("Str_Files_ErrGone", "The file is no longer there."));
                        Move(w, w.Target);
                        Succeed(w);
                    }
                    catch (Exception ex)
                    {
                        string? home = Unpark(w);
                        if (home != null && string.Equals(home, w.Target, StringComparison.OrdinalIgnoreCase)) Succeed(w);
                        else Settle(w, RenameStatus.Failed, ex.Message + (home != null ? " It kept its old name." : StillParked(w)));
                    }
                    Complete(w, from);
                }

                void Cancel(Work w)
                {
                    string from = w.Current;
                    string? home = Unpark(w);   // never leave a file under a temporary name
                    if (home != null && string.Equals(home, w.Target, StringComparison.OrdinalIgnoreCase)) Succeed(w);
                    else Settle(w, RenameStatus.Cancelled, Loc.T("Str_Rename_MsgNotRenamed", "Not renamed - the run was cancelled.") + StillParked(w));
                    Complete(w, from);
                }

                while (true)
                {
                    while (queue.Count > 0)
                    {
                        var w = queue.Dequeue();
                        if (w.Done || Blocked(w)) continue;   // blocked ones come back when their name frees up
                        if (token.IsCancellationRequested && !IsTempName(w.Current)) Cancel(w);
                        else Finish(w);
                        DrainSettles();
                    }

                    var left = work.Where(w => !w.Done).ToList();
                    if (left.Count == 0) break;

                    if (token.IsCancellationRequested)
                    {
                        foreach (var w in left.Where(w => !w.Done))
                        {
                            Cancel(w);
                            DrainSettles();
                        }
                        continue;
                    }

                    // Everything left waits on another file that's also waiting: a circle (a→b, b→a).
                    // Park one file under a temporary name to break it.
                    var park = left.FirstOrDefault(w => !IsTempName(w.Current));
                    if (park == null)
                    {
                        foreach (var w in left.Where(w => !w.Done))
                        {
                            Finish(w);   // can't happen, but never loop forever
                            DrainSettles();
                        }
                        continue;
                    }
                    string before = park.Current;
                    try
                    {
                        Move(park, Path.Combine(Path.GetDirectoryName(before)!,
                            $"{Path.GetFileName(park.Original)}{TempMarker}{Guid.NewGuid().ToString("N")[..8]}.tmp"));
                        bySource.Remove(before);
                        bySource[park.Current] = park;
                        if (byTarget.TryGetValue(before, out var waiting) && !waiting.Done) queue.Enqueue(waiting);
                    }
                    catch (Exception ex)
                    {
                        Settle(park, RenameStatus.Failed, ex.Message);
                        Complete(park, before);
                        DrainSettles();
                    }
                }

                journal.FileCount = result.Renamed;
                result.Elapsed = clock.Elapsed;
                return (result, journal);
            });

        // =================================================================
        //  Undo
        // =================================================================

        /// <summary>
        /// Plays the journal backwards, so swaps and chains unwind in the
        /// right order. A change of case only is undone too (the old undo
        /// saw "photo.jpg" as already there and gave up). If the old name has
        /// been taken since, the file comes back as " (2)". Cancelling keeps
        /// the steps not yet undone in <see cref="RenameUndoResult.Remaining"/>.
        /// </summary>
        public static Task<RenameUndoResult> UndoAsync(RenameJournal journal, IProgress<RenameProgress>? progress = null, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new RenameUndoResult();
                var clock = Stopwatch.StartNew();
                long last = 0;
                List<RenameJournalStep> steps = journal.Steps;

                for (int i = steps.Count - 1; i >= 0; i--)
                {
                    if (token.IsCancellationRequested)
                    {
                        result.Remaining.AddRange(steps.Take(i + 1));
                        break;
                    }

                    var step = steps[i];
                    bool counts = !IsTempName(step.From);   // a stop-over isn't a file of its own
                    try
                    {
                        if (!File.Exists(step.To))
                        {
                            if (counts) result.Missing++;
                            continue;
                        }
                        bool caseOnly = string.Equals(step.To, step.From, StringComparison.OrdinalIgnoreCase);
                        if (!caseOnly && File.Exists(step.From))
                        {
                            string alt = UniqueName(step.From);
                            File.Move(step.To, alt);
                            if (counts) result.RestoredWithNewName++;
                            continue;
                        }
                        File.Move(step.To, step.From);
                        if (counts) result.Restored++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Errors.Add($"{Path.GetFileName(step.To)}: {ex.Message}");
                    }

                    int done = steps.Count - i;
                    if (done == 1 || i == 0 || clock.ElapsedMilliseconds - last >= ReportEvery.TotalMilliseconds)
                    {
                        last = clock.ElapsedMilliseconds;
                        progress?.Report(new RenameProgress(RenamePhase.Undoing, done, steps.Count, Path.GetFileName(step.From)));
                    }
                }
                return result;
            });

        public static string UniqueName(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileName(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, WithCounter(name, n));
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }
    }
}
