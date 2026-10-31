using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Export of everything read, as JSON, CSV or XML. Written to a temp name
    /// and moved into place, off the UI thread. Only rows that were read are
    /// included.
    /// </summary>
    public static partial class MetadataExtractManager
    {
        public static string ExtensionFor(MetadataExportFormat format) => format switch
        {
            MetadataExportFormat.Csv => ".csv",
            MetadataExportFormat.Xml => ".xml",
            _ => ".json",
        };

        public static string ExportFilter(MetadataExportFormat format) => format switch
        {
            MetadataExportFormat.Csv => "CSV - opens in Excel (*.csv)|*.csv",
            MetadataExportFormat.Xml => "XML (*.xml)|*.xml",
            _ => "JSON (*.json)|*.json",
        };

        public static string ExportHint(MetadataExportFormat format) => format switch
        {
            MetadataExportFormat.Csv => "One row per file, one column per detail - opens in Excel.",
            MetadataExportFormat.Xml => "One <File> per file with its <Field>s, for other programs.",
            _ => "Grouped by section (General, Camera, Location…), for scripts and other tools.",
        };

        /// <summary>A snapshot of what to export, taken on the UI thread (rows can change while the file is written).</summary>
        public sealed record ExportRow(string Name, string FullPath, MetadataKind Kind, string Summary, IReadOnlyList<MetadataField> Fields);

        public static List<ExportRow> Snapshot(IEnumerable<MetadataFileItem> items) =>
            items.Where(i => i.Status == MetadataStatus.Done && i.Fields is { Count: > 0 })
                 .Select(i => new ExportRow(i.Name, i.FullPath, i.Kind, i.Summary, i.Fields!))
                 .ToList();

        /// <summary>Writes the export. Returns how many files went in.</summary>
        public static Task<int> ExportAsync(IReadOnlyList<ExportRow> rows, string path, MetadataExportFormat format, CancellationToken token) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Directory.CreateDirectory(folder);
                string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        switch (format)
                        {
                            case MetadataExportFormat.Csv: WriteCsv(rows, stream, token); break;
                            case MetadataExportFormat.Xml: WriteXml(rows, stream, token); break;
                            default: WriteJson(rows, stream, token); break;
                        }
                    }
                    File.Move(temp, path, overwrite: true);   // the Save dialog already asked about replacing
                    return rows.Count;
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }, token);

        private static void WriteJson(IReadOnlyList<ExportRow> rows, Stream stream, CancellationToken token)
        {
            using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            w.WriteStartArray();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                w.WriteStartObject();
                w.WriteString("File", row.Name);
                w.WriteString("Path", row.FullPath);
                w.WriteString("Kind", row.Kind.ToString());
                w.WriteString("Summary", row.Summary);
                w.WriteStartObject("Metadata");
                foreach (var group in row.Fields.GroupBy(f => f.Group))
                {
                    w.WriteStartObject(group.Key);
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in group)
                        if (names.Add(field.Property)) w.WriteString(field.Property, field.Value);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        /// <summary>
        /// One row per file; columns are "Section: Property" (a video has a
        /// Video and an Audio "Codec"), the union over all files in first-seen
        /// order. Old version: a file with two same-named fields crashed it.
        /// </summary>
        private static void WriteCsv(IReadOnlyList<ExportRow> rows, Stream stream, CancellationToken token)
        {
            static string Key(MetadataField f) => $"{f.Group}: {f.Property}";
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
                foreach (var field in row.Fields)
                    if (seen.Add(Key(field))) columns.Add(Key(field));

            using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));   // the BOM tells Excel it's UTF-8 ("°", accents)
            w.WriteLine(string.Join(",", new[] { "File", "Path", "Kind" }.Concat(columns).Select(CsvCell)));
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in row.Fields) lookup.TryAdd(Key(field), field.Value);
                var cells = new[] { row.Name, row.FullPath, row.Kind.ToString() }.Concat(columns.Select(c => lookup.TryGetValue(c, out var v) ? v : ""));
                w.WriteLine(string.Join(",", cells.Select(CsvCell)));
            }
        }

        /// <summary>
        /// Quotes as needed, and stops spreadsheet formulas: metadata comes
        /// from the files (anyone can write "=HYPERLINK(…)" into a photo's
        /// description), so a cell starting with = + - @ gets a leading
        /// apostrophe - unless it's just a number like -0.7.
        /// </summary>
        public static string CsvCell(string value)
        {
            string v = value ?? "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                v = "'" + v;
            bool quote = v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || v != v.Trim();
            return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        /// <summary>XML can't hold most control characters - the old export crashed on EXIF text containing them.</summary>
        public static string XmlSafe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(c).Append(value[++i]);
                }
                else if (XmlConvert.IsXmlChar(c))
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static void WriteXml(IReadOnlyList<ExportRow> rows, Stream stream, CancellationToken token)
        {
            var root = new XElement("MetadataExport", new XAttribute("created", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)));
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                root.Add(new XElement("File",
                    new XAttribute("name", XmlSafe(row.Name)),
                    new XAttribute("path", XmlSafe(row.FullPath)),
                    new XAttribute("kind", row.Kind.ToString()),
                    row.Fields.Select(f => new XElement("Field",
                        new XAttribute("group", XmlSafe(f.Group)),
                        new XAttribute("property", XmlSafe(f.Property)),
                        XmlSafe(f.Value)))));
            }
            using var w = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) });
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(w);
        }

        public static string PlainExportError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows didn't allow saving there - pick another folder.",
            DirectoryNotFoundException => "That folder isn't there any more.",
            IOException io when io.HResult == unchecked((int)0x80070020) || io.HResult == 32 => "The file is open in another program (Excel?) - close it and try again.",
            IOException io when io.HResult == unchecked((int)0x80070070) => "The disk is full.",
            _ => ex.Message,
        };
    }
}
