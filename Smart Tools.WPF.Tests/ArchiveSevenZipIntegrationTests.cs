using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>
    /// The command lines against the real 7-Zip that ships in Engines\7ZIP: a split, locked archive is made, listed, tested and
    /// extracted, and a second extract over the same folder replaces nothing. Skipped when 7z.exe is not found.
    /// </summary>
    public class ArchiveSevenZipIntegrationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-archive-" + Guid.NewGuid().ToString("N"));
        private readonly string? _exe = FindExe();

        public ArchiveSevenZipIntegrationTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private static string? FindExe()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Smart Tools.WPF", "Engines", "7ZIP", "7z.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private (int Code, string Output) Run(IReadOnlyList<string> args)
        {
            var info = new ProcessStartInfo(_exe!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            p.StandardInput.Close();
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }

        private string MakeSource()
        {
            string src = Path.Combine(_root, "my files");   // a space on purpose
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            var random = new byte[3_000_000];
            new Random(7).NextBytes(random);   // incompressible, so 1 MB parts really split
            File.WriteAllBytes(Path.Combine(src, "sub", "big.bin"), random);
            return src;
        }

        [Fact]
        public void A_split_locked_archive_is_made_read_tested_and_extracted_without_replacing_anything()
        {
            if (_exe == null) return;   // no 7-Zip beside the sources: nothing to run
            string src = MakeSource();
            string target = Path.Combine(_root, "out put", "pack.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var make = Run(SevenZip.Create(target, new[] { src }, ArchiveFormat.SevenZip, ArchiveLevel.Fast, 1024 * 1024, "p w'1", hideNames: true));
            Assert.True(make.Code == 0, make.Output);
            var parts = Directory.GetFiles(Path.GetDirectoryName(target)!, "pack.7z.*").Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.True(parts.Count >= 3, string.Join(",", parts));
            Assert.Equal("pack.7z.001", parts[0]);

            // the parts are found from any one of them
            var set = ArchiveSplit.Find(parts[1]!, parts!);
            Assert.Equal(SplitKind.Numbered, set.Kind);
            Assert.True(set.IsComplete);
            string start = Path.Combine(Path.GetDirectoryName(target)!, set.Start);

            // without the password 7-Zip fails at once (it never waits at a keyboard) and says it is locked
            var locked = Run(SevenZip.List(start, null));
            Assert.True(SevenZip.NeedsPassword(locked.Output), locked.Output);
            Assert.Null(SevenZip.ParseListing(locked.Output));

            var listed = Run(SevenZip.List(start, "p w'1"));
            var listing = SevenZip.ParseListing(listed.Output);
            Assert.NotNull(listing);
            Assert.True(listing!.Info.IsSplit);
            Assert.Equal(parts.Count, listing.Info.Volumes);
            Assert.Equal(2, listing.Files);
            Assert.Contains(listing.Entries, e => e.Name == "big.bin" && e.Size == 3_000_000);

            var okTest = Run(SevenZip.Test(start, "p w'1"));
            Assert.True(okTest.Code == 0, okTest.Output);
            Assert.NotEqual(0, Run(SevenZip.Test(start, "wrong")).Code);

            string dest = Path.Combine(_root, "extracted here");   // a space on purpose
            Directory.CreateDirectory(dest);
            var first = Run(SevenZip.Extract(start, dest, "p w'1"));
            Assert.True(first.Code == 0, first.Output);
            string a = Path.Combine(dest, "my files", "a.txt");
            Assert.Equal("hello", File.ReadAllText(a));
            Assert.Equal(3_000_000, new FileInfo(Path.Combine(dest, "my files", "sub", "big.bin")).Length);

            // extracting again into the same folder keeps the file that is there and renames the new one
            File.WriteAllText(a, "changed by the user");
            Assert.Equal(0, Run(SevenZip.Extract(start, dest, "p w'1")).Code);
            Assert.Equal("changed by the user", File.ReadAllText(a));
            Assert.Contains(Directory.GetFiles(Path.Combine(dest, "my files")), f => Path.GetFileName(f).StartsWith("a_") || Path.GetFileName(f).StartsWith("a "));
        }

        [Fact]
        public void Extracting_only_the_picked_paths_leaves_the_rest()
        {
            if (_exe == null) return;
            string src = MakeSource();
            string zip = Path.Combine(_root, "plain.zip");
            Assert.Equal(0, Run(SevenZip.Create(zip, new[] { src }, ArchiveFormat.Zip, ArchiveLevel.Normal, 0, null, false)).Code);

            var listing = SevenZip.ParseListing(Run(SevenZip.List(zip, null)).Output)!;
            Assert.False(listing.Info.IsSplit);
            Assert.Equal("zip", listing.Info.Type, ignoreCase: true);
            string one = listing.Entries.Single(e => e.Name == "a.txt").Path;

            string dest = Path.Combine(_root, "only");
            Directory.CreateDirectory(dest);
            Assert.Equal(0, Run(SevenZip.Extract(zip, dest, null, new[] { one })).Code);
            Assert.True(File.Exists(Path.Combine(dest, "my files", "a.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "my files", "sub", "big.bin")));
        }
    }
}
