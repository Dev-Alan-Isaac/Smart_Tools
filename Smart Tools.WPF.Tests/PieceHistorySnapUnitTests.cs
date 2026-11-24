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

        [Theory]
        [InlineData(SnapFace.Left, 100 - 40, 0, 0)]
        [InlineData(SnapFace.Right, 600, 0, 0)]
        [InlineData(SnapFace.Above, 100, 18, 0)]
        [InlineData(SnapFace.Below, 100, -700, 0)]
        [InlineData(SnapFace.InFront, 100, 0, 300)]
        [InlineData(SnapFace.Behind, 100, 0, -40)]
        public void Place_TouchesTheChosenFace_AndLinesUpAtTheStart(SnapFace face, double x, double y, double z)
        {
            var target = Box("shelf", 100, 0, 0, 500, 18, 300);
            var leg = Box("leg", 900, 900, 900, 40, 700, 40);
            var moved = PieceSnap.Place(leg, target, face, SnapAlign.Start);
            Assert.Equal(leg.Size, moved.Size);
            // the two touching axes follow the face, the others sit at the target's start edge
            double ex = face is SnapFace.Left or SnapFace.Right ? x : 100;
            double ey = face is SnapFace.Above or SnapFace.Below ? y : 0;
            double ez = face is SnapFace.InFront or SnapFace.Behind ? z : 0;
            Assert.Equal(new Vec3(ex, ey, ez), moved.Min);
        }

        [Fact]
        public void Place_CentreAndEnd_AlignTheOtherAxes()
        {
            var target = Box("top", 0, 0, 0, 1000, 30, 600);
            var leg = Box("leg", 0, 0, 0, 40, 700, 40);
            var centre = PieceSnap.Place(leg, target, SnapFace.Below, SnapAlign.Center);
            Assert.Equal(new Vec3(480, -700, 280), centre.Min);
            var end = PieceSnap.Place(leg, target, SnapFace.Below, SnapAlign.End);
            Assert.Equal(new Vec3(960, -700, 560), end.Min);
        }

        [Fact]
        public void Apply_SnappedBoardsTouchWithoutOverlap_AndBadPairsChangeNothing()
        {
            var parts = new List<Part> { Box("top", 0, 0, 0, 1000, 30, 600), Box("leg", 500, 500, 500, 40, 700, 40) };
            var snapped = PieceSnap.Apply(parts, 1, 0, SnapFace.Below, SnapAlign.Start);
            Assert.Empty(PieceChecks.Overlapping(snapped));
            Assert.Empty(PieceChecks.Loose(snapped));
            Assert.Same(parts, PieceSnap.Apply(parts, 1, 1, SnapFace.Left, SnapAlign.Start));
            Assert.Same(parts, PieceSnap.Apply(parts, 5, 0, SnapFace.Left, SnapAlign.Start));
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
