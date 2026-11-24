using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Tests
{
    public class PrintPlannerTests
    {
        private static Part Board(string name, double x, double y, double z) => new(name, new Vec3(0, 0, 0), new Vec3(x, y, z), PartKind.Board);

        private static PieceModel Model(params Part[] parts) => new("Test", parts);

        private static PrintSettings Bed(double ratio = 1, double clearance = 0, double x = 220, double y = 220, double h = 250) =>
            new(ratio, clearance, x, y, h, 4);

        private static bool Overlap(PlacedPart a, PlacedPart b) =>
            a.Min.X < b.Min.X + b.Size.X && b.Min.X < a.Min.X + a.Size.X && a.Min.Z < b.Min.Z + b.Size.Z && b.Min.Z < a.Min.Z + a.Size.Z;

        // ------------------------------------------------------------- scale and transform
        [Fact]
        public void Scaling_a_model_scales_every_position_and_size()
        {
            var scaled = PieceTransform.Scale(Model(new Part("A", new Vec3(100, 200, 300), new Vec3(10, 20, 30), PartKind.Board)), 0.1);
            var p = scaled.Parts[0];
            Assert.Equal((10, 20, 30), (p.Min.X, p.Min.Y, p.Min.Z));
            Assert.Equal((1, 2, 3), (p.Size.X, p.Size.Y, p.Size.Z));
        }

        [Fact]
        public void A_scale_of_one_to_ten_is_a_tenth() => Assert.Equal(0.1, Bed(ratio: 10).Scale, 12);

        [Fact]
        public void A_ratio_below_one_is_treated_as_full_size() => Assert.Equal(1.0, Bed(ratio: 0).Scale, 12);

        // ------------------------------------------------------------- one part
        [Fact]
        public void A_board_is_laid_flat_with_its_thinnest_side_up()
        {
            var plan = PrintPlanner.Plan(Model(Board("Shelf", 18, 400, 250)), Bed(x: 500, y: 500));
            var placed = Assert.Single(Assert.Single(plan.Plates).Parts);
            Assert.Equal(18, placed.Size.Y, 9);                 // height = thinnest side
            Assert.Equal(400, Math.Max(placed.Size.X, placed.Size.Z), 9);
            Assert.Equal(250, Math.Min(placed.Size.X, placed.Size.Z), 9);
        }

        [Fact]
        public void The_clearance_makes_each_part_smaller_in_every_direction()
        {
            var plan = PrintPlanner.Plan(Model(Board("A", 20, 50, 100)), Bed(clearance: 0.4));
            var p = plan.Plates[0].Parts[0];
            var dims = new[] { p.Size.X, p.Size.Y, p.Size.Z }.OrderBy(d => d).ToArray();
            Assert.Equal(new[] { 19.6, 49.6, 99.6 }, dims.Select(d => Math.Round(d, 6)));
        }

        [Fact]
        public void The_clearance_never_shrinks_a_part_to_nothing()
        {
            var plan = PrintPlanner.Plan(Model(Board("Tiny", 0.3, 5, 5)), Bed(clearance: 1));
            Assert.True(plan.Plates[0].Parts[0].Size.Y >= 0.2);
        }

        [Fact]
        public void The_scale_is_applied_before_the_clearance()
        {
            // 1:10 of 100 mm is 10 mm, minus 0.2 clearance
            var plan = PrintPlanner.Plan(Model(Board("A", 100, 100, 100)), Bed(ratio: 10, clearance: 0.2));
            Assert.All(new[] { plan.Plates[0].Parts[0].Size.X, plan.Plates[0].Parts[0].Size.Y, plan.Plates[0].Parts[0].Size.Z }, d => Assert.Equal(9.8, d, 9));
        }

        // ------------------------------------------------------------- the bed
        [Fact]
        public void A_part_too_big_for_the_bed_is_reported_not_dropped()
        {
            var plan = PrintPlanner.Plan(Model(Board("Long", 18, 1200, 400)), Bed());
            Assert.Empty(plan.Plates);
            Assert.Equal("Long", Assert.Single(plan.TooBig).Name);
        }

        [Fact]
        public void A_part_too_tall_for_the_bed_is_reported()
        {
            var plan = PrintPlanner.Plan(Model(Board("Block", 300, 300, 300)), Bed(h: 250));
            Assert.Single(plan.TooBig);
        }

        [Fact]
        public void A_part_that_only_fits_turned_a_quarter_is_turned()
        {
            // 200 x 100 on a 120 wide, 220 deep bed: only fits with the long side along the depth
            var plan = PrintPlanner.Plan(Model(Board("Plank", 10, 200, 100)), Bed(x: 120, y: 220));
            var placed = Assert.Single(plan.Plates.Single().Parts);
            Assert.True(placed.Size.X <= 120 && placed.Size.Z <= 220);
            Assert.True(placed.Rotated);
        }

        [Fact]
        public void Parts_that_fit_together_share_a_plate_without_overlapping_and_inside_the_bed()
        {
            var parts = Enumerable.Range(1, 6).Select(i => Board($"P{i}", 10, 60, 40)).ToArray();
            var plan = PrintPlanner.Plan(Model(parts), Bed());
            var plate = Assert.Single(plan.Plates);
            Assert.Equal(6, plate.Parts.Count);
            foreach (var a in plate.Parts)
            {
                Assert.True(a.Min.X >= 0 && a.Min.Z >= 0 && a.Min.X + a.Size.X <= 220 && a.Min.Z + a.Size.Z <= 220);
                foreach (var b in plate.Parts.Where(b => !ReferenceEquals(a, b)))
                    Assert.False(Overlap(a, b), $"{a.Part.Name} overlaps {b.Part.Name}");
            }
        }

        [Fact]
        public void Parts_keep_a_gap_between_them()
        {
            var plan = PrintPlanner.Plan(Model(Board("A", 10, 100, 100), Board("B", 10, 100, 100)), Bed());
            var p = plan.Plates.Single().Parts;
            double gap = Math.Max(Math.Abs(p[0].Min.X - (p[1].Min.X + p[1].Size.X)), Math.Abs(p[1].Min.X - (p[0].Min.X + p[0].Size.X)));
            double gapZ = Math.Max(Math.Abs(p[0].Min.Z - (p[1].Min.Z + p[1].Size.Z)), Math.Abs(p[1].Min.Z - (p[0].Min.Z + p[0].Size.Z)));
            Assert.True(Math.Max(gap, gapZ) >= 4 - 1e-9);
        }

        [Fact]
        public void More_parts_than_one_bed_holds_start_another_plate()
        {
            var parts = Enumerable.Range(1, 5).Select(i => Board($"P{i}", 10, 200, 200)).ToArray();   // one per plate
            var plan = PrintPlanner.Plan(Model(parts), Bed());
            Assert.Equal(5, plan.Plates.Count);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, plan.Plates.Select(p => p.Number));
            Assert.Equal(5, plan.PlacedCount);
        }

        [Fact]
        public void Every_part_ends_up_on_a_plate_or_in_the_too_big_list()
        {
            var parts = new[] { Board("A", 10, 100, 100), Board("B", 10, 1000, 1000), Board("C", 10, 50, 50) };
            var plan = PrintPlanner.Plan(Model(parts), Bed());
            Assert.Equal(3, plan.PlacedCount + plan.TooBig.Count);
        }

        [Fact]
        public void Planning_is_repeatable()
        {
            var parts = Enumerable.Range(1, 8).Select(i => Board($"P{i}", 10, 30 + i * 7, 20 + i * 5)).ToArray();
            var a = PrintPlanner.Plan(Model(parts), Bed());
            var b = PrintPlanner.Plan(Model(parts), Bed());
            Assert.Equal(a.Plates.SelectMany(p => p.Parts).Select(p => (p.Part.Name, p.Min)), b.Plates.SelectMany(p => p.Parts).Select(p => (p.Part.Name, p.Min)));
        }

        [Fact]
        public void A_scaled_down_piece_fits_where_the_full_size_one_does_not()
        {
            var table = PieceTemplates.Find("table")!;
            var model = table.Build(table.Params.ToDictionary(p => p.Key, p => p.Default));
            Assert.NotEmpty(PrintPlanner.Plan(model, Bed(ratio: 1)).TooBig);
            Assert.Empty(PrintPlanner.Plan(model, Bed(ratio: 10)).TooBig);
        }

        [Fact]
        public void A_plate_becomes_a_model_of_its_placed_parts()
        {
            var plan = PrintPlanner.Plan(Model(Board("A", 10, 100, 100), Board("B", 10, 50, 50)), Bed());
            var model = PrintPlanner.PlateModel(plan.Plates[0], "Test");
            Assert.Equal(2, model.Parts.Count);
            Assert.Equal(new[] { "A", "B" }.OrderBy(n => n), model.Parts.Select(p => p.Name).OrderBy(n => n));
        }
    }

    public class DimensionLineTests
    {
        private static readonly PieceModel Box = new("Box", new[] { new Part("A", new Vec3(100, 0, 50), new Vec3(1200, 750, 700), PartKind.Board) });

        [Fact]
        public void The_overall_dimensions_are_width_height_and_depth()
        {
            var lines = DimensionLines.Overall(Box, 80);
            Assert.Equal(new[] { 1200.0, 750.0, 700.0 }, lines.Select(l => l.LengthMm));
        }

        [Fact]
        public void Each_line_runs_along_one_axis()
        {
            foreach (var l in DimensionLines.Overall(Box, 80))
            {
                var d = l.B - l.A;
                int axes = (Math.Abs(d.X) > 1e-9 ? 1 : 0) + (Math.Abs(d.Y) > 1e-9 ? 1 : 0) + (Math.Abs(d.Z) > 1e-9 ? 1 : 0);
                Assert.Equal(1, axes);
            }
        }

        [Fact]
        public void The_line_is_drawn_away_from_the_piece_by_the_offset()
        {
            var width = DimensionLines.Overall(Box, 80)[0];
            Assert.Equal(80, (width.Start - width.A).Length, 9);
            Assert.Equal(80, (width.End - width.B).Length, 9);
        }

        [Fact]
        public void The_measured_points_are_on_the_piece()
        {
            foreach (var l in DimensionLines.Overall(Box, 80))
                foreach (var p in new[] { l.A, l.B })
                {
                    Assert.InRange(p.X, 100, 1300);
                    Assert.InRange(p.Y, 0, 750);
                    Assert.InRange(p.Z, 50, 750);
                }
        }

        [Fact]
        public void One_part_has_its_own_three_dimensions()
        {
            var lines = DimensionLines.ForPart(Box.Parts[0], 40);
            Assert.Equal(new[] { 1200.0, 750.0, 700.0 }, lines.Select(l => l.LengthMm));
        }

        [Fact]
        public void An_empty_piece_has_no_dimensions() =>
            Assert.Empty(DimensionLines.Overall(new PieceModel("none", Array.Empty<Part>()), 80));
    }

    public class LengthFormatTests
    {
        [Theory]
        [InlineData(1200, LengthUnit.Millimetres, "1200 mm")]
        [InlineData(12.5, LengthUnit.Millimetres, "12.5 mm")]
        [InlineData(12.04, LengthUnit.Millimetres, "12 mm")]
        [InlineData(1200, LengthUnit.Centimetres, "120 cm")]
        [InlineData(756, LengthUnit.Centimetres, "75.6 cm")]
        [InlineData(25.4, LengthUnit.Inches, "1 in")]
        [InlineData(1200, LengthUnit.Inches, "47.24 in")]
        [InlineData(0, LengthUnit.Millimetres, "0 mm")]
        public void Formats_in_the_chosen_unit(double mm, LengthUnit unit, string expected) =>
            Assert.Equal(expected, LengthFormat.Format(mm, unit));

        [Fact]
        public void The_decimal_separator_is_always_a_dot()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            try { Assert.Equal("12.5 mm", LengthFormat.Format(12.5, LengthUnit.Millimetres)); }
            finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        }
    }

    public class CutListTests
    {
        [Fact]
        public void Identical_boards_are_one_row_with_a_quantity()
        {
            var t = PieceTemplates.Find("table")!;
            var model = t.Build(t.Params.ToDictionary(p => p.Key, p => p.Default));
            var legs = CutList.From(model).Single(r => r.Name == "Leg");
            Assert.Equal(4, legs.Quantity);
        }

        [Fact]
        public void Sizes_are_listed_longest_first()
        {
            var model = new PieceModel("x", new[] { new Part("Board", Vec3.Zero, new Vec3(18, 400, 250), PartKind.Board) });
            var row = CutList.From(model).Single();
            Assert.Equal((400.0, 250.0, 18.0), (row.Length, row.Width, row.Thickness));
        }

        [Fact]
        public void The_same_board_lying_the_other_way_still_counts_as_the_same_cut()
        {
            var model = new PieceModel("x", new[]
            {
                new Part("Shelf 1", Vec3.Zero, new Vec3(18, 400, 250), PartKind.Board),
                new Part("Shelf 2", Vec3.Zero, new Vec3(400, 18, 250), PartKind.Board),
            });
            Assert.Equal(2, Assert.Single(CutList.From(model)).Quantity);
        }

        [Fact]
        public void Numbered_names_group_under_their_plain_name()
        {
            var model = new PieceModel("x", new[]
            {
                new Part("Shelf 1", Vec3.Zero, new Vec3(18, 400, 250), PartKind.Board),
                new Part("Shelf 2", Vec3.Zero, new Vec3(18, 400, 250), PartKind.Board),
                new Part("Side Left", Vec3.Zero, new Vec3(18, 1800, 300), PartKind.Panel),
            });
            Assert.Equal(new[] { "Shelf", "Side Left" }, CutList.From(model).Select(r => r.Name).OrderBy(n => n));
        }

        [Fact]
        public void Different_sizes_under_one_name_are_separate_rows()
        {
            var model = new PieceModel("x", new[]
            {
                new Part("Apron 1", Vec3.Zero, new Vec3(900, 70, 20), PartKind.Board),
                new Part("Apron 2", Vec3.Zero, new Vec3(500, 70, 20), PartKind.Board),
            });
            Assert.Equal(2, CutList.From(model).Count);
        }

        [Fact]
        public void The_rows_add_up_to_every_part()
        {
            foreach (var t in PieceTemplates.All)
            {
                var model = t.Build(t.Params.ToDictionary(p => p.Key, p => p.Default));
                Assert.Equal(model.Parts.Count, CutList.From(model).Sum(r => r.Quantity));
            }
        }

        [Fact]
        public void The_total_volume_is_the_sum_of_the_boards()
        {
            var model = new PieceModel("x", new[]
            {
                new Part("A", Vec3.Zero, new Vec3(100, 10, 10), PartKind.Board),
                new Part("B", Vec3.Zero, new Vec3(200, 10, 10), PartKind.Board),
            });
            Assert.Equal(30000, CutList.TotalVolumeMm3(model), 6);
        }

        [Fact]
        public void A_row_knows_which_parts_it_stands_for()
        {
            var model = new PieceModel("x", new[]
            {
                new Part("Leg 1", Vec3.Zero, new Vec3(60, 700, 60), PartKind.Leg),
                new Part("Leg 2", Vec3.Zero, new Vec3(60, 700, 60), PartKind.Leg),
            });
            Assert.Equal(new[] { "Leg 1", "Leg 2" }, CutList.From(model).Single().PartNames);
        }
    }

    public class OrbitCameraTests
    {
        private const double W = 800, H = 600;

        [Fact]
        public void The_target_is_in_the_middle_of_the_view()
        {
            var cam = new OrbitCamera { Target = new Vec3(100, 200, 300), Distance = 2000 };
            var p = cam.Project(cam.Target, W, H)!.Value;
            Assert.Equal(W / 2, p.X, 6);
            Assert.Equal(H / 2, p.Y, 6);
        }

        [Fact]
        public void A_point_to_the_right_of_the_target_is_right_of_centre_and_up_is_up_the_screen()
        {
            var cam = new OrbitCamera { Yaw = 0, Pitch = 0, Distance = 2000 };
            var right = cam.Project(new Vec3(100, 0, 0), W, H)!.Value;
            var up = cam.Project(new Vec3(0, 100, 0), W, H)!.Value;
            Assert.True(right.X > W / 2);
            Assert.True(up.Y < H / 2);   // screen Y grows downwards
        }

        [Fact]
        public void A_point_behind_the_camera_does_not_project()
        {
            var cam = new OrbitCamera { Yaw = 0, Pitch = 0, Distance = 1000 };
            Assert.Null(cam.Project(new Vec3(0, 0, 5000), W, H));
        }

        [Fact]
        public void The_camera_looks_at_the_target_from_its_distance()
        {
            var cam = new OrbitCamera { Target = new Vec3(10, 20, 30), Yaw = 40, Pitch = 25, Distance = 1500 };
            Assert.Equal(1500, (cam.Position - cam.Target).Length, 6);
            Assert.Equal(1.0, Vec3.Dot(cam.LookDirection.Normalized, (cam.Target - cam.Position).Normalized), 9);
        }

        [Fact]
        public void Orbiting_changes_the_angles_and_keeps_the_pitch_off_the_poles()
        {
            var cam = new OrbitCamera { Yaw = 10, Pitch = 10 };
            cam.Orbit(30, 500);
            Assert.Equal(40, cam.Yaw, 9);
            Assert.Equal(OrbitCamera.MaxPitch, cam.Pitch, 9);
            cam.Orbit(0, -5000);
            Assert.Equal(OrbitCamera.MinPitch, cam.Pitch, 9);
        }

        [Fact]
        public void Zooming_stays_between_the_limits()
        {
            var cam = new OrbitCamera { Distance = 1000 };
            cam.Zoom(0.001);
            Assert.Equal(OrbitCamera.MinDistance, cam.Distance, 9);
            cam.Zoom(1e9);
            Assert.Equal(OrbitCamera.MaxDistance, cam.Distance, 9);
        }

        [Fact]
        public void Zooming_in_makes_things_bigger_on_screen()
        {
            var cam = new OrbitCamera { Distance = 3000 };
            var p = new Vec3(200, 0, 0);
            double before = cam.Project(p, W, H)!.Value.X - W / 2;
            cam.Zoom(0.5);
            double after = cam.Project(p, W, H)!.Value.X - W / 2;
            Assert.True(after > before);
        }

        [Fact]
        public void Panning_moves_the_target_without_changing_the_distance()
        {
            var cam = new OrbitCamera { Yaw = 0, Pitch = 0, Distance = 2000 };
            var before = cam.Target;
            cam.Pan(100, 0, H);
            Assert.NotEqual(before, cam.Target);
            Assert.Equal(2000, cam.Distance, 9);
            Assert.Equal(0, cam.Target.Y, 6);   // a sideways drag at level pitch stays level
        }

        [Fact]
        public void Fitting_a_box_puts_all_its_corners_inside_the_view()
        {
            var cam = new OrbitCamera();
            var min = new Vec3(0, 0, 0);
            var size = new Vec3(1400, 750, 800);
            cam.FitTo(min, size, W, H);
            foreach (var x in new[] { 0.0, size.X })
                foreach (var y in new[] { 0.0, size.Y })
                    foreach (var z in new[] { 0.0, size.Z })
                    {
                        var p = cam.Project(new Vec3(x, y, z), W, H);
                        Assert.NotNull(p);
                        Assert.InRange(p!.Value.X, 0, W);
                        Assert.InRange(p.Value.Y, 0, H);
                    }
        }

        [Fact]
        public void Fitting_also_works_in_a_tall_narrow_view()
        {
            var cam = new OrbitCamera();
            cam.FitTo(Vec3.Zero, new Vec3(900, 1800, 300), 300, 700);
            foreach (var x in new[] { 0.0, 900 })
                foreach (var y in new[] { 0.0, 1800 })
                    foreach (var z in new[] { 0.0, 300 })
                    {
                        var p = cam.Project(new Vec3(x, y, z), 300, 700)!.Value;
                        Assert.InRange(p.X, 0, 300);
                        Assert.InRange(p.Y, 0, 700);
                    }
        }
    }
}
