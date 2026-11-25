using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Models
{
    /// <summary>What a game asks for at one level (minimum or recommended), as the store words it.</summary>
    public sealed record SpecLevel(string? Processor, string? Graphics, double? MemoryGb, string? Storage)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Processor) && string.IsNullOrWhiteSpace(Graphics) && MemoryGb == null;
    }

    public sealed record GameSpec(string Name, int AppId, SpecLevel Minimum, SpecLevel Recommended);

    public sealed record GameHit(int AppId, string Name);

    /// <summary>The parts of a PC that matter for a game. A score is null when the part is not in the table.</summary>
    public sealed record HardwareProfile(string CpuName, int? CpuScore, string GpuName, int? GpuScore, double RamGb, string? CpuEstimatedFrom = null, string? GpuEstimatedFrom = null);

    public enum LevelVerdict { Unknown, Below, Borderline, Meets, Comfortable }

    public enum LimitedBy { Unknown, Nothing, Cpu, Gpu, Memory, Balanced }

    /// <summary>One part against one level: its score, the required score, and the ratio (1.0 = exactly what the game asks).</summary>
    public sealed record PartCheck(string Part, double? Yours, double? Required, double? Ratio, string? Matched);

    public sealed record LevelResult(PartCheck Cpu, PartCheck Gpu, PartCheck Memory, LevelVerdict Verdict)
    {
        public IEnumerable<PartCheck> Parts => new[] { Cpu, Gpu, Memory };
    }

    public sealed record BottleneckResult(LevelResult Minimum, LevelResult Recommended, LimitedBy Limit, string LimitPart)
    {
        /// <summary>The level the limit was judged on: the recommended one if the game lists it, else the minimum.</summary>
        public LevelResult Judged => Recommended.Verdict != LevelVerdict.Unknown ? Recommended : Minimum;
    }
}

namespace Smart_Tools.WPF.Controllers
{
    using Smart_Tools.WPF.Models;

    /// <summary>
    /// Reading a game's page on the Steam store (search results and system requirements) and comparing it with a PC. Pure text work:
    /// the network call is in the manager. All of it is an ESTIMATE - store requirement lines are free text, and the hardware scores are rounded.
    /// </summary>
    public static class GameBottleneckMath
    {
        // =================================================================
        //  Reading the store's JSON
        // =================================================================

        /// <summary>The games in a store search answer ({"items":[{"id":1,"name":"..","type":"app"}]}). Empty when it is not that.</summary>
        public static IReadOnlyList<GameHit> ParseSearch(string json)
        {
            var hits = new List<GameHit>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return hits;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() is { } t && !t.Equals("app", StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.TryGetProperty("id", out var id) && id.TryGetInt32(out int appId)
                        && item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text)
                        hits.Add(new GameHit(appId, text));
                }
            }
            catch (JsonException)
            {
                // not JSON: no games
            }
            return hits;
        }

        /// <summary>The requirements in an appdetails answer. Null when the store has none for this app (or the answer is not what we expect).</summary>
        public static GameSpec? ParseDetails(string json, int appId)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var app)) return null;
                if (!app.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
                if (!app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
                string name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                // the store sends [] (an empty array) instead of an object when a game lists nothing
                if (!data.TryGetProperty("pc_requirements", out var req) || req.ValueKind != JsonValueKind.Object) return null;
                var min = ParseLevel(req.TryGetProperty("minimum", out var a) ? a.GetString() : null);
                var rec = ParseLevel(req.TryGetProperty("recommended", out var b) ? b.GetString() : null);
                return min.IsEmpty && rec.IsEmpty ? null : new GameSpec(name, appId, min, rec);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>One block of requirement HTML ("&lt;li&gt;&lt;strong&gt;Processor:&lt;/strong&gt; Intel ...&lt;br&gt;&lt;/li&gt;...") as Processor, Graphics, Memory and Storage.</summary>
        public static SpecLevel ParseLevel(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return new SpecLevel(null, null, null, null);
            string text = Regex.Replace(html, @"<\s*(br|/li|/p|/ul)\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", "");
            text = WebUtility.HtmlDecode(text).Replace(' ', ' ');

            string? cpu = null, gpu = null, storage = null;
            double? ram = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0 || colon == line.Length - 1) continue;
                string key = line[..colon].Trim().ToLowerInvariant();
                string value = line[(colon + 1)..].Trim();
                if (key.StartsWith("minimum") || key.StartsWith("recommended") || key.StartsWith("os")) continue;
                if (key.Contains("processor") || key == "cpu") cpu ??= value;
                else if (key.Contains("graphics") || key.Contains("video") || key == "gpu") gpu ??= value;
                else if (key.Contains("memory") || key == "ram") ram ??= ParseMemoryGb(value);
                else if (key.Contains("storage") || key.Contains("hard disk") || key.Contains("disk space")) storage ??= value;
            }
            return new SpecLevel(cpu, gpu, ram, storage);
        }

        private static readonly Regex Memory = new(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>GB|G|MB|M)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>"8 GB RAM" -> 8, "512 MB" -> 0.5. Null when there is no figure.</summary>
        public static double? ParseMemoryGb(string text)
        {
            var m = Memory.Match(text);
            if (!m.Success || !double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;
            return m.Groups["u"].Value.StartsWith("M", StringComparison.OrdinalIgnoreCase) ? n / 1024 : n;
        }

        // =================================================================
        //  Turning requirement text into a score
        // =================================================================

        private static readonly Regex Alternatives = new(@"\s+or\s+|\s*/\s*|\s*;\s*|\s*\|\s*|\s*,\s*|\s+and\s+|\s+equivalent", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The score a requirement line stands for. A line often lists alternatives ("Intel Core i5-4460 / AMD FX-6300"): any one will do,
        /// so the weakest part named is what the game needs. Null when no part named is in the table.
        /// </summary>
        public static (int Score, string Matched)? RequiredScore(string? text, bool gpu)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            (int Score, string Matched)? weakest = null;
            foreach (string piece in Alternatives.Split(text))
            {
                var part = gpu ? HardwareScores.ResolveGpu(piece) : HardwareScores.ResolveCpu(piece);
                if (part == null) continue;
                if (weakest == null || part.Score < weakest.Value.Score) weakest = (part.Score, part.Estimated ? "~" + part.Name : part.Name);   // "~" = estimated from its family
            }
            return weakest;
        }

        // =================================================================
        //  The comparison
        // =================================================================

        /// <summary>Under this a part is below what is asked; between this and 1 it is borderline.</summary>
        public const double BelowRatio = 0.85;

        /// <summary>At or over this a part has comfortable room.</summary>
        public const double ComfortableRatio = 1.5;

        /// <summary>CPU and GPU ratios closer than this (relative to the larger) are called balanced.</summary>
        public const double BalancedGap = 0.25;

        public static BottleneckResult Compare(HardwareProfile pc, GameSpec game)
        {
            var min = Level(pc, game.Minimum);
            var rec = Level(pc, game.Recommended);
            var judged = rec.Verdict != LevelVerdict.Unknown ? rec : min;
            var (limit, part) = FindLimit(judged);
            return new BottleneckResult(min, rec, limit, part);
        }

        public static LevelResult Level(HardwareProfile pc, SpecLevel need)
        {
            var cpuNeed = RequiredScore(need.Processor, gpu: false);
            var gpuNeed = RequiredScore(need.Graphics, gpu: true);
            var cpu = Check("CPU", pc.CpuScore, cpuNeed?.Score, cpuNeed?.Matched);
            var gpu = Check("GPU", pc.GpuScore, gpuNeed?.Score, gpuNeed?.Matched);
            var ram = Check("RAM", pc.RamGb > 0 ? pc.RamGb : null, need.MemoryGb, null);
            return new LevelResult(cpu, gpu, ram, Verdict(new[] { cpu, gpu, ram }));
        }

        private static PartCheck Check(string part, double? yours, double? required, string? matched) =>
            new(part, yours, required, yours is { } y && required is { } r && r > 0 ? y / r : null, matched);

        private static LevelVerdict Verdict(IEnumerable<PartCheck> parts)
        {
            var ratios = parts.Where(p => p.Ratio != null).Select(p => p.Ratio!.Value).ToList();
            if (ratios.Count == 0) return LevelVerdict.Unknown;
            double worst = ratios.Min();
            return worst < BelowRatio ? LevelVerdict.Below : worst < 1.0 ? LevelVerdict.Borderline : worst < ComfortableRatio ? LevelVerdict.Meets : LevelVerdict.Comfortable;
        }

        /// <summary>The part with the least room is what holds the PC back for this game; CPU and GPU close together are balanced.</summary>
        private static (LimitedBy, string) FindLimit(LevelResult level)
        {
            var known = level.Parts.Where(p => p.Ratio != null).ToList();
            if (known.Count == 0) return (LimitedBy.Unknown, "");
            var weakest = known.OrderBy(p => p.Ratio!.Value).First();
            if (weakest.Ratio >= 1.0) return (LimitedBy.Nothing, "");

            if (level.Cpu.Ratio is { } c && level.Gpu.Ratio is { } g && weakest.Part != "RAM" && Math.Abs(c - g) / Math.Max(c, g) < BalancedGap && Math.Max(c, g) < 1.0)
                return (LimitedBy.Balanced, "CPU + GPU");
            return (weakest.Part switch { "CPU" => LimitedBy.Cpu, "GPU" => LimitedBy.Gpu, _ => LimitedBy.Memory }, weakest.Part);
        }

        /// <summary>A PC is described by names. Looks the parts up in the table (a laptop card is scaled down).</summary>
        public static HardwareProfile Profile(string cpuName, string gpuName, double ramGb)
        {
            var cpu = HardwareScores.ResolveCpu(cpuName);
            var gpu = HardwareScores.ScoreGpu(gpuName);
            return new HardwareProfile(cpuName, cpu?.Score, gpuName, gpu?.Score, ramGb,
                cpu is { Estimated: true } ? cpu.BasedOn : null, gpu is { Match.Estimated: true } ? gpu.Value.Match.BasedOn : null);
        }
    }
}
