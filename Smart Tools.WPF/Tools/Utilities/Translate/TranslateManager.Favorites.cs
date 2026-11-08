using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Starred language pairs. Only pair ids ("en-es") are stored, never any text.</summary>
    public sealed partial class TranslateManager
    {
        public const int MaxFavorites = 12;

        public static TranslateSettings LoadSettings()
        {
            var settings = JsonSettings.Load<TranslateSettings>("translate_settings");
            settings.Favorites ??= new List<string>();
            return settings;
        }

        public static void SaveSettings(TranslateSettings settings) => JsonSettings.Save("translate_settings", settings);

        public static string PairId(string fromCode, string toCode) => $"{fromCode}-{toCode}";

        public static bool IsFavorite(IReadOnlyList<string> favorites, string pairId) => favorites.Contains(pairId, StringComparer.Ordinal);

        /// <summary>Adds the pair, or takes it off when it is already there. False when the list is full and nothing was added.</summary>
        public static bool ToggleFavorite(List<string> favorites, string pairId)
        {
            int at = favorites.FindIndex(f => f == pairId);
            if (at >= 0) { favorites.RemoveAt(at); return true; }
            if (favorites.Count >= MaxFavorites) return false;
            favorites.Add(pairId);
            return true;
        }

        /// <summary>Splits "en-es" into its codes; false when it isn't a pair id.</summary>
        public static bool TrySplitPairId(string id, out string from, out string to)
        {
            from = to = "";
            int dash = id.IndexOf('-');
            if (dash <= 0 || dash == id.Length - 1) return false;
            from = id[..dash];
            to = id[(dash + 1)..];
            return true;
        }

        /// <summary>Drops duplicates and pairs that can't be translated any more (a custom pair was removed). True when something changed.</summary>
        public static bool PruneFavorites(List<string> favorites)
        {
            int before = favorites.Count;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            favorites.RemoveAll(id => !seen.Add(id) || !TrySplitPairId(id, out string f, out string t) || Route(f, t) == null);
            return favorites.Count != before;
        }
    }
}
