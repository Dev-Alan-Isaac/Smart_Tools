using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Round holes through a board: keeping them valid, and the surface of a board that has them. A face with holes is cut into
    /// vertical strips between the x of every hole vertex, so each strip is a few trapezoids (the holes are convex and do not
    /// touch), and the points where a hole starts or ends are added to the neighbouring strip so the surface has no cracks.
    /// </summary>
    public static class PieceHoles
    {
        public const int MaxHoles = 8;
        public const double MinDiameter = 1;
        private const double Margin = 0.5;      // a hole stays this far from the board's edge and from other holes
        private const int Sides = 32;           // a circle is drawn as a 32-sided polygon
        private const double Eps = 1e-7;

        /// <summary>The two axes a hole through <paramref name="axis"/> is placed along, in ascending order.</summary>
        public static (int First, int Second) Across(int axis) => axis switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };

        private static double Get(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static Vec3 Unit(int axis) => axis == 0 ? new Vec3(1, 0, 0) : axis == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);

        /// <summary>The direction a board is thinnest in (ties go to Z, then Y), where a hole normally goes through.</summary>
        public static int ThinAxis(Vec3 size)
        {
            int axis = 2;
            if (size.Y < Get(size, axis)) axis = 1;
            if (size.X < Get(size, axis)) axis = 0;
            return axis;
        }

        /// <summary>
        /// The holes made safe for a board of this size: no NaN, all through the same direction as the first, inside the face with a
        /// small margin, not overlapping an earlier one, at most <see cref="MaxHoles"/>. A face too small for a hole has none.
        /// </summary>
        public static IReadOnlyList<Hole> Sanitize(Vec3 size, IEnumerable<Hole>? holes)
        {
            var kept = new List<Hole>();
            if (holes == null) return kept;
            int? axis = null;
            foreach (var h in holes)
            {
                if (kept.Count >= MaxHoles) break;
                if (double.IsNaN(h.U) || double.IsNaN(h.V) || double.IsNaN(h.Diameter)) continue;
                axis ??= Math.Clamp(h.Axis, 0, 2);
                var (a1, a2) = Across(axis.Value);
                double l1 = Get(size, a1), l2 = Get(size, a2);
                double room = Math.Min(l1, l2) - 2 * Margin;
                if (room < MinDiameter) continue;
                double d = Math.Clamp(h.Diameter, MinDiameter, room), r = d / 2;
                double u = Math.Clamp(h.U, r + Margin, l1 - r - Margin), v = Math.Clamp(h.V, r + Margin, l2 - r - Margin);
                bool overlaps = kept.Any(k =>
                {
                    double du = k.U - u, dv = k.V - v;
                    return Math.Sqrt(du * du + dv * dv) < k.Diameter / 2 + r + Margin;
                });
                if (!overlaps) kept.Add(new Hole(axis.Value, u, v, d));
            }
            return kept;
        }

        // =================================================================
        //  The surface of a board with holes
        // =================================================================

        private static Triangle Tri(Vec3 a, Vec3 b, Vec3 c, Vec3 outward)
        {
            var n = Vec3.Cross(b - a, c - a);
            return Vec3.Dot(n, outward) < 0 ? new Triangle(a, c, b, outward) : new Triangle(a, b, c, outward);
        }

        private static (double U, double V)[] Circle(Hole h)
        {
            var points = new (double, double)[Sides];
            for (int k = 0; k < Sides; k++)
            {
                double angle = 2 * Math.PI * k / Sides;
                points[k] = (h.U + h.Diameter / 2 * Math.Cos(angle), h.V + h.Diameter / 2 * Math.Sin(angle));
            }
            return points;
        }

        /// <summary>The whole surface of the board: its four plain sides, the two faces with the holes cut out, and the hole walls.</summary>
        public static IReadOnlyList<Triangle> Triangles(Part part)
        {
            int axis = part.Holes[0].Axis;
            var (a1, a2) = Across(axis);
            double l1 = Get(part.Size, a1), l2 = Get(part.Size, a2), depth = Get(part.Size, axis);
            Vec3 At(double u, double v, double w) => part.Min + Unit(a1) * u + Unit(a2) * v + Unit(axis) * w;

            var list = new List<Triangle>();
            for (int f = 0; f < BoxMesh.Faces.Length; f++)
            {
                if (f / 2 == axis || f / 2 == a2) continue;   // the faces the holes go through, and the two long sides, are made below
                var (corners, normal) = BoxMesh.Faces[f];
                var a = BoxMesh.Corner(part, corners[0]);
                var b = BoxMesh.Corner(part, corners[1]);
                var c = BoxMesh.Corner(part, corners[2]);
                var d = BoxMesh.Corner(part, corners[3]);
                list.Add(new Triangle(a, b, c, normal));
                list.Add(new Triangle(a, c, d, normal));
            }

            var circles = part.Holes.Select(Circle).ToList();
            var cuts = CutPositions(l1, circles);
            // a strip line may cross a hole between two of its vertices: that point becomes a vertex of the hole too (its wall needs it)
            circles = circles.Select(ring => Refine(ring, cuts)).ToList();

            // the two sides along the strips are cut at the same places as the faces they meet, so no edge is left half-shared
            foreach (double v in new[] { 0, l2 })
            {
                var outward = Unit(a2) * (v == 0 ? -1 : 1);
                for (int s = 0; s + 1 < cuts.Count; s++)
                {
                    list.Add(Tri(At(cuts[s], v, 0), At(cuts[s + 1], v, 0), At(cuts[s + 1], v, depth), outward));
                    list.Add(Tri(At(cuts[s], v, 0), At(cuts[s + 1], v, depth), At(cuts[s], v, depth), outward));
                }
            }

            var flat = FaceTriangles(l1, l2, circles);
            var up = Unit(axis);
            foreach (var (p, q, r) in flat)
            {
                list.Add(Tri(At(p.U, p.V, depth), At(q.U, q.V, depth), At(r.U, r.V, depth), up));
                list.Add(Tri(At(p.U, p.V, 0), At(q.U, q.V, 0), At(r.U, r.V, 0), -up));
            }

            for (int h = 0; h < circles.Count; h++)
            {
                var ring = circles[h];
                var centre = At(part.Holes[h].U, part.Holes[h].V, 0);
                for (int k = 0; k < ring.Length; k++)
                {
                    var p = ring[k];
                    var q = ring[(k + 1) % ring.Length];
                    var p0 = At(p.U, p.V, 0);
                    var q0 = At(q.U, q.V, 0);
                    var p1 = At(p.U, p.V, depth);
                    var q1 = At(q.U, q.V, depth);
                    var inward = (centre - (p0 + q0) * 0.5).Normalized;   // away from the wood, into the hole
                    list.Add(Tri(p0, q0, q1, inward));
                    list.Add(Tri(p0, q1, p1, inward));
                }
            }
            return list;
        }

        private static double Lower((double U, double V)[] ring, double x) => Extent(ring, x, true);

        private static double Upper((double U, double V)[] ring, double x) => Extent(ring, x, false);

        /// <summary>The lowest or highest v of a convex ring on the vertical line at <paramref name="x"/>.</summary>
        private static double Extent((double U, double V)[] ring, double x, bool lowest)
        {
            double best = lowest ? double.PositiveInfinity : double.NegativeInfinity;
            for (int i = 0; i < ring.Length; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Length];
                if (x < Math.Min(p.U, q.U) - Eps || x > Math.Max(p.U, q.U) + Eps) continue;
                double v;
                if (Math.Abs(q.U - p.U) < Eps) v = lowest ? Math.Min(p.V, q.V) : Math.Max(p.V, q.V);
                else v = p.V + Math.Clamp((x - p.U) / (q.U - p.U), 0, 1) * (q.V - p.V);
                best = lowest ? Math.Min(best, v) : Math.Max(best, v);
            }
            return best;
        }

        /// <summary>Where the face is cut into strips: both ends and the x of every vertex of every hole, each once.</summary>
        private static List<double> CutPositions(double l1, List<(double U, double V)[]> rings)
        {
            var xs = new List<double> { 0, l1 };
            foreach (var ring in rings) xs.AddRange(ring.Select(p => p.U));
            xs.Sort();
            var cuts = new List<double>();
            foreach (double x in xs)
                if (cuts.Count == 0 || x - cuts[^1] > Eps) cuts.Add(x);
            return cuts;
        }

        /// <summary>The ring with an extra point on every edge wherever a cut line crosses it (the shape does not change).</summary>
        private static (double U, double V)[] Refine((double U, double V)[] ring, List<double> cuts)
        {
            var result = new List<(double U, double V)>();
            for (int i = 0; i < ring.Length; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Length];
                result.Add(p);
                var between = cuts.Where(x => x > Math.Min(p.U, q.U) + Eps && x < Math.Max(p.U, q.U) - Eps);
                foreach (double x in p.U <= q.U ? between : between.Reverse())
                    result.Add((x, p.V + (x - p.U) / (q.U - p.U) * (q.V - p.V)));
            }
            return result.ToArray();
        }

        private sealed record Edge(double LowLeft, double LowRight, double HighLeft, double HighRight);

        /// <summary>A rectangle 0..l1 by 0..l2 with convex holes cut out, as counter-clockwise triangles in (u, v).</summary>
        private static List<((double U, double V), (double U, double V), (double U, double V))> FaceTriangles(double l1, double l2, List<(double U, double V)[]> rings)
        {
            var cuts = CutPositions(l1, rings);
            var result = new List<((double, double), (double, double), (double, double))>();
            for (int s = 0; s + 1 < cuts.Count; s++)
            {
                double xa = cuts[s], xb = cuts[s + 1];
                var inside = rings.Where(r => r.Min(p => p.U) < xb - Eps && r.Max(p => p.U) > xa + Eps)
                                  .OrderBy(r => Lower(r, (xa + xb) / 2)).ToList();
                // holes that only touch this strip at one point: that point must be a vertex here too
                var leftPoints = rings.Where(r => Math.Abs(r.Max(p => p.U) - xa) <= Eps).Select(r => Lower(r, xa)).ToList();
                var rightPoints = rings.Where(r => Math.Abs(r.Min(p => p.U) - xb) <= Eps).Select(r => Lower(r, xb)).ToList();

                // bottom, then for every hole its lower and upper edge, then the top
                var edges = new List<Edge> { new(0, 0, 0, 0) };
                foreach (var r in inside) edges.Add(new Edge(Lower(r, xa), Lower(r, xb), Upper(r, xa), Upper(r, xb)));
                edges.Add(new Edge(l2, l2, l2, l2));

                for (int k = 0; k <= inside.Count; k++)
                {
                    // the region between the upper edge of the previous hole (or the bottom) and the lower edge of the next (or the top)
                    double lowL = k == 0 ? 0 : edges[k].HighLeft, lowR = k == 0 ? 0 : edges[k].HighRight;
                    double highL = edges[k + 1].LowLeft, highR = edges[k + 1].LowRight;
                    if (k == inside.Count) { highL = l2; highR = l2; }

                    var polygon = new List<(double U, double V)> { (xa, lowL), (xb, lowR) };
                    polygon.AddRange(rightPoints.Where(y => y > lowR + Eps && y < highR - Eps).OrderBy(y => y).Select(y => (xb, y)));
                    polygon.Add((xb, highR));
                    polygon.Add((xa, highL));
                    polygon.AddRange(leftPoints.Where(y => y > lowL + Eps && y < highL - Eps).OrderByDescending(y => y).Select(y => (xa, y)));

                    double cu = polygon.Average(p => p.U), cv = polygon.Average(p => p.V);
                    for (int i = 0; i < polygon.Count; i++)
                    {
                        var p = polygon[i];
                        var q = polygon[(i + 1) % polygon.Count];
                        double area = ((p.U - cu) * (q.V - cv) - (q.U - cu) * (p.V - cv)) / 2;
                        if (Math.Abs(area) > 1e-9) result.Add(((cu, cv), p, q));
                    }
                }
            }
            return result;
        }
    }
}
