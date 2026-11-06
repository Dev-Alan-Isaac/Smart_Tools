using ImageMagick;
using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>What "Same content, any format" remembers about one picture: about 130 bytes (more at Heavy).</summary>
    internal sealed class ImageSig
    {
        public int Width, Height;

        /// <summary>Difference hash and perceptual hash of the picture in each of the 8 flips/rotations ([0] = as it is).</summary>
        public ulong[] D = Array.Empty<ulong>();
        public ulong[] P = Array.Empty<ulong>();

        /// <summary>Heavy level: the same for the centre crops.</summary>
        public ulong[]? CropD, CropP;
    }

    /// <summary>
    /// Pictures that were flipped, rotated, resized, stretched, letterboxed, recoloured or (Heavy) cropped or inverted.
    /// A picture is a 64x64 grey thumbnail with its black bars trimmed, so none of that depends on size, aspect ratio or
    /// file type. Two hashes have to agree (a smooth sky-and-ground picture fools one of them alone). Candidate pairs
    /// come from a hash index, not from comparing every pair.
    /// </summary>
    internal static class ImageContentMatcher
    {
        internal readonly record struct Rules(int MaxD, int MaxP, bool Heavy);

        internal static Rules RulesFor(SameContentLevel level) => level == SameContentLevel.Heavy ? new Rules(8, 12, true) : new Rules(6, 10, false);

        /// <summary>Null when the picture can't be read or is blank.</summary>
        public static ImageSig? Read(string path, bool crops, CancellationToken token)
        {
            try
            {
                byte[] gray;
                int width, height;
                using (var loaded = ImageLoad.Open(path, token, 512))
                {
                    var image = loaded.Image;
                    image.AutoOrient();
                    width = (int)loaded.SourceWidth;
                    height = (int)loaded.SourceHeight;
                    if ((image.Width > image.Height) != (width > height) && image.Width != image.Height) (width, height) = (height, width);
                    if (width == 0 || height == 0) (width, height) = ((int)image.Width, (int)image.Height);

                    image.Resize(new MagickGeometry(FrameHasher.Side, FrameHasher.Side) { IgnoreAspectRatio = true });
                    image.ColorSpace = ColorSpace.Gray;
                    image.Depth = 8;
                    gray = image.ToByteArray(MagickFormat.Gray);
                }
                if (gray.Length < FrameHasher.FrameBytes) return null;
                if (!FrameHasher.TryHashPicture(gray, crops, out var d, out var p, out var cropD, out var cropP)) return null;
                return new ImageSig { Width = width, Height = height, D = d, P = p, CropD = cropD, CropP = cropP };
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Percent alike (0-100) when the pictures match, else -1.</summary>
        internal static int Verify(ImageSig a, ImageSig b, Rules rules)
        {
            int best = -1;
            void Consider(int dd, int pp)
            {
                if (dd > rules.MaxD || pp > rules.MaxP) return;
                best = Math.Max(best, (int)Math.Round(100 * (1 - (dd + pp) / 128.0)));
            }

            for (int o = 0; o < FrameHasher.Orientations; o++)
            {
                Consider(BitOperations.PopCount(a.D[0] ^ b.D[o]), BitOperations.PopCount(a.P[0] ^ b.P[o]));
                // Colour negative: every comparison in the hash flips.
                if (rules.Heavy) Consider(64 - BitOperations.PopCount(a.D[0] ^ b.D[o]), 64 - BitOperations.PopCount(a.P[0] ^ b.P[o]));
            }
            if (best >= 0 || !rules.Heavy) return best;

            // One picture is a centre crop of the other: the crops of the larger one match the whole of the smaller.
            // Tighter than for whole pictures, because a small region says less.
            var crop = rules with { MaxD = rules.MaxD - 2, MaxP = rules.MaxP - 2 };
            for (int pass = 0; pass < 2; pass++)
            {
                var (full, small) = pass == 0 ? (a, b) : (b, a);
                if (full.CropD == null || full.CropP == null) continue;
                for (int k = 0; k < full.CropD.Length; k++)
                {
                    int dd = BitOperations.PopCount(full.CropD[k] ^ small.D[0]);
                    int pp = BitOperations.PopCount(full.CropP[k] ^ small.P[0]);
                    if (dd <= crop.MaxD && pp <= crop.MaxP) best = Math.Max(best, (int)Math.Round(100 * (1 - (dd + pp) / 128.0)));
                }
            }
            return best;
        }

        /// <summary>Groups of pictures with the percent alike.</summary>
        public static async Task<List<(List<int> Members, int Percent)>> MatchAsync(
            IReadOnlyList<ImageSig?> sigs, SameContentLevel level, int parallelism, CancellationToken token)
        {
            var rules = RulesFor(level);
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = token };

            var hashes = new List<ulong>();
            var owners = new List<int>();
            for (int i = 0; i < sigs.Count; i++)
                if (sigs[i] != null)
                {
                    hashes.Add(sigs[i]!.D[0]);
                    owners.Add(i);
                }
            if (hashes.Count < 2) return new();
            var index = new FrameIndex(hashes.ToArray(), owners.ToArray());

            var candidates = new ConcurrentDictionary<long, byte>();
            await Task.Run(() => Parallel.For(0, sigs.Count, po, i =>
            {
                var s = sigs[i];
                if (s == null) return;
                var found = new HashSet<int>();
                foreach (ulong h in s.D)
                {
                    index.Query(h, i, found);
                    if (rules.Heavy) index.Query(~h, i, found);
                }
                if (rules.Heavy && s.CropD != null)
                    foreach (ulong h in s.CropD) index.Query(h, i, found);
                foreach (int other in found)
                    candidates[((long)Math.Min(i, other) << 32) | (uint)Math.Max(i, other)] = 0;
            }), token).ConfigureAwait(false);

            var edges = new ConcurrentDictionary<long, int>();
            await Task.Run(() => Parallel.ForEach(candidates.Keys, po, key =>
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                int percent = Verify(sigs[a]!, sigs[b]!, rules);
                if (percent >= 0) edges[key] = percent;
            }), token).ConfigureAwait(false);

            // Any verified pair joins two pictures: edited copies need not match each other directly
            // (a rotated copy and a cropped copy of the same original).
            var parent = Enumerable.Range(0, sigs.Count).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            foreach (var key in edges.Keys) parent[Find((int)(key >> 32))] = Find((int)(key & 0xFFFFFFFF));

            var percentOf = edges.GroupBy(e => Find((int)(e.Key >> 32))).ToDictionary(g => g.Key, g => (int)Math.Round(g.Average(e => e.Value)));
            return Enumerable.Range(0, sigs.Count).Where(i => sigs[i] != null).GroupBy(Find).Where(g => g.Count() > 1)
                .Select(g => (g.ToList(), percentOf.TryGetValue(g.Key, out int p) ? p : 0)).ToList();
        }
    }
}
