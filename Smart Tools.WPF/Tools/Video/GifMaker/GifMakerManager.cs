using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// GIF Maker - planning: output size, ffmpeg filter graphs (palette per
    /// GIF), frame counts, checks, the image list and saved settings. Pure
    /// logic (no WPF, no processes). Running ffmpeg/ffprobe is in
    /// GifMakerManager.Run.cs; the animated preview in GifMakerManager.Render.cs.
    /// </summary>
    public static partial class GifMakerManager
    {
        public static readonly string[] VideoExtensions =
        {
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg",
            ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".gif",
        };

        public static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".tif", ".tiff",
        };

        public static readonly int[] FrameRates = { 10, 15, 20, 25 };
        public static readonly int[] Widths = { 0, 240, 320, 480, 640, 800 };
        public static readonly double[] Speeds = { 0.5, 1, 1.5, 2 };
        public static readonly double[] ImageDurations = { 0.1, 0.2, 0.5, 1, 2 };
        public static readonly int[] PlayCounts = { 0, 1, 2, 3, 5, 10 };

        /// <summary>More than this many frames is refused: the GIF would be enormous and slow to make.</summary>
        public const int MaxFrames = 3000;

        /// <summary>Above this the plan note warns that the file will be big.</summary>
        public const int LargeFrames = 600;

        public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        // =================================================================
        //  Size, length, frames
        // =================================================================

        /// <summary>The GIF's size: the source scaled down to <paramref name="maxWidth"/> (0 = as is), never up.</summary>
        public static (int Width, int Height) OutputSize(int sourceWidth, int sourceHeight, int maxWidth)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0) return (0, 0);
            if (maxWidth <= 0 || sourceWidth <= maxWidth) return (sourceWidth, sourceHeight);
            int height = (int)Math.Max(1, Math.Round(sourceHeight * (double)maxWidth / sourceWidth));
            return (maxWidth, height);
        }

        /// <summary>The trimmed part of the video, in source seconds.</summary>
        public static double SelectionSeconds(GifOptions o) => Math.Max(0, o.EndSeconds - o.StartSeconds);

        /// <summary>How long the GIF plays once: the selection at the chosen speed, or images × time each.</summary>
        public static double GifSeconds(GifOptions o, int imageCount = 0) => o.Mode == GifSourceMode.Images
            ? imageCount * o.SecondsPerImage
            : SelectionSeconds(o) / Math.Clamp(o.Speed, 0.1, 10);

        public static int FrameCount(GifOptions o, int imageCount = 0) => o.Mode == GifSourceMode.Images
            ? imageCount
            : (int)Math.Ceiling(GifSeconds(o) * o.Fps - 1e-6);

        /// <summary>"4.3 s of video → 2.9 s GIF · 43 frames · 480×270".</summary>
        public static string DescribePlan(GifOptions o, int width, int height, int imageCount = 0)
        {
            var parts = new List<string>();
            if (o.Mode == GifSourceMode.Video)
            {
                double sel = SelectionSeconds(o);
                parts.Add(Math.Abs(o.Speed - 1) < 0.001
                    ? $"{sel:0.0} s"
                    : $"{sel:0.0} s of video → {GifSeconds(o):0.0} s at {o.Speed:0.#}×");
            }
            else
            {
                parts.Add($"{GifSeconds(o, imageCount):0.0} s");
            }
            parts.Add(GifText.Plural(FrameCount(o, imageCount), "frame", "frames"));
            if (width > 0 && height > 0) parts.Add($"{width}×{height}");
            return string.Join(" · ", parts);
        }

        /// <summary>Why this GIF can't be made as set, or null when it can.</summary>
        public static string? Problem(GifOptions o, GifVideoInfo? video, int usableImages)
        {
            if (o.Mode == GifSourceMode.Video)
            {
                if (video == null) return "Choose a video first.";
                if (SelectionSeconds(o) < 0.1) return "The selection is too short - move the end further from the start.";
            }
            else if (usableImages == 0)
            {
                return "Add some images first.";
            }
            int frames = FrameCount(o, usableImages);
            if (frames > MaxFrames)
                return $"That's {frames:N0} frames - more than a GIF should hold ({MaxFrames:N0}). Shorten the selection, lower the frame rate or speed it up.";
            return null;
        }

        /// <summary>A gentle warning shown under the plan (big files), or null.</summary>
        public static string? Warning(GifOptions o, int width, int height, int imageCount = 0)
        {
            int frames = FrameCount(o, imageCount);
            if (frames > LargeFrames) return $"{frames:N0} frames makes a big GIF. A shorter selection or a lower frame rate keeps it small.";
            if ((long)width * height > 1280L * 720 && frames > 50) return "Large frames make big GIFs. A smaller width keeps it quick to load.";
            return null;
        }

        // =================================================================
        //  ffmpeg arguments
        // =================================================================

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>palettegen/paletteuse settings for each quality.</summary>
        public static (int Colors, string Dither) QualitySettings(GifQuality q) => q switch
        {
            GifQuality.Small => (64, "bayer:bayer_scale=4"),
            GifQuality.Best => (256, "sierra2_4a"),
            _ => (128, "bayer:bayer_scale=3"),
        };

        /// <summary>The gif muxer counts repeats after the first play: -1 = once, 0 = forever.</summary>
        public static string LoopArgument(int plays) => plays switch
        {
            <= 0 => "0",
            1 => "-1",
            _ => (plays - 1).ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Video → GIF in one pass: speed, frame rate, size, then a palette
        /// made for this clip (stats_mode=diff favours what moves) and used
        /// with only the changed rectangle written per frame.
        /// </summary>
        public static string VideoFilter(GifOptions o, int sourceWidth)
        {
            var chain = new List<string>();
            if (Math.Abs(o.Speed - 1) > 0.001) chain.Add($"setpts=PTS/{Num(o.Speed)}");
            chain.Add($"fps={o.Fps.ToString(CultureInfo.InvariantCulture)}");
            if (o.MaxWidth > 0 && (sourceWidth <= 0 || sourceWidth > o.MaxWidth))
                chain.Add($"scale={o.MaxWidth.ToString(CultureInfo.InvariantCulture)}:-1:flags=lanczos");
            var (colors, dither) = QualitySettings(o.Quality);
            return $"[0:v]{string.Join(",", chain)},split[a][b];" +
                   $"[a]palettegen=max_colors={colors}:stats_mode=diff[p];" +
                   $"[b][p]paletteuse=dither={dither}:diff_mode=rectangle";
        }

        /// <summary>Full arguments for Video mode (start/length can be overridden for the size estimate's sample).</summary>
        public static List<string> VideoArguments(string input, GifOptions o, int sourceWidth, string output, double? start = null, double? length = null)
        {
            double from = start ?? o.StartSeconds;
            double len = length ?? SelectionSeconds(o);
            return new List<string>
            {
                "-nostdin", "-y",
                "-ss", Num(from), "-t", Num(len), "-i", input,
                "-an", "-sn", "-dn",
                "-filter_complex", VideoFilter(o, sourceWidth),
                "-loop", LoopArgument(o.Plays),
                "-progress", "pipe:1", "-nostats",
                "-f", "gif", output,
            };
        }

        /// <summary>One image → one PNG frame of exactly <paramref name="width"/>×<paramref name="height"/>, fitted and centred.</summary>
        public static List<string> NormalizeImageArguments(string input, int width, int height, GifFill fill, string output)
        {
            string w = width.ToString(CultureInfo.InvariantCulture), h = height.ToString(CultureInfo.InvariantCulture);
            string color = fill switch { GifFill.White => "white", GifFill.Black => "black", _ => "black@0" };
            string format = fill == GifFill.Transparent ? "rgba" : "rgb24";
            return new List<string>
            {
                "-nostdin", "-y", "-i", input,
                "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=lanczos,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color={color},format={format}",
                "-frames:v", "1", "-update", "1", output,
            };
        }

        /// <summary>The numbered PNG frames → GIF, each shown for SecondsPerImage, transparency kept when chosen.</summary>
        public static List<string> SequenceArguments(string framePattern, GifOptions o, string output)
        {
            int ms = (int)Math.Round(Math.Clamp(o.SecondsPerImage, 0.02, 60) * 1000);
            var (colors, dither) = QualitySettings(o.Quality);
            bool transparent = o.Fill == GifFill.Transparent;
            string filter = $"split[a][b];[a]palettegen=max_colors={colors}:reserve_transparent={(transparent ? 1 : 0)}[p];" +
                            $"[b][p]paletteuse=dither={dither}" + (transparent ? ":alpha_threshold=128" : "");
            return new List<string>
            {
                "-nostdin", "-y",
                "-framerate", $"1000/{ms.ToString(CultureInfo.InvariantCulture)}",
                "-i", framePattern,
                "-filter_complex", filter,
                "-loop", LoopArgument(o.Plays),
                "-progress", "pipe:1", "-nostats",
                "-f", "gif", output,
            };
        }

        /// <summary>Scales a sample's size up to the whole GIF (the first frame is always the biggest, so it errs on the high side).</summary>
        public static long ScaleEstimate(long sampleBytes, double sampleUnits, double totalUnits) =>
            sampleUnits <= 0 ? sampleBytes : (long)(sampleBytes * Math.Max(1, totalUnits / sampleUnits));

        /// <summary>The name suggested in the Output card: the video's name, or the first image's folder.</summary>
        public static string SuggestedName(GifSourceMode mode, GifVideoInfo? video, IReadOnlyList<GifImageItem> images)
        {
            if (mode == GifSourceMode.Video && video != null) return Path.GetFileNameWithoutExtension(video.FullPath);
            if (mode == GifSourceMode.Images && images.Count > 0)
            {
                string folder = Path.GetFileName(images[0].Folder.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(folder) ? "animation" : folder;
            }
            return "animation";
        }

        // =================================================================
        //  The image list (Images mode)
        // =================================================================

        public static List<GifImageGroup> BuildQueueGroups(IEnumerable<GifImageSource> sources, IReadOnlyCollection<GifImageItem> items, bool includeSubfolders)
        {
            var list = sources.ToList();
            var groups = list.Where(s => s.IsFolder)
                .Select(s =>
                {
                    string root = s.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    int count = items.Count(i => i.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                    return new GifImageGroup { FolderPath = s.FullPath, IsFolder = true, IncludeSubfolders = includeSubfolders, ImageCount = count };
                })
                .ToList();
            groups.AddRange(list.Where(s => !s.IsFolder)
                .GroupBy(s => Path.GetDirectoryName(s.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new GifImageGroup { FolderPath = g.Key, FilePaths = g.Select(s => s.FullPath).ToList(), ImageCount = g.Count() }));
            return groups;
        }

        /// <summary>The images behind the added sources, folder by folder in name order, without duplicates.</summary>
        public static List<(string Path, long Size, DateTime Modified)> ListImages(IReadOnlyList<GifImageSource> sources, bool includeSubfolders, CancellationToken token)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<(string, long, DateTime)>();
            var options = new EnumerationOptions { RecurseSubdirectories = includeSubfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };

            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!source.IsFolder)
                {
                    if (File.Exists(source.FullPath) && seen.Add(source.FullPath)) result.Add(Describe(source.FullPath));
                    continue;
                }
                if (!Directory.Exists(source.FullPath)) continue;
                var found = new List<(string, long, DateTime)>();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(source.FullPath, "*", options))
                    {
                        token.ThrowIfCancellationRequested();
                        if (IsImage(file) && seen.Add(file)) found.Add(Describe(file));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // the folder went away while listing: keep what was found
                }
                result.AddRange(found.OrderBy(f => Path.GetDirectoryName(f.Item1) ?? "", NaturalStringComparer.Instance)
                                     .ThenBy(f => Path.GetFileName(f.Item1), NaturalStringComparer.Instance));
            }
            return result;
        }

        private static (string, long, DateTime) Describe(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return (path, info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (path, 0, DateTime.MinValue);
            }
        }

        public static void Renumber(IList<GifImageItem> list)
        {
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        }

        /// <summary>Moves the chosen frames one place up (-1) or down (+1) as a block. Returns true if anything moved.</summary>
        public static bool Move(IList<GifImageItem> list, IReadOnlyCollection<GifImageItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<GifImageItem>(chosen);
            bool moved = false;
            if (direction < 0)
            {
                for (int i = 1; i < list.Count; i++)
                    if (set.Contains(list[i]) && !set.Contains(list[i - 1]))
                    {
                        (list[i - 1], list[i]) = (list[i], list[i - 1]);
                        moved = true;
                    }
            }
            else
            {
                for (int i = list.Count - 2; i >= 0; i--)
                    if (set.Contains(list[i]) && !set.Contains(list[i + 1]))
                    {
                        (list[i + 1], list[i]) = (list[i], list[i + 1]);
                        moved = true;
                    }
            }
            if (moved) Renumber(list);
            return moved;
        }

        /// <summary>Name order ("frame 2" before "frame 10") or date order, oldest first.</summary>
        public static List<GifImageItem> Sorted(IEnumerable<GifImageItem> items, bool byDate) => byDate
            ? items.OrderBy(i => i.Modified).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList()
            : items.OrderBy(i => i.Folder, NaturalStringComparer.Instance).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "gif_maker.json");

        public static GifSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new GifSettings();
                var s = JsonSerializer.Deserialize<GifSettings>(File.ReadAllText(file), JsonOptions) ?? new GifSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = GifSourceMode.Video;
                if (!Enum.IsDefined(s.Quality)) s.Quality = GifQuality.Balanced;
                if (!Enum.IsDefined(s.Fill)) s.Fill = GifFill.Transparent;
                if (!FrameRates.Contains(s.Fps)) s.Fps = 15;
                if (!Widths.Contains(s.MaxWidth)) s.MaxWidth = 480;
                if (!PlayCounts.Contains(s.Plays)) s.Plays = 0;
                if (!Speeds.Any(v => Math.Abs(v - s.Speed) < 0.001)) s.Speed = 1;
                if (!ImageDurations.Any(v => Math.Abs(v - s.SecondsPerImage) < 0.001)) s.SecondsPerImage = 0.5;
                return s;
            }
            catch (Exception)
            {
                return new GifSettings();
            }
        }

        public static void SaveSettings(GifSettings settings, string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string temp = file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // settings are a convenience: never fail the tool over them
            }
        }
    }
}
