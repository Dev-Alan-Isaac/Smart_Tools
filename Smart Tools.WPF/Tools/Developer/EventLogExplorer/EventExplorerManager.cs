using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Event Log Explorer backend. This file is plain logic that does not touch Windows
    /// (the XPath that Windows runs, the Event ID filter, text filters, CSV / JSON, settings);
    /// reading logs is in EventExplorerManager.Windows.cs.
    /// </summary>
    public sealed partial class EventExplorerManager
    {
        public static readonly int[] MaxEventChoices = { 500, 1000, 5000, 20000 };
        public static readonly int[] LiveChoices = { 0, 5, 10, 30 };

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // =================================================================
        //  Query building
        // =================================================================

        /// <summary>The start of the chosen range (null = no limit).</summary>
        public static DateTime? RangeStart(EventExplorerTimeRange range, DateTime now) => range switch
        {
            EventExplorerTimeRange.LastHour => now.AddHours(-1),
            EventExplorerTimeRange.Last24Hours => now.AddHours(-24),
            EventExplorerTimeRange.Last7Days => now.AddDays(-7),
            EventExplorerTimeRange.Last30Days => now.AddDays(-30),
            _ => null,
        };

        /// <summary>
        /// Reads "41, 1000-1005, -7, -200-210": numbers and ranges to include, a leading minus to
        /// exclude (the same as Event Viewer's filter box). Returns null with an error text when
        /// something isn't understood.
        /// </summary>
        public static EventIdFilter? ParseEventIds(string? text, out string? error)
        {
            error = null;
            var filter = new EventIdFilter();
            if (string.IsNullOrWhiteSpace(text)) return filter;

            foreach (string raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool exclude = raw.StartsWith('-');
                string body = exclude ? raw[1..] : raw;
                int from, to;

                int dash = body.IndexOf('-');
                if (dash < 0)
                {
                    if (!TryId(body, out from)) return Fail(raw, out error);
                    to = from;
                }
                else
                {
                    if (!TryId(body[..dash], out from) || !TryId(body[(dash + 1)..], out to) || to < from) return Fail(raw, out error);
                }
                (exclude ? filter.Exclude : filter.Include).Add((from, to));
            }
            return filter;

            static bool TryId(string s, out int id) =>
                int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id is >= 0 and <= 65535;

            static EventIdFilter? Fail(string token, out string? message)
            {
                message = token;
                return null;
            }
        }

        /// <summary>
        /// The XPath Windows runs, so levels, time and Event IDs are filtered before any event is
        /// read: <c>*[System[(Level=1 or Level=2) and (EventID=41) and TimeCreated[@SystemTime&gt;='…']]]</c>.
        /// All five levels (or none) add no level clause.
        /// </summary>
        public static string BuildXPath(IReadOnlyCollection<EventExplorerLevel> levels, DateTime? fromLocal, DateTime? toLocal, EventIdFilter? ids)
        {
            var clauses = new List<string>();

            if (levels.Count is > 0 and < 5)
            {
                var parts = new List<string>();
                foreach (var level in levels.Distinct().OrderBy(l => (int)l))
                {
                    parts.Add($"Level={(int)level}");
                    if (level == EventExplorerLevel.Information) parts.Add("Level=0");   // "always log" events read as Information
                }
                clauses.Add("(" + string.Join(" or ", parts) + ")");
            }

            if (ids is { IsEmpty: false })
            {
                if (ids.Include.Count > 0)
                    clauses.Add("(" + string.Join(" or ", ids.Include.Select(IdRange)) + ")");
                // Windows' XPath subset has no not(): leave an ID out with != (or outside a range).
                foreach (var range in ids.Exclude)
                    clauses.Add(range.From == range.To
                        ? $"EventID!={range.From}"
                        : $"(EventID<{range.From} or EventID>{range.To})");
            }

            if (fromLocal != null || toLocal != null)
            {
                var times = new List<string>();
                if (fromLocal is { } from) times.Add($"@SystemTime>='{Utc(from)}'");
                if (toLocal is { } to) times.Add($"@SystemTime<='{Utc(to)}'");
                clauses.Add($"TimeCreated[{string.Join(" and ", times)}]");
            }

            return clauses.Count == 0 ? "*" : $"*[System[{string.Join(" and ", clauses)}]]";

            static string IdRange((int From, int To) r) =>
                r.From == r.To ? $"EventID={r.From}" : $"(EventID>={r.From} and EventID<={r.To})";

            static string Utc(DateTime local) =>
                local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        // =================================================================
        //  Filtering what was read
        // =================================================================

        /// <summary>
        /// Source and text filters (applied after the message is formatted, which Windows can't do
        /// in XPath). Every word of <paramref name="text"/> must appear somewhere in the event.
        /// </summary>
        public static bool Matches(EventExplorerEntry entry, string? source, string? text)
        {
            if (!string.IsNullOrWhiteSpace(source) &&
                entry.Provider.IndexOf(source.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (string.IsNullOrWhiteSpace(text)) return true;
            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (entry.Message.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Provider.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.EventId.ToString(CultureInfo.InvariantCulture).Contains(word, StringComparison.Ordinal)) continue;
                if (entry.LevelText.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                return false;
            }
            return true;
        }

        /// <summary>First non-empty line, tidied and cut short, for the list row.</summary>
        public static string Summarize(string? message, int maxLength = 240)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            string line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Length > 0) ?? "";
            line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return line.Length <= maxLength ? line : line[..(maxLength - 1)] + "…";
        }

        public static (int Critical, int Error, int Warning, int Information, int Verbose) CountLevels(IEnumerable<EventExplorerEntry> entries)
        {
            int c = 0, e = 0, w = 0, i = 0, v = 0;
            foreach (var entry in entries)
            {
                switch (entry.Level)
                {
                    case EventExplorerLevel.Critical: c++; break;
                    case EventExplorerLevel.Error: e++; break;
                    case EventExplorerLevel.Warning: w++; break;
                    case EventExplorerLevel.Verbose: v++; break;
                    default: i++; break;
                }
            }
            return (c, e, w, i, v);
        }

        // =================================================================
        //  Event details (XML)
        // =================================================================

        /// <summary>Indented XML for display; the original text when it can't be parsed.</summary>
        public static string PrettyXml(string xml)
        {
            try
            {
                return XDocument.Parse(xml).ToString();
            }
            catch (System.Xml.XmlException)
            {
                return xml;
            }
        }

        /// <summary>The event's own data items: EventData / Data, or the children of UserData.</summary>
        public static List<KeyValuePair<string, string>> ParseEventData(string xml)
        {
            var result = new List<KeyValuePair<string, string>>();
            try
            {
                var root = XDocument.Parse(xml).Root;
                if (root == null) return result;

                var eventData = root.Elements().FirstOrDefault(e => e.Name.LocalName == "EventData");
                if (eventData != null)
                {
                    int index = 1;
                    foreach (var data in eventData.Elements())
                    {
                        string name = (string?)data.Attribute("Name") ?? $"Data {index}";
                        result.Add(new KeyValuePair<string, string>(name, data.Value));
                        index++;
                    }
                    return result;
                }

                var userData = root.Elements().FirstOrDefault(e => e.Name.LocalName == "UserData")?.Elements().FirstOrDefault();
                if (userData != null)
                {
                    foreach (var item in userData.Elements())
                        result.Add(new KeyValuePair<string, string>(item.Name.LocalName, item.Value));
                }
            }
            catch (Exception)
            {
                // unreadable XML: no data items
            }
            return result;
        }

        // =================================================================
        //  Export
        // =================================================================

        /// <summary>CSV for Excel (write it with a BOM so it opens as UTF-8).</summary>
        public static string BuildCsv(IEnumerable<EventExplorerEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Level,Event ID,Source,Log,Computer,Record ID,Message");
            foreach (var e in entries)
            {
                sb.Append(Csv(e.TimeCreated.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                  .Append(Csv(e.LevelText)).Append(',')
                  .Append(e.EventId.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(e.Provider)).Append(',')
                  .Append(Csv(e.LogName)).Append(',')
                  .Append(Csv(e.Computer)).Append(',')
                  .Append(e.RecordId?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                  .AppendLine(Csv(e.Message));
            }
            return sb.ToString();

            static string Csv(string value)
            {
                // Spreadsheets run text that starts with = + - @ as a formula: keep it text.
                if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
                return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
            }
        }

        public static string BuildJson(IEnumerable<EventExplorerEntry> entries) =>
            JsonSerializer.Serialize(entries.Select(e => new
            {
                time = new DateTimeOffset(e.TimeCreated).ToString("o", CultureInfo.InvariantCulture),
                level = e.LevelText,
                eventId = e.EventId,
                source = e.Provider,
                log = e.LogName,
                computer = e.Computer,
                recordId = e.RecordId,
                message = e.Message,
            }), JsonOptions);

        public static string SuggestFileName(string logName, EventExplorerExportFormat format)
        {
            string safe = string.Concat(logName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            string ext = format switch { EventExplorerExportFormat.Csv => "csv", EventExplorerExportFormat.Json => "json", _ => "evtx" };
            return $"{safe}-{DateTime.Now:yyyyMMdd-HHmm}.{ext}";
        }

        public static void WriteTextExport(string path, string content) =>
            AtomicFile.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // =================================================================
        //  Settings
        // =================================================================

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "event_log_explorer.json");

        /// <summary>Missing file, bad JSON or odd values all fall back to the defaults.</summary>
        public static EventExplorerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new EventExplorerSettings();
                var s = JsonSerializer.Deserialize<EventExplorerSettings>(File.ReadAllText(file), JsonOptions) ?? new EventExplorerSettings();

                if (string.IsNullOrWhiteSpace(s.LastLog)) s.LastLog = "Application";
                if (!Enum.IsDefined(s.TimeRange)) s.TimeRange = EventExplorerTimeRange.Last24Hours;
                s.Levels = (s.Levels ?? new()).Where(Enum.IsDefined).Distinct().ToList();
                if (s.Levels.Count == 0) s.Levels = new EventExplorerSettings().Levels;
                if (!MaxEventChoices.Contains(s.MaxEvents)) s.MaxEvents = 1000;
                if (!LiveChoices.Contains(s.LiveSeconds)) s.LiveSeconds = 0;
                return s;
            }
            catch (Exception)
            {
                return new EventExplorerSettings();
            }
        }

        public static void SaveSettings(EventExplorerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
