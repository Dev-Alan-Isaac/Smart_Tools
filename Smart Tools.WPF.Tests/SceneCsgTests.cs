using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Shapes, their placement, and the boolean engine, checked by exact volumes.</summary>
    public class SceneCsgTests
    {
        internal static SceneShape S(ShapeKind kind, Vec3 position, Vec3 size, Vec3? rotation = null, bool hole = false, int segments = 32) =>
            new(SceneEdit.NewId(), kind.ToString(), kind, position, size, rotation ?? Vec3.Zero, IsHole: hole, Segments: segments);

        internal static List<CsgPolygon> Polys(SceneShape s) => Primitives.Placed(s, null).Select(p => new CsgPolygon(p, s.Colour)).ToList();

        internal static double Vol(IEnumerable<CsgPolygon> polys) => Csg.Volume(Csg.Triangles(polys));

        /// <summary>The area of a regular n-gon that fits a circle of this radius (what a round shape really is).</summary>
        internal static double NGon(int n, double r) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

        [Theory]
        [InlineData(ShapeKind.Box, 1.0)]
        [InlineData(ShapeKind.Wedge, 0.5)]
        public void Flat_sided_unit_shapes_have_their_exact_volume(ShapeKind kind, double volume) =>
            Assert.Equal(volume, Vol(Polys(S(kind, Vec3.Zero, new Vec3(1, 1, 1)))), 9);

        [Theory]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(32)]
        public void Round_unit_shapes_have_the_volume_of_their_polygon(int n)
        {
            double a = NGon(n, 0.5);
            Assert.Equal(a, Vol(Polys(S(ShapeKind.Cylinder, Vec3.Zero, new Vec3(1, 1, 1), segments: n))), 9);
            Assert.Equal(a / 3, Vol(Polys(S(ShapeKind.Cone, Vec3.Zero, new Vec3(1, 1, 1), segments: n))), 9);
            var tube = new SceneShape("t", "Tube", ShapeKind.Tube, Vec3.Zero, new Vec3(1, 1, 1), Vec3.Zero, Segments: n, Inner: 0.5);
            Assert.Equal(a - NGon(n, 0.25), Vol(Polys(tube)), 9);
        }

        [Fact]
        public void A_sphere_is_close_to_a_ball_and_faces_outward()
        {
            double v = Vol(Polys(S(ShapeKind.Sphere, Vec3.Zero, new Vec3(1, 1, 1))));
            Assert.InRange(v, Math.PI / 6 * 0.97, Math.PI / 6);
        }

        [Theory]
        [InlineData(0, 0, 0, false)]
        [InlineData(30, 45, 60, false)]
        [InlineData(90, 0, 0, true)]
        [InlineData(-17, 123, 5, true)]
        public void Scaling_turning_and_mirroring_keep_the_volume_and_the_surface_outward(double rx, double ry, double rz, bool mirrored)
        {
            var wedge = S(ShapeKind.Wedge, new Vec3(10, 20, 30), new Vec3(40, 50, 60), new Vec3(rx, ry, rz)) with { Mirrored = mirrored };
            Assert.Equal(0.5 * 40 * 50 * 60, Vol(Polys(wedge)), 6);
        }

        [Fact]
        public void A_turned_cylinder_has_a_tight_box()
        {
            var b = Primitives.Bounds(S(ShapeKind.Cylinder, new Vec3(0, 0, 0), new Vec3(20, 100, 20), new Vec3(90, 0, 0)), null);
            Assert.Equal(20, b.Size.X, 6);
            Assert.Equal(20, b.Size.Y, 6);
            Assert.Equal(100, b.Size.Z, 6);
        }

        [Fact]
        public void Euler_angles_come_back_from_their_matrix()
        {
            foreach (var r in new[] { new Vec3(10, 20, 30), new Vec3(-170, 89.9, 45), new Vec3(0, 90, 0), new Vec3(30, -90, 0), new Vec3(180, 0, 0) })
            {
                var m = Mat3.FromEuler(r);
                var back = Mat3.FromEuler(m.ToEuler());
                foreach (var v in new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) })
                    Assert.True(((m * v) - (back * v)).Length < 1e-6, $"{r}: {m * v} vs {back * v}");
            }
        }

        [Fact]
        public void A_hole_right_through_a_box_takes_out_exactly_its_prism()
        {
            var box = Polys(S(ShapeKind.Box, Vec3.Zero, new Vec3(100, 100, 100)));
            var hole = Polys(S(ShapeKind.Cylinder, Vec3.Zero, new Vec3(20, 120, 20), new Vec3(90, 0, 0)));
            Assert.Equal(1e6 - NGon(32, 10) * 100, Vol(Csg.Subtract(box, hole)), 3);
        }

        [Fact]
        public void A_blind_pocket_takes_out_only_its_depth()
        {
            // 100 x 20 x 100 board, a 40 x 40 pocket from the top, 10 deep (the hole sticks 1 mm out of the top)
            var board = Polys(S(ShapeKind.Box, new Vec3(0, 10, 0), new Vec3(100, 20, 100)));
            var pocket = Polys(S(ShapeKind.Box, new Vec3(0, 15.5, 0), new Vec3(40, 11, 40)));
            Assert.Equal(200_000 - 16_000, Vol(Csg.Subtract(board, pocket)), 3);
        }

        [Fact]
        public void A_hole_flush_with_a_face_still_cuts_cleanly()
        {
            var board = Polys(S(ShapeKind.Box, new Vec3(0, 10, 0), new Vec3(100, 20, 100)));
            var flush = Polys(S(ShapeKind.Box, new Vec3(0, 15, 0), new Vec3(40, 10, 40)));   // top of the hole = top of the board
            Assert.Equal(200_000 - 16_000, Vol(Csg.Subtract(board, flush)), 3);
        }

        [Fact]
        public void Union_and_intersection_of_overlapping_boxes()
        {
            var a = Polys(S(ShapeKind.Box, Vec3.Zero, new Vec3(100, 100, 100)));
            var b = Polys(S(ShapeKind.Box, new Vec3(50, 0, 0), new Vec3(100, 100, 100)));
            Assert.Equal(1.5e6, Vol(Csg.Union(a, b)), 3);
            Assert.Equal(0.5e6, Vol(Csg.Intersect(a, b)), 3);
            Assert.Equal(0.5e6, Vol(Csg.Subtract(a, b)), 3);
        }

        [Fact]
        public void Boxes_that_only_touch_are_laid_side_by_side()
        {
            var a = Polys(S(ShapeKind.Box, new Vec3(0, 50, 0), new Vec3(100, 100, 100)));
            var b = Polys(S(ShapeKind.Box, new Vec3(0, 150, 0), new Vec3(100, 100, 100)));
            var u = Csg.Union(a, b);
            Assert.Equal(12, u.Count);
            Assert.Equal(2e6, Vol(u), 3);
        }

        [Fact]
        public void A_turned_wedge_cut_off_a_corner()
        {
            // a 45 degree turned box hole cuts a corner of a 100 mm cube: the cut-off part is a triangular prism
            var cube = Polys(S(ShapeKind.Box, new Vec3(50, 50, 50), new Vec3(100, 100, 100)));
            var cutter = Polys(S(ShapeKind.Box, new Vec3(100, 100, 50), new Vec3(Math.Sqrt(2) * 40, Math.Sqrt(2) * 40, 120), new Vec3(0, 0, 45)));
            Assert.Equal(1e6 - 0.5 * 40 * 40 * 100, Vol(Csg.Subtract(cube, cutter)), 2);
        }

        [Fact]
        public void A_mesh_shape_is_its_stored_surface_scaled()
        {
            var unit = Primitives.Box().SelectMany(p => new[] { p[0], p[1], p[2], p[0], p[2], p[3] }).ToList();
            var meshes = new Dictionary<string, MeshData> { ["m"] = new MeshData("m", unit) };
            var shape = new SceneShape("x", "Imported", ShapeKind.Mesh, Vec3.Zero, new Vec3(10, 20, 30), new Vec3(0, 30, 0), MeshId: "m");
            Assert.Equal(6000, Csg.Volume(Csg.Triangles(Primitives.Placed(shape, meshes).Select(p => new CsgPolygon(p, null)))), 6);
            Assert.Empty(Primitives.Placed(shape with { MeshId = "gone" }, meshes));
        }

        [Fact]
        public void The_walls_of_a_hole_take_the_colour_of_the_solid_it_cut()
        {
            var box = S(ShapeKind.Box, Vec3.Zero, new Vec3(100, 100, 100)) with { Colour = "#C0392B" };
            var hole = S(ShapeKind.Cylinder, Vec3.Zero, new Vec3(20, 120, 20), hole: true) with { Colour = "#2E86DE" };
            var scene = Scene.Empty("t") with { Nodes = new SceneNode[] { new GroupNode("g", "G", new SceneNode[] { box, hole }) } };
            var obj = Assert.Single(new SceneMeshBuilder().Build(scene));
            Assert.All(obj.Triangles, t => Assert.Equal("#C0392B", t.Colour));
            Assert.False(obj.IsHole);
        }
    }
}
