namespace Smart_Tools.WPF.Controllers
{
    // Mock Data Generator - helpers for the user's saved value lists (edited in Settings,
    // picked on a list column). Pure: no WPF, no storage.
    public static partial class MockDataManager
    {
        public const int MaxListNameLength = 60;

        /// <summary>Why a list can't have this name (null when it can).</summary>
        public static string? ValidateListName(string? name, IEnumerable<string?> otherNames)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) return "Give the list a name.";
            if (trimmed.Length > MaxListNameLength) return $"Keep the name under {MaxListNameLength} characters.";
            if (otherNames.Any(n => string.Equals(n?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
                return $"There's already a list called \"{trimmed}\".";
            return null;
        }

        /// <summary>
        /// Adds what was typed or pasted (separated by commas, semicolons or new lines) to a list's values.
        /// Values already there are skipped (any capitalisation). The result has one value per line.
        /// </summary>
        public static (string Values, int Added) AddListValues(string existing, string typed)
        {
            var values = SplitList(existing).ToList();
            var seen = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (string value in SplitList(typed))
            {
                if (!seen.Add(value)) continue;
                values.Add(value);
                added++;
            }
            return (string.Join("\n", values), added);
        }

        public static string RemoveListValue(string existing, string value)
        {
            var values = SplitList(existing).ToList();
            int index = values.FindIndex(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) values.RemoveAt(index);
            return string.Join("\n", values);
        }
    }
}
