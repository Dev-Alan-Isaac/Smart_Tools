using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    public class MeshEdgesTests
    {
        private static List<Triangle> Built(SceneShape shape)
        {
            var scene = Scene.Empty("t") with { Nodes = new SceneNode[] { shape } };
            return new SceneMeshBuilder().Build(scene, includeHidden: true).Single().Triangles.Select(t => t.Triangle).ToList();
        }

        [Fact]
        public void A_box_has_its_12_edges_and_8_corners_and_no_face_diagonals()
        {
            var box = new SceneShape(SceneEdit.NewId(), "b", ShapeKind.Box, new Vec3(0, 10, 0), new Vec3(40, 20, 30), Vec3.Zero);
            var f = MeshEdges.Find(Built(box));
            Assert.Equal(12, f.Edges.Count);
            Assert.Equal(8, f.Corners.Count);
            Assert.Contains(f.Corners, c => (c - new Vec3(20, 20, 15)).Length < 1e-6);
            // every edge runs along one axis (a face diagonal would not)
            Assert.All(f.Edges, e =>
            {
                var d = e.B - e.A;
                int axes = (Math.Abs(d.X) > 1e-6 ? 1 : 0) + (Math.Abs(d.Y) > 1e-6 ? 1 : 0) + (Math.Abs(d.Z) > 1e-6 ? 1 : 0);
                Assert.Equal(1, axes);
            });
        }

        [Fact]
        public void A_round_shape_gives_its_rims_not_every_side_line()
        {
            var cyl = new SceneShape(SceneEdit.NewId(), "c", ShapeKind.Cylinder, new Vec3(0, 50, 0), new Vec3(20, 100, 20), Vec3.Zero, Segments: 32);
            var f = MeshEdges.Find(Built(cyl));
            Assert.Equal(64, f.Edges.Count);   // 32 around the top rim + 32 around the bottom rim
            Assert.All(f.Corners, c => Assert.True(Math.Abs(c.Y) < 1e-6 || Math.Abs(c.Y - 100) < 1e-6));
        }

        [Fact]
        public void The_nearest_point_on_an_edge_stays_on_the_edge()
        {
            var ray = new Ray3(new Vec3(5, 100, 0), new Vec3(0, -1, 0));   // looking straight down at x = 5
            var p = MeshEdges.NearestOnSegment(ray, new Vec3(0, 0, 0), new Vec3(10, 0, 0))!.Value;
            Assert.Equal(new Vec3(5, 0, 0), p);
            var clamped = MeshEdges.NearestOnSegment(new Ray3(new Vec3(30, 100, 0), new Vec3(0, -1, 0)), new Vec3(0, 0, 0), new Vec3(10, 0, 0))!.Value;
            Assert.Equal(new Vec3(10, 0, 0), clamped);   // past the end: the end
        }

        [Fact]
        public void Along_the_main_axis_keeps_only_the_biggest_difference()
        {
            var to = MeshEdges.AlongMainAxis(new Vec3(0, 0, 0), new Vec3(3, 50, -4));
            Assert.Equal(new Vec3(0, 50, 0), to);
        }
    }
}
