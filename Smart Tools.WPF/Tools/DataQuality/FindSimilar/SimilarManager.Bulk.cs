using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Fingerprinting a list of files with the shared cache. Used by the Find Similar scan and by
    /// Duplicate Finder's "same content, any format" stage, so both read the same cache and the
    /// same decoders.
    /// </summary>
    public sealed partial class SimilarManager
    {
        internal static string FfmpegPath => FfmpegExe;

        /// <summary>Duration and picture size (rotation applied) of a video; zeros when ffprobe can't read it.</summary>
        internal static async Task<ProbeResult> ProbeVideoAsync(string path, CancellationToken token) =>
            await ProbeAsync(path, video: true, token).ConfigureAwait(false);

        /// <summary>Length in seconds from ffprobe; 0 when it can't be read.</summary>
        internal static async Task<double> ProbeDurationAsync(string path, CancellationToken token) =>
            (await ProbeAsync(path, video: false, token).ConfigureAwait(false)).Duration;

        /// <summary>
        /// Fingerprints <paramref name="files"/> in parallel. A file that can't be read, or whose decoder times out,
        /// gets a null entry; the scan goes on. Fingerprints already known to the cache are not recomputed.
        /// </summary>
        /// <param name="onFileDone">Called once per file, from worker threads.</param>
        /// <param name="onUnreadable">Called for each null result.</param>
        /// <param name="unreadableSoFar">Used only to cap how many failures are logged one by one.</param>
        internal async Task<(FileFingerprint?[] Prints, int FromCache)> FingerprintFilesAsync(
            IReadOnlyList<FileInfo> files, SimilarDetectionMethod method, int parallelism,
            Action<string>? onFileDone, Action? onUnreadable, Func<int>? unreadableSoFar, CancellationToken token)
        {
            var cache = Cache;
            cache?.BeginScan();
            int fromCache = 0;
            var prints = new FileFingerprint?[files.Count];

            await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelism),
                CancellationToken = token,
            }, async (i, ct) =>
            {
                try
                {
                    if (cache != null && cache.TryGet(files[i], method, out var known))
                    {
                        prints[i] = known;
                        Interlocked.Increment(ref fromCache);
                    }
                    else
                    {
                        prints[i] = await _fingerprinter(files[i].FullName, method, ct).ConfigureAwait(false);
                        if (prints[i] != null) cache?.Put(files[i], method, prints[i]!);
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
                    if ((unreadableSoFar?.Invoke() ?? 0) < 50) Log.Info($"Fingerprint failed, skipped: {files[i].FullName} ({ex.GetType().Name}: {ex.Message})");
                }

                if (prints[i] == null) onUnreadable?.Invoke();
                onFileDone?.Invoke(files[i].FullName);
            }).ConfigureAwait(false);

            cache?.Save();
            return (prints, fromCache);
        }
    }
}
