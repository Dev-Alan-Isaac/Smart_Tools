using Smart_Tools.WPF.Models;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class TranslateManager
    {
        private static readonly TranslateLanguagePair[] BuiltInPairs =
        {
            new("en", "es", 115),
            new("es", "en", 115),
        };

        /// <summary>The pairs the app offers: the built-in ones, then the ones the user added in Settings.</summary>
        public static IReadOnlyList<TranslateLanguagePair> Pairs => _pairs ??= BuiltInPairs.Concat(LoadCustomPairs()).ToList();

        private static IReadOnlyList<TranslateLanguagePair>? _pairs;   // read on first use, after the other statics exist

        /// <summary>Raised on the UI thread after a pair is added to or removed from the list.</summary>
        public static event Action? PairsChanged;

        /// <summary>One manager for the Translate page and the Settings section, so a model that is loaded can be released before it's deleted.</summary>
        public static TranslateManager Shared { get; } = new();

        public static string CustomPairsPath => Path.Combine(AppPaths.DataRoot, "translate.json");

        private static readonly Regex LanguageCode = new("^[a-z]{2,8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static TranslateLanguagePair? Find(string fromCode, string toCode) =>
            Pairs.FirstOrDefault(p => p.FromCode == fromCode && p.ToCode == toCode);

        public static bool IsCustom(TranslateLanguagePair pair) => !BuiltInPairs.Contains(pair);

        /// <summary>The folder every pair's model folder lives in.</summary>
        public static string ModelsRoot => AppPaths.TranslateModels;

        /// <summary>Opens a folder in File Explorer (creating it first, so the button always has somewhere to go). False when Windows refuses.</summary>
        public static bool OpenInExplorer(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private sealed class CustomPairDto
        {
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public int ApproxMb { get; set; }
        }

        private sealed class CustomPairsFile
        {
            public List<CustomPairDto> CustomPairs { get; set; } = new();
        }

        /// <summary>Missing or damaged file → no custom pairs, never a crash.</summary>
        private static List<TranslateLanguagePair> LoadCustomPairs()
        {
            try
            {
                if (!File.Exists(CustomPairsPath)) return new();
                var file = JsonSerializer.Deserialize<CustomPairsFile>(File.ReadAllText(CustomPairsPath));
                return (file?.CustomPairs ?? new())
                    .Where(d => LanguageCode.IsMatch(d.From ?? "") && LanguageCode.IsMatch(d.To ?? "") && d.From != d.To)
                    .Select(d => new TranslateLanguagePair(d.From, d.To, Math.Max(1, d.ApproxMb)))
                    .Where(p => !BuiltInPairs.Any(b => b.Id == p.Id))
                    .DistinctBy(p => p.Id)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new();
            }
        }

        private static void SaveCustomPairs()
        {
            var file = new CustomPairsFile
            {
                CustomPairs = Pairs.Where(IsCustom).Select(p => new CustomPairDto { From = p.FromCode, To = p.ToCode, ApproxMb = p.ApproxMb }).ToList(),
            };
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                AtomicFile.WriteAllText(CustomPairsPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Names people type → the short code the models use ("chinese" and "ch" are zh, "jp" is ja).</summary>
        private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "en", ["ingles"] = "en", ["spanish"] = "es", ["espanol"] = "es",
            ["french"] = "fr", ["frances"] = "fr", ["german"] = "de", ["aleman"] = "de",
            ["italian"] = "it", ["italiano"] = "it", ["portuguese"] = "pt", ["portugues"] = "pt",
            ["dutch"] = "nl", ["neerlandes"] = "nl", ["catalan"] = "ca",
            ["russian"] = "ru", ["ruso"] = "ru", ["arabic"] = "ar", ["arabe"] = "ar",
            ["chinese"] = "zh", ["chino"] = "zh", ["ch"] = "zh", ["cn"] = "zh",
            ["japanese"] = "ja", ["japones"] = "ja", ["jp"] = "ja",
        };

        /// <summary>Lowercase, no accents, and a language name or common mistake turned into the code the models use.</summary>
        public static string NormalizeLanguage(string input)
        {
            var plain = new System.Text.StringBuilder();
            foreach (char c in input.Trim().Normalize(System.Text.NormalizationForm.FormD))
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) plain.Append(c);
            string s = plain.ToString().ToLowerInvariant();
            return LanguageAliases.GetValueOrDefault(s, s);
        }

        /// <summary>The pair as typed, then with Japanese spelled the other way (some models say "ja", others "jap").</summary>
        private static IEnumerable<(string From, string To)> NameVariants(string from, string to)
        {
            yield return (from, to);
            if (SwapJapanese(from) != from || SwapJapanese(to) != to) yield return (SwapJapanese(from), SwapJapanese(to));
        }

        private static string SwapJapanese(string code) => code switch { "ja" => "jap", "jap" => "ja", _ => code };

        /// <summary>
        /// Checks that Hugging Face has an Opus-MT export for the pair with every file the tool needs, and adds it to the
        /// list. Accepts codes or language names. Returns the pair, or null with a short reason. Nothing is downloaded yet.
        /// </summary>
        public static async Task<(TranslateLanguagePair? Pair, string? Error)> AddCustomPairAsync(string from, string to, CancellationToken token)
        {
            from = NormalizeLanguage(from);
            to = NormalizeLanguage(to);
            if (!LanguageCode.IsMatch(from) || !LanguageCode.IsMatch(to))
                return (null, "Use the short language codes, like en, es, or zh (or the language name).");
            if (from == to) return (null, "Pick two different languages.");
            if (NameVariants(from, to).Any(v => Find(v.From, v.To) != null)) return (null, "That pair is already in the list.");

            foreach (var (f, t) in NameVariants(from, to))
            {
                var (pair, error, notFound) = await TryAddAsync(f, t, token).ConfigureAwait(true);
                if (pair != null) return (pair, null);
                if (!notFound) return (null, error);
            }
            return (null, $"There's no ready-made model for {from} to {to}. Models usually go to or from English, so try translating in two steps through English.");
        }

        private static async Task<(TranslateLanguagePair? Pair, string? Error, bool NotFound)> TryAddAsync(string from, string to, CancellationToken token)
        {
            var candidate = new TranslateLanguagePair(from, to, 1);
            try
            {
                using var response = await Http.GetAsync($"{HubUrl}api/models/{candidate.Repo}/tree/main?recursive=true", token).ConfigureAwait(true);
                // Hugging Face answers 401 (not 404) for a repo that doesn't exist or isn't public.
                if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
                    return (null, null, true);
                if (!response.IsSuccessStatusCode)
                    return (null, $"The model list couldn't be checked (the server answered {(int)response.StatusCode}).", false);

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(true));
                var sizes = doc.RootElement.EnumerateArray()
                    .Where(e => e.TryGetProperty("path", out _) && e.TryGetProperty("size", out _))
                    .ToDictionary(e => e.GetProperty("path").GetString() ?? "", e => e.GetProperty("size").GetInt64());
                var missing = ModelFiles.Where(f => !sizes.ContainsKey(f.Remote)).Select(f => f.Remote).ToList();
                if (missing.Count > 0)
                    return (null, $"That model is missing files this tool needs ({string.Join(", ", missing)}).", false);

                long total = ModelFiles.Sum(f => sizes[f.Remote]);
                var pair = new TranslateLanguagePair(from, to, (int)Math.Max(1, (total + 1048575) / 1048576));
                _pairs = Pairs.Append(pair).ToList();
                SaveCustomPairs();
                PairsChanged?.Invoke();
                return (pair, null, false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                return (null, "Couldn't reach the download server. Check your internet connection and try again.", false);
            }
        }

        /// <summary>Takes a pair the user added off the list and deletes its downloaded model. Returns false when the files couldn't be removed.</summary>
        public bool RemoveCustomPair(TranslateLanguagePair pair)
        {
            if (!IsCustom(pair)) return false;
            if (!Remove(pair)) return false;
            _pairs = Pairs.Where(p => p != pair).ToList();
            SaveCustomPairs();
            PairsChanged?.Invoke();
            return true;
        }
    }
}
