using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>The .dmodel v2 format, old v1 designs turned into shapes, and the examples.</summary>
    public class SceneJsonTests
    {
        public static IEnumerable<object[]> TemplateIds() => PieceTemplates.All.Select(t => new object[] { t.Id });

        public static IEnumerable<object[]> ExampleIds() => SceneExamples.All.Select(e => new object[] { e.Id });

        private static double OldVolume(IEnumerable<Part> parts) =>
            parts.SelectMany(BoxMesh.Triangles).Sum(t => Vec3.Dot(t.A, Vec3.Cross(t.B, t.C))) / 6;

        private static double NewVolume(Scene scene) =>
            new SceneMeshBuilder().Build(scene, includeHidden: true).Where(o => !o.IsHole).Sum(o => Csg.Volume(o.Triangles));

        private static Scene Of(IEnumerable<SceneNode> nodes) => Scene.Empty("t") with { Nodes = nodes.ToList() };

        [Fact]
        public void A_v2_design_comes_back_exactly()
        {
            var unit = Primitives.Box().SelectMany(p => new[] { p[0], p[1], p[2], p[0], p[2], p[3] }).ToList();
            var scene = new Scene("Desk lamp", new SceneNode[]
            {
                new SceneShape("a", "Base", ShapeKind.Cylinder, new Vec3(0, 5, 0), new Vec3(120, 10, 120), Vec3.Zero, Colour: "#262626", Segments: 48),
                new GroupNode("g", "Arm", new SceneNode[]
                {
                    new SceneShape("b", "Arm", ShapeKind.Box, new Vec3(0, 200, 0), new Vec3(20, 400, 20), new Vec3(0, 0, 15.5), Mirrored: true, Locked: true),
                    new SceneShape("c", "Slot", ShapeKind.Tube, new Vec3(0, 300, 0), new Vec3(10, 30, 10), Vec3.Zero, IsHole: true, Inner: 0.3, Hidden: true),
                }, Colour: "#2E86DE"),
                new SceneShape("d", "Shade", ShapeKind.Mesh, new Vec3(0, 420, 0), new Vec3(80, 60, 80), Vec3.Zero, MeshId: "m1"),
                new SceneShape("e", "Wedge", ShapeKind.Wedge, new Vec3(1.25, 2.5, 3.75), new Vec3(1, 2, 3), new Vec3(-30, 60, 179)),
            }, new Dictionary<string, MeshData> { ["m1"] = new MeshData("m1", unit), ["unused"] = new MeshData("unused", unit) });

            string json = SceneJson.ToJson(scene);
            Assert.DoesNotContain("unused", json);
            Assert.True(SceneJson.TryRead(json, out var back, out string error), error);
            Assert.Equal(scene.Name, back.Name);
            Assert.Equal(scene.Nodes, back.Nodes);
            Assert.Equal(unit, back.Meshes["m1"].Points);
        }

        [Theory]
        [InlineData("")]
        [InlineData("[]")]
        [InlineData("{ broken")]
        [InlineData("{\"Version\":9,\"Name\":\"x\",\"Nodes\":[]}")]
        [InlineData("{\"Version\":2,\"Name\":\"x\",\"Nodes\":[],\"Meshes\":[{\"Id\":\"m\",\"Points\":\"not base64!\"}]}")]
        public void Files_that_cannot_be_read_are_refused_with_a_reason(string json)
        {
            Assert.False(SceneJson.TryRead(json, out _, out string error));
            Assert.NotEmpty(error);
        }

        [Fact]
        public void Hand_edited_files_are_made_safe()
        {
            const string json = """
                {"Version":2,"Name":"","Nodes":[
                  {"Type":"shape","Id":"same","Name":"A","Kind":"Box","Position":[0,0,0],"Size":[-5,10,10]},
                  {"Type":"shape","Id":"same","Name":"B","Kind":"Teapot"},
                  {"Type":"shape","Name":"C","Kind":"Mesh","Mesh":"missing"},
                  {"Type":"group","Name":"Empty","Children":[]}
                ]}
                """;
            Assert.True(SceneJson.TryRead(json, out var scene, out string error), error);
            Assert.Equal(CustomPieceJson.DefaultName, scene.Name);
            Assert.Equal(3, scene.Nodes.Count);
            Assert.Equal(3, scene.Nodes.Select(n => n.Id).Distinct().Count());
            Assert.All(scene.Nodes.Cast<SceneShape>(), s => Assert.Equal(ShapeKind.Box, s.Kind));
            Assert.Equal(SceneEdit.MinSize, ((SceneShape)scene.Nodes[0]).Size.X);
        }

        [Fact]
        public void A_v1_design_with_holes_cuts_groups_and_colours_is_converted()
        {
            var front = new Part("Front", new Vec3(0, 0, 300), new Vec3(400, 200, 18), PartKind.Panel,
                new[] { new Hole(2, 200, 150, 30) }, new[] { new Bevel(5, 3, 45, 3) }, "Drawer 1");
            var bottom = new Part("Bottom", new Vec3(12, 12, 0), new Vec3(376, 12, 300), PartKind.Board, Group: "Drawer 1");
            var top = new Part("Top", new Vec3(0, 400, 0), new Vec3(400, 18, 318), PartKind.Top);
            var parts = new[] { front, bottom, top };
            var paint = new Dictionary<string, BoardPaint> { ["Top"] = new() { Color = "#1F3A5F" } };
            string v1 = CustomPieceJson.ToJson("Old chest", parts, paint);

            Assert.True(SceneJson.TryRead(v1, out var scene, out string error), error);
            Assert.Equal("Old chest", scene.Name);
            Assert.Equal(new[] { "Drawer 1", "Top" }, scene.Nodes.Select(n => n.Name));
            var drawer = (GroupNode)scene.Nodes[0];
            var frontGroup = (GroupNode)drawer.Children[0];
            Assert.Equal("Front", frontGroup.Name);
            Assert.Equal(new[] { ShapeKind.Box, ShapeKind.Cylinder, ShapeKind.Wedge }, frontGroup.Children.Cast<SceneShape>().Select(s => s.Kind));
            Assert.Equal("#1F3A5F", ((SceneShape)scene.Nodes[1]).Colour);
            Assert.Equal(LegacyPieces.Wood[PartKind.Board], ((SceneShape)drawer.Children[1]).Colour);

            // the hole is a 32-sided cylinder now; the old surface was a different polygon, so compare with the cylinder's own size
            double hole = Math.PI * 15 * 15 * 18;
            Assert.InRange(NewVolume(scene), OldVolume(parts) - 0.03 * hole, OldVolume(parts) + 0.03 * hole);
        }

        [Fact]
        public void A_bevel_becomes_a_wedge_that_removes_exactly_its_strip()
        {
            foreach (var (faceA, faceB) in new[] { (5, 3), (3, 5), (0, 2), (1, 4), (2, 1), (4, 0) })
            {
                var part = new Part("P", new Vec3(10, 20, 30), new Vec3(400, 200, 18), PartKind.Panel, Edges: new[] { new Bevel(faceA, faceB, 30, 4) });
                var scene = Of(new[] { LegacyPieces.FromPart(part) });
                Assert.Equal(OldVolume(new[] { part }), NewVolume(scene), 3);
            }
        }

        [Fact]
        public void A_miter_through_the_board_is_kept()
        {
            var raw = new Part("Side", Vec3.Zero, new Vec3(18, 700, 300), PartKind.Panel, Edges: new[] { new Bevel(3, 1, 45, 10000) });
            var part = raw with { Edges = PieceBevels.Sanitize(raw.Size, raw.Bevels) };   // what the old editor kept: cut exactly through
            Assert.Equal(OldVolume(new[] { part }), NewVolume(Of(new[] { LegacyPieces.FromPart(part) })), 3);
        }

        [Theory]
        [MemberData(nameof(TemplateIds))]
        public void Every_furniture_preset_keeps_its_volume_as_shapes(string id)
        {
            var t = PieceTemplates.Find(id)!;
            var parts = t.Build(t.Params.ToDictionary(p => p.Key, p => p.Default)).Parts;
            var scene = Of(LegacyPieces.FromParts(parts));
            double holes = parts.SelectMany(p => p.Holes.Select(h => Math.PI * h.Diameter * h.Diameter / 4 * (h.Axis == 0 ? p.Size.X : h.Axis == 1 ? p.Size.Y : p.Size.Z))).Sum();
            double old = OldVolume(parts);
            Assert.InRange(NewVolume(scene), old - 0.03 * holes - 1e-6 * old, old + 0.03 * holes + 1e-6 * old);
        }

        [Theory]
        [MemberData(nameof(ExampleIds))]
        public void Every_example_builds_at_its_defaults_and_its_slider_ends(string id)
        {
            var example = SceneExamples.Find(id)!;
            foreach (var pick in new Func<ParamSpec, double>[] { p => p.Default, p => p.Min, p => p.Max })
            {
                var node = example.Build(example.Params.ToDictionary(p => p.Key, pick));
                var scene = Of(new[] { node });
                Assert.True(NewVolume(scene) > 0, id);
                // holes reach past the faces they cut on purpose, so look at the finished solid, not at the box around every shape
                Assert.True(new SceneMeshBuilder().Build(scene).Where(o => !o.IsHole).Min(o => o.Bounds.Min.Y) > -1e-6, $"{id} goes under the floor");
                Assert.All(SceneEdit.Shapes(node), s => Assert.True(s.Size.X >= SceneEdit.MinSize && s.Size.Y >= SceneEdit.MinSize && s.Size.Z >= SceneEdit.MinSize, $"{id}: {s.Name}"));
                Assert.Equal(SceneEdit.Shapes(node).Count(), SceneEdit.Shapes(node).Select(s => s.Id).Distinct().Count());
            }
        }

        [Fact]
        public void The_box_example_is_hollow_and_its_lid_plate_is_whole()
        {
            var example = SceneExamples.Find("box-lid")!;
            var node = example.Build(new Dictionary<string, double> { ["width"] = 100, ["depth"] = 80, ["height"] = 50, ["wall"] = 2, ["lid"] = 1 });
            var objects = new SceneMeshBuilder().Build(Of(new[] { node }));
            double body = 100 * 50 * 80 - 96 * 48 * 76;
            double lid = 100 * 2 * 80 + (95.4 * 75.4 - 91.4 * 71.4) * 6;
            Assert.Equal(body + lid, Csg.Volume(objects.Single().Triangles), 2);
        }
    }
}
