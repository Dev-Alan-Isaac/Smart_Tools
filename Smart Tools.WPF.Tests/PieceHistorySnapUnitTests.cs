using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    public class PieceHistorySnapUnitTests
    {
        private static Part Box(string n, double x, double y, double z, double w, double h, double d) =>
            new(n, new Vec3(x, y, z), new Vec3(w, h, d), PartKind.Board);

        private static PieceState State(string name, params Part[] parts) => new(name, parts);

        [Fact]
        public void Undo_AndRedo_WalkBackAndForward()
        {
            var h = new PieceHistory();
            var a = State("a"); var b = State("b", Box("x", 0, 0, 0, 1, 1, 1));
            h.Push(a, b);
            Assert.True(h.CanUndo);
            Assert.False(h.CanRedo);
            Assert.Equal(a, h.Undo(b));
            Assert.True(h.CanRedo);
            Assert.Equal(b, h.Redo(a));
            Assert.Null(h.Redo(b));
        }

        [Fact]
        public void Push_OfNoChange_IsNotRecorded_AndNewChangeClearsRedo()
        {
            var h = new PieceHistory();
            var a = State("a"); var b = State("b");
            h.Push(a, State("a"));
            Assert.False(h.CanUndo);
            h.Push(a, b);
            h.Undo(b);
            h.Push(a, State("c"));
            Assert.False(h.CanRedo);
        }

        [Fact]
        public void History_KeepsOnlyTheLastHundred()
        {
            var h = new PieceHistory();
            for (int i = 0; i < PieceHistory.Limit + 20; i++) h.Push(State("n" + i), State("n" + (i + 1)));
            int steps = 0;
            var cur = State("end");
            while (h.Undo(cur) is { } back) { cur = back; steps++; }
            Assert.Equal(PieceHistory.Limit, steps);
        }

        private static readonly OrbitCamera Cam = new() { Yaw = 35, Pitch = 22, Distance = 3000, Target = new Vec3(500, 300, 300) };

        [Fact]
        public void Ray_ThroughAProjectedPoint_PassesThroughThatPoint()
        {
            var p = new Vec3(620, 140, 410);
            var s = Cam.Project(p, 900, 600)!.Value;
            var ray = Cam.Ray(s.X, s.Y, 900, 600);
            double along = PieceDrag.ClosestOnAxis(ray, p, new Vec3(1, 0, 0)) ?? double.NaN;   // any line through p
            var onLine = p + new Vec3(1, 0, 0) * along;
            var onRay = PieceDrag.PointAt(ray, Vec3.Dot(onLine - ray.Origin, ray.Direction));
            Assert.True((onRay - p).Length < 1.0);
        }

        [Fact]
        public void Pick_ReturnsTheNearestBoardUnderThePointer()
        {
            var parts = new List<Part> { Box("back", 0, 0, 0, 500, 500, 50), Box("front", 0, 0, 200, 500, 500, 50) };
            var ray = new Ray3(new Vec3(250, 250, 2000), new Vec3(0, 0, -1));
            Assert.Equal(1, PieceDrag.Pick(parts, ray, out double d));
            Assert.Equal(1750, d, 6);
            Assert.Equal(-1, PieceDrag.Pick(parts, new Ray3(new Vec3(900, 250, 2000), new Vec3(0, 0, -1)), out _));
        }

        [Fact]
        public void RayHitsHeight_FindsTheFloorPoint_AndRefusesAParallelOrBackwardsRay()
        {
            var hit = PieceDrag.RayHitsHeight(new Ray3(new Vec3(0, 100, 0), new Vec3(0, -1, 1).Normalized), 0);
            Assert.NotNull(hit);
            Assert.Equal(100, hit!.Value.Z, 6);
            Assert.Null(PieceDrag.RayHitsHeight(new Ray3(new Vec3(0, 100, 0), new Vec3(1, 0, 0)), 0));
            Assert.Null(PieceDrag.RayHitsHeight(new Ray3(new Vec3(0, 100, 0), new Vec3(0, 1, 0)), 0));
        }

        [Fact]
        public void ClosestOnAxis_MeasuresAMouseMoveAlongAnAxis()
        {
            var up = new Vec3(0, 1, 0);
            var look = new Ray3(new Vec3(0, 300, 1000), new Vec3(0, 0, -1));
            Assert.Equal(300, PieceDrag.ClosestOnAxis(look, new Vec3(0, 0, 0), up)!.Value, 6);
            Assert.Null(PieceDrag.ClosestOnAxis(new Ray3(new Vec3(0, 0, 0), up), new Vec3(5, 5, 5), up));
        }

        [Fact]
        public void Move_StopsAgainstTheFaceOfAnotherBoard_WhenCloseEnough()
        {
            var parts = new List<Part> { Box("top", 0, 700, 0, 1000, 30, 600), Box("leg", 0, 0, 0, 40, 700, 40) };
            // dropped 6 mm too low and 9 mm off to the side: it climbs under the top and lines up with its start edge
            var r = PieceDrag.Move(parts, 1, new Vec3(9, -6, 3), grid: 0, threshold: 12, magnet: true);
            Assert.Equal(new Vec3(0, 0, 0), r.Min);   // y: floor and "under the top" are both 0 here
            Assert.Equal(0, r.Target[0]);
            Assert.Equal(0, r.Target[2]);
        }

        [Fact]
        public void Move_PutsABoardOnTopOfAnother_AndOnTheFloor()
        {
            var parts = new List<Part> { Box("base", 0, 0, 0, 500, 20, 500), Box("shelf", 0, 300, 0, 500, 20, 500) };
            var r = PieceDrag.Move(parts, 1, new Vec3(200, 25, 100), grid: 0, threshold: 12, magnet: true);
            Assert.Equal(20, r.Min.Y);   // sits on the base
            Assert.Equal(0, r.Target[1]);
            var floor = PieceDrag.Move(parts, 1, new Vec3(900, 8, 900), grid: 0, threshold: 12, magnet: true);
            Assert.Equal(0, floor.Min.Y);
            Assert.Equal(SnapResult.Floor, floor.Target[1]);
        }

        [Fact]
        public void Move_FallsBackToTheGrid_AndHonoursMagnetOffAndGridOff()
        {
            var parts = new List<Part> { Box("a", 0, 0, 0, 100, 100, 100), Box("b", 500, 0, 500, 100, 100, 100) };
            var grid = PieceDrag.Move(parts, 1, new Vec3(333, 47, 262), grid: 10, threshold: 5, magnet: false);
            Assert.Equal(new Vec3(330, 50, 260), grid.Min);
            Assert.All(grid.Target, t => Assert.Equal(SnapResult.None, t));
            var free = PieceDrag.Move(parts, 1, new Vec3(333.3, 47.1, 262.2), grid: 0, threshold: 5, magnet: false);
            Assert.Equal(new Vec3(333.3, 47.1, 262.2), free.Min);
            var far = PieceDrag.Move(parts, 1, new Vec3(150, 30, 30), grid: 0, threshold: 5, magnet: true);
            Assert.Equal(150, far.Min.X);   // nothing within 5 mm: not pulled
        }

        [Fact]
        public void Resize_MovesOnlyTheDraggedFace_SnapsToANeighbourAndKeepsAMinimum()
        {
            var parts = new List<Part> { Box("a", 0, 0, 0, 100, 50, 50), Box("wall", 400, 0, 0, 20, 300, 300) };
            var grown = PieceDrag.Resize(parts, 0, 0, highFace: true, proposedFace: 396, grid: 0, threshold: 10, magnet: true);
            Assert.Equal(0, grown.Min.X);
            Assert.Equal(400, grown.Size.X);   // reached the wall
            Assert.Equal(50, grown.Size.Y);
            var left = PieceDrag.Resize(parts, 0, 0, highFace: false, proposedFace: -34, grid: 10, threshold: 0, magnet: false);
            Assert.Equal(-30, left.Min.X);     // grid
            Assert.Equal(130, left.Size.X);    // the far face stayed at 100
            var tiny = PieceDrag.Resize(parts, 0, 0, highFace: true, proposedFace: -500, grid: 0, threshold: 0, magnet: false);
            Assert.Equal(PieceEditor.MinSize, tiny.Size.X);
        }

        [Theory]
        [InlineData("450", LengthUnit.Millimetres, 450)]
        [InlineData("45", LengthUnit.Centimetres, 450)]
        [InlineData("2", LengthUnit.Inches, 50.8)]
        [InlineData("2 in", LengthUnit.Millimetres, 50.8)]
        [InlineData("30cm", LengthUnit.Inches, 300)]
        [InlineData("1,2 m", LengthUnit.Centimetres, 1200)]
        [InlineData("12.5mm", LengthUnit.Inches, 12.5)]
        [InlineData("2\"", LengthUnit.Millimetres, 50.8)]
        public void TryParseMm_ReadsTheUnitTypedOrTheBoxsUnit(string text, LengthUnit box, double mm)
        {
            Assert.True(LengthFormat.TryParseMm(text, box, out double got));
            Assert.Equal(mm, got, 6);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("5 ft")]
        [InlineData("1e3")]
        public void TryParseMm_RefusesWhatIsNotALength(string text) => Assert.False(LengthFormat.TryParseMm(text, LengthUnit.Millimetres, out _));

        [Fact]
        public void Number_ShowsTheValueInTheUnit_AndRoundTrips()
        {
            Assert.Equal("120", LengthFormat.Number(1200, LengthUnit.Centimetres));
            Assert.Equal("47.244", LengthFormat.Number(1200, LengthUnit.Inches));
            Assert.Equal("1200", LengthFormat.Number(1200, LengthUnit.Millimetres));
            Assert.True(LengthFormat.TryParseMm(LengthFormat.Number(18, LengthUnit.Inches), LengthUnit.Inches, out double back));
            Assert.InRange(back, 17.98, 18.02);
        }
    }
}
