using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>The piece templates: whatever the sliders say, the piece must be a valid, connected, non-overlapping set of boards.</summary>
    public class PieceTemplatesTests
    {
        private const double Eps = 1e-6;

        public static IEnumerable<object[]> Ids() => PieceTemplates.All.Select(t => new object[] { t.Id });

        private static Dictionary<string, double> Defaults(PieceTemplate t) => t.Params.ToDictionary(p => p.Key, p => p.Default);

        /// <summary>Every combination of "each slider at its minimum or maximum" (the corners of the parameter space).</summary>
        private static IEnumerable<Dictionary<string, double>> Corners(PieceTemplate t)
        {
            int n = t.Params.Count;
            for (int mask = 0; mask < 1 << n; mask++)
                yield return t.Params.Select((p, i) => (p, i)).ToDictionary(x => x.p.Key, x => (mask >> x.i & 1) == 0 ? x.p.Min : x.p.Max);
        }

        private static double Overlap(Part a, Part b)
        {
            double Axis(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
            return Axis(a.Min.X, a.Max.X, b.Min.X, b.Max.X) * Axis(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y) * Axis(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
        }

        private static bool Touches(Part a, Part b)
        {
            bool Axis(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0) >= -Eps;
            return Axis(a.Min.X, a.Max.X, b.Min.X, b.Max.X) && Axis(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y) && Axis(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
        }

        [Fact]
        public void The_templates_are_listed_with_unique_ids() =>
            Assert.Equal(new[] { "table", "chair", "bookshelf", "cabinet", "filing-cabinet", "door", "desk", "desk-l", "shelf", "kitchen-base", "wall-cabinet", "wardrobe", "sideboard" }, PieceTemplates.All.Select(t => t.Id));

        [Theory]
        [MemberData(nameof(Ids))]
        public void Find_returns_the_template_by_id(string id) => Assert.Equal(id, PieceTemplates.Find(id)?.Id);

        [Fact]
        public void Find_of_an_unknown_id_is_null() => Assert.Null(PieceTemplates.Find("sofa"));

        [Theory]
        [MemberData(nameof(Ids))]
        public void Defaults_build_a_valid_piece(string id)
        {
            var t = PieceTemplates.Find(id)!;
            var model = t.Build(Defaults(t));
            Assert.NotEmpty(model.Parts);
            foreach (var p in model.Parts)
            {
                Assert.True(p.Size.X > 0 && p.Size.Y > 0 && p.Size.Z > 0, $"{p.Name} has no volume");
                Assert.True(p.Min.Y >= -Eps, $"{p.Name} is below the floor");
            }
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void Every_corner_of_the_parameter_space_builds_a_valid_piece(string id)
        {
            var t = PieceTemplates.Find(id)!;
            foreach (var values in Corners(t))
            {
                var model = t.Build(values);
                string where = string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}"));
                Assert.NotEmpty(model.Parts);
                foreach (var p in model.Parts)
                {
                    Assert.True(p.Size.X > 0.5 && p.Size.Y > 0.5 && p.Size.Z > 0.5, $"{id}: {p.Name} too thin ({where})");
                    Assert.True(p.Min.Y >= -Eps, $"{id}: {p.Name} below the floor ({where})");
                    Assert.False(double.IsNaN(p.Min.X + p.Min.Y + p.Min.Z + p.Size.X + p.Size.Y + p.Size.Z), $"{id}: NaN ({where})");
                }
            }
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void Boards_never_overlap_each_other(string id)
        {
            var t = PieceTemplates.Find(id)!;
            foreach (var values in Corners(t).Append(Defaults(t)))
            {
                var parts = t.Build(values).Parts;
                for (int i = 0; i < parts.Count; i++)
                    for (int j = i + 1; j < parts.Count; j++)
                        Assert.True(Overlap(parts[i], parts[j]) < 1e-3 || (parts[i].Bevels.Count + parts[j].Bevels.Count > 0 && !PieceBevels.SolidsOverlap(parts[i], parts[j])),
                            $"{id}: {parts[i].Name} overlaps {parts[j].Name} by {Overlap(parts[i], parts[j])}");
            }
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void The_piece_holds_together_every_part_touches_the_rest(string id)
        {
            var t = PieceTemplates.Find(id)!;
            foreach (var values in Corners(t).Append(Defaults(t)))
            {
                var parts = t.Build(values).Parts;
                var seen = new HashSet<int> { 0 };
                var queue = new Queue<int>(new[] { 0 });
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    for (int j = 0; j < parts.Count; j++)
                        if (!seen.Contains(j) && Touches(parts[i], parts[j])) { seen.Add(j); queue.Enqueue(j); }
                }
                Assert.True(seen.Count == parts.Count, $"{id}: {parts.Count - seen.Count} loose part(s) ({string.Join(", ", values.Select(kv => kv.Key + "=" + kv.Value))})");
            }
        }

        [Fact]
        public void The_table_is_as_big_as_its_sliders_say()
        {
            var t = PieceTemplates.Find("table")!;
            var v = Defaults(t);
            v["width"] = 1400; v["depth"] = 800; v["height"] = 720;
            var size = t.Build(v).Size;
            Assert.Equal((1400, 720, 800), (size.X, size.Y, size.Z));
        }

        [Fact]
        public void The_bookshelf_is_as_big_as_its_sliders_say()
        {
            var t = PieceTemplates.Find("bookshelf")!;
            var v = Defaults(t);
            v["width"] = 900; v["height"] = 2000; v["depth"] = 320;
            var size = t.Build(v).Size;
            Assert.Equal((900, 2000, 320), (size.X, size.Y, size.Z));
        }

        [Fact]
        public void The_cabinet_is_as_big_as_its_sliders_say()
        {
            var t = PieceTemplates.Find("cabinet")!;
            var v = Defaults(t);
            v["width"] = 1000; v["height"] = 900; v["depth"] = 500;
            var size = t.Build(v).Size;
            Assert.Equal((1000, 900, 500), (size.X, size.Y, size.Z));
        }

        [Fact]
        public void The_chair_is_as_big_as_its_sliders_say()
        {
            var t = PieceTemplates.Find("chair")!;
            var v = Defaults(t);
            v["width"] = 440; v["depth"] = 430; v["seatHeight"] = 460; v["backHeight"] = 400;
            var size = t.Build(v).Size;
            Assert.Equal((440, 860, 430), (size.X, size.Y, size.Z));
        }

        [Fact]
        public void The_bookshelf_has_as_many_shelves_as_asked()
        {
            var t = PieceTemplates.Find("bookshelf")!;
            var v = Defaults(t);
            v["shelves"] = 3;
            Assert.Equal(3, t.Build(v).Parts.Count(p => p.Name.StartsWith("Shelf")));
            v["shelves"] = 0;
            Assert.Equal(0, t.Build(v).Parts.Count(p => p.Name.StartsWith("Shelf")));
        }

        [Fact]
        public void The_cabinet_has_one_or_two_doors()
        {
            var t = PieceTemplates.Find("cabinet")!;
            var v = Defaults(t);
            v["doors"] = 1;
            Assert.Equal(1, t.Build(v).Parts.Count(p => p.Name.StartsWith("Door")));
            v["doors"] = 2;
            Assert.Equal(2, t.Build(v).Parts.Count(p => p.Name.StartsWith("Door")));
        }

        [Fact]
        public void The_table_has_four_legs_and_a_top()
        {
            var parts = PieceTemplates.Find("table")!.Build(Defaults(PieceTemplates.Find("table")!)).Parts;
            Assert.Equal(4, parts.Count(p => p.Kind == PartKind.Leg));
            Assert.Single(parts, p => p.Name == "Top");
        }

        [Fact]
        public void No_apron_when_its_height_is_zero()
        {
            var t = PieceTemplates.Find("table")!;
            var v = Defaults(t);
            v["apron"] = 0;
            Assert.DoesNotContain(t.Build(v).Parts, p => p.Name.StartsWith("Apron"));
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void Sliders_out_of_range_are_pulled_back_into_range(string id)
        {
            var t = PieceTemplates.Find(id)!;
            var wild = t.Params.ToDictionary(p => p.Key, p => p.Max * 10 + 1000);
            var tame = t.Params.ToDictionary(p => p.Key, p => p.Max);
            var a = t.Build(wild);
            var b = t.Build(tame);
            Assert.Equal(b.Size, a.Size);
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void Missing_sliders_use_their_defaults(string id)
        {
            var t = PieceTemplates.Find(id)!;
            Assert.Equal(t.Build(Defaults(t)).Size, t.Build(new Dictionary<string, double>()).Size);
        }

        [Fact]
        public void Whole_number_sliders_are_rounded()
        {
            var t = PieceTemplates.Find("bookshelf")!;
            var v = Defaults(t);
            v["shelves"] = 3.6;
            Assert.Equal(4, t.Build(v).Parts.Count(p => p.Name.StartsWith("Shelf")));
        }

        [Fact]
        public void Slider_specs_are_sane()
        {
            foreach (var t in PieceTemplates.All)
            {
                Assert.Equal(t.Params.Count, t.Params.Select(p => p.Key).Distinct().Count());
                foreach (var p in t.Params)
                {
                    Assert.True(p.Min < p.Max, $"{t.Id}.{p.Key}");
                    Assert.InRange(p.Default, p.Min, p.Max);
                    Assert.True(p.Step > 0);
                }
            }
        }
    }
}
