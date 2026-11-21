namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One shipped version: its number and the language keys of its bullet points.</summary>
    internal sealed record ReleaseEntry(string Version, string[] ItemKeys);

    /// <summary>
    /// The patch notes shown once in the "What's new" window after the app is updated.
    /// TO ADD NOTES FOR A NEW VERSION: bump &lt;Version&gt; in the csproj, add a <see cref="ReleaseEntry"/> at the TOP of
    /// <see cref="All"/>, and add its <c>Str_Rel_*</c> keys to en.xaml AND es.xaml. Nothing else.
    /// </summary>
    internal static class ReleaseNotes
    {
        /// <summary>Newest first. Item keys are language keys (text lives in en.xaml / es.xaml).</summary>
        public static readonly ReleaseEntry[] All =
        {
            new("2.3.0", new[]
            {
                "Str_Rel_2_3_0_a", "Str_Rel_2_3_0_b",
            }),
            new("2.2.0", new[]
            {
                "Str_Rel_2_2_0_a", "Str_Rel_2_2_0_b", "Str_Rel_2_2_0_c", "Str_Rel_2_2_0_d", "Str_Rel_2_2_0_e",
                "Str_Rel_2_2_0_f", "Str_Rel_2_2_0_g", "Str_Rel_2_2_0_h", "Str_Rel_2_2_0_i", "Str_Rel_2_2_0_j",
            }),
            new("2.1.0", new[]
            {
                "Str_Rel_2_1_0_a", "Str_Rel_2_1_0_b", "Str_Rel_2_1_0_c", "Str_Rel_2_1_0_d",
                "Str_Rel_2_1_0_e", "Str_Rel_2_1_0_f", "Str_Rel_2_1_0_g",
                "Str_Rel_2_1_0_h", "Str_Rel_2_1_0_i", "Str_Rel_2_1_0_j", "Str_Rel_2_1_0_k",
            }),
        };

        /// <summary>
        /// The entries to show: every one newer than <paramref name="lastSeen"/> up to and including the running
        /// <paramref name="current"/> version (at most 3, newest first). Empty when nothing is new.
        /// </summary>
        public static List<ReleaseEntry> Pending(string lastSeen, string current)
        {
            var result = new List<ReleaseEntry>();
            if (!TryParse(current, out var cur)) return result;
            bool hasSeen = TryParse(lastSeen, out var seen);
            foreach (var entry in All)
            {
                if (!TryParse(entry.Version, out var v) || v > cur) continue;
                if (hasSeen && v <= seen) continue;
                result.Add(entry);
                if (result.Count == 3) break;
            }
            return result;
        }

        private static bool TryParse(string? text, out Version version)
        {
            version = new Version();
            if (string.IsNullOrWhiteSpace(text)) return false;
            // "2.1.0-beta" -> "2.1.0"
            int dash = text.IndexOf('-');
            return Version.TryParse(dash >= 0 ? text[..dash] : text, out version!);
        }
    }
}
