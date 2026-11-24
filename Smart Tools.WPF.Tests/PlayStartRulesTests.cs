using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>
    /// Media Viewer (Mixed page): files were added but none chosen yet. The Play button must work then,
    /// starting the highlighted row, or the first one when nothing is highlighted.
    /// </summary>
    public class PlayStartRulesTests
    {
        [Fact]
        public void Play_is_pressable_when_files_are_listed_even_if_none_is_current() =>
            Assert.True(PlayStartRules.CanPress(hasCurrent: false, itemCount: 3));

        [Fact]
        public void Play_is_pressable_for_the_current_item() =>
            Assert.True(PlayStartRules.CanPress(hasCurrent: true, itemCount: 1));

        [Fact]
        public void Play_is_not_pressable_with_an_empty_list_and_nothing_current() =>
            Assert.False(PlayStartRules.CanPress(hasCurrent: false, itemCount: 0));

        [Theory]
        [InlineData(3, 2, 2)]    // the highlighted row wins
        [InlineData(3, 0, 0)]
        [InlineData(3, -1, 0)]   // nothing highlighted: the top
        [InlineData(3, 3, 0)]    // a stale highlight past the end: the top
        [InlineData(0, -1, -1)]  // an empty list has nothing to start
        public void The_first_item_to_play_is_the_highlighted_row_or_else_the_top(int count, int selected, int expected) =>
            Assert.Equal(expected, PlayStartRules.StartIndex(count, selected));
    }
}
