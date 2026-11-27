using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    public class SceneSnapTests
    {
        private static readonly bool[] Floor = { true, false, true };
        private static readonly bool[] Lift = { false, true, false };

        private static Bounds3 Box(double x, double y, double z, double w, double h, double d) =>
            new(new Vec3(x, y, z), new Vec3(x + w, y + h, z + d));

        // a 100 x 100 x 100 block at the origin, and a 20 x 20 x 20 cube being dragged
        private static readonly Bounds3 Big = Box(0, 0, 0, 100, 100, 100);
        private static readonly Bounds3 Small = Box(300, 0, 300, 20, 20, 20);

        private static SnapMove MoveSmallTo(double x, double z, double threshold = 5, double grid = 0) =>
            SceneSnap.Move(Small, new Vec3(x - Small.Min.X, 0, z - Small.Min.Z), new[] { Big }, Floor, grid, threshold, magnet: true);

        [Fact]
        public void Centre_on_centre_snaps_when_close()
        {
            var move = MoveSmallTo(38, 41);              // centre would be 48 / 51, the block's centre is 50
            var at = Small.Min + move.Delta;
            Assert.Equal(40, at.X, 6);
            Assert.Equal(40, at.Z, 6);
            Assert.All(move.Guides, g => Assert.Equal(SnapKind.Centre, g.Kind));
            Assert.All(move.Guides, g => Assert.Equal(50, g.At, 6));
        }

        [Fact]
        public void Corner_lines_up_flush_with_the_corner()
        {
            var move = MoveSmallTo(2, 78);               // near the block's low-X side and its high-Z side (78 + 20 = 98)
            var at = Small.Min + move.Delta;
            Assert.Equal(0, at.X, 6);
            Assert.Equal(80, at.Z, 6);
            Assert.All(move.Guides, g => Assert.Equal(SnapKind.Edge, g.Kind));
        }

        [Fact]
        public void Side_by_side_touch_wins_over_nothing()
        {
            var move = MoveSmallTo(103, 40);             // just right of the block
            var at = Small.Min + move.Delta;
            Assert.Equal(100, at.X, 6);
            Assert.Contains(move.Guides, g => g.Axis == 0 && g.Kind == SnapKind.Touch);
        }

        [Fact]
        public void Out_of_reach_goes_to_the_grid()
        {
            var move = MoveSmallTo(213, 247, threshold: 5, grid: 10);
            var at = Small.Min + move.Delta;
            Assert.Equal(210, at.X, 6);
            Assert.Equal(250, at.Z, 6);
            Assert.Empty(move.Guides);
        }

        [Fact]
        public void Magnet_off_only_uses_the_grid()
        {
            var move = SceneSnap.Move(Small, new Vec3(38 - 300, 0, 41 - 300), new[] { Big }, Floor, 10, 5, magnet: false);
            var at = Small.Min + move.Delta;
            Assert.Equal(40, at.X, 6);
            Assert.Equal(40, at.Z, 6);
            Assert.Empty(move.Guides);
        }

        [Fact]
        public void Floor_drag_never_changes_height_and_lift_never_slides()
        {
            var slide = SceneSnap.Move(Small, new Vec3(5, 77, 5), new[] { Big }, Floor, 0, 1, true);
            Assert.Equal(0, slide.Delta.Y);
            var lift = SceneSnap.Move(Small, new Vec3(55, 40, 55), new[] { Big }, Lift, 0, 1, true);
            Assert.Equal(0, lift.Delta.X);
            Assert.Equal(0, lift.Delta.Z);
        }

        [Fact]
        public void Lifting_near_the_top_sits_on_top_and_near_zero_lands_on_the_floor()
        {
            var lifted = Box(10, 30, 10, 20, 20, 20);
            var onTop = SceneSnap.Move(lifted, new Vec3(0, 72, 0), new[] { Big }, Lift, 0, 5, true);   // low side would be at 102
            Assert.Equal(100, lifted.Min.Y + onTop.Delta.Y, 6);
            Assert.Contains(onTop.Guides, g => g.Kind == SnapKind.Touch);

            var down = SceneSnap.Move(lifted, new Vec3(0, -28, 0), Array.Empty<Bounds3>(), Lift, 0, 5, true);   // low side would be at 2
            Assert.Equal(0, lifted.Min.Y + down.Delta.Y, 6);
            Assert.Equal(SnapKind.Floor, Assert.Single(down.Guides).Kind);
        }

        [Fact]
        public void Pulling_a_face_snaps_to_the_other_box_and_keeps_the_far_face()
        {
            var board = Box(0, 0, 150, 200, 10, 50);                          // a flat board beside the block
            var (box, guide) = SceneSnap.Face(board, 0, highFace: true, proposedFace: 97, new[] { Big }, 0, 5, true);
            Assert.Equal(0, box.Min.X, 6);                                    // the far face stayed
            Assert.Equal(100, box.Max.X, 6);                                  // pulled to the block's side
            Assert.Equal(SnapKind.Edge, guide!.Kind);

            var (centred, g2) = SceneSnap.Face(board, 0, true, 52, new[] { Big }, 0, 5, true);
            Assert.Equal(50, centred.Max.X, 6);
            Assert.Equal(SnapKind.Centre, g2!.Kind);
        }

        [Fact]
        public void A_face_cannot_cross_the_other_face_and_uses_the_grid_when_free()
        {
            var board = Box(0, 0, 0, 200, 10, 50);
            var (thin, _) = SceneSnap.Face(board, 1, highFace: true, proposedFace: -40, Array.Empty<Bounds3>(), 0, 1, false);
            Assert.Equal(SceneSnap.MinSide, thin.Size.Y, 6);

            var (gridded, guide) = SceneSnap.Face(board, 2, highFace: false, proposedFace: -23, Array.Empty<Bounds3>(), 10, 1, true);
            Assert.Equal(-20, gridded.Min.Z, 6);
            Assert.Equal(50, gridded.Max.Z, 6);
            Assert.Null(guide);
        }

        [Fact]
        public void The_nearest_match_wins()
        {
            // low side at 49: the block's centre (50, 1 away) beats its far side (100) and its near side (0)
            var move = MoveSmallTo(49, 300, threshold: 60);
            Assert.Equal(50, Small.Min.X + move.Delta.X, 6);
        }
    }
}
