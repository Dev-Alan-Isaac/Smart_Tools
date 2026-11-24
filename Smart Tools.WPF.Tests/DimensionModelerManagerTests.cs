using System.IO;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Tests
{
    public class DimensionModelerManagerTests
    {
        private static PieceModel Table()
        {
            var t = PieceTemplates.Find("table")!;
            return t.Build(t.Params.ToDictionary(p => p.Key, p => p.Default));
        }

        private static PrintSettings Print(double ratio = 1, double clearance = 0.2, double bed = 220) => new(ratio, clearance, bed, bed, 250, 4);

        [Fact]
        public void The_extension_follows_the_mode()
        {
            Assert.Equal(".obj", DimensionModelerManager.Extension(ModelerExportMode.AssembledObj));
            Assert.Equal(".obj", DimensionModelerManager.Extension(ModelerExportMode.PrintObj));
            Assert.Equal(".stl", DimensionModelerManager.Extension(ModelerExportMode.AssembledStl));
            Assert.Equal(".stl", DimensionModelerManager.Extension(ModelerExportMode.PrintStl));
        }

        [Fact]
        public void One_plate_keeps_the_name_and_several_are_numbered()
        {
            Assert.Equal(Path.Combine("a", "table.stl"), DimensionModelerManager.PlatePath(Path.Combine("a", "table.stl"), 1, 1));
            Assert.Equal(Path.Combine("a", "table-plate2.stl"), DimensionModelerManager.PlatePath(Path.Combine("a", "table.stl"), 2, 3));
        }

        [Fact]
        public void The_whole_piece_is_one_obj_file_at_the_asked_extension()
        {
            using var d = new TempDir();
            var result = DimensionModelerManager.Export(ModelerExportMode.AssembledObj, d.File("table.stl"), Table(), Print());
            var file = Assert.Single(result.Files);
            Assert.Equal(d.File("table.obj"), file);
            Assert.Contains("g Top", File.ReadAllText(file));
            Assert.Empty(result.TooBig);
        }

        [Fact]
        public void The_whole_piece_as_stl_is_a_binary_file()
        {
            using var d = new TempDir();
            var file = Assert.Single(DimensionModelerManager.Export(ModelerExportMode.AssembledStl, d.File("t"), Table(), Print()).Files);
            Assert.EndsWith(".stl", file);
            var bytes = File.ReadAllBytes(file);
            Assert.Equal((uint)(Table().Parts.Count * 12), BitConverter.ToUInt32(bytes, 80));
        }

        [Fact]
        public void The_print_scale_shrinks_the_whole_piece_file()
        {
            using var d = new TempDir();
            var file = Assert.Single(DimensionModelerManager.Export(ModelerExportMode.AssembledObj, d.File("t.obj"), Table(), Print(ratio: 10)).Files);
            double maxX = File.ReadAllLines(file).Where(l => l.StartsWith("v ")).Max(l => double.Parse(l.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(120, maxX, 3);   // a 1200 mm wide table at 1:10
        }

        [Fact]
        public void A_scaled_table_prints_on_one_plate_with_every_part()
        {
            using var d = new TempDir();
            var result = DimensionModelerManager.Export(ModelerExportMode.PrintStl, d.File("t.stl"), Table(), Print(ratio: 10));
            Assert.Single(result.Files);
            Assert.Empty(result.TooBig);
            var bytes = File.ReadAllBytes(result.Files[0]);
            Assert.Equal((uint)(Table().Parts.Count * 12), BitConverter.ToUInt32(bytes, 80));
        }

        [Fact]
        public void A_full_size_table_has_nothing_that_fits_the_bed_and_says_so()
        {
            using var d = new TempDir();
            var result = DimensionModelerManager.Export(ModelerExportMode.PrintStl, d.File("t.stl"), Table(), Print(ratio: 1, bed: 100));
            Assert.NotEmpty(result.TooBig);
            Assert.Equal(Table().Parts.Count, result.Files.Count == 0 ? result.TooBig.Count : result.TooBig.Count + 99);
        }

        [Fact]
        public void Several_plates_become_several_numbered_files()
        {
            using var d = new TempDir();
            var model = new PieceModel("Boards", Enumerable.Range(1, 3).Select(i => new Part($"B{i}", Vec3.Zero, new Vec3(10, 200, 200), PartKind.Board)).ToList());
            var result = DimensionModelerManager.Export(ModelerExportMode.PrintObj, d.File("boards.obj"), model, Print(clearance: 0));
            Assert.Equal(new[] { "boards-plate1.obj", "boards-plate2.obj", "boards-plate3.obj" }, result.Files.Select(Path.GetFileName));
        }

        [Fact]
        public void An_existing_file_is_never_overwritten()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("t.obj"), "keep me");
            var file = Assert.Single(DimensionModelerManager.Export(ModelerExportMode.AssembledObj, d.File("t.obj"), Table(), Print()).Files);
            Assert.Equal(d.File("t (2).obj"), file);
            Assert.Equal("keep me", File.ReadAllText(d.File("t.obj")));
        }

        [Fact]
        public void Settings_round_trip_through_a_file_with_text_enums()
        {
            using var d = new TempDir();
            var settings = new ModelerSettings { Template = "chair", Unit = LengthUnit.Inches, ScaleRatio = 10, ClearanceMm = 0.3 };
            settings.Values["chair"] = new Dictionary<string, double> { ["width"] = 450 };
            DimensionModelerManager.SaveSettings(settings, d.File("s.json"));
            Assert.Contains("Inches", File.ReadAllText(d.File("s.json")));
            var back = DimensionModelerManager.LoadSettings(d.File("s.json"));
            Assert.Equal(("chair", LengthUnit.Inches, 10.0, 450.0), (back.Template, back.Unit, back.ScaleRatio, back.Values["chair"]["width"]));
        }

        [Fact]
        public void A_missing_settings_file_gives_the_defaults()
        {
            using var d = new TempDir();
            var s = DimensionModelerManager.LoadSettings(d.File("none.json"));
            Assert.Equal(("table", true, 1.0), (s.Template, s.ShowDimensions, s.ScaleRatio));
        }
    }
}
