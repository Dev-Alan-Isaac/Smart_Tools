using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Find Similar Files backend. This file: scanning (off the UI thread),
    /// grouping, keep rules, move/delete and settings. The engines are
    /// <see cref="SimilarImageEngine"/> and <see cref="SimilarVideoEngine"/> (OpenCV) and
    /// <see cref="SimilarAudioEngine"/> (SoundFingerprinting); <see cref="SimilarMatcher"/> turns their
    /// signatures into groups; SimilarManager.Media.cs reads the files (OpenCV, ffmpeg).
    ///
    /// A scan handles one media kind. Within it, files match across file formats, resolutions and
    /// encodings; flipped/rotated images, cropped/edited images, trimmed or mirrored videos and
    /// trimmed or re-encoded audio are found too. Signatures of images and videos are cached on disk
    /// (<see cref="SimilarFingerprintCache"/>) so a rescan only reads what changed.
    /// </summary>
    public sealed partial class SimilarManager
    {
        private const long ReportIntervalMs = 100;

        /// <summary>Computes a file's signature; null when it can't be read. Swappable for tests.</summary>
        public delegate Task<MediaSignature?> Signer(string path, MediaKind kind, CancellationToken token);

        private readonly Signer _signer;
        private readonly bool _usesMediaDecoders;

        public SimilarManager()
        {
            _signer = ComputeSignatureAsync;
            _usesMediaDecoders = true;
        }

        public SimilarManager(Signer signer) => _signer = signer;

        // =================================================================
        //  Methods
        // =================================================================

        public static bool IsImplemented(SimilarDetectionMethod method) => method is
            SimilarDetectionMethod.ImageOpenCv or SimilarDetectionMethod.VideoOpenCv or SimilarDetectionMethod.AudioFingerprint;

        /// <summary>Older saved methods become the engine for their media kind.</summary>
        public static SimilarDetectionMethod Normalize(SimilarDetectionMethod method) => KindOf(method) switch
        {
            MediaKind.Video => SimilarDetectionMethod.VideoOpenCv,
            MediaKind.Audio => SimilarDetectionMethod.AudioFingerprint,
            _ => SimilarDetectionMethod.ImageOpenCv,
        };

        public static MediaKind KindOf(SimilarDetectionMethod method) => method switch
        {
            SimilarDetectionMethod.PHash or SimilarDetectionMethod.WHash or SimilarDetectionMethod.DHash
                or SimilarDetectionMethod.Ssim or SimilarDetectionMethod.FeatureMatching
                or SimilarDetectionMethod.ImageOpenCv => MediaKind.Image,

            SimilarDetectionMethod.VideoFrameHash or SimilarDetectionMethod.VideoFrameSsim
                or SimilarDetectionMethod.VideoPerceptualHash or SimilarDetectionMethod.VideoSceneDetection
                or SimilarDetectionMethod.VideoMotionFlow or SimilarDetectionMethod.VideoOpenCv => MediaKind.Video,

            _ => MediaKind.Audio,
        };

        public static string DisplayName(SimilarDetectionMethod method) => Normalize(method) switch
        {
            SimilarDetectionMethod.ImageOpenCv => "Images (OpenCV)",
            SimilarDetectionMethod.VideoOpenCv => "Videos (OpenCV)",
            _ => "Audio (SoundFingerprinting)",
        };

        // =================================================================
        //  Scan
        // =================================================================

        private sealed class ScanState
        {
            public int Processed;
            public int Total;
            public int Unreadable;
            public int Inaccessible;
            public int Links;
            public SimilarScanPhase Phase;
            public long LastReport = long.MinValue;
            public readonly Stopwatch Watch = Stopwatch.StartNew();
            public IProgress<SimilarScanProgress>? Progress;
            public readonly object Lock = new();

            private long _lastLog = -LogIntervalMs;
            private const long LogIntervalMs = 5000;

            public void Report(string path, bool force = false)
            {
                if (Progress == null) return;
                lock (Lock)
                {
                    long now = Watch.ElapsedMilliseconds;
                    if (force || now - _lastLog >= LogIntervalMs)
                    {
                        _lastLog = now;
                        Log.Info($"Scan {Phase}: {Volatile.Read(ref Processed)}/{Total} done, {Unreadable} unreadable, {now / 1000}s elapsed, now: {path}");
                    }
                    if (!force && now - LastReport < ReportIntervalMs) return;
                    LastReport = now;
                    Progress.Report(new SimilarScanProgress(Phase, Volatile.Read(ref Processed), Total, path));
                }
            }
        }

        /// <summary>
        /// Finds groups of similar files. Runs entirely off the calling
        /// thread (the old version listed files on the UI thread). Groups
        /// come back biggest-savings first, each with a keeper chosen by
        /// <paramref name="keepRule"/> and the rest selected.
        /// </summary>
        public Task<SimilarScanResult> ScanAsync(SimilarScanOptions options, SimilarKeepRule keepRule, IProgress<SimilarScanProgress>? progress, CancellationToken token = default)
        {
            var kind = KindOf(options.Method);
            if (kind == MediaKind.Audio && _usesMediaDecoders && !FfmpegAvailable)
                throw new InvalidOperationException(Loc.F("Str_FindSimilar_NeedsFfmpeg", "Needs ffmpeg.exe and ffprobe.exe in {0} - they weren't found.", FfmpegFolder));

            return Task.Run(() => ScanCoreAsync(options, kind, keepRule, progress, token), token);
        }

        private async Task<SimilarScanResult> ScanCoreAsync(SimilarScanOptions options, MediaKind kind, SimilarKeepRule keepRule, IProgress<SimilarScanProgress>? progress, CancellationToken token)
        {
            var state = new ScanState { Progress = progress, Phase = SimilarScanPhase.Listing };
            Log.Info($"Scan start: {kind}, threshold {options.ThresholdPercent}%, crops {options.FindCroppedCopies}, {options.MaxDegreeOfParallelism} workers, {options.Folders.Count()} folder(s)");
            var files = EnumerateCandidateFiles(options, kind, state, token);
            Log.Info($"Scan listed {files.Count} candidate files in {state.Watch.Elapsed.TotalSeconds:0.0}s");

            // ---- 1. A signature per file (cached for images and videos) ----
            state.Phase = SimilarScanPhase.Fingerprinting;
            state.Total = files.Count;
            state.Report("", force: true);

            var cache = kind is MediaKind.Image or MediaKind.Video && _usesMediaDecoders ? SimilarFingerprintCache.Load() : null;
            int cached = 0;
            var sigs = new MediaSignature?[files.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = token,
            }, async (i, ct) =>
            {
                try
                {
                    var hit = cache?.TryGet(files[i]);
                    if (hit != null)
                    {
                        sigs[i] = hit;
                        Interlocked.Increment(ref cached);
                    }
                    else
                    {
                        sigs[i] = await _signer(files[i].FullName, kind, ct).ConfigureAwait(false);
                        if (sigs[i] != null) cache?.Put(files[i], sigs[i]!);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // A per-file timeout (hung ffmpeg) - skip just this file.
                    Log.Warn($"Fingerprint timed out, skipped: {files[i].FullName}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Capped: a bad drive could fail thousands of files.
                    if (Volatile.Read(ref state.Unreadable) < 50) Log.Info($"Fingerprint failed, skipped: {files[i].FullName} ({ex.GetType().Name}: {ex.Message})");
                }

                if (sigs[i] == null) Interlocked.Increment(ref state.Unreadable);
                Interlocked.Increment(ref state.Processed);
                state.Report(files[i].FullName);
            }).ConfigureAwait(false);

            cache?.Save();
            Log.Info($"Scan fingerprinting finished in {state.Watch.Elapsed.TotalSeconds:0.0}s ({cached} from the cache), matching");

            // ---- 2. Which files are the same content ----
            state.Phase = SimilarScanPhase.Matching;
            state.Processed = 0;
            state.Total = 1;
            state.Report("", force: true);
            void OnMatching(int done, int total)
            {
                state.Total = Math.Max(1, total);
                Volatile.Write(ref state.Processed, Math.Min(done, state.Total));
                state.Report("");
            }

            List<SimilarEdge> edges = await Task.Run(async () => kind switch
            {
                MediaKind.Image => SimilarMatcher.ImageEdges(sigs, options.ThresholdPercent, options.FindCroppedCopies, OnMatching, token),
                MediaKind.Video => SimilarMatcher.VideoEdges(sigs, options.ThresholdPercent, OnMatching, token),
                _ => await SimilarAudioEngine.FindEdgesAsync(sigs, SimilarMatcher.AudioConfidenceFor(options.ThresholdPercent),
                    new Progress<int>(done => OnMatching(done, sigs.Count(s => s != null))), token).ConfigureAwait(false),
            }, token).ConfigureAwait(false);

            Log.Info($"Scan matching finished in {state.Watch.Elapsed.TotalSeconds:0.0}s: {edges.Count} matching pairs, grouping");
            state.Phase = SimilarScanPhase.Grouping;
            state.Report("", force: true);

            // ---- 3. Groups ----
            var groups = new List<SimilarGroup>();
            foreach (var component in SimilarMatcher.Group(files.Count, edges))
            {
                token.ThrowIfCancellationRequested();
                var group = new SimilarGroup { Kind = kind, SimilarityPercent = (int)Math.Round(component.MeanSimilarity) };
                var fileIndex = new Dictionary<SimilarFileItem, int>();
                foreach (int index in component.Members.OrderBy(i => files[i].FullName, StringComparer.OrdinalIgnoreCase))
                {
                    var file = files[index];
                    var sig = sigs[index]!;
                    var item = new SimilarFileItem
                    {
                        FullPath = file.FullName,
                        Name = file.Name,
                        Folder = file.DirectoryName ?? "",
                        SizeBytes = file.Length,
                        Kind = kind,
                        ModifiedTime = SafeLastWrite(file),
                        Hash = sig.ImageHashes.Length > 0 ? sig.ImageHashes[0].ToString("X16") : "",
                        DurationSeconds = sig.DurationSeconds,
                        Width = sig.Width,
                        Height = sig.Height,
                        GroupInfo = group,
                    };
                    group.Items.Add(item);
                    fileIndex[item] = index;
                }
                ApplyKeepRule(group, keepRule);
                DescribeDifferences(group, fileIndex, sigs, kind, options.ThresholdPercent);
                groups.Add(group);
            }

            groups.Sort((a, b) => b.PotentialBytes.CompareTo(a.PotentialBytes));
            for (int i = 0; i < groups.Count; i++) groups[i].Id = i + 1;

            state.Phase = SimilarScanPhase.Done;
            state.Report("", force: true);
            Log.Info($"Scan done: {groups.Count} groups from {files.Count} files ({state.Unreadable} unreadable) in {state.Watch.Elapsed.TotalSeconds:0.0}s");

            return new SimilarScanResult
            {
                Groups = groups,
                Kind = kind,
                FilesScanned = files.Count,
                UnreadableFiles = state.Unreadable,
                InaccessibleFolders = state.Inaccessible,
                SkippedLinks = state.Links,
                Elapsed = state.Watch.Elapsed,
            };
        }

        /// <summary>
        /// Fills each copy's "Why" line: what makes it differ from the rest (flipped, cropped, trimmed from the pairs
        /// that matched it; other size or other format from comparing it with the keeper). The keeper itself stays blank.
        /// </summary>
        private static void DescribeDifferences(SimilarGroup group, Dictionary<SimilarFileItem, int> fileIndex, MediaSignature?[] sigs, MediaKind kind, double threshold)
        {
            var keeper = group.Items.FirstOrDefault(i => i.IsKeep);
            var keeperSig = keeper != null && fileIndex.TryGetValue(keeper, out int keeperIndex) ? sigs[keeperIndex] : null;
            foreach (var item in group.Items)
            {
                if (ReferenceEquals(item, keeper)) continue;

                var tags = SimilarTag.None;
                if (keeperSig != null && fileIndex.TryGetValue(item, out int index) && sigs[index] is { } itemSig)
                    tags = SimilarMatcher.Describe(kind, itemSig, keeperSig, threshold);

                if (keeper != null)
                {
                    if (item.Width > 0 && keeper.Width > 0 && (item.Width != keeper.Width || item.Height != keeper.Height))
                        tags |= SimilarTag.DifferentResolution;
                    if (!string.Equals(Path.GetExtension(item.Name), Path.GetExtension(keeper.Name), StringComparison.OrdinalIgnoreCase))
                        tags |= SimilarTag.DifferentFormat;
                }
                item.Why = DescribeTags(tags);
            }
        }

        public static string DescribeTags(SimilarTag tags)
        {
            var parts = new List<string>();
            if (tags.HasFlag(SimilarTag.Flipped)) parts.Add(Loc.T("Str_FindSimilar_Tag_Flipped", "Flipped"));
            if (tags.HasFlag(SimilarTag.Rotated)) parts.Add(Loc.T("Str_FindSimilar_Tag_Rotated", "Rotated"));
            if (tags.HasFlag(SimilarTag.Cropped)) parts.Add(Loc.T("Str_FindSimilar_Tag_Cropped", "Cropped or edited"));
            if (tags.HasFlag(SimilarTag.Trimmed)) parts.Add(Loc.T("Str_FindSimilar_Tag_Trimmed", "Trimmed or different length"));
            if (tags.HasFlag(SimilarTag.DifferentResolution)) parts.Add(Loc.T("Str_FindSimilar_Tag_Resolution", "Different size"));
            if (tags.HasFlag(SimilarTag.DifferentFormat)) parts.Add(Loc.T("Str_FindSimilar_Tag_Format", "Different format"));
            return string.Join(" · ", parts);
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

        private static List<FileInfo> EnumerateCandidateFiles(SimilarScanOptions options, MediaKind kind, ScanState state, CancellationToken token)
        {
            var files = new List<FileInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enumeration = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false };

            foreach (string root in options.Folders)
            {
                if (!Directory.Exists(root)) continue;
                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(root));

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
                        state.Inaccessible++;
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
                        if (options.IgnoreSystemFiles && attrs.HasFlag(FileAttributes.System)) continue;

                        if (entry is DirectoryInfo sub)
                        {
                            if (!options.IncludeSubfolders) continue;
                            // Junctions/symlinks aren't followed (the old walk did: loops, double counting).
                            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                            {
                                state.Links++;
                                continue;
                            }
                            pending.Push(sub);
                        }
                        else if (entry is FileInfo file && !attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            if (FileTypeDetector.Detect(file.Name) != kind) continue;
                            long length;
                            try
                            {
                                length = file.Length;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                continue;
                            }
                            if (length == 0 || !seen.Add(file.FullName)) continue;
                            files.Add(file);
                            state.Total = files.Count;
                            state.Report(file.FullName);
                        }
                    }
                }
            }
            return files;
        }

        // =================================================================
        //  ffprobe output
        // =================================================================

        internal readonly record struct ProbeResult(double Duration, int Width, int Height);

        /// <summary>
        /// Parses ffprobe key=value output. Phone videos store portrait as
        /// landscape + a 90° rotation tag; the size is swapped so a rotated
        /// copy and a re-encoded upright copy still pass the aspect check
        /// (the old parser ignored rotation).
        /// </summary>
        internal static ProbeResult ParseProbe(string output)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                if (key.StartsWith("TAG:", StringComparison.OrdinalIgnoreCase)) key = key[4..];
                fields.TryAdd(key, line[(eq + 1)..].Trim());
            }

            double duration = fields.TryGetValue("duration", out var d) && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? dv : 0;
            int width = fields.TryGetValue("width", out var w) && int.TryParse(w, out int wv) ? wv : 0;
            int height = fields.TryGetValue("height", out var h) && int.TryParse(h, out int hv) ? hv : 0;

            string? rotation = fields.TryGetValue("rotation", out var r1) ? r1 : fields.TryGetValue("rotate", out var r2) ? r2 : null;
            if (rotation != null && double.TryParse(rotation, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg)
                && Math.Abs(Math.Round(deg) % 180) == 90)
                (width, height) = (height, width);

            return new ProbeResult(double.IsFinite(duration) ? duration : 0, width, height);
        }

        // =================================================================
        //  Keep rules
        // =================================================================

        public static SimilarFileItem? PickKeeper(IReadOnlyList<SimilarFileItem> items, SimilarKeepRule rule)
        {
            if (items.Count == 0) return null;
            static DateTime Known(DateTime d) => d == default ? DateTime.MaxValue : d;

            IOrderedEnumerable<SimilarFileItem> ordered = rule switch
            {
                SimilarKeepRule.LargestFile => items.OrderByDescending(i => i.SizeBytes),
                SimilarKeepRule.Oldest => items.OrderBy(i => Known(i.ModifiedTime)),
                SimilarKeepRule.Newest => items.OrderByDescending(i => i.ModifiedTime),
                // Resolution first: a re-saved copy can be bigger in bytes but smaller in pixels.
                _ => items.OrderByDescending(i => i.PixelCount).ThenByDescending(i => i.DurationSeconds).ThenByDescending(i => i.SizeBytes),
            };
            return ordered.ThenBy(i => i.FullPath.Length).ThenBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase).First();
        }

        /// <summary>Keeper by rule, everything else selected.</summary>
        public static void ApplyKeepRule(SimilarGroup group, SimilarKeepRule rule)
        {
            var keeper = PickKeeper(group.Items, rule);
            if (keeper != null) group.SetKeep(keeper);
        }

        public static void ApplyKeepRule(IEnumerable<SimilarGroup> groups, SimilarKeepRule rule)
        {
            foreach (var g in groups) ApplyKeepRule(g, rule);
        }

        public static List<SimilarGroup> GroupsLosingEveryCopy(IEnumerable<SimilarGroup> groups) => groups.Where(g => g.AllSelected).ToList();

        // =================================================================
        //  Move / delete
        // =================================================================

        public sealed class ActionResult
        {
            public List<SimilarFileItem> Succeeded { get; } = new();
            public List<(SimilarFileItem Item, string Error)> Failed { get; } = new();
            public long Bytes { get; set; }
        }

        /// <summary>Deletes off the UI thread - to the Recycle Bin unless told otherwise (the old Delete Selected was permanent).</summary>
        public Task<ActionResult> DeleteAsync(IReadOnlyList<SimilarFileItem> items, bool sendToRecycleBin, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new ActionResult();
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

        public Task<ActionResult> MoveAsync(IReadOnlyList<SimilarFileItem> items, string destination, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new ActionResult();
                Directory.CreateDirectory(destination);
                foreach (var item in items)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        if (!File.Exists(item.FullPath))
                        {
                            result.Failed.Add((item, "The file no longer exists."));
                            continue;
                        }
                        MoveFile(item.FullPath, Path.Combine(destination, item.Name));
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

        /// <summary>Drops handled files, then groups left with fewer than two (renumbered). Re-picks a keeper if it went.</summary>
        public static List<SimilarGroup> RemoveHandled(List<SimilarGroup> groups, IEnumerable<SimilarFileItem> handled, SimilarKeepRule rule)
        {
            var gone = new HashSet<SimilarFileItem>(handled);
            var remaining = new List<SimilarGroup>();
            foreach (var group in groups)
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count < 2) continue;
                if (!group.Items.Any(i => i.IsKeep))
                {
                    var keeper = PickKeeper(group.Items, rule)!;
                    keeper.IsKeep = true;
                    keeper.IsSelected = false;
                }
                remaining.Add(group);
            }
            for (int i = 0; i < remaining.Count; i++)
            {
                remaining[i].Id = i + 1;
                remaining[i].Refresh();
            }
            return remaining;
        }

        public static string UniquePath(string path) => SafeFileOps.UniquePath(path);

        public static bool HasRecycleBin(string path) => SafeFileOps.HasRecycleBin(path);

        /// <summary>
        /// Moves without ever overwriting. On the same drive that is a rename; across drives the file is copied
        /// to a temp name, its hash checked against the source, and only then is the original removed.
        /// </summary>
        private static void MoveFile(string source, string destination)
        {
            string target = SafeFileOps.UniquePath(destination);
            if (SafeFileOps.SameVolume(source, target))
            {
                File.Move(source, target);
                return;
            }

            string temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            try
            {
                string sourceHash = SafeFileOps.CopyToTemp(source, temp);
                string copyHash;
                using (var copy = File.OpenRead(temp)) copyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy));
                if (!string.Equals(sourceHash, copyHash, StringComparison.Ordinal))
                    throw new IOException(Loc.T("Str_FindSimilar_MoveVerifyFailed", "The copy did not match the original, so nothing was moved."));

                SafeFileOps.CopyTimestamps(source, temp);
                File.Move(temp, target);
                SafeFileOps.ClearReadOnly(source);
                File.Delete(source);
            }
            catch
            {
                SafeFileOps.DeleteQuietly(temp);
                throw;
            }
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "similar_files.json");

        public static SimilarFinderSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new SimilarFinderSettings();
                var s = JsonSerializer.Deserialize<SimilarFinderSettings>(File.ReadAllText(file), JsonOptions) ?? new SimilarFinderSettings();
                s.Method = Normalize(s.Method);   // older hash/frame/chromaprint choices become the current engines
                s.ThresholdPercent = Math.Clamp(s.ThresholdPercent, 50, 100);
                return s;
            }
            catch (Exception)
            {
                return new SimilarFinderSettings();
            }
        }

        public static void SaveSettings(SimilarFinderSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
