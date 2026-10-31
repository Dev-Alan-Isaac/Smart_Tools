using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Validate Files - video and audio checks with ffprobe/ffmpeg
    /// (Engines\FFMPEG, or PATH). The old version had no decoder and
    /// marked every video and audio file "Warning: checks skipped".
    /// </summary>
    public sealed partial class ValidateManager
    {
        /// <summary>Set by tests to use a specific ffmpeg/ffprobe (or a missing path to simulate none).</summary>
        public static string? FfmpegPathOverride { get; set; }
        public static string? FfprobePathOverride { get; set; }

        public static string FfmpegFolder => Smart_Tools.WPF.Controllers.FfmpegLocator.Folder;
        public static string? FfmpegExe => ResolveTool("ffmpeg", FfmpegPathOverride);
        public static string? FfprobeExe => ResolveTool("ffprobe", FfprobePathOverride);
        public static bool HasFfmpeg => FfmpegExe != null && FfprobeExe != null;

        private static string? ResolveTool(string name, string? overridePath)
        {
            if (overridePath != null) return File.Exists(overridePath) ? overridePath : null;

            string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
            string bundled = Path.Combine(FfmpegFolder, exe);
            if (File.Exists(bundled)) return bundled;

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // bad PATH entry
                }
            }
            return null;
        }

        /// <summary>At most this many ffmpeg decodes at once (each already uses several cores).</summary>
        private static readonly SemaphoreSlim DecodeGate = new(Math.Max(1, Environment.ProcessorCount / 4));

        // =================================================================
        //  ffprobe
        // =================================================================

        public sealed record MediaStream(string Type, string Codec, double Duration);

        public sealed record MediaProbe(bool Ok, string? Error, double Duration, IReadOnlyList<MediaStream> Streams)
        {
            public MediaStream? Video => Streams.FirstOrDefault(s => s.Type == "video" && s.Codec is not ("mjpeg" or "png" or "bmp"));   // not cover art
            public MediaStream? Audio => Streams.FirstOrDefault(s => s.Type == "audio");
        }

        public static MediaProbe Probe(string path, CancellationToken token)
        {
            string exe = FfprobeExe ?? throw new InvalidOperationException("ffprobe wasn't found.");
            var run = RunTool(exe, new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,duration", "-of", "json", path }, TimeSpan.FromSeconds(60), token);
            if (run.TimedOut) return new MediaProbe(false, "reading the file took too long", 0, Array.Empty<MediaStream>());
            var parsed = ParseProbeJson(run.Output);
            if (run.ExitCode != 0 || parsed == null)
                return new MediaProbe(false, FriendlyMediaError(run.Errors), 0, Array.Empty<MediaStream>());
            return parsed with { Error = run.Errors.Count > 0 ? FriendlyMediaError(run.Errors) : null };
        }

        /// <summary>ffprobe's JSON -> streams and length (null if it isn't valid JSON).</summary>
        public static MediaProbe? ParseProbeJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                double Dur(JsonElement e) =>
                    e.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;

                var streams = new List<MediaStream>();
                if (root.TryGetProperty("streams", out var list))
                {
                    foreach (var s in list.EnumerateArray())
                    {
                        streams.Add(new MediaStream(
                            s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "",
                            s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "",
                            Dur(s)));
                    }
                }
                double duration = root.TryGetProperty("format", out var format) ? Dur(format) : 0;
                return new MediaProbe(true, null, duration, streams);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Plain-language reason from ffmpeg/ffprobe error lines.</summary>
        public static string FriendlyMediaError(IReadOnlyList<string> errors)
        {
            string all = string.Join("\n", errors);
            if (all.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase))
                return "the video's index (moov) is missing - the recording was probably cut off before it finished";
            if (all.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase) && errors.Count <= 2)
                return "it can't be read as media - it's damaged or not really this kind of file";
            if (all.Contains("partial file", StringComparison.OrdinalIgnoreCase) || all.Contains("Truncat", StringComparison.OrdinalIgnoreCase) || all.Contains("End of file", StringComparison.OrdinalIgnoreCase))
                return "the file is cut short";
            string? first = errors.Select(CleanLogLine).FirstOrDefault(l => l.Length > 0);
            return first ?? "ffmpeg couldn't read it";
        }

        /// <summary>"[h264 @ 0x55..] [error] cabac decode failed" -> "cabac decode failed".</summary>
        public static string CleanLogLine(string line)
        {
            string s = Regex.Replace(line, @"^\s*(\[[^\]]*@\s*0x[0-9a-fA-F]+\]\s*)?", "");
            s = Regex.Replace(s, @"^\[(error|fatal|panic|warning|info)\]\s*", "");
            return s.Trim();
        }

        // =================================================================
        //  Decoding
        // =================================================================

        public readonly record struct DecodeResult(int ErrorCount, string? FirstError, bool Failed, string? FailReason, bool TimedOut, double? MaxVolumeDb);

        /// <summary>
        /// Time ranges to decode: Quick = start and end, Standard = six
        /// samples spread through, Full = everything (null = whole file).
        /// </summary>
        public static List<(double Start, double Length)>? DecodePlan(VideoScanDepth depth, double duration)
        {
            if (depth == VideoScanDepth.Full || duration <= 0) return null;
            if (depth == VideoScanDepth.Quick)
                return duration <= 8 ? null : new() { (0, 3), (duration - 3, 3) };
            if (duration <= 16) return null;
            var plan = new List<(double, double)>();
            for (int i = 0; i < 6; i++) plan.Add((Math.Max(0, (duration - 2) * i / 5), 2));
            return plan;
        }

        /// <summary>Decodes (without writing anything) and counts decoder errors.</summary>
        public static DecodeResult Decode(string path, IReadOnlyList<string> maps, List<(double Start, double Length)>? plan, double duration, bool measureVolume, CancellationToken token)
        {
            string exe = FfmpegExe ?? throw new InvalidOperationException("ffmpeg wasn't found.");
            int errorCount = 0;
            string? firstError = null;
            double? maxVolume = null;
            var ranges = plan ?? new List<(double, double)> { (-1, -1) };

            DecodeGate.Wait(token);
            try
            {
                foreach (var (start, length) in ranges)
                {
                    token.ThrowIfCancellationRequested();
                    var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", measureVolume ? "level+info" : "level+error" };
                    if (start > 0) args.AddRange(new[] { "-ss", start.ToString("0.###", CultureInfo.InvariantCulture) });
                    args.AddRange(new[] { "-i", path });
                    if (length > 0) args.AddRange(new[] { "-t", length.ToString("0.###", CultureInfo.InvariantCulture) });
                    foreach (string m in maps) args.AddRange(new[] { "-map", m });
                    if (measureVolume) args.AddRange(new[] { "-af", "volumedetect" });
                    args.AddRange(new[] { "-f", "null", "-" });

                    double seconds = length > 0 ? length : Math.Max(duration, 60);
                    var run = RunTool(exe, args, TimeSpan.FromSeconds(120 + seconds * 3), token);
                    if (run.TimedOut) return new DecodeResult(errorCount, firstError, false, null, true, maxVolume);

                    var errors = run.Errors.Where(l => l.Contains("[error]", StringComparison.Ordinal) || l.Contains("[fatal]", StringComparison.Ordinal)).ToList();
                    errorCount += errors.Count;
                    firstError ??= errors.Select(CleanLogLine).FirstOrDefault(l => l.Length > 0);
                    if (measureVolume)
                    {
                        var m = run.Errors.Select(l => Regex.Match(l, @"max_volume:\s*(-?[\d.]+|-inf)\s*dB")).LastOrDefault(x => x.Success);
                        if (m != null)
                            maxVolume = m.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    if (run.ExitCode != 0)
                        return new DecodeResult(errorCount, firstError, true, FriendlyMediaError(errors.Count > 0 ? errors : run.Errors), false, maxVolume);
                }
            }
            finally
            {
                DecodeGate.Release();
            }
            return new DecodeResult(errorCount, firstError, false, null, false, maxVolume);
        }

        private sealed record ToolRun(int ExitCode, string Output, List<string> Errors, bool TimedOut);

        private static ToolRun RunTool(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken token)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            var errors = new List<string>();
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (errors)
                {
                    if (errors.Count < 2000) errors.Add(e.Data);   // a badly damaged file can print millions of lines
                }
            };
            p.Start();
            p.BeginErrorReadLine();
            var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);

            var watch = Stopwatch.StartNew();
            while (!p.WaitForExit(200))
            {
                if (token.IsCancellationRequested || watch.Elapsed > timeout)
                {
                    try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                    p.WaitForExit();
                    token.ThrowIfCancellationRequested();
                    return new ToolRun(-1, "", errors, true);
                }
            }
            p.WaitForExit();   // flushes the error events
            lock (errors) return new ToolRun(p.ExitCode, output.Result, errors.ToList(), false);
        }

        // =================================================================
        //  The checks
        // =================================================================

        private static string NoFfmpegNote => $"stream checks need ffmpeg in {FfmpegFolder} (not found)";

        private static string CheckVideo(string path, ValidationOptions o, Findings f, CancellationToken token)
        {
            bool deep = o.VideoCheckStream || o.VideoCheckAudioStream || o.VideoCheckDuration || o.VideoCheckFrames;
            if (!deep) return "Container header OK";
            if (!HasFfmpeg)
            {
                f.Notes.Add(NoFfmpegNote);
                return "Container header OK";
            }

            var probe = Probe(path, token);
            if (!probe.Ok)
            {
                f.Problems.Add(probe.Error ?? "it can't be read as video");
                return "";
            }
            var video = probe.Video;
            var audio = probe.Audio;
            if (video == null)
            {
                f.Warnings.Add(audio != null ? "it has no video track - it's audio only" : "it has no video or audio tracks");
                return "";
            }

            if (o.VideoCheckDuration)
            {
                if (probe.Duration <= 0) f.Warnings.Add("its length is unknown - the index may be damaged, so seeking may not work");
                else if (video.Duration > 0 && Math.Abs(video.Duration - probe.Duration) > Math.Max(2, probe.Duration * 0.05))
                    f.Warnings.Add($"the video track is {Clock(video.Duration)} long but the file says {Clock(probe.Duration)} - part may be missing");
            }

            // Stream check = a quick decode; frame check = decode at the chosen depth.
            if (o.VideoCheckStream || o.VideoCheckFrames || (o.VideoCheckAudioStream && audio != null))
            {
                var depth = o.VideoCheckFrames ? o.VideoScanDepth : VideoScanDepth.Quick;
                var maps = new List<string>();
                if (o.VideoCheckStream || o.VideoCheckFrames) maps.Add("0:v:0");
                if (o.VideoCheckAudioStream && audio != null) maps.Add("0:a:0");
                var d = Decode(path, maps, DecodePlan(depth, probe.Duration), probe.Duration, false, token);
                ReportDecode(d, f, maps.Count > 1 ? "video or audio" : "video");
                string how = depth switch
                {
                    VideoScanDepth.Full => "every frame decoded",
                    VideoScanDepth.Standard => "samples decoded through the file",
                    _ => "start and end decoded",
                };
                if (audio == null && o.VideoCheckAudioStream) f.Notes.Add("no audio track");
                return $"{video.Codec.ToUpperInvariant()}{(audio != null ? " + " + audio.Codec.ToUpperInvariant() : "")}, {Clock(probe.Duration)}, {how}";
            }
            return $"{video.Codec.ToUpperInvariant()}{(audio != null ? " + " + audio.Codec.ToUpperInvariant() : "")}, {Clock(probe.Duration)}";
        }

        private static string CheckAudioStreams(string path, ValidationOptions o, Findings f, CancellationToken token)
        {
            bool deep = o.AudioFullDecode || o.AudioCheckDuration || o.AudioFlagSilent || o.AudioCheckTruncated;
            if (!deep) return "Header OK";
            if (!HasFfmpeg)
            {
                f.Notes.Add(NoFfmpegNote);
                return "Header OK";
            }

            var probe = Probe(path, token);
            if (!probe.Ok)
            {
                f.Problems.Add(probe.Error ?? "it can't be read as audio");
                return "";
            }
            var audio = probe.Audio;
            if (audio == null)
            {
                f.Problems.Add("it has no audio track");
                return "";
            }
            if (o.AudioCheckDuration && probe.Duration <= 0)
                f.Warnings.Add("its length is unknown - players may not be able to seek");

            if (o.AudioFullDecode || o.AudioFlagSilent || o.AudioCheckTruncated)
            {
                var d = Decode(path, new[] { "0:a:0" }, null, probe.Duration, o.AudioFlagSilent, token);
                ReportDecode(d, f, "audio");
                if (o.AudioFlagSilent && d.MaxVolumeDb is double db && db <= -80)
                    f.Warnings.Add("it's silent (no sound above -80 dB)");
                return $"{audio.Codec.ToUpperInvariant()}, {Clock(probe.Duration)}, fully decoded";
            }
            return $"{audio.Codec.ToUpperInvariant()}, {Clock(probe.Duration)}";
        }

        private static void ReportDecode(DecodeResult d, Findings f, string what)
        {
            if (d.TimedOut)
                f.CantCheck.Add($"decoding the {what} took too long and was stopped");
            else if (d.Failed)
                f.Problems.Add(d.FailReason ?? $"the {what} can't be decoded");
            else if (d.ErrorCount > 0)
                f.Problems.Add($"{d.ErrorCount:N0} decoding error{(d.ErrorCount == 1 ? "" : "s")} - damaged {what} data (first: {d.FirstError})");
        }

        private static string Clock(double seconds)
        {
            if (seconds <= 0) return "length unknown";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
