namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Dimension Modeler data model. Plain data and vector maths - no WPF, no I/O.
    //  Millimetres everywhere. X = width (left to right), Y = height (up), Z = depth
    //  (the front of a piece faces +Z). A piece is a set of axis-aligned boxes: boards, legs, panels.
    // =====================================================================

    /// <summary>A point or a size in millimetres.</summary>
    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public static Vec3 Zero => new(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public double Length => Math.Sqrt(Dot(this, this));

        public Vec3 Normalized => Length < 1e-12 ? Zero : this * (1.0 / Length);
    }

    /// <summary>What a board is for - the preview colours by it, nothing else depends on it.</summary>
    public enum PartKind
    {
        Top,
        Board,
        Leg,
        Panel,
    }

    /// <summary>One box of the piece: its lowest corner and its size.</summary>
    public sealed record Part(string Name, Vec3 Min, Vec3 Size, PartKind Kind)
    {
        public Vec3 Max => Min + Size;
    }

    /// <summary>A finished piece: its boards, and the box around them.</summary>
    public sealed record PieceModel(string Name, IReadOnlyList<Part> Parts)
    {
        public Vec3 Min => Parts.Count == 0 ? Vec3.Zero : new Vec3(Parts.Min(p => p.Min.X), Parts.Min(p => p.Min.Y), Parts.Min(p => p.Min.Z));

        public Vec3 Max => Parts.Count == 0 ? Vec3.Zero : new Vec3(Parts.Max(p => p.Max.X), Parts.Max(p => p.Max.Y), Parts.Max(p => p.Max.Z));

        public Vec3 Size => Max - Min;
    }

    /// <summary>One slider of a template. <see cref="Label"/> is the English fallback; the view looks the text up by key.</summary>
    public sealed record ParamSpec(string Key, string Label, double Default, double Min, double Max, double Step = 1, bool Integer = false);

    /// <summary>How lengths are written on the dimension lines.</summary>
    public enum LengthUnit
    {
        Millimetres,
        Centimetres,
        Inches,
    }

    /// <summary>One triangle of a board's surface, wound counter-clockwise seen from outside, with its outward normal.</summary>
    public readonly record struct Triangle(Vec3 A, Vec3 B, Vec3 C, Vec3 Normal);

    /// <summary>
    /// A measured length with its drawn line: the line runs from <see cref="A"/> + <see cref="Offset"/> to <see cref="B"/> + <see cref="Offset"/>,
    /// and the short extension lines join it back to the measured points A and B on the piece.
    /// </summary>
    public sealed record DimensionLine(Vec3 A, Vec3 B, Vec3 Offset, double LengthMm)
    {
        public Vec3 Start => A + Offset;

        public Vec3 End => B + Offset;
    }

    /// <summary>A point on screen, in pixels from the top-left corner.</summary>
    public readonly record struct ScreenPoint(double X, double Y);
}
