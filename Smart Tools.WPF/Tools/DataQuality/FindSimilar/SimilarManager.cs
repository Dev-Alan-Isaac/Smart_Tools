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
    /// grouping, keep rules, move/delete and settings. Hashing maths is in
    /// <see cref="SimilarHashing"/>; decoding images/video/audio (Magick.NET,
    /// ffmpeg) is in SimilarManager.Media.cs.
    ///
    /// Implemented methods: pHash / dHash / wHash for images, frame
    /// sampling + dHash for video, and a simplified acoustic fingerprint for
    /// audio. The other SimilarDetectionMethod values aren't offered in the
    /// sidebar any more.
    /// </summary>
    public sealed partial class SimilarManager
    {
        private const long ReportIntervalMs = 100;

        /// <summary>Computes a file's fingerprint; null when it can't be read. Swappable for tests.</summary>
        public delegate Task<FileFingerprint?> Fingerprinter(string path, SimilarDetectionMethod method, CancellationToken token);

        private readonly Fingerprinter _fingerprinter;
        private readonly bool _usesMediaDecoders;

        public SimilarManager()
        {
            _fingerprinter = ComputeFingerprintAsync;
            _usesMediaDecoders = true;
        }

        public SimilarManager(Fingerprinter fingerprinter) => _fingerprinter = fingerprinter;

        // =================================================================
        //  Methods
        // =================================================================

        public static bool IsImplemented(SimilarDetectionMethod method) => method is
            SimilarDetectionMethod.PHash or SimilarDetectionMethod.DHash or SimilarDetectionMethod.WHash or
            SimilarDetectionMethod.VideoFrameHash or SimilarDetectionMethod.AudioChromaprint;

        public static MediaKind KindOf(SimilarDetectionMethod method) => method switch
        {
            SimilarDetectionMethod.PHash or SimilarDetectionMethod.WHash or SimilarDetectionMethod.DHash
                or SimilarDetectionMethod.Ssim or SimilarDetectionMethod.FeatureMatching => MediaKind.Image,

            SimilarDetectionMethod.VideoFrameHash or SimilarDetectionMethod.VideoFrameSsim
                or SimilarDetectionMethod.VideoPerceptualHash or SimilarDetectionMethod.VideoSceneDetection
                or SimilarDetectionMethod.VideoMotionFlow => MediaKind.Video,

            _ => MediaKind.Audio,
        };

        public static string DisplayName(SimilarDetectionMethod method) => method switch
        {
            SimilarDetectionMethod.PHash => Loc.T("Str_Similar_MethodPHash", "Perceptual hash (pHash)"),
            SimilarDetectionMethod.WHash => Loc.T("Str_Similar_MethodWHash", "Wavelet hash (wHash)"),
            SimilarDetectionMethod.DHash => Loc.T("Str_Similar_MethodDHash", "Difference hash (dHash)"),
            SimilarDetectionMethod.VideoFrameHash => Loc.T("Str_Similar_MethodFrames", "Frame sampling + hashing"),
            SimilarDetectionMethod.AudioChromaprint => Loc.T("Str_Similar_MethodAudio", "Acoustic fingerprint"),
            _ => method.ToString(),
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
            if (!IsImplemented(options.Method))
                throw new NotSupportedException($"{DisplayName(options.Method)} isn't available. Use pHash, dHash or wHash for images, frame sampling for video, or the acoustic fingerprint for audio.");

            var kind = KindOf(options.Method);
            if (kind != MediaKind.Image && _usesMediaDecoders && !FfmpegAvailable)
                throw new InvalidOperationException($"Comparing {(kind == MediaKind.Video ? "videos" : "audio")} needs ffmpeg.exe and ffprobe.exe in {FfmpegFolder}. They weren't found there.");

            return Task.Run(() => ScanCoreAsync(options, kind, keepRule, progress, token), token);
        }

        private async Task<SimilarScanResult> ScanCoreAsync(SimilarScanOptions options, MediaKind kind, SimilarKeepRule keepRule, IProgress<SimilarScanProgress>? progress, CancellationToken token)
        {
            var state = new ScanState { Progress = progress, Phase = SimilarScanPhase.Listing };
            Log.Info($"Scan start: {kind}, method {options.Method}, threshold {options.ThresholdPercent}%, {options.MaxDegreeOfParallelism} workers, {options.Folders.Count()} folder(s)");
            var files = EnumerateCandidateFiles(options, kind, state, token);
            Log.Info($"Scan listed {files.Count} candidate files in {state.Watch.Elapsed.TotalSeconds:0.0}s");

            state.Phase = SimilarScanPhase.Fingerprinting;
            state.Total = files.Count;
            state.Report("", force: true);

            var prints = new FileFingerprint?[files.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = token,
            }, async (i, ct) =>
            {
                try
                {
                    prints[i] = await _fingerprinter(files[i].FullName, options.Method, ct).ConfigureAwait(false);
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

                if (prints[i] == null) Interlocked.Increment(ref state.Unreadable);
                Interlocked.Increment(ref state.Processed);
                state.Report(files[i].FullName);
            }).ConfigureAwait(false);

            Log.Info($"Scan fingerprinting finished in {state.Watch.Elapsed.TotalSeconds:0.0}s, grouping");
            state.Phase = SimilarScanPhase.Grouping;
            state.Report("", force: true);

            // Path order keeps greedy clustering reproducible across rescans.
            var readable = Enumerable.Range(0, files.Count).Where(i => prints[i] != null)
                .OrderBy(i => files[i].FullName, StringComparer.OrdinalIgnoreCase).ToList();
            var items = readable.Select(i => prints[i]!).ToList();

            var clusters = SimilarHashing.Cluster(items, SimilarHashing.MaxDistanceFor(options.ThresholdPercent), token);

            var groups = new List<SimilarGroup>();
            foreach (var cluster in clusters.Where(c => c.Count > 1))
            {
                var group = new SimilarGroup
                {
                    Kind = kind,
                    SimilarityPercent = SimilarHashing.SimilarityPercent(cluster.Select(c => items[c].Fingerprint).ToList()),
                };
                foreach (int c in cluster)
                {
                    var file = files[readable[c]];
                    var fp = items[c];
                    group.Items.Add(new SimilarFileItem
                    {
                        FullPath = file.FullName,
                        Name = file.Name,
                        Folder = file.DirectoryName ?? "",
                        SizeBytes = file.Length,
                        Kind = kind,
                        ModifiedTime = SafeLastWrite(file),
                        Hash = fp.Fingerprint.ToString(),
                        DurationSeconds = fp.DurationSeconds,
                        Width = fp.Width,
                        Height = fp.Height,
                        GroupInfo = group,
                    });
                }
                ApplyKeepRule(group, keepRule);
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
                            result.Failed.Add((item, Loc.T("Str_DataQuality_MsgFileGone", "The file no longer exists.")));
                            continue;
                        }
                        File.Move(item.FullPath, UniquePath(Path.Combine(destination, item.Name)));
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
                if (!IsImplemented(s.Method)) s.Method = SimilarDetectionMethod.PHash;
                s.ThresholdPercent = Math.Clamp(s.ThresholdPercent, 50, 100);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new SimilarFinderSettings();
            }
        }

        public static void SaveSettings(SimilarFinderSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
