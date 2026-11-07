using OpenCvSharp;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Numerics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Videos with OpenCV. A video becomes a row of perceptual hashes, one per sampled frame (plus the
    /// mirrored frame). Two videos match when some time offset lines up enough frames: a trimmed start or
    /// end, a different container/codec/bitrate/resolution, or a mirrored copy all still align.
    /// </summary>
    internal static class SimilarVideoEngine
    {
        public const int MaxSamples = 240;
        private const double FlatVariance = 10.0;
        private static readonly TimeSpan SamplingBudget = TimeSpan.FromSeconds(90);

        /// <summary>Seconds between samples for a video of this length: 1 s up to 4 minutes, then a whole number of seconds.</summary>
        public static int StepFor(double durationSeconds) => Math.Max(1, (int)Math.Ceiling(durationSeconds / MaxSamples));

        // =================================================================
        //  Sampling
        // =================================================================

        /// <summary>
        /// Reads the video with OpenCV. Null when OpenCV can't open it or gets too few frames
        /// (the caller then falls back to ffmpeg).
        /// </summary>
        public static MediaSignature? Sample(string path, CancellationToken token)
        {
            try
            {
                using var capture = new VideoCapture(path, VideoCaptureAPIs.ANY);
                if (!capture.IsOpened()) return null;

                double fps = capture.Get(VideoCaptureProperties.Fps);
                double count = capture.Get(VideoCaptureProperties.FrameCount);
                if (fps < 0.1 || count < 1 || !double.IsFinite(fps) || !double.IsFinite(count)) return null;
                double duration = count / fps;
                if (duration <= 0) return null;

                int step = StepFor(duration);
                var frames = new List<ulong>();
                var mirrored = new List<ulong>();
                int failed = 0;
                var watch = Stopwatch.StartNew();

                using var frame = new Mat();
                using var gray = new Mat();
                using var flip = new Mat();
                for (int t = 0; t < duration; t += step)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed > SamplingBudget && frames.Count >= 10) break;   // very slow codec: use what we have

                    capture.Set(VideoCaptureProperties.PosMsec, t * 1000.0);
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        failed++;
                        frames.Add(MediaSignature.FlatFrame);
                        mirrored.Add(MediaSignature.FlatFrame);
                        continue;
                    }

                    if (frame.Channels() == 1) frame.CopyTo(gray);
                    else Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

                    var (hash, variance) = SimilarImageEngine.HashOf(gray);
                    if (variance < FlatVariance)
                    {
                        frames.Add(MediaSignature.FlatFrame);
                        mirrored.Add(MediaSignature.FlatFrame);
                        continue;
                    }
                    frames.Add(hash);
                    Cv2.Flip(gray, flip, FlipMode.Y);
                    mirrored.Add(SimilarImageEngine.HashOf(flip).Hash);
                }

                // Mostly unreadable: let ffmpeg try instead.
                if (frames.Count == 0 || failed * 2 > frames.Count) return null;

                return new MediaSignature
                {
                    Kind = MediaKind.Video,
                    DurationSeconds = duration,
                    Width = (int)capture.Get(VideoCaptureProperties.FrameWidth),
                    Height = (int)capture.Get(VideoCaptureProperties.FrameHeight),
                    VideoStep = step,
                    Frames = frames.ToArray(),
                    FramesMirror = mirrored.ToArray(),
                };
            }
            catch (Exception ex) when (ex is OpenCVException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Builds the signature from raw 32x32 gray frames that ffmpeg extracted
        /// (<c>fps=1/step,scale=32:32,format=gray</c>): the fallback for codecs OpenCV can't open.
        /// </summary>
        public static MediaSignature? FromRawFrames(byte[] raw, double duration, int width, int height, int step)
        {
            const int side = SimilarHashing.PHashSize;
            int frameBytes = side * side;
            int count = raw.Length / frameBytes;
            if (count == 0) return null;

            var frames = new ulong[count];
            var mirrored = new ulong[count];
            var grid = new double[side, side];
            var flipped = new double[side, side];
            for (int f = 0; f < count; f++)
            {
                int offset = f * frameBytes;
                for (int y = 0; y < side; y++)
                    for (int x = 0; x < side; x++)
                    {
                        double v = raw[offset + y * side + x];
                        grid[y, x] = v;
                        flipped[y, side - 1 - x] = v;
                    }

                if (SimilarHashing.Variance(grid) < FlatVariance)
                {
                    frames[f] = mirrored[f] = MediaSignature.FlatFrame;
                    continue;
                }
                frames[f] = SimilarHashing.PHash(grid);
                mirrored[f] = SimilarHashing.PHash(flipped);
            }

            return new MediaSignature
            {
                Kind = MediaKind.Video,
                DurationSeconds = duration,
                Width = width,
                Height = height,
                VideoStep = step,
                Frames = frames,
                FramesMirror = mirrored,
            };
        }

        // =================================================================
        //  Matching
        // =================================================================

        /// <summary>Result of lining two videos up.</summary>
        public readonly record struct Alignment(double Coverage, double MeanDistance, bool Mirrored, double OffsetSeconds, bool Matched);

        /// <summary>
        /// Best alignment of two videos: slides the shorter row of frame hashes over the longer one and counts
        /// frames whose hashes agree (flat frames never count). Coverage is the share of the shorter video's
        /// usable frames that found a partner. Rows sampled at different steps are compared at the coarser step,
        /// trying every phase of the finer one.
        /// </summary>
        public static Alignment Align(MediaSignature a, MediaSignature b, int frameTolerance)
        {
            if (a.VideoStep <= 0 || b.VideoStep <= 0) return default;

            int step = Math.Max(a.VideoStep, b.VideoStep);
            int ka = step / a.VideoStep, kb = step / b.VideoStep;
            if (ka * a.VideoStep != step || kb * b.VideoStep != step) return default;   // steps not multiples of each other

            Alignment best = default;
            for (int pa = 0; pa < ka; pa++)
            {
                var rowA = Decimate(a.Frames, pa, ka);
                var rowAMirror = Decimate(a.FramesMirror, pa, ka);
                for (int pb = 0; pb < kb; pb++)
                {
                    var rowB = Decimate(b.Frames, pb, kb);
                    var plain = Slide(rowA, rowB, frameTolerance);
                    var flipped = Slide(rowAMirror, rowB, frameTolerance);

                    foreach (var (cand, mirror) in new[] { (plain, false), (flipped, true) })
                    {
                        if (cand.Coverage <= best.Coverage) continue;
                        best = cand with { Mirrored = mirror, OffsetSeconds = cand.OffsetSeconds * step };
                    }
                }
            }
            return best;
        }

        private static ulong[] Decimate(ulong[] row, int phase, int every)
        {
            if (every == 1) return row;
            var result = new List<ulong>(row.Length / every + 1);
            for (int i = phase; i < row.Length; i += every) result.Add(row[i]);
            return result.ToArray();
        }

        private static Alignment Slide(ulong[] x, ulong[] y, int tolerance)
        {
            int usableX = x.Count(h => h != MediaSignature.FlatFrame);
            int usableY = y.Count(h => h != MediaSignature.FlatFrame);
            int shorterUsable = Math.Min(usableX, usableY);
            if (shorterUsable < 3) return default;

            double bestCoverage = 0, bestMean = 0, bestOffset = 0;
            // offset o: x[i] pairs with y[i + o]
            for (int o = -(x.Length - 1); o < y.Length; o++)
            {
                int from = Math.Max(0, -o), to = Math.Min(x.Length, y.Length - o);
                if (to - from < 3) continue;

                int matched = 0;
                long distance = 0;
                for (int i = from; i < to; i++)
                {
                    ulong hx = x[i], hy = y[i + o];
                    if (hx == MediaSignature.FlatFrame || hy == MediaSignature.FlatFrame) continue;
                    int d = BitOperations.PopCount(hx ^ hy);
                    if (d <= tolerance)
                    {
                        matched++;
                        distance += d;
                    }
                }

                double coverage = (double)matched / shorterUsable;
                if (matched >= 3 && coverage > bestCoverage)
                {
                    bestCoverage = coverage;
                    bestMean = (double)distance / matched;
                    bestOffset = o;
                }
            }
            return new Alignment(bestCoverage, bestMean, false, bestOffset, bestCoverage > 0);
        }
    }
}
