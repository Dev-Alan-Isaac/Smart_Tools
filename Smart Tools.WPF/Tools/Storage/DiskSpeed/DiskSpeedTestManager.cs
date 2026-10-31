using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Disk Speed Test backend: picks a writable folder, runs a real
    /// sequential write then read of a temporary file, and keeps the
    /// history. No WPF types - the view only shows what this reports.
    ///
    /// What it measures: large sequential transfers (what copying big files
    /// sees), not random 4K IOPS like CrystalDiskMark's RND tests.
    ///
    /// Accuracy: both passes open the file unbuffered (FILE_FLAG_NO_BUFFERING,
    /// O_DIRECT on Linux), so the read is not served from the RAM copy the
    /// write just left behind - the old engine's read results were mostly
    /// RAM speed. Drives that refuse unbuffered I/O fall back to normal I/O
    /// and the result says so (CacheBypassed = false).
    /// </summary>
    public sealed class DiskSpeedTestManager
    {
        public const string TempFilePrefix = ".smarttools_diskspeedtest_";

        /// <summary>Headroom on top of the test file so the test never fills a drive.</summary>
        public const double RequiredFreeSpaceMultiplier = 1.2;

        public const int MaxHistory = 50;

        /// <summary>Windows FILE_FLAG_NO_BUFFERING (.NET maps it to O_DIRECT on Linux).</summary>
        private const FileOptions NoBuffering = (FileOptions)0x20000000;

        /// <summary>Unbuffered I/O needs sector-aligned buffers, offsets and lengths; 4096 covers every modern drive.</summary>
        private const int Alignment = 4096;

        private const double ReportIntervalSeconds = 0.1;
        private const double SpeedWindowSeconds = 0.4;

        // =================================================================
        //  Drives + preflight
        // =================================================================

        public static List<DriveTarget> GetDrives()
        {
            string systemRoot = SafeSystemRoot();
            var list = new List<DriveTarget>();

            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;

                    string root = d.RootDirectory.FullName;
                    string kind = d.DriveType switch
                    {
                        DriveType.Removable => "USB",
                        DriveType.Network => "Network",
                        _ => "",
                    };
                    string shortRoot = root.TrimEnd('\\', '/');
                    if (shortRoot.Length == 0) shortRoot = root;
                    string volume = d.VolumeLabel?.Trim() ?? "";
                    bool addVolume = volume.Length > 0 && volume != root && volume != shortRoot;
                    string name = addVolume ? $"{volume} ({shortRoot})" : shortRoot;   // like Explorer: "Windows (C:)"
                    if (kind.Length > 0) name += $" · {kind}";

                    list.Add(new DriveTarget
                    {
                        RootPath = root,
                        Label = name,
                        FreeBytes = d.AvailableFreeSpace,
                        TotalBytes = d.TotalSize,
                        DriveType = d.DriveType,
                        IsSystemDrive = string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase),
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // drive went away between listing and reading - skip it
                }
            }

            // System drive first - it's what most people want to test.
            return list.OrderByDescending(d => d.IsSystemDrive).ThenBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string SafeSystemRoot()
        {
            try
            {
                return Path.GetPathRoot(Environment.SystemDirectory) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Checks the target before a run: finds a folder on it we can
        /// actually write to and checks there's room. A drive root such as
        /// C:\ needs admin rights to write to, so for the system drive this
        /// falls back to the temp folder (same drive, same speed) - the old
        /// view just failed with "access denied" on its default choice.
        /// </summary>
        public static SpeedTestPreflight Prepare(string target, long testFileSizeBytes)
        {
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
                return new SpeedTestPreflight { Error = "Choose a drive or folder to test." };

            long required = (long)(RoundUp(testFileSizeBytes, Alignment) * RequiredFreeSpaceMultiplier);

            string? folder = null;
            string? note = null;
            foreach (var candidate in CandidateFolders(target))
            {
                if (!CanWrite(candidate)) continue;
                folder = candidate;
                if (!SamePath(candidate, target))
                    note = $"Testing in {candidate} (you can't write to {target} without admin rights - same drive, same speed).";
                break;
            }

            if (folder == null)
            {
                return new SpeedTestPreflight
                {
                    Error = $"Smart Tools can't write to {target}. Choose a folder you can save files in (for example your Documents folder on that drive).",
                    RequiredBytes = required,
                };
            }

            long free = FreeSpace(folder);
            if (free >= 0 && free < required)
            {
                return new SpeedTestPreflight
                {
                    Folder = folder,
                    FreeBytes = free,
                    RequiredBytes = required,
                    Error = $"Not enough free space: this test needs {DiskSizeText.Format(required)} " +
                            $"(the test file plus a safety margin) and only {DiskSizeText.Format(free)} is free. Pick a smaller test size.",
                };
            }

            return new SpeedTestPreflight { Folder = folder, FreeBytes = free, RequiredBytes = required, Note = note };
        }

        /// <summary>The folder itself, then - only if it's a drive root - writable folders on the same drive.</summary>
        private static IEnumerable<string> CandidateFolders(string target)
        {
            string full = Path.GetFullPath(target);
            yield return full;

            string? root = Path.GetPathRoot(full);
            bool isRoot = root != null && SamePath(full, root);
            if (!isRoot) yield break;

            foreach (string alt in new[]
            {
                Path.GetTempPath(),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            })
            {
                if (string.IsNullOrEmpty(alt)) continue;
                string altFull;
                try
                {
                    altFull = Path.GetFullPath(alt);
                }
                catch (Exception)
                {
                    continue;
                }
                if (string.Equals(Path.GetPathRoot(altFull), root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(altFull))
                    yield return altFull;
            }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                          Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// <summary>Real probe: create and delete a tiny file (ACL checks alone miss read-only media, policies, etc.).</summary>
        public static bool CanWrite(string folder)
        {
            string probe = Path.Combine(folder, $"{TempFilePrefix}probe_{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
            finally
            {
                TryDelete(probe);
            }
        }

        /// <summary>Free bytes for the drive holding <paramref name="folder"/>, or -1 when unknown (some network shares).</summary>
        public static long FreeSpace(string folder)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root)) return -1;
                // UNC roots (\\server\share) throw in DriveInfo - the old view crashed here.
                if (root.StartsWith(@"\\", StringComparison.Ordinal)) return -1;
                var drive = new DriveInfo(root);
                return drive.IsReady ? drive.AvailableFreeSpace : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // =================================================================
        //  Run
        // =================================================================

        /// <summary>
        /// Write pass, then read pass, on a background thread. The temp file
        /// is deleted on every path (success, cancel, error). Progress is
        /// throttled to ~10 updates a second - the old engine posted one per
        /// 4 MB block, hundreds a second on an NVMe drive.
        /// </summary>
        public Task<DiskSpeedTestResult> RunAsync(DiskSpeedTestOptions options, IProgress<SpeedSample>? progress, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(options.TargetFolder)) throw new ArgumentException("No target folder.", nameof(options));
            if (options.TestFileSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Test size must be positive.");

            return Task.Run(() => Run(options, progress, token), token);
        }

        private static DiskSpeedTestResult Run(DiskSpeedTestOptions options, IProgress<SpeedSample>? progress, CancellationToken token)
        {
            CleanupStaleFiles(options.TargetFolder);

            int block = (int)RoundUp(Math.Max(Alignment, options.BlockSizeBytes), Alignment);
            long total = RoundUp(options.TestFileSizeBytes, Alignment);
            string path = Path.Combine(options.TargetFolder, $"{TempFilePrefix}{Guid.NewGuid():N}.tmp");

            // Pinned (never moves) and over-allocated so a 4096-aligned slice
            // fits - no unsafe code needed in the app project.
            byte[] raw = GC.AllocateUninitializedArray<byte>(block + Alignment, pinned: true);
            long address = Marshal.UnsafeAddrOfPinnedArrayElement(raw, 0).ToInt64();
            int start = (int)((Alignment - address % Alignment) % Alignment);

            try
            {
                var buffer = raw.AsSpan(start, block);
                // Random, not zeros - some SSDs and file systems compress or
                // deduplicate zeros and would report impossible speeds.
                Random.Shared.NextBytes(buffer);

                bool unbuffered = options.BypassCache;
                var write = WritePass(path, total, buffer, ref unbuffered, progress, token);
                var read = ReadPass(path, total, buffer, unbuffered, progress, token);

                return new DiskSpeedTestResult
                {
                    TargetDisplay = options.TargetFolder,
                    TestFileSizeBytes = total,
                    WriteMBps = write.Average,
                    WritePeakMBps = Math.Max(write.Peak, write.Average),
                    ReadMBps = read.Average,
                    ReadPeakMBps = Math.Max(read.Peak, read.Average),
                    CacheBypassed = unbuffered,
                    Tier = ClassifyTier(read.Average),
                };
            }
            finally
            {
                TryDelete(path);
            }
        }

        private readonly record struct PassResult(double Average, double Peak);

        private static PassResult WritePass(string path, long total, Span<byte> buffer, ref bool unbuffered, IProgress<SpeedSample>? progress, CancellationToken token)
        {
            // Some drives/file systems (network shares, FAT on some card
            // readers) refuse unbuffered handles - fall back rather than fail.
            Microsoft.Win32.SafeHandles.SafeFileHandle? handle = null;
            if (unbuffered)
            {
                try
                {
                    handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering, total);
                    RandomAccess.Write(handle, buffer[..Alignment], 0);   // some refuse only at the first write
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                    handle?.Dispose();
                    unbuffered = false;
                    TryDelete(path);
                    handle = OpenBufferedWrite(path, total);
                }
            }
            else
            {
                handle = OpenBufferedWrite(path, total);
            }

            using (handle)
            {
                var meter = new SpeedMeter(SpeedTestPhase.Writing, total, progress);
                long offset = 0;
                while (offset < total)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, total - offset);
                    RandomAccess.Write(handle, buffer[..count], offset);
                    offset += count;
                    meter.Add(count);
                }
                if (!unbuffered) RandomAccess.FlushToDisk(handle);
                return meter.Finish();
            }
        }

        private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenBufferedWrite(string path, long total) =>
            // WriteThrough still pushes each write to the disk instead of the write-behind cache.
            File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.WriteThrough, total);

        private static PassResult ReadPass(string path, long total, Span<byte> buffer, bool unbuffered, IProgress<SpeedSample>? progress, CancellationToken token)
        {
            var options = unbuffered ? NoBuffering | FileOptions.SequentialScan : FileOptions.SequentialScan;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, options);

            var meter = new SpeedMeter(SpeedTestPhase.Reading, total, progress);
            long offset = 0;
            while (offset < total)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, total - offset);
                int read = RandomAccess.Read(handle, buffer[..count], offset);
                if (read <= 0) break;
                offset += read;
                meter.Add(read);
            }
            return meter.Finish();
        }

        /// <summary>Tracks average, a smoothed "current" speed over a short window, and the peak.</summary>
        private sealed class SpeedMeter
        {
            private readonly SpeedTestPhase _phase;
            private readonly long _total;
            private readonly IProgress<SpeedSample>? _progress;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private readonly Queue<(double T, long Bytes)> _window = new();
            private long _bytes;
            private double _lastReport = double.NegativeInfinity;
            private double _peak;

            public SpeedMeter(SpeedTestPhase phase, long total, IProgress<SpeedSample>? progress)
            {
                _phase = phase;
                _total = total;
                _progress = progress;
                _window.Enqueue((0, 0));
                progress?.Report(new SpeedSample(phase, 0, 0, 0, 0, total));
            }

            public void Add(long count)
            {
                _bytes += count;
                double t = _watch.Elapsed.TotalSeconds;
                _window.Enqueue((t, _bytes));
                while (_window.Count > 2 && t - _window.Peek().T > SpeedWindowSeconds) _window.Dequeue();

                double current = Current(t);
                // Peak only once the window is full, so one fast first block doesn't count.
                if (t >= SpeedWindowSeconds) _peak = Math.Max(_peak, current);

                if (t - _lastReport >= ReportIntervalSeconds)
                {
                    _lastReport = t;
                    _progress?.Report(new SpeedSample(_phase, t, current, ToMBps(_bytes, t), _bytes, _total));
                }
            }

            private double Current(double t)
            {
                var first = _window.Peek();
                return ToMBps(_bytes - first.Bytes, t - first.T);
            }

            public PassResult Finish()
            {
                double t = _watch.Elapsed.TotalSeconds;
                double avg = ToMBps(_bytes, t);
                _progress?.Report(new SpeedSample(_phase, t, Current(t), avg, _bytes, _total));
                return new PassResult(avg, _peak);
            }
        }

        /// <summary>Decimal MB/s (1 MB = 1,000,000 bytes) - matches drive spec sheets.</summary>
        public static double ToMBps(long bytes, double seconds) => seconds <= 0 ? 0 : bytes / 1_000_000.0 / seconds;

        public static long RoundUp(long value, long multiple) => (value + multiple - 1) / multiple * multiple;

        public static SpeedTier ClassifyTier(double readMBps) => readMBps switch
        {
            < 200 => SpeedTier.Hdd,
            < 600 => SpeedTier.SataSsd,
            < 4000 => SpeedTier.Nvme,
            _ => SpeedTier.NvmeFast,
        };

        /// <summary>A round axis maximum at or above <paramref name="value"/>: 1, 2, 2.5, 5 x 10^n.</summary>
        public static double NiceCeiling(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return 100;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (double step in new[] { 1, 2, 2.5, 5, 10 })
            {
                if (step * magnitude >= value - 1e-9) return step * magnitude;
            }
            return 10 * magnitude;
        }

        // =================================================================
        //  Temp files
        // =================================================================

        /// <summary>
        /// Deletes test files left behind by a run that never finished (app
        /// killed, PC crashed). Only files older than 10 minutes, so a test
        /// running in another Smart Tools window is left alone.
        /// </summary>
        public static int CleanupStaleFiles(string folder, TimeSpan? olderThan = null)
        {
            int deleted = 0;
            var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromMinutes(10));
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles(TempFilePrefix + "*.tmp"))
                {
                    if (file.LastWriteTimeUtc > cutoff) continue;
                    if (TryDelete(file.FullName)) deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // best effort
            }
            return deleted;
        }

        /// <summary>Best effort - antivirus can hold a just-closed file for a moment.</summary>
        public static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // =================================================================
        //  History (%AppData%\Smart Tools\disk_speed_history.json)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string HistoryPath => Path.Combine(
            AppPaths.DataRoot, "disk_speed_history.json");

        public static List<DiskSpeedTestResult> LoadHistory(string? path = null)
        {
            try
            {
                string file = path ?? HistoryPath;
                if (!File.Exists(file)) return new List<DiskSpeedTestResult>();
                var list = JsonSerializer.Deserialize<List<DiskSpeedTestResult>>(File.ReadAllText(file), JsonOptions);
                return list?.OrderByDescending(r => r.Timestamp).Take(MaxHistory).ToList() ?? new List<DiskSpeedTestResult>();
            }
            catch (Exception)
            {
                // Corrupt or unreadable history isn't worth an error - start fresh.
                return new List<DiskSpeedTestResult>();
            }
        }

        public static void SaveHistory(IEnumerable<DiskSpeedTestResult> history, string? path = null)
        {
            string file = path ?? HistoryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string json = JsonSerializer.Serialize(history.Take(MaxHistory).ToList(), JsonOptions);
            string temp = file + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, file, overwrite: true);
        }

        /// <summary>Sets each result's bar widths relative to the fastest speed in the list.</summary>
        public static void ScaleHistoryBars(IReadOnlyList<DiskSpeedTestResult> history)
        {
            double max = 0;
            foreach (var r in history) max = Math.Max(max, Math.Max(r.WriteMBps, r.ReadMBps));
            foreach (var r in history)
            {
                r.WriteBarPercent = max > 0 ? r.WriteMBps * 100 / max : 0;
                r.ReadBarPercent = max > 0 ? r.ReadMBps * 100 / max : 0;
            }
        }
    }
}
