using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class SystemSnapshotManager
    {
        // =================================================================
        //  Showing a table in the app's language
        //  The scan data stays English and stable (JSON, CSV, section ids). Only what is shown on screen and in the
        //  HTML / text exports goes through here: titles, column names, notes and the fixed words in cells.
        //  Every lookup falls back to the English text, so a missing translation never breaks a table.
        //  Call on the UI thread (Loc reads the application's resources).
        // =================================================================

        /// <summary>A table as the person sees it.</summary>
        public sealed record ShownTable(string Title, string? Note, string[] Headers, List<string[]> Rows);

        /// <summary>Columns whose cells hold a fixed word (Yes, Running, Inbound...) that has a translation.</summary>
        private static readonly HashSet<string> WordColumns = new(StringComparer.Ordinal)
        {
            "State", "Start type", "Enabled", "Core to Windows", "Administrator", "Password required", "Virtual", "DNS setting", "Address setting",
            "Active", "In use now", "Firewall on", "Inbound by default", "Outbound by default", "Block all inbound", "Notifications off",
            "Direction", "Action", "Source", "Exists", "Has private key", "Uses %references%", "Scope", "For", "Store", "Where", "Signatures",
            "Note", "Found in", "Profile", "Profiles", "Protocol", "Type",
        };

        private static readonly Regex SecretsSentence = new(@"(\d+) value\(s\) that look like secrets are shown as \(hidden\)\.", RegexOptions.Compiled);
        private static readonly Regex SkippedTasksSentence = new(@"(\d+) tasks under \\Microsoft\\ were left out \(tick ""[^""]*"" to list them\)\.", RegexOptions.Compiled);
        private static readonly Regex AllRulesSentence = new(@"^All (\d+) rules\.$", RegexOptions.Compiled);
        private static readonly Regex CustomRulesSentence = new(@"^(\d+) rules added by people or installers\. (\d+) rules that Windows ships were left out .*$", RegexOptions.Compiled);

        /// <summary>The key part of a text: letters and digits only ("Display name" -> "Displayname").</summary>
        private static string Slug(string text) => new(text.Where(char.IsAsciiLetterOrDigit).ToArray());

        /// <summary>The on-screen name of a section: the translated one when there is one, else the English one.</summary>
        public static string SectionTitle(SnapshotTable table) => Loc.T("Str_SysSnap_Sec_" + table.Id, table.Title);

        public static ShownTable Localize(SnapshotTable table)
        {
            bool facts = table.Headers is ["Item", "Value"];
            var headers = table.Headers.Select(h => Loc.T("Str_SysSnap_Col_" + Slug(h), h)).ToArray();

            var rows = new List<string[]>(table.Rows.Count);
            foreach (var row in table.Rows)
            {
                var shown = new string[row.Length];
                for (int c = 0; c < row.Length; c++)
                {
                    string cell = row[c];
                    string header = c < table.Headers.Length ? table.Headers[c] : "";
                    if (cell.Length == 0 || cell.Length > 40) shown[c] = cell;
                    else if (facts && c == 0) shown[c] = Loc.T("Str_SysSnap_Item_" + Slug(cell), cell);
                    else if (facts && c == 1 || WordColumns.Contains(header)) shown[c] = Loc.T("Str_SysSnap_Val_" + Slug(cell), cell);
                    else shown[c] = cell;
                }
                rows.Add(shown);
            }

            return new ShownTable(SectionTitle(table), LocalizeNote(table), headers, rows);
        }

        /// <summary>
        /// Notes are built from a fixed sentence per section plus a few sentences with numbers in them.
        /// Each known sentence is swapped for its translation; anything else (a path, an unexpected error) stays as it is.
        /// </summary>
        private static string? LocalizeNote(SnapshotTable table)
        {
            string note = table.Note ?? "";
            if (note.Length == 0) return null;

            const string failedPrefix = "Could not be read: ";
            if (note.StartsWith(failedPrefix, StringComparison.Ordinal))
                return Loc.F("Str_SysSnap_NoteFailed", failedPrefix + "{0}", note[failedPrefix.Length..]);

            var parts = new List<string>();

            if (AllRulesSentence.Match(note) is { Success: true } all)
                return Loc.F("Str_SysSnap_NoteRulesAll", "All {0} rules.", all.Groups[1].Value);
            if (CustomRulesSentence.Match(note) is { Success: true } custom)
                return Loc.F("Str_SysSnap_NoteRulesCustom", note, custom.Groups[1].Value, custom.Groups[2].Value);

            string skipped = "", secrets = "";
            if (SkippedTasksSentence.Match(note) is { Success: true } sk)
            {
                skipped = Loc.F("Str_SysSnap_NoteSkippedTasks", sk.Value, sk.Groups[1].Value);
                note = note.Replace(sk.Value, "");
            }
            if (SecretsSentence.Match(note) is { Success: true } sc)
            {
                secrets = Loc.F("Str_SysSnap_NoteSecrets", sc.Value, sc.Groups[1].Value);
                note = note.Replace(sc.Value, "");
            }

            note = note.Trim();
            if (note.Length > 0)
            {
                string key = "Str_SysSnap_Note_" + table.Id + (table.Id == "features" && note.StartsWith("Windows did not", StringComparison.Ordinal) ? "_Empty" : "");
                parts.Add(Loc.T(key, note));
            }
            if (skipped.Length > 0) parts.Add(skipped);
            if (secrets.Length > 0) parts.Add(secrets);
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }
}
