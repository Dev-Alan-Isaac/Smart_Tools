using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Board colours: whole board, one face, groups, and which triangles belong to which face.</summary>
    public class PiecePaintTests
    {
        private static Part Board(params Bevel[] bevels) =>
            new("Door 1", new Vec3(10, 20, 30), new Vec3(400, 700, 18), PartKind.Panel, Edges: bevels.Length == 0 ? null : bevels);

        [Theory]
        [InlineData("#C0392B", "#C0392B")]
        [InlineData("c0392b", "#C0392B")]
        [InlineData(" #00ff00 ", "#00FF00")]
        [InlineData("#12345", null)]
        [InlineData("red", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Colours_are_read_and_written_one_way(string? given, string? expected) => Assert.Equal(expected, PiecePaint.Normalize(given));

        [Fact]
        public void Every_triangle_of_a_plain_board_lies_on_one_of_its_six_faces()
        {
            var part = Board();
            var faces = BoxMesh.Triangles(part).Select(t => PiecePaint.FaceOf(part, t)).ToList();
            Assert.DoesNotContain(null, faces);
            Assert.Equal(new int?[] { 0, 1, 2, 3, 4, 5 }, faces.Distinct().OrderBy(f => f).ToArray());
        }

        [Fact]
        public void The_slope_of_a_bevel_is_not_a_face_and_takes_the_board_colour()
        {
            var part = Board(new Bevel(5, 3, 45, 5));   // the front face, cut along its top edge
            var faces = BoxMesh.Triangles(part).Select(t => PiecePaint.FaceOf(part, t)).ToList();
            Assert.Contains(null, faces);
            Assert.Contains(5, faces);
        }

        [Fact]
        public void The_wall_of_a_hole_is_not_the_face_it_points_at()
        {
            var part = Board() with { Cuts = new[] { new Hole(2, 200, 350, 30) } };
            foreach (var t in BoxMesh.Triangles(part))
                if (PiecePaint.FaceOf(part, t) is int face && face is 0 or 1)
                    Assert.All(new[] { t.A, t.B, t.C }, c => Assert.True(c.X is 10 or 410));
        }

        [Theory]
        [InlineData(10, 300, 40, 0)]     // on the left side
        [InlineData(410, 300, 40, 1)]
        [InlineData(200, 20, 40, 2)]
        [InlineData(200, 720, 40, 3)]
        [InlineData(200, 300, 30, 4)]
        [InlineData(200, 300, 48, 5)]    // the front
        public void The_clicked_point_names_the_face(double x, double y, double z, int face) =>
            Assert.Equal(face, PiecePaint.FaceAt(Board(), new Vec3(x, y, z)));

        [Fact]
        public void A_face_colour_wins_over_the_board_colour_and_the_board_over_wood()
        {
            var paints = new Dictionary<string, BoardPaint>();
            Assert.Null(PiecePaint.ColourOf(paints, "Door 1", 5));
            PiecePaint.PaintBoard(paints, "Door 1", "#FFFFFF");
            PiecePaint.PaintFace(paints, "Door 1", 5, "#c0392b");
            Assert.Equal("#C0392B", PiecePaint.ColourOf(paints, "Door 1", 5));
            Assert.Equal("#FFFFFF", PiecePaint.ColourOf(paints, "Door 1", 3));
            Assert.Equal("#FFFFFF", PiecePaint.ColourOf(paints, "Door 1", null));
        }

        [Fact]
        public void Painting_the_whole_board_repaints_its_faces_too()
        {
            var paints = new Dictionary<string, BoardPaint>();
            PiecePaint.PaintFace(paints, "Door 1", 5, "#C0392B");
            PiecePaint.PaintBoard(paints, "Door 1", "#2E86DE");
            Assert.Equal("#2E86DE", PiecePaint.ColourOf(paints, "Door 1", 5));
        }

        [Fact]
        public void Back_to_wood_and_back_to_the_board_colour_leave_nothing_behind()
        {
            var paints = new Dictionary<string, BoardPaint>();
            PiecePaint.PaintFace(paints, "Door 1", 5, "#C0392B");
            PiecePaint.PaintFace(paints, "Door 1", 5, null);
            Assert.Empty(paints);

            PiecePaint.PaintBoard(paints, "Door 1", "#C0392B");
            PiecePaint.PaintBoard(paints, "Door 1", null);
            Assert.Empty(paints);
        }

        [Fact]
        public void Only_one_face_of_a_board_changes()
        {
            var paints = new Dictionary<string, BoardPaint>();
            PiecePaint.PaintFace(paints, "Door 1", 5, "#C0392B");
            Assert.Null(PiecePaint.ColourOf(paints, "Door 1", 4));
            Assert.Null(PiecePaint.ColourOf(paints, "Door 1", null));
            Assert.Null(PiecePaint.ColourOf(paints, "Door 2", 5));
        }

        [Fact]
        public void Sanitize_drops_what_is_not_a_colour_and_keeps_boards_that_are_gone_for_now()
        {
            var paints = new Dictionary<string, BoardPaint>
            {
                ["Door 2"] = new() { Color = "#ABCDEF" },                                        // the Doors slider is at 1 right now
                ["Bad"] = new() { Color = "blue" },
                ["Faces"] = new() { Faces = new Dictionary<int, string> { [9] = "#FFFFFF", [5] = "nope", [3] = "ffffff" } },
                ["Null"] = null!,
            };
            PiecePaint.Sanitize(paints);
            Assert.Equal(new[] { "Door 2", "Faces" }, paints.Keys.OrderBy(k => k).ToArray());
            Assert.Equal(new[] { 3 }, paints["Faces"].Faces!.Keys.ToArray());
            Assert.Equal("#FFFFFF", paints["Faces"].Faces![3]);
        }

        [Fact]
        public void Every_swatch_is_a_colour_with_its_own_name()
        {
            Assert.All(PiecePaint.Swatches, s => Assert.NotNull(PiecePaint.Normalize(s.Hex)));
            Assert.Equal(PiecePaint.Swatches.Count, PiecePaint.Swatches.Select(s => s.Id).Distinct().Count());
        }
    }
}
