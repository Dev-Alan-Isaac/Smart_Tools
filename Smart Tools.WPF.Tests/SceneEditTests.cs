using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;
using static Smart_Tools.WPF.Tests.SceneCsgTests;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Scene edits (groups, holes, align, mirror, turn, resize), the finished objects, and picking.</summary>
    public class SceneEditTests
    {
        private static Scene With(params SceneNode[] nodes) => Scene.Empty("t") with { Nodes = nodes };

        private static SceneShape Box(string name, Vec3 centre, Vec3 size) => new(SceneEdit.NewId(), name, ShapeKind.Box, centre, size, Vec3.Zero);

        private static double Volume(Scene scene) => new SceneMeshBuilder().Build(scene, includeHidden: true).Where(o => !o.IsHole).Sum(o => Csg.Volume(o.Triangles));

        [Fact]
        public void Added_shapes_get_new_ids_and_free_names()
        {
            var scene = SceneEdit.Add(Scene.Empty("t"), Box("Box", Vec3.Zero, new Vec3(10, 10, 10)), out string a);
            scene = SceneEdit.Add(scene, Box("Box", Vec3.Zero, new Vec3(10, 10, 10)), out string b);
            scene = SceneEdit.Add(scene, Box("box", Vec3.Zero, new Vec3(10, 10, 10)), out _);
            Assert.NotEqual(a, b);
            Assert.Equal(new[] { "Box", "Box 2", "box 3" }, scene.Nodes.Select(n => n.Name));
        }

        [Fact]
        public void Group_and_ungroup_keep_the_order_and_need_two()
        {
            var a = Box("A", Vec3.Zero, new Vec3(10, 10, 10));
            var b = Box("B", new Vec3(20, 0, 0), new Vec3(10, 10, 10));
            var c = Box("C", new Vec3(40, 0, 0), new Vec3(10, 10, 10));
            var scene = With(a, b, c);
            Assert.Same(scene, SceneEdit.Group(scene, new[] { b.Id }, "G", out _));
            var grouped = SceneEdit.Group(scene, new[] { c.Id, b.Id }, null, out string g);
            Assert.Equal(new[] { "A", "Group" }, grouped.Nodes.Select(n => n.Name));
            Assert.Equal(new[] { "B", "C" }, ((GroupNode)grouped.Nodes[1]).Children.Select(n => n.Name));
            var back = SceneEdit.Ungroup(grouped, new[] { g });
            Assert.Equal(new[] { "A", "B", "C" }, back.Nodes.Select(n => n.Name));
        }

        [Fact]
        public void Deleting_inside_a_group_leaves_no_empty_or_single_groups()
        {
            var a = Box("A", Vec3.Zero, new Vec3(10, 10, 10));
            var b = Box("B", new Vec3(20, 0, 0), new Vec3(10, 10, 10));
            var scene = With(new GroupNode("g", "G", new SceneNode[] { a, b }));
            var left = SceneEdit.Delete(scene, new[] { b.Id });
            Assert.Equal("A", Assert.Single(left.Nodes).Name);
            Assert.Empty(SceneEdit.Delete(scene, new[] { "g" }).Nodes);
        }

        [Fact]
        public void Duplicates_sit_after_the_original_moved_and_renamed()
        {
            var a = Box("Shelf 1", Vec3.Zero, new Vec3(10, 10, 10));
            var scene = SceneEdit.Duplicate(With(a), new[] { a.Id }, new Vec3(20, 0, 0), out var ids);
            var copy = (SceneShape)scene.Nodes[1];
            Assert.Equal(ids.Single(), copy.Id);
            Assert.Equal("Shelf 2", copy.Name);
            Assert.Equal(new Vec3(20, 0, 0), copy.Position);
        }

        [Fact]
        public void A_hole_cuts_only_inside_its_group()
        {
            var box = Box("Box", new Vec3(0, 50, 0), new Vec3(100, 100, 100));
            var hole = SceneEdit.Sanitize(new SceneShape(SceneEdit.NewId(), "Hole", ShapeKind.Box, new Vec3(0, 50, 0), new Vec3(40, 120, 40), Vec3.Zero, IsHole: true));
            var apart = With(box, hole);
            var objects = new SceneMeshBuilder().Build(apart);
            Assert.Equal(2, objects.Count);
            Assert.True(objects.Single(o => o.NodeId == hole.Id).IsHole);
            Assert.Equal(1e6, Volume(apart), 3);

            var grouped = SceneEdit.Group(apart, new[] { box.Id, hole.Id }, "Cut", out _);
            Assert.Equal(1e6 - 40 * 40 * 100, Volume(grouped), 3);
            Assert.Equal(1e6 + 40 * 40 * 20, Volume(SceneEdit.SetHole(grouped, new[] { hole.Id }, false)), 3);   // a solid again: they join (it sticks out 10 mm above and below)
        }

        [Fact]
        public void A_group_of_holes_is_one_hole_that_cuts_as_a_whole()
        {
            var box = Box("Box", new Vec3(0, 50, 0), new Vec3(100, 100, 100));
            var h1 = Box("H1", new Vec3(-20, 50, 0), new Vec3(20, 120, 20)) with { IsHole = true };
            var h2 = Box("H2", new Vec3(20, 50, 0), new Vec3(20, 120, 20)) with { IsHole = true };
            var scene = With(box, new GroupNode("holes", "Holes", new SceneNode[] { h1, h2 }));
            Assert.True(new SceneMeshBuilder().Build(scene).Single(o => o.NodeId == "holes").IsHole);
            var cut = SceneEdit.Group(scene, new[] { box.Id, "holes" }, null, out _);
            Assert.Equal(1e6 - 2 * 20 * 20 * 100, Volume(cut), 3);
        }

        [Fact]
        public void Hidden_shapes_leave_the_view_but_not_the_exports()
        {
            var box = Box("Box", new Vec3(0, 50, 0), new Vec3(100, 100, 100));
            var hole = Box("Hole", new Vec3(0, 50, 0), new Vec3(40, 120, 40)) with { IsHole = true, Hidden = true };
            var scene = With(new GroupNode("g", "G", new SceneNode[] { box, hole }));
            var builder = new SceneMeshBuilder();
            Assert.Equal(1e6, Csg.Volume(builder.Build(scene).Single().Triangles), 3);
            Assert.Equal(1e6 - 40 * 40 * 100, Csg.Volume(builder.Build(scene, includeHidden: true).Single().Triangles), 3);
        }

        [Fact]
        public void Align_lines_up_the_low_sides()
        {
            var a = Box("A", new Vec3(0, 5, 0), new Vec3(10, 10, 10));
            var b = Box("B", new Vec3(50, 30, 0), new Vec3(20, 20, 20));
            var scene = SceneEdit.Align(With(a, b), new[] { a.Id, b.Id }, 0, AlignMode.Min);
            Assert.Equal(-5, SceneEdit.BoundsOf(scene, SceneEdit.Find(scene, b.Id)!).Min.X, 9);
            scene = SceneEdit.Align(scene, new[] { a.Id, b.Id }, 1, AlignMode.Max);
            Assert.Equal(40, SceneEdit.BoundsOf(scene, SceneEdit.Find(scene, a.Id)!).Max.Y, 9);
        }

        [Fact]
        public void Mirroring_twice_gives_back_the_same_surface()
        {
            var wedge = new SceneShape("w", "W", ShapeKind.Wedge, new Vec3(10, 20, 30), new Vec3(40, 50, 60), new Vec3(10, 20, 30));
            var scene = With(wedge);
            foreach (int axis in new[] { 0, 1, 2 })
            {
                var once = SceneEdit.Mirror(scene, new[] { "w" }, axis);
                var m = (SceneShape)once.Nodes[0];
                Assert.True(m.Mirrored);
                Assert.Equal(0.5 * 40 * 50 * 60, Volume(once), 4);
                var twice = (SceneShape)SceneEdit.Mirror(once, new[] { "w" }, axis).Nodes[0];
                var p0 = Primitives.Placed(wedge, null).SelectMany(p => p).ToList();
                var p2 = Primitives.Placed(twice, null).SelectMany(p => p).ToList();
                Assert.Equal(p0.Count, p2.Count);
                Assert.All(p0, p => Assert.Contains(p2, q => (p - q).Length < 1e-6));
            }
        }

        [Fact]
        public void Mirroring_a_wedge_on_X_flips_where_its_high_side_is()
        {
            // a ramp turned so its high side is at -X; after a mirror on X it must be at +X
            var wedge = new SceneShape("w", "W", ShapeKind.Wedge, Vec3.Zero, new Vec3(100, 50, 100), new Vec3(0, 90, 0));
            double Top(SceneShape s, double x) => Primitives.Placed(s, null).SelectMany(p => p).Where(p => Math.Abs(p.X - x) < 1e-6).Max(p => p.Y);
            Assert.Equal(25, Top(wedge, -50), 6);
            var mirrored = (SceneShape)SceneEdit.Mirror(With(wedge), new[] { "w" }, 0).Nodes[0];
            Assert.Equal(25, Top(mirrored, 50), 6);
            Assert.Equal(-25, Top(mirrored, -50), 6);
        }

        [Fact]
        public void A_quarter_turn_swaps_width_and_depth()
        {
            var a = Box("A", new Vec3(0, 5, 0), new Vec3(100, 10, 50));
            var turned = SceneEdit.Rotate(With(a), new[] { a.Id }, 1, 90);
            var b = SceneEdit.BoundsOf(turned, turned.Nodes[0]);
            Assert.Equal(50, b.Size.X, 6);
            Assert.Equal(100, b.Size.Z, 6);
            Assert.Equal(new Vec3(0, 5, 0), b.Centre);
        }

        [Fact]
        public void Dropping_puts_each_object_on_the_floor()
        {
            var a = Box("A", new Vec3(0, 100, 0), new Vec3(10, 10, 10));
            var scene = SceneEdit.DropToFloor(With(a), new[] { a.Id });
            Assert.Equal(0, SceneEdit.BoundsOf(scene, scene.Nodes[0]).Min.Y, 9);
        }

        [Fact]
        public void Resizing_a_group_stretches_its_members_and_their_gaps()
        {
            var a = Box("A", new Vec3(5, 5, 5), new Vec3(10, 10, 10));
            var b = Box("B", new Vec3(25, 5, 5), new Vec3(10, 10, 10));
            var scene = With(new GroupNode("g", "G", new SceneNode[] { a, b }));
            var big = SceneEdit.Resize(scene, "g", new Bounds3(Vec3.Zero, new Vec3(60, 20, 10)));
            var bounds = SceneEdit.BoundsOf(big, big.Nodes[0]);
            Assert.Equal(new Vec3(60, 20, 10), bounds.Size);
            var members = ((GroupNode)big.Nodes[0]).Children.Cast<SceneShape>().ToList();
            Assert.Equal(new Vec3(20, 20, 10), members[0].Size);
            Assert.Equal(50, members[1].Position.X, 9);
        }

        [Fact]
        public void Colour_on_a_group_paints_all_of_it_and_none_clears_all()
        {
            var a = Box("A", Vec3.Zero, new Vec3(10, 10, 10)) with { Colour = "#2E86DE" };
            var b = Box("B", new Vec3(20, 0, 0), new Vec3(10, 10, 10));
            var scene = With(new GroupNode("g", "G", new SceneNode[] { a, b }));
            var red = SceneEdit.SetColour(scene, new[] { "g" }, "c0392b");
            Assert.All(new SceneMeshBuilder().Build(red).Single().Triangles, t => Assert.Equal("#C0392B", t.Colour));
            var none = SceneEdit.SetColour(red, new[] { "g" }, null);
            Assert.All(new SceneMeshBuilder().Build(none).Single().Triangles, t => Assert.Null(t.Colour));
        }

        [Fact]
        public void Crystal_material_follows_the_colour_rules_and_survives_saving()
        {
            var a = Box("A", Vec3.Zero, new Vec3(10, 10, 10)) with { Id = "a", Colour = "#2E86DE" };
            var b = Box("B", new Vec3(20, 0, 0), new Vec3(10, 10, 10));
            var scene = With(new GroupNode("g", "G", new SceneNode[] { a, b }));

            // the group made crystal: both parts are crystal, colours untouched
            var glass = SceneEdit.SetMaterial(scene, new[] { "g" }, "Crystal");
            var tris = new SceneMeshBuilder().Build(glass).Single().Triangles;
            Assert.All(tris, t => Assert.Equal(SceneMaterial.Crystal, t.Material));
            Assert.Contains(tris, t => t.Colour == "#2E86DE");
            Assert.Contains(tris, t => t.Colour == null);
            Assert.Equal(SceneMaterial.Crystal, SceneEdit.ShownMaterial(glass, "a"));

            // a part set on its own wins; standard on a part inside a crystal group is "inherit", so set the group back instead
            var onlyA = SceneEdit.SetMaterial(scene, new[] { "a" }, SceneMaterial.Crystal);
            var mixed = new SceneMeshBuilder().Build(onlyA).Single().Triangles;
            Assert.Contains(mixed, t => t.Material == SceneMaterial.Crystal && t.Colour == "#2E86DE");
            Assert.Contains(mixed, t => t.Material == null);

            // saved and read back; an unknown material is dropped
            Assert.True(SceneJson.TryRead(SceneJson.ToJson(glass), out var back, out _));
            Assert.Equal(SceneMaterial.Crystal, ((GroupNode)back.Nodes[0]).Material);
            Assert.Null(SceneMaterial.Normalize("lava"));

            // taken out of the group, a part keeps the crystal look it showed
            var outA = SceneEdit.TakeOut(glass, new[] { "a" }, out _);
            Assert.Equal(SceneMaterial.Crystal, ((SceneShape)SceneEdit.Find(outA, "a")!).Material);
        }

        [Fact]
        public void A_part_painted_inside_a_coloured_group_keeps_its_own_colour()
        {
            var a = Box("A", Vec3.Zero, new Vec3(10, 10, 10)) with { Id = "a" };
            var b = Box("B", new Vec3(20, 0, 0), new Vec3(10, 10, 10));
            var scene = With(new GroupNode("g", "G", new SceneNode[] { a, b }));
            var red = SceneEdit.SetColour(scene, new[] { "g" }, "#C0392B");
            var blueA = SceneEdit.SetColour(red, new[] { "a" }, "#2E86DE");
            var colours = new SceneMeshBuilder().Build(blueA).Single().Triangles.Select(t => t.Colour).Distinct().OrderBy(c => c).ToList();
            Assert.Equal(new[] { "#2E86DE", "#C0392B" }, colours);   // A blue, B still the group's red
            // painting the whole group again repaints the part too
            var green = SceneEdit.SetColour(blueA, new[] { "g" }, "#27AE60");
            Assert.All(new SceneMeshBuilder().Build(green).Single().Triangles, t => Assert.Equal("#27AE60", t.Colour));
        }

        [Fact]
        public void Sanitize_keeps_numbers_in_range()
        {
            var s = SceneEdit.Sanitize(new SceneShape("x", " ", (ShapeKind)99, new Vec3(double.NaN, 1e9, 0), new Vec3(0, -5, 1e9), new Vec3(370, -190, double.NaN), Segments: 1000, Inner: 5));
            Assert.Equal("Shape", s.Name);
            Assert.Equal(ShapeKind.Box, s.Kind);
            Assert.Equal(new Vec3(0, SceneEdit.MaxPosition, 0), s.Position);
            Assert.Equal(new Vec3(SceneEdit.MinSize, SceneEdit.MinSize, SceneEdit.MaxSize), s.Size);
            Assert.Equal(new Vec3(10, 170, 0), s.Rotation);
            Assert.Equal(Primitives.MaxSegments, s.Segments);
            Assert.Equal(0.95, s.Inner);
        }

        [Fact]
        public void History_undoes_and_redoes_whole_scenes()
        {
            var history = new SceneHistory();
            var s0 = Scene.Empty("t");
            var s1 = SceneEdit.Add(s0, Box("A", Vec3.Zero, new Vec3(10, 10, 10)), out _);
            history.Push(s0, s1);
            history.Push(s1, s1);   // no change: not recorded
            Assert.Same(s0, history.Undo(s1));
            Assert.False(history.CanUndo);
            Assert.Same(s1, history.Redo(s0));
        }

        // =================================================================
        //  Picking
        // =================================================================

        [Fact]
        public void Picking_hits_a_turned_shape_where_it_is_drawn()
        {
            // a long thin box turned 45 degrees: the ray passes inside its outer box but misses the box itself
            var bar = new SceneShape("bar", "Bar", ShapeKind.Box, new Vec3(0, 50, 0), new Vec3(200, 10, 10), new Vec3(0, 0, 45));
            var objects = new SceneMeshBuilder().Build(With(bar));
            Assert.Null(ScenePick.Pick(objects, new Ray3(new Vec3(60, 0, 500), new Vec3(0, 0, -1))));
            var hit = ScenePick.Pick(objects, new Ray3(new Vec3(0, 50, 500), new Vec3(0, 0, -1)));
            Assert.Equal("bar", hit!.Value.NodeId);
            Assert.Equal(495, hit.Value.Distance, 6);
        }

        [Fact]
        public void A_ray_down_a_through_hole_hits_nothing()
        {
            var plate = Box("Plate", new Vec3(0, 5, 0), new Vec3(100, 10, 100));
            var hole = new SceneShape(SceneEdit.NewId(), "Hole", ShapeKind.Cylinder, new Vec3(0, 5, 0), new Vec3(20, 12, 20), Vec3.Zero, IsHole: true);
            var objects = new SceneMeshBuilder().Build(With(new GroupNode("g", "G", new SceneNode[] { plate, hole })));
            Assert.Null(ScenePick.Pick(objects, new Ray3(new Vec3(0, 100, 0), new Vec3(0, -1, 0))));
            Assert.NotNull(ScenePick.Pick(objects, new Ray3(new Vec3(30, 100, 0), new Vec3(0, -1, 0))));
        }

        [Fact]
        public void A_drop_lands_on_top_of_a_shape_or_on_the_floor()
        {
            var block = Box("Block", new Vec3(0, 20, 0), new Vec3(40, 40, 40));
            var objects = new SceneMeshBuilder().Build(With(block));
            var down = new Vec3(0, -1, 0);
            Assert.Equal(40, ScenePick.DropPoint(objects, new Ray3(new Vec3(5, 300, 5), down))!.Value.Y, 9);
            Assert.Equal(new Vec3(100, 0, 0), ScenePick.DropPoint(objects, new Ray3(new Vec3(100, 300, 0), down)));
            Assert.Null(ScenePick.DropPoint(objects, new Ray3(new Vec3(0, 300, 0), new Vec3(1, 0, 0))));
        }
    }
}
