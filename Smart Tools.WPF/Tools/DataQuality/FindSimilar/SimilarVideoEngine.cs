using OpenCvSharp;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Videos with OpenCV. A video becomes a row of frame descriptions, one per sampled frame. Two videos match
    /// when some time offset lines up enough frames, so a trimmed start or end, another container/codec/bitrate/
    /// resolution and colour changes all still align. Each frame is described to survive more than that:
    /// <list type="bullet">
    /// <item>black bars (letterbox) are trimmed before hashing;</item>
    /// <item>the mirrored frame and two centre crops (85%, 70%, mirrored too) are hashed, so a flipped or cropped copy
    /// matches (compared in both directions: A as a crop of B, or B as a crop of A);</item>
    /// <item>four quadrant hashes let a frame still match when a logo, subtitle or overlay spoils one corner.</item>
    /// </list>
    /// Not handled: speed changes (time-stretched copies) and cuts re-ordered inside the video.
    /// </summary>
    internal static class SimilarVideoEngine
    {
        public const int MaxSamples = 240;
        private const double FlatVariance = 10.0;
        private const double BarLevel = 18;          // average brightness (0-255) under which a border row/column is a bar
        private static readonly TimeSpan SamplingBudget = TimeSpan.FromSeconds(90);

        /// <summary>Seconds between samples for a video of this length: 1 s up to 4 minutes, then a whole number of seconds.</summary>
        public static int StepFor(double durationSeconds) => Math.Max(1, (int)Math.Ceiling(durationSeconds / MaxSamples));

        // =================================================================
        //  One frame
        // =================================================================

        /// <summary>Crop hashes per frame: 0-8 = 70% windows on a 3x3 grid (4 is the centre), 9 = centre 85%, 10 = mirrored centre 85%, 11 = mirrored centre 70%.</summary>
        public const int ZoomSlots = 12;

        private readonly record struct FrameSet(ulong Full, ulong Mirror, ulong[] Zoom, ulong[] Quads)
        {
            public bool Flat => Full == MediaSignature.FlatFrame;
            public static FrameSet FlatSet => new(MediaSignature.FlatFrame, MediaSignature.FlatFrame,
                Enumerable.Repeat(MediaSignature.FlatFrame, ZoomSlots).ToArray(), new ulong[8]);
        }

        private static FrameSet DescribeFrame(Mat gray)
        {
            using var content = TrimBars(gray);
            var (full, variance) = SimilarImageEngine.HashOf(content);
            if (variance < FlatVariance) return FrameSet.FlatSet;

            using var mirrored = new Mat();
            Cv2.Flip(content, mirrored, FlipMode.Y);

            var zoom = new ulong[ZoomSlots];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    zoom[row * 3 + col] = HashWindow(content, 0.70, col * 0.15, row * 0.15);
            zoom[9] = HashWindow(content, 0.85, 0.075, 0.075);
            zoom[10] = HashWindow(mirrored, 0.85, 0.075, 0.075);
            zoom[11] = HashWindow(mirrored, 0.70, 0.15, 0.15);

            var quads = new ulong[8];
            for (int q = 0; q < 4; q++)
            {
                quads[q] = QuadrantHash(content, q);
                quads[4 + q] = QuadrantHash(mirrored, q);
            }
            return new FrameSet(full, SimilarImageEngine.HashOf(mirrored).Hash, zoom, quads);
        }

        /// <summary>The picture without black bars at its edges (at most a third trimmed per side); a copy of the input if there are none.</summary>
        private static Mat TrimBars(Mat gray)
        {
            int w = gray.Width, h = gray.Height;
            if (w < 64 || h < 64) return gray.Clone();

            using var small = new Mat();
            Cv2.Resize(gray, small, new OpenCvSharp.Size(64, 64), 0, 0, InterpolationFlags.Area);
            var px = new byte[64 * 64];
            Marshal.Copy(small.Data, px, 0, px.Length);

            double Row(int y) { double s = 0; for (int x = 0; x < 64; x++) s += px[y * 64 + x]; return s / 64; }
            double Col(int x) { double s = 0; for (int y = 0; y < 64; y++) s += px[y * 64 + x]; return s / 64; }

            int top = 0, bottom = 0, left = 0, right = 0;
            while (top < 21 && Row(top) < BarLevel) top++;
            while (bottom < 21 && Row(63 - bottom) < BarLevel) bottom++;
            while (left < 21 && Col(left) < BarLevel) left++;
            while (right < 21 && Col(63 - right) < BarLevel) right++;

            if (top + bottom < 2 && left + right < 2) return gray.Clone();
            var rect = new Rect(left * w / 64, top * h / 64, w - (left + right) * w / 64, h - (top + bottom) * h / 64);
            if (rect.Width < 32 || rect.Height < 32) return gray.Clone();
            using var roi = new Mat(gray, rect);
            return roi.Clone();
        }

        /// <summary>pHash of a window <paramref name="size"/> (fraction of the frame) whose top-left corner is at (ox, oy) as fractions of the frame.</summary>
        private static ulong HashWindow(Mat m, double size, double ox, double oy)
        {
            int cw = Math.Max(8, (int)(m.Width * size)), ch = Math.Max(8, (int)(m.Height * size));
            int x = Math.Clamp((int)(m.Width * ox), 0, Math.Max(0, m.Width - cw)), y = Math.Clamp((int)(m.Height * oy), 0, Math.Max(0, m.Height - ch));
            var rect = new Rect(x, y, Math.Min(cw, m.Width), Math.Min(ch, m.Height));
            using var roi = new Mat(m, rect);
            return SimilarImageEngine.HashOf(roi).Hash;
        }

        /// <summary>dHash (9x8) of one quarter of the frame: 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right.</summary>
        private static ulong QuadrantHash(Mat m, int quadrant)
        {
            int qw = m.Width / 2, qh = m.Height / 2;
            if (qw < 9 || qh < 8) return 0;
            using var roi = new Mat(m, new Rect((quadrant & 1) * qw, (quadrant >> 1) * qh, qw, qh));
            using var small = new Mat();
            Cv2.Resize(roi, small, new OpenCvSharp.Size(9, 8), 0, 0, InterpolationFlags.Area);
            var grid = new double[8, 9];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 9; x++)
                    grid[y, x] = small.At<byte>(y, x);
            return SimilarHashing.DHash(grid);
        }

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
                if (!double.IsFinite(fps) || fps < 0.1 || fps > 1000) fps = 25;   // unknown frame rate: assume a common one
                double count = capture.Get(VideoCaptureProperties.FrameCount);

                // A damaged header can claim 9223372036854775807 frames, or none. Such a length is not trusted:
                // the frames are read in order instead and the real length is measured from them.
                double duration = double.IsFinite(count) && count >= 1 ? SimilarText.SaneSeconds(count / fps) : 0;
                if (duration <= 0) return SampleSequential(capture, fps, token);

                int step = StepFor(duration);
                var sets = new List<FrameSet>();
                int consecutiveFailures = 0, lastGood = -1;
                double lastGoodSecond = 0;
                var watch = Stopwatch.StartNew();

                using var frame = new Mat();
                using var gray = new Mat();
                for (int k = 0; k <= MaxSamples; k++)
                {
                    double t = (double)k * step;
                    if (t >= duration) break;
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed > SamplingBudget && sets.Count >= 10) break;   // very slow codec: use what we have

                    capture.Set(VideoCaptureProperties.PosMsec, t * 1000.0);
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        // Past what is really in the file (truncated copy, header longer than the data).
                        if (++consecutiveFailures >= 3) break;
                        sets.Add(FrameSet.FlatSet);
                        continue;
                    }

                    consecutiveFailures = 0;
                    if (frame.Channels() == 1) frame.CopyTo(gray);
                    else Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    sets.Add(DescribeFrame(gray));
                    lastGood = sets.Count - 1;
                    lastGoodSecond = t;
                }

                // Too few readable frames: let ffmpeg try instead.
                if (lastGood < 2) return null;
                if (sets.Count > lastGood + 1) sets.RemoveRange(lastGood + 1, sets.Count - lastGood - 1);
                if (consecutiveFailures >= 3) duration = Math.Min(duration, lastGoodSecond + step);   // the file ends before its header says

                // Seeking that lands on the same picture every time means the timestamps are broken (a damaged header):
                // that row would match nothing real. Read the video in order instead. (A genuinely still video takes
                // this slower road too, and comes out the same.)
                int nonFlat = sets.Count(s => !s.Flat);
                if (nonFlat >= 4 && sets.Where(s => !s.Flat).Select(s => s.Full).Distinct().Count() <= Math.Max(1, nonFlat / 10))
                {
                    using var again = new VideoCapture(path, VideoCaptureAPIs.ANY);
                    return again.IsOpened() ? SampleSequential(again, fps, token) : null;
                }

                return BuildSignature(sets, duration, step, capture);
            }
            catch (Exception ex) when (ex is OpenCVException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        private static MediaSignature BuildSignature(List<FrameSet> sets, double duration, int step, VideoCapture capture) => new()
        {
            Kind = MediaKind.Video,
            DurationSeconds = duration,
            Width = (int)capture.Get(VideoCaptureProperties.FrameWidth),
            Height = (int)capture.Get(VideoCaptureProperties.FrameHeight),
            VideoStep = step,
            Frames = sets.Select(s => s.Full).ToArray(),
            FramesMirror = sets.Select(s => s.Flat ? MediaSignature.FlatFrame : s.Mirror).ToArray(),
            FrameZoom = sets.SelectMany(s => s.Zoom).ToArray(),
            FrameQuads = sets.SelectMany(s => s.Quads).ToArray(),
        };

        /// <summary>
        /// For a video whose header gives no usable length: reads the frames in order, keeps one per second (up to
        /// <see cref="MaxSamples"/>) and takes the length from how many frames there really were. Slower than seeking,
        /// so it is only used when the header can't be trusted.
        /// </summary>
        private static MediaSignature? SampleSequential(VideoCapture capture, double fps, CancellationToken token)
        {
            int every = Math.Max(1, (int)Math.Round(fps));
            var sets = new List<FrameSet>();
            long index = 0;
            var watch = Stopwatch.StartNew();

            using var frame = new Mat();
            using var gray = new Mat();
            while (sets.Count < MaxSamples)
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed > SamplingBudget && sets.Count >= 10) break;
                if (!capture.Grab()) break;
                if (index++ % every != 0) continue;
                if (!capture.Retrieve(frame) || frame.Empty()) continue;

                if (frame.Channels() == 1) frame.CopyTo(gray);
                else Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                sets.Add(DescribeFrame(gray));
            }

            if (sets.Count < 3) return null;
            double duration = SimilarText.SaneSeconds(index / fps);
            if (duration <= 0) duration = sets.Count;
            return BuildSignature(sets, duration, 1, capture);
        }

        /// <summary>
        /// Builds the signature from raw 32x32 gray frames that ffmpeg extracted
        /// (<c>fps=1/step,scale=32:32,format=gray</c>): the fallback for codecs OpenCV can't open. Whole-frame and
        /// mirrored hashes only (no crop or quadrant variants), so such files still match re-encodes, trims and flips.
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

            var noZoom = new ulong[count * ZoomSlots];
            Array.Fill(noZoom, MediaSignature.FlatFrame);
            return new MediaSignature
            {
                Kind = MediaKind.Video,
                DurationSeconds = duration,
                Width = width,
                Height = height,
                VideoStep = step,
                Frames = frames,
                FramesMirror = mirrored,
                FrameZoom = noZoom,
                FrameQuads = Array.Empty<ulong>(),
            };
        }

        // =================================================================
        //  Matching
        // =================================================================

        /// <summary>Result of lining two videos up.</summary>
        public readonly record struct Alignment(double Coverage, double MeanDistance, bool Mirrored, bool Cropped, double OffsetSeconds, bool Matched);

        /// <summary>One row of frames to slide: whole-frame hashes plus optional quadrant hashes (4 per frame).</summary>
        private sealed record Row(ulong[] Full, ulong[]? Quads);

        /// <summary>Variant 0 plain, 1 mirrored, 2.. = crop slot (variant - 2); variants 12 and 13 are the mirrored crops.</summary>
        private static Row RowOf(MediaSignature s, int variant)
        {
            int n = s.Frames.Length;
            switch (variant)
            {
                case 0:
                    return new Row(s.Frames, Quads(s, 0));
                case 1:
                    return new Row(s.FramesMirror, Quads(s, 4));
                default:
                    {
                        if (s.FrameZoom.Length != n * ZoomSlots) return new Row(Array.Empty<ulong>(), null);
                        int slot = variant - 2;   // see ZoomSlots
                        var full = new ulong[n];
                        for (int f = 0; f < n; f++) full[f] = s.Frames[f] == MediaSignature.FlatFrame ? MediaSignature.FlatFrame : s.FrameZoom[f * ZoomSlots + slot];
                        return new Row(full, null);
                    }
            }
        }

        private static ulong[]? Quads(MediaSignature s, int offset)
        {
            int n = s.Frames.Length;
            if (s.FrameQuads.Length != n * 8) return null;
            var q = new ulong[n * 4];
            for (int f = 0; f < n; f++)
                for (int k = 0; k < 4; k++) q[f * 4 + k] = s.FrameQuads[f * 8 + offset + k];
            return q;
        }

        /// <summary>
        /// Best alignment of two videos: slides one row of frames over the other and counts frames that agree (flat
        /// frames never count). Coverage is the share of the shorter video's usable frames that found a partner.
        /// Tries each version of A (plain, mirrored, cropped, cropped+mirrored) against B as it is, then each version
        /// of B against A. Rows sampled at different steps are compared at the coarser step, trying every phase of the finer.
        /// </summary>
        public static Alignment Align(MediaSignature a, MediaSignature b, int frameTolerance)
        {
            if (a.VideoStep <= 0 || b.VideoStep <= 0) return default;

            int step = Math.Max(a.VideoStep, b.VideoStep);
            int ka = step / a.VideoStep, kb = step / b.VideoStep;
            if (ka * a.VideoStep != step || kb * b.VideoStep != step) return default;   // steps not multiples of each other

            Alignment best = default;
            for (int pa = 0; pa < ka; pa++)
                for (int pb = 0; pb < kb; pb++)
                {
                    for (int variant = 0; variant < 2 + ZoomSlots; variant++)
                    {
                        // A's variant against B's plain frames: B is a flipped/cropped version of A.
                        Consider(ref best, Slide(Decimate(RowOf(a, variant), pa, ka), Decimate(RowOf(b, 0), pb, kb), frameTolerance), variant, step);
                        // B's variant against A's plain frames: A is the flipped/cropped one.
                        if (variant > 0)
                            Consider(ref best, Slide(Decimate(RowOf(b, variant), pb, kb), Decimate(RowOf(a, 0), pa, ka), frameTolerance), variant, step);
                    }
                }
            return best;
        }

        private static void Consider(ref Alignment best, Alignment candidate, int variant, int step)
        {
            if (candidate.Coverage <= best.Coverage) return;
            best = candidate with
            {
                Mirrored = variant is 1 or 12 or 13,
                Cropped = variant >= 2,
                OffsetSeconds = candidate.OffsetSeconds * step,
            };
        }

        private static Row Decimate(Row row, int phase, int every)
        {
            if (every == 1) return row;
            var full = new List<ulong>(row.Full.Length / every + 1);
            var quads = row.Quads == null ? null : new List<ulong>();
            for (int i = phase; i < row.Full.Length; i += every)
            {
                full.Add(row.Full[i]);
                if (quads != null) for (int k = 0; k < 4; k++) quads.Add(row.Quads![i * 4 + k]);
            }
            return new Row(full.ToArray(), quads?.ToArray());
        }

        private static Alignment Slide(Row x, Row y, int tolerance)
        {
            if (x.Full.Length == 0 || y.Full.Length == 0) return default;
            int usableX = x.Full.Count(h => h != MediaSignature.FlatFrame);
            int usableY = y.Full.Count(h => h != MediaSignature.FlatFrame);
            int shorterUsable = Math.Min(usableX, usableY);
            if (shorterUsable < 3) return default;

            bool quads = x.Quads != null && y.Quads != null;
            int quadTolerance = tolerance + 2;

            double bestCoverage = 0, bestMean = 0, bestOffset = 0;
            // offset o: x[i] pairs with y[i + o]
            for (int o = -(x.Full.Length - 1); o < y.Full.Length; o++)
            {
                int from = Math.Max(0, -o), to = Math.Min(x.Full.Length, y.Full.Length - o);
                if (to - from < 3) continue;

                int matched = 0;
                long distance = 0;
                for (int i = from; i < to; i++)
                {
                    ulong hx = x.Full[i], hy = y.Full[i + o];
                    if (hx == MediaSignature.FlatFrame || hy == MediaSignature.FlatFrame) continue;
                    int d = BitOperations.PopCount(hx ^ hy);
                    if (d <= tolerance)
                    {
                        matched++;
                        distance += d;
                    }
                    else if (quads)
                    {
                        // A logo or subtitle can spoil the whole-frame hash: three quarters still agreeing is a match.
                        int agree = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            ulong qx = x.Quads![i * 4 + k], qy = y.Quads![(i + o) * 4 + k];
                            // A near-flat quarter hashes to almost all zeros in every video: it proves nothing.
                            int ones = BitOperations.PopCount(qx);
                            if (ones < 12 || ones > 52) continue;
                            if (BitOperations.PopCount(qx ^ qy) <= quadTolerance) agree++;
                        }
                        if (agree >= 3)
                        {
                            matched++;
                            distance += tolerance;
                        }
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
            return new Alignment(bestCoverage, bestMean, false, false, bestOffset, bestCoverage > 0);
        }
    }
}
