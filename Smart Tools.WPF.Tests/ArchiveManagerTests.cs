using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>Split archives (which parts, which is missing), the 7-Zip command lines, and reading 7-Zip's listing.</summary>
    public class ArchiveManagerTests
    {
        // ---------------------------------------------------------------- split sets

        [Fact]
        public void A_plain_archive_is_not_split()
        {
            var set = ArchiveSplit.Find("photos.zip", new[] { "photos.zip", "notes.txt" });
            Assert.Equal(SplitKind.None, set.Kind);
            Assert.False(set.IsSplit);
            Assert.True(set.IsComplete);
            Assert.Equal("photos.zip", set.Start);
        }

        [Theory]
        [InlineData("backup.7z.002")]   // any part leads to the first one
        [InlineData("backup.7z.001")]
        [InlineData("backup.7z.003")]
        public void Numbered_parts_start_at_the_first_whichever_one_is_picked(string picked)
        {
            var set = ArchiveSplit.Find(picked, new[] { "backup.7z.001", "backup.7z.002", "backup.7z.003", "other.7z.001" });
            Assert.Equal(SplitKind.Numbered, set.Kind);
            Assert.Equal("backup.7z.001", set.Start);
            Assert.Equal(3, set.Parts.Count);   // the other archive's part is not mixed in
            Assert.True(set.IsComplete);
        }

        [Fact]
        public void A_missing_middle_part_is_named()
        {
            var set = ArchiveSplit.Find("data.zip.001", new[] { "data.zip.001", "data.zip.003", "data.zip.004" });
            Assert.False(set.IsComplete);
            Assert.Equal(new[] { "data.zip.002" }, set.Missing);
        }

        [Fact]
        public void A_missing_first_part_is_named_and_the_names_match_case_loosely()
        {
            var set = ArchiveSplit.Find("DATA.7Z.002", new[] { "DATA.7Z.002", "data.7z.003" });
            Assert.Equal(new[] { "DATA.7Z.001" }, set.Missing.Select(m => m.ToUpperInvariant()));
            Assert.Equal(2, set.Parts.Count);
        }

        [Fact]
        public void Zip_segments_need_the_final_zip_file()
        {
            var ok = ArchiveSplit.Find("movie.z02", new[] { "movie.z01", "movie.z02", "movie.zip" });
            Assert.Equal(SplitKind.ZipSegments, ok.Kind);
            Assert.Equal("movie.zip", ok.Start);   // 7-Zip opens the .zip and finds the .zNN parts beside it
            Assert.True(ok.IsComplete);

            var noLast = ArchiveSplit.Find("movie.z01", new[] { "movie.z01", "movie.z02" });
            Assert.Equal(new[] { "movie.zip" }, noLast.Missing);

            var gap = ArchiveSplit.Find("movie.zip", new[] { "movie.z01", "movie.z03", "movie.zip" });
            Assert.Equal(SplitKind.ZipSegments, gap.Kind);
            Assert.Equal(new[] { "movie.z02" }, gap.Missing);
        }

        [Fact]
        public void A_zip_with_no_segments_beside_it_is_a_normal_file()
        {
            Assert.Equal(SplitKind.None, ArchiveSplit.Find("movie.zip", new[] { "movie.zip", "movie.mp4" }).Kind);
        }

        [Theory]
        [InlineData("set.part2.rar", "set.part1.rar")]
        [InlineData("set.part02.rar", "set.part01.rar")]
        public void Rar_parts_start_at_part_one_keeping_the_number_width(string picked, string start)
        {
            string two = picked.Contains("part02") ? "set.part02.rar" : "set.part2.rar";
            string one = start;
            var set = ArchiveSplit.Find(picked, new[] { one, two });
            Assert.Equal(SplitKind.RarParts, set.Kind);
            Assert.Equal(start, set.Start);
            Assert.True(set.IsComplete);
        }

        [Fact]
        public void Old_style_rar_sets_start_at_the_rar_and_report_gaps()
        {
            var set = ArchiveSplit.Find("old.r01", new[] { "old.rar", "old.r00", "old.r01", "old.r03" });
            Assert.Equal(SplitKind.RarOld, set.Kind);
            Assert.Equal("old.rar", set.Start);
            Assert.Equal(new[] { "old.r02" }, set.Missing);
        }

        [Theory]
        [InlineData("backup.7z.001", "backup")]
        [InlineData("photos.tar.gz", "photos")]
        [InlineData("movie.z01", "movie")]
        [InlineData("set.part01.rar", "set")]
        [InlineData("a.b.zip", "a.b")]
        [InlineData("plain", "plain")]
        [InlineData(".zip", ".zip")]
        public void The_folder_name_for_extracting_drops_the_archive_endings(string file, string stem) =>
            Assert.Equal(stem, ArchiveSplit.Stem(file));

        // ---------------------------------------------------------------- command lines

        [Fact]
        public void Extract_never_replaces_files_and_keeps_awkward_names_in_one_argument()
        {
            var args = SevenZip.Extract(@"C:\In put\a b.7z.001", @"C:\Out put\a b", "p w'1", new[] { @"src\x y.txt" });
            Assert.Equal("x", args[0]);
            Assert.Contains("-aou", args);                       // an existing file is kept, the extracted one is renamed
            Assert.Contains(@"-oC:\Out put\a b", args);          // one argument, spaces and all
            Assert.Contains("-p" + "p w'1", args);
            Assert.Equal(@"src\x y.txt", args[^1]);
            Assert.Equal(@"C:\In put\a b.7z.001", args[^2]);
        }

        [Fact]
        public void A_password_with_a_double_quote_is_refused_because_7zip_cannot_take_it()
        {
            Assert.False(SevenZip.CanUsePassword("ab\"c"));
            Assert.True(SevenZip.CanUsePassword("p w'1 ñ€"));
            Assert.True(SevenZip.CanUsePassword(""));
            Assert.True(SevenZip.CanUsePassword(null));
        }

        [Fact]
        public void No_password_means_no_password_switch_so_7zip_cannot_wait_for_one_at_the_keyboard()
        {
            Assert.DoesNotContain(SevenZip.List("a.7z", null), a => a.StartsWith("-p"));
            Assert.DoesNotContain(SevenZip.Extract("a.7z", "out", "", null), a => a.StartsWith("-p"));
        }

        [Fact]
        public void Create_adds_the_split_size_and_strong_encryption_only_where_it_applies()
        {
            var sevenZ = SevenZip.Create(@"C:\o\a.7z", new[] { @"C:\src" }, ArchiveFormat.SevenZip, ArchiveLevel.Maximum, 700L * 1024 * 1024, "pw", hideNames: true);
            Assert.Contains("-t7z", sevenZ);
            Assert.Contains("-mx=9", sevenZ);
            Assert.Contains("-v" + 700L * 1024 * 1024 + "b", sevenZ);
            Assert.Contains("-mhe=on", sevenZ);

            var zip = SevenZip.Create(@"C:\o\a.zip", new[] { @"C:\src" }, ArchiveFormat.Zip, ArchiveLevel.Normal, 0, "pw", hideNames: true);
            Assert.Contains("-mem=AES256", zip);
            Assert.DoesNotContain("-mhe=on", zip);            // zip cannot hide names
            Assert.DoesNotContain(zip, a => a.StartsWith("-v"));

            var tar = SevenZip.Create(@"C:\o\a.tar", new[] { @"C:\src" }, ArchiveFormat.Tar, ArchiveLevel.Store, 0, "pw", hideNames: false);
            Assert.DoesNotContain(tar, a => a.StartsWith("-p"));   // tar has no encryption
        }

        [Theory]
        [InlineData("700m", 700L * 1024 * 1024)]
        [InlineData("700 MB", 700L * 1024 * 1024)]
        [InlineData("4g", 4L * 1024 * 1024 * 1024)]
        [InlineData("1,5 g", 1610612736L)]
        [InlineData("1.5G", 1610612736L)]
        [InlineData("650000k", 650000L * 1024)]
        [InlineData("1048576", 1048576L)]
        public void Split_sizes_are_read_like_a_person_types_them(string text, long expected)
        {
            Assert.True(SevenZip.TryParseSize(text, out long bytes));
            Assert.Equal(expected, bytes);
        }

        [Theory]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("-5m")]
        [InlineData("big")]
        [InlineData("10 x")]
        public void Nonsense_sizes_are_refused(string text) => Assert.False(SevenZip.TryParseSize(text, out _));

        // ---------------------------------------------------------------- reading 7-Zip's text

        private const string SplitListing = @"
7-Zip 26.02 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-06-25

Scanning the drive for archives:
1 file, 1048576 bytes (1024 KiB)

Listing archive: out.7z.001

--
Path = out.7z.001
Type = Split
Physical Size = 1048576
Volumes = 3
Total Physical Size = 3000465
----
Path = out.7z
Size = 3000465
--
Path = out.7z
Type = 7z
Physical Size = 3000465
Headers Size = 257
Method = LZMA2:3m 7zAES
Solid = +
Blocks = 1

----------
Path = src
Size = 0
Packed Size = 0
Modified = 2026-11-25 13:26:55.3045465
Attributes = D
CRC =
Encrypted = -
Method =
Block =

Path = src\a.txt
Size = 6
Packed Size = 3000208
Modified = 2026-11-25 13:26:55.3045465
Attributes = A
CRC = 363A3020
Encrypted = +
Method = LZMA2:3m 7zAES:19
Block = 0

Path = src\big.bin
Size = 3000000
Packed Size =
Modified = 2026-11-25 13:26:55.3524988
Attributes = A
CRC = 53F67B43
Encrypted = +
Method = LZMA2:3m 7zAES:19
Block = 0
";

        [Fact]
        public void A_listing_of_a_split_archive_is_read()
        {
            // 7-Zip prints CRLF on Windows
            var listing = SevenZip.ParseListing(SplitListing.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Assert.NotNull(listing);
            Assert.True(listing!.Info.IsSplit);
            Assert.Equal("7z", listing.Info.Type);
            Assert.Equal(3, listing.Info.Volumes);
            Assert.Equal(3000465, listing.Info.PhysicalSize);
            Assert.True(listing.Info.Solid);
            Assert.Equal(1, listing.Folders);
            Assert.Equal(2, listing.Files);
            Assert.Equal(3000006, listing.TotalSize);
            Assert.True(listing.AnyEncrypted);

            var folder = listing.Entries[0];
            Assert.True(folder.IsFolder);
            Assert.Equal("src", folder.Name);
            var file = listing.Entries[1];
            Assert.Equal(@"src\a.txt", file.Path);
            Assert.Equal("a.txt", file.Name);
            Assert.Equal(6, file.Size);
            Assert.Equal(3000208, file.Packed);
            Assert.Null(listing.Entries[2].Packed);   // 7-Zip leaves it empty inside a solid block
            Assert.Equal(new DateTime(2026, 11, 25, 13, 26, 55), listing.Entries[1].Modified!.Value.AddTicks(-(listing.Entries[1].Modified!.Value.Ticks % TimeSpan.TicksPerSecond)));
        }

        [Fact]
        public void Text_that_is_not_a_listing_gives_nothing()
        {
            Assert.Null(SevenZip.ParseListing(""));
            Assert.Null(SevenZip.ParseListing("Listing archive: x.7z\n\nErrors: 1\n\nERROR: x.7z : Cannot open encrypted archive. Wrong password?\n"));
        }

        [Fact]
        public void Percent_password_and_error_lines_are_recognised()
        {
            Assert.Equal(45, SevenZip.ParsePercent("  12% 1 + a.txt\r 45% 3 + big.bin"));
            Assert.Null(SevenZip.ParsePercent("Everything is Ok"));
            Assert.Null(SevenZip.ParsePercent("250% bigger"));   // not a progress figure
            Assert.True(SevenZip.NeedsPassword("ERROR: x.7z : Cannot open encrypted archive. Wrong password?"));
            Assert.True(SevenZip.NeedsPassword("Enter password (will not be echoed):\nBreak signaled"));
            Assert.False(SevenZip.NeedsPassword("Everything is Ok"));
            Assert.Equal("Data Error in encrypted file. Wrong password? : a.txt", SevenZip.FirstError("Archives with Errors: 1\r\nERROR: Data Error in encrypted file. Wrong password? : a.txt\r\n"));
            Assert.Equal("", SevenZip.FirstError("Everything is Ok"));
        }
    }
}
