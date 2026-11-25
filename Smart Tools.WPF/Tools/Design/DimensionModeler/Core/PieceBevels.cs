namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// An edge of a board cut at an angle. The edge is where face <see cref="FaceA"/> meets face <see cref="FaceB"/> (faces: 0 left, 1 right,
    /// 2 bottom, 3 top, 4 back, 5 front). The cut eats <see cref="Depth"/> mm into face A and leaves face A at <see cref="Angle"/> degrees
    /// (45 = a plain chamfer). Cutting through the whole of face B is a MITER (for example 45 degrees through a board's thickness at its end).
    /// </summary>
    public readonly record struct Bevel(int FaceA, int FaceB, double Angle, double Depth);
}

namespace Smart_Tools.WPF.Controllers
{
    using Smart_Tools.WPF.Models;

    /// <summary>
    /// Angled cuts on board edges. The surface of a bevelled board is the plain (or holed) board cut by each bevel plane, with the opening
    /// closed by a flat cap made from exactly the same edge points, so it stays watertight. Holes are kept clear of the cuts.
    /// </summary>
    public static class PieceBevels
    {
        public const int MaxBevels = 12;
        public const double MinAngle = 5;
        public const double MaxAngle = 85;

        public static int AxisOf(int face) => face / 2;

        public static bool IsHigh(int face) => face % 2 == 1;

        private static double Get(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static Vec3 Unit(int axis) => axis == 0 ? new Vec3(1, 0, 0) : axis == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);

        /// <summary>How far the cut goes into face A (along face B's axis) and into face B (along face A's axis).</summary>
        public static (double IntoA, double IntoB) Setbacks(Bevel b) => (b.Depth, b.Depth * Math.Tan(b.Angle * Math.PI / 180));

        /// <summary>
        /// Valid bevels only: real faces of two different directions, the angle kept to 5..85 degrees, the depth kept inside the board (a
        /// depth that would cut past face B is shortened to cut exactly through it: that is the miter), one bevel per edge, at most 12.
        /// </summary>
        public static IReadOnlyList<Bevel> Sanitize(Vec3 size, IEnumerable<Bevel> bevels)
        {
            var kept = new List<Bevel>();
            foreach (var b in bevels)
            {
                if (kept.Count >= MaxBevels) break;
                if (b.FaceA is < 0 or > 5 || b.FaceB is < 0 or > 5 || AxisOf(b.FaceA) == AxisOf(b.FaceB)) continue;
                if (double.IsNaN(b.Angle) || double.IsNaN(b.Depth) || b.Depth <= 0) continue;
                if (kept.Any(k => k.FaceA == b.FaceA && k.FaceB == b.FaceB || k.FaceA == b.FaceB && k.FaceB == b.FaceA)) continue;
                double angle = Math.Clamp(b.Angle, MinAngle, MaxAngle);
                double tan = Math.Tan(angle * Math.PI / 180);
                double alongB = Get(size, AxisOf(b.FaceB)), alongA = Get(size, AxisOf(b.FaceA));
                double depth = Math.Min(b.Depth, alongB);
                if (depth * tan > alongA) depth = alongA / tan;       // the miter: through the whole of face B
                if (depth < 0.1) continue;
                kept.Add(new Bevel(b.FaceA, b.FaceB, angle, depth));
            }
            return kept;
        }

        /// <summary>
        /// How far a point is OUTSIDE the board's kept side of a bevel, in mm, measured square to the cut (negative = inside the material).
        /// </summary>
        public static double Outside(Part part, Bevel b, Vec3 point)
        {
            int a = AxisOf(b.FaceA), c = AxisOf(b.FaceB);
            var (intoA, intoB) = Setbacks(b);
            // inward distances from face B (along c) and from face A (along a)
            double u = IsHigh(b.FaceB) ? Get(part.Max, c) - Get(point, c) : Get(point, c) - Get(part.Min, c);
            double w = IsHigh(b.FaceA) ? Get(part.Max, a) - Get(point, a) : Get(point, a) - Get(part.Min, a);
            double f = u / intoA + w / intoB - 1;                  // >= 0 is kept
            double grad = Math.Sqrt(1 / (intoA * intoA) + 1 / (intoB * intoB));
            return -f / grad;
        }

        /// <summary>The cut face's outward direction (away from the material).</summary>
        public static Vec3 Normal(Bevel b)
        {
            int a = AxisOf(b.FaceA), c = AxisOf(b.FaceB);
            var (intoA, intoB) = Setbacks(b);
            // the kept side grows inward from both faces; outward is toward both faces
            var n = Unit(c) * ((IsHigh(b.FaceB) ? 1 : -1) / intoA) + Unit(a) * ((IsHigh(b.FaceA) ? 1 : -1) / intoB);
            return n.Normalized;
        }

        /// <summary>The three points that show the cut: on the edge (middle of its length), where the cut meets face A, and where it meets face B.</summary>
        public static (Vec3 Edge, Vec3 OnA, Vec3 OnB) Marker(Part part, Bevel b)
        {
            int a = AxisOf(b.FaceA), c = AxisOf(b.FaceB), along = 3 - a - c;
            var (intoA, intoB) = Setbacks(b);
            double ea = IsHigh(b.FaceA) ? Get(part.Max, a) : Get(part.Min, a);
            double ec = IsHigh(b.FaceB) ? Get(part.Max, c) : Get(part.Min, c);
            double mid = Get(part.Min, along) + Get(part.Size, along) / 2;
            Vec3 Point(double va, double vc)
            {
                var v = new double[3];
                v[a] = va;
                v[c] = vc;
                v[along] = mid;
                return new Vec3(v[0], v[1], v[2]);
            }
            double inA = IsHigh(b.FaceA) ? -1 : 1, inC = IsHigh(b.FaceB) ? -1 : 1;
            return (Point(ea, ec), Point(ea, ec + inC * intoA), Point(ea + inA * intoB, ec));
        }

        // =================================================================
        //  Holes and bevels together
        // =================================================================

        /// <summary>
        /// Can this hole stay with these bevels? A hole must not run into a cut: across a bevel's edge the circle must stay on the kept side,
        /// and a hole through face A or B must stay out of the strip the cut takes from that face.
        /// </summary>
        public static bool HoleIsClear(Vec3 size, Hole hole, IReadOnlyList<Bevel> bevels, double margin = 1)
        {
            var part = new Part("", Vec3.Zero, size, PartKind.Board);
            var (u1, u2) = PieceHoles.Across(hole.Axis);
            double r = hole.Diameter / 2;
            foreach (var b in bevels)
            {
                int a = AxisOf(b.FaceA), c = AxisOf(b.FaceB);
                var (intoA, intoB) = Setbacks(b);
                if (hole.Axis != a && hole.Axis != c)
                {
                    // the cut runs along the hole: the circle (in the a/c plane) must sit on the kept side of the cut line
                    var centre = new double[3];
                    centre[u1] = hole.U;
                    centre[u2] = hole.V;
                    centre[hole.Axis] = Get(size, hole.Axis) / 2;
                    if (Outside(part, b, new Vec3(centre[0], centre[1], centre[2])) > -(r + margin)) return false;
                }
                else
                {
                    // the hole goes through face A or face B: keep out of the strip the cut takes off the other face's edge
                    int across = hole.Axis == a ? c : a;            // the direction the strip runs inward
                    double strip = hole.Axis == a ? intoA : intoB;
                    bool fromHigh = hole.Axis == a ? IsHigh(b.FaceB) : IsHigh(b.FaceA);
                    double pos = across == u1 ? hole.U : hole.V;
                    double inward = fromHigh ? Get(size, across) - pos : pos;
                    if (inward - r < strip + margin) return false;
                }
            }
            return true;
        }

        /// <summary>Is a point inside the board's material (its box minus the bevels), with <paramref name="margin"/> mm to spare?</summary>
        public static bool Contains(Part part, Vec3 p, double margin)
        {
            for (int axis = 0; axis < 3; axis++)
                if (Get(p, axis) < Get(part.Min, axis) + margin || Get(p, axis) > Get(part.Max, axis) - margin) return false;
            foreach (var b in part.Bevels)
                if (Outside(part, b, p) > -margin) return false;
            return true;
        }

        /// <summary>
        /// Do two boards really share material? Their boxes may cross where a bevel took the material away (two mitred boards meeting at a
        /// corner), so the shared box is sampled: any point inside both means they overlap.
        /// </summary>
        public static bool SolidsOverlap(Part a, Part b, double tolerance = 0.01)
        {
            var lo = new Vec3(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z));
            var hi = new Vec3(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z));
            if (hi.X - lo.X <= tolerance || hi.Y - lo.Y <= tolerance || hi.Z - lo.Z <= tolerance) return false;
            if (a.Bevels.Count == 0 && b.Bevels.Count == 0) return true;
            const int n = 9;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    for (int k = 0; k < n; k++)
                    {
                        var p = new Vec3(lo.X + (hi.X - lo.X) * (i + 0.5) / n, lo.Y + (hi.Y - lo.Y) * (j + 0.5) / n, lo.Z + (hi.Z - lo.Z) * (k + 0.5) / n);
                        if (Contains(a, p, tolerance) && Contains(b, p, tolerance)) return true;
                    }
            return false;
        }

        // =================================================================
        //  The surface
        // =================================================================

        /// <summary>The board's surface: plain or holed, then cut by each bevel and closed again.</summary>
        public static IReadOnlyList<Triangle> Triangles(Part part)
        {
            var plain = part with { Edges = null };
            IReadOnlyList<Triangle> surface = BoxMesh.Triangles(plain);
            foreach (var b in part.Bevels) surface = Cut(surface, p => Outside(part, b, p), Normal(b));
            return surface;
        }

        private static (long, long, long) Key(Vec3 v) => ((long)Math.Round(v.X * 1e5), (long)Math.Round(v.Y * 1e5), (long)Math.Round(v.Z * 1e5));

        /// <summary>Keeps the part of the surface on the material's side of a plane and closes the opening with a flat cap.</summary>
        private static IReadOnlyList<Triangle> Cut(IReadOnlyList<Triangle> surface, Func<Vec3, double> outside, Vec3 capNormal)
        {
            const double eps = 1e-7;
            var kept = new List<Triangle>(surface.Count + 16);
            foreach (var t in surface)
            {
                Vec3[] pts = { t.A, t.B, t.C };
                double[] d = { outside(t.A), outside(t.B), outside(t.C) };
                if (d.All(x => x <= eps))
                {
                    kept.Add(t);
                    continue;
                }
                if (d.All(x => x >= -eps)) continue;   // wholly on the cut-away side (or lying in the cut)

                var poly = new List<Vec3>(4);
                for (int i = 0; i < 3; i++)
                {
                    int j = (i + 1) % 3;
                    if (d[i] <= eps) poly.Add(pts[i]);
                    if (d[i] < -eps && d[j] > eps || d[i] > eps && d[j] < -eps) poly.Add(Crossing(pts[i], d[i], pts[j], d[j]));
                }
                for (int i = 1; i + 1 < poly.Count; i++) kept.Add(new Triangle(poly[0], poly[i], poly[i + 1], t.Normal));
            }

            // the opening: directed edges with no partner, all lying in the cut
            var points = new Dictionary<(long, long, long), Vec3>();
            var directed = new HashSet<((long, long, long), (long, long, long))>();
            foreach (var t in kept)
            {
                Vec3[] v = { t.A, t.B, t.C };
                for (int i = 0; i < 3; i++)
                {
                    var k1 = Key(v[i]);
                    var k2 = Key(v[(i + 1) % 3]);
                    points[k1] = v[i];
                    directed.Add((k1, k2));
                }
            }
            var next = new Dictionary<(long, long, long), (long, long, long)>();
            foreach (var (k1, k2) in directed)
                if (!directed.Contains((k2, k1)) && Math.Abs(outside(points[k1])) < 1e-4 && Math.Abs(outside(points[k2])) < 1e-4)
                    next[k1] = k2;

            var used = new HashSet<(long, long, long)>();
            foreach (var start in next.Keys.ToList())
            {
                if (used.Contains(start)) continue;
                var loop = new List<Vec3>();
                var at = start;
                while (!used.Contains(at) && next.TryGetValue(at, out var to))
                {
                    used.Add(at);
                    loop.Add(points[at]);
                    at = to;
                }
                if (loop.Count < 3) continue;
                var centre = loop.Aggregate(Vec3.Zero, (s, p) => s + p) * (1.0 / loop.Count);
                for (int i = 0; i < loop.Count; i++)
                    kept.Add(new Triangle(centre, loop[(i + 1) % loop.Count], loop[i], capNormal));   // reversed, so each edge pairs with its opening edge
            }
            return kept;
        }

        /// <summary>Where an edge crosses the cut. Worked out from the same end every time, so two triangles sharing the edge get the same point.</summary>
        private static Vec3 Crossing(Vec3 p, double dp, Vec3 q, double dq)
        {
            bool swap = p.X > q.X || p.X == q.X && (p.Y > q.Y || p.Y == q.Y && p.Z > q.Z);
            if (swap)
            {
                (p, q) = (q, p);
                (dp, dq) = (dq, dp);
            }
            double t = dp / (dp - dq);
            return p + (q - p) * t;
        }
    }
}
