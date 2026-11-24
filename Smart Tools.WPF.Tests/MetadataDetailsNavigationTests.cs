using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>The previous / next buttons of the Metadata details window: they wrap round like Find Similar's.</summary>
    public class MetadataDetailsNavigationTests
    {
        [Theory]
        [InlineData(5, 0, 1, 1)]
        [InlineData(5, 3, 1, 4)]
        [InlineData(5, 4, 1, 0)]    // past the last: back to the first
        [InlineData(5, 0, -1, 4)]   // before the first: the last
        [InlineData(5, 2, -1, 1)]
        [InlineData(1, 0, 1, 0)]    // one file: stays
        [InlineData(1, 0, -1, 0)]
        [InlineData(3, 1, 7, 2)]    // bigger jumps still land inside the list
        [InlineData(3, 1, -7, 0)]
        public void Step_moves_and_wraps(int count, int index, int delta, int expected) =>
            Assert.Equal(expected, MetadataDetailsNavigation.Step(count, index, delta));

        [Fact]
        public void Step_on_an_empty_list_has_nowhere_to_go() =>
            Assert.Equal(-1, MetadataDetailsNavigation.Step(0, 0, 1));

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, true)]
        public void The_buttons_only_matter_with_more_than_one_file(int count, bool expected) =>
            Assert.Equal(expected, MetadataDetailsNavigation.CanStep(count));

        [Theory]
        [InlineData(12, 0, "1 / 12")]
        [InlineData(12, 11, "12 / 12")]
        [InlineData(0, 0, "")]
        [InlineData(3, -1, "")]
        public void Position_reads_one_based(int count, int index, string expected) =>
            Assert.Equal(expected, MetadataDetailsNavigation.Position(count, index));
    }
}
