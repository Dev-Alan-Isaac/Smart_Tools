using Smart_Tools.WPF.Models;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Reading, checking and writing the text of a hosts file. Portable: no WPF, no file access.</summary>
    public sealed partial class HostsEditorManager
    {
        private static readonly Regex HostName = new(@"^[A-Za-z0-9_*][A-Za-z0-9_.*\-]{0,251}$", RegexOptions.Compiled);

        public static List<HostsRow> ParseHosts(IEnumerable<string> lines) => lines.Select(ParseLine).ToList();

        /// <summary>
        /// "127.0.0.1 a.test b.test # note" is an entry; "# 127.0.0.1 a.test" is an entry that is switched off;
        /// anything else starting with # (or not an entry at all) is kept as a comment.
        /// </summary>
        public static HostsRow ParseLine(string line)
        {
            string text = line.Trim();
            if (text.Length == 0) return new HostsRow { Kind = HostsRowKind.Blank, Raw = line };

            bool enabled = true;
            string body = text;
            if (text[0] == '#')
            {
                enabled = false;
                body = text.TrimStart('#').Trim();
            }

            if (!TryParseEntry(body, out string address, out string names, out string comment))
                return new HostsRow { Kind = HostsRowKind.Comment, Raw = line };

            var row = new HostsRow { Kind = HostsRowKind.Entry, Raw = line, IsEnabled = enabled, Address = address, Names = names, Comment = comment };
            row.Modified = false;   // the setters above flag a change; reading the file is not one
            return row;
        }

        private static bool TryParseEntry(string text, out string address, out string names, out string comment)
        {
            address = names = comment = "";
            int hash = text.IndexOf('#');
            string main = hash >= 0 ? text[..hash] : text;
            comment = hash >= 0 ? text[(hash + 1)..].Trim() : "";

            var parts = main.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !LooksLikeAddress(parts[0])) return false;
            address = parts[0];
            names = string.Join(" ", parts.Skip(1));
            return true;
        }

        /// <summary>IPAddress.TryParse accepts "1" (= 0.0.0.1); a hosts file needs the full dotted or colon form.</summary>
        private static bool LooksLikeAddress(string text) => (text.Contains('.') || text.Contains(':')) && IPAddress.TryParse(text, out _);

        public static HostsProblem Validate(HostsRow row)
        {
            if (row.Kind != HostsRowKind.Entry) return HostsProblem.None;
            if (!LooksLikeAddress(row.Address)) return HostsProblem.BadAddress;
            var names = row.Names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0) return HostsProblem.NoNames;
            return names.All(n => HostName.IsMatch(n)) ? HostsProblem.None : HostsProblem.BadName;
        }

        public static string FormatRow(HostsRow row)
        {
            if (row.Kind != HostsRowKind.Entry || !row.Modified) return row.Raw;
            var sb = new StringBuilder();
            if (!row.IsEnabled) sb.Append("# ");
            sb.Append(row.Address.PadRight(16)).Append(' ').Append(row.Names);
            if (row.Comment.Length > 0) sb.Append(" # ").Append(row.Comment);
            return sb.ToString();
        }

        /// <summary>The whole file, with Windows line endings and a closing newline.</summary>
        public static string FormatHosts(IEnumerable<HostsRow> rows) => string.Concat(rows.Select(r => FormatRow(r) + "\r\n"));
    }
}
