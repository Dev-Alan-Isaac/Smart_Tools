using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Numerics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Turns signatures into "these two files are the same content" pairs, then pairs into groups.
    /// Nothing here touches files: it works on <see cref="MediaSignature"/>s only.
    /// </summary>
    internal static class SimilarMatcher
    {
        /// <summary>The 0-100 slider mapped to what each media kind needs.</summary>
        public static int HashDistanceFor(double thresholdPercent) => (int)Math.Floor((100.0 - thresholdPercent) / 100.0 * 64.0);
        public static int FrameToleranceFor(double thresholdPercent) => 6 + (int)Math.Round((100.0 - thresholdPercent) / 100.0 * 14.0);
        public static double VideoCoverageFor(double thresholdPercent) => 0.5 + Math.Clamp((thresholdPercent - 50) / 50.0, 0, 1) * 0.3;
        public static int OrbInliersFor(double thresholdPercent) => 12 + (int)Math.Round(Math.Clamp(thresholdPercent - 50, 0, 50) * 0.3);
        public static double AudioConfidenceFor(double thresholdPercent) => 0.3 + Math.Clamp((thresholdPercent - 50) / 50.0, 0, 1) * 0.5;

        private const double FlatVariance = 10.0;
        private const int OrbMinVotes = 6;
        private const int OrbVerifyPerImage = 8;
        private const int PostingCap = 64;

        // =================================================================
        //  Images
        // =================================================================

        public static List<SimilarEdge> ImageEdges(IReadOnlyList<MediaSignature?> sigs, double threshold, bool findCrops,
            Action<int, int>? onProgress, CancellationToken token)
        {
            int n = sigs.Count;
            int maxDistance = HashDistanceFor(threshold);
            var edges = new ConcurrentBag<SimilarEdge>();
            var found = new ConcurrentDictionary<long, byte>();
            int done = 0;

            // 1. Hash pass: every pair, 8 variants of one side against the other (flip/rotate proof).
            Parallel.For(0, n, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                var a = sigs[i];
                if (a != null && a.ImageHashes.Length == 8)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        var b = sigs[j];
                        if (b == null || b.ImageHashes.Length != 8) continue;

                        var (distance, variant) = SimilarImageEngine.BestHashDistance(a, b);
                        bool flat = a.Variance < FlatVariance || b.Variance < FlatVariance;
                        double allowed = flat ? maxDistance * 0.5 : maxDistance;
                        if (variant != 0) allowed *= 0.75;   // 8 chances to collide: demand a closer hash
                        if (distance > allowed) continue;

                        var tags = SimilarTag.None;
                        if ((variant & 1) != 0) tags |= SimilarTag.Flipped;
                        if ((variant >> 1) != 0) tags |= SimilarTag.Rotated;
                        edges.Add(new SimilarEdge(i, j, Math.Round(100.0 * (1 - distance / 64.0)), tags));
                        found[Pair(i, j)] = 0;
                    }
                }
                onProgress?.Invoke(Interlocked.Increment(ref done), n * 2);
            });

            if (!findCrops) return edges.ToList();

            // 2. Crop/edit pass: shortlist by shared ORB descriptor prefixes, then verify with RANSAC.
            var index = new Dictionary<uint, List<int>>();
            for (int i = 0; i < n; i++)
            {
                var s = sigs[i];
                if (s == null || s.OrbRows < 12) continue;
                foreach (uint key in Keys(s.Orb))
                {
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>();
                    if (list.Count < PostingCap && (list.Count == 0 || list[^1] != i)) list.Add(i);
                }
            }

            int minInliers = OrbInliersFor(threshold);
            Parallel.For(0, n, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                var a = sigs[i];
                if (a != null && a.OrbRows >= 12)
                {
                    var votes = new Dictionary<int, int>();
                    CountVotes(index, Keys(a.Orb), i, votes);
                    CountVotes(index, Keys(a.OrbMirror), i, votes);

                    foreach (var (j, count) in votes.Where(v => v.Value >= OrbMinVotes).OrderByDescending(v => v.Value).Take(OrbVerifyPerImage))
                    {
                        token.ThrowIfCancellationRequested();
                        long pair = Pair(i, j);
                        if (found.ContainsKey(pair)) continue;

                        int inliers = SimilarImageEngine.OrbInliers(a, sigs[j]!, out bool mirrored);
                        if (inliers < minInliers) continue;

                        // The same pair can be reached from both sides; the first to claim it wins.
                        if (!found.TryAdd(pair, 0)) continue;
                        var tags = SimilarTag.Cropped | (mirrored ? SimilarTag.Flipped : SimilarTag.None);
                        edges.Add(new SimilarEdge(Math.Min(i, j), Math.Max(i, j), Math.Min(98, 55 + inliers), tags));
                    }
                }
                onProgress?.Invoke(Interlocked.Increment(ref done), n * 2);
            });

            return edges.ToList();
        }

        /// <summary>Two 16-bit prefixes of each ORB descriptor, tagged by which prefix they are.</summary>
        private static IEnumerable<uint> Keys(byte[] orb)
        {
            int rows = orb.Length / MediaSignature.OrbBytesPerRow;
            for (int r = 0; r < rows; r++)
            {
                int o = r * MediaSignature.OrbBytesPerRow;
                yield return (uint)(orb[o] | orb[o + 1] << 8);
                yield return 0x10000u | (uint)(orb[o + 2] | orb[o + 3] << 8);
            }
        }

        private static void CountVotes(Dictionary<uint, List<int>> index, IEnumerable<uint> keys, int self, Dictionary<int, int> votes)
        {
            foreach (uint key in keys)
            {
                if (!index.TryGetValue(key, out var list)) continue;
                foreach (int other in list)
                {
                    if (other == self) continue;
                    votes[other] = votes.GetValueOrDefault(other) + 1;
                }
            }
        }

        private static long Pair(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        // =================================================================
        //  Video
        // =================================================================

        public static List<SimilarEdge> VideoEdges(IReadOnlyList<MediaSignature?> sigs, double threshold, Action<int, int>? onProgress, CancellationToken token)
        {
            int n = sigs.Count;
            int tolerance = FrameToleranceFor(threshold);
            double required = VideoCoverageFor(threshold);

            // Index the frame hashes by four 16-bit slices, so a pair needs only a few near-identical frames to be considered.
            var index = new Dictionary<uint, List<int>>();
            for (int i = 0; i < n; i++)
            {
                var s = sigs[i];
                if (s == null) continue;
                foreach (uint key in FrameKeys(s.Frames))
                {
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>();
                    if (list.Count < PostingCap && (list.Count == 0 || list[^1] != i)) list.Add(i);
                }
            }

            var candidates = new ConcurrentDictionary<long, byte>();
            Parallel.For(0, n, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                var s = sigs[i];
                if (s == null) return;
                var votes = new Dictionary<int, int>();
                CountVotes(index, FrameKeys(s.Frames), i, votes);
                CountVotes(index, FrameKeys(s.FramesMirror), i, votes);
                foreach (var (j, count) in votes)
                    if (count >= 3) candidates[Pair(i, j)] = 0;
            });

            var pairs = candidates.Keys.ToArray();
            var edges = new ConcurrentBag<SimilarEdge>();
            int done = 0;
            Parallel.ForEach(pairs, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, pair =>
            {
                int i = (int)(pair >> 32), j = (int)(pair & 0xFFFFFFFF);
                var a = sigs[i]!;
                var b = sigs[j]!;
                var align = SimilarVideoEngine.Align(a, b, tolerance);
                if (align.Matched && align.Coverage >= required)
                {
                    var tags = SimilarTag.None;
                    if (align.Mirrored) tags |= SimilarTag.Flipped;
                    if (IsTrimmed(a, b, align.OffsetSeconds)) tags |= SimilarTag.Trimmed;
                    double similarity = Math.Clamp(align.Coverage * 100 - align.MeanDistance * 0.5, 0, 100);
                    edges.Add(new SimilarEdge(i, j, Math.Round(similarity), tags));
                }
                onProgress?.Invoke(Interlocked.Increment(ref done), pairs.Length);
            });
            return edges.ToList();
        }

        private static IEnumerable<uint> FrameKeys(ulong[] frames)
        {
            foreach (ulong h in frames)
            {
                if (h == MediaSignature.FlatFrame) continue;
                for (int slice = 0; slice < 4; slice++)
                    yield return (uint)(slice << 16) | (uint)((h >> (slice * 16)) & 0xFFFF);
            }
        }

        // =================================================================
        //  Describing a copy
        // =================================================================

        /// <summary>
        /// How <paramref name="copy"/> differs from <paramref name="original"/> (flipped, rotated, cropped, trimmed),
        /// worked out for this one pair so a group of many copies doesn't smear every tag onto every file.
        /// </summary>
        public static SimilarTag Describe(MediaKind kind, MediaSignature copy, MediaSignature original, double threshold)
        {
            switch (kind)
            {
                case MediaKind.Image:
                    {
                        if (copy.ImageHashes.Length != 8 || original.ImageHashes.Length != 8) return SimilarTag.None;
                        var (distance, variant) = SimilarImageEngine.BestHashDistance(copy, original);
                        int allowed = HashDistanceFor(threshold);
                        if (distance <= allowed * (variant != 0 ? 0.75 : 1.0))
                        {
                            var tags = SimilarTag.None;
                            if ((variant & 1) != 0) tags |= SimilarTag.Flipped;
                            if ((variant >> 1) != 0) tags |= SimilarTag.Rotated;
                            return tags;
                        }
                        // The hashes disagree but the file is in this group, so it was found by its features: a crop or an
                        // edit. Only the mirrored-or-not question is asked of the features (it may have joined through
                        // another copy, so its fit to this one can be weaker than the group's cut-off).
                        SimilarImageEngine.OrbInliers(copy, original, out bool mirrored);
                        return SimilarTag.Cropped | (mirrored ? SimilarTag.Flipped : SimilarTag.None);
                    }
                case MediaKind.Video:
                    {
                        var align = SimilarVideoEngine.Align(copy, original, FrameToleranceFor(threshold));
                        if (!align.Matched) return SimilarTag.None;
                        var tags = align.Mirrored ? SimilarTag.Flipped : SimilarTag.None;
                        if (IsTrimmed(copy, original, align.OffsetSeconds)) tags |= SimilarTag.Trimmed;
                        return tags;
                    }
                default:
                    return IsTrimmed(copy, original, 0) ? SimilarTag.Trimmed : SimilarTag.None;
            }
        }

        private static bool IsTrimmed(MediaSignature a, MediaSignature b, double offsetSeconds)
        {
            double ratio = a.DurationSeconds > 0 && b.DurationSeconds > 0
                ? Math.Max(a.DurationSeconds, b.DurationSeconds) / Math.Min(a.DurationSeconds, b.DurationSeconds) : 1;
            return ratio > 1.1 || (a.VideoStep > 0 && Math.Abs(offsetSeconds) >= Math.Max(a.VideoStep, b.VideoStep) * 2);
        }

        // =================================================================
        //  Grouping
        // =================================================================

        public sealed class Component
        {
            public List<int> Members { get; } = new();
            public double MeanSimilarity { get; set; }
        }

        /// <summary>Connected components of the pair graph (groups of two or more).</summary>
        public static List<Component> Group(int count, IReadOnlyList<SimilarEdge> edges)
        {
            var parent = Enumerable.Range(0, count).ToArray();
            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            foreach (var e in edges)
            {
                int ra = Find(e.A), rb = Find(e.B);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }

            var byRoot = new Dictionary<int, Component>();
            var touched = new HashSet<int>();
            foreach (var e in edges)
            {
                touched.Add(e.A);
                touched.Add(e.B);
            }
            foreach (int i in touched.OrderBy(i => i))
            {
                int root = Find(i);
                if (!byRoot.TryGetValue(root, out var c)) byRoot[root] = c = new Component();
                c.Members.Add(i);
            }

            var sum = new Dictionary<int, (double Total, int Count)>();
            foreach (var e in edges)
            {
                int root = Find(e.A);
                var s = sum.GetValueOrDefault(root);
                sum[root] = (s.Total + e.Similarity, s.Count + 1);
            }
            foreach (var (root, c) in byRoot)
                c.MeanSimilarity = sum.TryGetValue(root, out var s) && s.Count > 0 ? s.Total / s.Count : 100;

            return byRoot.Values.Where(c => c.Members.Count > 1).ToList();
        }
    }
}
