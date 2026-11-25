using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Measuring the free space inside a piece, and hiding boards (a hidden door neither blocks a gap nor can be picked).</summary>
    public class PieceGapsTests
    {
        private static Part Board(string name, double x, double y, double z, double w, double h, double d) =>
            new(name, new Vec3(x, y, z), new Vec3(w, h, d), PartKind.Board);

        // a 600 wide, 800 tall, 300 deep box: two sides, a bottom, a top, a shelf in the middle, and a door in front
        private static List<Part> Cabinet() => new()
        {
            Board("Left", 0, 0, 0, 18, 800, 300),
            Board("Right", 582, 0, 0, 18, 800, 300),
            Board("Bottom", 18, 0, 0, 564, 18, 300),
            Board("Top", 18, 782, 0, 564, 18, 300),
            Board("Shelf", 18, 300, 0, 564, 18, 280),
            Board("Door", 0, 0, 300, 600, 800, 18),
        };

        [Fact]
        public void A_shelf_measures_the_space_to_the_boards_around_it()
        {
            var parts = Cabinet();
            var shelf = parts.Single(p => p.Name == "Shelf");
            var gaps = DimensionLines.Gaps(parts, shelf).Select(g => Math.Round(g.LengthMm, 3)).OrderBy(x => x).ToList();
            // below to the bottom board (300 - 18), above to the top (782 - 318), to each side wall (touching: no line), to the door (300 - 280)
            Assert.Contains(282.0, gaps);
            Assert.Contains(464.0, gaps);
            Assert.Contains(20.0, gaps);
            Assert.DoesNotContain(0.0, gaps);
        }

        [Fact]
        public void A_hidden_door_does_not_count_so_the_shelf_looks_at_nothing_in_front()
        {
            var parts = Cabinet();
            var shelf = parts.Single(p => p.Name == "Shelf");
            var gaps = DimensionLines.Gaps(parts, shelf, p => p.Name == "Door");
            Assert.DoesNotContain(gaps, g => Math.Abs(g.LengthMm - 20) < 1e-9);
            Assert.Equal(2, gaps.Count);   // only the bottom and the top boards are left in view
        }

        [Fact]
        public void Gap_lines_run_between_the_two_faces_through_the_middle_of_where_they_overlap()
        {
            var parts = Cabinet();
            var gap = DimensionLines.Gaps(parts, parts.Single(p => p.Name == "Shelf"), p => p.Name == "Door")
                .Single(g => Math.Abs(g.LengthMm - 282) < 1e-9);
            Assert.Equal(new Vec3(300, 18, 140), gap.A);    // top of the bottom board, middle of the shelf's width and depth
            Assert.Equal(new Vec3(300, 300, 140), gap.B);   // underside of the shelf
            Assert.Equal(gap.A, gap.Start);                  // no offset: the line is drawn in the empty space itself
        }

        [Fact]
        public void A_hidden_board_cannot_be_picked_and_the_click_goes_through_it()
        {
            var parts = Cabinet();
            var ray = new Ray3(new Vec3(300, 400, 2000), new Vec3(0, 0, -1));
            Assert.Equal(parts.FindIndex(p => p.Name == "Door"), PieceDrag.Pick(parts, ray, out _));
            int behind = PieceDrag.Pick(parts, ray, out _, p => p.Name == "Door");
            Assert.Equal(-1, behind);   // nothing is inside the cabinet at that height
            Assert.NotEqual(parts.FindIndex(p => p.Name == "Door"), behind);
        }
    }
}
