using System.Globalization;
using System.Text;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// A 3D file read into boards. <see cref="Shaped"/> = boards whose surface was not a plain box (holes, angled cuts, round or
    /// turned shapes): they became the box around them. <see cref="Dropped"/> = separate objects left out over the board limit.
    /// <see cref="FromMetres"/> = the file was so small it was read as metres (Blender and most 3D programs) and scaled to mm.
    /// </summary>
    public sealed record ImportedPiece(IReadOnlyList<Part> Parts, int Shaped, int Dropped, bool FromMetres);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Reads an OBJ or STL file into boards for the custom piece: every separate object (an OBJ group, or a closed shell of
    /// triangles) becomes one board the size of the box around it. Boards are axis-aligned boxes, so anything else is simplified,
    /// and the result says how many were. The piece is moved so its lowest corner is at 0, 0, 0. Pure: no WPF.
    /// </summary>
    public static class PieceImport
    {
        /// <summary>Larger files are refused instead of filling the memory (a furniture piece is far smaller).</summary>
        public const long MaxBytes = 200L * 1024 * 1024;

        public const int MaxTriangles = 3_000_000;

        /// <summary>A piece whose longest side is at most this many units is taken to be in metres.</summary>
        public const double MetresBelow = 20;

        public static bool TryRead(string fileName, byte[] data, string partName, out ImportedPiece piece, out string error)
        {
            piece = new ImportedPiece(Array.Empty<Part>(), 0, 0, false);
            error = "";
            if (data.LongLength > MaxBytes)
            {
                error = "The file is too big.";
                return false;
            }
            string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            List<(string? Name, List<Triangle> Triangles)>? groups = ext switch
            {
                ".obj" => ReadObj(Encoding.UTF8.GetString(data), out error),
                ".stl" => ReadStl(data, out error),
                _ => null,
            };
            if (groups == null)
            {
                if (error.Length == 0) error = "Only OBJ and STL files can be imported.";
                return false;
            }
            if (groups.Sum(g => g.Triangles.Count) == 0)
            {
                error = "The file has no surfaces in it.";
                return false;
            }
            var built = Build(groups, partName);
            if (built == null)
            {
                error = "The file has no surfaces in it.";
                return false;
            }
            piece = built;
            return true;
        }

        // =================================================================
        //  Files
        // =================================================================

        /// <summary>OBJ: vertices and faces (any polygon, fanned into triangles), split by "g" / "o" names. Y is up, as written.</summary>
        public static List<(string? Name, List<Triangle> Triangles)>? ReadObj(string text, out string error)
        {
            error = "";
            var vertices = new List<Vec3>();
            var groups = new List<(string? Name, List<Triangle> Triangles)>();
            string? currentName = null;
            var current = new List<Triangle>();
            int total = 0;
            var face = new List<int>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var bits = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (bits[0])
                {
                    case "v" when bits.Length >= 4:
                        if (!Num(bits[1], out double x) || !Num(bits[2], out double y) || !Num(bits[3], out double z))
                        {
                            error = $"A point could not be read: \"{Short(line)}\".";
                            return null;
                        }
                        vertices.Add(new Vec3(x, y, z));
                        break;
                    case "g" or "o":
                        // a new name starts a new object, unless nothing was drawn under the old one yet ("o Cube" then "g Cube_Mat")
                        string? name = bits.Length > 1 ? string.Join(' ', bits.Skip(1)).Replace('_', ' ').Trim() : null;
                        if (current.Count > 0)
                        {
                            groups.Add((currentName, current));
                            current = new List<Triangle>();
                        }
                        currentName = string.IsNullOrWhiteSpace(name) || name == "default" ? null : name;
                        break;
                    case "f" when bits.Length >= 4:
                        face.Clear();
                        foreach (var token in bits.Skip(1))
                        {
                            string first = token.Split('/')[0];
                            if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index == 0)
                            {
                                error = $"A face could not be read: \"{Short(line)}\".";
                                return null;
                            }
                            index = index > 0 ? index - 1 : vertices.Count + index;   // negative = counted back from the last point
                            if (index < 0 || index >= vertices.Count)
                            {
                                error = $"A face uses a point that does not exist: \"{Short(line)}\".";
                                return null;
                            }
                            face.Add(index);
                        }
                        for (int i = 1; i + 1 < face.Count; i++)
                        {
                            if (++total > MaxTriangles)
                            {
                                error = "The model has too many triangles.";
                                return null;
                            }
                            current.Add(Tri(vertices[face[0]], vertices[face[i]], vertices[face[i + 1]]));
                        }
                        break;
                }
            }
            if (current.Count > 0) groups.Add((currentName, current));
            return groups;
        }

        /// <summary>STL, binary or text. STL is Z up; it is turned to this tool's Y up the same way the export turns it (Z -> Y, Y -> -Z).</summary>
        public static List<(string? Name, List<Triangle> Triangles)>? ReadStl(byte[] data, out string error)
        {
            error = "";
            var triangles = new List<Triangle>();
            static Vec3 YUp(double x, double y, double z) => new(x, z, -y);

            bool binary = data.Length >= 84 && 84L + 50L * BitConverter.ToUInt32(data, 80) == data.LongLength;
            if (binary)
            {
                uint count = BitConverter.ToUInt32(data, 80);
                if (count > MaxTriangles)
                {
                    error = "The model has too many triangles.";
                    return null;
                }
                for (int i = 0; i < count; i++)
                {
                    int at = 84 + 50 * i + 12;   // skip the stored normal: it is worked out from the corners
                    Vec3 P(int k) => YUp(BitConverter.ToSingle(data, at + 12 * k), BitConverter.ToSingle(data, at + 12 * k + 4), BitConverter.ToSingle(data, at + 12 * k + 8));
                    triangles.Add(Tri(P(0), P(1), P(2)));
                }
            }
            else
            {
                string text = Encoding.ASCII.GetString(data);
                if (!text.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase))
                {
                    error = "This is not an STL file.";
                    return null;
                }
                var corners = new List<Vec3>(3);
                foreach (var raw in text.Split('\n'))
                {
                    var bits = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (bits.Length == 4 && bits[0] == "vertex")
                    {
                        if (!Num(bits[1], out double x) || !Num(bits[2], out double y) || !Num(bits[3], out double z))
                        {
                            error = $"A point could not be read: \"{Short(raw.Trim())}\".";
                            return null;
                        }
                        corners.Add(YUp(x, y, z));
                    }
                    else if (bits.Length >= 1 && bits[0] == "endfacet")
                    {
                        if (corners.Count == 3)
                        {
                            if (triangles.Count >= MaxTriangles)
                            {
                                error = "The model has too many triangles.";
                                return null;
                            }
                            triangles.Add(Tri(corners[0], corners[1], corners[2]));
                        }
                        corners.Clear();
                    }
                }
            }
            if (triangles.Any(t => !Finite(t.A) || !Finite(t.B) || !Finite(t.C)))
            {
                error = "The file has points that are not numbers.";
                return null;
            }
            return new List<(string?, List<Triangle>)> { (null, triangles) };
        }

        // =================================================================
        //  Objects -> boards
        // =================================================================

        private static ImportedPiece? Build(List<(string? Name, List<Triangle> Triangles)> groups, string partName)
        {
            var solids = new List<(string? Name, Vec3 Min, Vec3 Max, bool Shaped)>();
            foreach (var (name, triangles) in groups)
            {
                var shells = Shells(triangles);
                for (int k = 0; k < shells.Count; k++)
                {
                    var (min, max) = Bounds(shells[k].Select(i => triangles[i]));
                    string? own = name == null ? null : shells.Count == 1 ? name : $"{name} {k + 1}";   // one named object in several pieces
                    solids.Add((own, min, max, !IsBox(shells[k].Select(i => triangles[i]), min, max)));
                }
            }
            if (solids.Count == 0) return null;

            // keep the biggest when there are too many, then name and order them bottom to top, left to right, back to front
            int dropped = Math.Max(0, solids.Count - PieceEditor.MaxParts);
            if (dropped > 0) solids = solids.OrderByDescending(s => Volume(s.Max - s.Min)).Take(PieceEditor.MaxParts).ToList();
            solids = solids.OrderBy(s => Math.Round(s.Min.Y, 3)).ThenBy(s => Math.Round(s.Min.X, 3)).ThenBy(s => Math.Round(s.Min.Z, 3)).ToList();

            var all = Bounds(solids.SelectMany(s => new[] { s.Min, s.Max }));
            var size = all.Max - all.Min;
            bool metres = Math.Max(size.X, Math.Max(size.Y, size.Z)) <= MetresBelow;
            double scale = metres ? 1000 : 1;

            var parts = new List<Part>();
            int n = 0;
            foreach (var s in solids)
            {
                string name = s.Name ?? $"{partName} {++n}";
                var part = new Part(name, (s.Min - all.Min) * scale, (s.Max - s.Min) * scale, PartKind.Board);
                parts.Add(PieceEditor.Sanitize(part, parts.Select(p => p.Name)));
            }
            return new ImportedPiece(parts, solids.Count(s => s.Shaped), dropped, metres);
        }

        /// <summary>
        /// Splits triangles into closed shells. Corners are welded by position. An edge with two triangles joins them; where more meet
        /// (two boards touching along an edge) each triangle is joined to the one it turns into first going round the edge on the
        /// inside of its own solid, so boards that only touch stay apart.
        /// </summary>
        public static List<List<int>> Shells(IReadOnlyList<Triangle> triangles)
        {
            var ids = new Dictionary<(long, long, long), int>();
            int Id(Vec3 p)
            {
                var key = ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000), (long)Math.Round(p.Z * 1000));
                if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
                return id;
            }
            var corners = triangles.Select(t => (Id(t.A), Id(t.B), Id(t.C))).ToList();

            // directed edge -> triangles that run along it in that direction
            var edges = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < corners.Count; i++)
            {
                var (a, b, c) = corners[i];
                if (a == b || b == c || c == a) continue;   // a triangle with no area joins nothing
                foreach (var e in new[] { (a, b), (b, c), (c, a) })
                {
                    if (!edges.TryGetValue(e, out var list)) edges[e] = list = new List<int>();
                    list.Add(i);
                }
            }

            var parent = Enumerable.Range(0, triangles.Count).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            void Join(int x, int y) => parent[Find(x)] = Find(y);

            foreach (var ((a, b), forward) in edges)
            {
                var backward = edges.TryGetValue((b, a), out var back) ? back : null;
                int count = forward.Count + (backward?.Count ?? 0);
                if (count == 2)
                {
                    // two triangles on one edge belong together, even when one of them is wound the wrong way round
                    if (backward == null) Join(forward[0], forward[1]);
                    else Join(forward[0], backward[0]);
                    continue;
                }
                if (backward == null) continue;
                foreach (int t in forward)
                {
                    int best = -1;
                    double bestTurn = double.MaxValue;
                    foreach (int other in backward)
                    {
                        double turn = Turn(triangles[t], corners[t], triangles[other], corners[other], a, b);
                        if (turn < bestTurn) { bestTurn = turn; best = other; }
                    }
                    if (best >= 0) Join(t, best);
                }
            }

            // triangles with no area (two corners welded together) are left out: alone they would become boards of their own
            return Enumerable.Range(0, triangles.Count).Where(i => corners[i].Item1 != corners[i].Item2 && corners[i].Item2 != corners[i].Item3 && corners[i].Item3 != corners[i].Item1)
                .GroupBy(Find).Select(g => g.ToList()).ToList();
        }

        /// <summary>How far (radians, 0..2pi) triangle t must turn round its edge a-b, towards its inside, to lie on triangle o.</summary>
        private static double Turn(Triangle t, (int, int, int) tc, Triangle o, (int, int, int) oc, int a, int b)
        {
            var pa = Corner(t, tc, a);
            var d = (Corner(t, tc, b) - pa).Normalized;
            Vec3 Wing(Triangle tri, (int A, int B, int C) ids)
            {
                var third = ids.A != a && ids.A != b ? tri.A : ids.B != a && ids.B != b ? tri.B : tri.C;
                var w = third - pa;
                return w - d * Vec3.Dot(w, d);
            }
            var wt = Wing(t, tc);
            var wo = Wing(o, oc);
            var inward = -Vec3.Cross(t.B - t.A, t.C - t.A);
            double sign = Vec3.Dot(Vec3.Cross(d, wt), inward) >= 0 ? 1 : -1;
            double angle = Math.Atan2(sign * Vec3.Dot(Vec3.Cross(wt, wo), d), Vec3.Dot(wt, wo));
            if (angle < 0) angle += 2 * Math.PI;
            return angle < 1e-6 ? 2 * Math.PI : angle;   // lying flat on top of it = the other side of the edge, so last
        }

        private static Vec3 Corner(Triangle t, (int A, int B, int C) ids, int id) => ids.A == id ? t.A : ids.B == id ? t.B : t.C;

        /// <summary>A plain box: every triangle lies on one of the box's six sides and together they cover its whole surface.</summary>
        private static bool IsBox(IEnumerable<Triangle> triangles, Vec3 min, Vec3 max)
        {
            var size = max - min;
            double eps = Math.Max(1e-4, 1e-6 * Math.Max(size.X, Math.Max(size.Y, size.Z)));
            double area = 0;
            foreach (var t in triangles)
            {
                bool onSide = false;
                for (int axis = 0; axis < 3 && !onSide; axis++)
                {
                    double ta = Axis(t.A, axis), tb = Axis(t.B, axis), tc = Axis(t.C, axis);
                    foreach (double plane in new[] { Axis(min, axis), Axis(max, axis) })
                        if (Math.Abs(ta - plane) < eps && Math.Abs(tb - plane) < eps && Math.Abs(tc - plane) < eps) onSide = true;
                }
                if (!onSide) return false;
                area += Vec3.Cross(t.B - t.A, t.C - t.A).Length / 2;
            }
            double box = 2 * (size.X * size.Y + size.Y * size.Z + size.X * size.Z);
            return box > 0 && Math.Abs(area - box) <= 1e-3 * box;
        }

        private static (Vec3 Min, Vec3 Max) Bounds(IEnumerable<Triangle> triangles) => Bounds(triangles.SelectMany(t => new[] { t.A, t.B, t.C }));

        private static (Vec3 Min, Vec3 Max) Bounds(IEnumerable<Vec3> points)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var p in points)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            return (new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));
        }

        private static double Axis(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static double Volume(Vec3 s) => s.X * s.Y * s.Z;

        private static Triangle Tri(Vec3 a, Vec3 b, Vec3 c) => new(a, b, c, Vec3.Cross(b - a, c - a).Normalized);

        private static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

        private static bool Num(string s, out double value) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

        private static string Short(string line) => line.Length <= 60 ? line : line[..60] + "...";
    }
}
