using Smart_Tools.WPF.Models;
using SoundFingerprinting;
using SoundFingerprinting.Audio;
using SoundFingerprinting.Builder;
using SoundFingerprinting.Data;
using SoundFingerprinting.InMemory;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Audio with SoundFingerprinting (MIT, in-memory model service only - no external storage or key).
    /// ffmpeg decodes any container/codec to 5.5 kHz mono float samples; SoundFingerprinting turns them
    /// into landmark hashes that survive re-encoding, a different format or bitrate, a trimmed start or end,
    /// and moderate noise. Every file is indexed, then every file is queried against the index.
    /// </summary>
    internal static class SimilarAudioEngine
    {
        public const int SampleRate = 5512;

        /// <summary>The model service holds roughly 5 GB per 100 hours of audio, so only the first half hour of a track is used.</summary>
        public const int MaxSeconds = 30 * 60;

        private static readonly IAudioService AudioService = new SoundFingerprintingAudioService();

        /// <summary>Hashes decoded samples; null when the track is silent or too short to fingerprint.</summary>
        public static async Task<AVHashes?> HashAsync(float[] samples, string origin)
        {
            if (samples.Length < SampleRate * 3) return null;   // under ~3 s
            var audio = new AudioSamples(samples, origin, SampleRate);
            var hashes = await FingerprintCommandBuilder.Instance
                .BuildFingerprintCommand()
                .From(audio)
                .UsingServices(AudioService)
                .Hash()
                .ConfigureAwait(false);
            return hashes.Audio == null || hashes.Audio.Count == 0 ? null : hashes;
        }

        /// <summary>
        /// Finds files whose audio contains (or is contained in) another file's audio.
        /// <paramref name="minConfidence"/> is 0-1. Similarity reported is the query coverage of the best match.
        /// </summary>
        public static async Task<List<SimilarEdge>> FindEdgesAsync(IReadOnlyList<MediaSignature?> signatures, double minConfidence,
            IProgress<int>? onQueried, CancellationToken token)
        {
            var model = new InMemoryModelService();
            for (int i = 0; i < signatures.Count; i++)
            {
                if (signatures[i]?.AudioHashes is AVHashes hashes)
                    await model.InsertAsync(new TrackInfo(i.ToString(), $"track {i}", "", MediaType.Audio), hashes, token).ConfigureAwait(false);
            }

            var best = new Dictionary<(int, int), SimilarEdge>();
            int done = 0;
            for (int i = 0; i < signatures.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (signatures[i]?.AudioHashes is not AVHashes query) continue;

                var result = await QueryCommandBuilder.Instance
                    .BuildQueryCommand()
                    .From(query)
                    .UsingServices(model)
                    .Query(token)
                    .ConfigureAwait(false);

                if (result.Audio?.ResultEntries != null)
                {
                    foreach (var match in result.Audio.ResultEntries)
                    {
                        if (match.Track?.Id == null || !int.TryParse(match.Track.Id, out int j) || j == i || j < 0 || j >= signatures.Count) continue;
                        double confidence = match.Confidence;
                        if (confidence < minConfidence) continue;

                        var tags = SimilarTag.None;
                        double a = signatures[i]!.DurationSeconds, b = signatures[j]!.DurationSeconds;
                        if (a > 0 && b > 0 && Math.Max(a, b) / Math.Min(a, b) > 1.15) tags |= SimilarTag.Trimmed;

                        var key = (Math.Min(i, j), Math.Max(i, j));
                        var edge = new SimilarEdge(key.Item1, key.Item2, Math.Round(Math.Min(100, confidence * 100)), tags);
                        if (!best.TryGetValue(key, out var old) || old.Similarity < edge.Similarity) best[key] = edge;
                    }
                }
                onQueried?.Report(++done);
            }
            return best.Values.ToList();
        }
    }
}
