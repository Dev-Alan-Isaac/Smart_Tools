using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>The presets with their bevel sliders turned up, and the drawer groups of the filing cabinet.</summary>
    public class PieceGroupsAndPresetCutsTests
    {
        private static (long, long, long) Key(Vec3 v) => ((long)Math.Round(v.X * 1e5), (long)Math.Round(v.Y * 1e5), (long)Math.Round(v.Z * 1e5));

        private static bool Watertight(IReadOnlyList<Triangle> triangles)
        {
            var edges = new Dictionary<((long, long, long), (long, long, long)), int>();
            foreach (var t in triangles)
            {
                var c = new[] { Key(t.A), Key(t.B), Key(t.C) };
                for (int i = 0; i < 3; i++)
                {
                    var e = (c[i], c[(i + 1) % 3]);
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                }
            }
            return edges.All(kv => kv.Value == 1 && edges.ContainsKey((kv.Key.Item2, kv.Key.Item1)));
        }

        public static IEnumerable<object[]> Ids() => PieceTemplates.All.Select(t => new object[] { t.Id });

        [Theory]
        [MemberData(nameof(Ids))]
        public void Every_board_of_every_preset_has_a_closed_surface_even_with_the_biggest_bevel(string id)
        {
            var t = PieceTemplates.Find(id)!;
            foreach (bool big in new[] { false, true })
            {
                var values = t.Params.ToDictionary(p => p.Key, p => big && p.Key.StartsWith("bevel") ? p.Max : p.Default);
                foreach (var part in t.Build(values).Parts)
                    Assert.True(Watertight(BoxMesh.Triangles(part)), $"{id}: {part.Name} is not closed (bevel {(big ? "max" : "default")})");
            }
        }

        [Fact]
        public void The_bevel_sliders_cut_the_door_and_drawer_fronts_and_the_desk_edge()
        {
            var cabinet = PieceTemplates.Find("cabinet")!;
            var v = cabinet.Params.ToDictionary(p => p.Key, p => p.Default);
            Assert.Empty(cabinet.Build(v).Parts.Single(p => p.Name == "Door 1").Bevels);   // 0 by default
            v["bevel"] = 10;
            v["bevelAngle"] = 30;
            var door = cabinet.Build(v).Parts.Single(p => p.Name == "Door 1");
            Assert.Equal(4, door.Bevels.Count);
            Assert.All(door.Bevels, b => Assert.Equal(30, b.Angle));
            Assert.Single(door.Holes);                                                       // the knob hole is still there

            var desk = PieceTemplates.Find("desk")!;
            var d = desk.Params.ToDictionary(p => p.Key, p => p.Default);
            d["bevel"] = 8;
            var top = desk.Build(d).Parts.Single(p => p.Name == "Top");
            Assert.Equal(new Bevel(3, 5, 45, 8), Assert.Single(top.Bevels));
            Assert.Single(top.Holes);
        }

        [Fact]
        public void Each_drawer_is_a_group_of_its_front_and_box()
        {
            var filing = PieceTemplates.Find("filing-cabinet")!;
            var parts = filing.Build(filing.Params.ToDictionary(p => p.Key, p => p.Default)).Parts;
            var groups = parts.Where(p => p.Group != null).GroupBy(p => p.Group!).ToList();
            Assert.Equal(4, groups.Count);
            Assert.All(groups, g => Assert.Equal(5, g.Count()));
            Assert.All(parts.Where(p => p.Group == null), p => Assert.DoesNotContain("Drawer", p.Name));   // the carcass is not in a group
        }

        [Fact]
        public void A_drawer_pulled_out_by_its_own_depth_is_clear_of_the_cabinet()
        {
            var filing = PieceTemplates.Find("filing-cabinet")!;
            var parts = filing.Build(filing.Params.ToDictionary(p => p.Key, p => p.Default)).Parts;
            var drawer = parts.Where(p => p.Group == "Drawer 2").ToList();
            double pull = drawer.Max(p => p.Max.Z) - drawer.Min(p => p.Min.Z);   // what the view uses for "pull out"
            var moved = drawer.Select(p => p with { Min = p.Min + new Vec3(0, 0, pull) }).ToList();
            var rest = parts.Where(p => p.Group != "Drawer 2").ToList();
            foreach (var m in moved)
                foreach (var r in rest)
                    Assert.False(PieceBevels.SolidsOverlap(m, r), $"{m.Name} still inside {r.Name}");
            Assert.True(moved.Min(p => p.Min.Z) >= parts.Where(p => p.Group == null).Max(p => p.Max.Z) - 1e-6);   // wholly in front of the carcass
        }
    }
}
