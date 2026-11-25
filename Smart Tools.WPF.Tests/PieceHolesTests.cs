using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Round holes through a board: kept valid, and a surface with no cracks and the right volume.</summary>
    public class PieceHolesTests
    {
        private static Part Door(double w = 400, double h = 700, double t = 18, params Hole[] holes) =>
            new("Door", new Vec3(10, 20, 30), new Vec3(w, h, t), PartKind.Panel, holes);

        private static (long, long, long) Key(Vec3 v) =>
            ((long)Math.Round(v.X * 1e5), (long)Math.Round(v.Y * 1e5), (long)Math.Round(v.Z * 1e5));

        /// <summary>Every edge must be used once in each direction: a surface with no gaps and no doubled faces.</summary>
        private static void AssertWatertight(IReadOnlyList<Triangle> triangles)
        {
            var edges = new Dictionary<((long, long, long), (long, long, long)), int>();
            foreach (var t in triangles)
            {
                var corners = new[] { Key(t.A), Key(t.B), Key(t.C) };
                for (int i = 0; i < 3; i++)
                {
                    var edge = (corners[i], corners[(i + 1) % 3]);
                    edges[edge] = edges.GetValueOrDefault(edge) + 1;
                }
            }
            foreach (var (edge, count) in edges)
            {
                Assert.Equal(1, count);
                Assert.True(edges.ContainsKey((edge.Item2, edge.Item1)), $"edge {edge} has no partner");
            }
        }

        private static double Volume(IReadOnlyList<Triangle> triangles) =>
            Math.Abs(triangles.Sum(t => Vec3.Dot(t.A, Vec3.Cross(t.B, t.C)) / 6));

        private static double RingArea(double diameter, int sides = 32) => sides / 2.0 * Math.Pow(diameter / 2, 2) * Math.Sin(2 * Math.PI / sides);

        [Fact]
        public void A_door_with_one_hole_is_closed_and_has_the_right_volume()
        {
            var door = Door(holes: new Hole(2, 340, 350, 20));
            var triangles = BoxMesh.Triangles(door);
            AssertWatertight(triangles);
            Assert.Equal(400 * 700 * 18 - RingArea(20) * 18, Volume(triangles), 3);
        }

        [Theory]
        [InlineData(0)]   // through X
        [InlineData(1)]   // through Y
        [InlineData(2)]   // through Z
        public void A_hole_can_go_through_any_direction(int axis)
        {
            var part = new Part("Block", new Vec3(0, 0, 0), new Vec3(100, 80, 60), PartKind.Board, new[] { new Hole(axis, 25, 22, 14) });
            var holes = PieceHoles.Sanitize(part.Size, part.Holes);
            part = part with { Cuts = holes };
            var triangles = BoxMesh.Triangles(part);
            AssertWatertight(triangles);
            double through = axis == 0 ? 100 : axis == 1 ? 80 : 60;
            Assert.Equal(100 * 80 * 60 - RingArea(14) * through, Volume(triangles), 3);
        }

        [Fact]
        public void Several_holes_stacked_and_side_by_side_still_close()
        {
            var holes = PieceHoles.Sanitize(new Vec3(400, 700, 18), new[]
            {
                new Hole(2, 100, 100, 40), new Hole(2, 100, 300, 60),   // one above the other (they share x)
                new Hole(2, 300, 200, 30), new Hole(2, 220, 500, 25), new Hole(2, 330, 600, 10),
            });
            Assert.Equal(5, holes.Count);
            var triangles = BoxMesh.Triangles(Door(holes: holes.ToArray()));
            AssertWatertight(triangles);
            double expected = 400 * 700 * 18 - holes.Sum(h => RingArea(h.Diameter)) * 18;
            Assert.Equal(expected, Volume(triangles), 3);
        }

        [Fact]
        public void The_stl_and_the_obj_of_a_board_with_a_hole_are_written()
        {
            var model = new PieceModel("Cabinet door", new[] { Door(holes: new Hole(2, 340, 350, 20)) });
            var stl = PieceExport.ToStlBinary(model);
            uint count = BitConverter.ToUInt32(stl, 80);
            Assert.Equal(84 + 50 * (int)count, stl.Length);
            Assert.True(count > 12);
            string obj = PieceExport.ToObj(model);
            Assert.Contains("g Door", obj);
            Assert.Equal(count, (uint)obj.Split('\n').Count(l => l.StartsWith("f ")));   // one face line per triangle
        }

        [Fact]
        public void Holes_are_kept_inside_the_board_and_apart_and_through_one_direction()
        {
            var size = new Vec3(200, 100, 18);
            var holes = PieceHoles.Sanitize(size, new[]
            {
                new Hole(2, -50, 500, 30),       // outside: pulled in
                new Hole(2, 10, 12, 500),        // far too big: made to fit
                new Hole(0, 100, 50, 10),        // another direction: follows the first
            });
            Assert.All(holes, h => Assert.Equal(2, h.Axis));
            foreach (var h in holes)
            {
                Assert.InRange(h.U - h.Diameter / 2, 0.5 - 1e-9, 200);
                Assert.InRange(h.U + h.Diameter / 2, 0, 199.5 + 1e-9);
                Assert.InRange(h.V - h.Diameter / 2, 0.5 - 1e-9, 100);
                Assert.InRange(h.V + h.Diameter / 2, 0, 99.5 + 1e-9);
            }
            for (int i = 0; i < holes.Count; i++)
                for (int j = i + 1; j < holes.Count; j++)
                    Assert.True(Math.Sqrt(Math.Pow(holes[i].U - holes[j].U, 2) + Math.Pow(holes[i].V - holes[j].V, 2)) >= holes[i].Diameter / 2 + holes[j].Diameter / 2);
        }

        [Fact]
        public void A_face_too_small_for_a_hole_has_none_and_nan_is_ignored()
        {
            Assert.Single(PieceHoles.Sanitize(new Vec3(2, 200, 200), new[] { new Hole(0, 100, 100, 5) }));   // through X: the face is 200 x 200 -> fine
            Assert.Empty(PieceHoles.Sanitize(new Vec3(200, 1.5, 200), new[] { new Hole(2, 50, 1, 5) })); // through Z: the face is 200 x 1.5 -> too thin
            Assert.Empty(PieceHoles.Sanitize(new Vec3(200, 200, 18), new[] { new Hole(2, double.NaN, 5, 5) }));
            Assert.Equal(PieceHoles.MaxHoles, PieceHoles.Sanitize(new Vec3(1000, 1000, 18), Enumerable.Range(0, 20).Select(i => new Hole(2, 60 + 100 * (i % 9), 60 + 100 * (i / 9), 20))).Count);
        }

        [Fact]
        public void Boards_with_the_same_holes_are_equal()
        {
            var a = Door(holes: new Hole(2, 340, 350, 20));
            var b = Door(holes: new Hole(2, 340, 350, 20));
            Assert.Equal(a, b);
            Assert.NotEqual(a, Door(holes: new Hole(2, 340, 350, 21)));
            Assert.NotEqual(a, Door());
        }

        [Fact]
        public void An_edited_board_pulls_its_hole_back_inside_when_made_smaller()
        {
            var parts = new List<Part> { Door(holes: new Hole(2, 340, 350, 20)) };
            var smaller = PieceEditor.Replace(parts, 0, parts[0] with { Size = new Vec3(100, 100, 18) });
            var hole = Assert.Single(smaller[0].Holes);
            Assert.True(hole.U + hole.Diameter / 2 <= 100 && hole.V + hole.Diameter / 2 <= 100);
        }

        [Fact]
        public void Holes_are_saved_in_the_design_file_and_read_back()
        {
            var parts = new List<Part> { Door(holes: new[] { new Hole(2, 340, 350, 20), new Hole(2, 60, 100, 12) }), new Part("Shelf", Vec3.Zero, new Vec3(300, 18, 200), PartKind.Board) };
            string json = CustomPieceJson.ToJson("Cabinet", parts);
            Assert.True(CustomPieceJson.TryRead(json, out _, out var back, out _));
            Assert.Equal(parts[0].Holes, back[0].Holes);
            Assert.Empty(back[1].Holes);
            Assert.DoesNotContain("\"Holes\": []", json);
        }

        // ---------------------------------------------------------------- the cabinet

        [Fact]
        public void The_cabinet_door_has_a_knob_hole_on_the_free_edge_and_none_when_it_is_zero()
        {
            var cabinet = PieceTemplates.Find("cabinet")!;
            var values = cabinet.Params.ToDictionary(p => p.Key, p => p.Default);
            var door = cabinet.Build(values).Parts.Single(p => p.Name == "Door 1");
            var hole = Assert.Single(door.Holes);
            Assert.Equal(2, hole.Axis);                       // through the door's thickness
            Assert.Equal(door.Size.X - 40, hole.U, 6);        // near the edge away from the hinge
            Assert.Equal(door.Size.Y / 2, hole.V, 6);
            AssertWatertight(BoxMesh.Triangles(door));

            values["knob"] = 0;
            Assert.Empty(cabinet.Build(values).Parts.Single(p => p.Name == "Door 1").Holes);
        }

        [Fact]
        public void The_cabinet_can_have_no_doors_and_two_doors_mirror_their_knobs()
        {
            var cabinet = PieceTemplates.Find("cabinet")!;
            var values = cabinet.Params.ToDictionary(p => p.Key, p => p.Default);
            values["doors"] = 0;
            Assert.DoesNotContain(cabinet.Build(values).Parts, p => p.Name.StartsWith("Door"));
            values["doors"] = 2;
            var parts = cabinet.Build(values).Parts;
            var first = parts.Single(p => p.Name == "Door 1").Holes[0];
            var second = parts.Single(p => p.Name == "Door 2").Holes[0];
            Assert.True(first.U > 100);
            Assert.Equal(40, second.U, 6);
        }

        // ---------------------------------------------------------------- printing

        [Theory]
        [InlineData(1, 0.2, false)]
        [InlineData(1, 0.2, true)]
        [InlineData(10, 0.2, false)]
        public void A_printed_door_keeps_its_hole_where_it_was_on_the_board(double ratio, double clearance, bool tightBed)
        {
            var cabinet = PieceTemplates.Find("cabinet")!;
            var values = cabinet.Params.ToDictionary(p => p.Key, p => p.Default);
            var model = cabinet.Build(values);
            var settings = new PrintSettings(ratio, clearance, tightBed ? 740 : 900, 900, 250, 4);
            var plan = PrintPlanner.Plan(model, settings);
            var placed = plan.Plates.SelectMany(p => p.Parts).Single(p => p.Part.Name == "Door 1");
            if (tightBed) Assert.True(placed.Rotated);   // this bed is only wide enough with the door turned a quarter
            var hole = Assert.Single(placed.Holes!);
            Assert.Equal(1, hole.Axis);                                      // lying flat: through the height
            Assert.InRange(hole.U, hole.Diameter / 2, placed.Size.X - hole.Diameter / 2);
            Assert.InRange(hole.V, hole.Diameter / 2, placed.Size.Z - hole.Diameter / 2);
            Assert.Equal(Math.Max(PieceHoles.MinDiameter, 8 / ratio), hole.Diameter, 6);   // never thinner than 1 mm

            // the door's knob is 40 mm from its right edge: lying flat the hole is that far from the matching edge of the plate part
            double edge = 40 / ratio;
            var fromEdges = new[] { hole.U, placed.Size.X - hole.U, hole.V, placed.Size.Z - hole.V };
            Assert.Contains(fromEdges, d => Math.Abs(d - (edge - clearance / 2)) < 1e-6);

            var plate = PrintPlanner.PlateModel(plan.Plates.Single(p => p.Parts.Contains(placed)), "Plate");
            AssertWatertight(BoxMesh.Triangles(plate.Parts.Single(p => p.Name == "Door 1")));
        }

        [Fact]
        public void Scaling_a_piece_scales_its_holes()
        {
            var model = new PieceModel("m", new[] { Door(holes: new Hole(2, 340, 350, 20)) });
            var half = PieceTransform.Scale(model, 0.5).Parts[0].Holes[0];
            Assert.Equal(new Hole(2, 170, 175, 10), half);
        }
    }
}
