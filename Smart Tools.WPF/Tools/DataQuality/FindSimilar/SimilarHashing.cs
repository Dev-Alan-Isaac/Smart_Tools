using System.Numerics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// A file's fingerprint: one 64-bit hash (images) or several (video
    /// frames, audio segments), compared by average Hamming distance over
    /// the positions both sides have.
    /// </summary>
    public readonly struct Fingerprint
    {
        private readonly ulong[] _hashes;

        public Fingerprint(ulong hash) => _hashes = new[] { hash };
        public Fingerprint(ulong[] hashes) => _hashes = hashes;

        public IReadOnlyList<ulong> Hashes => _hashes ?? Array.Empty<ulong>();
        public int Count => _hashes?.Length ?? 0;

        public double DistanceTo(Fingerprint other)
        {
            int n = Math.Min(Count, other.Count);
            if (n == 0) return SimilarHashing.HashBits;

            double total = 0;
            for (int i = 0; i < n; i++)
                total += BitOperations.PopCount(_hashes[i] ^ other._hashes[i]);
            return total / n;
        }

        public override string ToString() => string.Join(",", Hashes.Select(h => h.ToString("X16")));
    }

    /// <summary>Everything clustering needs to know about one file.</summary>
    public sealed class FileFingerprint
    {
        public required Fingerprint Fingerprint { get; init; }

        /// <summary>Images: a second hash family (dHash vs pHash) that fails on different images - both must agree.</summary>
        public ulong? SecondaryHash { get; init; }

        /// <summary>Images: grayscale variance of a 16x16 grid; near 0 means a blank/flat picture.</summary>
        public double? Variance { get; init; }

        public double DurationSeconds { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
    }

    /// <summary>
    /// The pure maths behind Find Similar Files: perceptual hashes from
    /// grayscale grids, the audio fingerprint from PCM, and clustering.
    /// No file or image-library access, so it's unit tested directly.
    /// </summary>
    public static class SimilarHashing
    {
        public const int HashBits = 64;
        public const int PHashSize = 32;
        public const int WHashSize = 32;
        public const int VarianceGridSize = 16;

        // =================================================================
        //  Image hashes (input: grayscale grids, 0-255, [row, column])
        // =================================================================

        /// <summary>dHash: 9x8 grid, 8 left-vs-right comparisons per row.</summary>
        public static ulong DHash(double[,] grid9x8)
        {
            ulong hash = 0;
            int bit = 63;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    if (grid9x8[y, x] > grid9x8[y, x + 1]) hash |= 1UL << bit;
                    bit--;
                }
            }
            return hash;
        }

        /// <summary>pHash: 32x32 DCT, low-frequency 8x8 corner (minus DC) against its median.</summary>
        public static ulong PHash(double[,] grid32)
        {
            var dct = Dct2D(grid32, PHashSize);
            var coeffs = new List<double>(63);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    if (!(x == 0 && y == 0)) coeffs.Add(dct[y, x]);

            double median = Median(coeffs);
            ulong hash = 0;
            int bit = 62;
            foreach (double c in coeffs)
            {
                if (c > median) hash |= 1UL << bit;
                bit--;
            }
            return hash;
        }

        /// <summary>wHash: two Haar low-pass levels (32 -> 8) against the median.</summary>
        public static ulong WHash(double[,] grid32)
        {
            var level2 = HaarLowPass(HaarLowPass(grid32, WHashSize), WHashSize / 2);
            var values = new List<double>(64);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    values.Add(level2[y, x]);

            double median = Median(values);
            ulong hash = 0;
            int bit = 63;
            foreach (double v in values)
            {
                if (v > median) hash |= 1UL << bit;
                bit--;
            }
            return hash;
        }

        /// <summary>Population variance of a grid (flatness signal).</summary>
        public static double Variance(double[,] grid)
        {
            int h = grid.GetLength(0), w = grid.GetLength(1);
            double sum = 0, sumSq = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v = grid[y, x];
                    sum += v;
                    sumSq += v * v;
                }
            int n = w * h;
            double mean = sum / n;
            return Math.Max(0, sumSq / n - mean * mean);
        }

        private static double[,] HaarLowPass(double[,] input, int size)
        {
            int half = size / 2;
            var output = new double[half, half];
            for (int y = 0; y < half; y++)
                for (int x = 0; x < half; x++)
                    output[y, x] = (input[2 * y, 2 * x] + input[2 * y, 2 * x + 1] + input[2 * y + 1, 2 * x] + input[2 * y + 1, 2 * x + 1]) / 4.0;
            return output;
        }

        // Cosine table for the 32-point DCT - the old code called Math.Cos
        // 2 x 32^3 times per image.
        private static readonly double[,] DctTable = BuildDctTable(PHashSize);

        private static double[,] BuildDctTable(int n)
        {
            var table = new double[n, n];
            for (int k = 0; k < n; k++)
            {
                double scale = k == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n);
                for (int i = 0; i < n; i++) table[k, i] = scale * Math.Cos(Math.PI / n * (i + 0.5) * k);
            }
            return table;
        }

        /// <summary>2D DCT-II (orthonormal) via separable row and column passes.</summary>
        public static double[,] Dct2D(double[,] input, int n)
        {
            var table = n == PHashSize ? DctTable : BuildDctTable(n);
            var temp = new double[n, n];
            var output = new double[n, n];

            for (int y = 0; y < n; y++)
                for (int k = 0; k < n; k++)
                {
                    double sum = 0;
                    for (int i = 0; i < n; i++) sum += input[y, i] * table[k, i];
                    temp[y, k] = sum;
                }

            for (int x = 0; x < n; x++)
                for (int k = 0; k < n; k++)
                {
                    double sum = 0;
                    for (int i = 0; i < n; i++) sum += temp[i, x] * table[k, i];
                    output[k, x] = sum;
                }

            return output;
        }

        public static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        // =================================================================
        //  Audio fingerprint (input: mono 16-bit PCM)
        // =================================================================

        public const int AudioSampleRate = 8000;
        public const int AudioFftSize = 4096;
        public const int AudioBands = 64;
        public const int AudioSegments = 8;

        /// <summary>
        /// A simplified acoustic fingerprint - not real Chromaprint. The old
        /// version averaged the spectrum of the whole minute into ONE hash
        /// with a median cut, so two different songs using the same notes
        /// (same key, same instruments) came out nearly identical. This one
        /// keeps time and shape:
        /// - leading/trailing near-silence is trimmed, so an extra second of
        ///   silence doesn't shift everything;
        /// - the rest is cut into 8 segments, each hashed on its own, so the
        ///   songs must agree segment by segment;
        /// - each bit is "is this band clearly louder than the next one?"
        ///   across 65 log-spaced bands from 120 Hz to 3.4 kHz (where melody
        ///   and harmony live) - independent of volume, with bands lost in
        ///   the noise floor held at 0 so they don't add random bits.
        /// </summary>
        public static ulong[]? AudioFingerprint(ReadOnlySpan<short> pcm)
        {
            int windowCount = pcm.Length / AudioFftSize;
            if (windowCount == 0) return null;

            var energies = new double[windowCount][];
            var loudness = new double[windowCount];
            var buffer = new Complex[AudioFftSize];

            for (int w = 0; w < windowCount; w++)
            {
                double sumSq = 0;
                for (int i = 0; i < AudioFftSize; i++)
                {
                    double s = pcm[w * AudioFftSize + i] / 32768.0;
                    buffer[i] = new Complex(s * HannWindow[i], 0);   // less leakage between bands
                    sumSq += s * s;
                }
                loudness[w] = sumSq;
                Fft(buffer);
                energies[w] = BandEnergies(buffer);
            }

            double loudest = loudness.Max();
            if (loudest <= 1e-9) return null;
            int first = 0, last = windowCount - 1;
            while (first < last && loudness[first] < loudest * 0.01) first++;
            while (last > first && loudness[last] < loudest * 0.01) last--;
            int usable = last - first + 1;

            int segments = Math.Min(AudioSegments, usable);
            var hashes = new ulong[segments];
            for (int s = 0; s < segments; s++)
            {
                int from = first + s * usable / segments;
                int to = first + (s + 1) * usable / segments;
                var sum = new double[AudioBands + 1];
                for (int w = from; w < to; w++)
                    for (int b = 0; b <= AudioBands; b++) sum[b] += energies[w][b];

                // Log energy over a floor 30 dB under the segment's loudest
                // band: bands down in the noise all sit at the floor, so they
                // compare "equal" (bit 0) in every copy instead of flipping
                // at random with the noise.
                double floor = sum.Max() * 1e-3 + 1e-12;
                ulong hash = 0;
                for (int b = 0; b < AudioBands; b++)
                    if (Math.Log(sum[b] + floor) > Math.Log(sum[b + 1] + floor) + SlopeMargin) hash |= 1UL << (63 - b);
                hashes[s] = hash;
            }
            return hashes;
        }

        /// <summary>Minimum log-energy step (about 0.9 dB) for a "louder" bit.</summary>
        private const double SlopeMargin = 0.2;

        private const double AudioLowHz = 120;
        private const double AudioHighHz = 3400;

        private static readonly double[] HannWindow =
            Enumerable.Range(0, AudioFftSize).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (AudioFftSize - 1))).ToArray();

        /// <summary>65 log-spaced band energies between 120 Hz and 3.4 kHz.</summary>
        private static double[] BandEnergies(Complex[] fft)
        {
            double hzPerBin = (double)AudioSampleRate / fft.Length;
            var energies = new double[AudioBands + 1];
            double logLo = Math.Log(AudioLowHz), logHi = Math.Log(AudioHighHz);

            for (int b = 0; b <= AudioBands; b++)
            {
                double f0 = Math.Exp(logLo + (logHi - logLo) * b / (AudioBands + 1));
                double f1 = Math.Exp(logLo + (logHi - logLo) * (b + 1) / (AudioBands + 1));
                int lo = (int)(f0 / hzPerBin);
                int hi = Math.Max(lo + 1, (int)(f1 / hzPerBin));
                double sum = 0;
                for (int k = lo; k < hi; k++) sum += fft[k].Magnitude * fft[k].Magnitude;
                energies[b] = sum / (hi - lo);
            }
            return energies;
        }

        /// <summary>In-place iterative radix-2 FFT; length must be a power of two.</summary>
        public static void Fft(Complex[] buffer)
        {
            int n = buffer.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (int j = 0; j < len / 2; j++)
                    {
                        var u = buffer[i + j];
                        var v = buffer[i + j + len / 2] * w;
                        buffer[i + j] = u + v;
                        buffer[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }
        }

        // =================================================================
        //  Clustering
        // =================================================================

        // Gates scale with the one threshold slider: loose end at the most
        // permissive setting, tight end when files must be nearly identical.
        private const double DurationRatioLimitLoose = 2.5;
        private const double DurationRatioLimitTight = 1.1;
        private const double AspectRatioToleranceLoose = 0.35;
        private const double AspectRatioToleranceTight = 0.05;
        private const double SecondaryLooseningFactorLoose = 2.0;
        private const double SecondaryLooseningFactorTight = 1.2;

        /// <summary>Near-blank images collide easily, so they need a closer match.</summary>
        public const double FlatVarianceThreshold = 10.0;
        private const double FlatDistanceTighteningFactor = 0.5;

        public static double MaxDistanceFor(double thresholdPercent) => (100.0 - thresholdPercent) / 100.0 * HashBits;

        private static double Scale(double strictness, double loose, double tight) => loose + Math.Clamp(strictness, 0, 1) * (tight - loose);

        /// <summary>
        /// Average-linkage greedy clustering: each file (in the given order)
        /// joins the compatible cluster with the smallest average distance
        /// within tolerance, else starts a new one. Same rules and results
        /// as the old version (checked against a port of it in the tests),
        /// now on plain arrays instead of dictionaries keyed by FileInfo,
        /// and cancellable.
        /// </summary>
        /// <returns>Clusters as lists of indexes into <paramref name="items"/>.</returns>
        public static List<List<int>> Cluster(IReadOnlyList<FileFingerprint> items, double maxDistance, CancellationToken token = default)
        {
            double strictness = 1.0 - maxDistance / HashBits;
            double durationLimit = Scale(strictness, DurationRatioLimitLoose, DurationRatioLimitTight);
            double aspectTolerance = Scale(strictness, AspectRatioToleranceLoose, AspectRatioToleranceTight);
            double secondaryFactor = Scale(strictness, SecondaryLooseningFactorLoose, SecondaryLooseningFactorTight);

            var clusters = new List<List<int>>();

            for (int index = 0; index < items.Count; index++)
            {
                if ((index & 255) == 0) token.ThrowIfCancellationRequested();
                var candidate = items[index];

                int best = -1;
                double bestDistance = double.MaxValue;
                for (int c = 0; c < clusters.Count; c++)
                {
                    var cluster = clusters[c];
                    if (!Compatible(candidate, cluster, items, maxDistance, durationLimit, aspectTolerance, secondaryFactor)) continue;

                    double effectiveMax = IsFlat(candidate) || cluster.Any(m => IsFlat(items[m]))
                        ? maxDistance * FlatDistanceTighteningFactor
                        : maxDistance;

                    double sum = 0;
                    foreach (int m in cluster) sum += items[m].Fingerprint.DistanceTo(candidate.Fingerprint);
                    double d = sum / cluster.Count;

                    if (d <= effectiveMax && d < bestDistance)
                    {
                        bestDistance = d;
                        best = c;
                    }
                }

                if (best >= 0) clusters[best].Add(index);
                else clusters.Add(new List<int> { index });
            }

            return clusters;
        }

        private static bool IsFlat(FileFingerprint f) => f.Variance is double v && v < FlatVarianceThreshold;

        private static bool Compatible(FileFingerprint candidate, List<int> cluster, IReadOnlyList<FileFingerprint> items,
            double maxDistance, double durationLimit, double aspectTolerance, double secondaryFactor)
        {
            foreach (int m in cluster)
            {
                var member = items[m];

                if (candidate.DurationSeconds > 0 && member.DurationSeconds > 0)
                {
                    double ratio = Math.Max(candidate.DurationSeconds, member.DurationSeconds) / Math.Min(candidate.DurationSeconds, member.DurationSeconds);
                    if (ratio > durationLimit) return false;
                }

                if (candidate.Width > 0 && candidate.Height > 0 && member.Width > 0 && member.Height > 0)
                {
                    double ra = (double)candidate.Width / candidate.Height;
                    double rb = (double)member.Width / member.Height;
                    if (Math.Abs(ra - rb) / Math.Max(ra, rb) > aspectTolerance) return false;
                }

                if (candidate.SecondaryHash is ulong ha && member.SecondaryHash is ulong hb)
                {
                    int distance = BitOperations.PopCount(ha ^ hb);
                    if (distance > Math.Min(HashBits, maxDistance * secondaryFactor)) return false;
                }
            }
            return true;
        }

        /// <summary>Average pairwise distance -> "85% alike".</summary>
        public static int SimilarityPercent(IReadOnlyList<Fingerprint> members)
        {
            if (members.Count < 2) return 100;
            double total = 0;
            int pairs = 0;
            for (int i = 0; i < members.Count; i++)
                for (int j = i + 1; j < members.Count; j++)
                {
                    total += members[i].DistanceTo(members[j]);
                    pairs++;
                }
            int percent = (int)Math.Round(100.0 * (1 - total / pairs / HashBits));
            return Math.Clamp(percent, 0, 100);
        }
    }
}
