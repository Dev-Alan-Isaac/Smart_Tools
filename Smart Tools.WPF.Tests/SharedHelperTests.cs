using System.IO;
using System.Text.Json;
using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>A fresh empty folder per test, removed afterwards.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "st-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    public class ByteFormatTests
    {
        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1024L, "1 KB")]
        [InlineData(1536L, "1.5 KB")]
        [InlineData(1048576L, "1 MB")]
        [InlineData(1610612736L, "1.5 GB")]
        [InlineData(1099511627776L, "1 TB")]
        [InlineData(2251799813685248L, "2048 TB")]
        public void Formats_like_the_tools_show_it(long bytes, string expected) =>
            Assert.Equal(expected, ByteFormat.Format(bytes));
    }

    public class NaturalStringComparerTests
    {
        [Fact]
        public void Numbers_sort_by_value_and_case_is_ignored()
        {
            var names = new[] { "IMG_10", "img_2", "IMG_1", "IMG_100" };
            var sorted = names.OrderBy(n => n, NaturalStringComparer.Instance).ToArray();
            Assert.Equal(new[] { "IMG_1", "img_2", "IMG_10", "IMG_100" }, sorted);
        }

        [Fact]
        public void Leading_zeros_do_not_change_the_order_of_different_numbers() =>
            Assert.True(NaturalStringComparer.Instance.Compare("a02", "a10") < 0);

        [Fact]
        public void A_shorter_name_comes_first_when_it_is_a_prefix() =>
            Assert.True(NaturalStringComparer.Instance.Compare("file", "file2") < 0);

        [Fact]
        public void Null_sorts_without_throwing() =>
            Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);

        [Fact]
        public void A_longer_name_comes_after_its_prefix() =>
            Assert.True(NaturalStringComparer.Instance.Compare("ab", "a") > 0);

        [Fact]
        public void A_digit_sorts_before_a_letter() =>
            Assert.True(NaturalStringComparer.Instance.Compare("1", "a") < 0);

        [Fact]
        public void The_same_number_with_other_leading_zeros_is_equal() =>
            Assert.Equal(0, NaturalStringComparer.Instance.Compare("a007", "a7"));
    }

    public class AtomicFileTests
    {
        [Fact]
        public void Text_is_written_as_UTF8_without_a_byte_order_mark_by_default()
        {
            using var d = new TempDir();
            AtomicFile.WriteAllText(d.File("a.txt"), "é");
            Assert.Equal(new byte[] { 0xC3, 0xA9 }, File.ReadAllBytes(d.File("a.txt")));
        }

        [Fact]
        public void A_given_encoding_is_used()
        {
            using var d = new TempDir();
            AtomicFile.WriteAllText(d.File("a.txt"), "é", new System.Text.UTF8Encoding(true));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0xC3, 0xA9 }, File.ReadAllBytes(d.File("a.txt")));
        }

        [Fact]
        public void Bytes_are_written_whole_over_an_older_file()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a.bin"), "old and longer");
            AtomicFile.WriteAllBytes(d.File("a.bin"), new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(d.File("a.bin")));
            Assert.Single(Directory.GetFiles(d.Path));
        }
    }

    public class SafeFileOpsTests
    {
        [Fact]
        public void UniquePath_returns_a_free_path_unchanged()
        {
            using var d = new TempDir();
            Assert.Equal(d.File("a.txt"), SafeFileOps.UniquePath(d.File("a.txt")));
        }

        [Fact]
        public void UniquePath_numbers_a_taken_name_before_the_extension()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a.txt"), "x");
            Assert.Equal(d.File("a (2).txt"), SafeFileOps.UniquePath(d.File("a.txt")));
            File.WriteAllText(d.File("a (2).txt"), "x");
            Assert.Equal(d.File("a (3).txt"), SafeFileOps.UniquePath(d.File("a.txt")));
        }

        [Fact]
        public void UniquePath_also_avoids_a_folder_with_that_name()
        {
            using var d = new TempDir();
            Directory.CreateDirectory(d.File("take.wav"));
            Assert.Equal(d.File("take (2).wav"), SafeFileOps.UniquePath(d.File("take.wav")));
        }

        [Fact]
        public void TryDelete_removes_a_file_and_ignores_a_missing_one()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a.tmp"), "x");
            SafeFileOps.TryDelete(d.File("a.tmp"));
            SafeFileOps.TryDelete(d.File("never-there.tmp"));
            Assert.False(File.Exists(d.File("a.tmp")));
        }

        [Fact]
        public void TryDelete_leaves_a_locked_file_alone_without_throwing()
        {
            using var d = new TempDir();
            using var held = new FileStream(d.File("busy.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            SafeFileOps.TryDelete(d.File("busy.tmp"));
            Assert.True(File.Exists(d.File("busy.tmp")));
        }

        [Fact]
        public void HasRecycleBin_is_true_on_a_fixed_drive_and_false_for_a_network_path()
        {
            using var d = new TempDir();
            Assert.True(SafeFileOps.HasRecycleBin(d.File("a.txt")));
            Assert.False(SafeFileOps.HasRecycleBin(@"\\server\share\a.txt"));
        }
    }

    public class JsonSettingsTests
    {
        private sealed class Sample { public string Name { get; set; } = ""; public int Count { get; set; } }

        [Fact]
        public void SaveFile_writes_loadable_json_and_creates_the_folder()
        {
            using var d = new TempDir();
            string file = Path.Combine(d.Path, "deep", "more", "s.json");
            bool saved = JsonSettings.SaveFile(file, new Sample { Name = "a", Count = 3 }, new JsonSerializerOptions());
            var back = JsonSettings.Load<Sample>("unused", file);
            Assert.True(saved);
            Assert.Equal(("a", 3), (back.Name, back.Count));
        }

        [Fact]
        public void SaveFile_replaces_the_old_file_and_leaves_no_temp_file_behind()
        {
            using var d = new TempDir();
            JsonSettings.SaveFile(d.File("s.json"), new Sample { Count = 1 }, new JsonSerializerOptions());
            JsonSettings.SaveFile(d.File("s.json"), new Sample { Count = 2 }, new JsonSerializerOptions());
            Assert.Equal(2, JsonSettings.Load<Sample>("unused", d.File("s.json")).Count);
            Assert.Equal(new[] { d.File("s.json") }, Directory.GetFiles(d.Path));
        }

        [Fact]
        public void SaveFile_returns_false_instead_of_throwing_when_the_disk_refuses()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("blocker"), "x");   // a file where a folder is needed
            bool saved = JsonSettings.SaveFile(Path.Combine(d.File("blocker"), "s.json"), new Sample(), new JsonSerializerOptions());
            Assert.False(saved);
        }

        [Fact]
        public void Saved_json_is_indented_so_a_person_can_read_it()
        {
            using var d = new TempDir();
            Assert.True(JsonSettings.TrySave("unused", new Sample { Name = "a" }, d.File("s.json")));
            Assert.Contains("\n", File.ReadAllText(d.File("s.json")));
        }

        [Fact]
        public void Save_and_Load_round_trip_through_a_given_path()
        {
            using var d = new TempDir();
            JsonSettings.Save("unused", new Sample { Name = "x", Count = 9 }, d.File("s.json"));
            var back = JsonSettings.Load<Sample>("unused", d.File("s.json"));
            Assert.Equal(("x", 9), (back.Name, back.Count));
        }

        [Fact]
        public void TrySave_reports_false_when_the_disk_refuses()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("blocker"), "x");
            Assert.False(JsonSettings.TrySave("unused", new Sample(), Path.Combine(d.File("blocker"), "s.json")));
        }

        [Fact]
        public void A_file_that_cannot_be_parsed_gives_defaults_and_keeps_a_bad_copy()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("s.json"), "{ not json");
            var back = JsonSettings.Load<Sample>("unused", d.File("s.json"));
            Assert.Equal(0, back.Count);
            Assert.Equal("{ not json", File.ReadAllText(d.File("s.json.bad")));
        }

        [Fact]
        public void A_locked_file_gives_defaults_and_no_bad_copy()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("s.json"), "{\"Count\":5}");
            using var held = new FileStream(d.File("s.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(0, JsonSettings.Load<Sample>("unused", d.File("s.json")).Count);
            Assert.False(File.Exists(d.File("s.json.bad")));
        }

        [Fact]
        public void PathFor_puts_the_file_in_the_data_folder_with_a_json_extension() =>
            Assert.Equal(Path.Combine(AppPaths.DataRoot, "tool.json"), JsonSettings.PathFor("tool"));

        [Fact]
        public void Load_gives_defaults_for_a_missing_file()
        {
            using var d = new TempDir();
            Assert.Equal(0, JsonSettings.Load<Sample>("unused", d.File("none.json")).Count);
        }
    }
}
