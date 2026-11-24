using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// File Converter backend. Split in three:
    /// <list type="bullet">
    /// <item>ConverterManager.cs (this file) - planning, no side effects:
    /// what's supported, codec/container rules, ffmpeg arguments, image
    /// plans, output paths, the file list. Unit tested.</item>
    /// <item>ConverterManager.Run.cs - running a batch: listing folders,
    /// ffmpeg with live per-file progress, moving results into place,
    /// deleting originals.</item>
    /// <item>ConverterManager.Images.cs - Magick.NET image conversion.</item>
    /// </list>
    /// Document conversion has no engine (it needs a PDF/Office library),
    /// so the view doesn't offer it any more instead of listing every
    /// document as "Unsupported".
    /// </summary>
    public sealed partial class ConverterManager
    {
        // =================================================================
        //  What can be read
        // =================================================================

        public static readonly HashSet<string> VideoInputExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m4v", ".mpeg", ".mpg", ".m2v", ".ts", ".m2ts", ".mts",
            ".vob", ".ogv", ".3gp", ".3g2", ".f4v", ".asf", ".rm", ".rmvb", ".divx", ".mxf", ".wtv",
        };

        public static readonly HashSet<string> AudioInputExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".alac", ".aif", ".aiff", ".ape", ".amr",
            ".mka", ".mp2", ".ac3", ".dts", ".wv", ".mpc", ".tta", ".au", ".caf",
        };

        /// <summary>Images Magick.NET can read (RAW via its libraw delegate).</summary>
        public static readonly HashSet<string> ImageInputExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif", ".ico", ".tga", ".psd", ".heic", ".heif",
            ".avif", ".jxl", ".svg", ".exr", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pcx", ".ppm", ".pgm", ".pbm",
        };

        public static MediaKind KindOf(string path)
        {
            string ext = Path.GetExtension(path);
            if (VideoInputExtensions.Contains(ext)) return MediaKind.Video;
            if (AudioInputExtensions.Contains(ext)) return MediaKind.Audio;
            if (ImageInputExtensions.Contains(ext)) return MediaKind.Image;
            return MediaKind.Unsupported;
        }

        /// <summary>
        /// Whether <paramref name="path"/> can be converted as <paramref name="type"/>.
        /// Videos also count for Audio (extract the soundtrack) - the old
        /// version rejected that common case.
        /// </summary>
        public static bool CanConvert(string path, ConvertFileType type) => (type, KindOf(path)) switch
        {
            (ConvertFileType.Video, MediaKind.Video) => true,
            (ConvertFileType.Audio, MediaKind.Audio or MediaKind.Video) => true,
            (ConvertFileType.Image, MediaKind.Image) => true,
            _ => false,
        };

        // =================================================================
        //  Target formats
        // =================================================================

        public static string TargetExtension(ConvertOptions o) => o.FileType switch
        {
            ConvertFileType.Video => o.VideoFormat switch
            {
                ConvertVideoFormat.Mkv => ".mkv",
                ConvertVideoFormat.Avi => ".avi",
                ConvertVideoFormat.Mov => ".mov",
                ConvertVideoFormat.Webm => ".webm",
                ConvertVideoFormat.Flv => ".flv",
                _ => ".mp4",
            },
            ConvertFileType.Audio => o.AudioFormat switch
            {
                ConvertAudioFormat.Wav => ".wav",
                ConvertAudioFormat.Flac => ".flac",
                ConvertAudioFormat.Aac => ".aac",
                ConvertAudioFormat.Ogg => ".ogg",
                ConvertAudioFormat.M4a => ".m4a",
                _ => ".mp3",
            },
            ConvertFileType.Image => o.ImageFormat switch
            {
                ConvertImageFormat.Png => ".png",
                ConvertImageFormat.Webp => ".webp",
                ConvertImageFormat.Avif => ".avif",
                ConvertImageFormat.Bmp => ".bmp",
                ConvertImageFormat.Tiff => ".tiff",
                ConvertImageFormat.Gif => ".gif",
                ConvertImageFormat.Ico => ".ico",
                _ => ".jpg",
            },
            _ => o.DocumentFormat switch
            {
                ConvertDocumentFormat.Docx => ".docx",
                ConvertDocumentFormat.Txt => ".txt",
                ConvertDocumentFormat.Rtf => ".rtf",
                ConvertDocumentFormat.Odt => ".odt",
                ConvertDocumentFormat.Html => ".html",
                _ => ".pdf",
            },
        };

        public static string TargetLabel(ConvertOptions o) => ConvertText.ExtensionLabel(TargetExtension(o));

        private static bool SameExtension(string path, string ext)
        {
            string e = Path.GetExtension(path);
            if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
            // .jpeg/.jpg and .tif/.tiff are the same format.
            static string Norm(string x) => x.ToLowerInvariant() switch { ".jpeg" or ".jfif" => ".jpg", ".tif" => ".tiff", var y => y };
            return Norm(e) == Norm(ext);
        }

        /// <summary>
        /// True when converting would change nothing: an image already in the
        /// target format with no resize. Same-format videos and audio are
        /// still converted - re-encoding smaller is a common reason to use
        /// this tool (the old version always skipped them).
        /// </summary>
        public static bool IsNoOp(string path, ConvertOptions o) =>
            o.FileType == ConvertFileType.Image && o.ImageResize == ConvertImageResize.Original && SameExtension(path, TargetExtension(o));

        // =================================================================
        //  Video: codec / container rules
        // =================================================================

        /// <summary>
        /// Codecs each container can actually hold. The old version let you
        /// pick any pair, so e.g. WEBM + H.264 or AVI + AV1 just failed with
        /// an ffmpeg error for every file.
        /// </summary>
        public static IReadOnlyList<ConvertVideoCodec> CodecsFor(ConvertVideoFormat format) => format switch
        {
            ConvertVideoFormat.Webm => new[] { ConvertVideoCodec.Vp9, ConvertVideoCodec.Av1 },
            ConvertVideoFormat.Mov => new[] { ConvertVideoCodec.H264, ConvertVideoCodec.H265 },
            ConvertVideoFormat.Avi or ConvertVideoFormat.Flv => new[] { ConvertVideoCodec.H264 },
            _ => new[] { ConvertVideoCodec.H264, ConvertVideoCodec.H265, ConvertVideoCodec.Vp9, ConvertVideoCodec.Av1 },
        };

        /// <summary>The requested codec if the container allows it, otherwise the container's first codec.</summary>
        public static ConvertVideoCodec ResolveCodec(ConvertVideoFormat format, ConvertVideoCodec requested)
        {
            var allowed = CodecsFor(format);
            return allowed.Contains(requested) ? requested : allowed[0];
        }

        public static string CodecLabel(ConvertVideoCodec codec) => codec switch
        {
            ConvertVideoCodec.H265 => "H.265 (HEVC)",
            ConvertVideoCodec.Vp9 => "VP9",
            ConvertVideoCodec.Av1 => "AV1",
            _ => "H.264 (AVC)",
        };

        /// <summary>
        /// Quality level for each codec - the scales differ (x265 CRF 28 looks
        /// like x264 CRF 23), so the old single 18/23/28 scale made H.265
        /// files needlessly big and VP9/AV1 ones blurry.
        /// </summary>
        internal static int CrfFor(ConvertVideoCodec codec, ConvertVideoQuality q) => (codec, q) switch
        {
            (ConvertVideoCodec.H264, ConvertVideoQuality.Highest) => 18,
            (ConvertVideoCodec.H264, ConvertVideoQuality.Smaller) => 28,
            (ConvertVideoCodec.H264, _) => 23,
            (ConvertVideoCodec.H265, ConvertVideoQuality.Highest) => 20,
            (ConvertVideoCodec.H265, ConvertVideoQuality.Smaller) => 30,
            (ConvertVideoCodec.H265, _) => 26,
            (ConvertVideoCodec.Vp9, ConvertVideoQuality.Highest) => 24,
            (ConvertVideoCodec.Vp9, ConvertVideoQuality.Smaller) => 40,
            (ConvertVideoCodec.Vp9, _) => 32,
            (_, ConvertVideoQuality.Highest) => 24,
            (_, ConvertVideoQuality.Smaller) => 42,
            _ => 32,
        };

        private static int HeightFor(ConvertVideoResolution r) => r switch
        {
            ConvertVideoResolution.R2160p => 2160,
            ConvertVideoResolution.R1080p => 1080,
            ConvertVideoResolution.R720p => 720,
            ConvertVideoResolution.R480p => 480,
            _ => 0,
        };

        /// <summary>ffmpeg arguments for a video conversion (input/output paths included).</summary>
        /// <param name="svtAv1Available">Use the fast SVT-AV1 encoder when ffmpeg has it (libaom is many times slower).</param>
        public static List<string> BuildVideoArgs(string input, string output, ConvertOptions o, bool svtAv1Available)
        {
            var codec = ResolveCodec(o.VideoFormat, o.VideoCodec);
            int crf = CrfFor(codec, o.VideoQuality);
            var a = new List<string> { "-nostdin", "-y", "-i", input, "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0" };

            // Never upscale: the old "scale=-2:1080" blew 480p clips up to 1080p (bigger file, no detail).
            int height = HeightFor(o.VideoResolution);
            if (height > 0) a.AddRange(new[] { "-vf", $"scale=-2:min({height}\\,ih)" });

            switch (codec)
            {
                case ConvertVideoCodec.H265:
                    a.AddRange(new[] { "-c:v", "libx265", "-preset", "medium", "-crf", I(crf), "-pix_fmt", "yuv420p" });
                    // Apple devices only play HEVC in MP4/MOV with the hvc1 tag.
                    if (o.VideoFormat is ConvertVideoFormat.Mp4 or ConvertVideoFormat.Mov) a.AddRange(new[] { "-tag:v", "hvc1" });
                    break;
                case ConvertVideoCodec.Vp9:
                    // -b:v 0 makes VP9 honour the CRF (without it the CRF is just a cap on a default bitrate).
                    a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", I(crf), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2", "-pix_fmt", "yuv420p" });
                    break;
                case ConvertVideoCodec.Av1:
                    if (svtAv1Available)
                        a.AddRange(new[] { "-c:v", "libsvtav1", "-preset", "8", "-crf", I(crf), "-pix_fmt", "yuv420p" });
                    else
                        a.AddRange(new[] { "-c:v", "libaom-av1", "-crf", I(crf), "-b:v", "0", "-cpu-used", "6", "-row-mt", "1", "-pix_fmt", "yuv420p" });
                    break;
                default:
                    // yuv420p: 10-bit / 4:4:4 sources otherwise make H.264 files most players can't open.
                    a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", I(crf), "-pix_fmt", "yuv420p" });
                    break;
            }

            // Audio each container accepts.
            switch (o.VideoFormat)
            {
                case ConvertVideoFormat.Webm:
                    a.AddRange(new[] { "-c:a", "libopus", "-b:a", "128k" });
                    break;
                case ConvertVideoFormat.Avi:
                    a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "192k" });
                    break;
                default:
                    a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                    break;
            }

            if (o.VideoFormat is ConvertVideoFormat.Mp4 or ConvertVideoFormat.Mov)
                a.AddRange(new[] { "-movflags", "+faststart" });   // plays before fully downloaded

            a.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
            return a;
        }

        // =================================================================
        //  Audio
        // =================================================================

        public static bool IsLossless(ConvertAudioFormat f) => f is ConvertAudioFormat.Wav or ConvertAudioFormat.Flac;

        private static string BitrateArg(ConvertAudioBitrate b) => b switch
        {
            ConvertAudioBitrate.K128 => "128k",
            ConvertAudioBitrate.K192 => "192k",
            ConvertAudioBitrate.K320 => "320k",
            _ => "256k",
        };

        /// <summary>Vorbis -q:a roughly matching each bitrate for stereo (4 ~ 128k ... 9 ~ 320k).</summary>
        internal static string VorbisQuality(ConvertAudioBitrate b) => b switch
        {
            ConvertAudioBitrate.K128 => "4",
            ConvertAudioBitrate.K192 => "6",
            ConvertAudioBitrate.K320 => "9",
            _ => "8",
        };

        /// <summary>
        /// Sample rate to request, or null to keep the source. MP3 can't go
        /// above 48 kHz (the old "96 kHz + MP3" failed every file).
        /// </summary>
        public static int? SampleRateFor(ConvertAudioFormat format, ConvertAudioSampleRate rate)
        {
            int? hz = rate switch
            {
                ConvertAudioSampleRate.Hz22050 => 22050,
                ConvertAudioSampleRate.Hz44100 => 44100,
                ConvertAudioSampleRate.Hz48000 => 48000,
                ConvertAudioSampleRate.Hz96000 => 96000,
                _ => null,
            };
            if (format == ConvertAudioFormat.Mp3 && hz > 48000) hz = 48000;
            return hz;
        }

        public static List<string> BuildAudioArgs(string input, string output, ConvertOptions o)
        {
            var a = new List<string> { "-nostdin", "-y", "-i", input, "-map", "0:a:0", "-vn", "-map_metadata", "0" };

            switch (o.AudioFormat)
            {
                case ConvertAudioFormat.Wav:
                    a.AddRange(new[] { "-c:a", "pcm_s16le" });
                    break;
                case ConvertAudioFormat.Flac:
                    a.AddRange(new[] { "-c:a", "flac", "-compression_level", "5" });
                    break;
                case ConvertAudioFormat.Ogg:
                    // Vorbis quality levels, not a bitrate: "-b:a 256k" (the
                    // default) makes libvorbis refuse mono files outright.
                    a.AddRange(new[] { "-c:a", "libvorbis", "-q:a", VorbisQuality(o.AudioBitrate) });
                    break;
                case ConvertAudioFormat.Aac:
                case ConvertAudioFormat.M4a:
                    a.AddRange(new[] { "-c:a", "aac", "-b:a", BitrateArg(o.AudioBitrate) });
                    break;
                default:
                    a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", BitrateArg(o.AudioBitrate) });
                    break;
            }

            if (SampleRateFor(o.AudioFormat, o.AudioSampleRate) is int hz) a.AddRange(new[] { "-ar", I(hz) });
            if (o.AudioFormat == ConvertAudioFormat.M4a) a.AddRange(new[] { "-movflags", "+faststart" });

            a.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
            return a;
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        // =================================================================
        //  Images
        // =================================================================

        /// <summary>What to do to an image, decided up front so it's testable.</summary>
        public sealed record ImagePlan(
            ConvertImageFormat Format,
            int? Quality,
            int MaxWidth,
            int MaxHeight,
            bool FlattenOnWhite,
            bool KeepAnimation);

        /// <summary>
        /// - Quality only for lossy formats (the old version set PNG's
        ///   "quality" to 50-100, which in ImageMagick is a zlib/filter code,
        ///   not quality).
        /// - Resizing only ever shrinks, and Custom accepts just a width or
        ///   just a height.
        /// - JPG/BMP have no transparency: flatten onto white (the old
        ///   version produced black backgrounds from transparent PNGs).
        /// - ICO is capped at 256 px (larger fails to write).
        /// - GIF/WEBP output keeps animation.
        /// </summary>
        public static ImagePlan PlanImage(ConvertOptions o)
        {
            (int w, int h) = o.ImageResize switch
            {
                ConvertImageResize.R4K => (3840, 2160),
                ConvertImageResize.R1080p => (1920, 1080),
                ConvertImageResize.R720p => (1280, 720),
                ConvertImageResize.Custom => (Math.Max(0, o.ImageCustomWidth ?? 0), Math.Max(0, o.ImageCustomHeight ?? 0)),
                _ => (0, 0),
            };

            if (o.ImageFormat == ConvertImageFormat.Ico)
            {
                w = w > 0 ? Math.Min(w, 256) : 256;
                h = h > 0 ? Math.Min(h, 256) : 256;
            }

            int? quality = o.ImageFormat is ConvertImageFormat.Jpg or ConvertImageFormat.Webp or ConvertImageFormat.Avif
                ? o.ImageQuality switch
                {
                    ConvertImageQuality.Highest => 95,   // 100 doubles the size for no visible gain
                    ConvertImageQuality.Medium => 70,
                    ConvertImageQuality.Low => 50,
                    _ => 85,
                }
                : null;

            return new ImagePlan(
                o.ImageFormat,
                quality,
                w,
                h,
                FlattenOnWhite: o.ImageFormat is ConvertImageFormat.Jpg or ConvertImageFormat.Bmp,
                KeepAnimation: o.ImageFormat is ConvertImageFormat.Gif or ConvertImageFormat.Webp);
        }

        // =================================================================
        //  Output paths
        // =================================================================

        /// <summary>
        /// Where a file's result goes (before any " (2)" for clashes):
        /// the output folder (plus the file's subfolder under the added
        /// folder when keeping structure), or next to the original.
        /// </summary>
        public static string PlannedOutputPath(ConvertFileItem item, ConvertOptions o)
        {
            string name = Path.GetFileNameWithoutExtension(item.FullPath) + TargetExtension(o);
            string sourceDir = Path.GetDirectoryName(item.FullPath) ?? "";

            if (o.OutputMode == ConvertOutputMode.NextToOriginal || string.IsNullOrWhiteSpace(o.OutputFolder))
                return Path.Combine(sourceDir, name);

            string dir = o.OutputFolder;
            if (o.KeepFolderStructure && item.SourceRoot != null)
            {
                string relative = Path.GetRelativePath(item.SourceRoot, sourceDir);
                if (relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                    dir = Path.Combine(dir, Path.GetFileName(item.SourceRoot.TrimEnd('\\', '/')), relative);
                else
                    dir = Path.Combine(dir, Path.GetFileName(item.SourceRoot.TrimEnd('\\', '/')));
            }
            return Path.Combine(dir, name);
        }

        /// <summary>Videos\Smart Tools\Convert, Pictures\Smart Tools\Convert, Music\Smart Tools\Convert (created on the first save).</summary>
        public static string DefaultOutputFolder(ConvertFileType type)
        {
            var special = type switch
            {
                ConvertFileType.Video => Environment.SpecialFolder.MyVideos,
                ConvertFileType.Image => Environment.SpecialFolder.MyPictures,
                ConvertFileType.Audio => Environment.SpecialFolder.MyMusic,
                _ => Environment.SpecialFolder.MyDocuments,
            };
            string root = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(root, "Smart Tools", "Convert");
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        // =================================================================
        //  The file list
        // =================================================================

        /// <summary>A file found in the queue, with the added folder it came from.</summary>
        public readonly record struct QueuedFile(string Path, string? Root, long Size);

        public sealed class ListResult
        {
            public List<ConvertFileItem> Items { get; } = new();
            public int SkippedOtherType { get; set; }
            public int SkippedSameFormat { get; set; }
            public int Removed { get; set; }

            /// <summary>With "overwrite existing" on: how many files of the list will replace a file that is already there, and the first of them.</summary>
            public int WillReplace { get; set; }
            public string? FirstReplaced { get; set; }
        }

        /// <summary>The first "name (2).ext", "name (3).ext"... that is not taken (what the run itself will pick).</summary>
        private static string FirstFreeName(HashSet<string> taken, string name)
        {
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                string candidate = $"{stem} ({i}){ext}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Builds the grid rows from the expanded queue: files of another
        /// type are left out (or listed as not supported), images already in
        /// the target format are listed as skipped, and files the user removed
        /// stay removed - the old "Remove selected" came back on the next
        /// settings change.
        /// </summary>
        public static ListResult BuildList(IEnumerable<QueuedFile> files, ConvertOptions o, ISet<string> removed)
        {
            var result = new ListResult();
            string target = TargetLabel(o);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The names already in each output folder, read once per folder (not one disk call per file), so the list can say
            // "Will be saved as name (2)" before the run, and warn when a file will be replaced.
            var namesIn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> NamesIn(string dir)
            {
                if (namesIn.TryGetValue(dir, out var set)) return set;
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (Directory.Exists(dir))
                        foreach (string existing in Directory.EnumerateFiles(dir)) set.Add(Path.GetFileName(existing));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // unreadable folder: the run will report it; the list just can't predict a clash there
                }
                namesIn[dir] = set;
                return set;
            }

            foreach (var f in files)
            {
                if (!seen.Add(f.Path)) continue;
                if (removed.Contains(f.Path))
                {
                    result.Removed++;
                    continue;
                }

                bool supported = CanConvert(f.Path, o.FileType);
                if (!supported && o.MatchSelectedTypeOnly)
                {
                    result.SkippedOtherType++;
                    continue;
                }

                var item = new ConvertFileItem
                {
                    FullPath = f.Path,
                    Name = Path.GetFileName(f.Path),
                    CurrentFormat = ConvertText.ExtensionLabel(Path.GetExtension(f.Path)),
                    SizeBytes = f.Size,
                    Kind = KindOf(f.Path),
                    SourceRoot = f.Root,
                    TargetFormat = target,
                };

                if (!supported)
                {
                    item.Status = ConvertStatus.Unsupported;
                    item.Message = item.Kind == MediaKind.Unsupported
                        ? o.FileType switch
                        {
                            ConvertFileType.Image => Loc.T("Str_Convert_NotImage", "Not an image file this tool can read."),
                            ConvertFileType.Audio => Loc.T("Str_Convert_NotAudio", "Not an audio file this tool can read."),
                            _ => Loc.T("Str_Convert_NotVideo", "Not a video file this tool can read."),
                        }
                        : Loc.F("Str_Convert_WrongType", "This is {0} - it can't become {1}. Switch the file type to convert it.",
                            item.Kind == MediaKind.Image ? Loc.T("Str_Convert_KindImage", "an image") : item.Kind == MediaKind.Video ? Loc.T("Str_Convert_KindVideo", "a video") : Loc.T("Str_Convert_KindAudio", "an audio file"), target);
                }
                else if (IsNoOp(f.Path, o))
                {
                    item.Status = ConvertStatus.Skipped;
                    item.Message = Loc.F("Str_Convert_NoOp", "Already {0} and no resize chosen - nothing to change.", target);
                    result.SkippedSameFormat++;
                }
                else if (SameExtension(f.Path, TargetExtension(o)))
                {
                    item.Message = Loc.T("Str_Convert_SameFormat", "Same format - it will be re-encoded with these settings.");
                }

                item.OutputPath = PlannedOutputPath(item, o);
                if (item.Status == ConvertStatus.Ready)
                {
                    string dir = Path.GetDirectoryName(item.OutputPath) ?? "", name = Path.GetFileName(item.OutputPath);
                    var taken = NamesIn(dir);
                    bool replaces = false;
                    if (taken.Contains(name))
                    {
                        if (o.OverwriteExisting) replaces = true;
                        else
                        {
                            name = FirstFreeName(taken, name);                       // what the run will pick: the name is already taken
                            item.OutputPath = Path.Combine(dir, name);
                        }
                    }
                    if (replaces)
                    {
                        result.WillReplace++;
                        result.FirstReplaced ??= name;
                    }
                    else
                    {
                        taken.Add(name);                                              // later files in this list see the name as used
                    }
                    string saveAs = replaces
                        ? Loc.F("Str_Convert_WillReplace", "Will REPLACE {0}.", name)
                        : Loc.F("Str_Convert_WillSaveAs", "Will be saved as {0}.", name);
                    item.Message = item.Message.Length == 0 ? saveAs : $"{saveAs} {item.Message}";
                }
                result.Items.Add(item);
            }
            return result;
        }

        /// <summary>Sidebar rows: each added folder, then added files grouped by folder.</summary>
        public static List<ConvertQueueGroup> BuildQueueGroups(IEnumerable<ConvertSource> sources, bool includeSubfolders)
        {
            var list = sources.ToList();
            var groups = list.Where(s => s.IsFolder)
                .Select(s => new ConvertQueueGroup { FolderPath = s.FullPath, IsFolder = true, IncludeSubfolders = includeSubfolders })
                .ToList();
            groups.AddRange(list.Where(s => !s.IsFolder)
                .GroupBy(s => Path.GetDirectoryName(s.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new ConvertQueueGroup { FolderPath = g.Key, FilePaths = g.Select(s => s.FullPath).ToList() }));
            return groups;
        }

        /// <summary>"MP4 · H.264 (AVC) · 1080p" for the summary pill.</summary>
        public static string DescribeTarget(ConvertOptions o)
        {
            var parts = new List<string> { TargetLabel(o) };
            switch (o.FileType)
            {
                case ConvertFileType.Video:
                    parts.Add(CodecLabel(ResolveCodec(o.VideoFormat, o.VideoCodec)));
                    if (HeightFor(o.VideoResolution) is int h and > 0) parts.Add($"max {h}p");
                    break;
                case ConvertFileType.Audio:
                    parts.Add(IsLossless(o.AudioFormat) ? "lossless" : BitrateArg(o.AudioBitrate).Replace("k", " kbps", StringComparison.Ordinal));
                    break;
                case ConvertFileType.Image:
                    var plan = PlanImage(o);
                    if (plan.Quality is int q) parts.Add($"quality {q}");
                    if (plan.MaxWidth > 0 || plan.MaxHeight > 0)
                        parts.Add($"max {(plan.MaxWidth > 0 ? I(plan.MaxWidth) : "any")} x {(plan.MaxHeight > 0 ? I(plan.MaxHeight) : "any")}");
                    break;
            }
            return string.Join(" · ", parts);
        }

        /// <summary>"Converted 11 of 12 in 1:23 · 3.4 GB -> 1.1 GB (-68%) · 1 failed".</summary>
        public static string DescribeRun(ConvertRunResult r)
        {
            int total = r.Converted + r.Failed + r.Cancelled;
            var parts = new List<string> { $"Converted {r.Converted:N0} of {total:N0} in {ConvertText.FormatDuration(r.Elapsed)}" };
            if (r.Converted > 0 && r.InputBytes > 0)
            {
                double change = (r.OutputBytes - r.InputBytes) * 100.0 / r.InputBytes;
                parts.Add($"{ConvertText.FormatBytes(r.InputBytes)} -> {ConvertText.FormatBytes(r.OutputBytes)} ({(change >= 0 ? "+" : "")}{change:0}%)");
            }
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed");
            if (r.Cancelled > 0) parts.Add($"{r.Cancelled:N0} cancelled");
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  ffmpeg output parsing
        // =================================================================

        /// <summary>Seconds from ffprobe's "format=duration" output, or 0.</summary>
        public static double ParseDuration(string ffprobeOutput) =>
            double.TryParse(ffprobeOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) && d > 0 ? d : 0;

        /// <summary>
        /// Position from one line of "-progress pipe:1" output ("out_time_us=12345678",
        /// in microseconds; out_time_ms is also microseconds despite its name).
        /// </summary>
        public static double? ParseProgressSeconds(string line)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) return null;
            string key = line[..eq].Trim();
            if (key is not ("out_time_us" or "out_time_ms")) return null;
            return long.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0 ? us / 1_000_000.0 : null;
        }

        /// <summary>Files converted at once: ffmpeg video encoders already use every core.</summary>
        public static int DefaultParallelism(ConvertFileType type)
        {
            int cores = Math.Max(2, Environment.ProcessorCount);
            return type switch
            {
                ConvertFileType.Video => cores >= 12 ? 2 : 1,
                ConvertFileType.Audio => Math.Min(4, cores),
                _ => cores,
            };
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "file_converter.json");

        public static ConverterSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ConverterSettings();
                var s = JsonSerializer.Deserialize<ConverterSettings>(File.ReadAllText(file), JsonOptions) ?? new ConverterSettings();
                if (s.FileType == ConvertFileType.Document) s.FileType = ConvertFileType.Video;
                return s;
            }
            catch (Exception)
            {
                return new ConverterSettings();
            }
        }

        public static void SaveSettings(ConverterSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);
    }
}
