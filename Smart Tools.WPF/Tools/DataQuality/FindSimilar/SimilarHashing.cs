namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The pure maths behind Find Similar Files' image and video hashes: a perceptual hash (pHash) from a
    /// 32x32 grayscale grid, and a flatness measure. No file or image-library access.
    /// (Grouping lives in <see cref="SimilarMatcher"/>; audio uses SoundFingerprinting.)
    /// </summary>
    public static class SimilarHashing
    {
        public const int HashBits = 64;
        public const int PHashSize = 32;

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

        /// <summary>Population variance of a grid (flatness signal: near 0 means a blank picture).</summary>
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

        // Cosine table for the 32-point DCT, built once.
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
    }
}
