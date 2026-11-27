using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>The real edges and corners of a surface: where it bends, not the lines that only split a flat face into triangles.</summary>
    public sealed record MeshFeatures(IReadOnlyList<(Vec3 A, Vec3 B)> Edges, IReadOnlyList<Vec3> Corners);

    /// <summary>What a measuring point caught.</summary>
    public enum TapeSnap
    {
        None,
        Corner,
        Edge,
        Surface,
        Floor,
    }
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Finding the real edges of a triangle surface, so a measuring point can sit exactly on an edge or a corner. An edge is real when
    /// the two triangles that share it face directions more than <see cref="BendDegrees"/> apart, or when only one triangle has it
    /// (an open border). Corners are the ends of real edges. Pure: no WPF.
    /// </summary>
    public static class MeshEdges
    {
        /// <summary>Faces this many degrees apart or more meet at a real edge (a 32-sided cylinder's sides are 11 apart: no edge).</summary>
        public const double BendDegrees = 20;

        private const double Weld = 1e-4;   // mm: corners closer than this are the same corner

        private readonly record struct Key(long X, long Y, long Z);

        private static Key KeyOf(Vec3 p) => new((long)Math.Round(p.X / Weld), (long)Math.Round(p.Y / Weld), (long)Math.Round(p.Z / Weld));

        public static MeshFeatures Find(IEnumerable<Triangle> triangles)
        {
            double cosLimit = Math.Cos(BendDegrees * Math.PI / 180);
            var points = new Dictionary<Key, Vec3>();
            var faces = new Dictionary<(Key, Key), List<Vec3>>();   // edge (sorted ends) -> normals of the triangles that have it

            Key Add(Vec3 p)
            {
                var k = KeyOf(p);
                points.TryAdd(k, p);
                return k;
            }

            foreach (var t in triangles)
            {
                var n = Vec3.Cross(t.B - t.A, t.C - t.A);
                double len = n.Length;
                if (len < 1e-12) continue;   // no area: no face, no edge
                n = n * (1 / len);
                var ka = Add(t.A);
                var kb = Add(t.B);
                var kc = Add(t.C);
                foreach (var (p, q) in new[] { (ka, kb), (kb, kc), (kc, ka) })
                {
                    if (p == q) continue;
                    var edge = Compare(p, q) < 0 ? (p, q) : (q, p);
                    if (!faces.TryGetValue(edge, out var list)) faces[edge] = list = new List<Vec3>(2);
                    list.Add(n);
                }
            }

            var edges = new List<(Vec3, Vec3)>();
            var corners = new HashSet<Key>();
            foreach (var ((p, q), normals) in faces)
            {
                bool real = normals.Count == 1;
                for (int i = 0; i < normals.Count && !real; i++)
                    for (int j = i + 1; j < normals.Count && !real; j++)
                        real = Vec3.Dot(normals[i], normals[j]) < cosLimit;
                if (!real) continue;
                edges.Add((points[p], points[q]));
                corners.Add(p);
                corners.Add(q);
            }
            return new MeshFeatures(edges, corners.Select(k => points[k]).ToList());
        }

        private static int Compare(Key a, Key b) =>
            a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);

        /// <summary>
        /// The point of the segment nearest to the line of sight (for catching an edge under the pointer). Null when the ray runs along
        /// the segment.
        /// </summary>
        public static Vec3? NearestOnSegment(Ray3 ray, Vec3 a, Vec3 b)
        {
            var d = b - a;
            double len = d.Length;
            if (len < 1e-9) return a;
            var unit = d * (1 / len);
            if (PieceDrag.ClosestOnAxis(ray, a, unit) is not { } s) return null;
            return a + unit * Math.Clamp(s, 0, len);
        }

        /// <summary>
        /// <paramref name="to"/> moved so that, from <paramref name="from"/>, only the biggest of the three differences is left: a
        /// straight measurement along X, Y or Z (Shift while measuring).
        /// </summary>
        public static Vec3 AlongMainAxis(Vec3 from, Vec3 to)
        {
            var d = to - from;
            double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
            return ax >= ay && ax >= az ? from + new Vec3(d.X, 0, 0)
                 : ay >= az ? from + new Vec3(0, d.Y, 0)
                 : from + new Vec3(0, 0, d.Z);
        }
    }
}
