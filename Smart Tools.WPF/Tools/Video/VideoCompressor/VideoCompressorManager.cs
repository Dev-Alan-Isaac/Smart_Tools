using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Video Compressor &amp; Trimmer backend, split in two:
    /// <list type="bullet">
    /// <item>VideoCompressorManager.cs (this file) - pure logic: the FFmpeg arguments, target-size maths, estimates, settings.
    /// No processes, no WPF.</item>
    /// <item>VideoCompressorManager.Run.cs - ffprobe and the batch run.</item>
    /// </list>
    /// </summary>
    public static partial class VideoCompressorManager
    {
        public static readonly int[] HeightChoices = { 0, 1080, 720, 480 };
        public static readonly int[] FpsChoices = { 0, 60, 30, 24, 15 };
        public static readonly double[] SpeedChoices = { 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };

        /// <summary>Below this a picture is just blocks; a target size that needs less is flagged and made at this rate.</summary>
        public const int MinVideoKbps = 100;

        public const int AudioKbpsKeep = 128;
        public const int AudioKbpsSmaller = 64;

        /// <summary>The container takes a little on top of the streams.</summary>
        private const double ContainerOverhead = 0.96;

        public static int CrfFor(VcQuality quality) => quality switch
        {
            VcQuality.Small => 28,
            VcQuality.High => 18,
            _ => 23,
        };

        public static int AudioKbps(VcAudio audio) => audio switch
        {
            VcAudio.Remove => 0,
            VcAudio.Smaller => AudioKbpsSmaller,
            _ => AudioKbpsKeep,
        };

        // =================================================================
        //  Trim and length
        // =================================================================

        /// <summary>The part of the video that is kept, in seconds of the source.</summary>
        public static (double Start, double End) TrimRange(VcFileItem item)
        {
            double total = item.Duration ?? 0;
            double start = Math.Clamp(item.TrimStart ?? 0, 0, total);
            double end = item.TrimEnd is double e ? Math.Clamp(e, start, total) : total;
            return (start, end);
        }

        /// <summary>How long the result plays: the kept part, sped up or slowed down.</summary>
        public static double OutputSeconds(VcFileItem item, VcOptions o)
        {
            var (start, end) = TrimRange(item);
            double speed = o.Mode == VcMode.TrimOnly ? 1 : Math.Clamp(o.Speed, 0.25, 8);
            return (end - start) / speed;
        }

        /// <summary>The shortest part the trim card lets you keep, in seconds.</summary>
        public const double MinKeepSeconds = 0.1;

        /// <summary>"90", "1:30", "1:02:03.5" (a comma works as the decimal mark) to seconds. Null when it isn't a time.</summary>
        public static double? ParseTime(string? text)
        {
            text = (text ?? "").Trim().Replace(',', '.');
            if (text.Length == 0) return null;
            var parts = text.Split(':');
            if (parts.Length > 3) return null;
            double total = 0;
            foreach (string part in parts)
            {
                if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || value < 0) return null;
                total = total * 60 + value;
            }
            return total;
        }

        /// <summary>
        /// The kept part after typing a start and an end: both clamped into the video, and at least <see cref="MinKeepSeconds"/> long.
        /// The value that was typed wins (<paramref name="startEdited"/> says which one it was); the other handle moves just far enough.
        /// </summary>
        public static (double Start, double End) ClampKeep(double start, double end, double duration, bool startEdited)
        {
            duration = Math.Max(duration, MinKeepSeconds);
            start = Math.Clamp(start, 0, duration);
            end = Math.Clamp(end, 0, duration);
            if (end - start >= MinKeepSeconds) return (start, end);

            if (!startEdited) return (Math.Max(0, end - MinKeepSeconds), end);
            if (start + MinKeepSeconds <= duration) return (start, start + MinKeepSeconds);
            return (Math.Max(0, duration - MinKeepSeconds), duration);   // typed a start at the very end: back it off
        }

        // =================================================================
        //  Target size
        // =================================================================

        /// <summary>
        /// The video bitrate (kbps) that makes a file of <paramref name="targetMb"/> over <paramref name="seconds"/> with
        /// <paramref name="audioKbps"/> of sound. <paramref name="reachable"/> is false when that leaves less than
        /// <see cref="MinVideoKbps"/>; the result is then the minimum.
        /// </summary>
        public static int TargetVideoKbps(double targetMb, double seconds, int audioKbps, out bool reachable)
        {
            reachable = true;
            if (seconds <= 0) return MinVideoKbps;
            double totalKbps = targetMb * 1024 * 1024 * 8 * ContainerOverhead / seconds / 1000.0;
            double video = totalKbps - audioKbps;
            if (video < MinVideoKbps)
            {
                reachable = false;
                return MinVideoKbps;
            }
            return (int)Math.Floor(video);
        }

        // =================================================================
        //  FFmpeg arguments
        // =================================================================

        /// <summary>The audio speed filter. atempo only takes 0.5 to 2 in older FFmpeg builds, so faster speeds are chained.</summary>
        public static string AtempoChain(double speed)
        {
            speed = Math.Clamp(speed, 0.25, 8);
            var parts = new List<string>();
            while (speed > 2.0 + 1e-9) { parts.Add("atempo=2"); speed /= 2.0; }
            while (speed < 0.5 - 1e-9) { parts.Add("atempo=0.5"); speed /= 0.5; }
            parts.Add("atempo=" + speed.ToString("0.####", CultureInfo.InvariantCulture));
            return string.Join(',', parts);
        }

        /// <summary>The picture filters: height cap, frame-rate cap, speed. Null when none is needed.</summary>
        public static string? BuildVideoFilter(VcFileItem item, VcOptions o)
        {
            var filters = new List<string>();
            if (o.MaxHeight > 0 && (item.Height == 0 || item.Height > o.MaxHeight))
                filters.Add($"scale=-2:'min({o.MaxHeight.ToString(CultureInfo.InvariantCulture)},ih)'");
            if (o.MaxFps > 0 && item.Fps > o.MaxFps + 0.5)
                filters.Add($"fps={o.MaxFps.ToString(CultureInfo.InvariantCulture)}");
            if (Math.Abs(o.Speed - 1) > 0.001)
                filters.Add("setpts=PTS/" + o.Speed.ToString("0.####", CultureInfo.InvariantCulture));
            return filters.Count == 0 ? null : string.Join(',', filters);
        }

        /// <summary>
        /// Encoder arguments for the video in <see cref="VcMode.Quality"/> mode. Graphics-card encoders use the nearest
        /// setting to the CRF; the processor (libx264) is the reference.
        /// </summary>
        public static List<string> QualityEncoderArgs(string encoder, int crf) => encoder switch
        {
            "h264_nvenc" => new() { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-pix_fmt", "yuv420p" },
            "h264_qsv" => new() { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "nv12" },
            "h264_amf" => new() { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", crf.ToString(CultureInfo.InvariantCulture), "-qp_p", (crf + 2).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p" },
            _ => new() { "-c:v", "libx264", "-preset", "fast", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p" },
        };

        /// <summary>
        /// The FFmpeg arguments for one output. <paramref name="pass"/> is 0 for a single pass, or 1 / 2 for target-size encoding
        /// (pass 1 writes only the statistics, to <paramref name="passLogPrefix"/>). <paramref name="encoder"/> only matters in Quality mode.
        /// </summary>
        public static List<string> BuildArguments(VcFileItem item, VcOptions o, string input, string output, int pass, string passLogPrefix, string encoder = "libx264")
        {
            var (start, end) = TrimRange(item);
            double keep = end - start;
            var args = new List<string> { "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };

            if (item.IsTrimmed || start > 0.001)
            {
                args.AddRange(new[] { "-ss", start.ToString("0.###", CultureInfo.InvariantCulture) });
                if (item.TrimEnd != null || keep < (item.Duration ?? 0) - 0.001)
                    args.AddRange(new[] { "-t", keep.ToString("0.###", CultureInfo.InvariantCulture) });
            }
            args.AddRange(new[] { "-i", input });

            if (o.Mode == VcMode.TrimOnly)
            {
                args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-f", "mp4", output });
                return args;
            }

            args.AddRange(new[] { "-map", "0:v:0" });
            bool audio = o.Audio != VcAudio.Remove && item.HasAudio;
            if (audio && pass != 1) args.AddRange(new[] { "-map", "0:a:0" });

            string? vf = BuildVideoFilter(item, o);
            if (vf != null) args.AddRange(new[] { "-vf", vf });

            if (o.Mode == VcMode.TargetSize)
            {
                int audioKbps = audio ? AudioKbps(o.Audio) : 0;
                int kbps = TargetVideoKbps(o.TargetMb, OutputSeconds(item, o), audioKbps, out _);
                string rate = kbps.ToString(CultureInfo.InvariantCulture) + "k";
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", rate, "-maxrate", (kbps * 2).ToString(CultureInfo.InvariantCulture) + "k", "-bufsize", (kbps * 2).ToString(CultureInfo.InvariantCulture) + "k",
                    "-pix_fmt", "yuv420p", "-pass", pass == 1 ? "1" : "2", "-passlogfile", passLogPrefix });
            }
            else
            {
                args.AddRange(QualityEncoderArgs(encoder, CrfFor(o.Quality)));
            }

            if (pass == 1)
            {
                args.AddRange(new[] { "-an", "-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null" });
                return args;
            }

            if (audio)
            {
                args.AddRange(o.Audio == VcAudio.Smaller
                    ? new[] { "-c:a", "aac", "-b:a", "64k", "-ac", "1" }
                    : new[] { "-c:a", "aac", "-b:a", "128k" });
                if (Math.Abs(o.Speed - 1) > 0.001) args.AddRange(new[] { "-af", AtempoChain(o.Speed) });
            }
            else
            {
                args.Add("-an");
            }
            args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", output });
            return args;
        }

        // =================================================================
        //  Reading ffprobe
        // =================================================================

        /// <summary>"30000/1001" to 29.97. Zero when it isn't a rate.</summary>
        public static double ParseFrameRate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var bits = text.Split('/');
            if (!double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return 0;
            if (bits.Length == 1) return num;
            return double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0 ? num / den : 0;
        }

        /// <summary>Turns ffprobe's JSON into a <see cref="VcProbe"/>. Null when it isn't JSON with a format.</summary>
        public static VcProbe? ParseProbe(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("format", out var format)) return null;

                double? duration = null;
                if (format.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String
                    && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
                    duration = seconds;

                int width = 0, height = 0;
                double fps = 0;
                bool hasVideo = false, hasAudio = false;
                if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        string type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                        if (type == "audio") hasAudio = true;
                        if (type != "video" || hasVideo) continue;
                        hasVideo = true;
                        if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out int wi)) width = wi;
                        if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out int hi)) height = hi;
                        fps = ParseFrameRate(stream.TryGetProperty("avg_frame_rate", out var avg) ? avg.GetString() : null);
                        if (fps <= 0) fps = ParseFrameRate(stream.TryGetProperty("r_frame_rate", out var r) ? r.GetString() : null);
                    }
                }

                if (!hasVideo) return new VcProbe(duration, 0, 0, 0, hasAudio, "This file has no picture, so it can't be compressed.");
                return new VcProbe(duration, width, height, fps, hasAudio, null);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // =================================================================
        //  Names and estimates
        // =================================================================

        /// <summary>"Holiday - compressed.mp4", or "Holiday - trimmed.mp4" when nothing is re-encoded.</summary>
        public static string OutputFileName(VcFileItem item, VcOptions o)
        {
            string stem = Path.GetFileNameWithoutExtension(item.FullPath);
            return $"{stem} - {(o.Mode == VcMode.TrimOnly ? "trimmed" : "compressed")}.mp4";
        }

        /// <summary>A rough size of the result in bytes, or null when the length isn't known yet. It is an estimate only.</summary>
        public static long? EstimateBytes(VcFileItem item, VcOptions o)
        {
            if (item.Duration is not double total || total <= 0 || item.SizeBytes <= 0) return null;
            var (start, end) = TrimRange(item);
            double keepFraction = Math.Clamp((end - start) / total, 0, 1);
            double inRange = item.SizeBytes * keepFraction;

            switch (o.Mode)
            {
                case VcMode.TrimOnly:
                    return (long)inRange;

                case VcMode.TargetSize:
                {
                    double outSeconds = OutputSeconds(item, o);
                    int audioKbps = item.HasAudio ? AudioKbps(o.Audio) : 0;
                    int kbps = TargetVideoKbps(o.TargetMb, outSeconds, audioKbps, out bool reachable);
                    double bytes = reachable ? o.TargetMb * 1024.0 * 1024 : (kbps + audioKbps) * 1000.0 / 8 * outSeconds / ContainerOverhead;
                    return (long)Math.Min(bytes, inRange);
                }

                default:
                {
                    double factor = o.Quality switch { VcQuality.Small => 0.30, VcQuality.High => 0.75, _ => 0.50 };
                    double heightScale = o.MaxHeight > 0 && item.Height > o.MaxHeight ? Math.Pow((double)o.MaxHeight / item.Height, 2) : 1;
                    double fpsScale = o.MaxFps > 0 && item.Fps > o.MaxFps + 0.5 ? o.MaxFps / item.Fps : 1;
                    double bytes = inRange * factor * heightScale * Math.Sqrt(fpsScale);
                    // Speeding up cuts the length, so the sound and picture have less to hold.
                    bytes /= Math.Clamp(o.Speed, 0.25, 8);
                    if (o.Audio == VcAudio.Remove && item.HasAudio) bytes -= AudioKbpsKeep * 1000.0 / 8 * OutputSeconds(item, o);
                    return (long)Math.Max(bytes, 0);
                }
            }
        }

        public static string EstimateText(VcFileItem item, VcOptions o) =>
            EstimateBytes(item, o) is long bytes ? "about " + VcText.FormatBytes(bytes) : "";

        public static string DescribeList(IReadOnlyCollection<VcFileItem> rows)
        {
            string text = VcText.Plural(rows.Count, "video", "videos");
            long size = rows.Sum(r => r.SizeBytes);
            double seconds = rows.Sum(r => r.Duration ?? 0);
            if (size > 0) text += " · " + VcText.FormatBytes(size);
            if (seconds > 0) text += " · " + VcText.FormatTime(seconds);
            return text;
        }

        /// <summary>"about 84 MB (62% smaller)" for the videos that haven't been done yet, or null when nothing can be said.</summary>
        public static string? DescribeEstimate(IEnumerable<VcFileItem> rows, VcOptions o)
        {
            long source = 0, result = 0;
            foreach (var row in rows.Where(r => r.Status != VcItemStatus.Done && r.Status != VcItemStatus.Unreadable))
            {
                if (EstimateBytes(row, o) is not long estimate) continue;
                source += row.SizeBytes;
                result += estimate;
            }
            if (source <= 0) return null;
            int saved = (int)Math.Round((1 - (double)result / source) * 100);
            return $"about {VcText.FormatBytes(result)}" + (saved > 0 ? $" ({saved}% smaller)" : "");
        }

        public static string DescribeResult(VcRunResult r)
        {
            var parts = new List<string>();
            if (r.Done > 0)
            {
                parts.Add(VcText.Plural(r.Done, "video done", "videos done"));
                if (r.SourceBytes > r.ResultBytes) parts.Add($"saved {VcText.FormatBytes(r.SourceBytes - r.ResultBytes)}");
            }
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed");
            if (r.Cancelled > 0) parts.Add($"{r.Cancelled:N0} canceled");
            if (parts.Count == 0) parts.Add("Nothing to do");
            return string.Join(" · ", parts) + " · " + VcText.FormatTime(r.Elapsed.TotalSeconds);
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\video_compressor.json)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "video_compressor.json");

        public static VcSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new VcSettings();
                return Sanitize(JsonSerializer.Deserialize<VcSettings>(File.ReadAllText(file), SettingsJson) ?? new VcSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new VcSettings();
            }
        }

        public static VcSettings Sanitize(VcSettings s)
        {
            if (!Enum.IsDefined(s.Mode)) s.Mode = VcMode.Quality;
            if (!Enum.IsDefined(s.Quality)) s.Quality = VcQuality.Balanced;
            if (!Enum.IsDefined(s.Audio)) s.Audio = VcAudio.Keep;
            s.TargetMb = Math.Clamp(s.TargetMb, 1, 100_000);
            if (!HeightChoices.Contains(s.MaxHeight)) s.MaxHeight = 0;
            if (!FpsChoices.Contains(s.MaxFps)) s.MaxFps = 0;
            if (!SpeedChoices.Any(v => Math.Abs(v - s.Speed) < 0.001)) s.Speed = 1;
            return s;
        }

        /// <summary>Never throws - a settings file that can't be written isn't worth an error.</summary>
        public static void SaveSettings(VcSettings s, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(Sanitize(s), SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
