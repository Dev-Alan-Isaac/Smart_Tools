using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>What a scan of files found: one row per color (or per place, when duplicates are kept) and how many files were read.</summary>
    public sealed record ColorScanResult(List<BatchColorRow> Rows, int FilesRead, int FilesSkipped, int Occurrences, bool Truncated);

    /// <summary>
    /// Finds colors written inside text files - style sheets, themes, JSON, XAML/XML, source code, palettes - in the
    /// notations those files use: #RGB / #RRGGBB / #RRGGBBAA, 0xAARRGGBB, rgb()/rgba(), hsl()/hsla(),
    /// Color.FromRgb(...)/Color.argb(...), CSS names after color-like properties, and the one-color-per-line
    /// palette formats (.gpl, .pal, .hex). No WPF types.
    /// </summary>
    public static class ColorFileScanner
    {
        /// <summary>Extensions picked up when a folder is scanned (files chosen by hand may have any extension).</summary>
        public static readonly string[] TextExtensions =
        {
            ".css", ".scss", ".sass", ".less", ".styl", ".html", ".htm", ".svg", ".xml", ".xaml", ".axaml", ".resx",
            ".json", ".js", ".jsx", ".ts", ".tsx", ".vue", ".cs", ".java", ".kt", ".swift", ".dart", ".py", ".ini",
            ".yaml", ".yml", ".toml", ".txt", ".md", ".csv", ".tsv", ".gpl", ".pal", ".hex", ".theme", ".qss", ".plist",
        };

        /// <summary>The Open-file filter for "Scan files".</summary>
        public const string DialogPatterns =
            "*.css;*.scss;*.less;*.html;*.svg;*.xml;*.xaml;*.json;*.js;*.ts;*.cs;*.java;*.kt;*.swift;*.dart;*.ini;*.yaml;*.yml;*.txt;*.md;*.csv;*.gpl;*.pal;*.hex";

        public const long MaxFileBytes = 5 * 1024 * 1024;
        public const int MaxFiles = 2000;
        public const int MaxRows = 50000;

        // Where 8 digits mean AARRGGBB (Android, WPF, .NET) instead of CSS's RRGGBBAA.
        private static readonly HashSet<string> AlphaFirstExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".xml", ".xaml", ".axaml", ".resx", ".cs", ".java", ".kt", ".dart", ".swift" };

        private static readonly Regex HexToken = new(
            @"(?<![\w&#])(?:#|0x)(?<h>[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?!\w)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RgbFn = new(@"\brgba?\(\s*[^()\r\n]{5,60}\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HslFn = new(@"\bhsla?\(\s*[^()\r\n]{5,60}\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const string ByteValue = @"(?:0x[0-9a-fA-F]{1,2}|\d{1,3})";
        private static readonly Regex ColorCall = new(
            $@"\bColor\.(?:From)?(?<k>Rgb|Argb)\(\s*(?<a>{ByteValue})\s*,\s*(?<b>{ByteValue})\s*,\s*(?<c>{ByteValue})(?:\s*,\s*(?<d>{ByteValue}))?\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamedAfterProperty = new(
            @"(?:\b(?:background-color|background|color|fill|stroke|border(?:-(?:top|right|bottom|left))?(?:-color)?|outline(?:-color)?|stop-color|caret-color|accent-color)\s*:|\b(?:fill|stroke|color)\s*=)\s*[""']?\s*(?<n>[a-zA-Z]{3,20})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PaletteTriple = new(@"^\s*(\d{1,3})[ \t]+(\d{1,3})[ \t]+(\d{1,3})\b", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex PaletteHexLine = new(@"^\s*([0-9a-fA-F]{6})\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

        private sealed record Hit(int Offset, string Text, RgbColor Color);

        /// <summary>Reads the files (folders are searched for known text types) and lists the colors found.</summary>
        /// <param name="unique">One row per distinct color (with how many more times it appears) instead of one per place.</param>
        public static ColorScanResult Scan(IEnumerable<string> paths, bool unique, bool subfolders = true, CancellationToken ct = default)
        {
            var files = Expand(paths, subfolders);
            int read = 0, skipped = 0, occurrences = 0;
            bool truncated = files.Count > MaxFiles;
            if (truncated) files = files.Take(MaxFiles).ToList();

            var rows = new List<BatchColorRow>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var places = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                string? text = ReadText(file);
                if (text == null) { skipped++; continue; }
                read++;

                var starts = LineStarts(text);
                string name = Path.GetFileName(file);
                foreach (var hit in FindColors(text, Path.GetExtension(file)))
                {
                    occurrences++;
                    string hex = hit.Color.Hex;
                    string place = $"{name}:{LineOf(starts, hit.Offset)}";

                    if (unique)
                    {
                        if (counts.TryGetValue(hex, out int n)) { counts[hex] = n + 1; continue; }
                        counts[hex] = 1;
                        places[hex] = place;
                    }
                    if (rows.Count >= MaxRows) { truncated = true; continue; }

                    rows.Add(Row(rows.Count + 1, hit, place));
                }
            }

            if (unique)
            {
                // Name where each color was first seen and how many more times it appears.
                for (int i = 0; i < rows.Count; i++)
                {
                    string hex = rows[i].Hex;
                    int more = counts[hex] - 1;
                    rows[i] = Row(i + 1, new Hit(0, rows[i].Input, ColorManager.TryParseColor(hex, out var c) ? c : default),
                        more > 0 ? $"{places[hex]} (+{more})" : places[hex]);
                }
            }

            return new ColorScanResult(rows, read, skipped, occurrences, truncated);
        }

        private static BatchColorRow Row(int index, Hit hit, string source) => new()
        {
            Index = index,
            Input = hit.Text.Length > 80 ? hit.Text[..80] : hit.Text,
            Status = ColorRowStatus.Converted,
            Hex = hit.Color.Hex,
            Rgb = ColorManager.ToRgbTriplet(hit.Color),
            Hsl = ColorManager.ToHslTriplet(hit.Color),
            Message = Loc.T("Str_Color_MsgRowConverted", "Converted"),
            Source = source,
        };

        // ---------------------------------------------------------------- files

        private static List<string> Expand(IEnumerable<string> paths, bool subfolders)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    foreach (string f in SafeIo.EnumerateFiles(path, subfolders, attributesToSkip: 0)
                                 .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (files.Count > MaxFiles) return files;
                        if (seen.Add(f)) files.Add(f);
                    }
                }
                else if (File.Exists(path) && seen.Add(path))
                {
                    files.Add(path);
                }
            }
            return files;
        }

        /// <summary>The file's text, or null when it is too big, binary or can't be opened.</summary>
        private static string? ReadText(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length is 0 or > MaxFileBytes) return null;
                string text = File.ReadAllText(path);   // honours a UTF-8 / UTF-16 byte-order mark
                int probe = Math.Min(text.Length, 4096);
                for (int i = 0; i < probe; i++)
                    if (text[i] == '\0') return null;
                return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        private static int[] LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        private static int LineOf(int[] starts, int offset)
        {
            int i = Array.BinarySearch(starts, offset);
            return (i >= 0 ? i : ~i - 1) + 1;
        }

        // ---------------------------------------------------------------- finding

        private static IEnumerable<Hit> FindColors(string text, string extension)
        {
            var hits = new List<Hit>();
            bool alphaFirst = AlphaFirstExtensions.Contains(extension);
            bool prose = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);

            foreach (Match m in HexToken.Matches(text))
            {
                string h = m.Groups["h"].Value;
                if (prose && h.Length <= 4 && h.All(char.IsDigit)) continue;   // "#123" is an issue number, not a color
                if (m.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && h.Length is not (6 or 8)) continue;   // 0x1000 is a number
                if (HexColor(h, alphaFirst || m.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) is { } c)
                    hits.Add(new Hit(m.Index, m.Value, c));
            }

            foreach (Match m in RgbFn.Matches(text))
                if (ColorManager.TryParseColor(m.Value, out var c)) hits.Add(new Hit(m.Index, m.Value, c));

            foreach (Match m in HslFn.Matches(text))
                if (ColorManager.TryParseColor(m.Value, out var c)) hits.Add(new Hit(m.Index, m.Value, c));

            foreach (Match m in ColorCall.Matches(text))
            {
                bool argb = m.Groups["k"].Value.Equals("Argb", StringComparison.OrdinalIgnoreCase);
                var nums = new[] { "a", "b", "c", "d" }.Select(n => m.Groups[n]).Where(g => g.Success).Select(g => ByteOf(g.Value)).ToList();
                if (argb && nums.Count == 4) nums = nums.Skip(1).ToList();
                if (nums.Count == 3 && nums.All(n => n is >= 0 and <= 255))
                    hits.Add(new Hit(m.Index, m.Value, new RgbColor((byte)nums[0], (byte)nums[1], (byte)nums[2])));
            }

            foreach (Match m in NamedAfterProperty.Matches(text))
                if (ColorManager.TryNamedColor(m.Groups["n"].Value, out var c))
                    hits.Add(new Hit(m.Groups["n"].Index, m.Groups["n"].Value, c));

            if (extension.Equals(".gpl", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pal", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in PaletteTriple.Matches(text))
                {
                    int r = int.Parse(m.Groups[1].Value), g = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
                    if (r <= 255 && g <= 255 && b <= 255)
                        hits.Add(new Hit(m.Index, $"{r} {g} {b}", new RgbColor((byte)r, (byte)g, (byte)b)));
                }
            }
            else if (extension.Equals(".hex", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in PaletteHexLine.Matches(text))
                    if (HexColor(m.Groups[1].Value, false) is { } c) hits.Add(new Hit(m.Index, m.Groups[1].Value, c));
            }

            hits.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return hits;
        }

        private static int ByteOf(string s) =>
            s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s);

        /// <summary>3, 4, 6 or 8 hex digits to a color; alpha is dropped. <paramref name="alphaFirst"/>: 4 and 8 digits are ARGB.</summary>
        private static RgbColor? HexColor(string h, bool alphaFirst)
        {
            if (h.Length is 4 or 8 && alphaFirst) h = h[(h.Length / 4)..];   // drop the leading A / AA
            else if (h.Length == 8) h = h[..6];                              // CSS: RRGGBBAA
            else if (h.Length == 4) h = h[..3];                              // CSS: RGBA
            return ColorManager.TryHexToRgb(h, out var c) ? c : null;
        }
    }
}
