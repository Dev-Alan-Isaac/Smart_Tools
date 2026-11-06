using System.Globalization;
using System.IO;
using System.Numerics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// What "Same content, any format" remembers about one video: a hash for every sampled frame
    /// (one every <see cref="VideoSignatureReader.Step"/> seconds, in one or two windows of the file) in each of the
    /// 8 flips/rotations. Nothing here depends on resolution, aspect ratio, black bars or container.
    /// A video costs about 4 KB of memory.
    /// </summary>
    internal sealed class VideoSig
    {
        public double Duration;
        public int Width, Height;
        public float[] Times = Array.Empty<float>();

        /// <summary>[frame][orientation 0..7]; orientation 0 is the frame as it is.</summary>
        public ulong[][] H = Array.Empty<ulong[]>();

        /// <summary>Black, white or blank frames say nothing about the video, so they never vote.</summary>
        public bool[] Flat = Array.Empty<bool>();

        /// <summary>Frames used to look this video up in the index.</summary>
        public int[] QueryFrames = Array.Empty<int>();

        /// <summary>Heavy level: per query frame, hashes of the centre crops (<see cref="FrameHasher.CropShapes"/>), 8 orientations each.</summary>
        public ulong[][]? Crop;

        public int NonFlat;
    }

    /// <summary>Frame pixels to 64-bit difference hashes, with the border and orientation handling.</summary>
    internal static class FrameHasher
    {
        public const int Side = 64;
        public const int FrameBytes = Side * Side;
        public const int Orientations = 8;
        /// <summary>Centre crops tried at Heavy: both sides, and one side only (a 16:9 picture cut to 4:3, say).</summary>
        public static readonly (double W, double H)[] CropShapes = { (0.85, 0.85), (0.70, 0.70), (0.80, 1.0), (1.0, 0.80) };

        private const int G = 72;                // 72 = 9 x 8: block averages give exactly 9 columns and 8 rows
        private const int BarTolerance = 10;     // grey levels a border line may vary and still count as a bar
        private const double FlatStdDev = 5.0;

        /// <summary>False for a frame with nothing in it.</summary>
        public static bool TryHash(byte[] raw, int offset, bool withCrops, out ulong[] orient, out ulong[]? crops)
        {
            orient = Array.Empty<ulong>();
            crops = null;

            double sum = 0, sum2 = 0;
            for (int i = 0; i < FrameBytes; i++)
            {
                double v = raw[offset + i];
                sum += v;
                sum2 += v * v;
            }
            double mean = sum / FrameBytes;
            if (sum2 / FrameBytes - mean * mean < FlatStdDev * FlatStdDev) return false;

            var (x0, y0, x1, y1) = TrimBars(raw, offset);
            orient = Variants(raw, offset, x0, y0, x1 - x0, y1 - y0);

            if (withCrops)
            {
                crops = new ulong[CropShapes.Length * Orientations];
                for (int k = 0; k < CropShapes.Length; k++)
                {
                    double w = (x1 - x0) * CropShapes[k].W, h = (y1 - y0) * CropShapes[k].H;
                    var v = Variants(raw, offset, x0 + (x1 - x0 - w) / 2, y0 + (y1 - y0 - h) / 2, w, h);
                    Array.Copy(v, 0, crops, k * Orientations, Orientations);
                }
            }
            return true;
        }

        /// <summary>Letterbox / pillarbox bars: constant lines along the edge (at most a quarter of each side).</summary>
        private static (int X0, int Y0, int X1, int Y1) TrimBars(byte[] raw, int offset)
        {
            int x0 = 0, y0 = 0, x1 = Side, y1 = Side;
            const int maxTrim = Side / 4;

            bool Row(int y, int a, int b)
            {
                int min = 255, max = 0;
                for (int x = a; x < b; x++)
                {
                    int v = raw[offset + y * Side + x];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                return max - min <= BarTolerance;
            }
            bool Col(int x, int a, int b)
            {
                int min = 255, max = 0;
                for (int y = a; y < b; y++)
                {
                    int v = raw[offset + y * Side + x];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                return max - min <= BarTolerance;
            }

            while (y0 < maxTrim && Row(y0, 0, Side)) y0++;
            while (Side - y1 < maxTrim && y1 > y0 + 1 && Row(y1 - 1, 0, Side)) y1--;
            while (x0 < maxTrim && Col(x0, y0, y1)) x0++;
            while (Side - x1 < maxTrim && x1 > x0 + 1 && Col(x1 - 1, y0, y1)) x1--;

            if (x1 - x0 < 24 || y1 - y0 < 24) return (0, 0, Side, Side);
            return (x0, y0, x1, y1);
        }

        /// <summary>The picture region resampled to 72x72, then hashed in all 8 flips/rotations.</summary>
        private static ulong[] Variants(byte[] raw, int offset, double fx, double fy, double fw, double fh)
        {
            var grid = new float[G * G];
            for (int i = 0; i < G; i++)
            {
                double sy = Math.Clamp(fy + (i + 0.5) * fh / G - 0.5, 0, Side - 1);
                int y0 = (int)sy, y1 = Math.Min(Side - 1, y0 + 1);
                double ty = sy - y0;
                for (int j = 0; j < G; j++)
                {
                    double sx = Math.Clamp(fx + (j + 0.5) * fw / G - 0.5, 0, Side - 1);
                    int x0 = (int)sx, x1 = Math.Min(Side - 1, x0 + 1);
                    double tx = sx - x0;
                    double top = raw[offset + y0 * Side + x0] * (1 - tx) + raw[offset + y0 * Side + x1] * tx;
                    double bottom = raw[offset + y1 * Side + x0] * (1 - tx) + raw[offset + y1 * Side + x1] * tx;
                    grid[i * G + j] = (float)(top * (1 - ty) + bottom * ty);
                }
            }

            var result = new ulong[Orientations];
            var turned = new float[G * G];
            for (int o = 0; o < Orientations; o++)
            {
                int turns = o & 3;
                bool mirror = o >= 4;
                for (int y = 0; y < G; y++)
                    for (int x = 0; x < G; x++)
                    {
                        int sx = mirror ? G - 1 - x : x;
                        (int ry, int rx) = turns switch
                        {
                            0 => (y, sx),
                            1 => (G - 1 - sx, y),
                            2 => (G - 1 - y, G - 1 - sx),
                            _ => (sx, G - 1 - y),
                        };
                        turned[y * G + x] = grid[ry * G + rx];
                    }
                result[o] = DHash(turned);
            }
            return result;
        }

        private static ulong DHash(float[] g)
        {
            Span<float> m = stackalloc float[8 * 9];
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 9; c++)
                {
                    float s = 0;
                    for (int y = 0; y < 9; y++)
                        for (int x = 0; x < 8; x++)
                            s += g[(r * 9 + y) * G + c * 8 + x];
                    m[r * 9 + c] = s;
                }

            ulong hash = 0;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    if (m[r * 9 + c] < m[r * 9 + c + 1]) hash |= 1UL << (r * 8 + c);
            return hash;
        }
    }

    /// <summary>Reads a video's signature with one ffmpeg process per window (not one per frame).</summary>
    internal static class VideoSignatureReader
    {
        /// <summary>Seconds between sampled frames. All videos use the same step so time offsets can be compared.</summary>
        public const double Step = 2.0;

        private const double WindowSeconds = 30;
        private const double MaxKeyframeSpacing = 3.0;
        private const double AccurateWindowSeconds = 12;
        private const int QueryFrameCount = 6;
        private static readonly TimeSpan KeyframeTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(120);

        public static async Task<VideoSig?> ReadAsync(string path, bool withCrops, CancellationToken token)
        {
            var probe = await SimilarManager.ProbeVideoAsync(path, token).ConfigureAwait(false);
            if (probe.Duration <= 0) return null;

            var times = new List<float>();
            var hashes = new List<ulong[]>();
            var crops = new List<ulong[]?>();
            var flat = new List<bool>();

            void AddFrames(byte[] raw, double start)
            {
                int frames = raw.Length / FrameHasher.FrameBytes;
                for (int i = 0; i < frames; i++)
                {
                    // A frame repeated because nothing new was decoded in that second is the same frame, not another vote.
                    if (i > 0 && raw.AsSpan(i * FrameHasher.FrameBytes, FrameHasher.FrameBytes)
                            .SequenceEqual(raw.AsSpan((i - 1) * FrameHasher.FrameBytes, FrameHasher.FrameBytes))) continue;
                    bool ok = FrameHasher.TryHash(raw, i * FrameHasher.FrameBytes, withCrops, out var orient, out var crop);
                    times.Add((float)(start + i * Step));
                    hashes.Add(ok ? orient : new ulong[FrameHasher.Orientations]);
                    crops.Add(crop);
                    flat.Add(!ok);
                }
            }

            static int CountFor(double length) => (int)Math.Floor(length / Step) + 1;

            // A short file is one window; a longer one is three (start, middle, end) so the whole file is represented.
            // An intro, an outro or a cut moves the content by seconds, so the windows of two copies overlap in part.
            bool single = probe.Duration <= 3 * WindowSeconds;
            double firstLength = single ? probe.Duration : WindowSeconds;

            // Keyframes only: no need to decode every frame. The first window shows whether that is accurate: with a
            // long GOP (ffmpeg's default is 10 s) the frame at a given second is really an older keyframe, so a copy
            // encoded with other keyframe positions would not line up.
            byte[] first = await GrabAsync(path, 0, firstLength, CountFor(firstLength), keyframesOnly: true, KeyframeTimeout, token).ConfigureAwait(false);
            int distinct = Distinct(first, first.Length / FrameHasher.FrameBytes);
            double spacing = distinct > 1 ? firstLength / (distinct - 1) : double.MaxValue;

            if (spacing <= MaxKeyframeSpacing)
            {
                AddFrames(first, 0);
                if (!single)
                    foreach (double start in new[] { (probe.Duration - WindowSeconds) / 2, probe.Duration - WindowSeconds })
                        AddFrames(await GrabAsync(path, start, WindowSeconds, CountFor(WindowSeconds), keyframesOnly: true, KeyframeTimeout, token).ConfigureAwait(false), start);
            }
            else
            {
                // Long GOP: decode a few short stretches completely instead (the GPU is used when there is one).
                var stretches = new List<(double Start, double Length)>();
                if (probe.Duration <= 36) stretches.Add((0, probe.Duration));
                else
                {
                    stretches.Add((0, AccurateWindowSeconds));
                    stretches.Add(((probe.Duration - AccurateWindowSeconds) / 2, AccurateWindowSeconds));
                    stretches.Add((probe.Duration - AccurateWindowSeconds, AccurateWindowSeconds));
                }
                foreach (var (start, length) in stretches)
                    AddFrames(await GrabAsync(path, start, length, CountFor(length), keyframesOnly: false, DecodeTimeout, token).ConfigureAwait(false), start);
            }

            int nonFlat = flat.Count(f => !f);
            if (nonFlat == 0) return null;

            var good = Enumerable.Range(0, flat.Count).Where(i => !flat[i]).ToList();
            int q = Math.Min(QueryFrameCount, good.Count);
            var query = new int[q];
            for (int k = 0; k < q; k++) query[k] = good[q == 1 ? 0 : (int)Math.Round(k * (good.Count - 1.0) / (q - 1))];

            return new VideoSig
            {
                Duration = probe.Duration,
                Width = probe.Width,
                Height = probe.Height,
                Times = times.ToArray(),
                H = hashes.ToArray(),
                Flat = flat.ToArray(),
                QueryFrames = query,
                Crop = withCrops ? query.Select(i => crops[i] ?? Array.Empty<ulong>()).ToArray() : null,
                NonFlat = nonFlat,
            };
        }

        private static Task<byte[]> GrabAsync(string path, double start, double length, int count, bool keyframesOnly, TimeSpan timeout, CancellationToken token)
        {
            var args = new List<string> { "-threads", "1" };
            if (keyframesOnly) args.AddRange(new[] { "-skip_frame", "nokey" });
            else args.AddRange(new[] { "-hwaccel", "auto" });   // falls back to software when there is no usable GPU decoder
            args.AddRange(new[]
            {
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", length.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-an", "-sn", "-dn", "-threads", "1",
                "-vf", $"fps=1/{Step.ToString(CultureInfo.InvariantCulture)},scale={FrameHasher.Side}:{FrameHasher.Side}:flags=area,format=gray",
                "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                "-f", "rawvideo", "-pix_fmt", "gray", "-",
            });
            return SimilarManager.RunForBytesAsync(SimilarManager.FfmpegPath, args, token, timeout);
        }

        private static int Distinct(byte[] raw, int frames)
        {
            int distinct = frames == 0 ? 0 : 1;
            for (int i = 1; i < frames; i++)
                if (!raw.AsSpan(i * FrameHasher.FrameBytes, FrameHasher.FrameBytes).SequenceEqual(raw.AsSpan((i - 1) * FrameHasher.FrameBytes, FrameHasher.FrameBytes)))
                    distinct++;
            return distinct;
        }
    }
}
