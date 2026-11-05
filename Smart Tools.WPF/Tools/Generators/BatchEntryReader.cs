using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>A file or folder added to a generator's batch list, and how many codes it gave.</summary>
    public sealed class BatchEntrySource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
        public int Count { get; init; }

        public string Name => Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FullPath;

        public string Subtitle => IsFolder
            ? Loc.N(Count, "Str_Generators_SrcFolderOne", "Folder · 1 file name", "Str_Generators_SrcFolderMany", "Folder · {0:N0} file names", Count)
            : Loc.N(Count, "Str_Generators_SrcFileOne", "1 code", "Str_Generators_SrcFileMany", "{0:N0} codes", Count);
    }

    /// <summary>
    /// Reads the values to encode out of a list file, the same way for every generator: CSV / TSV (the column named
    /// value, code, text... or the first one), JSON (an array of strings, of objects, or an object holding such an
    /// array), XML (the leaf elements) and anything else as one value per line. No WPF types.
    /// </summary>
    public static class BatchEntryReader
    {
        public const long MaxFileBytes = 20 * 1024 * 1024;
        public const int MaxEntries = 100_000;
        private const int MaxEntryLength = 4000;

        // Column / property / element names that mean "the value to encode", in priority order.
        private static readonly string[] KnownNames =
        {
            "value", "code", "text", "data", "content", "barcode", "qr", "qrcode", "url", "link", "sku", "ean", "upc", "id", "number", "name",
        };

        /// <summary>The values in a file, or an error text (already localized) when it can't be read.</summary>
        public static (List<string> Entries, string? Error) ReadFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return (new List<string>(), Loc.T("Str_Generators_ErrSourceGone", "That file no longer exists."));
                if (info.Length > MaxFileBytes) return (new List<string>(), Loc.F("Str_Generators_ErrSourceBig", "{0} is larger than 20 MB.", info.Name));

                string text = File.ReadAllText(path);
                var found = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".csv" => FromDelimited(text),
                    ".tsv" => FromDelimited(text, '\t'),
                    ".json" => FromJson(text),
                    ".xml" => FromXml(text),
                    _ => FromLines(text),
                };
                return (Clean(found), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or XmlException or DecoderFallbackException)
            {
                return (new List<string>(), Loc.F("Str_Generators_ErrSourceRead", "Couldn't read {0}: {1}", Path.GetFileName(path), ex.Message));
            }
        }

        /// <summary>The file names directly inside a folder.</summary>
        public static (List<string> Entries, string? Error) ReadFolderNames(string folder)
        {
            try
            {
                return (Clean(Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f) ?? "").Order(StringComparer.OrdinalIgnoreCase)), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new List<string>(), Loc.F("Str_Err_FolderRead", "Couldn't read that folder: {0}", ex.Message));
            }
        }

        private static List<string> Clean(IEnumerable<string> raw) =>
            raw.Select(v => v.Trim()).Where(v => v.Length > 0).Select(v => v.Length > MaxEntryLength ? v[..MaxEntryLength] : v).Take(MaxEntries).ToList();

        private static IEnumerable<string> FromLines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // ---------------------------------------------------------------- CSV / TSV

        private static IEnumerable<string> FromDelimited(string text, char? forced = null)
        {
            var rows = ParseRows(text, forced ?? DetectDelimiter(text));
            if (rows.Count == 0) return Array.Empty<string>();

            // A header row is one whose cells include a known name; its column is the one to read.
            int column = 0, start = 0, best = int.MaxValue;
            for (int c = 0; c < rows[0].Count; c++)
            {
                int rank = Array.IndexOf(KnownNames, rows[0][c].Trim().ToLowerInvariant());
                if (rank >= 0 && rank < best) { best = rank; column = c; start = 1; }
            }
            return rows.Skip(start).Select(r => column < r.Count ? r[column] : "");
        }

        private static char DetectDelimiter(string text)
        {
            string first = text.Split('\n', 2)[0];
            bool quoted = false;
            int comma = 0, semicolon = 0, tab = 0;
            foreach (char ch in first)
            {
                if (ch == '"') quoted = !quoted;
                else if (!quoted)
                {
                    if (ch == ',') comma++;
                    else if (ch == ';') semicolon++;
                    else if (ch == '\t') tab++;
                }
            }
            return tab > comma && tab >= semicolon ? '\t' : semicolon > comma ? ';' : ',';
        }

        private static List<List<string>> ParseRows(string text, char delimiter)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;

            void EndCell() { row.Add(cell.ToString()); cell.Clear(); }
            void EndRow()
            {
                EndCell();
                if (row.Count > 1 || row[0].Trim().Length > 0) rows.Add(row);
                row = new List<string>();
            }

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else cell.Append(ch);
                }
                else if (ch == '"' && cell.Length == 0) quoted = true;
                else if (ch == delimiter) EndCell();
                else if (ch == '\n') EndRow();
                else if (ch != '\r') cell.Append(ch);
            }
            if (cell.Length > 0 || row.Count > 0) EndRow();
            return rows;
        }

        // ---------------------------------------------------------------- JSON

        private static IEnumerable<string> FromJson(string text)
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var found = new List<string>();
            CollectJson(doc.RootElement, found, 0);
            return found;
        }

        private static void CollectJson(JsonElement element, List<string> into, int depth)
        {
            if (depth > 6 || into.Count >= MaxEntries) return;
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) CollectJson(item, into, depth + 1);
                    break;

                case JsonValueKind.Object:
                    // An object that holds a list (for example { "codes": [...] }) is read through that list.
                    var list = element.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
                    if (list.Value.ValueKind == JsonValueKind.Array && !HasKnownScalar(element)) { CollectJson(list.Value, into, depth + 1); break; }
                    if (PickScalar(element) is { } picked) into.Add(picked);
                    break;

                case JsonValueKind.String:
                    into.Add(element.GetString() ?? "");
                    break;

                case JsonValueKind.Number:
                    into.Add(element.GetRawText());
                    break;
            }
        }

        private static bool HasKnownScalar(JsonElement obj) =>
            obj.EnumerateObject().Any(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number && Array.IndexOf(KnownNames, p.Name.ToLowerInvariant()) >= 0);

        private static string? PickScalar(JsonElement obj)
        {
            var props = obj.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number).ToList();
            foreach (string name in KnownNames)
            {
                var hit = props.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (hit.Value.ValueKind != JsonValueKind.Undefined) return hit.Value.ValueKind == JsonValueKind.String ? hit.Value.GetString() : hit.Value.GetRawText();
            }
            if (props.Count == 0) return null;
            var first = props[0].Value;
            return first.ValueKind == JsonValueKind.String ? first.GetString() : first.GetRawText();
        }

        // ---------------------------------------------------------------- XML

        private static IEnumerable<string> FromXml(string text)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var leaves = XDocument.Load(reader).Descendants().Where(e => !e.HasElements && e.Value.Trim().Length > 0).ToList();

            foreach (string name in KnownNames)
            {
                var named = leaves.Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count > 0) return named.Select(e => e.Value);
            }
            return leaves.Select(e => e.Value);
        }
    }
}
