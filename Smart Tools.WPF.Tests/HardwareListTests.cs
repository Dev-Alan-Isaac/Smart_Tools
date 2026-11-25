using System.IO;
using System.Linq;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Parts that are new since the table was made (estimated from their family), and the downloadable part list. The table is shared state, so these run one after another.</summary>
    [Collection("HardwareTable")]
    public class HardwareListTests
    {
        // ---------------------------------------------------------------- a part that is not in the table

        [Fact]
        public void A_new_generation_is_estimated_from_the_same_line_one_generation_back()
        {
            var gpu = HardwareScores.ResolveGpu("NVIDIA GeForce RTX 5070")!;
            Assert.True(gpu.Estimated);
            Assert.Equal("GeForce RTX 4070", gpu.BasedOn);
            Assert.Equal((int)Math.Round(27000 * 1.2), gpu.Score);

            var ti = HardwareScores.ResolveGpu("NVIDIA GeForce RTX 5070 Ti")!;
            Assert.Equal("GeForce RTX 4070 TI", ti.BasedOn);                  // the same suffix line, not the plain card with a guess on top
            Assert.Equal((int)Math.Round(31000 * 1.2), ti.Score);
            Assert.True(ti.Score > gpu.Score);

            Assert.Equal((int)Math.Round(13000 * 1.2), HardwareScores.ResolveGpu("GeForce RTX 4050")!.Score);   // from the RTX 3050
            Assert.Equal((int)Math.Round(25000 * 1.2), HardwareScores.ResolveGpu("AMD Radeon RX 8800 XT")!.Score);
        }

        [Fact]
        public void New_processors_step_up_a_little_per_generation()
        {
            var ryzen = HardwareScores.ResolveCpu("AMD Ryzen 9 9950X 16-Core Processor")!;
            Assert.True(ryzen.Estimated);
            Assert.Equal("Ryzen 9 7950X", ryzen.BasedOn);
            Assert.Equal((int)Math.Round(63000 * 1.08 * 1.08), ryzen.Score);

            var intel = HardwareScores.ResolveCpu("Intel(R) Core(TM) i5-15600K")!;
            Assert.Equal("Core i5-14600K", intel.BasedOn);
            Assert.Equal((int)Math.Round(39500 * 1.08), intel.Score);
        }

        [Fact]
        public void A_part_in_the_table_is_exact_and_an_unknown_family_gives_nothing()
        {
            var exact = HardwareScores.ResolveGpu("NVIDIA GeForce RTX 3060")!;
            Assert.False(exact.Estimated);
            Assert.Null(exact.BasedOn);
            Assert.Equal(17000, exact.Score);
            Assert.Null(HardwareScores.ResolveCpu("Acme Quantum 9000"));
            Assert.Null(HardwareScores.ResolveCpu("Intel Core Ultra 7 265K"));   // a different naming scheme: not guessed
            Assert.Null(HardwareScores.ResolveGpu(""));
        }

        [Fact]
        public void An_estimate_counts_in_the_comparison_and_says_what_it_is_based_on()
        {
            var pc = GameBottleneckMath.Profile("Intel(R) Core(TM) i5-15600K", "NVIDIA GeForce RTX 5070", 32);
            Assert.NotNull(pc.CpuScore);
            Assert.NotNull(pc.GpuScore);
            Assert.Equal("Core i5-14600K", pc.CpuEstimatedFrom);
            Assert.Equal("GeForce RTX 4070", pc.GpuEstimatedFrom);

            // a game that asks for a card the table lacks still gets a requirement, marked with "~"
            var need = GameBottleneckMath.RequiredScore("NVIDIA GeForce RTX 5060", gpu: true)!.Value;
            Assert.StartsWith("~", need.Matched);
            Assert.Equal((int)Math.Round(19500 * 1.2), need.Score);
        }

        [Fact]
        public void A_laptop_card_that_is_estimated_is_scaled_down_too()
        {
            var desktop = HardwareScores.ScoreGpu("NVIDIA GeForce RTX 5070")!.Value;
            var laptop = HardwareScores.ScoreGpu("NVIDIA GeForce RTX 5070 Laptop GPU")!.Value;
            Assert.True(laptop.Laptop);
            Assert.Equal((int)(desktop.Score * HardwareScores.LaptopFactor), laptop.Score);
        }

        // ---------------------------------------------------------------- a part list file

        private const string Good = "{\"version\":\"2027-03-01\",\"cpus\":[{\"name\":\"Core i5-15600K\",\"score\":41000}],\"gpus\":[{\"name\":\"RTX 5070\",\"score\":33000},{\"name\":\"RTX 3060\",\"score\":17500}]}";

        [Fact]
        public void A_good_list_is_read()
        {
            Assert.True(HardwareScores.TryParseTableFile(Good, out var file, out _));
            Assert.Equal("2027-03-01", file.Version);
            Assert.Equal(new HardwareEntry("RTX 5070", 33000), file.Gpus[0]);
            Assert.True(HardwareScores.TryParseTableFile("{\"version\":\"1\",\"gpus\":[{\"name\":\"RTX 9999\",\"score\":1}]}", out _, out _));   // only cards is fine
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{\"cpus\":[{\"name\":\"Core i5\",\"score\":1}]}")]                                          // no version
        [InlineData("{\"version\":\"1\"}")]                                                                      // no parts
        [InlineData("{\"version\":\"1\",\"cpus\":[{\"name\":\"Core i5-1\",\"score\":0}]}")]                      // a score of 0
        [InlineData("{\"version\":\"1\",\"cpus\":[{\"name\":\"Core i5-1\",\"score\":9999999}]}")]                // an absurd score
        [InlineData("{\"version\":\"1\",\"cpus\":[{\"name\":\"\",\"score\":5}]}")]                              // no name
        [InlineData("{\"version\":\"1\",\"cpus\":[{\"name\":\"Core i5-1\",\"score\":\"fast\"}]}")]               // not a number
        [InlineData("{\"version\":\"1\",\"cpus\":\"all of them\"}")]
        public void A_broken_list_is_refused_as_a_whole(string json) => Assert.False(HardwareScores.TryParseTableFile(json, out _, out _));

        [Fact]
        public void One_bad_line_refuses_the_whole_list_so_nothing_is_half_applied()
        {
            const string json = "{\"version\":\"1\",\"gpus\":[{\"name\":\"RTX 5070\",\"score\":33000},{\"name\":\"RTX 5080\",\"score\":-4}]}";
            Assert.False(HardwareScores.TryParseTableFile(json, out _, out string error));
            Assert.Contains("gpus", error);
        }

        [Fact]
        public void A_list_adds_parts_and_corrects_old_ones_and_going_back_restores_the_built_in_table()
        {
            try
            {
                int cpusBefore = HardwareScores.CpuCount, gpusBefore = HardwareScores.GpuCount;
                Assert.True(HardwareScores.ResolveGpu("NVIDIA GeForce RTX 5070")!.Estimated);

                Assert.True(HardwareScores.TryParseTableFile(Good, out var file, out _));
                HardwareScores.Apply(file);
                Assert.Equal("2027-03-01", HardwareScores.ActiveVersion);
                Assert.Equal(gpusBefore + 1, HardwareScores.GpuCount);                 // RTX 5070 is new
                Assert.Equal(cpusBefore + 1, HardwareScores.CpuCount);
                var now = HardwareScores.ResolveGpu("NVIDIA GeForce RTX 5070")!;
                Assert.False(now.Estimated);                                           // known exactly now
                Assert.Equal(33000, now.Score);
                Assert.Equal(17500, HardwareScores.FindGpu("NVIDIA GeForce RTX 3060")!.Score);   // corrected, not doubled

                // the same part written another way is still one part
                HardwareScores.Apply(new HardwareTableFile("x", Array.Empty<HardwareEntry>(), new[] { new HardwareEntry("GeForce RTX 4090", 40000) }));
                Assert.Equal(gpusBefore, HardwareScores.GpuCount);
                Assert.Equal(40000, HardwareScores.FindGpu("RTX 4090")!.Score);
            }
            finally
            {
                HardwareScores.Apply(null);
            }
            Assert.Equal(HardwareScores.BuiltInVersion, HardwareScores.ActiveVersion);
            Assert.Equal(38500, HardwareScores.FindGpu("RTX 4090")!.Score);
        }

        [Fact]
        public void The_list_file_that_ships_with_the_sources_is_valid_and_keeps_every_built_in_part()
        {
            string? path = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && path == null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Smart Tools.WPF", "Tools", "Performance", "GameBottleneck", "Data", "hardware-scores.json");
                if (File.Exists(candidate)) path = candidate;
            }
            Assert.NotNull(path);
            Assert.True(HardwareScores.TryParseTableFile(File.ReadAllText(path!), out var shipped, out string error), error);
            Assert.True(HardwareScores.TryParseTableFile(HardwareScores.BuiltInJson(), out var builtIn, out _));

            // This is the file users download, so it is meant to run AHEAD of the built-in table: new parts and corrected scores are welcome.
            // It must never lose a part the built-in table has, and its version (a date) must not be older.
            Assert.True(string.CompareOrdinal(shipped.Version, builtIn.Version) >= 0, "the list's version is older than the built-in table");
            Assert.Empty(builtIn.Cpus.Select(e => e.Name).Except(shipped.Cpus.Select(e => e.Name)));
            Assert.Empty(builtIn.Gpus.Select(e => e.Name).Except(shipped.Gpus.Select(e => e.Name)));
        }
    }
}
