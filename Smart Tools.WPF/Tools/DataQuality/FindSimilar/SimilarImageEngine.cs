using ImageMagick;
using OpenCvSharp;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Images with OpenCV. Each picture gets (1) pHash of the picture in 4 rotations x mirrored, so a
    /// flipped or turned copy still hashes close to the original, and (2) ORB features of the picture
    /// and its mirror, so a cropped, edited or re-framed copy can be confirmed by matching points and
    /// fitting a homography (RANSAC). Different resolution and file format don't matter: everything is
    /// computed on a normalised grayscale copy.
    /// </summary>
    internal static class SimilarImageEngine
    {
        private const int MaxSide = 512;
        private const int OrbFeatures = 300;
        private const long OpenCvDecodeLimit = 256L * 1024 * 1024;
        private const double RatioTest = 0.75;
        private const double MinInlierShare = 0.5;

        // =================================================================
        //  Signature
        // =================================================================

        public static MediaSignature? Compute(string path)
        {
            try
            {
                var (width, height) = ReadSize(path);

                using var gray = DecodeGray(path, Math.Max(width, height));
                if (gray == null || gray.Empty()) return null;
                if (width <= 0 || height <= 0) (width, height) = (gray.Width, gray.Height);

                var sig = new MediaSignature { Kind = MediaKind.Image, Width = width, Height = height };

                // 8 hashes: [rotation * 2 + mirrored]
                var hashes = new ulong[8];
                using (var turned = gray.Clone())
                {
                    Mat current = turned;
                    var owned = new List<Mat>();
                    try
                    {
                        for (int rot = 0; rot < 4; rot++)
                        {
                            if (rot > 0)
                            {
                                var next = new Mat();
                                Cv2.Rotate(current, next, RotateFlags.Rotate90Clockwise);
                                owned.Add(next);
                                current = next;
                            }
                            var (hash, variance) = HashOf(current);
                            hashes[rot * 2] = hash;
                            if (rot == 0) sig.Variance = variance;

                            using var mirrored = new Mat();
                            Cv2.Flip(current, mirrored, FlipMode.Y);
                            hashes[rot * 2 + 1] = HashOf(mirrored).Hash;
                        }
                    }
                    finally
                    {
                        foreach (var m in owned) m.Dispose();
                    }
                }
                sig.ImageHashes = hashes;

                (sig.Orb, sig.OrbPoints) = Orb(gray);
                using (var mirror = new Mat())
                {
                    Cv2.Flip(gray, mirror, FlipMode.Y);
                    (sig.OrbMirror, sig.OrbMirrorPoints) = Orb(mirror);
                }
                return sig;
            }
            catch (Exception ex) when (ex is OpenCVException or MagickException or IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
            {
                return null;
            }
        }

        /// <summary>pHash (8x8 DCT corner against its median) and the flatness variance of a gray picture.</summary>
        internal static (ulong Hash, double Variance) HashOf(Mat gray)
        {
            using var small = new Mat();
            Cv2.Resize(gray, small, new OpenCvSharp.Size(SimilarHashing.PHashSize, SimilarHashing.PHashSize), 0, 0, InterpolationFlags.Area);
            var grid = new double[SimilarHashing.PHashSize, SimilarHashing.PHashSize];
            for (int y = 0; y < SimilarHashing.PHashSize; y++)
                for (int x = 0; x < SimilarHashing.PHashSize; x++)
                    grid[y, x] = small.At<byte>(y, x);
            return (SimilarHashing.PHash(grid), SimilarHashing.Variance(grid));
        }

        private static (byte[] Descriptors, float[] Points) Orb(Mat gray)
        {
            using var orb = ORB.Create(OrbFeatures);
            using var descriptors = new Mat();
            orb.DetectAndCompute(gray, null, out KeyPoint[] keyPoints, descriptors);
            if (descriptors.Empty() || keyPoints.Length == 0) return (Array.Empty<byte>(), Array.Empty<float>());

            int rows = descriptors.Rows;
            if (descriptors.Cols != MediaSignature.OrbBytesPerRow) return (Array.Empty<byte>(), Array.Empty<float>());

            var bytes = new byte[rows * MediaSignature.OrbBytesPerRow];
            using var continuous = descriptors.Clone();   // a clone is always one continuous block
            Marshal.Copy(continuous.Data, bytes, 0, bytes.Length);

            rows = Math.Min(rows, keyPoints.Length);
            var points = new float[rows * 2];
            for (int i = 0; i < rows; i++)
            {
                points[i * 2] = keyPoints[i].Pt.X;
                points[i * 2 + 1] = keyPoints[i].Pt.Y;
            }
            if (rows * MediaSignature.OrbBytesPerRow != bytes.Length)
                Array.Resize(ref bytes, rows * MediaSignature.OrbBytesPerRow);
            return (bytes, points);
        }

        // =================================================================
        //  Decoding
        // =================================================================

        /// <summary>True size from the header (corrected for EXIF rotation) without decoding the pixels.</summary>
        private static (int Width, int Height) ReadSize(string path)
        {
            try
            {
                var info = new MagickImageInfo(path);
                int w = (int)info.Width, h = (int)info.Height;
                // EXIF orientations 5-8 turn the picture a quarter; matched by name so a renamed enum can't break the build.
                if (info.Orientation.ToString() is "LeftTop" or "RightTop" or "RightBottom" or "LeftBottom")
                    (w, h) = (h, w);
                return (w, h);
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// OpenCV decodes straight from bytes (its own file open mishandles non-ASCII Windows paths) and,
        /// for big pictures, at a reduced size. Formats it can't read (HEIC, AVIF, GIF, RAW...) go through Magick.NET.
        /// </summary>
        private static Mat? DecodeGray(string path, int longSide)
        {
            Mat? mat = null;
            try
            {
                if (new FileInfo(path).Length <= OpenCvDecodeLimit)
                {
                    var flag = longSide >= 4096 * 2 ? ImreadModes.ReducedGrayscale8
                        : longSide >= 2048 * 2 ? ImreadModes.ReducedGrayscale4
                        : longSide >= 1024 * 2 ? ImreadModes.ReducedGrayscale2
                        : ImreadModes.Grayscale;
                    mat = Cv2.ImDecode(File.ReadAllBytes(path), flag);
                }
            }
            catch (Exception ex) when (ex is OpenCVException or IOException or OutOfMemoryException)
            {
                mat?.Dispose();
                mat = null;
            }

            if (mat == null || mat.Empty())
            {
                mat?.Dispose();
                mat = DecodeWithMagick(path);
            }
            return mat == null ? null : LimitSide(mat);
        }

        private static Mat? DecodeWithMagick(string path)
        {
            try
            {
                var settings = new MagickReadSettings { Width = MaxSide * 2, Height = MaxSide * 2 };
                using var image = new MagickImage(path, settings);
                image.AutoOrient();
                image.ColorSpace = ColorSpace.Gray;
                image.Resize(new MagickGeometry(MaxSide, MaxSide));
                byte[] png = image.ToByteArray(MagickFormat.Png);
                return Cv2.ImDecode(png, ImreadModes.Grayscale);
            }
            catch (Exception ex) when (ex is MagickException or OpenCVException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static Mat LimitSide(Mat mat)
        {
            int longest = Math.Max(mat.Width, mat.Height);
            if (longest <= MaxSide) return mat;
            double scale = (double)MaxSide / longest;
            var smaller = new Mat();
            Cv2.Resize(mat, smaller, new OpenCvSharp.Size(Math.Max(1, (int)Math.Round(mat.Width * scale)), Math.Max(1, (int)Math.Round(mat.Height * scale))), 0, 0, InterpolationFlags.Area);
            mat.Dispose();
            return smaller;
        }

        // =================================================================
        //  Matching
        // =================================================================

        /// <summary>Smallest bit distance between <paramref name="a"/> (any rotation/mirror) and <paramref name="b"/> as it is.</summary>
        public static (int Distance, int Variant) BestHashDistance(MediaSignature a, MediaSignature b)
        {
            int best = int.MaxValue, bestVariant = 0;
            ulong target = b.ImageHashes[0];
            for (int v = 0; v < 8; v++)
            {
                int d = System.Numerics.BitOperations.PopCount(a.ImageHashes[v] ^ target);
                if (d < best)
                {
                    best = d;
                    bestVariant = v;
                }
            }
            return (best, bestVariant);
        }

        /// <summary>
        /// Confirms two pictures show the same scene even when cropped or edited: matches ORB points
        /// (ratio test), fits a homography with RANSAC and counts the inliers. Tries the mirrored picture too.
        /// Returns 0 when there is no believable fit.
        /// </summary>
        public static int OrbInliers(MediaSignature a, MediaSignature b, out bool mirrored)
        {
            mirrored = false;
            int plain = Inliers(a.Orb, a.OrbPoints, b.Orb, b.OrbPoints);
            int flipped = Inliers(a.OrbMirror, a.OrbMirrorPoints, b.Orb, b.OrbPoints);
            if (flipped > plain)
            {
                mirrored = true;
                return flipped;
            }
            return plain;
        }

        private static int Inliers(byte[] da, float[] pa, byte[] db, float[] pb)
        {
            int rowsA = da.Length / MediaSignature.OrbBytesPerRow, rowsB = db.Length / MediaSignature.OrbBytesPerRow;
            if (rowsA < 12 || rowsB < 12) return 0;

            using var ma = ToMat(da, rowsA);
            using var mb = ToMat(db, rowsB);
            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            DMatch[][] knn = matcher.KnnMatch(ma, mb, 2);

            var src = new List<Point2d>();
            var dst = new List<Point2d>();
            foreach (var pair in knn)
            {
                if (pair.Length < 2 || pair[0].Distance >= RatioTest * pair[1].Distance) continue;
                src.Add(new Point2d(pa[pair[0].QueryIdx * 2], pa[pair[0].QueryIdx * 2 + 1]));
                dst.Add(new Point2d(pb[pair[0].TrainIdx * 2], pb[pair[0].TrainIdx * 2 + 1]));
            }
            if (src.Count < 10) return 0;

            using var mask = new Mat();
            using var h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 3.0, mask);
            if (h == null || h.Empty() || mask.Empty()) return 0;

            // A believable photo-to-photo fit: a plain scale/shift (crop, resize), not a collapse to a line,
            // a huge stretch or strong perspective, which is what unrelated look-alike patterns produce.
            double a = h.At<double>(0, 0), b = h.At<double>(0, 1), c = h.At<double>(1, 0), d = h.At<double>(1, 1);
            double det = a * d - b * c;
            if (!double.IsFinite(det) || Math.Abs(det) < 0.1 || Math.Abs(det) > 10) return 0;
            if (Math.Abs(h.At<double>(2, 0)) > 0.001 || Math.Abs(h.At<double>(2, 1)) > 0.001) return 0;

            int inliers = Cv2.CountNonZero(mask);
            // Most of the matches must agree on the one fit; a handful of lucky ones among noise is not a copy.
            return inliers >= MinInlierShare * src.Count ? inliers : 0;
        }

        private static Mat ToMat(byte[] data, int rows)
        {
            var m = new Mat(rows, MediaSignature.OrbBytesPerRow, MatType.CV_8UC1);
            Marshal.Copy(data, 0, m.Data, rows * MediaSignature.OrbBytesPerRow);
            return m;
        }
    }
}
