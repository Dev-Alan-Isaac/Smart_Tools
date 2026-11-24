using System.IO;
using System.Security.Cryptography;
using System.Text;
using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Tests
{
    public class FilesAreIdenticalTests
    {
        private static string Make(TempDir d, string name, byte[] bytes)
        {
            File.WriteAllBytes(d.File(name), bytes);
            return d.File(name);
        }

        [Fact]
        public void Same_bytes_are_identical_even_under_different_names()
        {
            using var d = new TempDir();
            var data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
            Assert.True(SafeFileOps.FilesAreIdentical(Make(d, "a", data), Make(d, "b", data)));
        }

        [Fact]
        public void Same_size_with_one_different_byte_is_not_identical()
        {
            using var d = new TempDir();
            var a = new byte[100]; var b = new byte[100]; b[99] = 1;
            Assert.False(SafeFileOps.FilesAreIdentical(Make(d, "a", a), Make(d, "b", b)));
        }

        [Fact]
        public void Different_sizes_are_not_identical()
        {
            using var d = new TempDir();
            Assert.False(SafeFileOps.FilesAreIdentical(Make(d, "a", new byte[3]), Make(d, "b", new byte[4])));
        }

        [Fact]
        public void A_missing_file_means_not_identical()
        {
            using var d = new TempDir();
            Assert.False(SafeFileOps.FilesAreIdentical(Make(d, "a", new byte[3]), d.File("none")));
            Assert.False(SafeFileOps.FilesAreIdentical(d.File("none"), Make(d, "b", new byte[3])));
        }

        [Fact]
        public void Chunked_check_reports_every_byte_and_asks_before_each_chunk()
        {
            using var d = new TempDir();
            var data = new byte[2_500_000];   // spans three 1 MB chunks
            long total = 0; int asked = 0;
            bool same = SafeFileOps.FilesAreIdentical(Make(d, "a", data), Make(d, "b", data), n => total += n, () => asked++, CancellationToken.None);
            Assert.True(same);
            Assert.Equal(data.Length, total);
            Assert.True(asked >= 3);
        }

        [Fact]
        public void Chunked_check_stops_at_the_first_difference()
        {
            using var d = new TempDir();
            var a = new byte[3_000_000]; var b = new byte[3_000_000]; b[10] = 7;
            long total = 0;
            Assert.False(SafeFileOps.FilesAreIdentical(Make(d, "a", a), Make(d, "b", b), n => total += n, null, CancellationToken.None));
            Assert.Equal(0, total);
        }

        [Fact]
        public void Chunked_check_throws_when_cancelled_instead_of_answering()
        {
            using var d = new TempDir();
            var data = new byte[10];
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                SafeFileOps.FilesAreIdentical(Make(d, "a", data), Make(d, "b", data), null, null, cts.Token));
        }

        [Fact]
        public void Chunked_check_with_a_missing_file_answers_false_not_an_error()
        {
            using var d = new TempDir();
            Assert.False(SafeFileOps.FilesAreIdentical(Make(d, "a", new byte[3]), d.File("none"), null, null, CancellationToken.None));
        }
    }

    public class HashAndCopyTests
    {
        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        [Fact]
        public void HashFile_gives_the_SHA256_in_hex_and_reports_the_bytes()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("abc.txt"), "abc");
            long seen = 0;
            Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", SafeFileOps.HashFile(d.File("abc.txt"), n => seen += n));
            Assert.Equal(3, seen);
        }

        [Fact]
        public void HashFile_of_an_empty_file_is_the_empty_hash()
        {
            using var d = new TempDir();
            File.WriteAllBytes(d.File("e"), Array.Empty<byte>());
            Assert.Equal(Sha(Array.Empty<byte>()), SafeFileOps.HashFile(d.File("e")));
        }

        [Fact]
        public void HashFile_throws_when_cancelled()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a"), "abc");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => SafeFileOps.HashFile(d.File("a"), token: cts.Token));
        }

        [Fact]
        public void CopyToTemp_copies_the_bytes_and_returns_the_hash_of_what_it_read()
        {
            using var d = new TempDir();
            var data = Enumerable.Range(0, 2_100_000).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(d.File("src"), data);
            long copied = 0;
            string hash = SafeFileOps.CopyToTemp(d.File("src"), d.File("dst.tmp"), n => copied += n);
            Assert.Equal(Sha(data), hash);
            Assert.Equal(data, File.ReadAllBytes(d.File("dst.tmp")));
            Assert.Equal(data.Length, copied);
        }

        [Fact]
        public void CopyToTemp_stops_when_cancelled_so_the_caller_can_remove_the_temp_file()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("src"), "abc");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => SafeFileOps.CopyToTemp(d.File("src"), d.File("dst.tmp"), token: cts.Token));
        }
    }

    public class TimestampAndAttributeTests
    {
        [Fact]
        public void CopyTimestamps_gives_the_copy_the_originals_write_time()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a"), "x");
            File.WriteAllText(d.File("b"), "x");
            var when = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(d.File("a"), when);
            SafeFileOps.CopyTimestamps(d.File("a"), d.File("b"));
            Assert.Equal(when, File.GetLastWriteTimeUtc(d.File("b")));
        }

        [Fact]
        public void CopyTimestamps_works_on_a_read_only_copy_and_leaves_it_read_only()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a"), "x");
            File.WriteAllText(d.File("b"), "x");
            File.SetAttributes(d.File("b"), FileAttributes.ReadOnly);
            var when = new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(d.File("a"), when);
            SafeFileOps.CopyTimestamps(d.File("a"), d.File("b"));
            Assert.Equal(when, File.GetLastWriteTimeUtc(d.File("b")));
            Assert.True(File.GetAttributes(d.File("b")).HasFlag(FileAttributes.ReadOnly));
            File.SetAttributes(d.File("b"), FileAttributes.Normal);
        }

        [Fact]
        public void ClearReadOnly_removes_the_flag_and_ignores_a_missing_file()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a"), "x");
            File.SetAttributes(d.File("a"), FileAttributes.ReadOnly);
            SafeFileOps.ClearReadOnly(d.File("a"));
            SafeFileOps.ClearReadOnly(d.File("none"));
            Assert.False(File.GetAttributes(d.File("a")).HasFlag(FileAttributes.ReadOnly));
        }

        [Fact]
        public void DeleteQuietly_removes_a_file_and_tolerates_null_and_missing()
        {
            using var d = new TempDir();
            File.WriteAllText(d.File("a"), "x");
            SafeFileOps.DeleteQuietly(d.File("a"));
            SafeFileOps.DeleteQuietly(null);
            SafeFileOps.DeleteQuietly(d.File("none"));
            Assert.False(File.Exists(d.File("a")));
        }

        [Fact]
        public void SameVolume_compares_drive_roots_ignoring_case()
        {
            Assert.True(SafeFileOps.SameVolume(@"C:\a\b.txt", @"c:\x\y.txt"));
            Assert.False(SafeFileOps.SameVolume(@"C:\a.txt", @"D:\a.txt"));
        }
    }

    public class RetryIoTests
    {
        [Fact]
        public void A_busy_file_is_retried_until_it_works()
        {
            int calls = 0;
            SafeFileOps.RetryIo(() => { if (++calls < 3) throw new IOException("busy"); });
            Assert.Equal(3, calls);
        }

        [Fact]
        public void A_missing_file_fails_at_once_without_retrying()
        {
            int calls = 0;
            Assert.Throws<FileNotFoundException>(() => SafeFileOps.RetryIo(() => { calls++; throw new FileNotFoundException(); }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void A_missing_folder_fails_at_once_without_retrying()
        {
            int calls = 0;
            Assert.Throws<DirectoryNotFoundException>(() => SafeFileOps.RetryIo(() => { calls++; throw new DirectoryNotFoundException(); }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void An_unrelated_error_is_not_retried()
        {
            int calls = 0;
            Assert.Throws<InvalidOperationException>(() => SafeFileOps.RetryIo(() => { calls++; throw new InvalidOperationException(); }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void A_file_that_stays_busy_gives_up_after_five_attempts()
        {
            int calls = 0;
            Assert.Throws<UnauthorizedAccessException>(() => SafeFileOps.RetryIo(() => { calls++; throw new UnauthorizedAccessException(); }));
            Assert.Equal(5, calls);
        }
    }
}
