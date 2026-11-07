using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Helpers shared with Duplicate Finder's "same content, any format" stage: ffmpeg/ffprobe access and a
    /// bulk fingerprint call that reads files with the same engines and cache as the Find Similar scan.
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
        /// Fingerprints <paramref name="files"/> in parallel (pictures and videos) as the older one-line
        /// <see cref="FileFingerprint"/> that Duplicate Finder clusters. A file that can't be read, or whose decoder
        /// times out, gets a null entry; the scan goes on. Signatures already in the cache are not recomputed.
        /// </summary>
        /// <param name="onFileDone">Called once per file, from worker threads.</param>
        /// <param name="onUnreadable">Called for each null result.</param>
        /// <param name="unreadableSoFar">Used only to cap how many failures are logged one by one.</param>
        internal async Task<(FileFingerprint?[] Prints, int FromCache)> FingerprintFilesAsync(
            IReadOnlyList<FileInfo> files, SimilarDetectionMethod method, int parallelism,
            Action<string>? onFileDone, Action? onUnreadable, Func<int>? unreadableSoFar, CancellationToken token)
        {
            var kind = KindOf(method);
            var cache = kind is MediaKind.Image or MediaKind.Video && _usesMediaDecoders ? SimilarFingerprintCache.Load() : null;
            int fromCache = 0;
            using var closeJournalOnCancel = token.Register(() => cache?.Close());
            var prints = new FileFingerprint?[files.Count];

            await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelism),
                CancellationToken = token,
            }, async (i, ct) =>
            {
                try
                {
                    var sig = cache?.TryGet(files[i]);
                    if (sig != null)
                    {
                        Interlocked.Increment(ref fromCache);
                    }
                    else
                    {
                        sig = await _signer(files[i].FullName, kind, ct).ConfigureAwait(false);
                        if (sig != null) cache?.Put(files[i], sig);
                    }
                    prints[i] = ToFileFingerprint(sig, kind);
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

        /// <summary>
        /// The older fingerprint shape: one hash for a picture; for a video five frames at fixed fractions of its
        /// length (so two videos of the same length line up frame for frame). Null when nothing usable was read.
        /// </summary>
        private static FileFingerprint? ToFileFingerprint(MediaSignature? sig, MediaKind kind)
        {
            if (sig == null) return null;

            if (kind == MediaKind.Image)
            {
                if (sig.ImageHashes.Length == 0) return null;
                return new FileFingerprint
                {
                    Fingerprint = new Fingerprint(sig.ImageHashes[0]),
                    Variance = sig.Variance,
                    Width = sig.Width,
                    Height = sig.Height,
                };
            }

            if (kind == MediaKind.Video)
            {
                var picked = new List<ulong>(5);
                for (int k = 1; k <= 5; k++)
                {
                    int index = (int)((long)sig.Frames.Length * k / 6);
                    if (index < sig.Frames.Length && sig.Frames[index] != MediaSignature.FlatFrame) picked.Add(sig.Frames[index]);
                }
                if (picked.Count == 0) return null;
                return new FileFingerprint
                {
                    Fingerprint = new Fingerprint(picked.ToArray()),
                    DurationSeconds = sig.DurationSeconds,
                    Width = sig.Width,
                    Height = sig.Height,
                };
            }
            return null;
        }
    }
}
