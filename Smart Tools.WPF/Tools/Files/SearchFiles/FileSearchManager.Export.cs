using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  File Search: export, copy and settings.
    //  Fixed against the old view: the CSV had no BOM (Excel showed é as
    //  Ã©), matched text like "=HYPERLINK(...)" ran as a formula, and the
    //  file was written on the UI thread straight over the target. An export
    //  never replaces an earlier one: a taken name gets the date and time.
    // =====================================================================
    public static partial class FileSearchManager
    {
        /// <summary>Old call: CSV, or text (values in unique mode, else the report) for a .txt path. Returns the file actually written.</summary>
        public static Task<string> ExportAsync(IReadOnlyList<FileSearchResult> rows, string path, bool unique) =>
            ExportAsync(rows, path, unique,
                !string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase) ? FileSearchExportFormat.Csv
                : unique ? FileSearchExportFormat.TextValues : FileSearchExportFormat.TextReport);

        /// <summary>
        /// Writes the rows as CSV, a values-only text file or a text report
        /// (under a temporary name, then renamed). Never replaces an existing
        /// file: if the name is taken, the date and time are added to it
        /// (see <see cref="UniqueExportPath"/>). Returns the file actually written.
        /// </summary>
        public static Task<string> ExportAsync(IReadOnlyList<FileSearchResult> rows, string path, bool unique, FileSearchExportFormat format) =>
            ExportAsync(rows, path, unique, format, DateTime.Now);

        internal static Task<string> ExportAsync(IReadOnlyList<FileSearchResult> rows, string path, bool unique, FileSearchExportFormat format, DateTime now) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Directory.CreateDirectory(folder);
                string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var w = new StreamWriter(temp, false, new UTF8Encoding(true)))
                    {
                        switch (format)
                        {
                            case FileSearchExportFormat.TextValues:
                                w.Write(ValuesTextGrouped(rows));
                                break;
                            case FileSearchExportFormat.TextReport:
                                WriteReport(w, rows, unique);
                                break;
                            default:
                                WriteCsv(w, rows, unique);
                                break;
                        }
                    }
                    // Never over an earlier export. Another program could create the
                    // same name between the check and the move, so move without
                    // overwriting and try the next free name if that happens.
                    for (int attempt = 0; ; attempt++)
                    {
                        string target = UniqueExportPath(path, now);
                        try
                        {
                            File.Move(temp, target, overwrite: false);
                            return target;
                        }
                        catch (IOException) when (File.Exists(target) && attempt < 20)
                        {
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            });

        public const string ExportStampFormat = "yyyy-MM-dd HH-mm-ss";

        /// <summary>The name offered in the Save dialog, dated so each export gets its own file: "search-results 2026-10-07 14-32-05.csv".</summary>
        public static string ExportFileName(FileSearchExportFormat format, DateTime now)
        {
            string stamp = now.ToString(ExportStampFormat, CultureInfo.InvariantCulture);
            return format switch
            {
                FileSearchExportFormat.TextValues => $"search-values {stamp}.txt",
                FileSearchExportFormat.TextReport => $"search-report {stamp}.txt",
                _ => $"search-results {stamp}.csv",
            };
        }

        /// <summary>
        /// The path itself when nothing is there yet. Otherwise the date and
        /// time go on the end of the name ("results 2026-10-07 14-32-05.csv"),
        /// and " (2)", " (3)"... if even that is taken (two exports in the
        /// same second, or the name already carried that stamp).
        /// </summary>
        public static string UniqueExportPath(string path, DateTime now)
        {
            if (!File.Exists(path)) return path;
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string stamp = now.ToString(ExportStampFormat, CultureInfo.InvariantCulture);
            string baseName = name.EndsWith(stamp, StringComparison.Ordinal) ? name : $"{name} {stamp}";
            string candidate = Path.Combine(folder, baseName + ext);
            for (int n = 2; File.Exists(candidate); n++)
                candidate = Path.Combine(folder, $"{baseName} ({n}){ext}");
            return candidate;
        }

        private static void WriteCsv(TextWriter w, IReadOnlyList<FileSearchResult> rows, bool unique)
        {
            bool segments = rows.Any(r => r.Segments.Count > 0);
            w.WriteLine("Match,File,Folder,Line,Column,Type,Pattern" + (segments ? ",Parts" : "") + (unique ? ",Times found,Found in,Located in" : ""));
            foreach (var r in rows)
            {
                var cells = new List<string>
                {
                    CsvCell(r.MatchText), CsvCell(r.Name), CsvCell(r.Folder),
                    r.LineNumber > 0 ? r.LineNumber.ToString(CultureInfo.InvariantCulture) : "",
                    r.LineNumber > 0 ? r.Column.ToString(CultureInfo.InvariantCulture) : "",
                    r.KindLabel, CsvCell(r.PatternName),
                };
                if (segments) cells.Add(CsvCell(r.SegmentsText));
                if (unique)
                {
                    cells.Add(r.Occurrences.ToString(CultureInfo.InvariantCulture));
                    var (names, folders) = FoundInColumns(r);
                    cells.Add(CsvCell(names));
                    cells.Add(CsvCell(folders));
                }
                w.WriteLine(string.Join(",", cells));
            }
        }

        /// <summary>
        /// Unique mode: every file the value was found in (first one first). Names go in one
        /// cell and their folders in another, in the same order, each joined with " | ".
        /// </summary>
        private static (string Names, string Folders) FoundInColumns(FileSearchResult r)
        {
            var paths = new List<string> { r.FullPath };
            paths.AddRange(r.OtherFiles);
            string names = string.Join(" | ", paths.Select(Path.GetFileName));
            string folders = string.Join(" | ", paths.Select(p => Path.GetDirectoryName(p) ?? ""));
            int more = r.FileCount - paths.Count;
            if (more > 0)
            {
                names += $" | … and {more:N0} more";
                folders += " | …";
            }
            return (names, folders);
        }

        /// <summary>Readable report for Notepad or an email: where each match is, which pattern found it, and the match.</summary>
        private static void WriteReport(TextWriter w, IReadOnlyList<FileSearchResult> rows, bool unique)
        {
            foreach (var r in rows)
            {
                string where = r.LineNumber > 0 ? $" (line {r.LineNumber}, column {r.Column})" : "";
                string pattern = r.PatternName.Length > 0 ? $" [{r.PatternName}]" : "";
                w.WriteLine($"[{r.KindLabel}]{pattern} {r.FullPath}{where}");
                w.WriteLine("    " + r.MatchText);
                if (r.Segments.Count > 0) w.WriteLine("    " + r.SegmentsText);
                if (unique && r.Occurrences > 1)
                    w.WriteLine($"    found {r.Occurrences:N0} times in {r.FileCount:N0} {FileSearchText.Plural(r.FileCount, "file", "files")}");
                if (unique && r.FileCount > 1)
                {
                    w.WriteLine("    found in:");
                    w.WriteLine("        " + r.FullPath);
                    foreach (string other in r.OtherFiles) w.WriteLine("        " + other);
                    int more = r.FileCount - 1 - r.OtherFiles.Count;
                    if (more > 0) w.WriteLine($"        … and {more:N0} more");
                }
                w.WriteLine();
            }
        }

        /// <summary>
        /// Values only, each once. One pattern (or text mode): just the list.
        /// Several patterns: a heading per pattern ("Email (12)") with its
        /// values under it, so emails and phones don't end up mixed.
        /// </summary>
        public static string ValuesTextGrouped(IEnumerable<FileSearchResult> rows)
        {
            var list = rows.ToList();
            var groups = list.GroupBy(r => r.PatternName).ToList();
            if (groups.Count <= 1) return ValuesText(list);
            var sb = new StringBuilder();
            foreach (var g in groups)
            {
                string values = ValuesText(g);
                int count = values.Count(c => c == '\n');
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"{(g.Key.Length > 0 ? g.Key : Loc.T("Str_SearchFiles_GroupOther", "Other"))} ({count:N0})");
                sb.Append(values);
            }
            return sb.ToString();
        }

        /// <summary>The matched values, one per line, each once (in the order found) - for Copy values.</summary>
        public static string ValuesText(IEnumerable<FileSearchResult> rows)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (var r in rows)
                if (seen.Add(r.MatchText)) sb.AppendLine(r.MatchText);
            return sb.ToString();
        }

        /// <summary>Quotes as needed; a cell starting with = + - @ gets a leading apostrophe (unless it's just a number) so Excel can't run it.</summary>
        public static string CsvCell(string value)
        {
            string v = value ?? "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            bool quote = v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || v != v.Trim();
            return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        public static string PlainSaveError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => Loc.T("Str_SearchFiles_ErrSaveDenied", "Windows didn't allow saving there - pick another folder."),
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => Loc.T("Str_SearchFiles_ErrSaveOpen", "The file is open in another program (Excel?) - close it and try again."),
            _ => ex.Message,
        };

        // =================================================================
        //  Settings (%AppData%\Smart Tools\file_search.json - no folders)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "file_search.json");

        public static FileSearchSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new FileSearchSettings();
                var s = JsonSerializer.Deserialize<FileSearchSettings>(File.ReadAllText(file), SettingsJson) ?? new FileSearchSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = FileSearchMode.Text;
                if (!Enum.IsDefined(s.ExportFormat)) s.ExportFormat = FileSearchExportFormat.Csv;
                s.SearchText ??= "";
                s.CheckedSavedPatterns = (s.CheckedSavedPatterns ?? new()).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                s.CustomPatterns = (s.CustomPatterns ?? new()).Where(p => !string.IsNullOrEmpty(p)).Distinct().Take(100).ToList();
                s.CheckedCustomPatterns = (s.CheckedCustomPatterns ?? new()).Where(p => s.CustomPatterns.Contains(p)).ToList();
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new FileSearchSettings();
            }
        }

        public static void SaveSettings(FileSearchSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, SettingsJson);
    }
}
