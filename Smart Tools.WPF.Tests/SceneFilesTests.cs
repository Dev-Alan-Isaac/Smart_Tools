using System.IO;
using System.Text;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>OBJ / STL out of the finished design, exact Mesh shapes in, and old settings moved to a design.</summary>
    public class SceneFilesTests
    {
        private static SceneShape Box(string name, Vec3 centre, Vec3 size, bool hole = false) =>
            new(SceneEdit.NewId(), name, ShapeKind.Box, centre, size, Vec3.Zero, IsHole: hole);

        private static Scene Plate()
        {
            var plate = Box("Plate", new Vec3(0, 5, 0), new Vec3(100, 10, 60));
            var hole = new SceneShape(SceneEdit.NewId(), "Hole", ShapeKind.Cylinder, new Vec3(0, 5, 0), new Vec3(20, 12, 20), Vec3.Zero, IsHole: true);
            var leg = Box("Leg", new Vec3(200, 50, 0), new Vec3(20, 100, 20));
            return new Scene("Test", new SceneNode[] { new GroupNode("g", "Holed plate", new SceneNode[] { plate, hole }), leg }, new Dictionary<string, MeshData>());
        }

        private static double Volume(IReadOnlyList<SceneShape> shapes, IReadOnlyDictionary<string, MeshData> meshes) =>
            new SceneMeshBuilder().Build(new Scene("x", shapes.ToList(), meshes)).Sum(o => Csg.Volume(o.Triangles));

        [Fact]
        public void An_exported_stl_comes_back_as_the_same_solids_with_the_holes_cut()
        {
            var objects = new SceneMeshBuilder().Build(Plate(), includeHidden: true).Where(o => !o.IsHole).ToList();
            byte[] stl = SceneFiles.ToStl(objects.SelectMany(o => o.Triangles.Select(t => t.Triangle)));
            Assert.True(SceneFiles.TryImport("t.stl", stl, "Part", out var imported, out string error), error);
            Assert.Equal(2, imported.Shapes.Count);
            Assert.All(imported.Shapes, s => Assert.Equal(ShapeKind.Mesh, s.Kind));
            double expected = 100 * 10 * 60 - SceneCsgTests.NGon(32, 10) * 10 + 20 * 100 * 20;
            Assert.Equal(expected, Volume(imported.Shapes, imported.Meshes), 0);   // float STL: within 1 mm3
            Assert.False(imported.FromMetres);
        }

        [Fact]
        public void An_exported_obj_keeps_the_object_names_and_the_scale()
        {
            var scene = Plate();
            var objects = new SceneMeshBuilder().Build(scene, includeHidden: true).Where(o => !o.IsHole).ToList();
            string obj = SceneFiles.ToObj(scene.Name, objects.Select(o => (SceneEdit.Find(scene, o.NodeId)!.Name, o.Triangles)), 0.5);
            Assert.Contains("g Holed_plate", obj);
            Assert.Contains("g Leg", obj);
            Assert.True(SceneFiles.TryImport("t.obj", Encoding.UTF8.GetBytes(obj), "Part", out var imported, out string error), error);
            Assert.Equal(new[] { "Holed plate", "Leg" }, imported.Shapes.Select(s => s.Name).OrderBy(n => n));
            var leg = imported.Shapes.Single(s => s.Name == "Leg");
            Assert.Equal(10, leg.Size.X, 3);
            Assert.Equal(50, leg.Size.Y, 3);
        }

        [Fact]
        public void An_import_stands_on_the_floor_in_the_middle_and_a_tiny_one_is_read_as_metres()
        {
            // a 0.2 m cube floating 3 m up, off to the side
            var cube = new SceneMeshBuilder().Build(new Scene("c", new SceneNode[] { Box("C", new Vec3(5, 3, -2), new Vec3(0.2, 0.2, 0.2)) }, new Dictionary<string, MeshData>())).Single();
            var stl = SceneFiles.ToStl(cube.Triangles.Select(t => t.Triangle));
            Assert.True(SceneFiles.TryImport("c.stl", stl, "Cube", out var imported, out _));
            var shape = Assert.Single(imported.Shapes);
            Assert.True(imported.FromMetres);
            Assert.Equal("Cube", shape.Name);
            var b = Primitives.Bounds(shape, imported.Meshes);
            Assert.Equal(0, b.Min.Y, 3);
            Assert.Equal(0, b.Centre.X, 3);
            Assert.Equal(0, b.Centre.Z, 3);
            Assert.Equal(200, b.Size.X, 2);
        }

        [Fact]
        public void A_flat_imported_object_keeps_a_thickness()
        {
            const string obj = "v 0 0 0\nv 100 0 0\nv 100 0 50\nf 1 2 3\n";
            Assert.True(SceneFiles.TryImport("flat.obj", Encoding.UTF8.GetBytes(obj), "Sheet", out var imported, out _));
            Assert.Equal(SceneEdit.MinSize, Assert.Single(imported.Shapes).Size.Y);
        }

        [Theory]
        [InlineData("a.3mf", "v 0 0 0")]
        [InlineData("a.obj", "# nothing here\n")]
        public void Files_that_cannot_be_imported_are_refused(string file, string text)
        {
            Assert.False(SceneFiles.TryImport(file, Encoding.UTF8.GetBytes(text), "Part", out _, out string error));
            Assert.NotEmpty(error);
        }

        [Fact]
        public void A_hole_alone_is_not_exported_and_hidden_shapes_are()
        {
            using var d = new TempDir();
            var lone = new Scene("t", new SceneNode[] { Box("H", Vec3.Zero, new Vec3(10, 10, 10), hole: true) }, new Dictionary<string, MeshData>());
            Assert.Null(DimensionModelerManager.ExportScene(lone, obj: false, d.File("x.stl"), 1));
            var hidden = new Scene("t", new SceneNode[] { Box("B", Vec3.Zero, new Vec3(10, 10, 10)) with { Hidden = true } }, new Dictionary<string, MeshData>());
            string file = DimensionModelerManager.ExportScene(hidden, obj: false, d.File("x.stl"), 10)!;
            Assert.True(File.Exists(file));
            Assert.Equal(84 + 12 * 50, new FileInfo(file).Length);
            string again = DimensionModelerManager.ExportScene(hidden, obj: false, d.File("x.stl"), 10)!;
            Assert.NotEqual(file, again);   // never replaces
        }

        // =================================================================
        //  Old settings
        // =================================================================

        [Fact]
        public void A_saved_design_is_read_back_from_the_settings()
        {
            var settings = new ModelerSettings();
            var scene = Plate();
            ModelerSceneStore.Save(settings, scene);
            Assert.Equal(scene.Nodes, ModelerSceneStore.Load(settings, "My design").Nodes);
        }

        [Fact]
        public void An_old_custom_piece_becomes_shapes_with_its_colours()
        {
            var parts = new[] { new Part("Panel 1", Vec3.Zero, new Vec3(600, 18, 300), PartKind.Board) };
            var settings = new ModelerSettings
            {
                Template = "custom",
                CustomName = "Desktop",
                CustomParts = CustomPieceJson.ToDtos(parts),
                Paint = new() { ["custom"] = new() { ["Panel 1"] = new BoardPaint { Color = "#6B4226" } } },
            };
            var scene = ModelerSceneStore.Load(settings, "My design");
            Assert.Equal("Desktop", scene.Name);
            var panel = Assert.IsType<SceneShape>(Assert.Single(scene.Nodes));
            Assert.Equal("#6B4226", panel.Colour);
            Assert.Equal(new Vec3(300, 9, 150), panel.Position);
            Assert.Single(settings.CustomParts);   // the old fields are left as they were
        }

        [Fact]
        public void An_old_preset_becomes_its_example_and_nothing_old_gives_an_empty_design()
        {
            var settings = new ModelerSettings { Template = "table", Values = new() { ["table"] = new() { ["width"] = 1400 } } };
            var scene = ModelerSceneStore.Load(settings, "My design");
            var group = Assert.IsType<GroupNode>(Assert.Single(scene.Nodes));
            Assert.Equal(1400, SceneEdit.BoundsOf(scene, group).Size.X, 6);
            Assert.Empty(ModelerSceneStore.Load(new ModelerSettings { Template = "custom" }, "My design").Nodes);
        }
    }
}
