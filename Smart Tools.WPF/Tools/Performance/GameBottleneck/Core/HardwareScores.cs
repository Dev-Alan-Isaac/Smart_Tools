using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Models
{
    /// <summary>One processor or graphics card in the table, with a rough relative speed. Scores are ESTIMATES, only good for comparing two parts.</summary>
    public sealed record HardwarePart(string Name, int Score, string Key);

    /// <summary>One line of a part list file: the part's name as the table writes it ("RTX 4070", "Core i5-12400") and its score.</summary>
    public sealed record HardwareEntry(string Name, int Score);

    /// <summary>A list of parts that can be added to the built-in table (downloaded by the user, or shipped as the starting file).</summary>
    public sealed record HardwareTableFile(string Version, IReadOnlyList<HardwareEntry> Cpus, IReadOnlyList<HardwareEntry> Gpus);

    /// <summary>A part's score. <see cref="Estimated"/> means the part is not in the table: it was worked out from <see cref="BasedOn"/>, a known part of the same family.</summary>
    public sealed record PartMatch(string Name, int Score, bool Estimated, string? BasedOn);

    public readonly record struct GpuScore(PartMatch Match, int Score, bool Laptop);
}

namespace Smart_Tools.WPF.Controllers
{
    using Smart_Tools.WPF.Models;

    /// <summary>
    /// A small offline table of popular processors and graphics cards with approximate benchmark-style scores (in the range of the public
    /// CPU Mark / G3D Mark lists, rounded). It is shipped with the app, so no hardware lookup needs the internet. The numbers are
    /// rough: they say "about this much faster", never "this many frames". Update the table when a release is made.
    /// </summary>
    public static class HardwareScores
    {
        /// <summary>A laptop part runs slower than the desktop card of the same name (less power, lower clocks).</summary>
        public const double LaptopFactor = 0.75;

        // name, score
        private static readonly (string Name, int Score)[] CpuData =
        {
            // Intel 14th / 13th / 12th gen
            ("Core i9-14900K", 60000), ("Core i7-14700K", 53000), ("Core i5-14600K", 39500), ("Core i5-14400", 26500), ("Core i3-14100", 15500),
            ("Core i9-13900K", 59000), ("Core i7-13700K", 46500), ("Core i5-13600K", 38500), ("Core i5-13400", 25500), ("Core i5-13500", 33000), ("Core i3-13100", 14500),
            ("Core i9-12900K", 41500), ("Core i7-12700K", 34500), ("Core i7-12700", 32500), ("Core i5-12600K", 28500), ("Core i5-12400", 19500), ("Core i3-12100", 13500),
            // Intel 11th - 6th
            ("Core i9-11900K", 24500), ("Core i7-11700K", 22500), ("Core i5-11600K", 19500), ("Core i5-11400", 17500),
            ("Core i9-10900K", 25500), ("Core i7-10700K", 19500), ("Core i5-10600K", 16000), ("Core i5-10400", 12500), ("Core i3-10100", 7800),
            ("Core i9-9900K", 18500), ("Core i7-9700K", 13300), ("Core i5-9600K", 10000), ("Core i5-9400", 9400), ("Core i3-9100", 6300),
            ("Core i7-8700K", 14000), ("Core i7-8700", 13000), ("Core i5-8600K", 10500), ("Core i5-8400", 9500), ("Core i3-8100", 6000),
            ("Core i7-7700K", 9900), ("Core i7-7700", 9400), ("Core i5-7600K", 7000), ("Core i5-7500", 6200), ("Core i5-7400", 5800), ("Core i3-7100", 4300),
            ("Core i7-6700K", 8900), ("Core i7-6700", 8200), ("Core i5-6600K", 6500), ("Core i5-6500", 5600), ("Core i3-6100", 4200),
            ("Core i7-4790K", 8000), ("Core i7-4770K", 7500), ("Core i5-4690K", 6200), ("Core i5-4460", 4900), ("Core i3-4130", 3300),
            ("Core i7-3770K", 6700), ("Core i5-3570K", 5000), ("Core i7-2600K", 5500), ("Core i5-2500K", 4000),
            // Intel laptop
            ("Core i7-1260P", 20500), ("Core i7-1165G7", 10000), ("Core i5-1135G7", 9700), ("Core i5-1235U", 13000), ("Core i3-1115G4", 6000), ("Core i7-10750H", 12500), ("Core i5-10300H", 8300),
            ("Core i9-12900H", 29500), ("Core i7-12700H", 28000), ("Core i5-12500H", 22000), ("Core i7-13700H", 30500), ("Core i5-13500H", 24500),
            ("Core i7-1355U", 14500), ("Core i5-1335U", 13500), ("Core i7-1255U", 12500), ("Core i5-1245U", 11500), ("Core i3-1215U", 8500), ("Core i7-1065G7", 8900),
            ("Core i5-1035G1", 7000), ("Core i3-1005G1", 3800), ("Core i7-8565U", 7300), ("Core i5-8265U", 6300), ("Core i5-8250U", 5600), ("Core i3-8130U", 4300), ("Core i7-7500U", 3900),
            ("Core i7-9750H", 10500), ("Core i7-8750H", 9500), ("Core i5-9300H", 8300), ("Core i7-11800H", 20500), ("Core i5-11400H", 17000), ("Core i9-13900H", 31500),
            ("Ryzen 3 5300U", 9900), ("Ryzen 5 5600U", 14800), ("Ryzen 7 5800U", 17200), ("Ryzen 5 4500U", 10800), ("Ryzen 7 4700U", 13200), ("Ryzen 7 4800H", 18500), ("Ryzen 5 4600H", 14500),
            ("Ryzen 5 3500U", 7600), ("Ryzen 7 3700U", 8100), ("Ryzen 3 3300U", 5300), ("Ryzen 7 6800U", 21000), ("Ryzen 5 6600U", 17500), ("Ryzen 5 7520U", 11000), ("Ryzen 7 7840HS", 27500),
            ("Ryzen 9 6900HX", 23500), ("Ryzen 5 5600H", 17500), ("Ryzen 7 5800H", 20000), ("Ryzen 9 5900HX", 24000), ("Ryzen 5 7535HS", 21000), ("Ryzen 7 7735U", 19500),
            ("Pentium Gold G5400", 3300), ("Celeron G5905", 3000), ("Celeron N4020", 1300), ("Intel N100", 5500),
            // AMD Ryzen 7000 / 5000 / 3000 / 2000 / 1000
            ("Ryzen 9 7950X3D", 62000), ("Ryzen 9 7950X", 63000), ("Ryzen 9 7900X", 52500), ("Ryzen 7 7800X3D", 34500), ("Ryzen 7 7700X", 36000), ("Ryzen 7 7700", 33500),
            ("Ryzen 5 7600X", 28500), ("Ryzen 5 7600", 27000),
            ("Ryzen 9 5950X", 46000), ("Ryzen 9 5900X", 39500), ("Ryzen 7 5800X3D", 28500), ("Ryzen 7 5800X", 27800), ("Ryzen 7 5700X", 24800),
            ("Ryzen 5 5600X", 21800), ("Ryzen 5 5600", 20500), ("Ryzen 5 5500", 19500), ("Ryzen 3 4100", 11200),
            ("Ryzen 9 3950X", 40000), ("Ryzen 9 3900X", 32500), ("Ryzen 7 3700X", 22800), ("Ryzen 7 3800X", 23500), ("Ryzen 5 3600X", 18000), ("Ryzen 5 3600", 17700), ("Ryzen 5 3500", 12700),
            ("Ryzen 3 3300X", 11500), ("Ryzen 3 3100", 10500), ("Ryzen 3 3200G", 7000), ("Ryzen 5 3400G", 9500),
            ("Ryzen 7 2700X", 17000), ("Ryzen 7 2700", 15500), ("Ryzen 5 2600X", 13500), ("Ryzen 5 2600", 12800), ("Ryzen 3 2200G", 6500),
            ("Ryzen 7 1800X", 14500), ("Ryzen 7 1700", 13000), ("Ryzen 5 1600", 11500), ("Ryzen 5 1400", 7600), ("Ryzen 3 1200", 5800),
            ("Ryzen 7 5700U", 15500), ("Ryzen 5 5500U", 12500), ("Ryzen 7 6800H", 22000), ("Ryzen 7 7735HS", 23500), ("Ryzen 3 3250U", 3700),
            ("FX-8350", 5000), ("FX-6300", 3700), ("Athlon 3000G", 4200), ("Athlon 200GE", 3800),
        };

        private static readonly (string Name, int Score)[] GpuData =
        {
            // NVIDIA RTX 40 / 30 / 20
            ("RTX 4090", 38500), ("RTX 4080 SUPER", 34500), ("RTX 4080", 34500), ("RTX 4070 TI SUPER", 32500), ("RTX 4070 TI", 31000), ("RTX 4070 SUPER", 29500), ("RTX 4070", 27000),
            ("RTX 4060 TI", 22500), ("RTX 4060", 19500),
            ("RTX 3090 TI", 28500), ("RTX 3090", 26500), ("RTX 3080 TI", 26000), ("RTX 3080", 25000), ("RTX 3070 TI", 23500), ("RTX 3070", 22000), ("RTX 3060 TI", 20000),
            ("RTX 3060", 17000), ("RTX 3050", 13000),
            ("RTX 2080 TI", 21500), ("RTX 2080 SUPER", 18500), ("RTX 2080", 17500), ("RTX 2070 SUPER", 16800), ("RTX 2070", 15500), ("RTX 2060 SUPER", 15000), ("RTX 2060", 14000),
            // NVIDIA GTX
            ("GTX 1660 TI", 13000), ("GTX 1660 SUPER", 12800), ("GTX 1660", 11500), ("GTX 1650 SUPER", 10300), ("GTX 1650", 7800), ("GTX 1630", 5200),
            ("GTX 1080 TI", 18500), ("GTX 1080", 15000), ("GTX 1070 TI", 14500), ("GTX 1070", 13500), ("GTX 1060", 10000), ("GTX 1050 TI", 6300), ("GTX 1050", 5200),
            ("GTX 980 TI", 12200), ("GTX 980", 9800), ("GTX 970", 8800), ("GTX 960", 5600), ("GTX 950", 4600), ("GTX 780 TI", 7500), ("GTX 780", 6500), ("GTX 770", 4400), ("GTX 760", 3700),
            ("GTX 680", 3500), ("GTX 670", 3300), ("GTX 660", 2700), ("GTX 650", 1300), ("GT 1030", 2700), ("GT 730", 700), ("GT 710", 400),
            // AMD RX
            ("RX 7900 XTX", 31500), ("RX 7900 XT", 29000), ("RX 7900 GRE", 26500), ("RX 7800 XT", 25000), ("RX 7700 XT", 22500), ("RX 7600 XT", 17500), ("RX 7600", 16500),
            ("RX 6950 XT", 26500), ("RX 6900 XT", 25000), ("RX 6800 XT", 24500), ("RX 6800", 22000), ("RX 6750 XT", 20000), ("RX 6700 XT", 19000), ("RX 6650 XT", 16500),
            ("RX 6600 XT", 16000), ("RX 6600", 14000), ("RX 6500 XT", 9000), ("RX 6400", 7000),
            ("RX 5700 XT", 15000), ("RX 5700", 14000), ("RX 5600 XT", 12000), ("RX 5500 XT", 8500),
            ("RX 590", 9800), ("RX 580", 8800), ("RX 570", 7600), ("RX 560", 4400), ("RX 550", 2700), ("RX 480", 8500), ("RX 470", 7300), ("RX 460", 4200),
            ("VEGA 64", 13000), ("VEGA 56", 11500), ("R9 390", 7700), ("R9 380", 4600), ("R9 290X", 7500), ("R9 290", 7000), ("R9 280X", 4500), ("R9 280", 3800), ("R7 370", 3300), ("R7 360", 2500), ("HD 7970", 3900), ("HD 7850", 2300),
            // Intel
            ("ARC A770", 16000), ("ARC A750", 15000), ("ARC A580", 13000), ("ARC A380", 6000),
            // integrated
            ("UHD GRAPHICS 770", 2900), ("UHD GRAPHICS 730", 2000), ("UHD GRAPHICS 630", 1600), ("UHD GRAPHICS 620", 1300), ("UHD GRAPHICS 600", 800), ("HD GRAPHICS 530", 1000), ("HD GRAPHICS 4600", 700),
            ("IRIS XE", 2400), ("IRIS PLUS", 1800),
            ("RADEON 780M", 4800), ("RADEON 680M", 3500), ("RADEON VEGA 11", 1900), ("RADEON VEGA 8", 1400), ("RADEON VEGA 3", 700), ("RADEON GRAPHICS", 1500),
        };

        /// <summary>The version of the table that ships in the code (a date). A downloaded list adds to it and wins where a part is in both.</summary>
        public const string BuiltInVersion = "2026-11-25";

        private sealed record TableSet(List<HardwarePart> Cpus, List<HardwarePart> Gpus, string Version);

        private static volatile TableSet _set = Build(null);

        private static string KeyOf(string name, bool gpu)
        {
            string key = Normalize(name);
            return gpu ? Regex.Replace(key, @"^(NVIDIA )?(GEFORCE )?", "") : key;   // "GeForce RTX 5070" and "RTX 5070" are one part
        }

        private static TableSet Build(HardwareTableFile? file)
        {
            var cpus = new Dictionary<string, HardwarePart>();
            foreach (var (name, score) in CpuData) cpus[KeyOf(name, false)] = new HardwarePart(name, score, KeyOf(name, false));
            var gpus = new Dictionary<string, HardwarePart>();
            foreach (var (name, score) in GpuData) gpus[KeyOf(name, true)] = new HardwarePart(Pretty(name), score, KeyOf(name, true));
            if (file != null)
            {
                foreach (var e in file.Cpus) cpus[KeyOf(e.Name, false)] = new HardwarePart(e.Name, e.Score, KeyOf(e.Name, false));
                foreach (var e in file.Gpus) gpus[KeyOf(e.Name, true)] = new HardwarePart(Pretty(e.Name), e.Score, KeyOf(e.Name, true));
            }
            return new TableSet(cpus.Values.ToList(), gpus.Values.ToList(), file?.Version ?? BuiltInVersion);
        }

        /// <summary>Use a downloaded list on top of the built-in table (null goes back to the built-in one alone).</summary>
        public static void Apply(HardwareTableFile? file) => _set = Build(file);

        /// <summary>The version of the list in use: a date, the built-in one or the downloaded one.</summary>
        public static string ActiveVersion => _set.Version;

        public static int CpuCount => _set.Cpus.Count;

        public static int GpuCount => _set.Gpus.Count;

        public static IReadOnlyList<HardwarePart> AllCpus => _set.Cpus.OrderByDescending(p => p.Score).ToList();

        public static IReadOnlyList<HardwarePart> AllGpus => _set.Gpus.OrderByDescending(p => p.Score).ToList();

        /// <summary>The built-in table as a list file: the starting point for a hosted list (a test keeps the shipped copy in step).</summary>
        public static string BuiltInJson()
        {
            var file = new HardwareTableFile(BuiltInVersion,
                CpuData.Select(d => new HardwareEntry(d.Name, d.Score)).ToList(), GpuData.Select(d => new HardwareEntry(d.Name, d.Score)).ToList());
            return JsonSerializer.Serialize(new
            {
                version = file.Version,
                cpus = file.Cpus.Select(e => new { name = e.Name, score = e.Score }),
                gpus = file.Gpus.Select(e => new { name = e.Name, score = e.Score }),
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        // =================================================================
        //  A list file
        // =================================================================

        public const int MaxFileBytes = 2 * 1024 * 1024;
        private const int MaxEntries = 5000;

        /// <summary>Reads a list file. Strict: one bad line refuses the whole file, so a broken or tampered download can never half-apply.</summary>
        public static bool TryParseTableFile(string? json, out HardwareTableFile file, out string error)
        {
            file = new HardwareTableFile("", Array.Empty<HardwareEntry>(), Array.Empty<HardwareEntry>());
            error = "";
            if (string.IsNullOrWhiteSpace(json)) { error = "empty"; return false; }
            if (json.Length > MaxFileBytes) { error = "too big"; return false; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "not a list"; return false; }
                string version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (version.Length is 0 or > 40) { error = "no version"; return false; }
                if (!TryEntries(root, "cpus", out var cpus, out error) || !TryEntries(root, "gpus", out var gpus, out error)) return false;
                if (cpus.Count + gpus.Count == 0) { error = "no parts"; return false; }
                file = new HardwareTableFile(version, cpus, gpus);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                error = "not a valid list";   // whatever shape it has, a bad file is refused, never a crash
                return false;
            }
        }

        private static bool TryEntries(JsonElement root, string name, out List<HardwareEntry> entries, out string error)
        {
            entries = new List<HardwareEntry>();
            error = "";
            if (!root.TryGetProperty(name, out var array)) return true;   // a list may carry only processors or only cards
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxEntries) { error = name + " is not a list"; return false; }
            foreach (var item in array.EnumerateArray())
            {
                string? part = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (part == null || part.Trim().Length is < 2 or > 80
                    || !item.TryGetProperty("score", out var sc) || sc.ValueKind != JsonValueKind.Number || !sc.TryGetInt32(out int score) || score is < 1 or > 250000)
                {
                    error = $"a line in {name} is not valid";
                    return false;
                }
                entries.Add(new HardwareEntry(part.Trim(), score));
            }
            return true;
        }

        private static string Pretty(string name) =>
            name.StartsWith("RTX") || name.StartsWith("GTX") || name.StartsWith("GT ") ? "GeForce " + name
            : name.StartsWith("RX") || name.StartsWith("VEGA") || name.StartsWith("R9") || name.StartsWith("R7") || name.StartsWith("HD ") ? "Radeon " + name.Replace("VEGA", "Vega")
            : name.StartsWith("ARC") ? "Intel Arc " + name[4..]
            : name;

        /// <summary>Upper case, brand marks gone, dashes and runs of spaces become one space, so "Core(TM) i5-12400F" and "CORE I5 12400F" compare alike.</summary>
        public static string Normalize(string text)
        {
            string s = text.ToUpperInvariant();
            s = s.Replace("(R)", "").Replace("(TM)", "").Replace("®", "").Replace("™", "");
            return Regex.Replace(s, @"[\s\-_/]+", " ").Trim();
        }

        /// <summary>The table row that best matches a processor or card name (the longest table key found inside it), or null.</summary>
        public static HardwarePart? FindCpu(string? name) => Best(_set.Cpus, name);

        public static HardwarePart? FindGpu(string? name) => Best(_set.Gpus, name);

        private static HardwarePart? Best(List<HardwarePart> table, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string n = Normalize(name);
            HardwarePart? best = null;
            foreach (var part in table)
            {
                int at = n.IndexOf(part.Key, StringComparison.Ordinal);
                if (at < 0) continue;
                int end = at + part.Key.Length;
                // a model number must not run on into more digits ("I5-12400" inside "I5-124000"), but letters after it are suffixes (F, K, X, TI...)
                if (end < n.Length && char.IsDigit(n[end]) && char.IsDigit(part.Key[^1])) continue;
                // and must not begin in the middle of a number ("RX580" inside "RX1580")
                if (at > 0 && char.IsDigit(n[at - 1]) && char.IsDigit(part.Key[0])) continue;
                // a suffix that makes a different part ("RTX3060" + "TI") is a longer key in the table, so the longest key wins
                if (best == null || part.Key.Length > best.Key.Length) best = part;
            }
            return best;
        }

        /// <summary>The score for a card of this name; a laptop or Max-Q card is scaled down. A card missing from the table is estimated from its family. Null when neither works.</summary>
        public static GpuScore? ScoreGpu(string? name)
        {
            var match = ResolveGpu(name);
            if (match == null) return null;
            bool laptop = name != null && (name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) || name.Contains("Max-Q", StringComparison.OrdinalIgnoreCase) || name.Contains("Mobile", StringComparison.OrdinalIgnoreCase));
            return new GpuScore(match, laptop ? (int)(match.Score * LaptopFactor) : match.Score, laptop);
        }

        // =================================================================
        //  A part that is not in the table: worked out from its family
        // =================================================================

        /// <summary>The table's row for this processor, or an estimate from the nearest known one of its family (marked as such).</summary>
        public static PartMatch? ResolveCpu(string? name) =>
            FindCpu(name) is { } exact ? new PartMatch(exact.Name, exact.Score, false, null) : Estimate(name, _set.Cpus, ParseCpu, CpuStep, 3);

        public static PartMatch? ResolveGpu(string? name) =>
            FindGpu(name) is { } exact ? new PartMatch(exact.Name, exact.Score, false, null) : Estimate(name, _set.Gpus, ParseGpu, GpuStep, 2);

        /// <summary>A newer generation of the same model is usually this much faster than the one before.</summary>
        private const double GpuStep = 1.2;

        private const double CpuStep = 1.08;

        /// <summary>family: parts that compete in one line (RTX, or Ryzen 5 desktop); number: the model number; factor: what the suffix adds (Ti, XT, K, X3D).</summary>
        private sealed record Model(string Family, int Number, double Factor);

        private static readonly Regex GpuName = new(@"\b(?<f>RTX|GTX|RX|ARC A)\s?(?<n>\d{3,4})(?<s> TI SUPER| TI| SUPER| XTX| XT| GRE)?\b", RegexOptions.Compiled);
        private static readonly Regex IntelName = new(@"\bCORE I(?<t>[3579]) (?<n>\d{4,5})(?<s>[A-Z]{0,2})\b", RegexOptions.Compiled);
        private static readonly Regex RyzenName = new(@"\bRYZEN (?<t>[3579]) (?<n>\d{4})(?<s>X3D|XT|X|GE|G|U|HS|HX|H)?\b", RegexOptions.Compiled);

        private static Model? ParseGpu(string normalized)
        {
            var m = GpuName.Match(normalized);
            if (!m.Success || !int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
            double factor = m.Groups["s"].Value switch { " TI SUPER" => 1.20, " TI" => 1.12, " SUPER" => 1.08, " XTX" => 1.17, " XT" => 1.10, " GRE" => 1.05, _ => 1.0 };
            return new Model(m.Groups["f"].Value, n, factor);
        }

        private static Model? ParseCpu(string normalized)
        {
            var intel = IntelName.Match(normalized);
            if (intel.Success && int.TryParse(intel.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                string suffix = intel.Groups["s"].Value;
                bool mobile = suffix.Length > 0 && "HUPG".Contains(suffix[0]);   // a laptop chip is a line of its own
                return new Model("INTEL" + intel.Groups["t"].Value + (mobile ? suffix[..1] : "D"), n, !mobile && suffix.StartsWith('K') ? 1.06 : 1.0);
            }
            var ryzen = RyzenName.Match(normalized);
            if (ryzen.Success && int.TryParse(ryzen.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int r))
            {
                string suffix = ryzen.Groups["s"].Value;
                bool mobile = suffix is "U" or "H" or "HS" or "HX";
                return new Model("RYZEN" + ryzen.Groups["t"].Value + (mobile ? "M" : "D"), r, mobile ? 1.0 : suffix switch { "X3D" => 1.10, "X" or "XT" => 1.04, "G" or "GE" => 0.95, _ => 1.0 });
            }
            return null;
        }

        /// <summary>
        /// Work out a part's score from its family: the same model line one generation (or two) earlier, scaled by a typical generation step;
        /// or, with no such sibling, by where its model number falls between two known parts. Null when the name is not of a known family.
        /// </summary>
        private static PartMatch? Estimate(string? name, List<HardwarePart> table, Func<string, Model?> parse, double step, int maxSteps)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var target = parse(Normalize(name));
            if (target == null) return null;

            var known = table.Select(p => (Part: p, Model: parse(p.Key))).Where(x => x.Model != null && x.Model.Family == target.Family).Select(x => (x.Part, Model: x.Model!)).ToList();
            if (known.Count == 0) return null;
            int generation = target.Number / 1000, sibling = target.Number % 1000;

            // the same line in an earlier generation (RTX 5070 from the RTX 4070), then the same line with another suffix
            foreach (bool sameSuffix in new[] { true, false })
            {
                var earlier = known
                    .Where(k => k.Model.Number % 1000 == sibling && k.Model.Number / 1000 < generation && (!sameSuffix || Math.Abs(k.Model.Factor - target.Factor) < 1e-9))
                    .OrderByDescending(k => k.Model.Number).FirstOrDefault();
                if (earlier.Part == null) continue;
                int gap = Math.Min(generation - earlier.Model.Number / 1000, maxSteps);
                double score = earlier.Part.Score * Math.Pow(step, gap) * (target.Factor / earlier.Model.Factor);
                return new PartMatch(name.Trim(), (int)Math.Round(score), true, earlier.Part.Name);
            }

            // otherwise between its neighbours by model number
            var lower = known.Where(k => k.Model.Number < target.Number).OrderByDescending(k => k.Model.Number).FirstOrDefault();
            var upper = known.Where(k => k.Model.Number > target.Number).OrderBy(k => k.Model.Number).FirstOrDefault();
            if (lower.Part != null && upper.Part != null)
            {
                double fraction = (double)(target.Number - lower.Model.Number) / (upper.Model.Number - lower.Model.Number);
                return new PartMatch(name.Trim(), (int)Math.Round(lower.Part.Score + (upper.Part.Score - lower.Part.Score) * fraction), true, lower.Part.Name + " / " + upper.Part.Name);
            }
            var nearest = lower.Part != null ? lower : upper;
            return nearest.Part == null ? null : new PartMatch(name.Trim(), nearest.Part.Score, true, nearest.Part.Name);
        }
    }
}
