using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Persists Recent Colors / Favorites to color_history.json in
    /// %AppData%\Smart Tools (same file and shape as before, so existing
    /// history carries over). Also owns the list rules - de-duplication,
    /// the Recent limit, hex normalisation - so the view just displays them.
    /// </summary>
    public sealed class ColorHistoryStore
    {
        public const int MaxRecent = 12;
        public const int MaxFavorites = 48;

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        private readonly string _filePath;

        public List<string> Recent { get; } = new();
        public List<string> Favorites { get; } = new();

        public ColorHistoryStore(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(
                AppPaths.DataRoot, "color_history.json");
        }

        /// <summary>Never throws: a missing or corrupt file just means empty lists.</summary>
        public void Load()
        {
            Recent.Clear();
            Favorites.Clear();

            try
            {
                if (!File.Exists(_filePath)) return;
                var data = JsonSerializer.Deserialize<ColorHistory>(File.ReadAllText(_filePath), SerializerOptions);
                if (data == null) return;

                Recent.AddRange(Clean(data.Recent).Take(MaxRecent));
                Favorites.AddRange(Clean(data.Favorites).Take(MaxFavorites));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // keep the empty lists
            }
        }

        /// <summary>Valid, upper-case "#RRGGBB", de-duplicated, order kept.</summary>
        private static IEnumerable<string> Clean(IEnumerable<string>? hexes)
        {
            var seen = new HashSet<string>();
            foreach (string h in hexes ?? Enumerable.Empty<string>())
                if (ColorManager.TryHexToRgb(h, out var rgb) && seen.Add(rgb.Hex))
                    yield return rgb.Hex;
        }

        /// <summary>Moves (or adds) the color to the front of Recent. Returns false if it was already first.</summary>
        public bool AddRecent(RgbColor color)
        {
            string hex = color.Hex;
            if (Recent.Count > 0 && Recent[0] == hex) return false;

            Recent.Remove(hex);
            Recent.Insert(0, hex);
            if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
            Save();
            return true;
        }

        public bool IsFavorite(RgbColor color) => Favorites.Contains(color.Hex);

        /// <summary>Adds or removes the color from Favorites. Returns true when it is now a favorite.</summary>
        public bool ToggleFavorite(RgbColor color)
        {
            string hex = color.Hex;
            bool nowFavorite;
            if (Favorites.Remove(hex))
                nowFavorite = false;
            else
            {
                Favorites.Insert(0, hex);
                if (Favorites.Count > MaxFavorites) Favorites.RemoveAt(Favorites.Count - 1);
                nowFavorite = true;
            }
            Save();
            return nowFavorite;
        }

        public void RemoveRecent(string hex) { if (Recent.Remove(Normalize(hex))) Save(); }
        public void RemoveFavorite(string hex) { if (Favorites.Remove(Normalize(hex))) Save(); }

        public void ClearRecent()
        {
            if (Recent.Count == 0) return;
            Recent.Clear();
            Save();
        }

        private static string Normalize(string hex) => ColorManager.TryHexToRgb(hex, out var rgb) ? rgb.Hex : hex;

        /// <summary>
        /// Best-effort; never throws. Written to a temp file then swapped in,
        /// so a crash mid-write can't leave a truncated (and then discarded) history.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                string json = JsonSerializer.Serialize(new ColorHistory { Recent = Recent.ToList(), Favorites = Favorites.ToList() }, SerializerOptions);
                string temp = _filePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the on-screen lists are unaffected
            }
        }
    }
}
