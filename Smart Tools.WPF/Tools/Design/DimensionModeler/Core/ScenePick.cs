using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>What a line of sight hit: the object, how far along the ray, the point and the surface's outward direction there.</summary>
    public readonly record struct SceneHit(string NodeId, double Distance, Vec3 Point, Vec3 Normal);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Finding what is under the mouse: the ray is tested against each object's real triangles (so a turned or round shape is hit where
    /// it is drawn, not where the box around it is). Pure: no WPF.
    /// </summary>
    public static class ScenePick
    {
        /// <summary>The nearest object the ray hits, or null. <paramref name="skip"/> leaves objects out (hidden ones, the one being dragged).</summary>
        public static SceneHit? Pick(IEnumerable<ObjectMesh> objects, Ray3 ray, Func<ObjectMesh, bool>? skip = null)
        {
            SceneHit? best = null;
            foreach (var obj in objects)
            {
                if (skip?.Invoke(obj) == true) continue;
                if (!RayHitsBox(ray, obj.Bounds)) continue;
                foreach (var ct in obj.Triangles)
                {
                    var t = ct.Triangle;
                    if (RayTriangle(ray, t.A, t.B, t.C) is not { } d) continue;
                    if (best is { } b && d >= b.Distance) continue;
                    best = new SceneHit(obj.NodeId, d, ray.Origin + ray.Direction * d, t.Normal);
                }
            }
            return best;
        }

        /// <summary>
        /// The single shape (not the whole joined object) of <paramref name="within"/> that the ray meets first, holes included, by each
        /// shape's own surface. Used to pick one part inside a group. Hidden shapes are left out. Null when the ray misses them all.
        /// </summary>
        public static SceneHit? PickShape(Scene scene, SceneNode within, Ray3 ray)
        {
            SceneHit? best = null;
            foreach (var shape in SceneEdit.Shapes(within))
            {
                if (shape.Hidden) continue;
                var box = Primitives.Bounds(shape, scene.Meshes);
                if (box.IsEmpty || !RayHitsBox(ray, box)) continue;
                foreach (var poly in Primitives.Placed(shape, scene.Meshes))
                    for (int i = 1; i + 1 < poly.Count; i++)
                    {
                        if (RayTriangle(ray, poly[0], poly[i], poly[i + 1]) is not { } d) continue;
                        if (best is { } b && d >= b.Distance) continue;
                        var n = Vec3.Cross(poly[i] - poly[0], poly[i + 1] - poly[0]);
                        best = new SceneHit(shape.Id, d, ray.Origin + ray.Direction * d, n.Length > 1e-12 ? n * (1 / n.Length) : n);
                    }
            }
            return best;
        }

        /// <summary>The flat triangles of one shape's surface where it stands (for drawing a picked part on its own).</summary>
        public static List<Triangle> ShapeTriangles(Scene scene, SceneShape shape)
        {
            var result = new List<Triangle>();
            foreach (var poly in Primitives.Placed(shape, scene.Meshes))
                for (int i = 1; i + 1 < poly.Count; i++)
                {
                    var n = Vec3.Cross(poly[i] - poly[0], poly[i + 1] - poly[0]);
                    result.Add(new Triangle(poly[0], poly[i], poly[i + 1], n.Length > 1e-12 ? n * (1 / n.Length) : n));
                }
            return result;
        }

        /// <summary>
        /// Where a new shape dropped at the mouse should stand: on top of the surface under the mouse when that surface faces up, else
        /// on the floor (Y = 0) where the ray meets it. Null when the ray never comes down to the floor.
        /// </summary>
        public static Vec3? DropPoint(IEnumerable<ObjectMesh> objects, Ray3 ray)
        {
            if (Pick(objects, ray, o => o.IsHole) is { } hit && hit.Normal.Y > 0.5) return hit.Point;
            if (Math.Abs(ray.Direction.Y) < 1e-9) return null;
            double t = -ray.Origin.Y / ray.Direction.Y;
            return t > 0 ? ray.Origin + ray.Direction * t : null;
        }

        /// <summary>Möller-Trumbore: how far along the ray it meets the triangle (either side), or null.</summary>
        public static double? RayTriangle(Ray3 ray, Vec3 a, Vec3 b, Vec3 c)
        {
            const double eps = 1e-9;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vec3.Cross(ray.Direction, e2);
            double det = Vec3.Dot(e1, p);
            if (Math.Abs(det) < eps) return null;
            double inv = 1 / det;
            var s = ray.Origin - a;
            double u = Vec3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return null;
            var q = Vec3.Cross(s, e1);
            double v = Vec3.Dot(ray.Direction, q) * inv;
            if (v < 0 || u + v > 1) return null;
            double t = Vec3.Dot(e2, q) * inv;
            return t > eps ? t : null;
        }

        private static bool RayHitsBox(Ray3 ray, Bounds3 b) => PieceDrag.RayBox(ray, b.Min, b.Max) != null;
    }
}
