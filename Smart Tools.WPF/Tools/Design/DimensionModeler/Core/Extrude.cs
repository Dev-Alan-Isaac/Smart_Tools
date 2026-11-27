using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>A point of a flat outline drawn on the floor (top view): X and Z in mm.</summary>
    public readonly record struct OutlinePoint(double X, double Z);

    /// <summary>Why an outline cannot become a solid.</summary>
    public enum OutlineProblem
    {
        None,
        TooFewPoints,
        TooManyPoints,
        NoArea,
        Crossing,
    }

    /// <summary>An extruded outline: the shape to add and the surface it uses (add both to the scene).</summary>
    public sealed record ExtrudedShape(SceneShape Shape, MeshData Mesh);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Extrude: a flat outline drawn on the floor becomes a solid of the chosen thickness (a prism). Any outline that does not cross
    /// itself works, also hollow-in ones like a hand or a star (the top and bottom are cut into triangles by ear clipping). The result is
    /// an ordinary Mesh shape, so it moves, stretches, colours, groups and cuts like any other. Pure: no WPF.
    /// </summary>
    public static class Extrude
    {
        public const int MaxPoints = 400;

        /// <summary>The smallest thickness (mm).</summary>
        public const double MinHeight = 0.1;

        /// <summary>The outline with repeated points, a closing copy of the first point and points on a straight line removed.</summary>
        public static List<OutlinePoint> Clean(IReadOnlyList<OutlinePoint> points)
        {
            var list = new List<OutlinePoint>();
            foreach (var p in points)
                if (list.Count == 0 || Dist(list[^1], p) > 1e-6) list.Add(p);
            if (list.Count > 1 && Dist(list[0], list[^1]) <= 1e-6) list.RemoveAt(list.Count - 1);

            bool changed = true;
            while (changed && list.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[(i - 1 + list.Count) % list.Count];
                    var b = list[i];
                    var c = list[(i + 1) % list.Count];
                    if (Math.Abs(Cross(a, b, c)) <= 1e-9 * Math.Max(1, Dist(a, c) * Dist(a, c)))
                    {
                        list.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            return list;
        }

        /// <summary>Whether the outline can be extruded (checked on the cleaned outline).</summary>
        public static OutlineProblem Check(IReadOnlyList<OutlinePoint> points)
        {
            var p = Clean(points);
            if (p.Count < 3) return OutlineProblem.TooFewPoints;
            if (p.Count > MaxPoints) return OutlineProblem.TooManyPoints;
            if (Crosses(p)) return OutlineProblem.Crossing;   // first: a bow tie's two halves cancel out to no area
            return Math.Abs(Area(p)) < 1e-6 ? OutlineProblem.NoArea : OutlineProblem.None;
        }

        /// <summary>
        /// The outline as a solid standing on <paramref name="baseY"/>, <paramref name="height"/> tall. Null when <see cref="Check"/>
        /// finds a problem.
        /// </summary>
        public static ExtrudedShape? Make(string name, IReadOnlyList<OutlinePoint> points, double height, double baseY = 0)
        {
            if (Check(points) != OutlineProblem.None) return null;
            var p = Clean(points);
            if (Area(p) > 0) p.Reverse();   // one winding for everything below: clockwise in X/Z, which faces the top up

            double minX = p.Min(q => q.X), maxX = p.Max(q => q.X), minZ = p.Min(q => q.Z), maxZ = p.Max(q => q.Z);
            double w = Math.Max(maxX - minX, MinHeight), d = Math.Max(maxZ - minZ, MinHeight), h = Math.Max(height, MinHeight);
            double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
            Vec3 Top(OutlinePoint q) => new((q.X - cx) / w, 0.5, (q.Z - cz) / d);
            Vec3 Bottom(OutlinePoint q) => new((q.X - cx) / w, -0.5, (q.Z - cz) / d);

            var tri = new List<Vec3>();
            void Add(Vec3 a, Vec3 b, Vec3 c, double wantY)
            {
                // keep every face wound outward: flip a cap triangle that faces the wrong way
                var n = Vec3.Cross(b - a, c - a);
                if (wantY != 0 && Math.Sign(n.Y) != Math.Sign(wantY)) (b, c) = (c, b);
                tri.Add(a); tri.Add(b); tri.Add(c);
            }
            foreach (var (a, b, c) in Triangulate(p))
            {
                Add(Top(p[a]), Top(p[b]), Top(p[c]), +1);
                Add(Bottom(p[a]), Bottom(p[c]), Bottom(p[b]), -1);
            }
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                // clockwise outline (negative area): this corner order faces each wall outward (checked by the volume tests)
                Add(Bottom(a), Top(b), Top(a), 0);
                Add(Bottom(a), Bottom(b), Top(b), 0);
            }

            string meshId = "extrude-" + SceneEdit.NewId();
            var shape = SceneEdit.Sanitize(new SceneShape(SceneEdit.NewId(), name, ShapeKind.Mesh,
                new Vec3(cx, baseY + h / 2, cz), new Vec3(w, h, d), Vec3.Zero, MeshId: meshId));
            return new ExtrudedShape(shape, new MeshData(meshId, tri));
        }

        // =================================================================
        //  Geometry
        // =================================================================

        private static double Dist(OutlinePoint a, OutlinePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        private static double Cross(OutlinePoint a, OutlinePoint b, OutlinePoint c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

        /// <summary>Signed area in X/Z (positive = counter-clockwise when X points right and Z points down the page).</summary>
        public static double Area(IReadOnlyList<OutlinePoint> p)
        {
            double s = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                s += a.X * b.Z - b.X * a.Z;
            }
            return s / 2;
        }

        /// <summary>True when two edges that are not neighbours touch or cross.</summary>
        public static bool Crosses(IReadOnlyList<OutlinePoint> p)
        {
            int n = p.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue;   // neighbours share a corner
                    if (SegmentsMeet(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) return true;
                }
            return false;
        }

        private static bool SegmentsMeet(OutlinePoint a, OutlinePoint b, OutlinePoint c, OutlinePoint d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            return (d1 == 0 && OnSegment(c, d, a)) || (d2 == 0 && OnSegment(c, d, b)) || (d3 == 0 && OnSegment(a, b, c)) || (d4 == 0 && OnSegment(a, b, d));
        }

        private static bool OnSegment(OutlinePoint a, OutlinePoint b, OutlinePoint p) =>
            p.X >= Math.Min(a.X, b.X) - 1e-12 && p.X <= Math.Max(a.X, b.X) + 1e-12 && p.Z >= Math.Min(a.Z, b.Z) - 1e-12 && p.Z <= Math.Max(a.Z, b.Z) + 1e-12;

        /// <summary>Ear clipping: the outline (no crossings) cut into triangles, as index triples into it.</summary>
        public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<OutlinePoint> p)
        {
            var result = new List<(int, int, int)>();
            var left = Enumerable.Range(0, p.Count).ToList();
            double sign = Math.Sign(Area(p));
            int guard = 0;
            while (left.Count > 3 && guard++ < 100000)
            {
                bool clipped = false;
                for (int i = 0; i < left.Count; i++)
                {
                    int ia = left[(i - 1 + left.Count) % left.Count], ib = left[i], ic = left[(i + 1) % left.Count];
                    var a = p[ia]; var b = p[ib]; var c = p[ic];
                    if (Math.Sign(Cross(a, b, c)) != sign) continue;   // a dent, not an ear
                    bool empty = true;
                    foreach (int k in left)
                    {
                        if (k == ia || k == ib || k == ic) continue;
                        if (Inside(a, b, c, p[k])) { empty = false; break; }
                    }
                    if (!empty) continue;
                    result.Add((ia, ib, ic));
                    left.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;   // numerically stuck: stop rather than loop (the check above refuses such outlines in practice)
            }
            if (left.Count == 3) result.Add((left[0], left[1], left[2]));
            return result;
        }

        private static bool Inside(OutlinePoint a, OutlinePoint b, OutlinePoint c, OutlinePoint p)
        {
            double c1 = Cross(a, b, p), c2 = Cross(b, c, p), c3 = Cross(c, a, p);
            bool neg = c1 < 0 || c2 < 0 || c3 < 0, pos = c1 > 0 || c2 > 0 || c3 > 0;
            return !(neg && pos);
        }
    }
}
