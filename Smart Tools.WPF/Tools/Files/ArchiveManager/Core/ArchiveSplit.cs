using System.IO;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Models
{
    /// <summary>How a big archive was cut into parts.</summary>
    public enum SplitKind
    {
        /// <summary>One file.</summary>
        None,
        /// <summary>name.7z.001, name.zip.002, name.001 ... (7-Zip, WinRAR "split", HJSplit).</summary>
        Numbered,
        /// <summary>name.z01, name.z02 ... and the last part name.zip (WinZip, Info-ZIP).</summary>
        ZipSegments,
        /// <summary>name.part1.rar, name.part2.rar ... (WinRAR, new style).</summary>
        RarParts,
        /// <summary>name.rar, name.r00, name.r01 ... (WinRAR, old style).</summary>
        RarOld,
    }

    /// <summary>
    /// The parts of one split archive found in a folder. <see cref="Start"/> is the file to hand to 7-Zip (it finds the other parts by
    /// itself, so every part has to sit in the same folder under its original name). <see cref="Missing"/> lists parts that are
    /// not there, so the tool can say which one before it starts, instead of failing half way.
    /// </summary>
    public sealed record SplitSet(SplitKind Kind, string Start, IReadOnlyList<string> Parts, IReadOnlyList<string> Missing)
    {
        public bool IsSplit => Kind != SplitKind.None;

        public bool IsComplete => Missing.Count == 0;
    }

    /// <summary>Finds the parts of a split archive from any one of them. Pure: it is given the file names of the folder.</summary>
    public static class ArchiveSplit
    {
        private static readonly Regex Numbered = new(@"^(?<base>.+)\.(?<n>\d{3,})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ZipSegment = new(@"^(?<base>.+)\.z(?<n>\d{2,})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RarPart = new(@"^(?<base>.+)\.part(?<n>\d+)\.rar$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RarOldPart = new(@"^(?<base>.+)\.r(?<n>\d{2,})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// <paramref name="picked"/> is the file name the user chose (any part). <paramref name="folderFiles"/> are the file names in
        /// the same folder (names only, no paths). A file that is not part of a set comes back as <see cref="SplitKind.None"/>.
        /// </summary>
        public static SplitSet Find(string picked, IEnumerable<string> folderFiles)
        {
            var names = new HashSet<string>(folderFiles, StringComparer.OrdinalIgnoreCase);
            names.Add(picked);

            if (RarPart.Match(picked) is { Success: true } rp)
            {
                string stem = rp.Groups["base"].Value;
                int width = rp.Groups["n"].Value.Length;
                var found = Numbers(names, new Regex($@"^{Regex.Escape(stem)}\.part(?<n>\d{{{width}}})\.rar$", RegexOptions.IgnoreCase));
                return Build(SplitKind.RarParts, found, n => $"{stem}.part{n.ToString().PadLeft(width, '0')}.rar", first: 1);
            }

            if (ZipSegment.Match(picked) is { Success: true } zs)
                return ZipSet(zs.Groups["base"].Value, names);

            if (picked.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(picked) is { Length: > 0 } zipStem
                && names.Contains(zipStem + ".z01"))
                return ZipSet(zipStem, names);

            if (RarOldPart.Match(picked) is { Success: true } ro)
                return OldRarSet(ro.Groups["base"].Value, names);

            if (picked.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(picked) is { Length: > 0 } rarStem
                && names.Contains(rarStem + ".r00"))
                return OldRarSet(rarStem, names);

            if (Numbered.Match(picked) is { Success: true } nm)
            {
                string stem = nm.Groups["base"].Value;
                int width = nm.Groups["n"].Value.Length;
                var found = Numbers(names, new Regex($@"^{Regex.Escape(stem)}\.(?<n>\d{{{width}}})$", RegexOptions.IgnoreCase));
                return Build(SplitKind.Numbered, found, n => $"{stem}.{n.ToString().PadLeft(width, '0')}", first: 1);
            }

            return new SplitSet(SplitKind.None, picked, new[] { picked }, Array.Empty<string>());
        }

        private static readonly Regex PartSuffix = new(@"(\.\d{3,}|\.part\d+\.rar|\.z\d{2,}|\.r\d{2,})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex KindSuffix = new(@"\.(7z|zip|rar|tar|gz|tgz|bz2|xz|zst|cab|iso|wim)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>"backup.7z.001" -> "backup", "photos.tar.gz" -> "photos": the name for the folder the files are extracted into.</summary>
        public static string Stem(string fileName)
        {
            string s = PartSuffix.Replace(fileName, "");
            for (int i = 0; i < 2; i++) s = KindSuffix.Replace(s, "");   // ".tar.gz" has two
            return s.Length == 0 ? Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } plain ? plain : fileName : s;
        }

        private static SplitSet ZipSet(string stem, HashSet<string> names)
        {
            var found = Numbers(names, new Regex($@"^{Regex.Escape(stem)}\.z(?<n>\d{{2,}})$", RegexOptions.IgnoreCase));
            string last = stem + ".zip";
            var parts = found.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            var missing = new List<string>();
            int highest = found.Count == 0 ? 0 : found.Keys.Max();
            int width = found.Count == 0 ? 2 : Math.Max(2, found.Values.First().Length - stem.Length - 2);
            for (int n = 1; n <= highest; n++)
                if (!found.ContainsKey(n)) missing.Add($"{stem}.z{n.ToString().PadLeft(width, '0')}");
            if (names.Contains(last)) parts.Add(names.First(x => x.Equals(last, StringComparison.OrdinalIgnoreCase)));
            else missing.Add(last);   // the last part carries the index: without it nothing can be opened
            return new SplitSet(SplitKind.ZipSegments, names.FirstOrDefault(x => x.Equals(last, StringComparison.OrdinalIgnoreCase)) ?? last, parts, missing);
        }

        private static SplitSet OldRarSet(string stem, HashSet<string> names)
        {
            var found = Numbers(names, new Regex($@"^{Regex.Escape(stem)}\.r(?<n>\d{{2,}})$", RegexOptions.IgnoreCase));
            string first = stem + ".rar";
            var parts = new List<string>();
            var missing = new List<string>();
            if (names.FirstOrDefault(x => x.Equals(first, StringComparison.OrdinalIgnoreCase)) is { } rar) parts.Add(rar);
            else missing.Add(first);
            int highest = found.Count == 0 ? -1 : found.Keys.Max();
            int width = found.Count == 0 ? 2 : Math.Max(2, found.Values.First().Length - stem.Length - 2);
            for (int n = 0; n <= highest; n++)
            {
                if (found.TryGetValue(n, out var name)) parts.Add(name);
                else missing.Add($"{stem}.r{n.ToString().PadLeft(width, '0')}");
            }
            return new SplitSet(SplitKind.RarOld, names.FirstOrDefault(x => x.Equals(first, StringComparison.OrdinalIgnoreCase)) ?? first, parts, missing);
        }

        private static SplitSet Build(SplitKind kind, SortedDictionary<int, string> found, Func<int, string> nameOf, int first)
        {
            var parts = found.Values.ToList();
            var missing = new List<string>();
            int highest = found.Count == 0 ? first : found.Keys.Max();
            for (int n = first; n <= highest; n++)
                if (!found.ContainsKey(n)) missing.Add(nameOf(n));
            string start = found.TryGetValue(first, out var firstName) ? firstName : nameOf(first);
            return new SplitSet(kind, start, parts, missing);
        }

        private static SortedDictionary<int, string> Numbers(IEnumerable<string> names, Regex pattern)
        {
            var found = new SortedDictionary<int, string>();
            foreach (var name in names)
                if (pattern.Match(name) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out int n)) found[n] = name;
            return found;
        }
    }
}
