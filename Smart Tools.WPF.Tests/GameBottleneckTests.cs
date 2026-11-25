using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Matching hardware names to the table, reading the store's answers, and comparing a PC with a game. All estimates.</summary>
    [Collection("HardwareTable")]
    public class GameBottleneckTests
    {
        // ---------------------------------------------------------------- the table

        [Theory]
        [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", 19500)]
        [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", 14000)]
        [InlineData("AMD Ryzen 5 5600X 6-Core Processor", 21800)]
        [InlineData("AMD Ryzen 3 3100 4-Core Processor", 10500)]   // "4" after the number is not part of the model
        [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", 28500)]
        [InlineData("Intel Core i5-12400", 19500)]
        [InlineData("AMD Ryzen 3 5300U with Radeon Graphics", 9900)]   // a laptop chip is its own row, not the desktop chip it starts with
        [InlineData("AMD Ryzen 5 5600U with Radeon Graphics", 14800)]
        public void Processors_are_found_by_their_model(string name, int score) => Assert.Equal(score, HardwareScores.FindCpu(name)!.Score);

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3060", 17000)]
        [InlineData("NVIDIA GeForce RTX 3060 Ti", 20000)]       // the longer name wins over the shorter one inside it
        [InlineData("NVIDIA GeForce GTX 1660 SUPER", 12800)]
        [InlineData("AMD Radeon RX 6600 XT", 16000)]
        [InlineData("AMD Radeon RX 6600", 14000)]
        [InlineData("Intel(R) UHD Graphics 630", 1600)]
        [InlineData("NVIDIA GeForce GTX 1060 6GB", 10000)]
        public void Cards_are_found_by_their_model(string name, int score) => Assert.Equal(score, HardwareScores.FindGpu(name)!.Score);

        [Fact]
        public void An_unknown_part_is_null_and_a_number_inside_a_longer_number_is_not_a_match()
        {
            Assert.Null(HardwareScores.FindCpu("Acme Quantum 9000"));
            Assert.Null(HardwareScores.FindCpu(""));
            Assert.Null(HardwareScores.FindCpu(null));
            Assert.Null(HardwareScores.FindGpu("Radeon RX 15800"));   // "RX 580" must not be found inside 15800
        }

        [Fact]
        public void A_laptop_card_counts_for_less_than_the_desktop_card_of_the_same_name()
        {
            var desktop = HardwareScores.ScoreGpu("NVIDIA GeForce RTX 3060")!.Value;
            var laptop = HardwareScores.ScoreGpu("NVIDIA GeForce RTX 3060 Laptop GPU")!.Value;
            Assert.False(desktop.Laptop);
            Assert.True(laptop.Laptop);
            Assert.Equal((int)(desktop.Score * HardwareScores.LaptopFactor), laptop.Score);
            Assert.True(HardwareScores.ScoreGpu("GeForce RTX 3050 Max-Q")!.Value.Laptop);
        }

        [Fact]
        public void The_lists_are_sorted_fastest_first_and_have_no_repeated_part()
        {
            var cpus = HardwareScores.AllCpus;
            Assert.Equal(cpus.OrderByDescending(p => p.Score), cpus);
            Assert.Equal(cpus.Count, cpus.Select(p => p.Key).Distinct().Count());
            var gpus = HardwareScores.AllGpus;
            Assert.Equal(gpus.Count, gpus.Select(p => p.Key).Distinct().Count());
            Assert.True(cpus.Count > 100 && gpus.Count > 100);
        }

        // ---------------------------------------------------------------- the store's answers

        [Fact]
        public void A_search_answer_gives_the_games_and_skips_other_kinds()
        {
            const string json = "{\"total\":3,\"items\":[{\"type\":\"app\",\"name\":\"Elden Ring\",\"id\":1245620},{\"type\":\"sub\",\"name\":\"Bundle\",\"id\":9},{\"type\":\"app\",\"name\":\"Elden Ring Soundtrack\",\"id\":1\"}]}";
            Assert.Empty(GameBottleneckMath.ParseSearch(json));   // broken JSON: nothing, no crash
            const string good = "{\"total\":3,\"items\":[{\"type\":\"app\",\"name\":\"Elden Ring\",\"id\":1245620},{\"type\":\"sub\",\"name\":\"Bundle\",\"id\":9},{\"type\":\"app\",\"name\":\"Another\",\"id\":7}]}";
            var hits = GameBottleneckMath.ParseSearch(good);
            Assert.Equal(new[] { 1245620, 7 }, hits.Select(h => h.AppId));
            Assert.Equal("Elden Ring", hits[0].Name);
            Assert.Empty(GameBottleneckMath.ParseSearch("{}"));
            Assert.Empty(GameBottleneckMath.ParseSearch("not json"));
        }

        private const string MinHtml = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> Windows 10 64-bit<br></li>"
            + "<li><strong>Processor:</strong> Intel Core i5-4460 / AMD FX-6300<br></li><li><strong>Memory:</strong> 8 GB RAM<br></li>"
            + "<li><strong>Graphics:</strong> NVIDIA GeForce GTX 960 / AMD Radeon R9 280<br></li><li><strong>Storage:</strong> 50 GB available space</li></ul>";

        private const string RecHtml = "<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li><strong>OS *:</strong> Windows 11<br></li>"
            + "<li><strong>Processor:</strong> Intel Core i7-8700K<br></li><li><strong>Memory:</strong> 16 GB RAM<br></li>"
            + "<li><strong>Graphics:</strong> NVIDIA GeForce RTX 2060<br></li></ul>";

        [Fact]
        public void A_requirements_block_is_read_into_its_four_fields()
        {
            var level = GameBottleneckMath.ParseLevel(MinHtml);
            Assert.Equal("Intel Core i5-4460 / AMD FX-6300", level.Processor);
            Assert.Equal("NVIDIA GeForce GTX 960 / AMD Radeon R9 280", level.Graphics);
            Assert.Equal(8, level.MemoryGb);
            Assert.Equal("50 GB available space", level.Storage);
            Assert.True(GameBottleneckMath.ParseLevel(null).IsEmpty);
            Assert.True(GameBottleneckMath.ParseLevel("<b>Nothing useful</b>").IsEmpty);
        }

        [Theory]
        [InlineData("8 GB RAM", 8.0)]
        [InlineData("512 MB", 0.5)]
        [InlineData("16GB", 16.0)]
        [InlineData("1,5 GB", 1.5)]
        public void Memory_text_is_read_in_gigabytes(string text, double gb) => Assert.Equal(gb, GameBottleneckMath.ParseMemoryGb(text));

        [Fact]
        public void The_details_answer_gives_the_spec_and_an_empty_array_means_none()
        {
            string json = "{\"123\":{\"success\":true,\"data\":{\"name\":\"Test Game\",\"pc_requirements\":{\"minimum\":" + System.Text.Json.JsonSerializer.Serialize(MinHtml) + ",\"recommended\":" + System.Text.Json.JsonSerializer.Serialize(RecHtml) + "}}}}";
            var spec = GameBottleneckMath.ParseDetails(json, 123)!;
            Assert.Equal("Test Game", spec.Name);
            Assert.Equal(16, spec.Recommended.MemoryGb);
            Assert.Equal("Intel Core i7-8700K", spec.Recommended.Processor);

            Assert.Null(GameBottleneckMath.ParseDetails("{\"5\":{\"success\":true,\"data\":{\"name\":\"X\",\"pc_requirements\":[]}}}", 5));
            Assert.Null(GameBottleneckMath.ParseDetails("{\"5\":{\"success\":false}}", 5));
            Assert.Null(GameBottleneckMath.ParseDetails("{}", 5));
            Assert.Null(GameBottleneckMath.ParseDetails("garbage", 5));
        }

        // ---------------------------------------------------------------- requirement text -> score

        [Fact]
        public void With_alternatives_the_weakest_named_part_is_what_the_game_needs()
        {
            var cpu = GameBottleneckMath.RequiredScore("Intel Core i5-4460 / AMD FX-6300", gpu: false)!.Value;
            Assert.Equal(3700, cpu.Score);   // the FX-6300 is the easier one
            var gpu = GameBottleneckMath.RequiredScore("NVIDIA GeForce GTX 960 or AMD Radeon R9 280", gpu: true)!.Value;
            Assert.Equal(3800, gpu.Score);
            Assert.Null(GameBottleneckMath.RequiredScore("Quad core 3.0 GHz", gpu: false));
            Assert.Null(GameBottleneckMath.RequiredScore(null, gpu: true));
        }

        // ---------------------------------------------------------------- the comparison

        private static GameSpec Game(string? minCpu = "Intel Core i5-4460", string? minGpu = "GeForce GTX 960", double? minRam = 8,
            string? recCpu = "Intel Core i7-8700K", string? recGpu = "GeForce RTX 2060", double? recRam = 16) =>
            new("Test", 1, new SpecLevel(minCpu, minGpu, minRam, null), new SpecLevel(recCpu, recGpu, recRam, null));

        [Fact]
        public void A_strong_pc_meets_the_recommended_spec_and_nothing_limits_it()
        {
            var pc = GameBottleneckMath.Profile("AMD Ryzen 7 5800X3D 8-Core Processor", "NVIDIA GeForce RTX 3070", 32);
            var result = GameBottleneckMath.Compare(pc, Game());
            Assert.Equal(LevelVerdict.Comfortable, result.Minimum.Verdict);
            Assert.True(result.Recommended.Verdict is LevelVerdict.Meets or LevelVerdict.Comfortable);
            Assert.Equal(LimitedBy.Nothing, result.Limit);
        }

        [Fact]
        public void A_weak_graphics_card_is_the_limit_when_the_processor_is_fine()
        {
            var pc = GameBottleneckMath.Profile("Intel(R) Core(TM) i7-12700K", "NVIDIA GeForce GTX 1050", 16);
            var result = GameBottleneckMath.Compare(pc, Game());
            Assert.Equal(LimitedBy.Gpu, result.Limit);
            Assert.Equal("GPU", result.LimitPart);
            Assert.True(result.Recommended.Gpu.Ratio < 1.0);
            Assert.True(result.Recommended.Cpu.Ratio > 1.0);
            Assert.Equal(LevelVerdict.Below, result.Recommended.Verdict);
        }

        [Fact]
        public void A_weak_processor_is_the_limit_when_the_card_is_fine()
        {
            var pc = GameBottleneckMath.Profile("Intel(R) Core(TM) i3-6100", "NVIDIA GeForce RTX 3080", 16);
            var result = GameBottleneckMath.Compare(pc, Game());
            Assert.Equal(LimitedBy.Cpu, result.Limit);
        }

        [Fact]
        public void Too_little_memory_is_named_as_the_limit()
        {
            var pc = GameBottleneckMath.Profile("AMD Ryzen 7 5800X", "NVIDIA GeForce RTX 3070", 4);
            var result = GameBottleneckMath.Compare(pc, Game());
            Assert.Equal(LimitedBy.Memory, result.Limit);
            Assert.Equal(LevelVerdict.Below, result.Minimum.Verdict);   // 4 GB is under the 8 GB minimum
        }

        [Fact]
        public void Two_weak_parts_close_together_are_called_balanced()
        {
            var pc = new HardwareProfile("cpu", 6500, "gpu", 6000, 16);   // both under the recommended spec by a similar amount
            var result = GameBottleneckMath.Compare(pc, Game());
            Assert.Equal(LimitedBy.Balanced, result.Limit);
        }

        [Fact]
        public void Parts_that_are_not_in_the_table_are_left_out_not_guessed()
        {
            var pc = GameBottleneckMath.Profile("Acme CPU", "Acme GPU", 16);
            Assert.Null(pc.CpuScore);
            Assert.Null(pc.GpuScore);
            var result = GameBottleneckMath.Compare(pc, Game());
            Assert.Null(result.Recommended.Cpu.Ratio);
            Assert.NotNull(result.Recommended.Memory.Ratio);                      // memory is still known
            Assert.Equal(LevelVerdict.Comfortable, GameBottleneckMath.Level(pc, new SpecLevel(null, null, 8, null)).Verdict);

            var unknownGame = GameBottleneckMath.Compare(GameBottleneckMath.Profile("Ryzen 5 5600X", "RTX 3060", 16), Game("Mystery chip", "Mystery card", null, null, null, null));
            Assert.Equal(LevelVerdict.Unknown, unknownGame.Minimum.Verdict);
            Assert.Equal(LimitedBy.Unknown, unknownGame.Limit);
        }

        [Fact]
        public void The_limit_is_judged_on_the_minimum_when_the_game_lists_no_recommended_spec()
        {
            var pc = GameBottleneckMath.Profile("Intel(R) Core(TM) i7-12700K", "NVIDIA GeForce GTX 1630", 16);
            var game = new GameSpec("Only minimum", 1, new SpecLevel("Intel Core i5-4460", "GeForce GTX 970", 8, null), new SpecLevel(null, null, null, null));
            var result = GameBottleneckMath.Compare(pc, game);
            Assert.Equal(LevelVerdict.Unknown, result.Recommended.Verdict);
            Assert.Same(result.Minimum, result.Judged);
            Assert.Equal(LimitedBy.Gpu, result.Limit);
        }
    }
}
