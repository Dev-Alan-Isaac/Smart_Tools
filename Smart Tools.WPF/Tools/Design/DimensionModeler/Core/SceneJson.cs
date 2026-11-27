using System.Text.Json;
using System.Text.Json.Serialization;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>A design as it is written to a .dmodel file (version 2) or kept in the settings. Plain values, no WPF.</summary>
    public sealed class SceneFileDto
    {
        public int Version { get; set; } = Smart_Tools.WPF.Controllers.SceneJson.CurrentVersion;
        public string Name { get; set; } = "";
        public List<SceneNodeDto> Nodes { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<MeshDto>? Meshes { get; set; }
    }

    /// <summary>One shape ("shape") or group ("group"). Defaults are left out of the file.</summary>
    public sealed class SceneNodeDto
    {
        public string Type { get; set; } = "shape";
        public string? Id { get; set; }
        public string Name { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Kind { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double[]? Position { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double[]? Size { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double[]? Rotation { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Hole { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Colour { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Segments { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Inner { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mesh { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Mirrored { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Hidden { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Locked { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<SceneNodeDto>? Children { get; set; }
    }

    /// <summary>An imported surface: its points as little-endian 32-bit floats (x, y, z, three points per triangle), base64.</summary>
    public sealed class MeshDto
    {
        public string Id { get; set; } = "";
        public string Points { get; set; } = "";
    }
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Reading and writing designs. Version 2 holds shapes and groups; version 1 files (boards with round holes and angled cuts, from
    /// before the shape builder) are read too and turned into shapes by <see cref="LegacyPieces"/>. Whatever is read is made safe.
    /// </summary>
    public static class SceneJson
    {
        public const int CurrentVersion = 2;

        /// <summary>Imported surfaces larger than this (points) are refused when reading, so a broken file cannot fill the memory.</summary>
        public const int MaxMeshPoints = 9_000_000;

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static string ToJson(Scene scene) => JsonSerializer.Serialize(ToDto(scene), Options);

        public static SceneFileDto ToDto(Scene scene)
        {
            var used = SceneEdit.All(scene).OfType<SceneShape>().Select(s => s.MeshId).OfType<string>().ToHashSet();
            var meshes = scene.Meshes.Values.Where(m => used.Contains(m.Id)).Select(m => new MeshDto { Id = m.Id, Points = Pack(m.Points) }).ToList();
            return new SceneFileDto { Name = scene.Name, Nodes = scene.Nodes.Select(ToDto).ToList(), Meshes = meshes.Count == 0 ? null : meshes };
        }

        private static SceneNodeDto ToDto(SceneNode node) => node switch
        {
            SceneShape s => new SceneNodeDto
            {
                Type = "shape", Id = s.Id, Name = s.Name, Kind = s.Kind.ToString(),
                Position = Arr(s.Position), Size = Arr(s.Size), Rotation = s.Rotation == Vec3.Zero ? null : Arr(s.Rotation),
                Hole = s.IsHole, Colour = s.Colour,
                Segments = s.Kind is ShapeKind.Cylinder or ShapeKind.Cone or ShapeKind.Sphere or ShapeKind.Tube && s.Segments != 32 ? s.Segments : null,
                Inner = s.Kind == ShapeKind.Tube ? s.Inner : null, Mesh = s.MeshId,
                Mirrored = s.Mirrored, Hidden = s.Hidden, Locked = s.Locked,
            },
            GroupNode g => new SceneNodeDto
            {
                Type = "group", Id = g.Id, Name = g.Name, Colour = g.Colour, Hidden = g.Hidden, Locked = g.Locked,
                Children = g.Children.Select(ToDto).ToList(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };

        /// <summary>Reads a design file of any version. A version 1 file (it has "Parts") goes through the old reader and is converted.</summary>
        public static bool TryRead(string? json, out Scene scene, out string error)
        {
            scene = Scene.Empty(CustomPieceJson.DefaultName);
            error = "";
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The file is empty.";
                return false;
            }
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        error = "The file has no design in it.";
                        return false;
                    }
                    if (doc.RootElement.TryGetProperty("Parts", out _) && !doc.RootElement.TryGetProperty("Nodes", out _))
                    {
                        if (!CustomPieceJson.TryRead(json, out string oldName, out var parts, out var paint, out error)) return false;
                        scene = Scene.Empty(oldName) with { Nodes = LegacyPieces.FromParts(parts, paint) };
                        return true;
                    }
                }
                var dto = JsonSerializer.Deserialize<SceneFileDto>(json, Options);
                if (dto == null)
                {
                    error = "The file has no design in it.";
                    return false;
                }
                if (dto.Version > CurrentVersion)
                {
                    error = "This design was saved by a newer version of Smart Tools.";
                    return false;
                }
                return TryFromDto(dto, out scene, out error);
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>A design from its stored form: ids made unique, names and numbers made safe, at most <see cref="SceneEdit.MaxShapes"/> shapes.</summary>
        public static bool TryFromDto(SceneFileDto dto, out Scene scene, out string error)
        {
            error = "";
            var meshes = new Dictionary<string, MeshData>();
            foreach (var m in dto.Meshes ?? new List<MeshDto>())
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Id) || meshes.ContainsKey(m.Id)) continue;
                if (!TryUnpack(m.Points, out var points))
                {
                    scene = Scene.Empty(CustomPieceJson.DefaultName);
                    error = "An imported model inside the file is damaged.";
                    return false;
                }
                meshes[m.Id] = new MeshData(m.Id, points);
            }
            var ids = new HashSet<string>();
            int shapes = 0;
            SceneNode? Read(SceneNodeDto? d)
            {
                if (d == null) return null;
                string id = !string.IsNullOrWhiteSpace(d.Id) && ids.Add(d.Id) ? d.Id : Take(SceneEdit.NewId());
                string name = string.IsNullOrWhiteSpace(d.Name) ? (d.Type == "group" ? "Group" : "Shape") : d.Name.Trim();
                if (string.Equals(d.Type, "group", StringComparison.OrdinalIgnoreCase))
                {
                    var kids = (d.Children ?? new List<SceneNodeDto>()).Select(Read).OfType<SceneNode>().ToList();
                    if (kids.Count == 0) return null;
                    return new GroupNode(id, name, kids, PiecePaint.Normalize(d.Colour), d.Hidden, d.Locked);
                }
                if (shapes >= SceneEdit.MaxShapes) return null;
                shapes++;
                var kind = Enum.TryParse<ShapeKind>(d.Kind, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : ShapeKind.Box;
                string? meshId = kind == ShapeKind.Mesh && d.Mesh != null && meshes.ContainsKey(d.Mesh) ? d.Mesh : null;
                if (kind == ShapeKind.Mesh && meshId == null) kind = ShapeKind.Box;   // its surface is missing: keep its place as a box
                return SceneEdit.Sanitize(new SceneShape(id, name, kind, Vec(d.Position, 0), Vec(d.Size, 10), Vec(d.Rotation, 0),
                    d.Hole, d.Colour, d.Segments ?? 32, d.Inner ?? 0.6, meshId, d.Mirrored, d.Hidden, d.Locked));
            }
            string Take(string id)
            {
                ids.Add(id);
                return id;
            }
            var nodes = (dto.Nodes ?? new List<SceneNodeDto>()).Select(Read).OfType<SceneNode>().ToList();
            string sceneName = string.IsNullOrWhiteSpace(dto.Name) ? CustomPieceJson.DefaultName : dto.Name.Trim();
            scene = new Scene(sceneName, nodes, meshes);
            return true;
        }

        private static double[] Arr(Vec3 v) => new[] { v.X, v.Y, v.Z };

        private static Vec3 Vec(double[]? a, double fallback) =>
            a is { Length: 3 } ? new Vec3(a[0], a[1], a[2]) : new Vec3(fallback, fallback, fallback);

        public static string Pack(IReadOnlyList<Vec3> points)
        {
            var bytes = new byte[points.Count * 12];
            for (int i = 0; i < points.Count; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 12), (float)points[i].X);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 12 + 4), (float)points[i].Y);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 12 + 8), (float)points[i].Z);
            }
            return Convert.ToBase64String(bytes);
        }

        public static bool TryUnpack(string? text, out IReadOnlyList<Vec3> points)
        {
            points = Array.Empty<Vec3>();
            if (string.IsNullOrEmpty(text)) return false;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return false;
            }
            if (bytes.Length % 36 != 0 || bytes.Length / 12 > MaxMeshPoints) return false;
            var list = new Vec3[bytes.Length / 12];
            for (int i = 0; i < list.Length; i++)
            {
                var p = new Vec3(BitConverter.ToSingle(bytes, i * 12), BitConverter.ToSingle(bytes, i * 12 + 4), BitConverter.ToSingle(bytes, i * 12 + 8));
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)) return false;
                list[i] = p;
            }
            points = list;
            return true;
        }
    }

    /// <summary>
    /// Boards from before the shape builder (and the furniture examples, which are still written as boards) turned into shapes:
    /// a board becomes a box in its wood colour (or the colour it was given); a round hole becomes a cylinder hole running through the
    /// board on its own axis; an angled cut (bevel or miter) becomes a wedge hole that takes exactly that strip off. A board with holes
    /// or cuts becomes a group named after it, and boards that shared a group name (a drawer) share a group again.
    /// </summary>
    public static class LegacyPieces
    {
        /// <summary>How far a cutting shape reaches past the faces it cuts through, so the cut never lies exactly on a face.</summary>
        public const double Overshoot = 1;

        public static readonly IReadOnlyDictionary<PartKind, string> Wood = new Dictionary<PartKind, string>
        {
            [PartKind.Top] = "#DEB07E",
            [PartKind.Board] = "#CC9E6C",
            [PartKind.Leg] = "#AA7C52",
            [PartKind.Panel] = "#BA8C60",
        };

        public static List<SceneNode> FromParts(IReadOnlyList<Part> parts, IReadOnlyDictionary<string, BoardPaint>? paint = null)
        {
            var nodes = new List<SceneNode>();
            var groups = new Dictionary<string, List<SceneNode>>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                var node = FromPart(part, paint);
                if (part.Group is { Length: > 0 } g)
                {
                    if (!groups.TryGetValue(g, out var members))
                    {
                        groups[g] = members = new List<SceneNode>();
                        nodes.Add(new GroupNode("pending:" + g, g, members));   // placeholder, filled below, where the first member was
                    }
                    members.Add(node);
                }
                else nodes.Add(node);
            }
            return nodes.Select(n => n is GroupNode { Id: var id } p && id.StartsWith("pending:", StringComparison.Ordinal)
                ? (p.Children.Count == 1 ? p.Children[0] : new GroupNode(SceneEdit.NewId(), p.Name, p.Children.ToList()))
                : n).ToList();
        }

        public static SceneNode FromPart(Part part, IReadOnlyDictionary<string, BoardPaint>? paint = null)
        {
            string colour = paint != null && paint.TryGetValue(part.Name, out var p) && PiecePaint.Normalize(p.Color) is { } own ? own : Wood[part.Kind];
            var box = new SceneShape(SceneEdit.NewId(), part.Name, ShapeKind.Box, part.Min + part.Size * 0.5, part.Size, Vec3.Zero, Colour: colour);
            var cutters = new List<SceneNode>();
            int n = 0;
            foreach (var hole in part.Holes) cutters.Add(HoleShape(part, hole, $"{part.Name} hole {++n}"));
            n = 0;
            foreach (var bevel in PieceBevels.Sanitize(part.Size, part.Bevels)) cutters.Add(BevelShape(part, bevel, $"{part.Name} cut {++n}"));
            if (cutters.Count == 0) return box;
            return new GroupNode(SceneEdit.NewId(), part.Name, new SceneNode[] { box with { Name = $"{part.Name} body" } }.Concat(cutters).ToList());
        }

        /// <summary>A cylinder hole through the board along the hole's axis, a little longer than the board is thick.</summary>
        public static SceneShape HoleShape(Part part, Hole hole, string name)
        {
            double length = Get(part.Size, hole.Axis) + 2 * Overshoot;
            var centre = part.Min + part.Size * 0.5;
            Vec3 position;
            Vec3 rotation;
            switch (hole.Axis)
            {
                case 0:   // through X: U is Y, V is Z
                    position = new Vec3(centre.X, part.Min.Y + hole.U, part.Min.Z + hole.V);
                    rotation = new Vec3(0, 0, -90);
                    break;
                case 1:   // through Y: U is X, V is Z
                    position = new Vec3(part.Min.X + hole.U, centre.Y, part.Min.Z + hole.V);
                    rotation = Vec3.Zero;
                    break;
                default:  // through Z: U is X, V is Y
                    position = new Vec3(part.Min.X + hole.U, part.Min.Y + hole.V, centre.Z);
                    rotation = new Vec3(90, 0, 0);
                    break;
            }
            return new SceneShape(SceneEdit.NewId(), name, ShapeKind.Cylinder, position, new Vec3(hole.Diameter, length, hole.Diameter), rotation, IsHole: true);
        }

        /// <summary>
        /// A wedge hole that removes the strip of a bevel: the material where (distance in from face B) / intoA + (distance in from
        /// face A) / intoB &lt; 1. The wedge's square corner sits on the edge (moved out by the overshoot), its two legs run in along the
        /// two faces, and its slope is exactly the bevel's cut.
        /// </summary>
        public static SceneShape BevelShape(Part part, Bevel bevel, string name)
        {
            int a = PieceBevels.AxisOf(bevel.FaceA), c = PieceBevels.AxisOf(bevel.FaceB);
            int e = 3 - a - c;
            var (intoA, intoB) = PieceBevels.Setbacks(bevel);
            var da = Unit(a) * (PieceBevels.IsHigh(bevel.FaceA) ? -1 : 1);   // into the material from face A
            var dc = Unit(c) * (PieceBevels.IsHigh(bevel.FaceB) ? -1 : 1);   // into the material from face B
            var de = Vec3.Cross(dc, da);                                       // so the turn has no mirror in it
            double o = Overshoot;
            double k = 1 + o / intoA + o / intoB;                               // longer legs, same slope, from a corner moved out by o
            var size = new Vec3(Get(part.Size, e) + 2 * o, intoA * k, intoB * k);

            double faceA = PieceBevels.IsHigh(bevel.FaceA) ? Get(part.Max, a) : Get(part.Min, a);
            double faceB = PieceBevels.IsHigh(bevel.FaceB) ? Get(part.Max, c) : Get(part.Min, c);
            var edge = Unit(a) * faceA + Unit(c) * faceB + Unit(e) * (Get(part.Min, e) + Get(part.Size, e) / 2);
            var corner = edge - da * o - dc * o;
            // the wedge's square corner is at (y, z) = (-0.5, -0.5) of its own box, legs along its +Y and +Z
            var turn = Mat3.FromColumns(de, dc, da);
            var position = corner + turn * new Vec3(0, size.Y / 2, size.Z / 2);
            return new SceneShape(SceneEdit.NewId(), name, ShapeKind.Wedge, position, size, turn.ToEuler(), IsHole: true);
        }

        private static double Get(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static Vec3 Unit(int axis) => axis == 0 ? new Vec3(1, 0, 0) : axis == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
    }
}
