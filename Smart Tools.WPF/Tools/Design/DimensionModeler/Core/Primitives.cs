using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The surface of each kind of shape as flat convex polygons, counter-clockwise seen from outside, inside the unit box -0.5..0.5,
    /// and the same polygons placed in the scene for one shape. Pure: no WPF.
    /// </summary>
    public static class Primitives
    {
        public const int MinSegments = 3;
        public const int MaxSegments = 128;

        /// <summary>The shape's polygons in the scene (scaled, mirrored, turned and moved). A Mesh shape whose surface is missing has none.</summary>
        public static List<IReadOnlyList<Vec3>> Placed(SceneShape shape, IReadOnlyDictionary<string, MeshData>? meshes)
        {
            var unit = Unit(shape, meshes);
            var m = Matrix(shape);
            bool flipped = m.Determinant < 0;
            var result = new List<IReadOnlyList<Vec3>>(unit.Count);
            foreach (var poly in unit)
            {
                var placed = new Vec3[poly.Count];
                for (int i = 0; i < poly.Count; i++) placed[i] = shape.Position + m * poly[i];
                if (flipped) Array.Reverse(placed);   // a mirror turns the surface inside out: wind it back
                result.Add(placed);
            }
            return result;
        }

        /// <summary>Unit point -> scene point, without the move: Rotation * (mirror) * Size.</summary>
        public static Mat3 Matrix(SceneShape shape)
        {
            var m = Mat3.FromEuler(shape.Rotation);
            if (shape.Mirrored) m = m * Mat3.FlipX;
            return m * Mat3.Scale(shape.Size);
        }

        /// <summary>The box around the shape as it sits in the scene (from its real corners, so a turned cylinder is not oversized).</summary>
        public static Bounds3 Bounds(SceneShape shape, IReadOnlyDictionary<string, MeshData>? meshes) =>
            Bounds3.Of(Placed(shape, meshes).SelectMany(p => p));

        public static IReadOnlyList<IReadOnlyList<Vec3>> Unit(SceneShape shape, IReadOnlyDictionary<string, MeshData>? meshes)
        {
            int n = Math.Clamp(shape.Segments, MinSegments, MaxSegments);
            return shape.Kind switch
            {
                ShapeKind.Box => Box(),
                ShapeKind.Cylinder => Cylinder(n),
                ShapeKind.Cone => Cone(n),
                ShapeKind.Sphere => Sphere(n),
                ShapeKind.Wedge => Wedge(),
                ShapeKind.Tube => Tube(n, Math.Clamp(shape.Inner, 0.05, 0.95)),
                ShapeKind.Mesh => shape.MeshId != null && meshes != null && meshes.TryGetValue(shape.MeshId, out var mesh) ? FromMesh(mesh) : Array.Empty<IReadOnlyList<Vec3>>(),
                _ => Box(),
            };
        }

        private static Vec3 V(double x, double y, double z) => new(x, y, z);

        public static IReadOnlyList<IReadOnlyList<Vec3>> Box()
        {
            const double h = 0.5;
            return new IReadOnlyList<Vec3>[]
            {
                new[] { V(-h, -h, -h), V(-h, -h, h), V(-h, h, h), V(-h, h, -h) },   // left (-X)
                new[] { V(h, -h, -h), V(h, h, -h), V(h, h, h), V(h, -h, h) },       // right (+X)
                new[] { V(-h, -h, -h), V(h, -h, -h), V(h, -h, h), V(-h, -h, h) },   // bottom (-Y)
                new[] { V(-h, h, -h), V(-h, h, h), V(h, h, h), V(h, h, -h) },       // top (+Y)
                new[] { V(-h, -h, -h), V(-h, h, -h), V(h, h, -h), V(h, -h, -h) },   // back (-Z)
                new[] { V(-h, -h, h), V(h, -h, h), V(h, h, h), V(-h, h, h) },       // front (+Z)
            };
        }

        /// <summary>A ramp: full height at the back (-Z), down to nothing at the front (+Z), the slope facing up and forward.</summary>
        public static IReadOnlyList<IReadOnlyList<Vec3>> Wedge()
        {
            const double h = 0.5;
            return new IReadOnlyList<Vec3>[]
            {
                new[] { V(-h, -h, -h), V(-h, -h, h), V(-h, h, -h) },               // left
                new[] { V(h, -h, -h), V(h, h, -h), V(h, -h, h) },                  // right
                new[] { V(-h, -h, -h), V(h, -h, -h), V(h, -h, h), V(-h, -h, h) },  // bottom
                new[] { V(-h, -h, -h), V(-h, h, -h), V(h, h, -h), V(h, -h, -h) },  // back
                new[] { V(-h, h, -h), V(-h, -h, h), V(h, -h, h), V(h, h, -h) },    // slope
            };
        }

        private static (double X, double Z) Ring(int i, int n, double r)
        {
            double a = 2 * Math.PI * i / n;
            return (r * Math.Cos(a), -r * Math.Sin(a));   // counter-clockwise seen from above (+Y)
        }

        /// <summary>Upright (along Y), round in X and Z.</summary>
        public static IReadOnlyList<IReadOnlyList<Vec3>> Cylinder(int n)
        {
            var polys = new List<IReadOnlyList<Vec3>>();
            var top = new Vec3[n];
            var bottom = new Vec3[n];
            for (int i = 0; i < n; i++)
            {
                var (x, z) = Ring(i, n, 0.5);
                top[i] = V(x, 0.5, z);
                bottom[n - 1 - i] = V(x, -0.5, z);
            }
            polys.Add(top);
            polys.Add(bottom);
            for (int i = 0; i < n; i++)
            {
                var (x0, z0) = Ring(i, n, 0.5);
                var (x1, z1) = Ring(i + 1, n, 0.5);
                polys.Add(new[] { V(x0, -0.5, z0), V(x1, -0.5, z1), V(x1, 0.5, z1), V(x0, 0.5, z0) });
            }
            return polys;
        }

        /// <summary>Point at the top, round base at the bottom.</summary>
        public static IReadOnlyList<IReadOnlyList<Vec3>> Cone(int n)
        {
            var polys = new List<IReadOnlyList<Vec3>>();
            var bottom = new Vec3[n];
            for (int i = 0; i < n; i++)
            {
                var (x, z) = Ring(i, n, 0.5);
                bottom[n - 1 - i] = V(x, -0.5, z);
            }
            polys.Add(bottom);
            var apex = V(0, 0.5, 0);
            for (int i = 0; i < n; i++)
            {
                var (x0, z0) = Ring(i, n, 0.5);
                var (x1, z1) = Ring(i + 1, n, 0.5);
                polys.Add(new[] { V(x0, -0.5, z0), V(x1, -0.5, z1), apex });
            }
            return polys;
        }

        /// <summary>A pipe: a cylinder with a round hole down its middle, the hole <paramref name="inner"/> of the width.</summary>
        public static IReadOnlyList<IReadOnlyList<Vec3>> Tube(int n, double inner)
        {
            var polys = new List<IReadOnlyList<Vec3>>();
            double ro = 0.5, ri = 0.5 * inner;
            for (int i = 0; i < n; i++)
            {
                var (ox0, oz0) = Ring(i, n, ro);
                var (ox1, oz1) = Ring(i + 1, n, ro);
                var (ix0, iz0) = Ring(i, n, ri);
                var (ix1, iz1) = Ring(i + 1, n, ri);
                polys.Add(new[] { V(ox0, -0.5, oz0), V(ox1, -0.5, oz1), V(ox1, 0.5, oz1), V(ox0, 0.5, oz0) });   // outside wall
                polys.Add(new[] { V(ix1, -0.5, iz1), V(ix0, -0.5, iz0), V(ix0, 0.5, iz0), V(ix1, 0.5, iz1) });   // inside wall, facing in
                polys.Add(new[] { V(ox0, 0.5, oz0), V(ox1, 0.5, oz1), V(ix1, 0.5, iz1), V(ix0, 0.5, iz0) });     // top ring
                polys.Add(new[] { V(ox1, -0.5, oz1), V(ox0, -0.5, oz0), V(ix0, -0.5, iz0), V(ix1, -0.5, iz1) }); // bottom ring
            }
            return polys;
        }

        /// <summary>A ball: <paramref name="n"/> slices round, n / 2 bands from bottom to top.</summary>
        public static IReadOnlyList<IReadOnlyList<Vec3>> Sphere(int n)
        {
            int bands = Math.Max(2, n / 2);
            Vec3 At(int band, int i)
            {
                double phi = Math.PI * band / bands - Math.PI / 2;   // -90 (bottom) .. 90 (top)
                double r = 0.5 * Math.Cos(phi), y = 0.5 * Math.Sin(phi);
                var (x, z) = Ring(i, n, r);
                return V(x, y, z);
            }
            var polys = new List<IReadOnlyList<Vec3>>();
            for (int b = 0; b < bands; b++)
                for (int i = 0; i < n; i++)
                {
                    if (b == 0) polys.Add(new[] { V(0, -0.5, 0), At(1, i + 1), At(1, i) });
                    else if (b == bands - 1) polys.Add(new[] { At(b, i), At(b, i + 1), V(0, 0.5, 0) });
                    else polys.Add(new[] { At(b, i), At(b, i + 1), At(b + 1, i + 1), At(b + 1, i) });
                }
            return polys;
        }

        private static IReadOnlyList<IReadOnlyList<Vec3>> FromMesh(MeshData mesh)
        {
            var polys = new List<IReadOnlyList<Vec3>>(mesh.Points.Count / 3);
            for (int i = 0; i + 2 < mesh.Points.Count; i += 3)
            {
                var a = mesh.Points[i];
                var b = mesh.Points[i + 1];
                var c = mesh.Points[i + 2];
                if (Vec3.Cross(b - a, c - a).Length < 1e-12) continue;   // no area
                polys.Add(new[] { a, b, c });
            }
            return polys;
        }
    }
}
