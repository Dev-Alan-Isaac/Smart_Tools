using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    public class ExtrudeTests
    {
        private static OutlinePoint P(double x, double z) => new(x, z);

        private static double Volume(Scene scene) =>
            new SceneMeshBuilder().Build(scene, includeHidden: true).Where(o => !o.IsHole).Sum(o => Csg.Volume(o.Triangles));

        private static Scene Add(ExtrudedShape e) =>
            SceneEdit.AddNodes(Scene.Empty("t"), new SceneNode[] { e.Shape }, out _, new Dictionary<string, MeshData> { [e.Mesh.Id] = e.Mesh });

        private static double SignedVolume(ExtrudedShape e)
        {
            var s = e.Shape.Size;
            var pts = e.Mesh.Points.Select(p => new Vec3(p.X * s.X, p.Y * s.Y, p.Z * s.Z)).ToList();
            double v = 0;
            for (int i = 0; i < pts.Count; i += 3) v += Vec3.Dot(pts[i], Vec3.Cross(pts[i + 1], pts[i + 2])) / 6;
            return v;
        }

        // an outline with dents, like a hand: a palm with three fingers sticking out
        private static readonly OutlinePoint[] Mitten =
        {
            P(0, 0), P(60, 0), P(60, -40), P(50, -40), P(50, -10), P(40, -10), P(40, -45), P(30, -45), P(30, -10),
            P(20, -10), P(20, -40), P(10, -40), P(10, -10), P(0, -10),
        };

        [Fact]
        public void A_square_becomes_a_box_of_the_right_volume_wound_outward()
        {
            var square = new[] { P(0, 0), P(40, 0), P(40, 30), P(0, 30) };
            foreach (var outline in new[] { square, square.Reverse().ToArray() })   // either drawing direction
            {
                var e = Extrude.Make("s", outline, 10)!;
                Assert.Equal(40 * 30 * 10, SignedVolume(e), 6);
                Assert.Equal(new Vec3(20, 5, 15), e.Shape.Position);
                Assert.Equal(new Vec3(40, 10, 30), e.Shape.Size);
            }
        }

        [Fact]
        public void A_dented_outline_keeps_its_exact_area()
        {
            double area = Math.Abs(Extrude.Area(Mitten));
            var e = Extrude.Make("mitten", Mitten, 8)!;
            Assert.Equal(area * 8, SignedVolume(e), 6);
            Assert.Equal(Mitten.Length - 2, Extrude.Triangulate(Extrude.Clean(Mitten)).Count);
        }

        [Fact]
        public void The_extruded_shape_works_in_a_scene_and_stands_on_the_base_height()
        {
            var e = Extrude.Make("m", Mitten, 8, baseY: 5)!;
            Assert.Equal(5, e.Shape.Position.Y - e.Shape.Size.Y / 2, 9);
            Assert.Equal(Math.Abs(Extrude.Area(Mitten)) * 8, Volume(Add(e)), 3);
        }

        [Fact]
        public void Bad_outlines_are_refused()
        {
            Assert.Equal(OutlineProblem.TooFewPoints, Extrude.Check(new[] { P(0, 0), P(10, 0) }));
            Assert.NotEqual(OutlineProblem.None, Extrude.Check(new[] { P(0, 0), P(10, 0), P(20, 0.0000000001), P(5, 0) }));   // all on one line
            Assert.Equal(OutlineProblem.Crossing, Extrude.Check(new[] { P(0, 0), P(10, 10), P(10, 0), P(0, 10) }));   // a bow tie
            Assert.Null(Extrude.Make("x", new[] { P(0, 0), P(10, 10), P(10, 0), P(0, 10) }, 5));
        }

        [Fact]
        public void Repeated_and_straight_line_points_are_dropped()
        {
            var clean = Extrude.Clean(new[] { P(0, 0), P(0, 0), P(5, 0), P(10, 0), P(10, 10), P(0, 10), P(0, 0) });
            Assert.Equal(4, clean.Count);
        }

        [Fact]
        public void The_hand_example_builds_into_one_solid_quickly()
        {
            var hand = SceneExamples.Find("hand")!;
            var scene = Scene.Empty("t") with { Nodes = new[] { hand.Build(null) } };
            var watch = Stopwatch.StartNew();
            var objects = new SceneMeshBuilder().Build(scene, includeHidden: true);
            watch.Stop();
            var solid = Assert.Single(objects, o => !o.IsHole);
            Assert.True(Csg.Volume(solid.Triangles) > 0);
            Assert.True(solid.Bounds.Min.Y > -0.5, "the hand lies on the floor, not under it");
            Assert.True(watch.Elapsed.TotalSeconds < 20, $"building the hand took {watch.Elapsed.TotalSeconds:0.0} s");
        }

        [Fact]
        public void The_thumb_points_out_to_the_side_and_the_fingers_away_from_the_wrist()
        {
            var hand = (GroupNode)SceneExamples.Find("hand")!.Build(null);
            var shapes = SceneEdit.Shapes(hand).ToList();
            var palm = shapes.Single(s => s.Name == "Palm");
            var thumbTip = shapes.Single(s => s.Name == "Thumb tip");
            var middleTip = shapes.Single(s => s.Name == "Middle finger tip");
            Assert.True(thumbTip.Position.X < palm.Position.X - palm.Size.X / 2, "thumb tip is outside the palm on its side");
            Assert.True(middleTip.Position.Z < palm.Position.Z - palm.Size.Z / 2, "fingers reach past the palm's front");
        }
    }
}
