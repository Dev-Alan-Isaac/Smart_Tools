using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    public enum CsvJsonDirection { CsvToJson, JsonToCsv }

    public sealed class CsvJsonSettings
    {
        public CsvJsonDirection Direction { get; set; } = CsvJsonDirection.CsvToJson;
        /// <summary>Auto, Comma, Semicolon, Tab, Pipe.</summary>
        public string Delimiter { get; set; } = "Auto";
        public bool HasHeader { get; set; } = true;
        public bool DetectTypes { get; set; } = true;
        public bool Indented { get; set; } = true;
    }

    public sealed record CsvJsonResult(string Text, int Rows, int Columns, string? Error);

    /// <summary>
    /// CSV &lt;-&gt; JSON: a real CSV reader (quotes, "" for a quote, line breaks inside a cell), numbers and true/false
    /// kept as such when asked, and JSON arrays of objects or of arrays back to CSV. No WPF types.
    /// </summary>
    public static class CsvJsonManager
    {
        public const string SettingsName = "csv_json";
        public const int MaxChars = 20_000_000;

        private static readonly Regex NumberPattern = new(@"^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static char DelimiterOf(string name, string text) => name switch
        {
            "Comma" => ',',
            "Semicolon" => ';',
            "Tab" => '\t',
            "Pipe" => '|',
            _ => Detect(text),
        };

        /// <summary>The most common of , ; tab | in the first lines (outside quotes); a comma when there is no clue.</summary>
        public static char Detect(string text)
        {
            var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
            bool quoted = false;
            int lines = 0;
            foreach (char c in text)
            {
                if (c == '"') quoted = !quoted;
                else if (!quoted)
                {
                    if (counts.ContainsKey(c)) counts[c]++;
                    else if (c == '\n' && ++lines >= 5) break;
                }
            }
            var best = counts.OrderByDescending(kv => kv.Value).First();
            return best.Value == 0 ? ',' : best.Key;
        }

        // =================================================================
        //  CSV reading and writing
        // =================================================================

        /// <summary>Rows of cells. Blank lines are skipped. An unclosed quote is an error (FormatException).</summary>
        public static List<List<string>> ParseCsv(string text, char delimiter)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, wasQuoted = false, rowQuoted = false;
            int i = 0;
            if (text.Length > 0 && text[0] == '﻿') i = 1;

            void EndCell()
            {
                row.Add(cell.ToString());
                cell.Clear();
                wasQuoted = false;
            }
            void EndRow()
            {
                EndCell();
                if (!(row.Count == 1 && row[0].Length == 0 && !rowQuoted)) rows.Add(row);
                row = new List<string>();
                rowQuoted = false;
            }

            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                }
                else if (c == '"' && cell.Length == 0 && !wasQuoted)
                {
                    quoted = wasQuoted = rowQuoted = true;
                }
                else if (c == delimiter) EndCell();
                else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
                else if (c == '\n') EndRow();
                else cell.Append(c);
            }
            if (quoted) throw new FormatException("A quote was opened but never closed.");
            if (cell.Length > 0 || row.Count > 0 || rowQuoted) EndRow();
            return rows;
        }

        public static string FormatCsv(IEnumerable<IEnumerable<string>> rows, char delimiter)
        {
            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                bool first = true;
                foreach (string cell in row)
                {
                    if (!first) sb.Append(delimiter);
                    first = false;
                    bool quote = cell.IndexOfAny(new[] { delimiter, '"', '\n', '\r' }) >= 0 || cell != cell.Trim();
                    if (quote) sb.Append('"').Append(cell.Replace("\"", "\"\"")).Append('"');
                    else sb.Append(cell);
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // =================================================================
        //  CSV -> JSON
        // =================================================================

        public static CsvJsonResult CsvToJson(string csv, char delimiter, bool hasHeader, bool detectTypes, bool indented)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new CsvJsonResult("", 0, 0, null);
            if (csv.Length > MaxChars) return new CsvJsonResult("", 0, 0, "That is too much text for this box. Use a smaller file.");

            List<List<string>> rows;
            try
            {
                rows = ParseCsv(csv, delimiter);
            }
            catch (FormatException ex)
            {
                return new CsvJsonResult("", 0, 0, ex.Message);
            }
            if (rows.Count == 0) return new CsvJsonResult("", 0, 0, null);

            int columns = rows.Max(r => r.Count);
            string[]? names = hasHeader ? HeaderNames(rows[0], columns) : null;
            var data = hasHeader ? rows.Skip(1).ToList() : rows;

            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartArray();
                foreach (var row in data)
                {
                    if (names != null)
                    {
                        w.WriteStartObject();
                        for (int c = 0; c < columns; c++)
                        {
                            w.WritePropertyName(names[c]);
                            WriteValue(w, c < row.Count ? row[c] : "", detectTypes);
                        }
                        w.WriteEndObject();
                    }
                    else
                    {
                        w.WriteStartArray();
                        foreach (string cell in row) WriteValue(w, cell, detectTypes);
                        w.WriteEndArray();
                    }
                }
                w.WriteEndArray();
            }
            return new CsvJsonResult(Encoding.UTF8.GetString(stream.ToArray()), data.Count, columns, null);
        }

        private static string[] HeaderNames(List<string> header, int columns)
        {
            var names = new string[columns];
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int c = 0; c < columns; c++)
            {
                string name = c < header.Count ? header[c].Trim() : "";
                if (name.Length == 0) name = "column" + (c + 1).ToString(CultureInfo.InvariantCulture);
                string unique = name;
                for (int n = 2; !used.Add(unique); n++) unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
                names[c] = unique;
            }
            return names;
        }

        private static void WriteValue(Utf8JsonWriter w, string cell, bool detectTypes)
        {
            if (detectTypes)
            {
                string t = cell.Trim();
                if (NumberPattern.IsMatch(t)) { w.WriteRawValue(t); return; }
                if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) { w.WriteBooleanValue(true); return; }
                if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) { w.WriteBooleanValue(false); return; }
                if (t == "null") { w.WriteNullValue(); return; }
            }
            w.WriteStringValue(cell);
        }

        // =================================================================
        //  JSON -> CSV
        // =================================================================

        public static CsvJsonResult JsonToCsv(string json, char delimiter)
        {
            if (string.IsNullOrWhiteSpace(json)) return new CsvJsonResult("", 0, 0, null);
            if (json.Length > MaxChars) return new CsvJsonResult("", 0, 0, "That is too much text for this box. Use a smaller file.");

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
            }
            catch (JsonException ex)
            {
                return new CsvJsonResult("", 0, 0, "The JSON isn't valid: " + ex.Message);
            }

            using (doc)
            {
                var root = doc.RootElement;
                var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
                if (items.Count == 0) return new CsvJsonResult("", 0, 0, null);

                var table = new List<List<string>>();
                if (items.All(e => e.ValueKind == JsonValueKind.Object))
                {
                    var columns = new List<string>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in items)
                        foreach (var p in item.EnumerateObject())
                            if (seen.Add(p.Name)) columns.Add(p.Name);

                    table.Add(columns);
                    foreach (var item in items)
                    {
                        var row = new List<string>(columns.Count);
                        foreach (string name in columns)
                            row.Add(item.TryGetProperty(name, out var v) ? CellText(v) : "");
                        table.Add(row);
                    }
                    return new CsvJsonResult(FormatCsv(table, delimiter), items.Count, columns.Count, null);
                }

                if (items.All(e => e.ValueKind == JsonValueKind.Array))
                {
                    foreach (var item in items) table.Add(item.EnumerateArray().Select(CellText).ToList());
                    return new CsvJsonResult(FormatCsv(table, delimiter), items.Count, table.Max(r => r.Count), null);
                }

                table.Add(new List<string> { "value" });
                foreach (var item in items) table.Add(new List<string> { CellText(item) });
                return new CsvJsonResult(FormatCsv(table, delimiter), items.Count, 1, null);
            }
        }

        private static string CellText(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => v.GetRawText(),
        };
    }
}
