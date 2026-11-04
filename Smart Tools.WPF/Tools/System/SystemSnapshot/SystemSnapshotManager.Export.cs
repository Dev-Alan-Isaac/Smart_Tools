using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class SystemSnapshotManager
    {
        // =================================================================
        //  Overview: the few figures worth seeing first
        // =================================================================

        public sealed record OverviewTile(string Label, string Value, string Icon);

        private static SnapshotTable? Find(SystemSnapshot s, string id) => s.Tables.FirstOrDefault(t => t.Id == id);

        private static string Fact(SystemSnapshot s, string table, string item) =>
            Find(s, table)?.Rows.FirstOrDefault(r => r.Length > 1 && r[0] == item)?[1] ?? "";

        private static int Count(SystemSnapshot s, string id) => Find(s, id)?.Rows.Count ?? 0;

        /// <summary>The tiles on the Overview page. A tile is left out when its section was not scanned.</summary>
        public static List<OverviewTile> BuildOverview(SystemSnapshot snapshot)
        {
            var tiles = new List<OverviewTile>();
            void Add(string label, string value, string icon) { if (value.Length > 0) tiles.Add(new OverviewTile(label, value, icon)); }

            if (Find(snapshot, "os") != null)
            {
                string windows = Fact(snapshot, "os", "Windows"), version = Fact(snapshot, "os", "Version"), build = Fact(snapshot, "os", "Build");
                Add("Windows", $"{windows} {version}".Trim() + (build.Length > 0 ? $" (build {build})" : ""), "MicrosoftWindows");
                Add("Processor", Fact(snapshot, "os", "Processor"), "Chip");
                Add("Memory", Fact(snapshot, "os", "Memory"), "Memory");
                Add("Computer", Fact(snapshot, "os", "Computer name"), "Laptop");
            }

            var adapters = Find(snapshot, "adapters");
            if (adapters != null)
            {
                var main = adapters.Rows.FirstOrDefault(r => r[3] == "Connected" && r[5].Length > 0 && r[7].Length > 0) ?? adapters.Rows.FirstOrDefault(r => r[3] == "Connected" && r[5].Length > 0);
                Add("Main address", main == null ? "" : $"{main[5]} ({main[2]})", "IpNetworkOutline");
            }

            var profiles = Find(snapshot, "firewall-profiles");
            if (profiles != null)
                Add("Firewall", string.Join(", ", profiles.Rows.Select(r => $"{r[0]} {(r[2] == "Yes" ? "on" : "OFF")}")), "ShieldHalfFull");

            var rules = Find(snapshot, "firewall-rules");
            if (rules != null) Add("Custom firewall rules", rules.Rows.Count(r => r[2] != "Windows").ToString(CultureInfo.InvariantCulture), "ShieldLockOutline");

            if (Find(snapshot, "ports") != null) Add("Listening ports", Count(snapshot, "ports").ToString(CultureInfo.InvariantCulture), "LanPending");

            var services = Find(snapshot, "services");
            if (services != null)
            {
                int running = services.Rows.Count(r => r[2] == "Running");
                Add("Services", $"{running} running of {services.Rows.Count}", "CogSyncOutline");
            }

            if (Find(snapshot, "startup") != null) Add("Startup items", Count(snapshot, "startup").ToString(CultureInfo.InvariantCulture), "RocketLaunchOutline");
            if (Find(snapshot, "tasks") != null) Add("Scheduled tasks", Count(snapshot, "tasks").ToString(CultureInfo.InvariantCulture), "CalendarClock");
            if (Find(snapshot, "programs") != null) Add("Installed programs", Count(snapshot, "programs").ToString(CultureInfo.InvariantCulture), "ApplicationOutline");
            if (Find(snapshot, "env") != null) Add("Environment variables", Count(snapshot, "env").ToString(CultureInfo.InvariantCulture), "VariableBox");
            return tiles;
        }

        // =================================================================
        //  Export: JSON (complete), text (like systeminfo), HTML (readable), CSV (one section)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep accents and symbols readable in the file
        };

        public static string ToJson(SystemSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

        public static string ToText(SystemSnapshot s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SYSTEM SNAPSHOT");
            sb.AppendLine($"Computer : {s.MachineName}");
            sb.AppendLine($"User     : {s.UserName}{(s.IsAdministrator ? " (administrator)" : "")}");
            sb.AppendLine($"Taken    : {s.CapturedAt:yyyy-MM-dd HH:mm}");
            if (!s.Complete) sb.AppendLine("The scan was cancelled: only the finished sections are here.");

            foreach (var table in s.Tables)
            {
                sb.AppendLine().AppendLine(new string('=', 78)).AppendLine(table.Title.ToUpperInvariant()).AppendLine(new string('=', 78));
                if (!string.IsNullOrEmpty(table.Note)) sb.AppendLine(table.Note).AppendLine();
                if (table.Rows.Count == 0) { sb.AppendLine("(nothing found)"); continue; }

                if (table.Headers.Length == 1)
                {
                    foreach (var row in table.Rows) sb.AppendLine(row[0]);
                }
                else if (table.Headers is ["Item", "Value"])
                {
                    int width = Math.Min(40, table.Rows.Max(r => r[0].Length));
                    foreach (var row in table.Rows) sb.AppendLine($"{row[0].PadRight(width)} : {row[1]}");
                }
                else
                {
                    // one block per row: long values (paths, address lists) stay whole and the file stays easy to search
                    int label = table.Headers.Max(h => h.Length);
                    int number = 0;
                    foreach (var row in table.Rows)
                    {
                        sb.AppendLine($"[{++number}]");
                        for (int c = 0; c < table.Headers.Length; c++)
                            if (row[c].Length > 0) sb.AppendLine($"  {table.Headers[c].PadRight(label)} : {row[c]}");
                    }
                }
            }
            return sb.ToString();
        }

        public static string ToCsv(SnapshotTable table)
        {
            static string Cell(string text) => text.IndexOfAny(new[] { ',', '"', '\r', '\n', ';' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
            var sb = new StringBuilder();
            sb.Append(string.Join(",", table.Headers.Select(Cell))).Append("\r\n");
            foreach (var row in table.Rows) sb.Append(string.Join(",", row.Select(Cell))).Append("\r\n");
            return sb.ToString();
        }

        public static string ToHtml(SystemSnapshot s)
        {
            string H(string text) => WebUtility.HtmlEncode(text);
            var sb = new StringBuilder();
            sb.Append("""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>System Snapshot
                """).Append(" - ").Append(H(s.MachineName)).Append("""
                </title>
                <style>
                :root{--bg:#f6f7f9;--card:#fff;--text:#1f2937;--muted:#6b7280;--line:#e5e7eb;--accent:#0ea5e9;--row:#f3f4f6}
                @media (prefers-color-scheme:dark){:root{--bg:#111418;--card:#1a1f26;--text:#e5e7eb;--muted:#9ca3af;--line:#2b323c;--row:#232a33}}
                body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
                main{max-width:1200px;margin:0 auto;padding:24px 16px}
                h1{margin:0 0 4px;font-size:24px}h2{margin:0 0 8px;font-size:17px}
                .meta{color:var(--muted);margin-bottom:16px}
                nav{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:16px}
                nav a{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:4px 10px;color:var(--text);text-decoration:none;font-size:13px}
                nav a:hover{border-color:var(--accent)}
                section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px;margin-bottom:16px}
                .note{color:var(--muted);margin:0 0 8px;font-size:13px}
                .wrap{overflow-x:auto}
                table{border-collapse:collapse;width:100%;font-size:13px}
                th,td{text-align:left;padding:5px 10px;border-bottom:1px solid var(--line);vertical-align:top}
                th{color:var(--muted);font-weight:600;white-space:nowrap}
                tbody tr:nth-child(even){background:var(--row)}
                td{word-break:break-word}
                input{width:100%;box-sizing:border-box;padding:8px 10px;margin-bottom:16px;border:1px solid var(--line);border-radius:8px;background:var(--card);color:var(--text)}
                @media print{input,nav{display:none}section{break-inside:avoid-page}}
                </style></head><body><main>
                """);

            sb.Append("<h1>System Snapshot</h1><div class=\"meta\">")
              .Append(H(s.MachineName)).Append(" · ").Append(H(s.UserName)).Append(s.IsAdministrator ? " (administrator)" : "")
              .Append(" · ").Append(s.CapturedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (!s.Complete) sb.Append(" · <strong>cancelled: only the finished sections are here</strong>");
            sb.Append("</div><input id=\"q\" placeholder=\"Filter every table...\" autofocus><nav>");
            foreach (var t in s.Tables) sb.Append("<a href=\"#").Append(H(t.Id)).Append("\">").Append(H(t.Title)).Append(" (").Append(t.Rows.Count).Append(")</a>");
            sb.Append("</nav>");

            foreach (var t in s.Tables)
            {
                sb.Append("<section id=\"").Append(H(t.Id)).Append("\"><h2>").Append(H(t.Title)).Append("</h2>");
                if (!string.IsNullOrEmpty(t.Note)) sb.Append("<p class=\"note\">").Append(H(t.Note)).Append("</p>");
                if (t.Rows.Count == 0) { sb.Append("<p class=\"note\">Nothing found.</p></section>"); continue; }

                sb.Append("<div class=\"wrap\"><table><thead><tr>");
                foreach (string header in t.Headers) sb.Append("<th>").Append(H(header)).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                foreach (var row in t.Rows)
                {
                    sb.Append("<tr>");
                    foreach (string cell in row) sb.Append("<td>").Append(H(cell)).Append("</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table></div></section>");
            }

            sb.Append("""
                </main><script>
                document.getElementById('q').addEventListener('input',function(){var q=this.value.toLowerCase();
                document.querySelectorAll('tbody tr').forEach(function(r){r.style.display=r.textContent.toLowerCase().indexOf(q)<0?'none':''})});
                </script></body></html>
                """);
            return sb.ToString();
        }

        public static string SuggestFileName(string machine, string extension, string? sectionId = null)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            string name = sectionId == null ? $"system_snapshot_{machine}_{stamp}" : $"{sectionId}_{machine}_{stamp}";
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            return name + extension;
        }
    }
}
