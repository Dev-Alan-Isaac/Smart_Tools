using System.Globalization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Models
{
    public enum ArchiveFormat { SevenZip, Zip, Tar }

    /// <summary>How hard 7-Zip tries to make the archive small (the -mx switch).</summary>
    public enum ArchiveLevel { Store = 0, Fast = 1, Normal = 5, Maximum = 9 }

    /// <summary>One file or folder inside an archive, as 7-Zip lists it.</summary>
    public sealed record ArchiveEntry(string Path, bool IsFolder, long Size, long? Packed, DateTime? Modified, bool Encrypted, string Method)
    {
        public string Name => Path.Replace('\\', '/').TrimEnd('/') is { } p ? p[(p.LastIndexOf('/') + 1)..] : Path;
    }

    /// <summary>What 7-Zip says about the archive as a whole.</summary>
    public sealed record ArchiveInfo(string Type, bool IsSplit, int Volumes, long PhysicalSize, string Method, bool Solid, bool HeadersEncrypted);

    public sealed record ArchiveListing(ArchiveInfo Info, IReadOnlyList<ArchiveEntry> Entries)
    {
        public int Files => Entries.Count(e => !e.IsFolder);

        public int Folders => Entries.Count(e => e.IsFolder);

        public long TotalSize => Entries.Where(e => !e.IsFolder).Sum(e => e.Size);

        public bool AnyEncrypted => Entries.Any(e => e.Encrypted);
    }
}

namespace Smart_Tools.WPF.Controllers
{
    using Smart_Tools.WPF.Models;

    /// <summary>
    /// The command lines for the bundled 7-Zip (Engines\7ZIP\7z.exe) and the reading of what it prints. Pure text work, no process, so it is
    /// tested without 7-Zip. Arguments are returned as a list (never one string), so a path or password with spaces or quotes is safe.
    /// </summary>
    public static class SevenZip
    {
        // -y: never stop to ask. -aou: a file that already exists is NOT replaced, the extracted one gets a new name.
        // -sccUTF-8: names come back in UTF-8 whatever the console code page is. -bsp1 -bso1: progress and messages on stdout.
        private static readonly string[] Quiet = { "-y", "-sccUTF-8" };

        /// <summary>
        /// 7-Zip reads its own command line and has no way to write a double quote inside a password, so such a password cannot be
        /// passed safely. The tool refuses it (with a message) instead of failing in a confusing way. Spaces and other symbols are fine.
        /// </summary>
        public static bool CanUsePassword(string? password) => string.IsNullOrEmpty(password) || !password.Contains('"');

        private static void Password(List<string> args, string? password)
        {
            if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
        }

        public static IReadOnlyList<string> List(string archive, string? password)
        {
            var args = new List<string> { "l", "-slt" };
            args.AddRange(Quiet);
            Password(args, password);
            args.Add(archive);
            return args;
        }

        public static IReadOnlyList<string> Test(string archive, string? password)
        {
            var args = new List<string> { "t", "-bsp1", "-bso1" };
            args.AddRange(Quiet);
            Password(args, password);
            args.Add(archive);
            return args;
        }

        /// <summary>Extract everything, or just <paramref name="only"/> (paths as listed) into <paramref name="folder"/>, keeping folders.</summary>
        public static IReadOnlyList<string> Extract(string archive, string folder, string? password, IReadOnlyList<string>? only = null)
        {
            var args = new List<string> { "x", "-aou", "-bsp1", "-bso1" };
            args.AddRange(Quiet);
            args.Add("-o" + folder);
            Password(args, password);
            args.Add(archive);
            if (only != null) args.AddRange(only);
            return args;
        }

        public static IReadOnlyList<string> Create(string target, IReadOnlyList<string> inputs, ArchiveFormat format, ArchiveLevel level,
            long splitBytes, string? password, bool hideNames)
        {
            var args = new List<string> { "a", "-t" + TypeSwitch(format), "-mx=" + (int)level, "-bsp1", "-bso1" };
            args.AddRange(Quiet);
            if (splitBytes > 0) args.Add($"-v{splitBytes}b");
            if (!string.IsNullOrEmpty(password) && format != ArchiveFormat.Tar)
            {
                args.Add("-p" + password);
                if (format == ArchiveFormat.SevenZip && hideNames) args.Add("-mhe=on");
                if (format == ArchiveFormat.Zip) args.Add("-mem=AES256");   // the old zip password scheme is easy to break
            }
            args.Add(target);
            args.AddRange(inputs);
            return args;
        }

        public static string TypeSwitch(ArchiveFormat format) => format switch
        {
            ArchiveFormat.Zip => "zip",
            ArchiveFormat.Tar => "tar",
            _ => "7z",
        };

        public static string Extension(ArchiveFormat format) => format switch
        {
            ArchiveFormat.Zip => ".zip",
            ArchiveFormat.Tar => ".tar",
            _ => ".7z",
        };

        // =================================================================
        //  Sizes: "700m", "4 GB", "1.5g", "650000k", "1048576"
        // =================================================================

        private static readonly Regex SizeText = new(@"^\s*(?<n>[0-9]+(?:[.,][0-9]+)?)\s*(?<u>[kmgt]?)b?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Reads a size typed by a person. Binary units (1 MB = 1024 * 1024 bytes), like 7-Zip. False for nothing, zero or nonsense.</summary>
        public static bool TryParseSize(string? text, out long bytes)
        {
            bytes = 0;
            var m = SizeText.Match(text ?? "");
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0) return false;
            double unit = m.Groups["u"].Value.ToLowerInvariant() switch
            {
                "k" => 1024d,
                "m" => 1024d * 1024,
                "g" => 1024d * 1024 * 1024,
                "t" => 1024d * 1024 * 1024 * 1024,
                _ => 1d,
            };
            double total = n * unit;
            if (total < 1 || total > long.MaxValue / 2) return false;
            bytes = (long)total;
            return true;
        }

        // =================================================================
        //  Reading "7z l -slt"
        // =================================================================

        /// <summary>Parses the technical listing. Returns null when the text holds no archive (an error, a wrong password).</summary>
        public static ArchiveListing? ParseListing(string output)
        {
            var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var types = new List<string>();
            var entries = new List<ArchiveEntry>();
            var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool inEntries = false, sawDivider = false;

            void FlushBlock()
            {
                if (block.Count > 0 && block.ContainsKey("Path") && inEntries) entries.Add(ToEntry(block));
                block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("----------", StringComparison.Ordinal))
                {
                    sawDivider = true;
                    inEntries = true;
                    block.Clear();
                    continue;
                }
                if (line.Length == 0)
                {
                    FlushBlock();
                    continue;
                }
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0 && !line.EndsWith(" =", StringComparison.Ordinal)) continue;   // banners, "--", "----", counters
                string key = eq < 0 ? line[..^2] : line[..eq];
                string value = eq < 0 ? "" : line[(eq + 3)..];
                if (inEntries) block[key] = value;
                else
                {
                    header[key] = value;
                    if (key.Equals("Type", StringComparison.OrdinalIgnoreCase)) types.Add(value);
                }
            }
            FlushBlock();

            if (!sawDivider || types.Count == 0) return null;
            bool split = types.Any(t => t.Equals("Split", StringComparison.OrdinalIgnoreCase));
            string type = types.LastOrDefault(t => !t.Equals("Split", StringComparison.OrdinalIgnoreCase)) ?? types[^1];
            string method = header.GetValueOrDefault("Method", "");
            var info = new ArchiveInfo(
                type, split, (int)Num(header, "Volumes"),
                Num(header, split ? "Total Physical Size" : "Physical Size"),
                method, header.GetValueOrDefault("Solid", "-") == "+",
                HeadersEncrypted: false);   // a listing only works with the password once the names are encrypted; the caller knows
            return new ArchiveListing(info, entries);
        }

        private static ArchiveEntry ToEntry(Dictionary<string, string> b)
        {
            string attrs = b.GetValueOrDefault("Attributes", "");
            bool folder = b.GetValueOrDefault("Folder", "") == "+" || attrs.Contains('D');
            DateTime? modified = DateTime.TryParse(b.GetValueOrDefault("Modified", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) ? m : null;
            long? packed = long.TryParse(b.GetValueOrDefault("Packed Size", ""), out long p) ? p : null;
            return new ArchiveEntry(b["Path"], folder, (long)Num(b, "Size"), packed, modified, b.GetValueOrDefault("Encrypted", "") == "+", b.GetValueOrDefault("Method", ""));
        }

        private static long Num(Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;

        // =================================================================
        //  What the run says
        // =================================================================

        private static readonly Regex Percent = new(@"(?<!\d)(?<p>\d{1,3})%", RegexOptions.Compiled);

        /// <summary>The last percentage in a chunk of 7-Zip's progress text ("  45% 3 + folder\file"), or null.</summary>
        public static int? ParsePercent(string text)
        {
            int? last = null;
            foreach (Match m in Percent.Matches(text))
                if (int.TryParse(m.Groups["p"].Value, out int p) && p is >= 0 and <= 100) last = p;
            return last;
        }

        /// <summary>Does 7-Zip's text say the archive needs a password, or that the one given is wrong?</summary>
        public static bool NeedsPassword(string output) =>
            output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Enter password", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Cannot open encrypted archive", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Break signaled", StringComparison.OrdinalIgnoreCase);

        /// <summary>The first line of 7-Zip's text that is an error, for showing to the user ("ERROR: ..."), or "".</summary>
        public static string FirstError(string output)
        {
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) return line["ERROR:".Length..].Trim();
                if (line.StartsWith("Can't open as archive", StringComparison.OrdinalIgnoreCase)) return line;
            }
            return "";
        }
    }
}
