using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Validate Files - planning and pure logic (no disk writes except
    /// settings and reports): file kinds, header signatures, the file list,
    /// wording, CSV reports, settings. The checks themselves are in
    /// ValidateManager.Checks.cs (+ .Media.cs for ffmpeg, .Images.cs for
    /// pictures); folders, running a scan and quarantine in .Run.cs.
    /// </summary>
    public sealed partial class ValidateManager
    {
        // =================================================================
        //  File kinds
        // =================================================================

        public static readonly IReadOnlySet<string> ImageExts = Set(".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".heic", ".heif", ".avif");
        public static readonly IReadOnlySet<string> VideoExts = Set(".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp");
        public static readonly IReadOnlySet<string> AudioExts = Set(".mp3", ".wav", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif");
        public static readonly IReadOnlySet<string> DocumentExts = Set(".pdf", ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".doc", ".xls", ".ppt", ".odt", ".ods", ".odp", ".rtf", ".txt");
        public static readonly IReadOnlySet<string> ArchiveExts = Set(".zip", ".gz", ".tgz", ".tar", ".7z", ".rar", ".bz2", ".xz");

        private static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);

        public static ValidationKind CategoryOf(string path)
        {
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path);
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ValidationKind.Archive;
            if (ImageExts.Contains(ext)) return ValidationKind.Image;
            if (VideoExts.Contains(ext)) return ValidationKind.Video;
            if (AudioExts.Contains(ext)) return ValidationKind.Audio;
            if (DocumentExts.Contains(ext)) return ValidationKind.Document;
            if (ArchiveExts.Contains(ext)) return ValidationKind.Archive;
            return ValidationKind.Other;
        }

        /// <summary>
        /// Whether a file gets its format checks. Auto: by kind, if that kind
        /// is ticked. A chosen type: only files of that type are listed at
        /// all (the old version ran e.g. video checks on every photo in the
        /// folder and called them all corrupted).
        /// </summary>
        public static bool WantsFormatChecks(ValidationKind category, ValidationOptions o) => o.Type switch
        {
            ValidationType.Auto => category switch
            {
                ValidationKind.Video => o.AutoVideo,
                ValidationKind.Image => o.AutoImage,
                ValidationKind.Audio => o.AutoAudio,
                ValidationKind.Document => o.AutoDocument,
                ValidationKind.Archive => o.AutoArchive,
                _ => false,
            },
            _ => (int)category == (int)o.Type,
        };

        public static bool FitsType(ValidationKind category, ValidationType type) =>
            type == ValidationType.Auto || (int)category == (int)type;

        // =================================================================
        //  Signatures
        // =================================================================

        /// <summary>What the first bytes say the file really is ("PNG", "ZIP"...), or null.</summary>
        public static string? Sniff(byte[] d)
        {
            bool At(int offset, string ascii) => d.Length >= offset + ascii.Length && Encoding.ASCII.GetString(d, offset, ascii.Length) == ascii;
            bool Bytes(params byte[] b) => d.Length >= b.Length && d.AsSpan(0, b.Length).SequenceEqual(b);

            if (Bytes(0xFF, 0xD8, 0xFF)) return "JPEG";
            if (Bytes(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "PNG";
            if (At(0, "GIF8")) return "GIF";
            if (At(0, "RIFF") && At(8, "WEBP")) return "WEBP";
            if (At(0, "RIFF") && At(8, "WAVE")) return "WAV";
            if (At(0, "RIFF") && At(8, "AVI ")) return "AVI";
            if (Bytes(0x49, 0x49, 0x2A, 0x00) || Bytes(0x4D, 0x4D, 0x00, 0x2A)) return "TIFF";
            if (At(4, "ftyp"))
            {
                string brand = d.Length >= 12 ? Encoding.ASCII.GetString(d, 8, 4) : "";
                return brand switch
                {
                    "heic" or "heix" or "hevc" or "mif1" or "msf1" => "HEIC",
                    "avif" or "avis" => "AVIF",
                    "M4A " or "M4B " => "M4A",
                    "qt  " => "MOV",
                    _ => "MP4",
                };
            }
            if (Bytes(0x1A, 0x45, 0xDF, 0xA3)) return "MKV";
            if (Bytes(0x30, 0x26, 0xB2, 0x75)) return "ASF";
            if (At(0, "FLV")) return "FLV";
            if (At(0, "%PDF-")) return "PDF";
            if (Bytes(0x50, 0x4B, 0x03, 0x04) || Bytes(0x50, 0x4B, 0x05, 0x06)) return "ZIP";
            if (Bytes(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)) return "OLE";
            if (At(0, "Rar!")) return "RAR";
            if (Bytes(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "7Z";
            if (Bytes(0x1F, 0x8B)) return "GZIP";
            if (At(0, "BZh")) return "BZIP2";
            if (Bytes(0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) return "XZ";
            if (At(0, "fLaC")) return "FLAC";
            if (At(0, "OggS")) return "OGG";
            if (At(0, "ID3")) return "MP3";
            if (At(0, "FORM") && (At(8, "AIFF") || At(8, "AIFC"))) return "AIFF";
            if (At(0, "{\\rtf")) return "RTF";
            if (At(257, "ustar")) return "TAR";
            if (At(0, "BM") && d.Length >= 14) return "BMP";
            if (Bytes(0x00, 0x00, 0x01, 0x00) || Bytes(0x00, 0x00, 0x02, 0x00)) return "ICO";
            return null;
        }

        /// <summary>Whether the header fits the extension (unknown extensions always fit).</summary>
        public static bool HeaderMatches(string path, byte[] d)
        {
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string? kind = Sniff(d);
            bool QuickTimeAtom() => d.Length >= 8 && Encoding.ASCII.GetString(d, 4, 4) is "moov" or "mdat" or "wide" or "free" or "skip" or "pnot";

            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return kind == "GZIP";
            return ext switch
            {
                ".jpg" or ".jpeg" or ".jpe" => kind == "JPEG",
                ".png" => kind == "PNG",
                ".gif" => kind == "GIF",
                ".bmp" => kind == "BMP",
                ".tif" or ".tiff" => kind == "TIFF",
                ".webp" => kind == "WEBP",
                ".ico" => kind == "ICO",
                ".heic" or ".heif" or ".avif" => kind is "HEIC" or "AVIF" or "MP4",

                // QuickTime files often start with other atoms than ftyp (old version called them corrupted).
                ".mp4" or ".m4v" or ".mov" or ".3gp" => kind is "MP4" or "MOV" or "M4A" || QuickTimeAtom(),
                ".m4a" => kind is "M4A" or "MP4" || QuickTimeAtom(),
                ".mkv" or ".webm" => kind == "MKV",
                ".avi" => kind == "AVI",
                ".wmv" or ".asf" or ".wma" => kind == "ASF",
                ".flv" => kind == "FLV",
                ".mpg" or ".mpeg" => d.Length >= 4 && d[0] == 0 && d[1] == 0 && d[2] == 1 && (d[3] == 0xBA || d[3] == 0xB3),
                ".ts" or ".m2ts" or ".mts" => d.Length >= 1 && d[0] == 0x47 || d.Length >= 5 && d[4] == 0x47,

                ".mp3" => kind == "MP3" || (d.Length >= 2 && d[0] == 0xFF && (d[1] & 0xE0) == 0xE0),
                ".aac" => kind == "MP3" || (d.Length >= 2 && d[0] == 0xFF && (d[1] & 0xF6) == 0xF0) || (d.Length >= 4 && Encoding.ASCII.GetString(d, 0, 4) == "ADIF"),
                ".wav" => kind == "WAV",
                ".flac" => kind == "FLAC" || (kind == "MP3"),   // FLAC may carry an ID3 tag first
                ".ogg" or ".oga" or ".opus" => kind == "OGG",
                ".aiff" or ".aif" => kind == "AIFF",

                ".pdf" => kind == "PDF" || Encoding.ASCII.GetString(d).Contains("%PDF-", StringComparison.Ordinal),
                ".docx" or ".xlsx" or ".pptx" or ".docm" or ".xlsm" or ".pptm" or ".odt" or ".ods" or ".odp" => kind is "ZIP" or "OLE",
                ".doc" or ".xls" or ".ppt" => kind == "OLE",
                ".rtf" => kind == "RTF",

                ".zip" => kind == "ZIP",
                ".gz" or ".tgz" => kind == "GZIP",
                ".tar" => kind == "TAR",
                ".7z" => kind == "7Z",
                ".rar" => kind == "RAR",
                ".bz2" => kind == "BZIP2",
                ".xz" => kind == "XZ",
                _ => true,
            };
        }

        /// <summary>
        /// "It's really a PNG" when the header belongs to a different known
        /// format - a renamed file isn't a corrupted one (the old version
        /// said "corrupted").
        /// </summary>
        public static string? WrongExtensionNote(string path, byte[] header)
        {
            if (HeaderMatches(path, header)) return null;
            string? kind = Sniff(header);
            return kind == null ? null : $"it's really a {kind} file with a {Path.GetExtension(path).ToLowerInvariant()} name";
        }

        // =================================================================
        //  PDF trailer
        // =================================================================

        /// <summary>
        /// Whether the last trailer dictionary has /Encrypt. Dictionaries are
        /// matched by nesting (the old IndexOf("&gt;&gt;") stopped at the
        /// first inner dictionary and missed /Encrypt after it).
        /// </summary>
        public static bool PdfTrailerDeclaresEncrypt(string tail)
        {
            int trailer = tail.LastIndexOf("trailer", StringComparison.Ordinal);
            if (trailer < 0)
            {
                // Cross-reference streams (PDF 1.5+): /Encrypt sits in the xref stream's dictionary near the end.
                int xref = tail.LastIndexOf("/XRef", StringComparison.Ordinal);
                if (xref < 0) return tail.Contains("/Encrypt", StringComparison.Ordinal);
                int open = tail.LastIndexOf("<<", xref, StringComparison.Ordinal);
                return open >= 0 && BalancedDict(tail, open) is string d && d.Contains("/Encrypt", StringComparison.Ordinal);
            }
            int start = tail.IndexOf("<<", trailer, StringComparison.Ordinal);
            return start >= 0 && BalancedDict(tail, start) is string dict && dict.Contains("/Encrypt", StringComparison.Ordinal);
        }

        private static string? BalancedDict(string s, int start)
        {
            int depth = 0;
            for (int i = start; i < s.Length - 1; i++)
            {
                if (s[i] == '<' && s[i + 1] == '<')
                {
                    depth++;
                    i++;
                }
                else if (s[i] == '>' && s[i + 1] == '>')
                {
                    depth--;
                    i++;
                    if (depth == 0) return s[start..(i + 1)];
                }
            }
            return null;
        }

        /// <summary>The byte offset after the last "startxref", or null.</summary>
        public static long? PdfStartXref(string tail)
        {
            int i = tail.LastIndexOf("startxref", StringComparison.Ordinal);
            if (i < 0) return null;
            var digits = new string(tail[(i + 9)..].SkipWhile(char.IsWhiteSpace).TakeWhile(char.IsDigit).ToArray());
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : null;
        }

        // =================================================================
        //  The file list
        // =================================================================

        /// <summary>A listed file.</summary>
        public sealed record QueuedFile(string FullPath, string? Root, long Length);

        public sealed class ListResult
        {
            public List<FileValidationResult> Items { get; } = new();

            /// <summary>Not of the chosen type (only when a type is picked).</summary>
            public int OtherType { get; set; }
        }

        /// <summary>
        /// Rows for the grid, before scanning, so the list is there at once
        /// and fills in live (the old view showed nothing until the whole
        /// scan finished, and lost everything on Cancel). Over-size files
        /// are listed as Skipped instead of silently vanishing.
        /// </summary>
        public static ListResult BuildList(IEnumerable<QueuedFile> files, ValidationOptions o, ISet<string>? hidden = null)
        {
            var result = new ListResult();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (!seen.Add(f.FullPath)) continue;
                if (hidden != null && hidden.Contains(f.FullPath)) continue;
                var category = CategoryOf(f.FullPath);
                if (!FitsType(category, o.Type))
                {
                    result.OtherType++;
                    continue;
                }

                var row = new FileValidationResult { FullPath = f.FullPath, SizeBytes = f.Length, Category = category };
                if (o.SkipFilesLargerThanBytes > 0 && f.Length > o.SkipFilesLargerThanBytes)
                {
                    row.Status = ValidationStatus.Skipped;
                    row.Details = $"Larger than the {ValidateText.FormatBytes(o.SkipFilesLargerThanBytes)} limit.";
                }
                result.Items.Add(row);
            }
            result.Items.Sort((a, b) =>
            {
                int c = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>Rows a scan checks (also re-checks the ones that were cancelled).</summary>
        public static bool IsPending(FileValidationResult r) => r.Status is ValidationStatus.Pending or ValidationStatus.Cancelled;

        /// <summary>Sidebar rows: each added folder, then added files grouped by folder.</summary>
        public static List<ValidationQueueGroup> BuildQueueGroups(IEnumerable<ValidationSource> sources, bool includeSubfolders)
        {
            var list = sources.ToList();
            var groups = list.Where(s => s.IsFolder)
                .Select(s => new ValidationQueueGroup { FolderPath = s.FullPath, IsFolder = true, IncludeSubfolders = includeSubfolders })
                .ToList();
            groups.AddRange(list.Where(s => !s.IsFolder)
                .GroupBy(s => Path.GetDirectoryName(s.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new ValidationQueueGroup { FolderPath = g.Key, FilePaths = g.Select(s => s.FullPath).ToList() }));
            return groups;
        }

        public static long SkipBytes(int value, SizeUnit unit) => value <= 0 ? 0 : value * unit switch
        {
            SizeUnit.KB => 1024L,
            SizeUnit.GB => 1024L * 1024 * 1024,
            SizeUnit.TB => 1024L * 1024 * 1024 * 1024,
            _ => 1024L * 1024,
        };

        // =================================================================
        //  Wording
        // =================================================================

        /// <summary>"Checked 1,204 in 0:42 · 3 corrupted · 12 warnings".</summary>
        public static string DescribeRun(ValidationRunResult r)
        {
            var parts = new List<string> { $"Checked {r.Checked:N0} file{(r.Checked == 1 ? "" : "s")} in {ValidateText.FormatDuration(r.Elapsed)}" };
            if (r.Corrupted > 0) parts.Add($"{r.Corrupted:N0} corrupted");
            if (r.Warning > 0) parts.Add($"{r.Warning:N0} with warnings");
            if (r.Unknown > 0) parts.Add($"{r.Unknown:N0} couldn't be checked");
            if (r.Cancelled > 0) parts.Add($"{r.Cancelled:N0} not checked (cancelled)");
            if (r.Corrupted + r.Warning + r.Unknown == 0 && r.Checked > 0) parts.Add("no problems found");
            return string.Join(" · ", parts);
        }

        public static string TypeHint(ValidationType t) => t switch
        {
            ValidationType.Video => "Only videos are listed. Needs ffmpeg for stream and frame checks.",
            ValidationType.Image => "Only pictures are listed. Each one is fully decoded to find damage.",
            ValidationType.Audio => "Only audio files are listed. Needs ffmpeg to decode them.",
            ValidationType.Document => "Only documents are listed (PDF, Office, OpenDocument, RTF, text).",
            ValidationType.Archive => "Only archives are listed. ZIP, GZIP and TAR are fully tested; 7Z and RAR by header.",
            _ => "Every file is checked. Each kind uses its own settings - pick a kind to fine-tune them.",
        };

        // =================================================================
        //  Reports
        // =================================================================

        public static string ReportsFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smart Tools", "Validation Reports");

        public static string DefaultQuarantineFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smart Tools", "Quarantine");

        public static string BuildCsvReport(IEnumerable<FileValidationResult> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("File name,Folder,Type,Size (bytes),Status,Details,Full path");
            foreach (var r in rows)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(r.FileName),
                    CsvEscape(r.Folder),
                    CsvEscape(r.FileType),
                    r.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(r.StatusText),
                    CsvEscape(r.Details),
                    CsvEscape(r.CurrentPath)));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Quotes when needed, and defuses values Excel would run as a
        /// formula (a file named "=HYPERLINK(...)" is a real attack).
        /// </summary>
        public static string CsvEscape(string? value)
        {
            value ??= "";
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "validate_files.json");

        public static ValidationSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ValidationSettings();
                var s = JsonSerializer.Deserialize<ValidationSettings>(File.ReadAllText(file), JsonOptions) ?? new ValidationSettings();
                s.Options ??= new ValidationOptions();
                s.QuarantineFolder ??= "";
                s.SkipSizeValue = Math.Clamp(s.SkipSizeValue, 0, 999_999);
                return s;
            }
            catch (Exception)
            {
                return new ValidationSettings();
            }
        }

        public static void SaveSettings(ValidationSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
