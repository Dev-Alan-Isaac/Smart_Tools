using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>The custom piece: boards you add and change yourself. Pure rules, so each can be pinned.</summary>
    public class PieceEditorTests
    {
        private static Part P(string name, double x, double y, double z, double w, double h, double d, PartKind kind = PartKind.Board) =>
            new(name, new Vec3(x, y, z), new Vec3(w, h, d), kind);

        private static double Overlap(Part a, Part b)
        {
            double Axis(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
            return Axis(a.Min.X, a.Max.X, b.Min.X, b.Max.X) * Axis(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y) * Axis(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
        }

        // ------------------------------------------------------------- names
        [Fact]
        public void The_first_board_of_a_kind_is_numbered_one() =>
            Assert.Equal("Board 1", PieceEditor.UniqueName(Array.Empty<string>(), "Board"));

        [Fact]
        public void A_taken_number_is_skipped()
        {
            Assert.Equal("Board 3", PieceEditor.UniqueName(new[] { "Board 1", "Board 2" }, "Board"));
            Assert.Equal("Board 2", PieceEditor.UniqueName(new[] { "Board 1", "board 3" }, "Board"));
        }

        [Fact]
        public void A_typed_name_is_kept_when_free_and_made_unique_when_not()
        {
            Assert.Equal("Left side", PieceEditor.UniqueTypedName(new[] { "Top" }, "  Left side  "));
            Assert.Equal("Top 2", PieceEditor.UniqueTypedName(new[] { "Top" }, "Top"));
            Assert.Equal("top 2", PieceEditor.UniqueTypedName(new[] { "Top" }, "top"));   // case does not make it different; the typed case is kept
            Assert.Equal("Top 3", PieceEditor.UniqueTypedName(new[] { "Top", "Top 2" }, "Top"));
        }

        [Fact]
        public void An_empty_name_becomes_a_numbered_board() =>
            Assert.Equal("Board 1", PieceEditor.UniqueTypedName(Array.Empty<string>(), "   "));

        // ------------------------------------------------------------- add
        [Theory]
        [InlineData(PartKind.Board)]
        [InlineData(PartKind.Leg)]
        [InlineData(PartKind.Panel)]
        [InlineData(PartKind.Top)]
        public void A_new_board_has_a_sensible_size_and_the_asked_kind(PartKind kind)
        {
            var added = PieceEditor.Add(Array.Empty<Part>(), kind);
            var p = Assert.Single(added);
            Assert.Equal(kind, p.Kind);
            Assert.True(p.Size.X >= 10 && p.Size.Y >= 10 && p.Size.Z >= 10);
            Assert.Equal(Vec3.Zero, p.Min);
        }

        [Fact]
        public void A_new_board_is_placed_beside_the_piece_touching_not_inside_it()
        {
            var first = PieceEditor.Add(Array.Empty<Part>(), PartKind.Board);
            var two = PieceEditor.Add(first, PartKind.Leg);
            Assert.Equal(2, two.Count);
            Assert.True(Overlap(two[0], two[1]) < 1e-9);
            Assert.Equal(two[0].Max.X, two[1].Min.X, 9);   // right beside the first
        }

        [Fact]
        public void New_boards_get_different_names()
        {
            var parts = PieceEditor.Add(PieceEditor.Add(PieceEditor.Add(Array.Empty<Part>(), PartKind.Board), PartKind.Board), PartKind.Board);
            Assert.Equal(new[] { "Board 1", "Board 2", "Board 3" }, parts.Select(p => p.Name));
        }

        [Fact]
        public void Adding_never_changes_the_list_it_was_given()
        {
            var original = new List<Part> { P("A", 0, 0, 0, 10, 10, 10) };
            PieceEditor.Add(original, PartKind.Board);
            Assert.Single(original);
        }

        [Fact]
        public void There_is_a_limit_on_the_number_of_boards()
        {
            var parts = new List<Part>();
            for (int i = 0; i < PieceEditor.MaxParts + 5; i++) parts = PieceEditor.Add(parts, PartKind.Board).ToList();
            Assert.Equal(PieceEditor.MaxParts, parts.Count);
        }

        // ------------------------------------------------------------- duplicate, remove, replace
        [Fact]
        public void A_copy_sits_beside_the_original_with_its_own_name()
        {
            var parts = new List<Part> { P("Leg 1", 0, 0, 0, 40, 700, 40, PartKind.Leg) };
            var result = PieceEditor.Duplicate(parts, 0);
            Assert.Equal(2, result.Count);
            Assert.Equal("Leg 2", result[1].Name);
            Assert.Equal(result[0].Size, result[1].Size);
            Assert.Equal(PartKind.Leg, result[1].Kind);
            Assert.True(Overlap(result[0], result[1]) < 1e-9);
        }

        [Fact]
        public void Copying_a_board_that_does_not_exist_changes_nothing()
        {
            var parts = new List<Part> { P("A", 0, 0, 0, 10, 10, 10) };
            Assert.Single(PieceEditor.Duplicate(parts, 5));
            Assert.Single(PieceEditor.Duplicate(parts, -1));
        }

        [Fact]
        public void Removing_takes_exactly_that_board()
        {
            var parts = new List<Part> { P("A", 0, 0, 0, 10, 10, 10), P("B", 10, 0, 0, 10, 10, 10), P("C", 20, 0, 0, 10, 10, 10) };
            Assert.Equal(new[] { "A", "C" }, PieceEditor.Remove(parts, 1).Select(p => p.Name));
            Assert.Equal(3, PieceEditor.Remove(parts, 7).Count);
        }

        [Fact]
        public void Replacing_swaps_one_board_and_keeps_the_rest()
        {
            var parts = new List<Part> { P("A", 0, 0, 0, 10, 10, 10), P("B", 10, 0, 0, 10, 10, 10) };
            var result = PieceEditor.Replace(parts, 1, P("B", 50, 0, 0, 20, 20, 20));
            Assert.Equal(new Vec3(50, 0, 0), result[1].Min);
            Assert.Equal(parts[0], result[0]);
        }

        [Fact]
        public void A_replaced_board_cannot_take_another_boards_name()
        {
            var parts = new List<Part> { P("A", 0, 0, 0, 10, 10, 10), P("B", 10, 0, 0, 10, 10, 10) };
            var result = PieceEditor.Replace(parts, 1, P("A", 10, 0, 0, 10, 10, 10));
            Assert.Equal("A 2", result[1].Name);
        }

        [Fact]
        public void A_board_may_keep_its_own_name_when_it_is_changed()
        {
            var parts = new List<Part> { P("A", 0, 0, 0, 10, 10, 10) };
            Assert.Equal("A", PieceEditor.Replace(parts, 0, P("A", 5, 5, 5, 10, 10, 10))[0].Name);
        }

        // ------------------------------------------------------------- sanitising what is typed
        [Fact]
        public void A_size_is_kept_between_the_limits()
        {
            var tiny = PieceEditor.Sanitize(P("A", 0, 0, 0, 0, -5, 0.2), Array.Empty<string>());
            Assert.Equal(PieceEditor.MinSize, tiny.Size.X);
            Assert.Equal(PieceEditor.MinSize, tiny.Size.Y);
            Assert.Equal(PieceEditor.MinSize, tiny.Size.Z);
            var huge = PieceEditor.Sanitize(P("A", 0, 0, 0, 1e9, 1e9, 1e9), Array.Empty<string>());
            Assert.Equal(PieceEditor.MaxSize, huge.Size.X);
        }

        [Fact]
        public void A_position_is_kept_between_the_limits()
        {
            var far = PieceEditor.Sanitize(P("A", 1e9, -1e9, 5, 10, 10, 10), Array.Empty<string>());
            Assert.Equal(PieceEditor.MaxPosition, far.Min.X);
            Assert.Equal(-PieceEditor.MaxPosition, far.Min.Y);
            Assert.Equal(5, far.Min.Z);
        }

        [Fact]
        public void Not_a_number_never_gets_into_a_board()
        {
            var bad = PieceEditor.Sanitize(P("A", double.NaN, double.PositiveInfinity, 0, double.NaN, 10, 10), Array.Empty<string>());
            Assert.All(new[] { bad.Min.X, bad.Min.Y, bad.Min.Z, bad.Size.X, bad.Size.Y, bad.Size.Z }, v => Assert.True(double.IsFinite(v)));
        }

        // ------------------------------------------------------------- model, templates
        [Fact]
        public void The_custom_piece_becomes_a_model_with_the_boards_in_order()
        {
            var parts = new List<Part> { P("A", 0, 0, 0, 10, 10, 10), P("B", 10, 0, 0, 10, 10, 10) };
            var model = PieceEditor.ToModel("My piece", parts);
            Assert.Equal("My piece", model.Name);
            Assert.Equal(new[] { "A", "B" }, model.Parts.Select(p => p.Name));
            Assert.Equal(new Vec3(20, 10, 10), model.Size);
        }

        [Theory]
        [InlineData("table")]
        [InlineData("chair")]
        [InlineData("bookshelf")]
        [InlineData("cabinet")]
        public void A_preset_can_be_copied_into_the_editor_without_losing_a_board(string id)
        {
            var t = PieceTemplates.Find(id)!;
            var model = t.Build(t.Params.ToDictionary(p => p.Key, p => p.Default));
            var copy = PieceEditor.FromModel(model);
            Assert.Equal(model.Parts.Count, copy.Count);
            Assert.Equal(model.Parts.Select(p => (p.Name, p.Min, p.Size, p.Kind)), copy.Select(p => (p.Name, p.Min, p.Size, p.Kind)));
        }

        [Fact]
        public void The_copy_is_independent_of_the_original_list()
        {
            var model = PieceTemplates.Find("table")!.Build(new Dictionary<string, double>());
            var copy = PieceEditor.FromModel(model).ToList();
            copy.RemoveAt(0);
            Assert.NotEqual(model.Parts.Count, copy.Count);
        }
    }

    public class PieceChecksTests
    {
        private static Part P(string name, double x, double y, double z, double w, double h, double d) =>
            new(name, new Vec3(x, y, z), new Vec3(w, h, d), PartKind.Board);

        [Theory]
        [InlineData("table")]
        [InlineData("chair")]
        [InlineData("bookshelf")]
        [InlineData("cabinet")]
        public void The_presets_pass_their_own_checks(string id)
        {
            var t = PieceTemplates.Find(id)!;
            var parts = t.Build(t.Params.ToDictionary(p => p.Key, p => p.Default)).Parts;
            Assert.Empty(PieceChecks.Overlapping(parts));
            Assert.Empty(PieceChecks.Loose(parts));
        }

        [Fact]
        public void Boards_that_share_space_are_reported_as_a_pair()
        {
            var parts = new[] { P("A", 0, 0, 0, 100, 100, 100), P("B", 50, 0, 0, 100, 100, 100), P("C", 300, 0, 0, 10, 10, 10) };
            var pair = Assert.Single(PieceChecks.Overlapping(parts));
            Assert.Equal(new[] { "A", "B" }, new[] { pair.First, pair.Second }.OrderBy(n => n));
        }

        [Fact]
        public void Boards_that_only_touch_are_not_overlapping()
        {
            var parts = new[] { P("A", 0, 0, 0, 100, 100, 100), P("B", 100, 0, 0, 100, 100, 100) };
            Assert.Empty(PieceChecks.Overlapping(parts));
        }

        [Fact]
        public void A_tiny_overlap_from_rounding_is_not_reported()
        {
            var parts = new[] { P("A", 0, 0, 0, 100, 100, 100), P("B", 99.9999, 0, 0, 100, 100, 100) };
            Assert.Empty(PieceChecks.Overlapping(parts));
        }

        [Fact]
        public void A_board_floating_away_from_the_rest_is_loose()
        {
            var parts = new[] { P("A", 0, 0, 0, 100, 100, 100), P("B", 100, 0, 0, 100, 100, 100), P("Far", 1000, 0, 0, 50, 50, 50) };
            Assert.Equal(new[] { "Far" }, PieceChecks.Loose(parts));
        }

        [Fact]
        public void Of_two_groups_the_smaller_is_the_loose_one()
        {
            var parts = new[]
            {
                P("A", 0, 0, 0, 100, 100, 100), P("B", 100, 0, 0, 100, 100, 100), P("C", 200, 0, 0, 100, 100, 100),
                P("X", 2000, 0, 0, 100, 100, 100), P("Y", 2100, 0, 0, 100, 100, 100),
            };
            Assert.Equal(new[] { "X", "Y" }, PieceChecks.Loose(parts).OrderBy(n => n));
        }

        [Fact]
        public void A_single_board_and_an_empty_piece_have_nothing_loose()
        {
            Assert.Empty(PieceChecks.Loose(Array.Empty<Part>()));
            Assert.Empty(PieceChecks.Loose(new[] { P("A", 0, 0, 0, 10, 10, 10) }));
        }

        [Fact]
        public void Boards_that_only_meet_at_an_edge_hold_together()
        {
            var parts = new[] { P("A", 0, 0, 0, 100, 100, 100), P("B", 100, 100, 100, 50, 50, 50) };   // corner to corner
            Assert.Empty(PieceChecks.Loose(parts));
        }
    }

    public class CustomPieceJsonTests
    {
        private static IReadOnlyList<Part> Sample() => new[]
        {
            new Part("Top", new Vec3(0, 700, 0), new Vec3(1200, 30, 700), PartKind.Top),
            new Part("Leg 1", new Vec3(20.5, 0, 20), new Vec3(60, 700, 60), PartKind.Leg),
        };

        [Fact]
        public void A_design_survives_being_written_and_read_back()
        {
            string json = CustomPieceJson.ToJson("My table", Sample());
            Assert.True(CustomPieceJson.TryRead(json, out string name, out var parts, out _));
            Assert.Equal("My table", name);
            Assert.Equal(Sample().Select(p => (p.Name, p.Min, p.Size, p.Kind)), parts.Select(p => (p.Name, p.Min, p.Size, p.Kind)));
        }

        [Fact]
        public void The_kind_is_written_as_a_word_so_the_file_can_be_read_by_a_person()
        {
            string json = CustomPieceJson.ToJson("x", Sample());
            Assert.Contains("\"Top\"", json);
            Assert.Contains("\"Leg\"", json);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("{ \"Parts\": 5 }")]
        [InlineData("[1,2,3]")]
        [InlineData("null")]
        public void A_file_that_is_not_a_design_is_refused_with_a_reason(string text)
        {
            Assert.False(CustomPieceJson.TryRead(text, out _, out var parts, out string error));
            Assert.Empty(parts);
            Assert.NotEmpty(error);
        }

        [Fact]
        public void A_design_with_no_boards_is_valid_but_empty()
        {
            Assert.True(CustomPieceJson.TryRead("{ \"Name\": \"Empty\", \"Parts\": [] }", out string name, out var parts, out _));
            Assert.Equal("Empty", name);
            Assert.Empty(parts);
        }

        [Fact]
        public void Bad_numbers_and_sizes_in_a_file_are_made_safe()
        {
            string json = "{ \"Name\": \"x\", \"Parts\": [ { \"Name\": \"A\", \"X\": 1e99, \"Y\": 0, \"Z\": 0, \"W\": -5, \"H\": 0, \"D\": 99999999, \"Kind\": \"Board\" } ] }";
            Assert.True(CustomPieceJson.TryRead(json, out _, out var parts, out _));
            var p = Assert.Single(parts);
            Assert.Equal(PieceEditor.MaxPosition, p.Min.X);
            Assert.Equal(PieceEditor.MinSize, p.Size.X);
            Assert.Equal(PieceEditor.MinSize, p.Size.Y);
            Assert.Equal(PieceEditor.MaxSize, p.Size.Z);
        }

        [Fact]
        public void An_unknown_kind_becomes_a_plain_board()
        {
            string json = "{ \"Parts\": [ { \"Name\": \"A\", \"W\": 10, \"H\": 10, \"D\": 10, \"Kind\": \"Spaceship\" } ] }";
            Assert.True(CustomPieceJson.TryRead(json, out _, out var parts, out _));
            Assert.Equal(PartKind.Board, parts[0].Kind);
        }

        [Fact]
        public void Two_boards_with_the_same_name_in_a_file_are_told_apart()
        {
            string json = "{ \"Parts\": [ { \"Name\": \"A\", \"W\": 10, \"H\": 10, \"D\": 10 }, { \"Name\": \"A\", \"W\": 10, \"H\": 10, \"D\": 10 } ] }";
            Assert.True(CustomPieceJson.TryRead(json, out _, out var parts, out _));
            Assert.Equal(new[] { "A", "A 2" }, parts.Select(p => p.Name));
        }

        [Fact]
        public void A_file_cannot_bring_more_boards_than_the_limit()
        {
            var many = string.Join(",", Enumerable.Range(0, PieceEditor.MaxParts + 50).Select(i => $"{{ \"Name\": \"B{i}\", \"W\": 10, \"H\": 10, \"D\": 10 }}"));
            Assert.True(CustomPieceJson.TryRead("{ \"Parts\": [" + many + "] }", out _, out var parts, out _));
            Assert.Equal(PieceEditor.MaxParts, parts.Count);
        }

        [Fact]
        public void A_missing_name_gets_a_default()
        {
            Assert.True(CustomPieceJson.TryRead("{ \"Parts\": [] }", out string name, out _, out _));
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        [Fact]
        public void Settings_keep_the_custom_piece_through_the_same_conversion()
        {
            var dtos = CustomPieceJson.ToDtos(Sample());
            var back = CustomPieceJson.FromDtos(dtos);
            Assert.Equal(Sample().Select(p => (p.Name, p.Min, p.Size, p.Kind)), back.Select(p => (p.Name, p.Min, p.Size, p.Kind)));
        }

        [Fact]
        public void Settings_with_nothing_give_an_empty_piece() =>
            Assert.Empty(CustomPieceJson.FromDtos(null));
    }
}
