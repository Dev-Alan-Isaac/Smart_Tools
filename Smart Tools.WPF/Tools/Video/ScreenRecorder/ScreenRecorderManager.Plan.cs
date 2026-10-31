using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The decisions behind a recording: the FFmpeg command, sizes, and reading the microphone list. Pure: no processes, no WPF.</summary>
    public sealed partial class ScreenRecorderManager
    {
        public static readonly int[] FpsChoices = { 15, 30, 60 };

        private static readonly Regex AudioDevice = new("\"(?<name>[^\"]+)\"\\s+\\(audio\\)", RegexOptions.Compiled);
        private static readonly Regex QuotedName = new("\"(?<name>[^\"]+)\"", RegexOptions.Compiled);

        public static int CrfFor(RecordQuality quality) => quality switch
        {
            RecordQuality.Small => 28,
            RecordQuality.High => 18,
            _ => 23,
        };

        /// <summary>H.264 in the usual 4:2:0 format needs even sides; an odd one is rounded down.</summary>
        public static int EvenSize(int value) => Math.Max(2, value & ~1);

        /// <summary>The area to record with even sides, or null when it is too small to be worth a video.</summary>
        public static RecordRegion? Normalize(RecordRegion? region)
        {
            if (region == null) return null;
            int w = EvenSize(region.Width), h = EvenSize(region.Height);
            return region.Width < 16 || region.Height < 16 ? null : new RecordRegion(region.X, region.Y, w, h);
        }

        /// <summary>
        /// The FFmpeg arguments, one item each (no quoting needed). Video is read with gdigrab; a microphone, if chosen, with
        /// DirectShow. The file is a fragmented MP4, so even if the app or PC dies mid-recording everything up to then plays.
        /// </summary>
        public static List<string> BuildArguments(RecordOptions o)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats" };

            args.AddRange(new[] { "-thread_queue_size", "512", "-f", "gdigrab", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-draw_mouse", o.ShowCursor ? "1" : "0" });
            string input = "desktop";
            if (o.Target == RecordTarget.Window)
            {
                // The window handle is exact; a title can match the wrong window or change while recording.
                input = o.WindowHandle != 0 ? $"hwnd=0x{o.WindowHandle:X}" : $"title={o.WindowTitle}";
            }
            else if (Normalize(o.Region) is { } r)
            {
                args.AddRange(new[] { "-offset_x", r.X.ToString(CultureInfo.InvariantCulture), "-offset_y", r.Y.ToString(CultureInfo.InvariantCulture), "-video_size", $"{r.Width}x{r.Height}" });
            }
            args.AddRange(new[] { "-i", input });

            bool sound = !string.IsNullOrWhiteSpace(o.Microphone);
            if (sound) args.AddRange(new[] { "-thread_queue_size", "512", "-f", "dshow", "-i", $"audio={o.Microphone}" });

            // A window can have any size, so the even-sides rule is applied to the picture itself as well.
            args.AddRange(new[] { "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2" });
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", CrfFor(o.Quality).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p" });
            if (sound) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-af", "aresample=async=1" });
            args.AddRange(new[] { "-movflags", "frag_keyframe+empty_moov+default_base_moof", o.OutputPath });
            return args;
        }

        // =================================================================
        //  Segments: a recording is several parts (a screen switch or a security prompt starts a new one) joined at the end
        // =================================================================

        /// <summary>The folder that holds the parts of one recording: "Recording 2026-06-01 14.30.05 (parts)" beside the final file.</summary>
        public static string PartsFolderFor(string finalPath) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(finalPath)) ?? "", Path.GetFileNameWithoutExtension(finalPath) + " (parts)");

        public static string PartPath(string partsFolder, int index) =>
            Path.Combine(partsFolder, $"part-{index.ToString("000", CultureInfo.InvariantCulture)}.mp4");

        /// <summary>The list FFmpeg's concat demuxer reads. Forward slashes, and a quote inside a name written as '\''.</summary>
        public static string BuildConcatListText(IEnumerable<string> parts) =>
            string.Concat(parts.Select(p => $"file '{p.Replace('\\', '/').Replace("'", "'\\''")}'\n"));

        /// <summary>Parts of different sizes can't be joined as they are: they have to be fitted to one picture size.</summary>
        public static bool NeedsReencode(IReadOnlyList<(int Width, int Height)> sizes) => sizes.Distinct().Count() > 1;

        /// <summary>The picture size every part is fitted into: the largest width and height, even numbers.</summary>
        public static (int Width, int Height) CanvasFor(IReadOnlyList<(int Width, int Height)> sizes) =>
            (EvenSize(sizes.Count == 0 ? 2 : sizes.Max(s => s.Width)), EvenSize(sizes.Count == 0 ? 2 : sizes.Max(s => s.Height)));

        /// <summary>
        /// The FFmpeg arguments that join the parts. Same sizes: the concat demuxer copies them (no re-encoding, so it is quick).
        /// Different sizes: each part is scaled to fit the canvas (black bars) and re-encoded with the recording's quality.
        /// </summary>
        public static List<string> BuildMergeArguments(string listFile, IReadOnlyList<string> parts, string output, IReadOnlyList<(int Width, int Height)> sizes,
                                                       RecordQuality quality, int fps, bool hasAudio)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-y" };
            if (!NeedsReencode(sizes))
            {
                args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", "-movflags", "+faststart", output });
                return args;
            }

            var (w, h) = CanvasFor(sizes);
            foreach (string part in parts) args.AddRange(new[] { "-i", part });

            var filter = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                filter.Append(CultureInfo.InvariantCulture,
                    $"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={fps}[v{i}];");
            }
            for (int i = 0; i < parts.Count; i++) filter.Append(hasAudio ? $"[v{i}][{i}:a]" : $"[v{i}]");
            filter.Append(CultureInfo.InvariantCulture, $"concat=n={parts.Count}:v=1:a={(hasAudio ? 1 : 0)}[v]{(hasAudio ? "[a]" : "")}");

            args.AddRange(new[] { "-filter_complex", filter.ToString(), "-map", "[v]" });
            if (hasAudio) args.AddRange(new[] { "-map", "[a]" });
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", CrfFor(quality).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p" });
            if (hasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
            args.AddRange(new[] { "-movflags", "+faststart", output });
            return args;
        }

        /// <summary>"1920,1080" (what ffprobe prints for width and height) to a size, or null.</summary>
        public static (int Width, int Height)? ParseSize(string probeOutput)
        {
            var bits = probeOutput.Trim().Split(',', StringSplitOptions.TrimEntries);
            return bits.Length >= 2 && int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                && int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && w > 0 && h > 0
                ? (w, h) : null;
        }

        /// <summary>
        /// The audio device names in the text <c>ffmpeg -list_devices true -f dshow -i dummy</c> prints. Newer builds mark each line
        /// with (audio) or (video); older ones list devices under a "DirectShow audio devices" heading. Device ids (@device_...) are skipped.
        /// </summary>
        public static List<string> ParseAudioDevices(string ffmpegOutput)
        {
            var names = new List<string>();
            var lines = ffmpegOutput.Replace("\r", "").Split('\n');

            foreach (string line in lines)
            {
                var m = AudioDevice.Match(line);
                if (m.Success) names.Add(m.Groups["name"].Value);
            }
            if (names.Count > 0) return names.Distinct().ToList();

            bool inAudio = false;
            foreach (string line in lines)
            {
                if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase)) { inAudio = true; continue; }
                if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase)) { inAudio = false; continue; }
                if (!inAudio || line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;
                var m = QuotedName.Match(line);
                if (m.Success && !m.Groups["name"].Value.StartsWith('@')) names.Add(m.Groups["name"].Value);
            }
            return names.Distinct().ToList();
        }

        /// <summary>The last meaningful lines of FFmpeg's error output, for a message a person can read.</summary>
        public static string PlainError(string ffmpegErrors)
        {
            var lines = ffmpegErrors.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return "";
            string text = string.Join(" ", lines.TakeLast(2));
            text = Regex.Replace(text, @"\[[a-z0-9_]+ @ [0-9A-Fa-f]+\]\s*", "");   // "[gdigrab @ 000001...]" means nothing to a user
            return text.Length > 300 ? text[..300] + "…" : text;
        }

        /// <summary>"00:07", "01:02:03".</summary>
        public static string FormatElapsed(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        /// <summary>"Recording 2026-06-01 14.30.05.mp4": no colons, which file names can't hold.</summary>
        public static string DefaultFileName(DateTime now) => $"Recording {now:yyyy-MM-dd HH.mm.ss}.mp4";
    }
}
