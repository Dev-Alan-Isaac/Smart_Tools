using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Image clean-up for hard pictures (blurry, low contrast, light text on dark) and the score that
    /// decides whether a reading is worth keeping. Pure pixels and strings: no WPF, no Windows Runtime.
    /// </summary>
    public static partial class ScreenOcrManager
    {
        /// <summary>The language tag that means "try every installed OCR language and keep the best".</summary>
        public const string AutoLanguageTag = "auto";

        // =================================================================
        //  Clean-up
        // =================================================================

        /// <summary>
        /// Grayscale, contrast stretched between the 2nd and 98th brightness percentile, flipped when the
        /// picture is mostly dark (so text is always dark on light), then sharpened.
        /// </summary>
        public static ScreenOcrImage Enhance(ScreenOcrImage source)
        {
            byte[] gray = ToGray(source);
            Stretch(gray);
            if (MeanOf(gray) < 110)
                for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(255 - gray[i]);
            return FromGray(Sharpen(gray, source.Width, source.Height), source.Width, source.Height);
        }

        /// <summary>A 3x3 median filter: removes speckle and JPEG noise while keeping the edges of letters.</summary>
        public static ScreenOcrImage Denoise(ScreenOcrImage source)
        {
            byte[] gray = ToGray(source);
            int w = source.Width, h = source.Height;
            var result = new byte[gray.Length];
            Span<byte> window = stackalloc byte[9];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            window[n++] = gray[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
                    window.Sort();
                    result[y * w + x] = window[4];
                }
            }
            return FromGray(result, w, h);
        }

        private static byte[] ToGray(ScreenOcrImage image)
        {
            byte[] p = image.Pixels;
            var gray = new byte[image.Width * image.Height];
            for (int i = 0, j = 0; j < gray.Length; i += 4, j++)
                gray[j] = (byte)((p[i] * 114 + p[i + 1] * 587 + p[i + 2] * 299) / 1000);   // BGRA
            return gray;
        }

        private static ScreenOcrImage FromGray(byte[] gray, int width, int height)
        {
            var pixels = new byte[gray.Length * 4];
            for (int i = 0, j = 0; j < gray.Length; i += 4, j++)
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = gray[j];
                pixels[i + 3] = 255;
            }
            return new ScreenOcrImage(pixels, width, height);
        }

        private static double MeanOf(byte[] gray)
        {
            if (gray.Length == 0) return 255;
            long sum = 0;
            foreach (byte b in gray) sum += b;
            return (double)sum / gray.Length;
        }

        /// <summary>Spreads the 2nd..98th percentile of brightness over 0..255. Flat pictures are left alone.</summary>
        private static void Stretch(byte[] gray)
        {
            if (gray.Length == 0) return;
            var histogram = new int[256];
            foreach (byte b in gray) histogram[b]++;

            int low = Percentile(histogram, gray.Length, 0.02), high = Percentile(histogram, gray.Length, 0.98);
            if (high - low < 16) return;

            for (int i = 0; i < gray.Length; i++)
                gray[i] = (byte)Math.Clamp((gray[i] - low) * 255 / (high - low), 0, 255);
        }

        private static int Percentile(int[] histogram, int total, double fraction)
        {
            long target = (long)(total * fraction), seen = 0;
            for (int v = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (seen > target) return v;
            }
            return 255;
        }

        /// <summary>Unsharp mask: the pixel plus 0.8x its difference from the 3x3 average.</summary>
        private static byte[] Sharpen(byte[] gray, int width, int height)
        {
            var result = new byte[gray.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            sum += gray[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)];
                    double center = gray[y * width + x];
                    result[y * width + x] = (byte)Math.Clamp(center + 0.8 * (center - sum / 9.0), 0, 255);
                }
            }
            return result;
        }

        // =================================================================
        //  Is this reading any good?
        // =================================================================

        /// <summary>
        /// How much real text a reading found: words made mostly of letters and digits count for one,
        /// stray symbols and lone punctuation (what OCR makes of a blur or the wrong language) cost half.
        /// <paramref name="isGood"/> is true when it is at least a word and nearly all of it is plausible.
        /// </summary>
        public static double ReadScore(IReadOnlyList<string> lines, out bool isGood)
        {
            int plausible = 0, junk = 0;
            foreach (string line in lines)
            {
                foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int letters = token.Count(char.IsLetterOrDigit);
                    bool real = letters >= 1 && letters >= token.Length * 0.6;
                    if (real) plausible++;
                    else junk++;
                }
            }

            int total = plausible + junk;
            isGood = plausible >= 1 && plausible >= total * 0.85;
            return plausible - junk * 0.5;
        }
    }
}
