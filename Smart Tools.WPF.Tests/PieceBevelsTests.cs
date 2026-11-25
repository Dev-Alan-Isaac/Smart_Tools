using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Edges cut at an angle (bevels) and through the whole board (miters): a closed surface, the right volume, holes kept clear.</summary>
    public class PieceBevelsTests
    {
        // faces: 0 left, 1 right, 2 bottom, 3 top, 4 back, 5 front
        private const int Left = 0, Right = 1, Bottom = 2, Top = 3, Back = 4, Front = 5;

        private static Part Board(double w, double h, double d, IReadOnlyList<Bevel>? bevels = null, IReadOnlyList<Hole>? holes = null) =>
            new("Board", new Vec3(10, 20, 30), new Vec3(w, h, d), PartKind.Panel, holes, bevels);

        private static (long, long, long) Key(Vec3 v) =>
            ((long)Math.Round(v.X * 1e5), (long)Math.Round(v.Y * 1e5), (long)Math.Round(v.Z * 1e5));

        private static void AssertWatertight(IReadOnlyList<Triangle> triangles)
        {
            var edges = new Dictionary<((long, long, long), (long, long, long)), int>();
            foreach (var t in triangles)
            {
                var c = new[] { Key(t.A), Key(t.B), Key(t.C) };
                for (int i = 0; i < 3; i++)
                {
                    var e = (c[i], c[(i + 1) % 3]);
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                }
            }
            foreach (var (edge, count) in edges)
            {
                Assert.Equal(1, count);
                Assert.True(edges.ContainsKey((edge.Item2, edge.Item1)), $"edge {edge} has no partner");
            }
        }

        private static double Volume(IReadOnlyList<Triangle> t) => Math.Abs(t.Sum(x => Vec3.Dot(x.A, Vec3.Cross(x.B, x.C)) / 6));

        /// <summary>Signed volume: positive only when every face points outward.</summary>
        private static double SignedVolume(IReadOnlyList<Triangle> t) => t.Sum(x => Vec3.Dot(x.A, Vec3.Cross(x.B, x.C)) / 6);

        [Fact]
        public void A_chamfer_takes_off_a_triangular_strip_and_the_surface_stays_closed()
        {
            var b = new Bevel(Front, Top, 45, 10);
            var part = Board(400, 700, 18, PieceBevels.Sanitize(new Vec3(400, 700, 18), new[] { b }));
            var tris = BoxMesh.Triangles(part);
            AssertWatertight(tris);
            Assert.Equal(400 * 700 * 18 - 0.5 * 10 * 10 * 400, Volume(tris), 3);
            Assert.True(SignedVolume(tris) > 0);   // faces wound outward
        }

        [Theory]
        [InlineData(30)]
        [InlineData(45)]
        [InlineData(60)]
        public void The_angle_sets_how_deep_the_cut_goes_into_the_other_face(double angle)
        {
            var size = new Vec3(400, 700, 18);
            var bevel = Assert.Single(PieceBevels.Sanitize(size, new[] { new Bevel(Front, Top, angle, 8) }));
            var (intoA, intoB) = PieceBevels.Setbacks(bevel);
            Assert.Equal(8, intoA, 6);
            Assert.Equal(8 * Math.Tan(angle * Math.PI / 180), intoB, 6);
            var tris = BoxMesh.Triangles(Board(400, 700, 18, new[] { bevel }));
            AssertWatertight(tris);
            Assert.Equal(400 * 700 * 18 - 0.5 * intoA * intoB * 400, Volume(tris), 3);
        }

        [Fact]
        public void A_miter_cuts_right_through_the_end_of_a_board()
        {
            // a board 300 long (X), 18 thick (Z): its right end cut at 45 degrees through the whole thickness
            var size = new Vec3(300, 80, 18);
            var miter = Assert.Single(PieceBevels.Sanitize(size, new[] { new Bevel(Right, Front, 45, 9999) }));
            Assert.Equal(18, miter.Depth, 6);                   // shortened so the cut goes exactly through the 18 mm
            var tris = BoxMesh.Triangles(Board(300, 80, 18, new[] { miter }));
            AssertWatertight(tris);
            Assert.Equal(300 * 80 * 18 - 0.5 * 18 * 18 * 80, Volume(tris), 3);
        }

        [Fact]
        public void Bevels_on_all_four_front_edges_meet_at_the_corners_and_still_close()
        {
            var size = new Vec3(400, 700, 18);
            var bevels = PieceBevels.Sanitize(size, new[] { new Bevel(Front, Left, 45, 8), new Bevel(Front, Right, 45, 8), new Bevel(Front, Top, 45, 8), new Bevel(Front, Bottom, 45, 8) });
            Assert.Equal(4, bevels.Count);
            var tris = BoxMesh.Triangles(Board(400, 700, 18, bevels));
            AssertWatertight(tris);
            double v = Volume(tris);
            // a raised-panel front: the four strips come off, the corner pieces they share are counted once
            double strips = 0.5 * 8 * 8 * (2 * 400 + 2 * 700);
            Assert.InRange(v, 400 * 700 * 18 - strips - 1, 400 * 700 * 18 - strips + 4 * 8 * 8 * 8 / 3.0 + 1);
        }

        [Fact]
        public void A_bevelled_door_keeps_its_knob_hole_and_closes()
        {
            var size = new Vec3(400, 700, 18);
            var bevels = PieceBevels.Sanitize(size, new[] { new Bevel(Front, Left, 45, 6), new Bevel(Front, Right, 45, 6), new Bevel(Front, Top, 45, 6), new Bevel(Front, Bottom, 45, 6) });
            var holes = new[] { new Hole(2, 360, 350, 20) };
            Assert.True(PieceBevels.HoleIsClear(size, holes[0], bevels));
            var tris = BoxMesh.Triangles(Board(400, 700, 18, bevels, holes));
            AssertWatertight(tris);
            double plain = Volume(BoxMesh.Triangles(Board(400, 700, 18, bevels)));
            Assert.True(Volume(tris) < plain);   // the hole is still there
        }

        [Fact]
        public void A_hole_that_would_run_into_a_cut_is_not_allowed()
        {
            var size = new Vec3(400, 700, 18);
            var bevels = PieceBevels.Sanitize(size, new[] { new Bevel(Front, Right, 45, 10) });
            Assert.False(PieceBevels.HoleIsClear(size, new Hole(2, 395, 350, 8), bevels));   // in the strip taken off the front
            Assert.True(PieceBevels.HoleIsClear(size, new Hole(2, 300, 350, 8), bevels));

            // the editor drops such a hole when the board is checked
            var parts = new List<Part> { new("Door", Vec3.Zero, size, PartKind.Panel, new[] { new Hole(2, 395, 350, 8), new Hole(2, 300, 350, 8) }, bevels) };
            var cleaned = PieceEditor.Replace(parts, 0, parts[0]);
            Assert.Single(cleaned[0].Holes);
        }

        [Fact]
        public void Bad_bevels_are_dropped_or_mended()
        {
            var size = new Vec3(400, 700, 18);
            var kept = PieceBevels.Sanitize(size, new[]
            {
                new Bevel(Front, Back, 45, 5),        // two faces of one direction: no edge there
                new Bevel(Front, Top, 45, -3),        // no depth
                new Bevel(Front, Top, 1, 5),          // angle pulled up to 5 degrees
                new Bevel(Top, Front, 45, 5),         // the same edge again (the other way round): dropped
                new Bevel(9, Top, 45, 5),             // not a face
                new Bevel(Front, Left, double.NaN, 5),
            });
            var only = Assert.Single(kept);
            Assert.Equal(PieceBevels.MinAngle, only.Angle);
        }

        [Fact]
        public void Two_mitred_boards_meeting_at_a_corner_do_not_overlap_but_plain_ones_do()
        {
            // a top and a side, both 18 thick, meeting at a 45 degree miter at the top right corner of a box
            var top = new Part("Top", new Vec3(0, 482, 0), new Vec3(500, 18, 300), PartKind.Top, null, new[] { new Bevel(Right, Bottom, 45, 18) });
            var side = new Part("Side", new Vec3(482, 0, 0), new Vec3(18, 500, 300), PartKind.Panel, null, new[] { new Bevel(Top, Left, 45, 18) });
            top = top with { Edges = PieceBevels.Sanitize(top.Size, top.Bevels) };
            side = side with { Edges = PieceBevels.Sanitize(side.Size, side.Bevels) };
            Assert.Empty(PieceChecks.Overlapping(new[] { top, side }));
            Assert.NotEmpty(PieceChecks.Overlapping(new[] { top with { Edges = null }, side with { Edges = null } }));
            Assert.Empty(PieceChecks.Loose(new[] { top, side }));
        }

        [Fact]
        public void Bevels_are_saved_and_read_back_and_scale_with_the_piece()
        {
            var size = new Vec3(400, 700, 18);
            var bevels = PieceBevels.Sanitize(size, new[] { new Bevel(Front, Top, 30, 8) });
            var parts = new List<Part> { new("Door", Vec3.Zero, size, PartKind.Panel, null, bevels, "Door group") };
            string json = CustomPieceJson.ToJson("Cabinet", parts);
            Assert.True(CustomPieceJson.TryRead(json, out _, out var back, out _));
            Assert.Equal(bevels, back[0].Bevels);
            Assert.Equal("Door group", back[0].Group);
            Assert.Equal(parts[0], back[0]);

            var half = PieceTransform.Scale(new PieceModel("m", parts), 0.5).Parts[0];
            Assert.Equal(4, half.Bevels[0].Depth, 6);
            Assert.Equal(30, half.Bevels[0].Angle, 6);
        }

        [Fact]
        public void A_bevelled_board_goes_onto_the_print_plate_with_its_cut_and_a_closed_surface()
        {
            var size = new Vec3(400, 700, 18);
            var bevels = PieceBevels.Sanitize(size, new[] { new Bevel(Front, Top, 45, 8) });
            var model = new PieceModel("Door", new[] { new Part("Door", Vec3.Zero, size, PartKind.Panel, null, bevels) });
            var plan = PrintPlanner.Plan(model, new PrintSettings(2, 0.2, 900, 900, 250, 4));
            var placed = plan.Plates.Single().Parts.Single();
            var flat = Assert.Single(placed.Bevels!);
            Assert.Equal(4, flat.Depth, 6);                                       // scaled by 1/2
            Assert.Equal(1, PieceBevels.AxisOf(flat.FaceA));                       // the front now faces up: the board lies flat
            var plate = PrintPlanner.PlateModel(plan.Plates.Single(), "Plate");
            var tris = BoxMesh.Triangles(plate.Parts.Single());
            AssertWatertight(tris);
            var p = plate.Parts.Single();
            Assert.True(Volume(tris) < p.Size.X * p.Size.Y * p.Size.Z);
        }

        [Fact]
        public void The_angle_marker_sits_on_the_edge_and_on_both_faces()
        {
            var part = Board(400, 700, 18, new[] { new Bevel(Front, Top, 45, 10) });
            var (edge, onA, onB) = PieceBevels.Marker(part, part.Bevels[0]);
            Assert.Equal(new Vec3(210, 720, 48), edge);           // middle of the top front edge
            Assert.Equal(new Vec3(210, 710, 48), onA);            // 10 mm down the front face
            Assert.Equal(new Vec3(210, 720, 38), onB.Y == 720 ? onB : onB);
            Assert.Equal(38, onB.Z, 6);                           // 10 mm back along the top
            Assert.True(Math.Abs(PieceBevels.Outside(part, part.Bevels[0], onA)) < 1e-9);
            Assert.True(Math.Abs(PieceBevels.Outside(part, part.Bevels[0], onB)) < 1e-9);
        }
    }
}
