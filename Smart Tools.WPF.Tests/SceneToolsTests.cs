using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    public class SceneToolsTests
    {
        private static SceneShape Box(string name, Vec3 centre, Vec3 size) => new(SceneEdit.NewId(), name, ShapeKind.Box, centre, size, Vec3.Zero);

        private static Scene With(params SceneNode[] nodes) => Scene.Empty("t") with { Nodes = nodes.ToList() };

        [Fact]
        public void Repeat_puts_copies_edge_to_edge_plus_the_gap()
        {
            var b = Box("Leg", new Vec3(10, 50, 0), new Vec3(20, 100, 20));
            var scene = SceneTools.Repeat(With(b), new[] { b.Id }, 3, 0, 5, out var ids);
            Assert.Equal(3, ids.Count);
            var xs = ids.Select(id => ((SceneShape)SceneEdit.Find(scene, id)!).Position.X).ToList();
            Assert.Equal(new[] { 35.0, 60.0, 85.0 }, xs);   // 20 wide + 5 gap each time
            Assert.Equal(4, SceneEdit.ShapeCount(scene));
        }

        [Fact]
        public void Repeat_keeps_copies_of_a_part_inside_its_group()
        {
            var a = Box("Shelf", new Vec3(0, 10, 0), new Vec3(100, 2, 30));
            var g = new GroupNode(SceneEdit.NewId(), "Unit", new SceneNode[] { a, Box("Side", new Vec3(-51, 50, 0), new Vec3(2, 100, 30)) });
            var scene = SceneTools.Repeat(With(g), new[] { a.Id }, 2, 1, 28, out var ids);
            Assert.Single(scene.Nodes);
            Assert.All(ids, id => Assert.Equal(g.Id, SceneEdit.TopOf(scene, id)!.Id));
            Assert.Equal(40, ((SceneShape)SceneEdit.Find(scene, ids[0])!).Position.Y, 9);
        }

        [Fact]
        public void Copy_and_paste_makes_new_objects_moved_and_brings_the_surfaces()
        {
            var mitten = Extrude.Make("m", new[] { new OutlinePoint(0, 0), new OutlinePoint(40, 0), new OutlinePoint(40, 30), new OutlinePoint(0, 30) }, 5)!;
            var scene = SceneEdit.AddNodes(Scene.Empty("t"), new SceneNode[] { mitten.Shape }, out var first, new Dictionary<string, MeshData> { [mitten.Mesh.Id] = mitten.Mesh });
            var clip = SceneTools.Copy(scene, first)!;
            Assert.Single(clip.Meshes);

            var pasted = SceneTools.Paste(Scene.Empty("other"), clip, new Vec3(10, 0, 10), out var ids);
            var shape = (SceneShape)SceneEdit.Find(pasted, ids.Single())!;
            Assert.NotEqual(first.Single(), shape.Id);
            Assert.True(pasted.Meshes.ContainsKey(shape.MeshId!));
            Assert.Equal(((SceneShape)SceneEdit.Find(scene, first[0])!).Position + new Vec3(10, 0, 10), shape.Position);
        }

        [Fact]
        public void Copying_a_group_and_its_part_copies_the_group_once()
        {
            var a = Box("A", Vec3.Zero, new Vec3(10, 10, 10));
            var g = new GroupNode(SceneEdit.NewId(), "G", new SceneNode[] { a, Box("B", new Vec3(20, 0, 0), new Vec3(10, 10, 10)) });
            var clip = SceneTools.Copy(With(g), new[] { g.Id, a.Id })!;
            Assert.Single(clip.Nodes);
        }

        [Fact]
        public void Even_scaling_keeps_proportions_the_floor_and_the_middle()
        {
            var start = new Bounds3(new Vec3(-10, 0, -20), new Vec3(10, 30, 20));          // 20 x 30 x 40 on the floor
            var pulled = new Bounds3(new Vec3(-10, 0, -20), new Vec3(30, 30, 20));         // X pulled from 20 to 40
            var even = SceneTools.ScaleEvenly(start, pulled, 0, highFace: true);
            Assert.Equal(new Vec3(40, 60, 80), even.Size);
            Assert.Equal(-10, even.Min.X, 9);    // the far face stayed
            Assert.Equal(0, even.Min.Y, 9);      // still on the floor
            Assert.Equal(0, even.Centre.Z, 9);   // across: same middle
        }
    }
}
