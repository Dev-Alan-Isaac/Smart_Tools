using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Video Stitcher backend, split in three:
    /// <list type="bullet">
    /// <item>VideoEditorManager.cs (this file) - no side effects: reading
    /// ffprobe's answer, deciding whether clips can be joined as they are,
    /// the ffmpeg commands, splitting, trims, names, the list and settings.</item>
    /// <item>VideoEditorManager.Render.cs - running ffprobe/ffmpeg: the
    /// stitch, the preview, encoder fallback, temporary files.</item>
    /// <item>VideoEditorManager.Files.cs - finding videos in folders.</item>
    /// </list>
    /// ffmpeg is found the same way as Video Replay (Engines\FFMPEG, then
    /// PATH) and the graphics-card encoders are shared with it.
    /// </summary>
    public static partial class VideoEditorManager
    {
        public static readonly IReadOnlyList<double> FadeChoices = new[] { 0, 0.5, 1.0, 2.0 };
        public static readonly IReadOnlyList<int> SplitChoices = new[] { 0, 15, 30, 60 };

        // =================================================================
        //  ffprobe
        // =================================================================

        /// <summary>
        /// Reads "ffprobe -print_format json -show_format -show_streams".
        /// Cover art (attached pictures) doesn't count as video. Null when
        /// the text isn't ffprobe JSON.
        /// </summary>
        public static VideoProbe? ParseProbe(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                double seconds = 0;
                if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
                    seconds = ParseDouble(d.GetString());

                JsonElement? video = null, audio = null;
                if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        string type = Str(s, "codec_type");
                        if (type == "video" && video == null && !IsAttachedPicture(s)) video = s;
                        else if (type == "audio" && audio == null) audio = s;
                    }
                }
                if (video == null && audio == null) return null;

                if (seconds <= 0)
                {
                    // Some files only store the length on the stream.
                    if (video is { } vs) seconds = ParseDouble(Str(vs, "duration"));
                    if (seconds <= 0 && audio is { } au) seconds = ParseDouble(Str(au, "duration"));
                }

                var v = video;
                var a = audio;
                return new VideoProbe(
                    Seconds: seconds,
                    HasVideo: v != null,
                    VideoCodec: v is { } v1 ? Str(v1, "codec_name") : "",
                    Width: v is { } v2 ? Int(v2, "width") : 0,
                    Height: v is { } v3 ? Int(v3, "height") : 0,
                    Fps: v is { } v4 ? PickRate(ParseRate(Str(v4, "r_frame_rate")), ParseRate(Str(v4, "avg_frame_rate"))) : 0,
                    PixelFormat: v is { } v5 ? Str(v5, "pix_fmt") : "",
                    Rotation: v is { } v6 ? ReadRotation(v6) : 0,
                    HasAudio: a != null,
                    AudioCodec: a is { } a1 ? Str(a1, "codec_name") : "",
                    SampleRate: a is { } a2 ? (int)ParseDouble(Str(a2, "sample_rate")) : 0,
                    Channels: a is { } a3 ? Int(a3, "channels") : 0);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsAttachedPicture(JsonElement s) =>
            s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;

        /// <summary>Rotation flag from the "rotate" tag (older files) or the display matrix (newer ffprobe). Normalised to 0/90/180/270.</summary>
        private static int ReadRotation(JsonElement s)
        {
            double r = 0;
            if (s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rot))
                r = ParseDouble(rot.ValueKind == JsonValueKind.String ? rot.GetString() : rot.ToString());
            if (r == 0 && s.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array)
                foreach (var sd in side.EnumerateArray())
                    if (sd.TryGetProperty("rotation", out var rr) && rr.ValueKind == JsonValueKind.Number)
                        r = rr.GetDouble();
            int deg = (int)Math.Round(r) % 360;
            if (deg < 0) deg += 360;
            return deg;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString() : "";

        private static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : 0;

        private static double ParseDouble(string? text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;

        /// <summary>
        /// The stream's base rate ("r_frame_rate") when it's a real frame
        /// rate; the average otherwise (variable-rate WebM often reports a
        /// base of 1000). The average alone drifts when the sound runs longer
        /// than the picture (29.82 instead of 30).
        /// </summary>
        private static double PickRate(double baseRate, double average) =>
            baseRate is > 0 and <= 120 ? baseRate : average > 0 ? average : 0;

        /// <summary>"30000/1001" -> 29.97; "0/0" or junk -> 0.</summary>
        public static double ParseRate(string? rate)
        {
            if (string.IsNullOrWhiteSpace(rate)) return 0;
            var parts = rate.Split('/');
            if (parts.Length == 2)
            {
                double n = ParseDouble(parts[0]), dn = ParseDouble(parts[1]);
                return dn > 0 && n > 0 ? n / dn : 0;
            }
            return Math.Max(0, ParseDouble(rate));
        }

        // =================================================================
        //  Formats
        // =================================================================

        public static string Extension(EditOutputFormat f) => f switch
        {
            EditOutputFormat.Mkv => ".mkv",
            EditOutputFormat.Mov => ".mov",
            EditOutputFormat.Webm => ".webm",
            EditOutputFormat.Avi => ".avi",
            EditOutputFormat.Flv => ".flv",
            _ => ".mp4",
        };

        /// <summary>ffmpeg's name for the container (given explicitly, because the temporary file's name doesn't end in it).</summary>
        public static string Muxer(EditOutputFormat f) => f switch
        {
            EditOutputFormat.Mkv => "matroska",
            EditOutputFormat.Mov => "mov",
            EditOutputFormat.Webm => "webm",
            EditOutputFormat.Avi => "avi",
            EditOutputFormat.Flv => "flv",
            _ => "mp4",
        };

        /// <summary>
        /// Codecs each container can hold when clips are copied as they are.
        /// The old code tried a copy and only fell back on an error, so
        /// H.264 into WebM failed - and then its H.264 re-encode into WebM
        /// failed too.
        /// </summary>
        public static bool ContainerAccepts(EditOutputFormat f, string videoCodec, string? audioCodec)
        {
            static bool In(string? c, params string[] list) => c != null && list.Contains(c, StringComparer.OrdinalIgnoreCase);
            bool audioOk(params string[] list) => audioCodec == null || In(audioCodec, list);
            return f switch
            {
                EditOutputFormat.Mkv => true,
                EditOutputFormat.Webm => In(videoCodec, "vp8", "vp9", "av1") && audioOk("opus", "vorbis"),
                EditOutputFormat.Mp4 or EditOutputFormat.Mov => In(videoCodec, "h264", "hevc", "mpeg4", "av1") && audioOk("aac", "mp3", "alac", "ac3", "eac3"),
                EditOutputFormat.Flv => In(videoCodec, "h264") && audioOk("aac", "mp3"),
                EditOutputFormat.Avi => In(videoCodec, "h264", "mpeg4", "mjpeg", "msmpeg4v3") && audioOk("mp3", "aac", "ac3", "pcm_s16le"),
                _ => false,
            };
        }

        /// <summary>Audio encoder for each container (WebM needs Opus, AVI plays best with MP3).</summary>
        public static List<string> AudioArgs(EditOutputFormat f, bool preview = false) => f switch
        {
            EditOutputFormat.Webm when !preview => new() { "-c:a", "libopus", "-b:a", "128k" },
            EditOutputFormat.Avi when !preview => new() { "-c:a", "libmp3lame", "-b:a", "192k" },
            _ => new() { "-c:a", "aac", "-b:a", preview ? "128k" : "160k" },
        };

        public static readonly IReadOnlyList<string> WebmVideoArgs = new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4" };
        public static readonly IReadOnlyList<string> PreviewVideoArgs = new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28" };

        // =================================================================
        //  Joining as they are, or re-encoding
        // =================================================================

        /// <summary>
        /// True when the clips can simply be put one after another (seconds,
        /// no quality loss): no fades or trims, and every clip has the same
        /// codecs, size, frame rate and audio layout - and the container
        /// takes them. Otherwise <paramref name="reason"/> says why they're
        /// re-encoded. The old code copied whenever ffmpeg didn't error,
        /// which made broken videos from clips of different sizes.
        /// </summary>
        public static bool CanJoinWithoutReencoding(IReadOnlyList<(EditClip Clip, VideoProbe Probe)> clips, EditOptions options, out string reason)
        {
            reason = "";
            if (clips.Count == 0) return false;
            if (options.FadeSeconds > 0)
            {
                reason = "fades are added";
                return false;
            }
            if (clips.Any(c => c.Clip.IsTrimmed))
            {
                reason = "some clips are trimmed";
                return false;
            }
            var first = clips[0].Probe;
            foreach (var (_, p) in clips)
            {
                if (!p.HasVideo || !string.Equals(p.VideoCodec, first.VideoCodec, StringComparison.OrdinalIgnoreCase) || p.Width != first.Width || p.Height != first.Height
                    || p.Rotation != first.Rotation || !string.Equals(p.PixelFormat, first.PixelFormat, StringComparison.OrdinalIgnoreCase) || Math.Abs(p.Fps - first.Fps) > 0.01)
                {
                    reason = "the clips have different sizes, frame rates or formats";
                    return false;
                }
                if (p.HasAudio != first.HasAudio || (p.HasAudio && (!string.Equals(p.AudioCodec, first.AudioCodec, StringComparison.OrdinalIgnoreCase) || p.SampleRate != first.SampleRate || p.Channels != first.Channels)))
                {
                    reason = "the clips' sound is in different formats";
                    return false;
                }
            }
            if (!ContainerAccepts(options.Format, first.VideoCodec, first.HasAudio ? first.AudioCodec : null))
            {
                reason = $"{Extension(options.Format).TrimStart('.').ToUpperInvariant()} can't hold {first.VideoCodec.ToUpperInvariant()} video as it is";
                return false;
            }
            var (w, h, _) = TargetFrame(first, options.Size);
            if (w != Even(first.DisplayWidth) || h != Even(first.DisplayHeight))
            {
                reason = "the picture size changes";
                return false;
            }
            return true;
        }

        /// <summary>Picture size and frame rate everything is fitted into: the first clip's, or 1080p / 720p in its orientation.</summary>
        public static (int Width, int Height, double Fps) TargetFrame(VideoProbe first, EditOutputSize size)
        {
            int w = first.DisplayWidth > 0 ? first.DisplayWidth : 1280;
            int h = first.DisplayHeight > 0 ? first.DisplayHeight : 720;
            bool portrait = h > w;
            (w, h) = size switch
            {
                EditOutputSize.P1080 => portrait ? (1080, 1920) : (1920, 1080),
                EditOutputSize.P720 => portrait ? (720, 1280) : (1280, 720),
                _ => (w, h),
            };
            double fps = first.Fps is > 1 and <= 120 ? Math.Min(60, Math.Round(first.Fps, 3)) : 30;
            return (Even(w), Even(h), fps);
        }

        /// <summary>Preview size: the target scaled to at most 640 × 360 (or 360 × 640), so the preview renders quickly.</summary>
        public static (int Width, int Height, double Fps) PreviewFrame(VideoProbe first)
        {
            var (w, h, fps) = TargetFrame(first, EditOutputSize.FirstClip);
            double scale = Math.Min(1, Math.Min(640.0 / Math.Max(w, h), 360.0 / Math.Min(w, h)));
            return (Even((int)(w * scale)), Even((int)(h * scale)), Math.Min(fps, 30));
        }

        private static int Even(int n) => Math.Max(2, n - n % 2);

        /// <summary>Start and length of the used part of a clip, kept inside the file (the old code faded from 0 when the length was unknown).</summary>
        public static (double Start, double Length) UsedPart(EditClip clip, double fileSeconds)
        {
            double start = Math.Clamp(clip.TrimStart?.TotalSeconds ?? 0, 0, Math.Max(0, fileSeconds - 0.1));
            double end = clip.TrimEnd?.TotalSeconds is double e && e > start ? Math.Min(e, fileSeconds) : fileSeconds;
            return (start, Math.Max(0.1, end - start));
        }

        /// <summary>A fade can't take more than a third of a short clip.</summary>
        public static double EffectiveFade(double fade, double length) => fade <= 0 ? 0 : Math.Min(fade, length / 3);

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// ffmpeg arguments that turn one clip into a part ready to join:
        /// the used part only, fitted into the target frame (letterboxed, never
        /// stretched), constant frame rate, stereo 48 kHz sound (silence when
        /// the clip has none but others do), and fades if chosen.
        /// </summary>
        public static List<string> NormalizeArgs(
            string source, VideoProbe probe, double start, double length,
            (int Width, int Height, double Fps) frame, double fade, bool withAudio,
            IEnumerable<string> videoArgs, IEnumerable<string> audioArgs, string output, string muxer = "mp4")
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
            if (start > 0) args.AddRange(new[] { "-ss", Num(start) });
            args.AddRange(new[] { "-i", source });
            bool silence = withAudio && !probe.HasAudio;
            if (silence) args.AddRange(new[] { "-f", "lavfi", "-t", Num(length), "-i", "anullsrc=r=48000:cl=stereo" });

            double f = EffectiveFade(fade, length);
            string fadeV = f > 0 ? $",fade=t=in:st=0:d={Num(f)},fade=t=out:st={Num(length - f)}:d={Num(f)}" : "";
            string fadeA = f > 0 ? $",afade=t=in:st=0:d={Num(f)},afade=t=out:st={Num(length - f)}:d={Num(f)}" : "";
            var graph = new StringBuilder();
            graph.Append($"[0:v:0]scale={frame.Width}:{frame.Height}:force_original_aspect_ratio=decrease,")
                 .Append($"pad={frame.Width}:{frame.Height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={Num(frame.Fps)},format=yuv420p{fadeV}[v]");
            if (withAudio)
                graph.Append($";[{(silence ? "1:a:0" : "0:a:0")}]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo{fadeA}[a]");

            args.AddRange(new[] { "-filter_complex", graph.ToString(), "-map", "[v]" });
            if (withAudio) args.AddRange(new[] { "-map", "[a]" });
            args.AddRange(new[] { "-t", Num(length) });
            args.AddRange(videoArgs);
            if (withAudio) args.AddRange(audioArgs);
            else args.Add("-an");
            args.AddRange(new[] { "-sn", "-dn", "-progress", "pipe:1", "-nostats", "-f", muxer, output });
            return args;
        }

        /// <summary>
        /// Container for the ready parts: MP4 keeps exact frame timing (MKV
        /// rounds to milliseconds, which made a 30 fps result report 59.94);
        /// WebM's VP9/Opus parts go in Matroska.
        /// </summary>
        public static (string Muxer, string Extension) PartContainer(EditOutputFormat format) =>
            format == EditOutputFormat.Webm ? ("matroska", ".mkv") : ("mp4", ".mp4");

        /// <summary>ffmpeg arguments that put ready parts (or matching clips) one after another without re-encoding.</summary>
        public static List<string> JoinArgs(string listFile, EditOutputFormat format, bool withAudio, string output)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", listFile, "-map", "0:v:0" };
            if (withAudio) args.AddRange(new[] { "-map", "0:a:0?" });
            args.AddRange(new[] { "-c", "copy" });
            if (format is EditOutputFormat.Mp4 or EditOutputFormat.Mov) args.AddRange(new[] { "-movflags", "+faststart" });
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", "-f", Muxer(format), output });
            return args;
        }

        /// <summary>ffmpeg's concat list. A ' in a path is written as '\'' (the old code did this too).</summary>
        public static string ConcatList(IEnumerable<string> paths)
        {
            var sb = new StringBuilder("ffconcat version 1.0\n");
            foreach (string p in paths) sb.Append("file '").Append(p.Replace("'", "'\\''")).Append("'\n");
            return sb.ToString();
        }

        /// <summary>Seconds done from a "-progress" line ("out_time_us=1234567"), else null.</summary>
        public static double? ParseProgressSeconds(string line)
        {
            foreach (string key in new[] { "out_time_us=", "out_time_ms=" })   // both are microseconds
                if (line.StartsWith(key, StringComparison.Ordinal) && long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
                    return us / 1_000_000.0;
            return null;
        }

        /// <summary>Clips into files no longer than <paramref name="splitSeconds"/> (0 = one file). A clip is never cut in two.</summary>
        public static List<List<T>> BuildSegments<T>(IReadOnlyList<T> clips, Func<T, double> seconds, double splitSeconds)
        {
            var segments = new List<List<T>>();
            var current = new List<T>();
            double total = 0;
            foreach (var clip in clips)
            {
                double s = Math.Max(0, seconds(clip));
                if (splitSeconds > 0 && current.Count > 0 && total + s > splitSeconds)
                {
                    segments.Add(current);
                    current = new List<T>();
                    total = 0;
                }
                current.Add(clip);
                total += s;
            }
            if (current.Count > 0) segments.Add(current);
            return segments;
        }

        /// <summary>The useful line from ffmpeg's error output, in plain words where it's a common one.</summary>
        public static string PlainFfmpegError(IEnumerable<string> errorLines)
        {
            var lines = errorLines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            string? Find(string text) => lines.LastOrDefault(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
            if (Find("Invalid data found") != null || Find("moov atom not found") != null) return "The clip is damaged or not a video ffmpeg can read.";
            if (Find("No such file") != null) return "The clip is no longer there.";
            if (Find("Permission denied") != null) return "Windows didn't allow the file to be read or written.";
            if (Find("No space left") != null) return "The disk is full.";
            if (Find("Unknown encoder") is string u) return "This ffmpeg can't make that format: " + u;
            return lines.LastOrDefault(l => !l.StartsWith("Conversion failed", StringComparison.OrdinalIgnoreCase)) ?? "ffmpeg stopped with an error.";
        }

        // =================================================================
        //  Names
        // =================================================================

        public const string DefaultFileName = "Stitched video";

        /// <summary>Videos\Smart Tools\Video Editor (kept apart from Convert, which uses Videos\Smart Tools\Convert).</summary>
        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Smart Tools", "Video Editor");

        /// <summary>
        /// A safe name without extension. A typed video extension is dropped
        /// ("Trip.mp4" -> "Trip"), but other dots stay - the old code cut
        /// "Trip v1.2" down to "Trip v1".
        /// </summary>
        public static string CleanFileName(string? name)
        {
            string n = (name ?? "").Trim();
            if (VideoReplayManager.IsVideo(n)) n = Path.GetFileNameWithoutExtension(n);
            n = new string(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            return n.Length == 0 ? DefaultFileName : n;
        }

        /// <summary>The finished file names: "Trip.mp4", or "Trip_1.mp4", "Trip_2.mp4" … when split.</summary>
        public static List<string> OutputNames(string folder, string fileName, EditOutputFormat format, int parts)
        {
            string stem = CleanFileName(fileName);
            string ext = Extension(format);
            return parts <= 1
                ? new List<string> { Path.Combine(folder, stem + ext) }
                : Enumerable.Range(1, parts).Select(i => Path.Combine(folder, $"{stem}_{i}{ext}")).ToList();
        }

        /// <summary>"Trip.mp4" -> "Trip (2).mp4" when taken. The old code deleted the existing file without asking.</summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        // =================================================================
        //  Trims
        // =================================================================

        public static readonly TimeSpan MinimumLength = TimeSpan.FromSeconds(0.5);

        /// <summary>Sets where the clip starts. Returns why not, or null when done.</summary>
        public static string? SetTrimStart(EditClip clip, TimeSpan position)
        {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            var end = clip.TrimEnd ?? clip.Duration;
            if (end is TimeSpan e && position > e - MinimumLength) return "The start has to be before the end.";
            clip.TrimStart = position < TimeSpan.FromSeconds(0.05) ? null : position;
            return null;
        }

        /// <summary>Sets where the clip ends. Returns why not, or null when done.</summary>
        public static string? SetTrimEnd(EditClip clip, TimeSpan position)
        {
            var start = clip.TrimStart ?? TimeSpan.Zero;
            if (position < start + MinimumLength) return "The end has to be after the start.";
            clip.TrimEnd = clip.Duration is TimeSpan d && position > d - TimeSpan.FromSeconds(0.05) ? null : position;
            return null;
        }

        public static void ClearTrim(EditClip clip)
        {
            clip.TrimStart = null;
            clip.TrimEnd = null;
        }

        // =================================================================
        //  The list
        // =================================================================

        /// <summary>Adds clips not already listed as one sidebar group (a set makes the check instant). Returns the new clips.</summary>
        public static List<EditClip> AddToList(IList<EditClip> list, ICollection<EditClipGroup> groups, IReadOnlyList<EditFile> files, string? folderPath, bool isFolder, bool includeSubfolders)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<EditClip>();
            foreach (var f in files)
            {
                if (!known.Add(f.FullPath)) continue;
                added.Add(new EditClip { FullPath = f.FullPath, SizeBytes = f.SizeBytes, Modified = f.Modified, Number = list.Count + added.Count + 1 });
            }
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new EditClipGroup
            {
                FolderPath = folderPath ?? (folders.Count == 1 ? folders[0] : null),
                IsFolder = isFolder,
                IncludeSubfolders = includeSubfolders,
            };
            foreach (var clip in added)
            {
                clip.Group = group;
                group.Items.Add(clip);
                list.Add(clip);
            }
            groups.Add(group);
            return added;
        }

        /// <summary>Removes clips from the list and their sidebar groups (empty groups go).</summary>
        public static void RemoveFromList(IList<EditClip> list, ICollection<EditClipGroup> groups, IReadOnlyCollection<EditClip> clips)
        {
            if (clips.Count == 0) return;
            var gone = new HashSet<EditClip>(clips);
            for (int i = list.Count - 1; i >= 0; i--)
                if (gone.Contains(list[i])) list.RemoveAt(i);
            foreach (var group in clips.Select(c => c.Group).OfType<EditClipGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
            Renumber(list);
        }

        /// <summary>
        /// After "Include subfolders" changes: brings a folder group in line
        /// with a fresh scan. Clips no longer found come off the list, new
        /// ones go at the end; the rest keep their place, tick and trim.
        /// Returns both so the view can read lengths / stop a removed clip.
        /// </summary>
        public static (List<EditClip> Added, List<EditClip> Removed) ApplyRescan(
            IList<EditClip> list,
            ICollection<EditClipGroup> groups,
            EditClipGroup group,
            IReadOnlyList<EditFile> files,
            bool includeSubfolders)
        {
            var found = new HashSet<string>(files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
            var removed = group.Items.Where(c => !found.Contains(c.FullPath)).ToList();
            if (removed.Count > 0)
            {
                var gone = new HashSet<EditClip>(removed);
                for (int i = list.Count - 1; i >= 0; i--)
                    if (gone.Contains(list[i])) list.RemoveAt(i);
                group.Items.RemoveAll(gone.Contains);
            }

            var known = new HashSet<string>(list.Select(c => c.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<EditClip>();
            foreach (var f in files)
            {
                if (!known.Add(f.FullPath)) continue;
                var clip = new EditClip { FullPath = f.FullPath, SizeBytes = f.SizeBytes, Modified = f.Modified, Group = group };
                added.Add(clip);
                group.Items.Add(clip);
                list.Add(clip);
            }

            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            Renumber(list);
            return (added, removed);
        }

        public static void Renumber(IList<EditClip> list)
        {
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        }

        /// <summary>Moves the chosen clips one place up (-1) or down (+1) as a block. Returns true if anything moved.</summary>
        public static bool Move(IList<EditClip> list, IReadOnlyCollection<EditClip> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<EditClip>(chosen);
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

        /// <summary>Name order ("Clip 2" before "Clip 10") or date order, oldest first.</summary>
        public static List<EditClip> Sorted(IEnumerable<EditClip> clips, bool byDate) => byDate
            ? clips.OrderBy(c => c.Modified).ThenBy(c => c.Name, NaturalStringComparer.Instance).ToList()
            : clips.OrderBy(c => c.Folder, NaturalStringComparer.Instance).ThenBy(c => c.Name, NaturalStringComparer.Instance).ToList();

        /// <summary>"12 clips · 14:05" (length of the ticked clips, trims included).</summary>
        public static string DescribeList(IReadOnlyCollection<EditClip> clips)
        {
            if (clips.Count == 0) return "No clips";
            var ticked = clips.Where(c => c.IsIncluded).ToList();
            string text = $"{clips.Count:N0} {EditText.Plural(clips.Count, "clip", "clips")}";
            if (ticked.Count != clips.Count) text += $" · {ticked.Count:N0} ticked";
            if (ticked.Count > 0 && ticked.All(c => c.UsedLength != null))
                text += " · " + EditText.FormatTime(TimeSpan.FromSeconds(ticked.Sum(c => c.UsedLength!.Value.TotalSeconds)));
            return text;
        }

        public static string DescribeResult(EditResult r)
        {
            if (r.Cancelled) return "Cancelled - nothing was saved.";
            if (r.Files.Count == 0) return r.Error ?? "No video was made.";
            var parts = new List<string>
            {
                r.Files.Count == 1
                    ? $"Saved {Path.GetFileName(r.Files[0])} · {EditText.FormatTime(r.Length)}"
                    : $"Saved {r.Files.Count} files ({Path.GetFileName(r.Files[0])} …) · {EditText.FormatTime(r.Length)}",
                r.JoinedWithoutReencoding ? "joined without re-encoding" : $"{r.Elapsed.TotalSeconds:0} s",
            };
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} {EditText.Plural(r.Failed, "clip", "clips")} left out - hover the status for why");
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "video_editor.json");

        public static EditorSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new EditorSettings();
                var s = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(file), JsonOptions) ?? new EditorSettings();
                if (!FadeChoices.Contains(s.FadeSeconds)) s.FadeSeconds = 0.5;
                if (!SplitChoices.Contains(s.SplitMinutes)) s.SplitMinutes = 15;
                if (!Enum.IsDefined(s.Format)) s.Format = EditOutputFormat.Mp4;
                if (!Enum.IsDefined(s.Size)) s.Size = EditOutputSize.FirstClip;
                s.Volume = double.IsFinite(s.Volume) ? Math.Clamp(s.Volume, 0, 1) : 1;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                return new EditorSettings();
            }
        }

        public static void SaveSettings(EditorSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never fail the tool over them.
            }
        }
    }
}
