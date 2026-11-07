using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Organize Files backend, split in four:
    /// <list type="bullet">
    /// <item>OrganizeManager.cs (this file) - planning with no side effects:
    /// folder names for each rule, the destination of every file, name
    /// clashes inside a batch, settings and the undo journal on disk.</item>
    /// <item>OrganizeManager.Run.cs - scanning the source folder, reading
    /// metadata in parallel, moving/copying safely, undo.</item>
    /// <item>OrganizeManager.Media.cs - ffprobe for video/audio rules.</item>
    /// <item>OrganizeManager.Images.cs - Magick.NET header reads for image rules.</item>
    /// </list>
    /// </summary>
    public sealed partial class OrganizeManager
    {
        public const int MaxSteps = 5;

        // =================================================================
        //  Rule choices (what the step combo lists)
        // =================================================================

        /// <summary>Document rules aren't listed: they need a document library that isn't in the app.</summary>
        public static IReadOnlyList<OrganizeCriterionChoice> Choices { get; } = new[]
        {
            new OrganizeCriterionChoice(OrganizeCriterion.Category, "Category", "General", "Images"),
            new OrganizeCriterionChoice(OrganizeCriterion.FileType, "Type (image / video / audio)", "General", "Videos"),
            new OrganizeCriterionChoice(OrganizeCriterion.Extension, "Extension", "General", "JPG"),
            new OrganizeCriterionChoice(OrganizeCriterion.Year, "Year", "General", "2024"),
            new OrganizeCriterionChoice(OrganizeCriterion.Month, "Month", "General", "07 July"),
            new OrganizeCriterionChoice(OrganizeCriterion.DateModified, "Date modified (day)", "General", "2024-07-14"),
            new OrganizeCriterionChoice(OrganizeCriterion.DateCreated, "Date created (day)", "General", "2024-07-14"),
            new OrganizeCriterionChoice(OrganizeCriterion.Size, "Size", "General", "1MB - 10MB"),
            new OrganizeCriterionChoice(OrganizeCriterion.FirstLetter, "First letter", "General", "V"),
            new OrganizeCriterionChoice(OrganizeCriterion.VideoResolution, "Resolution", "Video", "1080p"),
            new OrganizeCriterionChoice(OrganizeCriterion.VideoDuration, "Length", "Video", "5 - 15 min"),
            new OrganizeCriterionChoice(OrganizeCriterion.VideoCodec, "Codec", "Video", "H264"),
            new OrganizeCriterionChoice(OrganizeCriterion.VideoFrameRate, "Frame rate", "Video", "30fps"),
            new OrganizeCriterionChoice(OrganizeCriterion.AudioBitrate, "Bitrate", "Audio", "256 - 320kbps"),
            new OrganizeCriterionChoice(OrganizeCriterion.AudioDuration, "Length", "Audio", "1 - 5 min"),
            new OrganizeCriterionChoice(OrganizeCriterion.AudioCodec, "Codec", "Audio", "MP3"),
            new OrganizeCriterionChoice(OrganizeCriterion.AudioChannels, "Channels", "Audio", "Stereo"),
            new OrganizeCriterionChoice(OrganizeCriterion.ImageResolution, "Megapixels", "Image", "10 - 20MP"),
            new OrganizeCriterionChoice(OrganizeCriterion.ImageDimensions, "Dimensions", "Image", "4032x3024"),
            new OrganizeCriterionChoice(OrganizeCriterion.ImageFormat, "Format", "Image", "JPEG"),
            new OrganizeCriterionChoice(OrganizeCriterion.ImageColorDepth, "Colour depth", "Image", "8-bit"),
        };

        public static bool IsOffered(OrganizeCriterion c) => Choices.Any(x => x.Criterion == c);

        /// <summary>The kind of file a rule reads, or null for rules that work on any file.</summary>
        public static MediaKind? KindFor(OrganizeCriterion c) => c switch
        {
            OrganizeCriterion.VideoResolution or OrganizeCriterion.VideoDuration or OrganizeCriterion.VideoCodec or OrganizeCriterion.VideoFrameRate => MediaKind.Video,
            OrganizeCriterion.AudioBitrate or OrganizeCriterion.AudioDuration or OrganizeCriterion.AudioCodec or OrganizeCriterion.AudioChannels => MediaKind.Audio,
            OrganizeCriterion.ImageResolution or OrganizeCriterion.ImageDimensions or OrganizeCriterion.ImageFormat or OrganizeCriterion.ImageColorDepth => MediaKind.Image,
            OrganizeCriterion.DocumentPageCount or OrganizeCriterion.DocumentAuthor or OrganizeCriterion.DocumentTitle or OrganizeCriterion.DocumentWordCount => MediaKind.Unsupported,
            _ => null,
        };

        public static bool NeedsProbe(OrganizeCriterion c) => KindFor(c) is MediaKind.Video or MediaKind.Audio;
        public static bool NeedsImageRead(OrganizeCriterion c) => KindFor(c) == MediaKind.Image;

        /// <summary>"e.g. 🖼️ Images\2024\holiday.jpg" for the sidebar.</summary>
        public static string ExamplePath(OrganizeLayout layout, IEnumerable<OrganizeStep> steps, bool emoji)
        {
            const string file = "holiday.jpg";
            return layout switch
            {
                OrganizeLayout.Flatten => file,
                OrganizeLayout.KeepStructure => Path.Combine("Trips", "Spain", file),
                _ => Path.Combine(steps
                    .Select(s => s.Criterion == OrganizeCriterion.Category
                        ? (emoji ? EmojiFor("Images") + " " : "") + "Images"
                        : Choices.FirstOrDefault(c => c.Criterion == s.Criterion)?.Example ?? "")
                    .Where(x => x.Length > 0)
                    .Append(file)
                    .ToArray()),
            };
        }

        // =================================================================
        //  Folder names
        // =================================================================

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars()
            .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

        /// <summary>
        /// Makes a label safe as one folder name. Category names come from
        /// Settings as free text - "Photos/Scans" used to create two folder
        /// levels, and "Docs?" failed every file.
        /// </summary>
        public static string SafeFolderName(string label)
        {
            var sb = new StringBuilder(label.Length);
            foreach (char c in label) sb.Append(InvalidNameChars.Contains(c) || char.IsControl(c) ? '-' : c);
            string name = sb.ToString().Trim().TrimEnd('.', ' ');
            if (name.Length == 0 || name.All(ch => ch == '.')) name = "Other";
            if (ReservedNames.Contains(name.Split('.')[0])) name = "_" + name;
            return name.Length > 100 ? name[..100].TrimEnd('.', ' ') : name;
        }

        public static string ExtensionLabel(string extension) =>
            string.IsNullOrEmpty(extension) || extension == "." ? "No extension" : extension.TrimStart('.').ToUpperInvariant();

        /// <summary>Category folder name from the Settings categories; unknown extensions -> "Other" or their extension.</summary>
        public static string CategoryFor(string extension, IReadOnlyList<OrganizeCategory> categories, bool groupUnknownAsOther, bool emoji)
        {
            foreach (var category in categories)
            {
                if (category.Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                    return (emoji ? EmojiFor(category.Name) + " " : "") + category.Name;
            }
            if (groupUnknownAsOther) return (emoji ? "\U0001F4C1 " : "") + "Other";
            return ExtensionLabel(extension);
        }

        /// <summary>Category names are free text, so the emoji is picked by keyword.</summary>
        public static string EmojiFor(string categoryName)
        {
            string name = categoryName.ToLowerInvariant();
            bool Has(params string[] words) => words.Any(name.Contains);

            if (Has("image", "photo", "picture", "pic")) return "\U0001F5BC️";
            if (Has("video", "movie", "film")) return "\U0001F3AC";
            if (Has("audio", "music", "sound", "song")) return "\U0001F3B5";
            if (Has("spreadsheet", "excel", "sheet")) return "\U0001F4CA";
            if (Has("presentation", "slide", "powerpoint")) return "\U0001F4D1";
            if (Has("ebook", "book")) return "\U0001F4DA";
            if (Has("document", "doc", "text", "pdf", "office")) return "\U0001F4C4";
            if (Has("archive", "zip", "compressed", "backup")) return "\U0001F5C3️";
            if (Has("code", "script", "dev", "source", "program")) return "\U0001F4BB";
            if (Has("executable", "install", "app", "setup")) return "⚙️";
            if (Has("font")) return "\U0001F520";
            return "\U0001F4C1";
        }

        /// <summary>The old version named this folder after MediaKind - so every document went into "Unsupported".</summary>
        public static string FileTypeLabel(MediaKind kind) => kind switch
        {
            MediaKind.Image => "Images",
            MediaKind.Video => "Videos",
            MediaKind.Audio => "Audio",
            _ => "Other",
        };

        public static string SizeBucketFor(long bytes)
        {
            const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
            return bytes switch
            {
                < 100 * KB => "0 - 100KB",
                < MB => "100KB - 1MB",
                < 10 * MB => "1MB - 10MB",
                < 100 * MB => "10MB - 100MB",
                < GB => "100MB - 1GB",
                _ => "1GB+",
            };
        }

        /// <summary>"07 July" - the number keeps months in calendar order (the old "July" sorted alphabetically).</summary>
        public static string MonthLabel(DateTime date) =>
            $"{date.Month:00} {CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(date.Month)}";

        public static string FirstLetterFor(string name)
        {
            string trimmed = name.TrimStart();
            if (trimmed.Length == 0) return "#";
            char c = char.ToUpperInvariant(trimmed[0]);
            return char.IsLetter(c) ? c.ToString() : char.IsDigit(c) ? "0-9" : "#";
        }

        public static string ResolutionLabelFor(int width, int height)
        {
            // Portrait phone video is 1080x1920: go by the shorter side.
            int shortSide = width > 0 && height > 0 ? Math.Min(width, height) : Math.Max(width, height);
            return shortSide switch
            {
                <= 0 => "Unknown resolution",
                >= 4320 => "8K (4320p)",
                >= 2160 => "4K (2160p)",
                >= 1440 => "1440p",
                >= 1080 => "1080p",
                >= 720 => "720p",
                >= 480 => "480p",
                _ => "Below 480p",
            };
        }

        public static string DurationBucketFor(double seconds) => seconds switch
        {
            <= 0 or double.NaN => "Unknown length",
            < 60 => "0 - 1 min",
            < 300 => "1 - 5 min",
            < 900 => "5 - 15 min",
            < 3600 => "15 - 60 min",
            _ => "60+ min",
        };

        public static string CodecLabelFor(string? codec) =>
            string.IsNullOrWhiteSpace(codec) ? "Unknown codec" : codec.ToUpperInvariant();

        public static string FrameRateLabelFor(double fps)
        {
            if (fps <= 0 || double.IsNaN(fps)) return "Unknown frame rate";
            if (Math.Abs(fps - 23.976) < 0.5) return "24fps";
            if (Math.Abs(fps - 29.97) < 0.5) return "30fps";
            if (Math.Abs(fps - 59.94) < 0.5) return "60fps";
            return $"{Math.Round(fps):0}fps";
        }

        public static string BitrateBucketFor(long bps)
        {
            if (bps <= 0) return "Unknown bitrate";
            long kbps = bps / 1000;
            return kbps switch
            {
                < 96 => "0 - 96kbps",
                < 128 => "96 - 128kbps",
                < 192 => "128 - 192kbps",
                < 256 => "192 - 256kbps",
                < 320 => "256 - 320kbps",
                _ => "320kbps+",
            };
        }

        public static string ChannelsLabelFor(int channels) => channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1 Surround",
            8 => "7.1 Surround",
            > 0 => $"{channels}ch",
            _ => "Unknown channels",
        };

        public static string MegapixelBucketFor(int width, int height)
        {
            if (width <= 0 || height <= 0) return "Unknown resolution";
            double mp = width * (double)height / 1_000_000;
            return mp switch
            {
                < 1 => "Under 1MP",
                < 2 => "1 - 2MP",
                < 5 => "2 - 5MP",
                < 10 => "5 - 10MP",
                < 20 => "10 - 20MP",
                _ => "20MP+",
            };
        }

        /// <summary>
        /// The folder name one step gives a file, or null for "no folder at
        /// this level". Media rules only look at their own kind of file: the
        /// old version ran ffprobe on every text file and filed them all
        /// under "Unknown Resolution".
        /// </summary>
        public static string? SegmentFor(
            OrganizeScannedFile file,
            OrganizeStep step,
            OrganizeOptions o,
            OrganizeMediaFacts? media,
            OrganizeImageFacts? image)
        {
            string name = Path.GetFileName(file.FullPath);
            string ext = Path.GetExtension(file.FullPath);
            var kind = FileTypeDetector.Detect(file.FullPath);

            var needs = KindFor(step.Criterion);
            if (needs != null && needs != kind)
                return step.GroupUnknownAsOther ? "Other" : null;

            string label = step.Criterion switch
            {
                OrganizeCriterion.Category => CategoryFor(ext, o.Categories, step.GroupUnknownAsOther, o.EmojiFolders),
                OrganizeCriterion.Extension => ExtensionLabel(ext),
                OrganizeCriterion.FileType => FileTypeLabel(kind),
                OrganizeCriterion.Size => SizeBucketFor(file.SizeBytes),
                OrganizeCriterion.DateCreated => file.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                OrganizeCriterion.DateModified => file.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                OrganizeCriterion.Year => file.Modified.Year.ToString(CultureInfo.InvariantCulture),
                OrganizeCriterion.Month => MonthLabel(file.Modified),
                OrganizeCriterion.FirstLetter => FirstLetterFor(name),

                OrganizeCriterion.VideoResolution => ResolutionLabelFor(media?.Width ?? 0, media?.Height ?? 0),
                OrganizeCriterion.VideoDuration or OrganizeCriterion.AudioDuration => DurationBucketFor(media?.DurationSeconds ?? 0),
                OrganizeCriterion.VideoCodec => CodecLabelFor(media?.VideoCodec),
                OrganizeCriterion.VideoFrameRate => FrameRateLabelFor(media?.FrameRate ?? 0),
                OrganizeCriterion.AudioBitrate => BitrateBucketFor(media?.AudioBitrateBps ?? 0),
                OrganizeCriterion.AudioCodec => CodecLabelFor(media?.AudioCodec),
                OrganizeCriterion.AudioChannels => ChannelsLabelFor(media?.AudioChannels ?? 0),

                OrganizeCriterion.ImageResolution => MegapixelBucketFor(image?.Width ?? 0, image?.Height ?? 0),
                OrganizeCriterion.ImageDimensions => image is { Width: > 0, Height: > 0 } ? $"{image.Width}x{image.Height}" : "Unknown dimensions",
                OrganizeCriterion.ImageFormat => string.IsNullOrWhiteSpace(image?.Format) ? "Unknown format" : image.Format.ToUpperInvariant(),
                OrganizeCriterion.ImageColorDepth => image is { Depth: > 0 } ? $"{image.Depth}-bit" : "Unknown colour depth",

                _ => "Other",
            };
            return SafeFolderName(label);
        }

        /// <summary>Subfolder of the source a file sits in ("" at the top). Never climbs out of the source.</summary>
        public static string RelativeSourceFolder(string filePath, string sourceFolder)
        {
            string relative = Path.GetDirectoryName(Path.GetRelativePath(sourceFolder, filePath)) ?? "";
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? "" : relative;
        }

        /// <summary>Destination subfolder for one file, relative to the destination root.</summary>
        public static string RelativeDestinationFolder(OrganizeScannedFile file, OrganizeOptions o, OrganizeMediaFacts? media, OrganizeImageFacts? image) =>
            o.Layout switch
            {
                OrganizeLayout.Flatten => "",
                OrganizeLayout.KeepStructure => RelativeSourceFolder(file.FullPath, o.SourceFolder),
                _ => Path.Combine(o.Steps
                    .Select(s => SegmentFor(file, s, o, media, image))
                    .Where(s => s != null)
                    .Cast<string>()
                    .ToArray()),
            };

        // =================================================================
        //  The plan
        // =================================================================

        private static string Full(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static bool SamePath(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
        public static bool IsUnder(string path, string folder)
        {
            string p = Full(path), f = Full(folder);
            return p.Equals(f, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Works out where every file goes. Files already where they belong
        /// are "In place" and keep their name; two files heading for the same
        /// name get " (2)", " (3)"... A name already on disk is only noted -
        /// the run checks whether it's the same file first.
        /// </summary>
        public static List<OrganizeFileItem> BuildPlan(
            IReadOnlyList<OrganizeScannedFile> files,
            OrganizeOptions o,
            Func<string, OrganizeMediaFacts?>? media = null,
            Func<string, OrganizeImageFacts?>? image = null)
        {
            var items = new List<OrganizeFileItem>(files.Count);
            var batchPaths = new HashSet<string>(files.Select(f => Full(f.FullPath)), StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                var kind = FileTypeDetector.Detect(f.FullPath);
                OrganizeMediaFacts? m = kind is MediaKind.Video or MediaKind.Audio ? media?.Invoke(f.FullPath) : null;
                OrganizeImageFacts? im = kind == MediaKind.Image ? image?.Invoke(f.FullPath) : null;

                string rel = RelativeDestinationFolder(f, o, m, im);
                string name = Path.GetFileName(f.FullPath);
                string relSource = RelativeSourceFolder(f.FullPath, o.SourceFolder);

                var item = new OrganizeFileItem
                {
                    FullPath = f.FullPath,
                    Name = name,
                    SizeBytes = f.SizeBytes,
                    Modified = f.Modified,
                    Kind = kind,
                    SourceFolderDisplay = relSource,
                    Destination = Path.Combine(o.DestinationFolder, rel, name),
                    DestinationFolderDisplay = rel.Length == 0 ? "Top level" : rel,
                };
                if (SamePath(item.Destination, item.FullPath))
                {
                    item.Status = OrganizeStatus.InPlace;
                    item.Message = "Already in the right folder.";
                }
                items.Add(item);
            }

            // Files staying put keep their names; the rest can't take them.
            var taken = new HashSet<string>(
                items.Where(i => i.Status == OrganizeStatus.InPlace).Select(i => Full(i.Destination)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in items.Where(i => i.Status == OrganizeStatus.Ready))
            {
                string dest = Full(item.Destination);
                if (taken.Add(dest))
                {
                    if (File.Exists(dest) && !batchPaths.Contains(dest))
                        item.Message = Loc.F("Str_Organize_MsgExists", "A file named {0} is already in that folder. If it's identical, this one isn't copied again; otherwise it's saved with \" (2)\".", item.Name);
                    continue;
                }

                string dir = Path.GetDirectoryName(dest) ?? "";
                string stem = Path.GetFileNameWithoutExtension(dest);
                string ext = Path.GetExtension(dest);
                string candidate;
                int n = 2;
                do
                {
                    candidate = Path.Combine(dir, $"{stem} ({n++}){ext}");
                } while (taken.Contains(candidate) || (File.Exists(candidate) && !batchPaths.Contains(candidate)));
                taken.Add(candidate);
                item.Destination = candidate;
                item.Message = Loc.F("Str_Organize_MsgSameName", "Another file here has the same name - this one will be saved as {0}.", Path.GetFileName(candidate));
            }

            return items;
        }

        /// <summary>"1,204 files into 18 folders · 3 already in place".</summary>
        public static string DescribePlan(IReadOnlyCollection<OrganizeFileItem> items)
        {
            int ready = items.Count(i => i.Status == OrganizeStatus.Ready);
            int folders = items.Where(i => i.Status == OrganizeStatus.Ready).Select(i => i.DestinationFolderDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int inPlace = items.Count(i => i.Status == OrganizeStatus.InPlace);
            int renamed = items.Count(i => i.Status == OrganizeStatus.Ready && i.IsRenamed);
            var parts = new List<string> { Loc.F("Str_Organize_SummaryInto", "{0} into {1}", FilesText.Files(ready), FilesText.Folders(folders)) };
            if (inPlace > 0) parts.Add(Loc.F("Str_Organize_SumAlreadyInPlace", "{0:N0} already in place", inPlace));
            if (renamed > 0) parts.Add(Loc.F("Str_Organize_SumRenamedClash", "{0:N0} renamed to avoid a clash", renamed));
            return string.Join(" · ", parts);
        }

        public static string DescribeRun(OrganizeRunResult r)
        {
            var parts = new List<string>();
            if (r.Moved > 0) parts.Add(Loc.F("Str_Organize_RunMoved", "Moved {0:N0}", r.Moved));
            if (r.Copied > 0) parts.Add(Loc.F("Str_Organize_RunCopied", "Copied {0:N0}", r.Copied));
            if (parts.Count == 0) parts.Add(Loc.T("Str_Organize_RunNothing", "Nothing organized"));
            parts[0] += Loc.F("Str_Organize_RunIn", " in {0:0.0} s", r.Elapsed.TotalSeconds);
            if (r.Duplicates > 0) parts.Add(Loc.F("Str_CopyQueue_RunAlreadyThere", "{0:N0} already there", r.Duplicates));
            if (r.FoldersRemoved > 0) parts.Add(Loc.F("Str_Organize_RunFoldersRemoved", "{0:N0} empty folders removed", r.FoldersRemoved));
            if (r.Failed > 0) parts.Add(Loc.F("Str_CopyQueue_RunFailed", "{0:N0} failed", r.Failed));
            if (r.Cancelled > 0) parts.Add(Loc.F("Str_Files_RunCancelled", "{0:N0} cancelled", r.Cancelled));
            return string.Join(" · ", parts);
        }

        public static string UniquePath(string path) => SafeFileOps.UniquePath(path);

        // =================================================================
        //  ffprobe JSON
        // =================================================================

        public static OrganizeMediaFacts? ParseFfprobeJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                static string Str(JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
                static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : 0;

                double duration = 0;
                long formatBitrate = 0;
                if (root.TryGetProperty("format", out var format))
                {
                    duration = Num(Str(format, "duration"));
                    formatBitrate = (long)Num(Str(format, "bit_rate"));
                }

                string videoCodec = "", audioCodec = "";
                int width = 0, height = 0, channels = 0;
                double fps = 0;
                long audioBitrate = 0;
                bool rotated = false;

                if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        string type = Str(s, "codec_type");
                        // Cover art in MP3/M4A shows up as a one-frame "video" stream.
                        bool attachedPic = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.ToString() == "1";

                        if (type == "video" && !attachedPic && videoCodec.Length == 0)
                        {
                            videoCodec = Str(s, "codec_name");
                            width = (int)Num(Str(s, "width"));
                            height = (int)Num(Str(s, "height"));
                            fps = ParseFraction(Str(s, "avg_frame_rate"));
                            if (fps <= 0) fps = ParseFraction(Str(s, "r_frame_rate"));
                            if (s.TryGetProperty("tags", out var tags) && Math.Abs(Num(Str(tags, "rotate"))) % 180 == 90) rotated = true;
                            if (duration <= 0) duration = Num(Str(s, "duration"));
                        }
                        else if (type == "audio" && audioCodec.Length == 0)
                        {
                            audioCodec = Str(s, "codec_name");
                            channels = (int)Num(Str(s, "channels"));
                            audioBitrate = (long)Num(Str(s, "bit_rate"));
                            if (duration <= 0) duration = Num(Str(s, "duration"));
                        }
                    }
                }

                if (audioBitrate == 0 && videoCodec.Length == 0) audioBitrate = formatBitrate;
                if (rotated) (width, height) = (height, width);
                return new OrganizeMediaFacts(duration, videoCodec, width, height, fps, audioCodec, channels, audioBitrate);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>"30000/1001" -> 29.97. "0/0" (unknown) -> 0.</summary>
        public static double ParseFraction(string? value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            var parts = value.Split('/');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den))
                return den != 0 ? num / den : 0;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double single) ? single : 0;
        }

        // =================================================================
        //  Settings + undo journal
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string AppDataFile(string name) => Path.Combine(
            AppPaths.DataRoot, name);

        private static T? Load<T>(string file) where T : class
        {
            try
            {
                return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                if (ex is JsonException or NotSupportedException) JsonSettings.KeepBadCopy(file);
                return null;
            }
        }

        private static void Save<T>(T value, string file) => AtomicFile.WriteAllText(file, JsonSerializer.Serialize(value, JsonOptions));

        public static OrganizeSettings LoadSettings(string? path = null)
        {
            string file = path ?? AppDataFile("organize_files.json");
            var s = Load<OrganizeSettings>(file) ?? new OrganizeSettings();
            s.Steps = (s.Steps ?? new()).Where(x => IsOffered(x.Criterion)).Take(MaxSteps).ToList();
            if (s.Steps.Count == 0) s.Steps.Add(new OrganizeStep { Criterion = OrganizeCriterion.Category });
            RemoveSavedFolders(file, s);
            return s;
        }

        /// <summary>
        /// Older versions saved the chosen source and destination folders.
        /// Those paths are no longer kept: rewrite the file without them.
        /// </summary>
        private static void RemoveSavedFolders(string file, OrganizeSettings loaded)
        {
            try
            {
                if (!File.Exists(file)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && (doc.RootElement.TryGetProperty("SourceFolder", out _) || doc.RootElement.TryGetProperty("DestinationFolder", out _)))
                    Save(loaded, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // unreadable file: Load already fell back to defaults; the next save replaces it
            }
        }

        public static void SaveSettings(OrganizeSettings settings, string? path = null) =>
            Save(settings, path ?? AppDataFile("organize_files.json"));

        public static string DefaultJournalPath => AppDataFile("organize_undo.json");

        public static OrganizeJournal? LoadJournal(string? path = null)
        {
            var j = Load<OrganizeJournal>(path ?? DefaultJournalPath);
            return j is { Entries.Count: > 0 } ? j : null;
        }

        public static void SaveJournal(OrganizeJournal journal, string? path = null) => Save(journal, path ?? DefaultJournalPath);

        public static void DeleteJournal(string? path = null)
        {
            try
            {
                File.Delete(path ?? DefaultJournalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}