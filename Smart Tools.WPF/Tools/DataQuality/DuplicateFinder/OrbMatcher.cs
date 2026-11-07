using OpenCvSharp;
using System.IO;
using System.Runtime.InteropServices;
using IM = ImageMagick;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// ORB keypoints (OpenCvSharp) for pictures that perceptual hashes miss: heavy crops, rotation, overlays.
    /// Pictures are decoded by Magick.NET (so HEIC, WebP, RAW... work), reduced, and handed to OpenCV as gray bytes.
    /// </summary>
    internal sealed class OrbSignature : IDisposable
    {
        private const int MaxSide = 512;
        private const int Features = 500;
        private const float RatioTest = 0.75f;

        /// <summary>The guide's rule of thumb: this many good keypoint matches means the same picture.</summary>
        public const int MinGoodMatches = 30;

        private readonly Mat _descriptors;
        public int Count { get; }

        private OrbSignature(Mat descriptors, int count)
        {
            _descriptors = descriptors;
            Count = count;
        }

        /// <summary>Null when the picture can't be read or has too little detail to describe.</summary>
        public static OrbSignature? Compute(string path, CancellationToken token)
        {
            try
            {
                byte[] gray;
                int width, height;
                using (var loaded = ImageLoad.Open(path, token, MaxSide))
                {
                    var image = loaded.Image;
                    image.AutoOrient();
                    image.Resize(new IM.MagickGeometry(MaxSide, MaxSide));
                    image.ColorSpace = IM.ColorSpace.Gray;
                    image.Depth = 8;
                    width = (int)image.Width;
                    height = (int)image.Height;
                    gray = image.ToByteArray(IM.MagickFormat.Gray);
                }
                if (gray.Length < width * height || width < 32 || height < 32) return null;

                using var mat = new Mat(height, width, MatType.CV_8UC1);
                Marshal.Copy(gray, 0, mat.Data, width * height);

                using var orb = ORB.Create(Features);
                var descriptors = new Mat();
                orb.DetectAndCompute(mat, null, out KeyPoint[] keypoints, descriptors);
                if (keypoints.Length < MinGoodMatches || descriptors.Empty())
                {
                    descriptors.Dispose();
                    return null;
                }
                return new OrbSignature(descriptors, keypoints.Length);
            }
            catch (Exception ex) when (ex is IM.MagickException or IOException or UnauthorizedAccessException or OpenCVException or ArgumentException or DllNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Good matches after Lowe's ratio test. Safe to call from several threads on different pairs.</summary>
        public static int GoodMatches(OrbSignature a, OrbSignature b)
        {
            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            DMatch[][] pairs = matcher.KnnMatch(a._descriptors, b._descriptors, 2);
            int good = 0;
            foreach (var pair in pairs)
                if (pair.Length == 2 && pair[0].Distance < RatioTest * pair[1].Distance) good++;
            return good;
        }

        public void Dispose() => _descriptors.Dispose();
    }
}
