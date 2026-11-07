using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Why two files were judged the same content (shown as small tags in the results).</summary>
    [Flags]
    public enum SimilarTag
    {
        None = 0,
        Flipped = 1,
        Rotated = 2,
        Cropped = 4,
        Trimmed = 8,
        DifferentResolution = 16,
        DifferentFormat = 32,
    }

    /// <summary>A pair of files the matcher judged to be the same content.</summary>
    public readonly record struct SimilarEdge(int A, int B, double Similarity, SimilarTag Tags);

    /// <summary>
    /// Everything the matcher needs to know about one file, produced once per
    /// file by the engines and cached on disk (<see cref="SimilarFingerprintCache"/>).
    /// Image: 8 perceptual hashes (4 rotations x mirrored) plus ORB features of the picture and its mirror.
    /// Video: one perceptual hash per sampled frame (and the mirrored frame).
    /// Audio: SoundFingerprinting hashes, kept in <see cref="AudioHashes"/> (not cached).
    /// </summary>
    public sealed class MediaSignature
    {
        /// <summary>Marks a flat (blank/black) frame; such frames never count as a match.</summary>
        public const ulong FlatFrame = ulong.MaxValue;

        public MediaKind Kind { get; init; }
        public double DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Images: grayscale variance of the 32x32 grid; near 0 means a blank/flat picture.</summary>
        public double Variance { get; set; }

        // ---- Image ----
        /// <summary>Index = rotation * 2 + mirrored. [0] is the picture as it is.</summary>
        public ulong[] ImageHashes { get; set; } = Array.Empty<ulong>();
        public byte[] Orb { get; set; } = Array.Empty<byte>();
        public float[] OrbPoints { get; set; } = Array.Empty<float>();
        public byte[] OrbMirror { get; set; } = Array.Empty<byte>();
        public float[] OrbMirrorPoints { get; set; } = Array.Empty<float>();

        // ---- Video ----
        /// <summary>Seconds between sampled frames.</summary>
        public int VideoStep { get; set; }
        /// <summary>Fast video scan: frames came from the keyframes nearest each sample time (much quicker to read, less exact for re-encodes).</summary>
        public bool KeyframeSampled { get; set; }
        public ulong[] Frames { get; set; } = Array.Empty<ulong>();
        public ulong[] FramesMirror { get; set; } = Array.Empty<ulong>();
        /// <summary>Per frame: pHash of crop windows of each frame, 12 per frame (see SimilarVideoEngine.ZoomSlots). For cropped copies.</summary>
        public ulong[] FrameZoom { get; set; } = Array.Empty<ulong>();
        /// <summary>Per frame: 4 quadrant hashes of the frame, then 4 of the mirrored frame (8 per frame). For logos and overlays.</summary>
        public ulong[] FrameQuads { get; set; } = Array.Empty<ulong>();

        // ---- Audio (not cached) ----
        public object? AudioHashes { get; set; }

        public bool Cacheable => Kind is MediaKind.Image or MediaKind.Video;

        public const int OrbBytesPerRow = 32;
        public int OrbRows => Orb.Length / OrbBytesPerRow;

        // =================================================================
        //  Binary form
        // =================================================================

        internal void Write(BinaryWriter w)
        {
            w.Write((byte)Kind);
            w.Write(DurationSeconds);
            w.Write(Width);
            w.Write(Height);
            w.Write(Variance);
            WriteUlongs(w, ImageHashes);
            WriteBytes(w, Orb);
            WriteFloats(w, OrbPoints);
            WriteBytes(w, OrbMirror);
            WriteFloats(w, OrbMirrorPoints);
            w.Write(VideoStep);
            w.Write(KeyframeSampled);
            WriteUlongs(w, Frames);
            WriteUlongs(w, FramesMirror);
            WriteUlongs(w, FrameZoom);
            WriteUlongs(w, FrameQuads);
        }

        internal static MediaSignature Read(BinaryReader r)
        {
            var s = new MediaSignature { Kind = (MediaKind)r.ReadByte() };
            s.DurationSeconds = r.ReadDouble();
            s.Width = r.ReadInt32();
            s.Height = r.ReadInt32();
            s.Variance = r.ReadDouble();
            s.ImageHashes = ReadUlongs(r);
            s.Orb = ReadBytes(r);
            s.OrbPoints = ReadFloats(r);
            s.OrbMirror = ReadBytes(r);
            s.OrbMirrorPoints = ReadFloats(r);
            s.VideoStep = r.ReadInt32();
            s.KeyframeSampled = r.ReadBoolean();
            s.Frames = ReadUlongs(r);
            s.FramesMirror = ReadUlongs(r);
            s.FrameZoom = ReadUlongs(r);
            s.FrameQuads = ReadUlongs(r);
            return s;
        }

        private static void WriteUlongs(BinaryWriter w, ulong[] a)
        {
            w.Write(a.Length);
            foreach (ulong v in a) w.Write(v);
        }

        private static ulong[] ReadUlongs(BinaryReader r)
        {
            int n = CheckedCount(r);
            var a = new ulong[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadUInt64();
            return a;
        }

        private static void WriteFloats(BinaryWriter w, float[] a)
        {
            w.Write(a.Length);
            foreach (float v in a) w.Write(v);
        }

        private static float[] ReadFloats(BinaryReader r)
        {
            int n = CheckedCount(r);
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadSingle();
            return a;
        }

        private static void WriteBytes(BinaryWriter w, byte[] a)
        {
            w.Write(a.Length);
            w.Write(a);
        }

        private static byte[] ReadBytes(BinaryReader r)
        {
            int n = CheckedCount(r);
            byte[] a = r.ReadBytes(n);
            if (a.Length != n) throw new EndOfStreamException();
            return a;
        }

        /// <summary>A damaged cache must fail with InvalidDataException, not allocate gigabytes.</summary>
        private static int CheckedCount(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 4_000_000) throw new InvalidDataException("Bad length in the fingerprint cache.");
            return n;
        }
    }
}
