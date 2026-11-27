using System.Globalization;
using System.IO;
using System.Text;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// An OBJ or STL file read as exact Mesh shapes: one per separate object, with the surfaces they use. <see cref="FromMetres"/> = the
    /// file was so small it was read as metres and scaled to mm. <see cref="Dropped"/> = objects left out over the shape limit.
    /// </summary>
    public sealed record ImportedMeshes(IReadOnlyList<SceneShape> Shapes, IReadOnlyDictionary<string, MeshData> Meshes, bool FromMetres, int Dropped);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Writing finished objects as OBJ / STL, and reading OBJ / STL files as exact Mesh shapes. Pure: no WPF.</summary>
    public static class SceneFiles
    {
        private static string Num(double value)
        {
            string s = value.ToString("0.####", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        /// <summary>Wavefront OBJ, millimetres, Y up: one group per object (named after it), plain triangles.</summary>
        public static string ToObj(string title, IEnumerable<(string Name, IReadOnlyList<ColouredTriangle> Triangles)> objects, double scale = 1)
        {
            var sb = new StringBuilder();
            sb.Append("# Smart Tools - 3D Modeler\n# ").Append(title).Append("\n# units: millimetres (1 unit = 1 mm), Y is up\n");
            int offset = 0;
            foreach (var (name, triangles) in objects)
            {
                sb.Append("g ").Append(string.IsNullOrWhiteSpace(name) ? "object" : name.Trim().Replace(' ', '_')).Append('\n');
                foreach (var ct in triangles)
                    foreach (var v in new[] { ct.Triangle.A, ct.Triangle.B, ct.Triangle.C })
                        sb.Append("v ").Append(Num(v.X * scale)).Append(' ').Append(Num(v.Y * scale)).Append(' ').Append(Num(v.Z * scale)).Append('\n');
                for (int i = 0; i < triangles.Count; i++)
                    sb.Append("f ").Append(offset + 3 * i + 1).Append(' ').Append(offset + 3 * i + 2).Append(' ').Append(offset + 3 * i + 3).Append('\n');
                offset += 3 * triangles.Count;
            }
            return sb.ToString();
        }

        /// <summary>Binary STL, Z up (what slicers expect), moved so the design starts at 0, 0, 0: Y (up) becomes Z, Z (depth) becomes -Y.</summary>
        public static byte[] ToStl(IEnumerable<Triangle> triangles, double scale = 1)
        {
            var list = triangles.ToList();
            var b = Bounds3.Of(list.SelectMany(t => new[] { t.A, t.B, t.C }));
            Vec3 Up(Vec3 p) => b.IsEmpty ? p : new Vec3(p.X - b.Min.X, b.Max.Z - p.Z, p.Y - b.Min.Y) * scale;

            using var stream = new MemoryStream(84 + 50 * list.Count);
            using var writer = new BinaryWriter(stream);
            var header = new byte[80];
            Encoding.ASCII.GetBytes("Smart Tools 3D Modeler (millimetres)").CopyTo(header, 0);
            writer.Write(header);
            writer.Write((uint)list.Count);
            foreach (var t in list)
            {
                var a = Up(t.A);
                var c1 = Up(t.B);
                var c2 = Up(t.C);
                var n = Vec3.Cross(c1 - a, c2 - a).Normalized;
                foreach (var v in new[] { n, a, c1, c2 })
                {
                    writer.Write((float)v.X);
                    writer.Write((float)v.Y);
                    writer.Write((float)v.Z);
                }
                writer.Write((ushort)0);
            }
            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>
        /// Reads an OBJ or STL file as Mesh shapes, one per separate object (an OBJ group, or a closed shell), each keeping its exact
        /// surface. The whole import stands on the floor centred on X and Z; <paramref name="name"/> names objects that have no name.
        /// </summary>
        public static bool TryImport(string fileName, byte[] data, string name, out ImportedMeshes imported, out string error)
        {
            imported = new ImportedMeshes(Array.Empty<SceneShape>(), new Dictionary<string, MeshData>(), false, 0);
            error = "";
            if (data.LongLength > PieceImport.MaxBytes)
            {
                error = "The file is too big.";
                return false;
            }
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            var groups = ext switch
            {
                ".obj" => PieceImport.ReadObj(Encoding.UTF8.GetString(data), out error),
                ".stl" => PieceImport.ReadStl(data, out error),
                _ => null,
            };
            if (groups == null)
            {
                if (error.Length == 0) error = "Only OBJ and STL files can be imported.";
                return false;
            }

            // each separate object: its own triangles
            var objects = new List<(string? Name, List<Triangle> Triangles)>();
            foreach (var (groupName, triangles) in groups)
            {
                var shells = JoinOpenShells(triangles, PieceImport.Shells(triangles));
                for (int k = 0; k < shells.Count; k++)
                    objects.Add((groupName == null ? null : shells.Count == 1 ? groupName : $"{groupName} {k + 1}", shells[k].Select(i => triangles[i]).ToList()));
            }
            objects.RemoveAll(o => o.Triangles.Count == 0);
            if (objects.Count == 0)
            {
                error = "The file has no surfaces in it.";
                return false;
            }
            int dropped = Math.Max(0, objects.Count - SceneEdit.MaxShapes);
            if (dropped > 0) objects = objects.OrderByDescending(o => Bounds3.Of(o.Triangles.SelectMany(Corners)).Size.Length).Take(SceneEdit.MaxShapes).ToList();

            var all = Bounds3.Of(objects.SelectMany(o => o.Triangles.SelectMany(Corners)));
            var size = all.Size;
            bool metres = Math.Max(size.X, Math.Max(size.Y, size.Z)) <= PieceImport.MetresBelow;
            double scale = metres ? 1000 : 1;
            // stand the whole import on the floor, centred on X and Z
            var shift = new Vec3(-(all.Min.X + all.Max.X) / 2, -all.Min.Y, -(all.Min.Z + all.Max.Z) / 2);

            var shapes = new List<SceneShape>();
            var meshes = new Dictionary<string, MeshData>();
            int n = 0;
            foreach (var (objName, triangles) in objects)
            {
                var b = Bounds3.Of(triangles.SelectMany(Corners));
                var s = b.Size;
                var centre = b.Centre;
                // the surface in the unit box: -0.5..0.5 on every side (a flat side stays flat)
                static double U(double v, double c, double len) => len < 1e-12 ? 0 : (v - c) / len;
                var points = new List<Vec3>(triangles.Count * 3);
                foreach (var t in triangles)
                    foreach (var p in Corners(t))
                        points.Add(new Vec3(U(p.X, centre.X, s.X), U(p.Y, centre.Y, s.Y), U(p.Z, centre.Z, s.Z)));
                string meshId = SceneEdit.NewId();
                meshes[meshId] = new MeshData(meshId, points);
                var shapeSize = new Vec3(Math.Max(s.X * scale, SceneEdit.MinSize), Math.Max(s.Y * scale, SceneEdit.MinSize), Math.Max(s.Z * scale, SceneEdit.MinSize));
                shapes.Add(SceneEdit.Sanitize(new SceneShape(SceneEdit.NewId(), objName ?? $"{name} {++n}", ShapeKind.Mesh, (centre + shift) * scale, shapeSize, Vec3.Zero, MeshId: meshId)));
            }
            if (shapes.Count == 1 && objects[0].Name == null) shapes[0] = shapes[0] with { Name = name };
            imported = new ImportedMeshes(shapes, meshes, metres, dropped);
            return true;
        }

        /// <summary>
        /// Puts back together an object that the shell split cut into pieces. Cut (CSG) surfaces, from this tool or any CAD program,
        /// have seams where an edge of one triangle meets the middle of another, so their triangles do not all share edges. A piece
        /// that is not closed (an edge with no partner) cannot be a whole object, so open pieces that share a corner are joined.
        /// Closed shells (whole boards, even touching at a corner) are left apart.
        /// </summary>
        public static List<List<int>> JoinOpenShells(IReadOnlyList<Triangle> triangles, List<List<int>> shells)
        {
            var ids = new Dictionary<(long, long, long), int>();
            int Id(Vec3 p)
            {
                var key = ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000), (long)Math.Round(p.Z * 1000));
                if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
                return id;
            }
            var corners = triangles.Select(t => (Id(t.A), Id(t.B), Id(t.C))).ToList();

            bool Closed(List<int> shell)
            {
                var edges = new Dictionary<(int, int), int>();
                foreach (int i in shell)
                {
                    var (a, b, c) = corners[i];
                    foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                    {
                        if (x == y) continue;
                        edges[(x, y)] = edges.GetValueOrDefault((x, y)) + 1;
                    }
                }
                return edges.All(e => edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == e.Value);
            }

            var open = shells.Where(s => !Closed(s)).ToList();
            if (open.Count < 2) return shells;
            var result = shells.Where(s => !open.Contains(s)).ToList();

            // join open pieces that share any corner
            var parent = Enumerable.Range(0, open.Count).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            var owner = new Dictionary<int, int>();
            for (int s = 0; s < open.Count; s++)
                foreach (int i in open[s])
                {
                    var (a, b, c) = corners[i];
                    foreach (int v in new[] { a, b, c })
                    {
                        if (owner.TryGetValue(v, out int other)) parent[Find(s)] = Find(other);
                        else owner[v] = s;
                    }
                }
            result.AddRange(Enumerable.Range(0, open.Count).GroupBy(Find).Select(g => g.SelectMany(s => open[s]).ToList()));
            return result;
        }

        private static IEnumerable<Vec3> Corners(Triangle t)
        {
            yield return t.A;
            yield return t.B;
            yield return t.C;
        }
    }

    /// <summary>
    /// The design the modeler shows, kept in the settings. Settings from before the shape builder are moved over once: a custom piece
    /// becomes shapes (with its colours); otherwise the preset that was open is inserted as an example, so the page is not empty.
    /// The old fields are left as they were, so nothing is lost.
    /// </summary>
    public static class ModelerSceneStore
    {
        public static Scene Load(ModelerSettings settings, string defaultName)
        {
            if (settings.Scene != null && SceneJson.TryFromDto(settings.Scene, out var saved, out _)) return saved;

            if (settings.CustomParts.Count > 0)
            {
                var parts = CustomPieceJson.FromDtos(settings.CustomParts);
                settings.Paint.TryGetValue("custom", out var paint);
                string name = string.IsNullOrWhiteSpace(settings.CustomName) ? defaultName : settings.CustomName;
                return Scene.Empty(name) with { Nodes = LegacyPieces.FromParts(parts, paint) };
            }
            if (SceneExamples.Find(settings.Template) is { } example)
            {
                settings.Values.TryGetValue(example.Id, out var values);
                return Scene.Empty(example.Name) with { Nodes = new[] { example.Build(values) } };
            }
            return Scene.Empty(defaultName);
        }

        public static void Save(ModelerSettings settings, Scene scene) => settings.Scene = SceneJson.ToDto(scene);
    }
}
