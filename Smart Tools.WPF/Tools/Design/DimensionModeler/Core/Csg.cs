using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>A flat convex polygon of a solid's surface, counter-clockwise seen from outside, with its plane and a tag (its colour).</summary>
    public sealed class CsgPolygon
    {
        public CsgPolygon(IReadOnlyList<Vec3> vertices, string? tag) : this(vertices, CsgPlane.FromPoints(vertices), tag) { }

        public CsgPolygon(IReadOnlyList<Vec3> vertices, CsgPlane plane, string? tag)
        {
            Vertices = vertices;
            Plane = plane;
            Tag = tag;
        }

        public IReadOnlyList<Vec3> Vertices { get; }

        public CsgPlane Plane { get; }

        public string? Tag { get; }

        public CsgPolygon Flipped() => new(Vertices.Reverse().ToArray(), Plane.Flipped(), Tag);

        public CsgPolygon WithTag(string? tag) => new(Vertices, Plane, tag);
    }

    /// <summary>A plane: the points p with Normal . p = W.</summary>
    public readonly record struct CsgPlane(Vec3 Normal, double W)
    {
        /// <summary>The plane of a polygon, from its corners (Newell's method, so a sliver's three first corners cannot spoil it).</summary>
        public static CsgPlane FromPoints(IReadOnlyList<Vec3> points)
        {
            double nx = 0, ny = 0, nz = 0;
            var centre = Vec3.Zero;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                nx += (a.Y - b.Y) * (a.Z + b.Z);
                ny += (a.Z - b.Z) * (a.X + b.X);
                nz += (a.X - b.X) * (a.Y + b.Y);
                centre += a;
            }
            var n = new Vec3(nx, ny, nz).Normalized;
            centre *= 1.0 / Math.Max(1, points.Count);
            return new CsgPlane(n, Vec3.Dot(n, centre));
        }

        public CsgPlane Flipped() => new(-Normal, -W);
    }

    /// <summary>
    /// Boolean operations on solids made of polygons, by binary space partitioning (the csg.js method by Evan Wallace, MIT licence,
    /// rewritten in C# without recursion so big shapes cannot run out of stack). Pure: no WPF.
    /// </summary>
    public static class Csg
    {
        /// <summary>How close (mm) a point must be to a plane to count as lying on it.</summary>
        public const double Epsilon = 1e-5;

        public static List<CsgPolygon> Union(IReadOnlyList<CsgPolygon> a, IReadOnlyList<CsgPolygon> b)
        {
            if (a.Count == 0) return b.ToList();
            if (b.Count == 0) return a.ToList();
            if (!BoundsOf(a).Overlaps(BoundsOf(b))) return a.Concat(b).ToList();   // apart (or only touching): nothing to cut
            var na = Node.Build(a);
            var nb = Node.Build(b);
            na.ClipTo(nb);
            nb.ClipTo(na);
            nb.Invert();
            nb.ClipTo(na);
            nb.Invert();
            na.Add(nb.All());
            return na.All();
        }

        public static List<CsgPolygon> Subtract(IReadOnlyList<CsgPolygon> a, IReadOnlyList<CsgPolygon> b)
        {
            if (a.Count == 0) return new List<CsgPolygon>();
            if (b.Count == 0 || !BoundsOf(a).Overlaps(BoundsOf(b))) return a.ToList();
            var na = Node.Build(a);
            var nb = Node.Build(b);
            na.Invert();
            na.ClipTo(nb);
            nb.ClipTo(na);
            nb.Invert();
            nb.ClipTo(na);
            nb.Invert();
            na.Add(nb.All());
            na.Invert();
            return na.All();
        }

        public static List<CsgPolygon> Intersect(IReadOnlyList<CsgPolygon> a, IReadOnlyList<CsgPolygon> b)
        {
            if (a.Count == 0 || b.Count == 0 || !BoundsOf(a).Overlaps(BoundsOf(b))) return new List<CsgPolygon>();
            var na = Node.Build(a);
            var nb = Node.Build(b);
            na.Invert();
            nb.ClipTo(na);
            nb.Invert();
            na.ClipTo(nb);
            nb.ClipTo(na);
            na.Add(nb.All());
            na.Invert();
            return na.All();
        }

        public static Bounds3 BoundsOf(IEnumerable<CsgPolygon> polygons) => Bounds3.Of(polygons.SelectMany(p => p.Vertices));

        /// <summary>Each polygon as a fan of triangles (polygons are convex), with its tag.</summary>
        public static List<ColouredTriangle> Triangles(IEnumerable<CsgPolygon> polygons)
        {
            var result = new List<ColouredTriangle>();
            foreach (var p in polygons)
            {
                var v = p.Vertices;
                for (int i = 1; i + 1 < v.Count; i++)
                {
                    var n = Vec3.Cross(v[i] - v[0], v[i + 1] - v[0]);
                    if (n.Length < 1e-12) continue;
                    result.Add(new ColouredTriangle(new Triangle(v[0], v[i], v[i + 1], n.Normalized), p.Tag));
                }
            }
            return result;
        }

        /// <summary>The enclosed volume (mm3) of a closed surface, by the divergence theorem. Cracks of T-junctions do not change it.</summary>
        public static double Volume(IEnumerable<ColouredTriangle> triangles) =>
            triangles.Sum(t => Vec3.Dot(t.Triangle.A, Vec3.Cross(t.Triangle.B, t.Triangle.C))) / 6;

        // =================================================================
        //  The tree
        // =================================================================

        private const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

        /// <summary>
        /// Splits a polygon by a plane into the four lists (csg.js "splitPolygon"). A polygon on the plane goes to coplanarFront when it
        /// faces the same way as the plane, else to coplanarBack.
        /// </summary>
        private static void Split(CsgPlane plane, CsgPolygon polygon, List<CsgPolygon> coplanarFront, List<CsgPolygon> coplanarBack, List<CsgPolygon> front, List<CsgPolygon> back)
        {
            var verts = polygon.Vertices;
            int type = 0;
            Span<int> types = verts.Count <= 64 ? stackalloc int[verts.Count] : new int[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                double t = Vec3.Dot(plane.Normal, verts[i]) - plane.W;
                int ti = t < -Epsilon ? Back : t > Epsilon ? Front : Coplanar;
                type |= ti;
                types[i] = ti;
            }
            switch (type)
            {
                case Coplanar:
                    (Vec3.Dot(plane.Normal, polygon.Plane.Normal) > 0 ? coplanarFront : coplanarBack).Add(polygon);
                    break;
                case Front:
                    front.Add(polygon);
                    break;
                case Back:
                    back.Add(polygon);
                    break;
                default:
                    var f = new List<Vec3>(verts.Count + 1);
                    var b = new List<Vec3>(verts.Count + 1);
                    for (int i = 0; i < verts.Count; i++)
                    {
                        int j = (i + 1) % verts.Count;
                        int ti = types[i], tj = types[j];
                        var vi = verts[i];
                        var vj = verts[j];
                        if (ti != Back) f.Add(vi);
                        if (ti != Front) b.Add(vi);
                        if ((ti | tj) == Spanning)
                        {
                            double t = (plane.W - Vec3.Dot(plane.Normal, vi)) / Vec3.Dot(plane.Normal, vj - vi);
                            var v = vi + (vj - vi) * t;
                            f.Add(v);
                            b.Add(v);
                        }
                    }
                    // the pieces lie in the polygon's own plane: keep it rather than work it out again from a possibly thin piece
                    if (f.Count >= 3) front.Add(new CsgPolygon(f, polygon.Plane, polygon.Tag));
                    if (b.Count >= 3) back.Add(new CsgPolygon(b, polygon.Plane, polygon.Tag));
                    break;
            }
        }

        private sealed class Node
        {
            private CsgPlane? _plane;
            private Node? _front;
            private Node? _back;
            private List<CsgPolygon> _polygons = new();

            public static Node Build(IReadOnlyList<CsgPolygon> polygons)
            {
                var node = new Node();
                node.Add(polygons);
                return node;
            }

            /// <summary>Puts polygons into the tree (csg.js "build"), splitting them where they cross planes already there.</summary>
            public void Add(IReadOnlyList<CsgPolygon> polygons)
            {
                var stack = new Stack<(Node Node, IReadOnlyList<CsgPolygon> Polygons)>();
                stack.Push((this, polygons));
                while (stack.Count > 0)
                {
                    var (node, list) = stack.Pop();
                    if (list.Count == 0) continue;
                    node._plane ??= list[0].Plane;
                    var front = new List<CsgPolygon>();
                    var back = new List<CsgPolygon>();
                    foreach (var p in list) Split(node._plane.Value, p, node._polygons, node._polygons, front, back);
                    if (front.Count > 0) stack.Push((node._front ??= new Node(), front));
                    if (back.Count > 0) stack.Push((node._back ??= new Node(), back));
                }
            }

            /// <summary>Turns the solid inside out (csg.js "invert").</summary>
            public void Invert()
            {
                foreach (var node in Nodes())
                {
                    node._polygons = node._polygons.Select(p => p.Flipped()).ToList();
                    if (node._plane is { } plane) node._plane = plane.Flipped();
                    (node._front, node._back) = (node._back, node._front);
                }
            }

            /// <summary>The parts of the polygons that lie outside this solid (csg.js "clipPolygons").</summary>
            public List<CsgPolygon> Clip(IReadOnlyList<CsgPolygon> polygons)
            {
                var result = new List<CsgPolygon>();
                var stack = new Stack<(Node Node, IReadOnlyList<CsgPolygon> Polygons)>();
                stack.Push((this, polygons));
                while (stack.Count > 0)
                {
                    var (node, list) = stack.Pop();
                    if (node._plane is not { } plane)
                    {
                        result.AddRange(list);
                        continue;
                    }
                    var front = new List<CsgPolygon>();
                    var back = new List<CsgPolygon>();
                    foreach (var p in list) Split(plane, p, front, back, front, back);
                    if (front.Count > 0)
                    {
                        if (node._front != null) stack.Push((node._front, front));
                        else result.AddRange(front);
                    }
                    if (back.Count > 0 && node._back != null) stack.Push((node._back, back));   // behind the last plane = inside: dropped
                }
                return result;
            }

            /// <summary>Removes the parts of this tree's polygons that are inside the other solid (csg.js "clipTo").</summary>
            public void ClipTo(Node other)
            {
                foreach (var node in Nodes()) node._polygons = other.Clip(node._polygons);
            }

            public List<CsgPolygon> All() => Nodes().SelectMany(n => n._polygons).ToList();

            private List<Node> Nodes()
            {
                var list = new List<Node>();
                var stack = new Stack<Node>();
                stack.Push(this);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    list.Add(n);
                    if (n._front != null) stack.Push(n._front);
                    if (n._back != null) stack.Push(n._back);
                }
                return list;
            }
        }
    }
}
