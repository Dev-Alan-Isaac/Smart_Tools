using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Numbers typed into the size and print boxes: a comma or a dot, in any regional setting.</summary>
    public class NumberInputTests
    {
        [Theory]
        [InlineData("12.5", 12.5)]
        [InlineData("12,5", 12.5)]
        [InlineData("  7 ", 7)]
        [InlineData("-3", -3)]
        [InlineData("1200", 1200)]
        [InlineData("0.2", 0.2)]
        [InlineData(".5", 0.5)]
        public void Reads_the_number(string text, double expected)
        {
            Assert.True(NumberInput.TryParse(text, out double value));
            Assert.Equal(expected, value, 9);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("abc")]
        [InlineData("12mm")]
        [InlineData("1.2.3")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        public void Refuses_what_is_not_a_number(string? text) => Assert.False(NumberInput.TryParse(text, out _));

        [Fact]
        public void The_regional_setting_does_not_change_the_answer()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            try
            {
                Assert.True(NumberInput.TryParse("12.5", out double a));
                Assert.True(NumberInput.TryParse("12,5", out double b));
                Assert.Equal((12.5, 12.5), (a, b));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Format_writes_a_short_dot_number()
        {
            Assert.Equal("1200", NumberInput.Format(1200));
            Assert.Equal("12.5", NumberInput.Format(12.5));
            Assert.Equal("0.2", NumberInput.Format(0.2));
            Assert.Equal("0.33", NumberInput.Format(0.3333));
        }
    }
}
