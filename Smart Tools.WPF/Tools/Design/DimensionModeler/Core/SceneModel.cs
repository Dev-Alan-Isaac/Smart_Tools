namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  3D Modeler scene: shapes and groups. Plain data, no WPF, no I/O. Millimetres, Y up, the front faces +Z, the floor is Y = 0.
    //  A shape is a unit primitive (it fills -0.5..0.5 on every axis) scaled by Size, turned by Rotation, moved to Position (its
    //  centre). A shape can be a HOLE: inside a group it cuts the group's solids; on its own it cuts nothing and is drawn as a ghost.
    // =====================================================================

    public enum ShapeKind
    {
        Box,
        Cylinder,
        Sphere,
        Cone,
        Wedge,
        Tube,
        Mesh,
    }

    /// <summary>Something in the scene: a shape or a group. Ids are stable across edits; names are what the person sees.</summary>
    public abstract record SceneNode(string Id, string Name, bool Hidden, bool Locked);

    /// <summary>
    /// One shape. <see cref="Rotation"/> is in degrees, applied X first, then Y, then Z. <see cref="Mirrored"/> flips the shape along its
    /// own X before turning it (what a mirror leaves behind). <see cref="Segments"/> is how round a round shape is (3..128);
    /// <see cref="Inner"/> is a tube's hole as a share of its width (0..0.95). <see cref="MeshId"/> names the imported surface of a
    /// Mesh shape in <see cref="Scene.Meshes"/>. <see cref="Colour"/> is "#RRGGBB" or null for the default look.
    /// </summary>
    public sealed record SceneShape(
        string Id, string Name, ShapeKind Kind, Vec3 Position, Vec3 Size, Vec3 Rotation,
        bool IsHole = false, string? Colour = null, int Segments = 32, double Inner = 0.6, string? MeshId = null,
        bool Mirrored = false, bool Hidden = false, bool Locked = false) : SceneNode(Id, Name, Hidden, Locked);

    /// <summary>A group: its solids are joined into one object and its holes are cut out of them. <see cref="Colour"/>, when set, paints all of it.</summary>
    public sealed record GroupNode(string Id, string Name, IReadOnlyList<SceneNode> Children, string? Colour = null, bool Hidden = false, bool Locked = false)
        : SceneNode(Id, Name, Hidden, Locked)
    {
        // a record compares lists by reference; two groups with the same children are the same group
        public bool Equals(GroupNode? other) =>
            other is not null && Id == other.Id && Name == other.Name && Colour == other.Colour && Hidden == other.Hidden && Locked == other.Locked
            && Children.SequenceEqual(other.Children);

        public override int GetHashCode() => HashCode.Combine(Id, Name, Children.Count, Colour, Hidden, Locked);
    }

    /// <summary>An imported surface, stored once and shared by every Mesh shape that uses it: triangles (3 points each) inside -0.5..0.5.</summary>
    public sealed record MeshData(string Id, IReadOnlyList<Vec3> Points);

    /// <summary>The whole design: its name, the top-level shapes and groups, and the imported surfaces they use.</summary>
    public sealed record Scene(string Name, IReadOnlyList<SceneNode> Nodes, IReadOnlyDictionary<string, MeshData> Meshes)
    {
        public static Scene Empty(string name) => new(name, Array.Empty<SceneNode>(), new Dictionary<string, MeshData>());

        public bool Equals(Scene? other) =>
            other is not null && Name == other.Name && Nodes.SequenceEqual(other.Nodes)
            && Meshes.Count == other.Meshes.Count && Meshes.Keys.All(other.Meshes.ContainsKey);

        public override int GetHashCode() => HashCode.Combine(Name, Nodes.Count, Meshes.Count);
    }

    /// <summary>A 3x3 matrix for turning shapes (rows r0, r1, r2).</summary>
    public readonly record struct Mat3(double M00, double M01, double M02, double M10, double M11, double M12, double M20, double M21, double M22)
    {
        public static Mat3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        /// <summary>Flips X (a mirror along the shape's own X).</summary>
        public static Mat3 FlipX => new(-1, 0, 0, 0, 1, 0, 0, 0, 1);

        public static Mat3 Scale(Vec3 s) => new(s.X, 0, 0, 0, s.Y, 0, 0, 0, s.Z);

        public static Mat3 FromColumns(Vec3 c0, Vec3 c1, Vec3 c2) => new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

        public static Mat3 RotationX(double deg)
        {
            double r = deg * Math.PI / 180, c = Clean(Math.Cos(r)), s = Clean(Math.Sin(r));
            return new(1, 0, 0, 0, c, -s, 0, s, c);
        }

        public static Mat3 RotationY(double deg)
        {
            double r = deg * Math.PI / 180, c = Clean(Math.Cos(r)), s = Clean(Math.Sin(r));
            return new(c, 0, s, 0, 1, 0, -s, 0, c);
        }

        public static Mat3 RotationZ(double deg)
        {
            double r = deg * Math.PI / 180, c = Clean(Math.Cos(r)), s = Clean(Math.Sin(r));
            return new(c, -s, 0, s, c, 0, 0, 0, 1);
        }

        /// <summary>The turn of a shape: X first, then Y, then Z (R = Rz * Ry * Rx).</summary>
        public static Mat3 FromEuler(Vec3 degrees) => RotationZ(degrees.Z) * RotationY(degrees.Y) * RotationX(degrees.X);

        /// <summary>The X, Y, Z degrees of a pure rotation (the opposite of <see cref="FromEuler"/>), rounded to 1e-6.</summary>
        public Vec3 ToEuler()
        {
            const double toDeg = 180 / Math.PI;
            double x, y, z;
            if (Math.Abs(M20) < 1 - 1e-9)
            {
                y = Math.Asin(-M20);
                x = Math.Atan2(M21, M22);
                z = Math.Atan2(M10, M00);
            }
            else if (M20 < 0)
            {
                y = Math.PI / 2;
                x = Math.Atan2(M01, M02);
                z = 0;
            }
            else
            {
                y = -Math.PI / 2;
                x = Math.Atan2(-M01, -M02);
                z = 0;
            }
            static double R(double v) { double d = Math.Round(v, 6); return d == 0 ? 0 : d; }
            return new Vec3(R(x * toDeg), R(y * toDeg), R(z * toDeg));
        }

        public double Determinant => M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        public Mat3 Transpose => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public static Mat3 operator *(Mat3 a, Mat3 b) => new(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        public static Vec3 operator *(Mat3 m, Vec3 v) => new(
            m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
            m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
            m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

        // 90 degree turns give exact 0 and 1, so boards turned square stay square
        private static double Clean(double v) => Math.Abs(v) < 1e-12 ? 0 : Math.Abs(Math.Abs(v) - 1) < 1e-12 ? Math.Sign(v) : v;

        public override string ToString() => FormattableString.Invariant($"[{M00:0.###} {M01:0.###} {M02:0.###}; {M10:0.###} {M11:0.###} {M12:0.###}; {M20:0.###} {M21:0.###} {M22:0.###}]");
    }

    /// <summary>A box in the scene's axes (the box around something).</summary>
    public readonly record struct Bounds3(Vec3 Min, Vec3 Max)
    {
        public Vec3 Size => Max - Min;

        public Vec3 Centre => (Min + Max) * 0.5;

        public bool IsEmpty => Min.X > Max.X;

        public static Bounds3 Nothing => new(new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), new Vec3(double.MinValue, double.MinValue, double.MinValue));

        public Bounds3 With(Vec3 p) => new(
            new Vec3(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z)),
            new Vec3(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z)));

        public Bounds3 With(Bounds3 b) => b.IsEmpty ? this : IsEmpty ? b : With(b.Min).With(b.Max);

        /// <summary>True when the two boxes share space (more than <paramref name="eps"/> on every axis; touching is not overlapping).</summary>
        public bool Overlaps(Bounds3 b, double eps = 1e-6) =>
            !IsEmpty && !b.IsEmpty
            && Min.X < b.Max.X - eps && b.Min.X < Max.X - eps
            && Min.Y < b.Max.Y - eps && b.Min.Y < Max.Y - eps
            && Min.Z < b.Max.Z - eps && b.Min.Z < Max.Z - eps;

        public static Bounds3 Of(IEnumerable<Vec3> points)
        {
            var b = Nothing;
            foreach (var p in points) b = b.With(p);
            return b;
        }
    }

    /// <summary>A triangle of a finished object with its colour ("#RRGGBB", or null for the default look).</summary>
    public readonly record struct ColouredTriangle(Triangle Triangle, string? Colour);

    /// <summary>
    /// One top-level object ready to draw or export: its triangles and its box. <see cref="IsHole"/> = a hole (or a group of holes) that
    /// is not grouped with anything, drawn as a see-through ghost and left out of exports.
    /// </summary>
    public sealed record ObjectMesh(string NodeId, IReadOnlyList<ColouredTriangle> Triangles, bool IsHole, Bounds3 Bounds);
}
