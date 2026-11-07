using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Large File Finder engine: walks a folder or drive (a few folders at a
    /// time), keeps the N largest files (a small heap - the full list is
    /// never sorted in memory), sends live snapshots, and deletes or moves
    /// files off the UI thread. No WPF, so it's tested in a plain test project.
    ///
    /// Fixed against the old LargeFinderManager / view:
    ///  - Folder links were followed: a link back up the tree listed the
    ///    same files again and again (41 copies of one file in the test).
    ///  - Cancel threw everything away; it now keeps what was found.
    ///  - Nothing appeared until the scan ended; the biggest files so far
    ///    now show up while it runs.
    ///  - One task per folder was kept alive for the whole scan (hundreds of
    ///    thousands on a full drive); now a fixed set of workers.
    ///  - Moving a file into the folder it's already in renamed it to
    ///    "name (1)"; a folder with the file's name at the destination made
    ///    the move fail.
    ///  - Moves and deletes ran on the UI thread (moving a 50 GB file to
    ///    another drive froze the window for minutes) with one error box per
    ///    file; they now run in the background with progress and a summary,
    ///    and a move to another drive can be cancelled without leaving a
    ///    half copy.
    ///  - "Send to the Recycle Bin" was promised even on drives that have
    ///    none (USB sticks, network shares), where Windows deletes for good.
    ///  - The drive list was read on the UI thread when the view opened (a
    ///    sleeping network drive froze it) and never refreshed.
    /// </summary>
    public static class LargeFileFinderManager
    {
        public const int SnapshotSize = 200;

        /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: an online-only cloud file (OneDrive "available online").</summary>
        private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
        public static TimeSpan ReportEvery { get; set; } = TimeSpan.FromMilliseconds(150);
        public static TimeSpan SnapshotEvery { get; set; } = TimeSpan.FromMilliseconds(500);

        private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi", "$WinREAgent", "$SysReset",
        };

        public static readonly IReadOnlyList<(string Name, long Bytes)> Units = new[]
        {
            ("KB", 1024L), ("MB", 1024L * 1024), ("GB", 1024L * 1024 * 1024),
        };

        /// <summary>"1.5" + GB → bytes. Null when it isn't a number ≥ 0.</summary>
        public static long? ParseSize(string? text, int unitIndex)
        {
            string t = (text ?? "").Trim().Replace(" ", "").Replace(',', '.');
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0 || !double.IsFinite(v)) return null;
            return (long)Math.Round(v * Units[Math.Clamp(unitIndex, 0, Units.Count - 1)].Bytes);
        }

        // =================================================================
        //  Categories
        // =================================================================

        private static readonly Dictionary<string, LargeFileCategory> CategoryByExtension = BuildCategories();

        private static Dictionary<string, LargeFileCategory> BuildCategories()
        {
            var map = new Dictionary<string, LargeFileCategory>(StringComparer.OrdinalIgnoreCase);
            void Add(LargeFileCategory c, string list)
            {
                foreach (var e in list.Split(' ')) map["." + e] = c;
            }
            Add(LargeFileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts vob 3gp");
            Add(LargeFileCategory.DiskImage, "iso img vhd vhdx vmdk vdi qcow2 dmg wim esd");
            Add(LargeFileCategory.Archive, "zip rar 7z tar gz tgz bz2 xz zst cab");
            Add(LargeFileCategory.Audio, "mp3 wav flac aac m4a ogg wma opus aiff");
            Add(LargeFileCategory.Image, "jpg jpeg png gif bmp tif tiff psd raw cr2 nef arw dng heic webp");
            Add(LargeFileCategory.Document, "pdf doc docx xls xlsx ppt pptx txt csv log pst ost epub");
            Add(LargeFileCategory.Program, "exe msi msix appx dll sys bin apk");
            Add(LargeFileCategory.Database, "db sqlite mdf ldf bak sql accdb mdb dat");
            return map;
        }

        public static LargeFileCategory CategoryOf(string path) =>
            CategoryByExtension.TryGetValue(Path.GetExtension(path), out var c) ? c : LargeFileCategory.Other;

        public static string CategoryName(LargeFileCategory c) => c switch
        {
            LargeFileCategory.DiskImage => "Disk images",
            LargeFileCategory.Video => "Videos",
            LargeFileCategory.Archive => "Archives",
            LargeFileCategory.Audio => "Audio",
            LargeFileCategory.Image => "Pictures",
            LargeFileCategory.Document => "Documents",
            LargeFileCategory.Program => "Programs",
            LargeFileCategory.Database => "Data & backups",
            _ => "Other",
        };

        /// <summary>Data colours for the size bar (same meaning in both themes).</summary>
        public static string ColorFor(LargeFileCategory c) => c switch
        {
            LargeFileCategory.Video => "#E5484D",
            LargeFileCategory.DiskImage => "#8E4EC6",
            LargeFileCategory.Archive => "#F5A524",
            LargeFileCategory.Audio => "#12A594",
            LargeFileCategory.Image => "#3E63DD",
            LargeFileCategory.Document => "#0090FF",
            LargeFileCategory.Program => "#D6409F",
            LargeFileCategory.Database => "#AD7F58",
            _ => "#8B8D98",
        };

        // =================================================================
        //  Drives
        // =================================================================

        /// <summary>Ready drives, system drive first. Call off the UI thread.</summary>
        public static List<LargeFileDrive> GetDrives()
        {
            var list = new List<LargeFileDrive>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    list.Add(new LargeFileDrive
                    {
                        RootPath = d.RootDirectory.FullName,
                        Label = d.VolumeLabel,
                        FreeBytes = d.AvailableFreeSpace,
                        TotalBytes = d.TotalSize,
                        IsFixed = d.DriveType == DriveType.Fixed,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
            return list.OrderByDescending(d => string.Equals(d.RootPath, system, StringComparison.OrdinalIgnoreCase))
                       .ThenBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // =================================================================
        //  Scan
        // =================================================================

        /// <summary>Ranks smallest first, so the heap's top is the one to drop (ties: the later path goes first).</summary>
        private sealed class WorstFirst : IComparer<(long Size, string Path)>
        {
            public int Compare((long Size, string Path) a, (long Size, string Path) b)
            {
                int c = a.Size.CompareTo(b.Size);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(b.Path, a.Path);
            }
        }

        private static int Rank(LargeFileItem a, LargeFileItem b)
        {
            int c = b.SizeBytes.CompareTo(a.SizeBytes);
            return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
        }

        private readonly record struct Entry(string Path, long Size, DateTime Modified);

        public static Task<LargeFileScanResult> ScanAsync(LargeFileOptions o, IProgress<LargeFileProgress>? progress, CancellationToken token) =>
            Task.Run(() => Scan(o, progress, token), CancellationToken.None);

        /// <summary>
        /// Walks the tree with a few workers. Links (and, by choice, hidden and
        /// system items) are skipped; an unreadable folder is counted, never
        /// fatal. Cancel returns what was found so far (Cancelled = true).
        /// </summary>
        public static LargeFileScanResult Scan(LargeFileOptions o, IProgress<LargeFileProgress>? progress, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(o.RootPath) || !Directory.Exists(o.RootPath))
                return new LargeFileScanResult { Error = "That folder isn't there or can't be opened." };

            var chosen = (o.IgnoreSystemFiles ? FileAttributes.System : 0) | (o.ShowHiddenFiles ? 0 : FileAttributes.Hidden);
            // Folders: never follow links. Files: skip online-only cloud files (not on this disk), but keep
            // OneDrive files that are downloaded - those are reparse points too.
            var folderOptions = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = chosen | FileAttributes.ReparsePoint, RecurseSubdirectories = false };
            var fileOptions = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = chosen | FileAttributes.Offline | RecallOnDataAccess, RecurseSubdirectories = false };

            var heap = new PriorityQueue<LargeFileItem, (long, string)>(new WorstFirst());
            var all = new List<LargeFileItem>();
            var gate = new object();
            int matches = 0;
            long matchedBytes = 0, files = 0, folders = 0;
            int inaccessible = 0;
            string current = o.RootPath;

            var pending = new ConcurrentStack<string>();
            pending.Push(Path.GetFullPath(o.RootPath));
            long outstanding = 1;

            void Add(in Entry e)
            {
                var item = new LargeFileItem { FullPath = e.Path, SizeBytes = e.Size, Modified = e.Modified, Category = CategoryOf(e.Path) };
                lock (gate)
                {
                    matches++;
                    matchedBytes += e.Size;
                    if (o.ResultLimit <= 0)
                    {
                        all.Add(item);
                        return;
                    }
                    var key = (e.Size, e.Path);
                    if (heap.Count < o.ResultLimit) heap.Enqueue(item, key);
                    else if (heap.TryPeek(out _, out var worst) && new WorstFirst().Compare(key, worst) > 0) heap.DequeueEnqueue(item, key);
                }
            }

            void Visit(string dir)
            {
                try
                {
                    var found = new FileSystemEnumerable<Entry>(dir, (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.LocalDateTime), fileOptions)
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                    };
                    long count = 0;
                    foreach (var e in found)
                    {
                        count++;
                        if (e.Size >= o.MinSizeBytes) Add(e);
                    }
                    Interlocked.Add(ref files, count);

                    if (o.IncludeSubfolders)
                    {
                        var subs = new FileSystemEnumerable<string>(dir, (ref FileSystemEntry e) => e.ToFullPath(), folderOptions)
                        {
                            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory,
                        }.Where(p => !(o.IgnoreSystemFiles && SystemFolderNames.Contains(Path.GetFileName(p)))).ToList();
                        if (subs.Count > 0)
                        {
                            Interlocked.Add(ref outstanding, subs.Count);
                            subs.Sort(StringComparer.OrdinalIgnoreCase);
                            for (int i = subs.Count - 1; i >= 0; i--) pending.Push(subs[i]);
                        }
                    }
                    Interlocked.Increment(ref folders);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    Interlocked.Increment(ref inaccessible);
                }
            }

            void Worker()
            {
                var spin = new SpinWait();
                while (Interlocked.Read(ref outstanding) > 0 && !token.IsCancellationRequested)
                {
                    if (!pending.TryPop(out var dir))
                    {
                        spin.SpinOnce();
                        continue;
                    }
                    spin.Reset();
                    Volatile.Write(ref current, dir);
                    Visit(dir);
                    Interlocked.Decrement(ref outstanding);
                }
            }

            List<LargeFileItem> Snapshot(int max)
            {
                lock (gate)
                {
                    var list = o.ResultLimit > 0 ? heap.UnorderedItems.Select(x => x.Element).ToList() : all.ToList();
                    list.Sort(Rank);
                    return list.Count > max ? list.GetRange(0, max) : list;
                }
            }

            var workers = Enumerable.Range(0, Math.Max(1, o.Workers)).Select(_ => Task.Run(Worker)).ToArray();
            var lastSnapshot = Stopwatch.StartNew();
            while (!Task.WaitAll(workers, ReportEvery))
            {
                if (progress == null) continue;
                bool snap = lastSnapshot.Elapsed >= SnapshotEvery;
                if (snap) lastSnapshot.Restart();
                int m;
                long mb;
                lock (gate)
                {
                    m = matches;
                    mb = matchedBytes;
                }
                progress.Report(new LargeFileProgress
                {
                    FilesScanned = Interlocked.Read(ref files),
                    FoldersScanned = Interlocked.Read(ref folders),
                    Matches = m,
                    MatchedBytes = mb,
                    CurrentFolder = Volatile.Read(ref current),
                    Top = snap ? Snapshot(SnapshotSize) : null,
                });
            }

            var result = Snapshot(int.MaxValue);
            return new LargeFileScanResult
            {
                Files = result,
                TotalMatches = matches,
                TotalMatchedBytes = matchedBytes,
                FilesScanned = files,
                FoldersScanned = folders,
                InaccessibleFolders = inaccessible,
                Cancelled = token.IsCancellationRequested,
                Elapsed = clock.Elapsed,
            };
        }

        /// <summary>Sets each row's bar against the biggest file shown.</summary>
        public static void ScaleBars(IReadOnlyList<LargeFileItem> items)
        {
            long top = items.Count == 0 ? 0 : items.Max(i => i.SizeBytes);
            foreach (var i in items) i.BarPercent = top <= 0 ? 0 : Math.Max(2, i.SizeBytes * 100.0 / top);
        }

        /// <summary>Total size per category, biggest first (for the size bar and the filter chips).</summary>
        public static List<(LargeFileCategory Category, int Count, long Bytes)> Breakdown(IEnumerable<LargeFileItem> items) =>
            items.GroupBy(i => i.Category)
                 .Select(g => (g.Key, g.Count(), g.Sum(i => i.SizeBytes)))
                 .OrderByDescending(x => x.Item3)
                 .ToList();

        public static string Describe(LargeFileScanResult r, int shown)
        {
            if (r.Error != null) return r.Error;
            var parts = new List<string>
            {
                $"{r.TotalMatches:N0} large {LargeFileText.Plural(r.TotalMatches, "file", "files")} ({LargeFileText.Bytes(r.TotalMatchedBytes)}) among {r.FilesScanned:N0} files in {r.FoldersScanned:N0} folders",
            };
            if (shown < r.TotalMatches) parts.Add($"showing the {shown:N0} biggest");
            if (r.InaccessibleFolders > 0) parts.Add($"{r.InaccessibleFolders:N0} {LargeFileText.Plural(r.InaccessibleFolders, "folder", "folders")} couldn't be opened");
            parts.Add(r.Elapsed.TotalSeconds < 1 ? $"{r.Elapsed.TotalMilliseconds:0} ms" : $"{r.Elapsed.TotalSeconds:0.0} s");
            string text = string.Join(" · ", parts) + ".";
            return r.Cancelled ? "Stopped. " + text : text;
        }

        // =================================================================
        //  Delete
        // =================================================================

        /// <summary>Only local fixed drives have a Recycle Bin; elsewhere Windows deletes for good.</summary>
        public static bool HasRecycleBin(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                return OperatingSystem.IsWindows() && new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>To the Recycle Bin where there is one, otherwise permanently (the view says which before asking).</summary>
        public static Task<LargeFileActionResult> DeleteAsync(IReadOnlyList<LargeFileItem> items, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new LargeFileActionResult();
                foreach (var item in items)
                {
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    try
                    {
                        if (!File.Exists(item.FullPath))
                        {
                            result.Failed.Add((item, "It isn't there any more."));
                            continue;
                        }
                        if (HasRecycleBin(item.FullPath))
                            FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        else
                        {
                            File.Delete(item.FullPath);
                            result.Permanent = true;
                        }
                        result.Succeeded.Add(item);
                        result.Bytes += item.SizeBytes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        result.Failed.Add((item, PlainError(ex)));
                    }
                }
                return result;
            }, CancellationToken.None);

        // =================================================================
        //  Move
        // =================================================================

        /// <summary>Same drive = an instant rename; another drive = a copy (with progress) then delete.</summary>
        public static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        /// <summary>Why the move can't start (destination missing space, inside the same folder...), or null.</summary>
        public static string? CheckMove(IReadOnlyList<LargeFileItem> items, string destination)
        {
            try
            {
                string dest = Path.GetFullPath(destination);
                if (items.All(i => string.Equals(Path.GetDirectoryName(i.FullPath), dest.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                    return "The files are already in that folder.";
                long crossBytes = items.Where(i => !SameVolume(i.FullPath, dest)).Sum(i => i.SizeBytes);
                if (crossBytes > 0)
                {
                    string? root = Path.GetPathRoot(dest);
                    if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        long free = new DriveInfo(root).AvailableFreeSpace;
                        if (free < crossBytes) return $"Not enough space there: {LargeFileText.Bytes(crossBytes)} to copy, {LargeFileText.Bytes(free)} free.";
                    }
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Moves the files off the UI thread. A file already in that folder is
        /// left alone; another drive means a copy under a temporary name
        /// (renamed when complete, original deleted after) - cancel removes
        /// the half copy and leaves the original untouched.
        /// </summary>
        public static Task<LargeFileActionResult> MoveAsync(IReadOnlyList<LargeFileItem> items, string destination, IProgress<LargeFileMoveProgress>? progress, CancellationToken token) =>
            Task.Run(() =>
            {
                var result = new LargeFileActionResult();
                string dest = Path.GetFullPath(destination);
                Directory.CreateDirectory(dest);
                long total = items.Sum(i => i.SizeBytes), before = 0;
                for (int n = 0; n < items.Count; n++)
                {
                    var item = items[n];
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    progress?.Report(new LargeFileMoveProgress(n, items.Count, item.Name, before, total));
                    try
                    {
                        if (!File.Exists(item.FullPath))
                        {
                            result.Failed.Add((item, "It isn't there any more."));
                        }
                        else if (string.Equals(Path.GetDirectoryName(item.FullPath), dest.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        {
                            result.Failed.Add((item, "It's already in that folder."));
                        }
                        else
                        {
                            string target = UniquePath(Path.Combine(dest, item.Name));
                            if (SameVolume(item.FullPath, dest)) File.Move(item.FullPath, target);
                            else
                            {
                                long baseDone = before;
                                string? warning = CopyThenDelete(item.FullPath, target, done => progress?.Report(new LargeFileMoveProgress(n, items.Count, item.Name, baseDone + done, total)), token);
                                if (warning != null) result.Warnings.Add((item, warning));
                            }
                            result.Succeeded.Add(item);
                            result.Bytes += item.SizeBytes;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.Failed.Add((item, PlainError(ex)));
                    }
                    before += item.SizeBytes;
                }
                progress?.Report(new LargeFileMoveProgress(items.Count, items.Count, "", total, total));
                return result;
            }, CancellationToken.None);

        /// <summary>
        /// Copies to a temporary name next to the target, renames it when
        /// complete (keeping the modified date), then deletes the source.
        /// Returns a warning when the original couldn't be removed.
        /// </summary>
        public static string? CopyThenDelete(string source, string target, Action<long>? onBytes, CancellationToken token)
        {
            string temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.part");
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    output.SetLength(input.Length);   // reserves the space up front (fails early on a full disk)
                    var buffer = new byte[4 << 20];
                    long done = 0;
                    var last = Stopwatch.StartNew();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        if (last.ElapsedMilliseconds >= 100)
                        {
                            onBytes?.Invoke(done);
                            last.Restart();
                        }
                    }
                    output.SetLength(done);
                }
                File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
                File.Move(temp, target);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
            try
            {
                File.Delete(source);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "Copied, but the original couldn't be removed: " + PlainError(ex);
            }
        }

        public static string PlainError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows didn't allow it (read-only, or needs admin rights).",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "It's open in another program.",
            IOException io when (io.HResult & 0xFFFF) is 39 or 112 || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase) => "The destination drive is full.",
            OperationCanceledException => "Cancelled.",
            _ => ex.Message,
        };

        // =================================================================
        //  Export (CSV)
        // =================================================================

        public static Task ExportCsvAsync(IReadOnlyList<LargeFileItem> items, string path) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var w = new StreamWriter(temp, false, new UTF8Encoding(true)))
                    {
                        w.WriteLine("Name,Folder,Size (bytes),Size,Type,Kind,Modified");
                        foreach (var i in items)
                            w.WriteLine(string.Join(",", Cell(i.Name), Cell(i.Folder), i.SizeBytes, Cell(i.SizeDisplay), Cell(i.TypeLabel), Cell(CategoryName(i.Category)),
                                i.Modified == default ? "" : i.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
                    }
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            });

        public static string Cell(string value)
        {
            string v = value ?? "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v != v.Trim() ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\large_file_finder.json - no paths)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "large_file_finder.json");

        public static LargeFileSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new LargeFileSettings();
                var s = JsonSerializer.Deserialize<LargeFileSettings>(File.ReadAllText(file), SettingsJson) ?? new LargeFileSettings();
                if (!Enum.IsDefined(s.Scope)) s.Scope = LargeFileScope.Folder;
                s.MinUnit = Math.Clamp(s.MinUnit, 0, Units.Count - 1);
                s.MinSize ??= "100";
                if (s.ResultLimit is not (0 or 25 or 50 or 100 or 500)) s.ResultLimit = 50;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new LargeFileSettings();
            }
        }

        public static void SaveSettings(LargeFileSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort: AtomicFile already removed its temp file
            }
        }
    }
}
