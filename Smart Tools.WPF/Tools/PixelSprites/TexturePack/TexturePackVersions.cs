using Smart_Tools.WPF.Models;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One line of the user's own version file (minecraft_pack_formats.json).</summary>
    public sealed class PackVersionEntry
    {
        public string Label { get; set; } = "";
        public int Format { get; set; }
        /// <summary>True from Minecraft 1.21.9 on: pack.mcmeta uses min_format / max_format instead of pack_format.</summary>
        public bool UsesMinMax { get; set; }
    }

    public sealed class PackVersionFile
    {
        public List<PackVersionEntry> Versions { get; set; } = new();
    }

    /// <summary>
    /// The Minecraft versions the Texture Pack Builder knows, and how to keep them up to date:
    /// the built-in list, plus the user's own file (%AppData%\Smart Tools\minecraft_pack_formats.json) on top of it,
    /// a check against the version list the Minecraft launcher keeps, and an update from the Minecraft Wiki.
    /// Format numbers are whole numbers: a format with a minor part (97.1) is stored as its major (97), which pack.mcmeta
    /// accepts for min_format and max_format (a single integer covers every minor version).
    /// No WPF types.
    /// </summary>
    public static class TexturePackVersions
    {
        public const string SettingsName = "minecraft_pack_formats";
        public const string WikiTemplateUrl = "https://minecraft.wiki/w/Template:Resource_pack_format?action=raw";
        public const string WikiPageUrl = "https://minecraft.wiki/w/Pack_format";
        public const string MojangManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        /// <summary>First format number that goes with min_format / max_format (25w31a / 1.21.9).</summary>
        public const int FirstMinMaxFormat = 65;

        public static string LauncherManifestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions", "version_manifest_v2.json");

        // =================================================================
        //  The list: built-in + the user's file
        // =================================================================

        /// <summary>The built-in versions with the user's file on top (same label = the file wins), newest first.</summary>
        public static List<PackVersion> Load(string? path = null)
        {
            var file = JsonSettings.Load<PackVersionFile>(SettingsName, path);
            var custom = (file.Versions ?? new()).Where(v => IsValid(v)).Select(v => new PackVersion(v.Label.Trim(), v.Format, v.UsesMinMax));
            return Merge(TexturePackManager.Versions, custom);
        }

        public static bool IsValid(PackVersionEntry v) => !string.IsNullOrWhiteSpace(v.Label) && v.Label.Length <= 60 && v.Format is >= 1 and <= 100000;

        /// <summary>Adds or replaces entries by label and sorts newest first.</summary>
        public static List<PackVersion> Merge(IEnumerable<PackVersion> baseList, IEnumerable<PackVersion> overrides)
        {
            var map = new Dictionary<string, PackVersion>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in baseList) map[v.Label] = v;
            foreach (var v in overrides) map[v.Label] = v;
            return map.Values.OrderByDescending(v => KeyOf(v.Label), VersionKeyComparer.Instance).ToList();
        }

        /// <summary>
        /// Remembers a version in the user's file (adds it, or replaces the same label). Returns the new full list.
        /// </summary>
        public static List<PackVersion> AddOrUpdate(PackVersion version, string? path = null)
        {
            var file = JsonSettings.Load<PackVersionFile>(SettingsName, path);
            file.Versions ??= new();
            file.Versions.RemoveAll(v => string.Equals(v.Label, version.Label, StringComparison.OrdinalIgnoreCase));
            file.Versions.Add(new PackVersionEntry { Label = version.Label, Format = version.Format, UsesMinMax = version.UsesMinMax });
            JsonSettings.Save(SettingsName, file, path);
            return Load(path);
        }

        // =================================================================
        //  Comparing versions ("1.21.9", "26.1", "1.21.9 - 1.21.10")
        // =================================================================

        /// <summary>The numbers of a version id ("1.21.10" -> 1,21,10). Anything after a space or dash is ignored.</summary>
        public static int[] KeyOf(string id)
        {
            string first = Regex.Match(id ?? "", @"\d+(\.\d+)*").Value;
            return first.Length == 0 ? Array.Empty<int>() : first.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        }

        public sealed class VersionKeyComparer : IComparer<int[]>
        {
            public static readonly VersionKeyComparer Instance = new();

            public int Compare(int[]? a, int[]? b)
            {
                a ??= Array.Empty<int>();
                b ??= Array.Empty<int>();
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                    if (x != y) return x.CompareTo(y);
                }
                return 0;
            }
        }

        /// <summary>The start and end of a label: "1.21.7 - 1.21.8" gives both, "26.3" gives the same twice.</summary>
        public static (int[] From, int[] To) RangeOf(string label)
        {
            var matches = Regex.Matches(label ?? "", @"\d+(\.\d+)*");
            if (matches.Count == 0) return (Array.Empty<int>(), Array.Empty<int>());
            int[] Parse(string s) => s.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            return (Parse(matches[0].Value), Parse(matches[^1].Value));
        }

        // =================================================================
        //  New releases: the launcher's own list
        // =================================================================

        /// <summary>The release ids in the launcher's version_manifest_v2.json (or any manifest text). Empty when it can't be read.</summary>
        public static List<string> ReleaseIdsFromManifest(string manifestJson)
        {
            var ids = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(manifestJson);
                if (!doc.RootElement.TryGetProperty("versions", out var versions)) return ids;
                foreach (var v in versions.EnumerateArray())
                    if (v.TryGetProperty("type", out var t) && t.GetString() == "release" && v.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                        ids.Add(s);
            }
            catch (JsonException) { }
            return ids;
        }

        /// <summary>Releases newer than the oldest version we know that no label covers, newest first.</summary>
        public static List<string> MissingReleases(IEnumerable<string> releaseIds, IEnumerable<PackVersion> known)
        {
            var ranges = known.Select(k => RangeOf(k.Label)).Where(r => r.From.Length > 0).ToList();
            if (ranges.Count == 0) return new List<string>();
            var oldest = ranges.Select(r => r.From).OrderBy(k => k, VersionKeyComparer.Instance).First();
            var cmp = VersionKeyComparer.Instance;

            return releaseIds
                .Where(id => KeyOf(id).Length > 0 && cmp.Compare(KeyOf(id), oldest) >= 0)
                .Where(id => !ranges.Any(r => cmp.Compare(KeyOf(id), r.From) >= 0 && cmp.Compare(KeyOf(id), r.To) <= 0))
                .OrderByDescending(id => KeyOf(id), cmp)
                .ToList();
        }

        /// <summary>The launcher's list when it exists; otherwise Mojang's online list. Null error means success.</summary>
        public static async Task<(List<string> Ids, string? Error)> GetReleaseIdsAsync(string? launcherPath = null, CancellationToken token = default)
        {
            string path = launcherPath ?? LauncherManifestPath;
            try
            {
                if (File.Exists(path))
                {
                    var local = ReleaseIdsFromManifest(await File.ReadAllTextAsync(path, token));
                    if (local.Count > 0) return (local, null);
                }
                using var http = NewClient();
                var online = ReleaseIdsFromManifest(await http.GetStringAsync(MojangManifestUrl, token));
                return online.Count > 0 ? (online, null) : (online, "Mojang's version list couldn't be read.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
            {
                return (new List<string>(), "Couldn't read the Minecraft version list: " + ex.Message);
            }
        }

        // =================================================================
        //  The Minecraft Wiki
        // =================================================================

        // {{Pack format table|nolist=1|VALUE|FROM|TO|...   (development rows have dev1=... in the FROM slot and are skipped)
        private static readonly Regex WikiRow = new(
            @"^\s*\{\{Pack format table\|nolist=1\|(?<value>\d+(?:\.\d+)?)\|(?<from>[^|{}=]+)\|(?<to>[^|{}=]*)\|",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>The release rows of the wiki's "Resource pack format" template, from 1.13 on.</summary>
        public static List<PackVersion> ParseWikiTemplate(string wikitext)
        {
            var list = new List<PackVersion>();
            foreach (Match m in WikiRow.Matches(wikitext ?? ""))
            {
                string from = m.Groups["from"].Value.Trim(), to = m.Groups["to"].Value.Trim();
                if (!int.TryParse(m.Groups["value"].Value.Split('.')[0], out int format) || format < 4) continue;   // 4 = Minecraft 1.13
                string label = to.Length > 0 && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase) ? $"{from} - {to}" : from;
                list.Add(new PackVersion(label, format, format >= FirstMinMaxFormat));
            }
            return list.GroupBy(v => v.Label, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        /// <summary>
        /// Downloads the wiki's table and puts new or changed rows into the user's file. Returns how many were added or changed;
        /// Error is set (and nothing changes) when the page can't be read.
        /// </summary>
        public static async Task<(int Added, int Changed, string? Error)> UpdateFromWikiAsync(string? path = null, string? wikitextOverride = null, CancellationToken token = default)
        {
            string text;
            try
            {
                if (wikitextOverride != null) text = wikitextOverride;
                else
                {
                    using var http = NewClient();
                    text = await http.GetStringAsync(WikiTemplateUrl, token);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return (0, 0, "Couldn't reach the Minecraft Wiki: " + ex.Message);
            }

            var found = ParseWikiTemplate(text);
            if (found.Count == 0) return (0, 0, "The wiki page didn't have the table in the shape I expected. Use Add version instead.");

            var current = Load(path).ToDictionary(v => v.Label, StringComparer.OrdinalIgnoreCase);
            int added = 0, changed = 0;
            foreach (var v in found)
            {
                if (!current.TryGetValue(v.Label, out var existing)) { added++; AddOrUpdate(v, path); }
                else if (existing.Format != v.Format || existing.UsesMinMax != v.UsesMinMax) { changed++; AddOrUpdate(v, path); }
            }
            return (added, changed, null);
        }

        private static HttpClient NewClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartTools/1.0 (texture pack builder)");
            return http;
        }
    }
}
