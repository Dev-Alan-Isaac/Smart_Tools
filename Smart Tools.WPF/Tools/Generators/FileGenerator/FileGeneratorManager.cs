using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// File Generator engine: plans names and sizes, checks the folder and
    /// free space, writes the files (FileGeneratorManager.Formats.cs),
    /// archives them, exports the report, and keeps the settings. No WPF, so
    /// it's tested in a plain test project.
    ///
    /// Fixed against the old FileGeneratorManager / view:
    ///  - Cancelling (or a full disk) left a half-written file behind. Files
    ///    are written under a hidden temporary name and only get their real
    ///    name when complete; a cancelled or failed file is deleted.
    ///  - A repeated pattern restarted every 8 KB (8192 isn't a multiple of
    ///    most pattern lengths), so "ABC" became "...ABAB C...".
    ///  - CSV / JSON / XML stopped up to a row short of the size asked for;
    ///    now they're exact (and still valid).
    ///  - A metadata tag with a tab, line break or quote made the JSON / XML
    ///    invalid (only \ and " / &amp; &lt; &gt; were escaped).
    ///  - DOCX was capped at 2 GB without saying so.
    ///  - No free-space check: a big batch filled the disk and failed half way.
    ///  - "file10" sorted before "file2" (numbers weren't padded).
    ///  - A name or extension Windows can't use failed every file with an
    ///    exception; it's now explained before anything is written.
    ///  - A big file showed no progress until it was done (progress is now
    ///    in bytes, about 10 times a second).
    ///  - The view exported CSV when JSON was picked (and JSON for CSV), and
    ///    treated the "No folder selected" text as a folder name.
    /// </summary>
    public static partial class FileGeneratorManager
    {
        /// <summary>Where files go until the user picks another folder: Downloads, in Smart Tools / File Generator.</summary>
        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Smart Tools", "File Generator");

        public const int MaxCount = 100_000;
        public const long MaxFileBytes = 1L << 40;   // 1 TB
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

        /// <summary>Unit choices in the size boxes, in order.</summary>
        public static readonly IReadOnlyList<(string Name, long Bytes)> Units = new[]
        {
            ("B", 1L), ("KB", 1024L), ("MB", 1024L * 1024), ("GB", 1024L * 1024 * 1024),
        };

        // =================================================================
        //  Reading the sidebar
        // =================================================================

        /// <summary>"1.5" + MB → 1,572,864. Accepts "1,5" too. Null when it isn't a number ≥ 0.</summary>
        public static long? ParseSize(string? text, int unitIndex)
        {
            string t = (text ?? "").Trim().Replace(" ", "");
            if (t.Length == 0) return null;
            if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0 || !double.IsFinite(value)) return null;
            long unit = Units[Math.Clamp(unitIndex, 0, Units.Count - 1)].Bytes;
            double bytes = Math.Round(value * unit);
            return bytes > MaxFileBytes ? MaxFileBytes + 1 : (long)bytes;
        }

        public static string Extension(FileGenOptions o) => o.Format switch
        {
            FileGenFormat.Custom => "." + (o.CustomExtension ?? "").Trim().TrimStart('.'),
            _ => "." + o.Format.ToString().ToLowerInvariant(),
        };

        /// <summary>What a format is, for the hint under the chips.</summary>
        public static string Describe(FileGenFormat f) => f switch
        {
            FileGenFormat.Txt => "Plain text in the chosen fill. Exact size.",
            FileGenFormat.Csv => "A table with Id, Name, Value and Timestamp columns. Exact size; opens in Excel.",
            FileGenFormat.Json => "A JSON list of items. Exact size; valid JSON.",
            FileGenFormat.Xml => "An XML list of items. Exact size; valid XML.",
            FileGenFormat.Pdf => "A one-page PDF that opens in any reader. Exact size from about 1 KB.",
            FileGenFormat.Docx => "A Word document with one line of text. Exact size from about 2 KB.",
            FileGenFormat.Bin => "Raw bytes in the chosen fill. Exact size.",
            FileGenFormat.Dat => "Raw bytes in the chosen fill. Exact size.",
            FileGenFormat.Jpg => "A 640×480 photo of one random colour. Exact size from about 2 KB.",
            FileGenFormat.Png => "A 640×480 image of one random colour. Exact size from about 2 KB.",
            FileGenFormat.Mp4 => "A valid MP4 container with no video in it - players open it as empty. Exact size.",
            _ => "Raw bytes with your own extension. Exact size.",
        };

        /// <summary>The fill matters for these formats; the others always contain their own structure.</summary>
        public static bool UsesFill(FileGenFormat f) => f is FileGenFormat.Txt or FileGenFormat.Bin or FileGenFormat.Dat or FileGenFormat.Custom
            or FileGenFormat.Csv or FileGenFormat.Json or FileGenFormat.Xml;

        // =================================================================
        //  Names
        // =================================================================

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly char[] BadNameChars = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

        /// <summary>The file name for the n-th file (0-based), before making it unique.</summary>
        public static string FileName(FileGenOptions o, int index)
        {
            long number = (long)o.StartIndex + index;
            int width = o.PadNumbers && o.Count > 1 ? Math.Max(1, ((long)o.StartIndex + o.Count - 1).ToString(CultureInfo.InvariantCulture).Length) : 1;
            string num = number < 0 ? number.ToString(CultureInfo.InvariantCulture) : number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            string ext = Extension(o);
            return o.Naming == FileGenNaming.Sequential
                ? $"{o.BaseName}{num}{ext}"
                : $"{o.Prefix}{o.BaseName}{o.Suffix}{(o.Count > 1 ? num : "")}{ext}";
        }

        /// <summary>Why Windows wouldn't accept this file name (null when it's fine).</summary>
        public static string? CheckFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name))) return "The name is empty.";
            int bad = name.IndexOfAny(BadNameChars);
            if (bad >= 0) return $"'{name[bad]}' can't be used in a file name.";
            if (name.EndsWith('.') || name.EndsWith(' ')) return "A file name can't end with a dot or a space.";
            string stem = name.Split('.')[0].TrimEnd();
            if (ReservedNames.Contains(stem)) return $"\"{stem}\" is a name Windows reserves - pick another.";
            if (name.Length > 200) return "The name is too long.";
            return null;
        }

        // =================================================================
        //  Plan: names, sizes, free space
        // =================================================================

        /// <summary>
        /// The live check under "Save to": first and last names, total size,
        /// free space - or the reason it can't run. Cheap; probes the disk.
        /// </summary>
        public static FileGenPlan CheckPlan(FileGenOptions o)
        {
            if (o.Count < 1) return new FileGenPlan { Error = "Make at least one file." };
            if (o.Count > MaxCount) return new FileGenPlan { Error = $"At most {MaxCount:N0} files at a time." };
            if (o.Format == FileGenFormat.Custom)
            {
                string ext = (o.CustomExtension ?? "").Trim().TrimStart('.');
                if (ext.Length == 0) return new FileGenPlan { Error = "Type an extension, like log or cfg." };
                if (ext.Length > 16 || !ext.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
                    return new FileGenPlan { Error = "Use only letters, numbers, - and _ in the extension." };
            }
            long max = o.SizeMode == FileGenSizeMode.Exact ? o.ExactSizeBytes : Math.Max(o.MinSizeBytes, o.MaxSizeBytes);
            if (max > MaxFileBytes) return new FileGenPlan { Error = $"Each file can be at most {FileGenText.Bytes(MaxFileBytes)}." };
            if (o.Format == FileGenFormat.Docx && max > 4L * 1024 * 1024 * 1024 - 65536)
                return new FileGenPlan { Error = "A Word file can be at most 4 GB." };

            string first = FileName(o, 0), last = FileName(o, o.Count - 1);
            string? nameError = CheckFileName(first) ?? CheckFileName(last);
            if (nameError != null) return new FileGenPlan { Error = nameError, FirstName = first, LastName = last };

            if (string.IsNullOrWhiteSpace(o.OutputFolder)) return new FileGenPlan { Error = "Choose where to save the files.", FirstName = first, LastName = last };
            string folder;
            try
            {
                folder = Path.GetFullPath(o.OutputFolder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new FileGenPlan { Error = "That folder path isn't valid.", FirstName = first, LastName = last };
            }
            string? existing = folder;
            while (existing != null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
            if (existing == null) return new FileGenPlan { Error = "That drive isn't there.", FirstName = first, LastName = last };

            long total = 0;
            try
            {
                total = checked(max * o.Count);
            }
            catch (OverflowException)
            {
                total = long.MaxValue;
            }
            long archiveExtra = o.Archive == FileGenArchive.None ? 0 : total;   // the archive is as big as the files (random data doesn't compress)
            long needed = total == long.MaxValue ? long.MaxValue : total + archiveExtra;

            long free = -1;
            try
            {
                free = new DriveInfo(Path.GetPathRoot(existing)!).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
            }
            if (free >= 0 && needed > free)
                return new FileGenPlan
                {
                    Error = $"Not enough free space: this needs up to {FileGenText.Bytes(needed)} and the drive has {FileGenText.Bytes(free)} free.",
                    FirstName = first, LastName = last, TotalBytesMax = total, FreeBytes = free,
                };
            string? warning = free >= 0 && needed > free * 0.9 ? "This will almost fill the drive." : null;
            if (warning == null && !Directory.Exists(folder)) warning = "The folder will be created.";
            return new FileGenPlan { FirstName = first, LastName = last, TotalBytesMax = total, FreeBytes = free, Warning = warning };
        }

        /// <summary>
        /// The rows to create: unique names (never overwriting a file that's
        /// there, nor repeating a name in the batch) and each file's size.
        /// </summary>
        public static List<FileGenItem> PlanItems(FileGenOptions o)
        {
            var items = new List<FileGenItem>(o.Count);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string folder = Path.GetFullPath(o.OutputFolder);
            long min = Math.Min(o.MinSizeBytes, o.MaxSizeBytes), max = Math.Max(o.MinSizeBytes, o.MaxSizeBytes);
            for (int i = 0; i < o.Count; i++)
            {
                string path = UniquePath(Path.Combine(folder, FileName(o, i)), taken);
                taken.Add(path);
                items.Add(new FileGenItem
                {
                    Name = Path.GetFileName(path),
                    FullPath = path,
                    Format = o.Format,
                    TargetBytes = o.SizeMode == FileGenSizeMode.Exact ? o.ExactSizeBytes : Random.Shared.NextInt64(min, max + 1),
                });
            }
            return items;
        }

        /// <summary>"name.txt", then "name (2).txt", "name (3).txt"…</summary>
        public static string UniquePath(string path, ISet<string>? alsoTaken = null)
        {
            bool Taken(string p) => File.Exists(p) || Directory.Exists(p) || (alsoTaken?.Contains(p) ?? false);
            if (!Taken(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!Taken(candidate)) return candidate;
            }
        }

        // =================================================================
        //  Generating
        // =================================================================

        /// <summary>
        /// Writes every planned file, one after another. Each is written
        /// under a hidden temporary name and renamed when complete, so a
        /// cancel or a full disk never leaves a half file. Row updates are
        /// posted to the caller's thread (the UI); progress is in bytes.
        /// </summary>
        public static Task<FileGenSummary> GenerateAsync(FileGenOptions o, IReadOnlyList<FileGenItem> items, IProgress<FileGenProgress>? progress, CancellationToken token)
        {
            var ui = SynchronizationContext.Current;
            void OnUi(Action a)
            {
                if (ui == null) a();
                else ui.Post(_ => a(), null);
            }

            return Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                Directory.CreateDirectory(o.OutputFolder);
                long total = items.Sum(i => i.TargetBytes), before = 0;
                var lastReport = Stopwatch.StartNew();
                var created = new List<FileGenItem>();
                bool cancelled = false;
                DateTime now = DateTime.Now;

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        for (int j = i; j < items.Count; j++)
                        {
                            var rest = items[j];
                            OnUi(() => rest.Status = FileGenStatus.Cancelled);
                        }
                        break;
                    }
                    OnUi(() => item.Status = FileGenStatus.Creating);
                    progress?.Report(new FileGenProgress(i, items.Count, item.Name, 0, item.TargetBytes, before, total));

                    string temp = Path.Combine(Path.GetDirectoryName(item.FullPath)!, $".{Path.GetFileName(item.FullPath)}.{Guid.NewGuid():N}.part");
                    long baseDone = before;
                    try
                    {
                        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                        {
                            WriteContent(fs, item.TargetBytes, o, now, written =>
                            {
                                if (lastReport.Elapsed < ReportEvery) return;
                                lastReport.Restart();
                                progress?.Report(new FileGenProgress(i, items.Count, item.Name, written, item.TargetBytes, baseDone + Math.Min(written, item.TargetBytes), total));
                            }, token);
                        }
                        string final = File.Exists(item.FullPath) ? UniquePath(item.FullPath) : item.FullPath;   // someone made one meanwhile
                        File.Move(temp, final);
                        long size = new FileInfo(final).Length;
                        OnUi(() =>
                        {
                            item.SizeBytes = size;
                            item.Status = FileGenStatus.Created;
                            if (final != item.FullPath) item.Message = Loc.F("Str_FileGen_MsgSavedAs", "Saved as {0}", Path.GetFileName(final));
                        });
                        created.Add(item);
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(temp);
                        cancelled = true;
                        for (int j = i; j < items.Count; j++)
                        {
                            var rest = items[j];
                            OnUi(() => rest.Status = FileGenStatus.Cancelled);
                        }
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
                    {
                        TryDelete(temp);
                        string message = PlainError(ex);
                        OnUi(() =>
                        {
                            item.Status = FileGenStatus.Failed;
                            item.Message = message;
                        });
                        if (IsDiskFull(ex))
                        {
                            for (int j = i + 1; j < items.Count; j++)
                            {
                                var rest = items[j];
                                OnUi(() =>
                                {
                                    rest.Status = FileGenStatus.Cancelled;
                                    rest.Message = Loc.T("Str_FileGen_MsgDiskFull", "Stopped: the disk is full.");
                                });
                            }
                            break;
                        }
                    }
                    before += item.TargetBytes;
                }
                progress?.Report(new FileGenProgress(items.Count, items.Count, "", 0, 0, cancelled ? before : total, total));

                string? archivePath = null, archiveError = null;
                if (!cancelled && o.Archive != FileGenArchive.None && created.Count > 0)
                {
                    try
                    {
                        progress?.Report(new FileGenProgress(items.Count, items.Count, "Packing the archive…", 0, 0, total, total));
                        archivePath = BuildArchive(created.Select(c => c.FullPath).Where(File.Exists).ToList(), o, token);
                        if (!o.KeepFilesAfterArchive)
                        {
                            foreach (var c in created)
                            {
                                TryDelete(c.FullPath);
                                OnUi(() => c.Message = Loc.F("Str_FileGen_MsgPackedItem", "Packed into {0}", Path.GetFileName(archivePath)));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        archiveError = "Archiving was cancelled - the files are still there.";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        archiveError = "The files were created, but the archive couldn't be made: " + PlainError(ex);
                    }
                }

                return new FileGenSummary
                {
                    Items = items,
                    ArchivePath = archivePath,
                    ArchiveError = archiveError,
                    Cancelled = cancelled,
                    Elapsed = clock.Elapsed,
                };
            }, CancellationToken.None);
        }

        private static bool IsDiskFull(Exception ex) => ex is IOException io && ((io.HResult & 0xFFFF) is 39 or 112 || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase));

        public static string PlainError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows didn't allow writing there.",
            IOException io when IsDiskFull(io) => "The disk is full.",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "The file is in use by another program.",
            PathTooLongException => "The path is too long.",
            _ => ex.Message,
        };

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>ZIP (stored - test data doesn't compress, so this is fast) or TAR, written under a temporary name.</summary>
        public static string BuildArchive(IReadOnlyList<string> files, FileGenOptions o, CancellationToken token)
        {
            string name = o.Archive == FileGenArchive.Zip ? "generated-files.zip" : "generated-files.tar";
            string path = UniquePath(Path.Combine(o.OutputFolder, name));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
                {
                    if (o.Archive == FileGenArchive.Zip)
                    {
                        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                        foreach (var f in files)
                        {
                            token.ThrowIfCancellationRequested();
                            zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Fastest);
                        }
                    }
                    else
                    {
                        using var tar = new TarWriter(fs);
                        foreach (var f in files)
                        {
                            token.ThrowIfCancellationRequested();
                            tar.WriteEntry(f, Path.GetFileName(f));
                        }
                    }
                }
                File.Move(temp, path);
                return path;
            }
            finally
            {
                TryDelete(temp);
            }
        }

        public static string DescribeSummary(FileGenSummary s)
        {
            var parts = new List<string> { Loc.N((int)Math.Min(s.Created, int.MaxValue), "Str_FileGen_MsgCreatedOne", "1 file created ({1})", "Str_FileGen_MsgCreatedMany", "{0:N0} files created ({1})", s.Created, FileGenText.Bytes(s.TotalBytes)) };
            if (s.Failed > 0) parts.Add(Loc.F("Str_FileGen_MsgFailedN", "{0:N0} failed", s.Failed));
            if (s.ArchivePath != null) parts.Add(Loc.F("Str_FileGen_MsgPackedInto", "packed into {0}", Path.GetFileName(s.ArchivePath)));
            parts.Add(s.Elapsed.TotalSeconds < 1 ? $"{s.Elapsed.TotalMilliseconds:0} ms" : $"{s.Elapsed.TotalSeconds:0.0} s");
            string text = string.Join(" · ", parts) + ".";
            return s.Cancelled ? Loc.T("Str_FileGen_MsgStopped", "Stopped.") + " " + text : text;
        }

        // =================================================================
        //  Report (JSON / CSV / XML)
        // =================================================================

        public static Task ExportReportAsync(IReadOnlyList<FileGenItem> items, string path, FileGenReportFormat format) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Directory.CreateDirectory(folder);
                string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(temp, ReportText(items, format), new UTF8Encoding(format == FileGenReportFormat.Csv));
                    File.Move(temp, path, overwrite: true);   // the Save dialog already asked about replacing
                }
                finally
                {
                    TryDelete(temp);
                }
            });

        public static string ReportText(IReadOnlyList<FileGenItem> items, FileGenReportFormat format)
        {
            switch (format)
            {
                case FileGenReportFormat.Json:
                    var rows = items.Select(i => new
                    {
                        name = i.Name,
                        path = i.FullPath,
                        format = i.Format.ToString().ToLowerInvariant(),
                        targetBytes = i.TargetBytes,
                        sizeBytes = i.Status == FileGenStatus.Created ? i.SizeBytes : 0,
                        size = i.SizeDisplay,
                        status = i.Status.ToString(),
                        note = i.Message.Length > 0 ? i.Message : i.SizeNote,
                    });
                    return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
                case FileGenReportFormat.Xml:
                    var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Files>\n");
                    foreach (var i in items)
                        sb.Append($"  <File name=\"{XmlText(i.Name)}\" format=\"{i.Format.ToString().ToLowerInvariant()}\" targetBytes=\"{i.TargetBytes}\" sizeBytes=\"{(i.Status == FileGenStatus.Created ? i.SizeBytes : 0)}\" status=\"{i.Status}\" note=\"{XmlText(i.Message.Length > 0 ? i.Message : i.SizeNote)}\">{XmlText(i.FullPath)}</File>\n");
                    return sb.Append("</Files>\n").ToString();
                default:
                    var csv = new StringBuilder("Name,Folder,Format,Target bytes,Size bytes,Size,Status,Note\r\n");
                    foreach (var i in items)
                        csv.Append(string.Join(",", Cell(i.Name), Cell(Path.GetDirectoryName(i.FullPath) ?? ""), i.Format.ToString().ToLowerInvariant(),
                            i.TargetBytes, i.Status == FileGenStatus.Created ? i.SizeBytes : 0, Cell(i.SizeDisplay), i.Status, Cell(i.Message.Length > 0 ? i.Message : i.SizeNote))).Append("\r\n");
                    return csv.ToString();
            }
        }

        /// <summary>CSV cell: quoted as needed; = + - @ at the start gets an apostrophe so Excel can't run it.</summary>
        public static string Cell(string value)
        {
            string v = value ?? "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            return CsvValue(v);
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\file_generator.json - no folder)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "file_generator.json");

        public static FileGenSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new FileGenSettings();
                var s = JsonSerializer.Deserialize<FileGenSettings>(File.ReadAllText(file), SettingsJson) ?? new FileGenSettings();
                if (!Enum.IsDefined(s.Format)) s.Format = FileGenFormat.Txt;
                if (!Enum.IsDefined(s.SizeMode)) s.SizeMode = FileGenSizeMode.Exact;
                if (!Enum.IsDefined(s.Fill)) s.Fill = FileGenFill.RandomBytes;
                if (!Enum.IsDefined(s.Naming)) s.Naming = FileGenNaming.Sequential;
                if (!Enum.IsDefined(s.Archive)) s.Archive = FileGenArchive.None;
                if (!Enum.IsDefined(s.ReportFormat)) s.ReportFormat = FileGenReportFormat.Json;
                s.ExactUnit = Math.Clamp(s.ExactUnit, 0, Units.Count - 1);
                s.MinUnit = Math.Clamp(s.MinUnit, 0, Units.Count - 1);
                s.MaxUnit = Math.Clamp(s.MaxUnit, 0, Units.Count - 1);
                s.Count = Math.Clamp(s.Count, 1, MaxCount);
                s.CustomExtension ??= "dat";
                s.ExactSize ??= "1";
                s.MinSize ??= "1";
                s.MaxSize ??= "10";
                s.PatternText ??= "SmartTools";
                s.MetadataTag ??= "";
                s.BaseName ??= "file";
                s.Prefix ??= "";
                s.Suffix ??= "";
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new FileGenSettings();
            }
        }

        public static void SaveSettings(FileGenSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
