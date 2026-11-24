using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Video to Audio - planning: which files count, ffmpeg arguments per
    /// format, output names, size estimates, the Files card rows and the
    /// saved settings. Pure logic (no WPF, no processes); probing and the
    /// run are in VideoToAudioManager.Run.cs.
    /// </summary>
    public static partial class VideoToAudioManager
    {
        /// <summary>What Add files / folders pick up.</summary>
        public static readonly string[] VideoExtensions =
        {
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg",
            ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob", ".asf", ".divx", ".f4v",
        };

        public static readonly int[] Bitrates = { 128, 192, 256, 320 };

        public static bool IsVideo(string path) =>
            VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        public static string Extension(VtaFormat format) => format switch
        {
            VtaFormat.M4a => ".m4a",
            VtaFormat.Wav => ".wav",
            VtaFormat.Flac => ".flac",
            _ => ".mp3",
        };

        /// <summary>WAV and FLAC keep every bit: no bitrate to choose.</summary>
        public static bool IsLossless(VtaFormat format) => format is VtaFormat.Wav or VtaFormat.Flac;

        public static int? SampleRateHz(VtaSampleRate rate) => rate switch
        {
            VtaSampleRate.Hz44100 => 44100,
            VtaSampleRate.Hz48000 => 48000,
            _ => null,
        };

        /// <summary>The tracks a row turns into files.</summary>
        public static IReadOnlyList<VtaAudioTrack> TracksToExtract(VtaFileItem item, VtaTrackMode mode)
        {
            if (item.Tracks.Count == 0) return Array.Empty<VtaAudioTrack>();
            if (mode == VtaTrackMode.Every) return item.Tracks;
            return new[] { item.SelectedTrack ?? item.Tracks[0] };
        }

        /// <summary>"Movie.mp3", or "Movie (spa).mp3" when every track of a multi-track video is saved.</summary>
        public static string OutputFileName(string sourcePath, VtaAudioTrack track, int trackCount, VtaFormat format)
        {
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            if (trackCount > 1) name += $" ({track.FileTag})";
            return OutputFolderManager.SafeName(name) + Extension(format);
        }

        /// <summary>
        /// ffmpeg arguments for one output file (input and output included).
        /// The output format is named (-f) because it is written to a .tmp
        /// name first and moved into place when it's finished.
        /// </summary>
        public static List<string> BuildArguments(string input, VtaAudioTrack track, VtaOptions o, string tempOutput)
        {
            var a = new List<string> { "-nostdin", "-y", "-i", input, "-map", $"0:a:{track.Number}", "-vn", "-sn", "-dn" };

            // Tags: the video's own (title, artist, date...) or none, then the sidebar's on top.
            a.AddRange(new[] { "-map_metadata", o.KeepTags ? "0" : "-1" });
            if (o.TitleFromFileName) a.AddRange(new[] { "-metadata", "title=" + Path.GetFileNameWithoutExtension(input) });
            if (!string.IsNullOrWhiteSpace(o.Artist)) a.AddRange(new[] { "-metadata", "artist=" + o.Artist.Trim() });
            if (!string.IsNullOrWhiteSpace(o.Album)) a.AddRange(new[] { "-metadata", "album=" + o.Album.Trim() });

            string kbps = Math.Clamp(o.BitrateKbps, 32, 320).ToString(CultureInfo.InvariantCulture) + "k";
            switch (o.Format)
            {
                case VtaFormat.M4a:
                    a.AddRange(new[] { "-c:a", "aac", "-b:a", kbps, "-movflags", "+faststart", "-f", "ipod" });
                    break;
                case VtaFormat.Wav:
                    a.AddRange(new[] { "-c:a", "pcm_s16le", "-rf64", "auto", "-f", "wav" });
                    break;
                case VtaFormat.Flac:
                    a.AddRange(new[] { "-c:a", "flac", "-compression_level", "5", "-f", "flac" });
                    break;
                default:
                    a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", kbps, "-id3v2_version", "3", "-write_id3v1", "1", "-f", "mp3" });
                    break;
            }

            // MP3 only goes up to 48 kHz; a 96 kHz track would make ffmpeg fail, so cap it.
            int? rate = SampleRateHz(o.SampleRate);
            if (rate == null && o.Format == VtaFormat.Mp3 && track.SampleRate > 48000) rate = 48000;
            if (rate != null) a.AddRange(new[] { "-ar", rate.Value.ToString(CultureInfo.InvariantCulture) });

            a.AddRange(new[] { "-progress", "pipe:1", "-nostats", tempOutput });
            return a;
        }

        /// <summary>Rough size of one output: bitrate × length, or the PCM size for WAV (FLAC ≈ 55% of it).</summary>
        public static long? EstimateBytes(TimeSpan? duration, VtaAudioTrack track, VtaOptions o)
        {
            if (duration is not TimeSpan d || d <= TimeSpan.Zero) return null;
            double seconds = d.TotalSeconds;
            int rate = SampleRateHz(o.SampleRate) ?? (track.SampleRate > 0 ? track.SampleRate : 48000);
            int channels = track.Channels > 0 ? track.Channels : 2;
            double pcm = seconds * rate * channels * 2;
            return o.Format switch
            {
                VtaFormat.Wav => (long)pcm,
                VtaFormat.Flac => (long)(pcm * 0.55),
                _ => (long)(seconds * o.BitrateKbps * 1000 / 8),
            };
        }

        /// <summary>The row's size text: "≈ 4.2 MB" for every file it will make.</summary>
        public static string? EstimateText(VtaFileItem item, VtaOptions o)
        {
            long total = 0;
            foreach (var track in TracksToExtract(item, o.TrackMode))
            {
                if (EstimateBytes(item.Duration, track, o) is not long b) return null;
                total += b;
            }
            return total > 0 ? "≈ " + VtaText.FormatBytes(total) : null;
        }

        /// <summary>"12 videos · 1:23:45" for the summary pill.</summary>
        public static string DescribeList(IReadOnlyCollection<VtaFileItem> items)
        {
            if (items.Count == 0) return "No videos";
            string text = VtaText.Plural(items.Count, "video", "videos");
            var known = items.Where(i => i.Duration.HasValue).ToList();
            if (known.Count > 0)
            {
                var total = TimeSpan.FromTicks(known.Sum(i => i.Duration!.Value.Ticks));
                text += " · " + (known.Count < items.Count ? "at least " : "") + VtaText.FormatTime(total);
            }
            return text;
        }

        /// <summary>Total estimate for the rows that will run ("≈ 340 MB"), or null when nothing is known yet.</summary>
        public static string? DescribeEstimate(IEnumerable<VtaFileItem> items, VtaOptions o)
        {
            long total = 0;
            bool any = false;
            foreach (var item in items.Where(i => i.CanRun))
                foreach (var track in TracksToExtract(item, o.TrackMode))
                    if (EstimateBytes(item.Duration, track, o) is long b)
                    {
                        total += b;
                        any = true;
                    }
            return any ? "≈ " + VtaText.FormatBytes(total) : null;
        }

        /// <summary>Plain-sentence note under the format chips.</summary>
        public static string FormatNote(VtaFormat format) => format switch
        {
            VtaFormat.M4a => "AAC in an .m4a file: small, plays on phones, iTunes and most players.",
            VtaFormat.Wav => "Uncompressed, so files are large. Bitrate doesn't apply.",
            VtaFormat.Flac => "Lossless and about half the size of WAV. Bitrate doesn't apply.",
            _ => "Plays everywhere. 192 kbps sounds good for most videos; 320 for music.",
        };

        public static string DescribeResult(VtaRunResult r)
        {
            var parts = new List<string>();
            if (r.Done > 0) parts.Add($"Saved {VtaText.Plural(r.Files.Count, "file", "files")} ({VtaText.FormatBytes(r.OutputBytes)})");
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed - hover a row to see why");
            if (r.Cancelled > 0) parts.Add($"{r.Cancelled:N0} cancelled");
            if (parts.Count == 0) return "Nothing was extracted.";
            return string.Join(" · ", parts) + $" · {r.Elapsed.TotalSeconds:0} s";
        }

        // =================================================================
        //  The Files card
        // =================================================================

        /// <summary>Card rows: each added folder (with how many videos it gave), then loose files grouped by folder.</summary>
        public static List<VtaQueueGroup> BuildQueueGroups(IEnumerable<VtaSource> sources, IReadOnlyCollection<VtaFileItem> items, bool includeSubfolders)
        {
            var list = sources.ToList();
            var groups = list.Where(s => s.IsFolder)
                .Select(s =>
                {
                    string root = s.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    int count = items.Count(i => i.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                    return new VtaQueueGroup { FolderPath = s.FullPath, IsFolder = true, IncludeSubfolders = includeSubfolders, VideoCount = count };
                })
                .ToList();
            groups.AddRange(list.Where(s => !s.IsFolder)
                .GroupBy(s => Path.GetDirectoryName(s.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new VtaQueueGroup { FolderPath = g.Key, FilePaths = g.Select(s => s.FullPath).ToList(), VideoCount = g.Count() }));
            return groups;
        }

        /// <summary>
        /// Lists the videos behind the added sources (folders read with or
        /// without subfolders; unreadable folders skipped), in name order, without duplicates.
        /// </summary>
        public static List<(string Path, long Size)> ListVideos(IReadOnlyList<VtaSource> sources, bool includeSubfolders, CancellationToken token)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<(string, long)>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSubfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            };

            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!source.IsFolder)
                {
                    if (File.Exists(source.FullPath) && seen.Add(source.FullPath))
                        result.Add((source.FullPath, SafeLength(source.FullPath)));
                    continue;
                }
                if (!Directory.Exists(source.FullPath)) continue;

                var found = new List<(string, long)>();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(source.FullPath, "*", options))
                    {
                        token.ThrowIfCancellationRequested();
                        if (IsVideo(file) && seen.Add(file)) found.Add((file, SafeLength(file)));
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

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "video_to_audio.json");

        public static VtaSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new VtaSettings();
                var s = JsonSerializer.Deserialize<VtaSettings>(File.ReadAllText(file), JsonOptions) ?? new VtaSettings();
                if (!Enum.IsDefined(s.Format)) s.Format = VtaFormat.Mp3;
                if (!Enum.IsDefined(s.SampleRate)) s.SampleRate = VtaSampleRate.Original;
                if (!Enum.IsDefined(s.TrackMode)) s.TrackMode = VtaTrackMode.Chosen;
                if (!Bitrates.Contains(s.BitrateKbps)) s.BitrateKbps = 192;
                return s;
            }
            catch (Exception)
            {
                return new VtaSettings();
            }
        }

        public static void SaveSettings(VtaSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);
    }
}
