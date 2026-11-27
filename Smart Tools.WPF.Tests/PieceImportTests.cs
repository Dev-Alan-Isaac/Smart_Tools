using System.Text;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>OBJ / STL into boards, and colours kept in .dmodel files.</summary>
    public class PieceImportTests
    {
        public static IEnumerable<object[]> Ids() => PieceTemplates.All.Select(t => new object[] { t.Id });

        private static PieceModel Preset(string id)
        {
            var t = PieceTemplates.Find(id)!;
            return t.Build(t.Params.ToDictionary(p => p.Key, p => p.Default));
        }

        /// <summary>Each board as "min x,y,z size w,h,d" measured from the piece's lowest corner, sorted, to compare two pieces.</summary>
        private static List<string> Boxes(IEnumerable<Part> parts)
        {
            var list = parts.ToList();
            var min = new Vec3(list.Min(p => p.Min.X), list.Min(p => p.Min.Y), list.Min(p => p.Min.Z));
            static string R(double v) => Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return list.Select(p => $"{R(p.Min.X - min.X)},{R(p.Min.Y - min.Y)},{R(p.Min.Z - min.Z)} {R(p.Size.X)},{R(p.Size.Y)},{R(p.Size.Z)}")
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void Obj_export_of_every_preset_comes_back_as_the_same_boards_and_names(string id)
        {
            var model = Preset(id);
            Assert.True(PieceImport.TryRead("x.obj", Encoding.UTF8.GetBytes(PieceExport.ToObj(model)), "Part", out var piece, out string error), error);
            Assert.Equal(Boxes(model.Parts), Boxes(piece.Parts));
            Assert.Equal(model.Parts.Select(p => p.Name).OrderBy(n => n), piece.Parts.Select(p => p.Name).OrderBy(n => n));
            Assert.False(piece.FromMetres);
            Assert.Equal(0, piece.Dropped);
            Assert.Equal(model.Parts.Count(p => p.Holes.Count > 0 || p.Bevels.Count > 0), piece.Shaped);
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void Stl_export_of_every_preset_splits_back_into_its_boards_even_where_they_touch(string id)
        {
            var model = Preset(id);
            Assert.True(PieceImport.TryRead("x.stl", PieceExport.ToStlBinary(model), "Part", out var piece, out string error), error);
            Assert.Equal(Boxes(model.Parts), Boxes(piece.Parts));
            Assert.All(piece.Parts, p => Assert.StartsWith("Part ", p.Name));
        }

        [Fact]
        public void Two_boxes_sharing_a_whole_face_stay_two_boards()
        {
            var model = new PieceModel("Stack", new[]
            {
                new Part("Low", Vec3.Zero, new Vec3(100, 50, 100), PartKind.Board),
                new Part("High", new Vec3(0, 50, 0), new Vec3(100, 50, 100), PartKind.Board),
            });
            Assert.True(PieceImport.TryRead("x.stl", PieceExport.ToStlBinary(model), "Part", out var piece, out _));
            Assert.Equal(Boxes(model.Parts), Boxes(piece.Parts));
        }

        [Fact]
        public void A_small_text_stl_is_read_as_metres_and_lands_at_zero()
        {
            // one 0.5 m cube standing 2 m up, as text STL (Z up)
            var cube = new PieceModel("Cube", new[] { new Part("C", new Vec3(3, 2, 1), new Vec3(0.5, 0.5, 0.5), PartKind.Board) });
            var sb = new StringBuilder("solid cube\n");
            foreach (var t in BoxMesh.Triangles(cube.Parts[0]))
            {
                sb.Append("facet normal 0 0 0\nouter loop\n");
                foreach (var v in new[] { t.A, t.B, t.C }) sb.Append(FormattableString.Invariant($"vertex {v.X} {-v.Z} {v.Y}\n"));
                sb.Append("endloop\nendfacet\n");
            }
            sb.Append("endsolid cube\n");
            Assert.True(PieceImport.TryRead("cube.STL", Encoding.ASCII.GetBytes(sb.ToString()), "Part", out var piece, out string error), error);
            var part = Assert.Single(piece.Parts);
            Assert.True(piece.FromMetres);
            Assert.Equal(Vec3.Zero, part.Min);
            Assert.Equal(500, part.Size.X, 6);
            Assert.Equal(500, part.Size.Y, 6);
            Assert.Equal(500, part.Size.Z, 6);
        }

        [Fact]
        public void Obj_with_quads_negative_indices_and_unnamed_objects()
        {
            // a 200 x 10 x 100 slab written with quads and indices counted back, plus a pyramid (not a box)
            const string obj = """
                o Shelf_board
                v 0 0 0
                v 200 0 0
                v 200 10 0
                v 0 10 0
                v 0 0 100
                v 200 0 100
                v 200 10 100
                v 0 10 100
                usemtl wood
                f -8 -5 -6 -7
                f -4 -3 -2 -1
                f -8 -7 -3 -4
                f -6 -5 -1 -2
                f -8 -4 -1 -5
                f -7 -6 -2 -3
                g
                v 300 0 0
                v 400 0 0
                v 350 0 80
                v 350 60 30
                f 9/1/1 10/2/1 11/3/1
                f 9 12 10
                f 10 12 11
                f 11 12 9
                """;
            Assert.True(PieceImport.TryRead("m.obj", Encoding.UTF8.GetBytes(obj), "Part", out var piece, out string error), error);
            Assert.Equal("Part 1+Shelf board", string.Join("+", piece.Parts.Select(p => p.Name).OrderBy(n => n)));
            var shelf = piece.Parts.Single(p => p.Name == "Shelf board");
            Assert.Equal(new Vec3(200, 10, 100), shelf.Size);
            var pyramid = piece.Parts.Single(p => p.Name == "Part 1");
            Assert.Equal(new Vec3(100, 60, 80), pyramid.Size);
            Assert.Equal(1, piece.Shaped);
        }

        [Theory]
        [InlineData("a.obj", "v 1 2\nf 1 2 3\n")]
        [InlineData("a.obj", "v 0 0 0\nf 1 2 3\n")]
        [InlineData("a.obj", "v 0 0 zero\n")]
        [InlineData("a.obj", "# nothing\n")]
        [InlineData("a.stl", "hello world")]
        [InlineData("a.3mf", "v 0 0 0")]
        public void Files_that_cannot_be_read_are_refused_with_a_reason(string file, string text)
        {
            Assert.False(PieceImport.TryRead(file, Encoding.UTF8.GetBytes(text), "Part", out var piece, out string error));
            Assert.NotEmpty(error);
            Assert.Empty(piece.Parts);
        }

        [Fact]
        public void More_objects_than_the_limit_keep_the_biggest()
        {
            var parts = Enumerable.Range(0, PieceEditor.MaxParts + 5)
                .Select(i => new Part($"B{i}", new Vec3(i * 50, 0, 0), new Vec3(i < 5 ? 2 : 20, 30, 30), PartKind.Board)).ToList();
            Assert.True(PieceImport.TryRead("x.obj", Encoding.UTF8.GetBytes(PieceExport.ToObj(new PieceModel("Many", parts))), "Part", out var piece, out _));
            Assert.Equal(PieceEditor.MaxParts, piece.Parts.Count);
            Assert.Equal(5, piece.Dropped);
            Assert.DoesNotContain(piece.Parts, p => p.Size.X < 10);
        }

        [Fact]
        public void Dmodel_keeps_the_colours_of_its_own_boards()
        {
            var parts = new[] { new Part("Top", Vec3.Zero, new Vec3(100, 18, 50), PartKind.Board) };
            var paint = new Dictionary<string, BoardPaint>
            {
                ["Top"] = new() { Color = "#6b4226", Faces = new() { [3] = "#C0392B", [9] = "#000000" } },
                ["Gone"] = new() { Color = "#2E86DE" },
            };
            string json = CustomPieceJson.ToJson("Desk", parts, paint);
            Assert.DoesNotContain("Gone", json);
            Assert.True(CustomPieceJson.TryRead(json, out _, out _, out var back, out _));
            var top = Assert.Single(back).Value;
            Assert.Equal("#6B4226", top.Color);
            Assert.Equal(new Dictionary<int, string> { [3] = "#C0392B" }, top.Faces);

            Assert.DoesNotContain("Paint", CustomPieceJson.ToJson("Desk", parts));
            Assert.True(CustomPieceJson.TryRead(CustomPieceJson.ToJson("Desk", parts), out _, out _, out var none, out _));
            Assert.Empty(none);
        }
    }
}
