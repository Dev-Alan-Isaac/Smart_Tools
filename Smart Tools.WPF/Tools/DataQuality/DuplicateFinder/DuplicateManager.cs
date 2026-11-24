using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SearchOption = System.IO.SearchOption;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Duplicate Finder backend: finding duplicate groups, choosing which
    /// copy to keep, and moving/deleting the rest. No WPF types.
    ///
    /// Content matching runs as a funnel so most files are read as little
    /// as possible:
    /// 1. size - files of a unique size can't have a duplicate (free);
    /// 2. quick fingerprint - hash of the first and last 16 KB (cheap,
    ///    rules out most same-size files that differ);
    /// 3. full hash (or byte-by-byte clustering) - only for what's left;
    /// 4. optional byte compare to confirm each hash group.
    /// </summary>
    public sealed partial class DuplicateManager
    {
        private const int BufferSize = 1 << 20;                  // 1 MB
        private const int FingerprintBytes = 16 * 1024;          // head and tail each
        private const long ReportIntervalMs = 100;

        private static readonly Dictionary<FileTypeFilter, string[]> TypeExtensions = new()
        {
            [FileTypeFilter.Images] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".psd", ".svg", ".ico", ".avif", ".jxl" },
            [FileTypeFilter.Videos] = new[] { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".mts", ".m2ts", ".ts", ".vob" },
            [FileTypeFilter.Audio] = new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".alac", ".ape" },
            [FileTypeFilter.Documents] = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".txt", ".md", ".csv", ".epub", ".pages", ".numbers", ".key" },
        };

        public static IReadOnlyCollection<string> ExtensionsFor(FileTypeFilter filter) =>
            TypeExtensions.TryGetValue(filter, out var list) ? list : Array.Empty<string>();

        // =================================================================
        //  Scan
        // =================================================================

        internal sealed class ScanState
        {
            public long FilesFound;
            public long BytesProcessed;
            public long BytesTotal;
            public int InaccessibleFolders;
            public int SkippedLinks;
            public int UnreadableFiles;
            public bool MediaSkippedNoFfmpeg;
            public bool HeavyLimited;
            public bool AudioLimited;
            public DuplicateScanPhase Phase;
            public long LastReport = long.MinValue;
            public readonly Stopwatch Watch = Stopwatch.StartNew();
            public IProgress<DuplicateScanProgress>? Progress;
            public readonly object Lock = new();

            public void Report(string path, bool force = false)
            {
                if (Progress == null) return;
                lock (Lock)
                {
                    long now = Watch.ElapsedMilliseconds;
                    if (!force && now - LastReport < ReportIntervalMs) return;
                    LastReport = now;
                    Progress.Report(new DuplicateScanProgress(Phase, FilesFound, Interlocked.Read(ref BytesProcessed), BytesTotal, path));
                }
            }
        }

        /// <summary>
        /// Finds duplicate groups. Runs entirely off the calling thread - the
        /// old version listed and grouped files on the UI thread, freezing
        /// the window on big folders. Groups come back biggest-savings first.
        /// </summary>
        public Task<DuplicateScanResult> ScanAsync(DuplicateScanOptions options, IProgress<DuplicateScanProgress>? progress, CancellationToken token = default) =>
            Task.Run(() => ScanCoreAsync(options, progress, token), token);

        private static async Task<DuplicateScanResult> ScanCoreAsync(DuplicateScanOptions options, IProgress<DuplicateScanProgress>? progress, CancellationToken token)
        {
            var state = new ScanState { Progress = progress, Phase = DuplicateScanPhase.Listing };
            var files = EnumerateCandidateFiles(options, state, token);

            state.Phase = DuplicateScanPhase.Comparing;
            List<(List<FileInfo> Files, string Hash)> raw = options.MatchMode switch
            {
                DuplicateMatchMode.FileName => files
                    .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => (g.ToList(), ""))
                    .ToList(),

                DuplicateMatchMode.NameAndSize => files
                    .GroupBy(f => (Name: f.Name.ToUpperInvariant(), f.Length))
                    .Where(g => g.Count() > 1)
                    .Select(g => (g.ToList(), ""))
                    .ToList(),

                _ => await GroupByContentAsync(files, options, state, token).ConfigureAwait(false),
            };

            var groups = options.MatchMode == DuplicateMatchMode.SameContent
                ? await BuildSameContentGroupsAsync(files, raw, options, state, token).ConfigureAwait(false)
                : BuildGroups(raw, options.IsContentMode);

            state.Phase = DuplicateScanPhase.Done;
            state.Report("", force: true);
            return new DuplicateScanResult
            {
                MediaSkippedNoFfmpeg = state.MediaSkippedNoFfmpeg,
                HeavyLimited = state.HeavyLimited,
                AudioLimited = state.AudioLimited,
                Groups = groups,
                FilesScanned = files.Count,
                InaccessibleFolders = state.InaccessibleFolders,
                SkippedLinks = state.SkippedLinks,
                UnreadableFiles = state.UnreadableFiles,
                Elapsed = state.Watch.Elapsed,
                MatchMode = options.MatchMode,
                IsContentVerified = options.IsContentMode,
            };
        }

        /// <summary>Turns raw file groups into numbered groups, biggest potential savings first.</summary>
        internal static List<DuplicateGroup> BuildGroups(IEnumerable<(List<FileInfo> Files, string Hash)> raw, bool contentVerified)
        {
            var groups = new List<DuplicateGroup>();
            foreach (var (files, hash) in raw)
            {
                if (files.Count < 2) continue;
                var group = new DuplicateGroup { Hash = hash, IsContentVerified = contentVerified };
                foreach (var f in files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    group.Files.Add(new DuplicateFileItem
                    {
                        FullPath = f.FullName,
                        Name = f.Name,
                        Folder = f.DirectoryName ?? "",
                        SizeBytes = f.Length,
                        ModifiedTime = SafeLastWrite(f),
                        Hash = hash,
                        GroupInfo = group,
                    });
                }
                groups.Add(group);
            }

            groups.Sort((a, b) => b.PotentialBytes.CompareTo(a.PotentialBytes));
            for (int i = 0; i < groups.Count; i++) groups[i].Number = i + 1;
            return groups;
        }

        private static DateTime SafeLastWrite(FileInfo f)
        {
            try
            {
                return f.LastWriteTime;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return default;
            }
        }

        // ---------------- Listing ----------------

        private static List<FileInfo> EnumerateCandidateFiles(DuplicateScanOptions options, ScanState state, CancellationToken token)
        {
            var files = new List<FileInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string e in options.IncludedExtensions) include.Add(NormalizeExtension(e));
            foreach (string e in ExtensionsFor(options.FileTypes)) include.Add(e);
            var exclude = new HashSet<string>(options.ExcludedExtensions.Select(NormalizeExtension), StringComparer.OrdinalIgnoreCase);

            var enumeration = new EnumerationOptions
            {
                IgnoreInaccessible = false,   // so unreadable folders throw and get counted
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            };

            // Iterative walk, one folder at a time, so one locked folder
            // doesn't abort the rest. Junctions/symlinked folders are not
            // followed - the old walk followed them (double counting, loops).
            foreach (string rootPath in options.Folders)
            {
                if (!Directory.Exists(rootPath)) continue;

                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(rootPath));

                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var dir = pending.Pop();

                    FileSystemInfo[] entries;
                    try
                    {
                        entries = dir.GetFileSystemInfos("*", enumeration);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        state.InaccessibleFolders++;
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

                        if (options.IgnoreHiddenFiles && attrs.HasFlag(FileAttributes.Hidden)) continue;
                        if (options.IgnoreSystemFiles && attrs.HasFlag(FileAttributes.System)) continue;

                        if (entry is DirectoryInfo sub)
                        {
                            if (!options.IncludeSubfolders) continue;
                            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                            {
                                state.SkippedLinks++;
                                continue;
                            }
                            pending.Push(sub);
                            continue;
                        }

                        if (entry is not FileInfo file) continue;
                        if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;   // symlinked file - the target is counted where it lives

                        long length;
                        try
                        {
                            length = file.Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        if (length < Math.Max(1, options.MinFileSizeBytes)) continue;

                        string ext = file.Extension;
                        if (include.Count > 0 && !include.Contains(ext)) continue;
                        if (exclude.Contains(ext)) continue;

                        if (!seen.Add(file.FullName)) continue;   // overlapping folders

                        files.Add(file);
                        state.FilesFound++;
                        state.Report(file.FullName);
                    }
                }
            }

            state.Report("", force: true);
            return files;
        }

        public static string NormalizeExtension(string ext)
        {
            ext = ext.Trim().TrimStart('*');
            if (ext.Length == 0) return ext;
            return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
        }

        /// <summary>".tmp, log;*.bak" -> [".tmp", ".log", ".bak"].</summary>
        public static string[] ParseExtensionList(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(NormalizeExtension)
                      .Where(e => e.Length > 1)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();

        // ---------------- Content matching ----------------

        private static async Task<List<(List<FileInfo> Files, string Hash)>> GroupByContentAsync(
            List<FileInfo> files, DuplicateScanOptions options, ScanState state, CancellationToken token)
        {
            // 1. Same size (free).
            var sizeGroups = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();

            // 2. Quick fingerprint (head + tail) - cheap, rules out most same-size non-duplicates.
            var candidates = sizeGroups.SelectMany(g => g).ToList();
            state.BytesTotal = candidates.Sum(f => Math.Min(f.Length, 2L * FingerprintBytes));
            var fingerprints = new ConcurrentDictionary<FileInfo, string>();

            await ForEachFileAsync(candidates, options.MaxDegreeOfParallelism, token, async (file, ct) =>
            {
                string? fp = await TryFingerprintAsync(file, state, ct).ConfigureAwait(false);
                if (fp != null) fingerprints[file] = fp;
            }).ConfigureAwait(false);

            var prefiltered = candidates
                .Where(fingerprints.ContainsKey)
                .GroupBy(f => (f.Length, Fp: fingerprints[f]))
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            // Files no bigger than both fingerprint windows were read completely - already exact.
            var exact = prefiltered.Where(g => g[0].Length <= 2L * FingerprintBytes).ToList();
            var needFull = prefiltered.Where(g => g[0].Length > 2L * FingerprintBytes).ToList();

            // 3. Full comparison, progress now measured over the bytes still to read.
            state.BytesProcessed = 0;
            state.BytesTotal = needFull.Sum(g => g.Sum(f => f.Length));
            state.Report("", force: true);

            var results = new List<(List<FileInfo>, string)>();

            if (options.MatchMode == DuplicateMatchMode.ByteByByte)
            {
                foreach (var g in exact) results.Add((g, ""));
                foreach (var g in needFull)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var cluster in await ClusterByBytesAsync(g, state, token).ConfigureAwait(false))
                        if (cluster.Count > 1) results.Add((cluster, ""));
                }
                return results;
            }

            var hashes = new ConcurrentDictionary<FileInfo, string>();
            await ForEachFileAsync(prefiltered.SelectMany(g => g).ToList(), options.MaxDegreeOfParallelism, token, async (file, ct) =>
            {
                try
                {
                    string hash = await ComputeHashAsync(file.FullName, options.MatchMode, state, file.Length > 2L * FingerprintBytes, ct).ConfigureAwait(false);
                    hashes[file] = hash;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref state.UnreadableFiles);
                }
            }).ConfigureAwait(false);

            foreach (var g in hashes.GroupBy(kv => (kv.Key.Length, kv.Value)).Where(g => g.Count() > 1))
                results.Add((g.Select(kv => kv.Key).ToList(), g.Key.Value));

            // 4. Confirm byte for byte. A hash collision is astronomically
            // unlikely, but this list feeds a delete - small files were
            // already compared in full by the fingerprint.
            if (options.VerifyWithByteCompare && options.MatchMode != DuplicateMatchMode.ByteByByte)
            {
                state.Phase = DuplicateScanPhase.Verifying;
                state.BytesProcessed = 0;
                state.BytesTotal = results.Where(r => r.Item1[0].Length > 2L * FingerprintBytes).Sum(r => r.Item1.Sum(f => f.Length));
                results = await VerifyGroupsAsync(results, state, token).ConfigureAwait(false);
            }

            return results;
        }

        private static async Task ForEachFileAsync(List<FileInfo> files, int parallelism, CancellationToken token, Func<FileInfo, CancellationToken, Task> body)
        {
            // Across ALL candidates at once - the old code went size group by
            // size group, so thousands of 2-file groups ran almost serially.
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelism),
                CancellationToken = token,
            }, async (file, ct) => await body(file, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private static async Task<string?> TryFingerprintAsync(FileInfo file, ScanState state, CancellationToken token)
        {
            try
            {
                await using var stream = OpenRead(file.FullName);
                long length = stream.Length;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                int headLength = (int)Math.Min(length, FingerprintBytes);
                byte[] buffer = new byte[FingerprintBytes];
                await ReadExactlyAsync(stream, buffer.AsMemory(0, headLength), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, headLength);

                long tailStart = Math.Max(headLength, length - FingerprintBytes);
                int tailLength = (int)(length - tailStart);
                if (tailLength > 0)
                {
                    stream.Seek(tailStart, SeekOrigin.Begin);
                    await ReadExactlyAsync(stream, buffer.AsMemory(0, tailLength), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, tailLength);
                }

                Interlocked.Add(ref state.BytesProcessed, headLength + tailLength);
                state.Report(file.FullName);
                return Convert.ToHexString(hash.GetHashAndReset());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref state.UnreadableFiles);
                return null;
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer[total..], token).ConfigureAwait(false);
                if (read == 0) throw new IOException("The file changed while it was being read.");
                total += read;
            }
        }

        private static FileStream OpenRead(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

        /// <summary>Full-file hash. Small files (fully covered by the fingerprint) don't count toward progress.</summary>
        internal static async Task<string> ComputeHashAsync(string path, DuplicateMatchMode mode, ScanState? state, bool countProgress, CancellationToken token)
        {
            await using var stream = OpenRead(path);
            var buffer = new byte[BufferSize];
            int read;

            if (mode == DuplicateMatchMode.Crc32)
            {
                uint crc = 0xFFFFFFFF;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) > 0)
                {
                    crc = Crc32.Update(crc, buffer.AsSpan(0, read));
                    if (countProgress && state != null) { Interlocked.Add(ref state.BytesProcessed, read); state.Report(path); }
                }
                return (crc ^ 0xFFFFFFFF).ToString("X8");
            }

            var algorithm = mode switch
            {
                DuplicateMatchMode.Md5 => HashAlgorithmName.MD5,
                DuplicateMatchMode.Sha1 => HashAlgorithmName.SHA1,
                _ => HashAlgorithmName.SHA256,
            };

            using var hash = IncrementalHash.CreateHash(algorithm);
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                if (countProgress && state != null) { Interlocked.Add(ref state.BytesProcessed, read); state.Report(path); }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>Byte-by-byte mode: compare each file with one member of each cluster so far.</summary>
        private static async Task<List<List<FileInfo>>> ClusterByBytesAsync(List<FileInfo> sameFingerprint, ScanState state, CancellationToken token)
        {
            var clusters = new List<List<FileInfo>>();
            foreach (var file in sameFingerprint)
            {
                token.ThrowIfCancellationRequested();
                bool placed = false;
                foreach (var cluster in clusters)
                {
                    try
                    {
                        if (await FilesAreIdenticalAsync(cluster[0].FullName, file.FullName, token).ConfigureAwait(false))
                        {
                            cluster.Add(file);
                            placed = true;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref state.UnreadableFiles);
                        placed = true;   // unreadable - leave it out
                        break;
                    }
                }
                if (!placed) clusters.Add(new List<FileInfo> { file });

                Interlocked.Add(ref state.BytesProcessed, file.Length);
                state.Report(file.FullName);
            }
            return clusters;
        }

        private static async Task<List<(List<FileInfo> Files, string Hash)>> VerifyGroupsAsync(
            List<(List<FileInfo> Files, string Hash)> groups, ScanState state, CancellationToken token)
        {
            var verified = new List<(List<FileInfo>, string)>();
            foreach (var (files, hash) in groups)
            {
                token.ThrowIfCancellationRequested();
                bool small = files[0].Length <= 2L * FingerprintBytes;
                if (small)
                {
                    verified.Add((files, hash));
                    continue;
                }

                var remaining = new List<FileInfo>(files);
                while (remaining.Count > 1)
                {
                    var reference = remaining[0];
                    var same = new List<FileInfo> { reference };
                    var different = new List<FileInfo>();
                    for (int i = 1; i < remaining.Count; i++)
                    {
                        bool identical;
                        try
                        {
                            identical = await FilesAreIdenticalAsync(reference.FullName, remaining[i].FullName, token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Interlocked.Increment(ref state.UnreadableFiles);
                            continue;
                        }
                        (identical ? same : different).Add(remaining[i]);
                        Interlocked.Add(ref state.BytesProcessed, remaining[i].Length);
                        state.Report(remaining[i].FullName);
                    }
                    if (same.Count > 1) verified.Add((same, hash));
                    remaining = different;
                }
            }
            return verified;
        }

        internal static async Task<bool> FilesAreIdenticalAsync(string pathA, string pathB, CancellationToken token)
        {
            if (string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase)) return true;

            await using var a = OpenRead(pathA);
            await using var b = OpenRead(pathB);
            if (a.Length != b.Length) return false;

            var bufferA = new byte[BufferSize];
            var bufferB = new byte[BufferSize];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Fill both buffers fully - a short read from one stream isn't a difference.
                int readA = await a.ReadAtLeastAsync(bufferA, BufferSize, throwOnEndOfStream: false, token).ConfigureAwait(false);
                int readB = await b.ReadAtLeastAsync(bufferB, BufferSize, throwOnEndOfStream: false, token).ConfigureAwait(false);
                if (readA != readB) return false;
                if (readA == 0) return true;
                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
            }
        }

        internal static class Crc32
        {
            private static readonly uint[] Table = BuildTable();

            private static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                return table;
            }

            public static uint Update(uint crc, ReadOnlySpan<byte> data)
            {
                foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
                return crc;
            }
        }

        // =================================================================
        //  Choosing what to keep
        // =================================================================

        /// <summary>
        /// Marks one keeper per group by <paramref name="rule"/>. When
        /// <paramref name="selectOthers"/> is true the rest are selected for
        /// Move/Delete. The view passes false for name-only scans: those
        /// files may differ, so nothing should be pre-selected for deletion
        /// (the old view pre-selected them anyway).
        /// </summary>
        public static void ApplyKeepRule(IEnumerable<DuplicateGroup> groups, KeepRule rule, IReadOnlyList<string>? folderOrder, bool selectOthers, bool selectApproximate = true)
        {
            foreach (var group in groups)
            {
                var keeper = PickKeeper(group.Files, rule, folderOrder);
                // "Same content, different format" groups are never pre-selected after a scan:
                // the user looks at them first and picks a keep rule on purpose (selectApproximate).
                bool select = selectOthers && (selectApproximate || group.Kind == DuplicateGroupKind.Exact);
                foreach (var file in group.Files)
                {
                    bool keep = ReferenceEquals(file, keeper);
                    file.IsKeep = keep;
                    file.IsSelected = select && !keep;
                }
                group.Refresh();
            }
        }

        public static DuplicateFileItem? PickKeeper(IReadOnlyList<DuplicateFileItem> files, KeepRule rule, IReadOnlyList<string>? folderOrder)
        {
            if (files.Count == 0) return null;

            int FolderRank(DuplicateFileItem f)
            {
                if (folderOrder == null) return int.MaxValue;
                for (int i = 0; i < folderOrder.Count; i++)
                {
                    string root = folderOrder[i].TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    if (f.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return i;
                }
                return int.MaxValue;
            }

            // Unknown dates sort last so they're never mistaken for the original.
            static DateTime Known(DateTime d) => d == default ? DateTime.MaxValue : d;

            // Ties broken by shortest path, then name, so the choice is stable.
            IOrderedEnumerable<DuplicateFileItem> ordered = rule switch
            {
                KeepRule.Newest => files.OrderByDescending(f => f.ModifiedTime),
                KeepRule.ShortestPath => files.OrderBy(f => f.FullPath.Length),
                KeepRule.FolderOrder => files.OrderBy(FolderRank).ThenBy(f => Known(f.ModifiedTime)),
                KeepRule.HighestQuality => files.OrderByDescending(f => f.Pixels).ThenByDescending(f => f.SizeBytes),
                _ => files.OrderBy(f => Known(f.ModifiedTime)),
            };
            return ordered.ThenBy(f => f.FullPath.Length).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First();
        }

        /// <summary>Groups where every copy is selected - acting would leave nothing behind.</summary>
        public static List<DuplicateGroup> GroupsLosingEveryCopy(IEnumerable<DuplicateGroup> groups) =>
            groups.Where(g => g.AllSelected).ToList();

        // =================================================================
        //  Move / delete
        // =================================================================

        /// <summary>Deletes off the UI thread; Recycle Bin by default (the old view deleted permanently).</summary>
        public Task<DuplicateActionResult> DeleteAsync(IReadOnlyList<DuplicateFileItem> items, bool sendToRecycleBin, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new DuplicateActionResult();
                foreach (var item in items)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        if (File.Exists(item.FullPath))
                        {
                            if (sendToRecycleBin && OperatingSystem.IsWindows())
                                FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                            else
                                File.Delete(item.FullPath);
                        }
                        result.Succeeded.Add(item);
                        result.Bytes += item.SizeBytes;
                    }
                    catch (Exception ex)
                    {
                        result.Failed.Add((item, ex is OperationCanceledException ? "Cancelled" : ex.Message));
                    }
                }
                return result;
            }, CancellationToken.None);

        /// <summary>Moves into <paramref name="destination"/>, renaming " (2)" on clashes. Off the UI thread.</summary>
        public Task<DuplicateActionResult> MoveAsync(IReadOnlyList<DuplicateFileItem> items, string destination, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new DuplicateActionResult();
                Directory.CreateDirectory(destination);
                foreach (var item in items)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        if (!File.Exists(item.FullPath))
                        {
                            result.Failed.Add((item, Loc.T("Str_DataQuality_MsgFileGone", "The file no longer exists.")));
                            continue;
                        }
                        string target = UniquePath(Path.Combine(destination, item.Name));
                        File.Move(item.FullPath, target);
                        result.Succeeded.Add(item);
                        result.Bytes += item.SizeBytes;
                    }
                    catch (Exception ex)
                    {
                        result.Failed.Add((item, ex.Message));
                    }
                }
                return result;
            }, CancellationToken.None);

        /// <summary>
        /// Takes finished items out of their groups and drops groups left
        /// with fewer than two files (no longer duplicates). Returns the
        /// groups that remain, renumbered.
        /// </summary>
        public static List<DuplicateGroup> RemoveHandled(List<DuplicateGroup> groups, IEnumerable<DuplicateFileItem> handled)
        {
            var gone = new HashSet<DuplicateFileItem>(handled);
            var remaining = new List<DuplicateGroup>();
            foreach (var group in groups)
            {
                group.Files.RemoveAll(gone.Contains);
                if (group.Files.Count < 2) continue;
                if (!group.Files.Any(f => f.IsKeep))
                {
                    // The keeper went - make the first remaining file the keeper.
                    group.Files[0].IsKeep = true;
                    group.Files[0].IsSelected = false;
                }
                remaining.Add(group);
            }
            for (int i = 0; i < remaining.Count; i++)
            {
                remaining[i].Number = i + 1;
                remaining[i].Refresh();
            }
            return remaining;
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        public static bool HasRecycleBin(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        // =================================================================
        //  Export
        // =================================================================

        /// <summary>CSV with a BOM so Excel opens it as UTF-8.</summary>
        public static string BuildCsv(IEnumerable<DuplicateGroup> groups)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Group,Action,File name,Folder,Full path,Size (bytes),Size,Modified,Hash");
            foreach (var group in groups)
            {
                foreach (var f in group.Files)
                {
                    string action = f.IsKeep ? "Keep" : f.IsSelected ? "Remove" : "";
                    sb.AppendLine(string.Join(",",
                        group.Number, action, Csv(f.Name), Csv(f.Folder), Csv(f.FullPath),
                        f.SizeBytes, Csv(f.Size), Csv(f.Modified), Csv(f.Hash)));
                }
            }
            return sb.ToString();
        }

        private static string Csv(string? value)
        {
            value ??= "";
            // Guard against spreadsheet formula injection from file names.
            if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "duplicate_finder.json");

        public static DuplicateFinderSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new DuplicateFinderSettings();
                return JsonSerializer.Deserialize<DuplicateFinderSettings>(File.ReadAllText(file), JsonOptions) ?? new DuplicateFinderSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new DuplicateFinderSettings();
            }
        }

        public static void SaveSettings(DuplicateFinderSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);

        public static string FormatBytes(long bytes) => DuplicateText.FormatBytes(bytes);
    }
}
