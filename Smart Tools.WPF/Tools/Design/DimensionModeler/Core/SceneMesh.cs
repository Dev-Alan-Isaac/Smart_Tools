using System.Globalization;
using System.Text;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Turns the scene into finished objects, one per top-level node: a shape is its own surface; a group is the union of its solids
    /// minus its holes (nested groups first); a hole, or a group of only holes, that is not inside another group cuts nothing and comes
    /// out as a ghost. Results are kept per node by content, so changing one object rebuilds only that object. Pure: no WPF.
    /// Not thread-safe: use one builder per thread (the view builds on a worker and throws away results that are no longer wanted).
    /// </summary>
    public sealed class SceneMeshBuilder
    {
        private readonly Dictionary<string, Baked> _cache = new();
        private HashSet<string> _used = new();

        /// <summary>A node's surface and whether it is a hole (a hole that is not grouped with anything stays a ghost).</summary>
        private sealed record Baked(List<CsgPolygon> Polygons, bool IsHole);

        /// <summary>
        /// The finished objects of the scene. <paramref name="includeHidden"/> = false leaves hidden shapes and groups out (the view);
        /// exports pass true, so hiding only ever changes what is on screen.
        /// </summary>
        public IReadOnlyList<ObjectMesh> Build(Scene scene, bool includeHidden = false)
        {
            _used = new HashSet<string>();
            var result = new List<ObjectMesh>();
            foreach (var node in scene.Nodes)
            {
                if (!includeHidden && node.Hidden) continue;
                var baked = Bake(node, scene.Meshes, includeHidden);
                if (baked == null || baked.Polygons.Count == 0) continue;
                // the polygons carry "material|colour" while joining; split it back into the two fields
                var triangles = Csg.Triangles(baked.Polygons).Select(t =>
                {
                    var (colour, material) = SceneMaterial.Split(t.Colour);
                    return new ColouredTriangle(t.Triangle, colour, material);
                }).ToList();
                result.Add(new ObjectMesh(node.Id, triangles, baked.IsHole, Csg.BoundsOf(baked.Polygons)));
            }
            // forget what this scene no longer has, so the cache cannot grow without end
            foreach (var key in _cache.Keys.Where(k => !_used.Contains(k)).ToList()) _cache.Remove(key);
            return result;
        }

        private Baked? Bake(SceneNode node, IReadOnlyDictionary<string, MeshData> meshes, bool includeHidden)
        {
            string key = Key(node, meshes, includeHidden);
            _used.Add(key);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            Baked? baked = node switch
            {
                SceneShape shape => new Baked(Primitives.Placed(shape, meshes).Select(p => new CsgPolygon(p, SceneMaterial.Tag(shape.Colour, shape.Material))).ToList(), shape.IsHole),
                GroupNode group => BakeGroup(group, meshes, includeHidden),
                _ => null,
            };
            if (baked != null) _cache[key] = baked;
            return baked;
        }

        private Baked? BakeGroup(GroupNode group, IReadOnlyDictionary<string, MeshData> meshes, bool includeHidden)
        {
            var solids = new List<List<CsgPolygon>>();
            var holes = new List<List<CsgPolygon>>();
            foreach (var child in group.Children)
            {
                if (!includeHidden && child.Hidden) continue;
                var baked = Bake(child, meshes, includeHidden);
                if (baked == null || baked.Polygons.Count == 0) continue;
                (baked.IsHole ? holes : solids).Add(baked.Polygons);
            }
            var holeClusters = Clusters(holes);
            if (solids.Count == 0) return new Baked(holeClusters.SelectMany(c => c.Polygons).ToList(), IsHole: true);   // only holes: one bigger hole

            var result = new List<CsgPolygon>();
            foreach (var cluster in Clusters(solids))
            {
                var polygons = cluster.Polygons;
                // the walls a hole leaves take the colour of the solid it cut (the colour covering most of that solid)
                string? wall = polygons.GroupBy(p => p.Tag).OrderByDescending(g => g.Sum(Area)).First().Key;
                foreach (var hole in holeClusters)
                    if (cluster.Bounds.Overlaps(hole.Bounds)) polygons = Csg.Subtract(polygons, hole.Polygons.Select(p => p.WithTag(HoleWall)).ToList());
                result.AddRange(polygons.Select(p => ReferenceEquals(p.Tag, HoleWall) ? p.WithTag(wall) : p));
            }
            // the group's colour and material are those of every part that has none of its own; a part's own look wins
            if (group.Colour != null || group.Material != null)
                result = result.Select(p =>
                {
                    var (colour, material) = SceneMaterial.Split(p.Tag);
                    string? tag = SceneMaterial.Tag(colour ?? group.Colour, material ?? group.Material);
                    return tag == p.Tag ? p : p.WithTag(tag);
                }).ToList();
            return new Baked(result, IsHole: false);
        }

        /// <summary>Marks the polygons a hole brings into a cut (compared by reference, so no colour text can be mistaken for it).</summary>
        private static readonly string HoleWall = new('\u0001', 1);

        private static double Area(CsgPolygon p)
        {
            var v = p.Vertices;
            var sum = Vec3.Zero;
            for (int i = 1; i + 1 < v.Count; i++) sum += Vec3.Cross(v[i] - v[0], v[i + 1] - v[0]);
            return sum.Length / 2;
        }

        /// <summary>
        /// Joins only the parts whose boxes really overlap (each cluster is one union); parts that are apart or only touch, like the
        /// boards of a cabinet, are just laid side by side. That keeps a group of many touching parts fast and free of needless cuts.
        /// </summary>
        private static List<(List<CsgPolygon> Polygons, Bounds3 Bounds)> Clusters(List<List<CsgPolygon>> parts)
        {
            var clusters = new List<(List<CsgPolygon> Polygons, Bounds3 Bounds)>();
            foreach (var part in parts)
            {
                var polygons = part;
                var bounds = Csg.BoundsOf(part);
                // merge with every cluster it overlaps (it may join two clusters into one)
                for (int i = clusters.Count - 1; i >= 0; i--)
                {
                    if (!clusters[i].Bounds.Overlaps(bounds)) continue;
                    polygons = Csg.Union(clusters[i].Polygons, polygons);
                    bounds = bounds.With(clusters[i].Bounds);
                    clusters.RemoveAt(i);
                }
                clusters.Add((polygons, bounds));
            }
            return clusters;
        }

        /// <summary>Everything that changes a node's surface, written out: equal text = equal surface.</summary>
        private static string Key(SceneNode node, IReadOnlyDictionary<string, MeshData> meshes, bool includeHidden)
        {
            var sb = new StringBuilder();
            Write(sb, node, meshes, includeHidden);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, SceneNode node, IReadOnlyDictionary<string, MeshData> meshes, bool includeHidden)
        {
            static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            static string V(Vec3 v) => $"{N(v.X)},{N(v.Y)},{N(v.Z)}";
            switch (node)
            {
                case SceneShape s:
                    sb.Append('S').Append((int)s.Kind).Append('|').Append(V(s.Position)).Append('|').Append(V(s.Size)).Append('|').Append(V(s.Rotation))
                      .Append('|').Append(s.IsHole ? 'H' : 's').Append(s.Mirrored ? 'M' : 'm').Append('|').Append(s.Colour).Append('/').Append(s.Material)
                      .Append('|').Append(s.Segments).Append('|').Append(N(s.Inner));
                    if (s.MeshId != null)
                        sb.Append('|').Append(s.MeshId).Append(':').Append(meshes.TryGetValue(s.MeshId, out var m) ? m.Points.Count : -1);
                    break;
                case GroupNode g:
                    sb.Append("G(").Append(g.Colour).Append('/').Append(g.Material);
                    foreach (var child in g.Children)
                    {
                        if (!includeHidden && child.Hidden) continue;
                        sb.Append(';');
                        Write(sb, child, meshes, includeHidden);
                    }
                    sb.Append(')');
                    break;
            }
        }
    }
}
