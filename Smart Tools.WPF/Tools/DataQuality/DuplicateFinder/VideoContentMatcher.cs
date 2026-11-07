using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Numerics;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DedupeTest")]

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Lookup of 64-bit frame hashes within a few bits, without comparing every pair. A hash within R bits of another
    /// must share at least one exact chunk when it is cut into R + 1 pieces, so a query only reads the entries that
    /// share a chunk (multi-index hashing). Public so the scale test can time it with random hashes.
    /// </summary>
    public sealed class FrameIndex
    {
        private static readonly int[] ChunkBits = { 10, 9, 9, 9, 9, 9, 9 };   // 7 chunks = radius 6
        public const int Radius = 6;

        private readonly ulong[] _hashes;
        private readonly int[] _owner;
        private readonly int[][] _start;     // per chunk: bucket start offsets (CSR)
        private readonly int[][] _entries;   // per chunk: entry ids ordered by bucket

        public int Count => _hashes.Length;

        public FrameIndex(ulong[] hashes, int[] owner)
        {
            _hashes = hashes;
            _owner = owner;
            _start = new int[ChunkBits.Length][];
            _entries = new int[ChunkBits.Length][];
            int shift = 0;
            for (int c = 0; c < ChunkBits.Length; c++)
            {
                int buckets = 1 << ChunkBits[c];
                var start = new int[buckets + 1];
                for (int i = 0; i < hashes.Length; i++) start[Chunk(hashes[i], shift, ChunkBits[c]) + 1]++;
                for (int b = 0; b < buckets; b++) start[b + 1] += start[b];
                var fill = (int[])start.Clone();
                var entries = new int[hashes.Length];
                for (int i = 0; i < hashes.Length; i++) entries[fill[Chunk(hashes[i], shift, ChunkBits[c])]++] = i;
                _start[c] = start;
                _entries[c] = entries;
                shift += ChunkBits[c];
            }
        }

        private static int Chunk(ulong h, int shift, int bits) => (int)((h >> shift) & ((1UL << bits) - 1));

        /// <summary>Owners (other than <paramref name="self"/>) with a frame within <see cref="Radius"/> bits of <paramref name="hash"/>.</summary>
        public void Query(ulong hash, int self, HashSet<int> owners)
        {
            int shift = 0;
            for (int c = 0; c < ChunkBits.Length; c++)
            {
                int b = Chunk(hash, shift, ChunkBits[c]);
                var entries = _entries[c];
                for (int p = _start[c][b], end = _start[c][b + 1]; p < end; p++)
                {
                    int e = entries[p];
                    int owner = _owner[e];
                    if (owner != self && BitOperations.PopCount(_hashes[e] ^ hash) <= Radius) owners.Add(owner);
                }
                shift += ChunkBits[c];
            }
        }
    }

    /// <summary>
    /// Matches videos that were rotated, flipped, stretched, letterboxed, given an intro / outro, cut or trimmed
    /// (Medium) and also centre-cropped (Heavy). A frame index proposes the pairs; each proposal is then checked by
    /// letting matching frames vote for one time offset between the two videos.
    /// </summary>
    internal static class VideoContentMatcher
    {
        /// <param name="MaxDistance">Bits of 64 a frame may differ.</param>
        /// <param name="MinMatchRatio">Of the frames of the shorter video that can be looked for in the other video's sampled
        /// windows, this share must be found. A copy finds nearly all; a video that only shares one section does not.</param>
        /// <param name="MinShare">The frames found must also be this share of the shorter video's sampled frames.</param>
        /// <param name="MinLengthRatio">The shorter video must be at least this fraction of the longer one's length.</param>
        internal readonly record struct Rules(int MaxDistance, double MinMatchRatio, double MinShare, double MinLengthRatio, bool Crops);

        internal static Rules RulesFor(SameContentLevel level) => level switch
        {
            SameContentLevel.Heavy => new Rules(8, 0.80, 0.20, 0.30, true),
            _ => new Rules(6, 0.85, 0.25, 0.60, false),
        };

        /// <summary>Percent alike (0-100) when the two videos match, else -1.</summary>
        internal static int Verify(VideoSig x, VideoSig y, Rules rules)
        {
            // The shorter video is the one that has to be found inside the longer one.
            var (a, b) = x.Duration <= y.Duration ? (x, y) : (y, x);
            if (a.Duration < b.Duration * rules.MinLengthRatio || a.NonFlat == 0) return -1;

            int best = -1;
            for (int o = 0; o < FrameHasher.Orientations; o++)
            {
                var pairs = new List<(int A, int B, int D)>();
                for (int i = 0; i < a.H.Length; i++)
                {
                    if (a.Flat[i]) continue;
                    for (int j = 0; j < b.H.Length; j++)
                    {
                        if (b.Flat[j]) continue;
                        int d = BitOperations.PopCount(a.H[i][0] ^ b.H[j][o]);
                        if (d <= rules.MaxDistance) pairs.Add((i, j, d));
                    }
                }
                best = Math.Max(best, Evaluate(pairs, a, b, onlyA: null, rules, allowCut: true));
            }
            if (best >= 0) return best;

            if (rules.Crops)
            {
                // One video is a centre crop of the other: the crop variants of the larger picture match the other's full frames.
                for (int pass = 0; pass < 2; pass++)
                {
                    var (full, cropped) = pass == 0 ? (x, y) : (y, x);
                    if (full.Crop == null) continue;
                    double ratio = Math.Min(full.Duration, cropped.Duration) / Math.Max(0.001, Math.Max(full.Duration, cropped.Duration));
                    if (ratio < rules.MinLengthRatio) continue;
                    var queryFrames = full.QueryFrames.Where(f => !full.Flat[f]).ToList();

                    for (int k = 0; k < FrameHasher.CropShapes.Length; k++)
                        for (int o = 0; o < FrameHasher.Orientations; o++)
                        {
                            var pairs = new List<(int A, int B, int D)>();
                            for (int q = 0; q < full.QueryFrames.Length; q++)
                            {
                                var crop = full.Crop[q];
                                if (crop.Length == 0) continue;
                                ulong h = crop[k * FrameHasher.Orientations + o];
                                for (int j = 0; j < cropped.H.Length; j++)
                                {
                                    if (cropped.Flat[j]) continue;
                                    int d = BitOperations.PopCount(h ^ cropped.H[j][0]);
                                    if (d <= rules.MaxDistance) pairs.Add((full.QueryFrames[q], j, d));
                                }
                            }
                            // Only the query frames carry crop variants, so demand most of them and a tighter distance.
                            pairs.RemoveAll(p => p.D > 6);
                            int percent = Evaluate(pairs, full, cropped, queryFrames, rules with { MinMatchRatio = 0.85 }, allowCut: false, minMatched: 4);
                            if (percent >= 0) return percent;
                        }
                }
            }
            return -1;
        }

        /// <summary>
        /// Decides from the matching frame pairs of one orientation. The frames vote for a time offset (neighbouring
        /// offsets merged: frames are seconds apart); with <paramref name="allowCut"/> one more offset can be added,
        /// which is a cut or an inserted section. The sections must follow each other in the same order in both
        /// videos, so footage that was only re-ordered does not count. Then every frame of the shorter video that
        /// falls inside a sampled window of the other one must have been found.
        /// </summary>
        private static int Evaluate(List<(int A, int B, int D)> pairs, VideoSig a, VideoSig b, IReadOnlyCollection<int>? onlyA, Rules rules, bool allowCut, int minMatched = 3)
        {
            if (pairs.Count == 0) return -1;

            var bins = new Dictionary<int, List<(int A, int B, int D)>>();
            foreach (var p in pairs)
            {
                int key = (int)Math.Round((a.Times[p.A] - b.Times[p.B]) / VideoSignatureReader.Step);
                if (!bins.TryGetValue(key, out var list)) bins[key] = list = new();
                list.Add(p);
            }

            List<(int A, int B, int D)> Around(int key)
            {
                var all = new List<(int A, int B, int D)>();
                for (int k = key - 1; k <= key + 1; k++)
                    if (bins.TryGetValue(k, out var l)) all.AddRange(l);
                return all;
            }
            static int Distinct(List<(int A, int B, int D)> l) => Math.Min(l.Select(p => p.A).Distinct().Count(), l.Select(p => p.B).Distinct().Count());

            int first = int.MinValue, firstVotes = 0;
            foreach (int key in bins.Keys)
            {
                int v = Distinct(Around(key));
                if (v > firstVotes) (first, firstVotes) = (key, v);
            }
            var chosen = Around(first);
            var offsets = new List<int> { first };

            if (allowCut)
            {
                int second = int.MinValue, secondVotes = 0;
                foreach (int key in bins.Keys)
                {
                    if (Math.Abs(key - first) <= 2) continue;
                    int v = Distinct(Around(key));
                    if (v > secondVotes) (second, secondVotes) = (key, v);
                }
                if (secondVotes >= 2)
                {
                    var both = chosen.Concat(Around(second)).OrderBy(p => a.Times[p.A]).ThenBy(p => b.Times[p.B]).ToList();
                    float previous = float.MinValue;
                    bool ordered = true;
                    foreach (var p in both)
                    {
                        float t = b.Times[p.B];
                        if (t < previous - (float)VideoSignatureReader.Step) { ordered = false; break; }
                        previous = Math.Max(previous, t);
                    }
                    if (ordered)
                    {
                        chosen = both;
                        offsets.Add(second);
                    }
                }
            }

            var inScope = onlyA ?? Enumerable.Range(0, a.H.Length).Where(i => !a.Flat[i]).ToList();
            var matched = chosen.Select(p => p.A).Where(inScope.Contains).ToHashSet();
            int notFound = 0;
            foreach (int i in inScope)
            {
                if (matched.Contains(i)) continue;
                // Could this frame have been found? Only if the other video was sampled around that moment.
                bool lookable = offsets.All(k => b.Times.Any(t => Math.Abs(t - (a.Times[i] - k * VideoSignatureReader.Step)) <= VideoSignatureReader.Step));
                if (lookable) notFound++;
            }

            int need = Math.Max(Math.Min(minMatched, inScope.Count), (int)Math.Ceiling(rules.MinShare * inScope.Count));
            if (matched.Count < need) return -1;
            if (matched.Count / (double)(matched.Count + notFound) < rules.MinMatchRatio) return -1;

            double avg = chosen.Where(p => matched.Contains(p.A)).Average(p => (double)p.D);
            return (int)Math.Round(100 * (1 - avg / 64));
        }

        /// <summary>Groups of videos with the percent alike, for Medium and Heavy.</summary>
        public static async Task<List<(List<int> Members, int Percent)>> MatchAsync(
            IReadOnlyList<VideoSig?> sigs, SameContentLevel level, int parallelism, CancellationToken token)
        {
            var rules = RulesFor(level);
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = token };

            // 1. Index every frame of every video.
            var hashes = new List<ulong>();
            var owners = new List<int>();
            for (int v = 0; v < sigs.Count; v++)
            {
                var s = sigs[v];
                if (s == null) continue;
                for (int i = 0; i < s.H.Length; i++)
                    if (!s.Flat[i])
                    {
                        hashes.Add(s.H[i][0]);
                        owners.Add(v);
                    }
            }
            if (hashes.Count == 0) return new();
            var index = new FrameIndex(hashes.ToArray(), owners.ToArray());

            // 2. Each video looks up a few of its frames, in every orientation (and crop at Heavy).
            var candidates = new ConcurrentDictionary<long, byte>();
            await Task.Run(() => Parallel.For(0, sigs.Count, po, v =>
            {
                var s = sigs[v];
                if (s == null) return;
                var found = new HashSet<int>();
                foreach (int f in s.QueryFrames)
                    for (int o = 0; o < FrameHasher.Orientations; o++)
                        index.Query(s.H[f][o], v, found);
                if (rules.Crops && s.Crop != null)
                    foreach (var crop in s.Crop.Take(3))
                        foreach (ulong h in crop) index.Query(h, v, found);
                foreach (int other in found)
                    candidates[((long)Math.Min(v, other) << 32) | (uint)Math.Max(v, other)] = 0;
            }), token).ConfigureAwait(false);

            // 3. Verify every proposed pair.
            var edges = new ConcurrentDictionary<long, int>();
            await Task.Run(() => Parallel.ForEach(candidates.Keys, po, key =>
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                int percent = Verify(sigs[a]!, sigs[b]!, rules);
                if (percent >= 0) edges[key] = percent;
            }), token).ConfigureAwait(false);

            // 4. Any verified pair joins two videos. Edited copies need not match each other (a cropped copy and a
            //    letterboxed copy of the same video), and each pair already passed the order and coverage checks.
            var parent = Enumerable.Range(0, sigs.Count).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            foreach (var key in edges.Keys)
                parent[Find((int)(key >> 32))] = Find((int)(key & 0xFFFFFFFF));

            var result = new List<(List<int>, int)>();
            foreach (var members in Enumerable.Range(0, sigs.Count).Where(i => sigs[i] != null).GroupBy(Find).Where(g => g.Count() > 1))
            {
                var set = new HashSet<int>(members);
                var percents = edges.Where(e => set.Contains((int)(e.Key >> 32))).Select(e => e.Value).ToList();
                result.Add((members.ToList(), percents.Count == 0 ? 0 : (int)Math.Round(percents.Average())));
            }
            return result;
        }
    }
}
