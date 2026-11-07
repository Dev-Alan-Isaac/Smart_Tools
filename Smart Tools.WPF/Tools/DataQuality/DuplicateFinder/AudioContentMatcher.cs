using SoundFingerprinting;
using SoundFingerprinting.Audio;
using SoundFingerprinting.Builder;
using SoundFingerprinting.Data;
using SoundFingerprinting.InMemory;
using SoundFingerprinting.Query;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Finds songs that are the same recording in different files (mp3 / flac / wav, other bitrates, volume
    /// changes, trimmed ends) with the SoundFingerprinting core library (MIT). ffmpeg decodes the audio; the
    /// hashes go into an in-memory index so every song is looked up against the rest instead of compared pair
    /// by pair. Neither the Emy storage nor the Bass audio add-ons are used.
    /// </summary>
    internal static class AudioContentMatcher
    {
        /// <summary>SoundFingerprinting works at 5512 Hz mono.</summary>
        private const int SampleRate = 5512;

        /// <summary>
        /// Only the start of each song is fingerprinted: hashes of a whole 2-hour file would not fit in memory
        /// for thousands of files. The duration check keeps two different songs with the same intro apart.
        /// </summary>
        public const int AnalyzeSeconds = 60;

        /// <summary>
        /// Measured: about 0.16 MB of index per song at 60 s, so this many songs stay near 1 GB.
        /// More songs than this in one scan are not all compared (the scan says so).
        /// </summary>
        public const int MaxSongsPerScan = 6000;

        private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(90);

        public sealed class Track
        {
            public required int Index { get; init; }
            public required string Path { get; init; }
            public double DurationSeconds { get; set; }
            public AVHashes? Hashes { get; set; }
        }

        public readonly record struct Edge(int A, int B, double Quality);

        /// <summary>Null when the file isn't audio ffmpeg can decode.</summary>
        public static async Task<AVHashes?> HashAsync(string path, CancellationToken token)
        {
            byte[] raw = await SimilarManager.RunForBytesAsync(SimilarManager.FfmpegPath, new[]
            {
                "-i", path, "-t", AnalyzeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-f", "f32le", "-",
            }, token, DecodeTimeout).ConfigureAwait(false);

            // Under 5 s there is not enough sound to identify.
            if (raw.Length < SampleRate * 4 * 5) return null;

            var samples = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 4);

            try
            {
                var hashes = await FingerprintCommandBuilder.Instance
                    .BuildFingerprintCommand()
                    .From(new AudioSamples(samples, path, SampleRate))
                    .UsingServices(new SoundFingerprintingAudioService())
                    .Hash()
                    .ConfigureAwait(false);
                return hashes.IsEmpty ? null : hashes;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                Log.Warn($"Audio fingerprint failed: {path}", ex);
                return null;
            }
        }

        /// <summary>
        /// Looks each track up in an index of all the others and returns the pairs that are the same recording.
        /// <paramref name="minCoverage"/>: how much of both songs must line up (0-1);
        /// <paramref name="durationTolerance"/>: allowed length difference (0.02 = 2 %).
        /// </summary>
        public static async Task<List<Edge>> MatchAsync(IReadOnlyList<Track> tracks, double minCoverage, double durationTolerance,
            int parallelism, Action<int>? onQueried, CancellationToken token)
        {
            var model = new InMemoryModelService();
            foreach (var t in tracks)
            {
                token.ThrowIfCancellationRequested();
                if (t.Hashes == null) continue;
                await model.InsertAsync(new TrackInfo(t.Index.ToString(), System.IO.Path.GetFileName(t.Path), "", MediaType.Audio), t.Hashes, token).ConfigureAwait(false);
            }

            var edges = new System.Collections.Concurrent.ConcurrentBag<Edge>();
            int done = 0;
            await Parallel.ForEachAsync(tracks.Where(t => t.Hashes != null), new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelism),
                CancellationToken = token,
            }, async (track, ct) =>
            {
                var result = await QueryCommandBuilder.Instance
                    .BuildQueryCommand()
                    .From(track.Hashes!)
                    .UsingServices(model)
                    .Query(ct)
                    .ConfigureAwait(false);

                foreach (var entry in result.Audio?.ResultEntries ?? Enumerable.Empty<ResultEntry>())
                {
                    if (!int.TryParse(entry.Track.Id, out int other) || other <= track.Index) continue;   // each pair once, never itself

                    double coverage = Math.Min(entry.Coverage.QueryRelativeCoverage, entry.Coverage.TrackRelativeCoverage);
                    if (coverage < minCoverage) continue;

                    var otherTrack = tracks[other];
                    if (track.DurationSeconds > 0 && otherTrack.DurationSeconds > 0)
                    {
                        double ratio = Math.Abs(track.DurationSeconds - otherTrack.DurationSeconds) / Math.Max(track.DurationSeconds, otherTrack.DurationSeconds);
                        if (ratio > durationTolerance) continue;
                    }
                    edges.Add(new Edge(track.Index, other, coverage));
                }
                onQueried?.Invoke(Interlocked.Increment(ref done));
            }).ConfigureAwait(false);

            return edges.ToList();
        }
    }
}
