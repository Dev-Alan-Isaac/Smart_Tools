using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Audio Trimmer &amp; Joiner - the engine half: ffprobe, the waveform, a playable copy for formats
    /// Windows can't play, and the export (ffmpeg with live progress, written under a temporary name and
    /// moved into place at the end, so a cancel or a failure leaves nothing behind).
    /// </summary>
    public static partial class AudioTrimmerManager
    {
        /// <summary>ffmpeg that prints nothing for this long is stopped.</summary>
        private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(3);

        /// <summary>Waveform data is read at this rate (mono); <see cref="AudioTrimmerPeaks.PerSecond"/> slices come out of it.</summary>
        private const int PeakSampleRate = 8_000;

        public static string FfmpegFolder => ConverterManager.FfmpegFolder;

        /// <summary>Null when ffmpeg and ffprobe are both there, else what's missing, in a sentence.</summary>
        public static string? MissingEngine()
        {
            bool ffmpeg = ConverterManager.FfmpegExe != null, ffprobe = ConverterManager.FfprobeExe != null;
            if (ffmpeg && ffprobe) return null;
            string which = !ffmpeg && !ffprobe ? "ffmpeg.exe and ffprobe.exe" : !ffmpeg ? "ffmpeg.exe" : "ffprobe.exe";
            return $"{which} wasn't found in {FfmpegFolder}. This tool needs it to read and write audio.";
        }

        // =================================================================
        //  ffprobe
        // =================================================================

        /// <summary>Length, sample rate and channels of the first sound track. Never throws except on cancel: problems go in <see cref="AudioTrimProbe.Error"/>.</summary>
        public static async Task<AudioTrimProbe> ProbeAsync(string path, CancellationToken token)
        {
            string? exe = ConverterManager.FfprobeExe;
            if (exe == null) return Failed($"ffprobe wasn't found in {FfmpegFolder}.");

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[]
            {
                "-v", "error", "-select_streams", "a:0",
                "-show_entries", "format=duration:stream=codec_type,codec_name,channels,sample_rate,duration",
                "-of", "json", path,
            })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (Exception ex) { return Failed("ffprobe couldn't start: " + ex.Message); }

            var outTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Smart_Tools.WPF.Controllers.ProbeTimeout.For(path));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                token.ThrowIfCancellationRequested();
                return Failed("Reading the file took too long - it may be damaged.");
            }

            string json = await outTask.ConfigureAwait(false);
            string err = (await errTask.ConfigureAwait(false)).Trim();
            var parsed = ParseProbe(json);
            if (parsed != null) return parsed;
            return Failed(err.Length > 0 ? PlainError(err) : "This file has no sound I can read.");
        }

        private static AudioTrimProbe Failed(string error) => new(0, 0, 0, "", error);

        /// <summary>Turns ffprobe's JSON into a probe. Null when there is no sound track.</summary>
        public static AudioTrimProbe? ParseProbe(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
                    return null;
                var s = streams[0];
                if (Str(s, "codec_type") is { } type && type != "audio") return null;

                double duration = Num(s, "duration") ?? 0;
                if (duration <= 0 && root.TryGetProperty("format", out var fmt)) duration = Num(fmt, "duration") ?? 0;
                return new AudioTrimProbe(
                    Math.Max(0, duration),
                    (int)(Num(s, "sample_rate") ?? 0),
                    (int)(Num(s, "channels") ?? 0),
                    Str(s, "codec_name") ?? "",
                    "");
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>ffprobe writes some numbers as strings and some as numbers.</summary>
        private static double? Num(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        // =================================================================
        //  Waveform
        // =================================================================

        /// <summary>
        /// Decodes the file (any format ffmpeg knows) to 8 kHz mono and keeps the loudest sample of every
        /// 1/250 s, scaled 0-255. Returns the slices and the length ffmpeg really delivered; null when it
        /// couldn't be read. Reports 0-1.
        /// </summary>
        public static async Task<(byte[] Peaks, double Seconds)?> ReadPeaksAsync(string path, double durationHint, IProgress<double>? progress, CancellationToken token)
        {
            string? exe = ConverterManager.FfmpegExe;
            if (exe == null) return null;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", PeakSampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-" })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            process.Start();
            var errTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            int perSlice = PeakSampleRate / AudioTrimmerPeaks.PerSecond;      // 32 samples
            var peaks = new List<byte>(durationHint > 0 ? (int)(durationHint * AudioTrimmerPeaks.PerSecond) + 16 : 4096);
            var buffer = new byte[1 << 16];
            int carry = 0, inSlice = 0, sliceMax = 0;
            long samples = 0;
            long expected = durationHint > 0 ? (long)(durationHint * PeakSampleRate) : 0;
            long lastReport = 0;

            try
            {
                var stream = process.StandardOutput.BaseStream;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), token).ConfigureAwait(false);
                    if (read == 0) break;
                    int bytes = carry + read;
                    int whole = bytes & ~1;                                    // whole 16-bit samples only
                    for (int i = 0; i < whole; i += 2)
                    {
                        int v = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8)));
                        if (v > sliceMax) sliceMax = v;
                        samples++;
                        if (++inSlice == perSlice)
                        {
                            peaks.Add((byte)Math.Min(255, sliceMax * 255 / 32767));
                            inSlice = 0;
                            sliceMax = 0;
                        }
                    }
                    carry = bytes - whole;
                    if (carry > 0) buffer[0] = buffer[whole];

                    if (expected > 0 && samples - lastReport > PeakSampleRate)   // about once a second of audio
                    {
                        lastReport = samples;
                        progress?.Report(Math.Min(0.99, samples / (double)expected));
                    }
                }
                if (inSlice > 0) peaks.Add((byte)Math.Min(255, sliceMax * 255 / 32767));
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                throw;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                }
                try { await errTask.ConfigureAwait(false); } catch (Exception) { /* nothing to report */ }
            }

            if (peaks.Count == 0) return null;
            progress?.Report(1);
            return (peaks.ToArray(), samples / (double)PeakSampleRate);
        }

        // =================================================================
        //  A playable copy
        // =================================================================

        /// <summary>Formats Windows' own decoder plays; anything else gets a converted copy for the preview.</summary>
        public static bool NeedsPlaybackCopy(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is not (".mp3" or ".wav" or ".wma" or ".m4a" or ".aac" or ".aif" or ".aiff" or ".flac");

        public static string PreviewFolder => Path.Combine(Path.GetTempPath(), "Smart Tools", "Audio Trimmer");

        /// <summary>Removes the preview copies: all of them (closing), or only the ones older than a day (starting).</summary>
        public static void SweepPreviewFolder(bool everything)
        {
            try
            {
                if (!Directory.Exists(PreviewFolder)) return;
                foreach (string file in Directory.EnumerateFiles(PreviewFolder))
                {
                    try
                    {
                        if (everything || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // still in use by a player: it goes next time
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // nothing to clean
            }
        }

        /// <summary>Converts a file to a WAV in the preview folder so the player can open it. Returns its path.</summary>
        public static async Task<string> MakePlaybackCopyAsync(string path, double duration, CancellationToken token)
        {
            Directory.CreateDirectory(PreviewFolder);
            string wav = Path.Combine(PreviewFolder, $"{Guid.NewGuid():N}.wav");
            string temp = wav + ".tmp";
            // 16-bit stereo at up to 48 kHz is ~192 KB per second.
            Smart_Tools.WPF.Controllers.OutputFolderManager.EnsureFreeSpace(PreviewFolder, (long)(Math.Max(0, duration) * 192_000));
            try
            {
                var args = new List<string>
                {
                    "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-i", path, "-map", "0:a:0", "-vn",
                    "-c:a", "pcm_s16le", "-rf64", "auto", "-f", "wav", temp,
                };
                await RunFfmpegAsync(args, duration, null, token).ConfigureAwait(false);
                File.Move(temp, wav, overwrite: true);
                return wav;
            }
            finally
            {
                TryDelete(temp);
            }
        }

        // =================================================================
        //  Export
        // =================================================================

        /// <summary>
        /// Writes the joined result to <paramref name="finalPath"/>. Reports 0-1. Peak normalize measures first
        /// (a quick pass over the same graph), then encodes with the gain that puts the loudest point at
        /// <see cref="PeakTargetDb"/>. Throws OperationCanceledException on cancel (nothing is left behind) and
        /// InvalidOperationException with a plain sentence when something is wrong. The caller has already asked
        /// about replacing an existing file.
        /// </summary>
        public static async Task<AudioTrimExportResult> ExportAsync(
            IReadOnlyList<AudioTrimClipState> clips, AudioTrimExportOptions options, string finalPath,
            IProgress<double>? progress, CancellationToken token)
        {
            if (MissingEngine() is { } missing) throw new InvalidOperationException(missing);
            if (clips.Count == 0) throw new InvalidOperationException("There is nothing to export.");

            string full = Path.GetFullPath(finalPath);
            foreach (var c in clips)
            {
                if (!File.Exists(c.Source.Path)) throw new InvalidOperationException($"\"{c.Source.FileName}\" isn't there any more.");
                if (string.Equals(Path.GetFullPath(c.Source.Path), full, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The result can't replace \"{c.Source.FileName}\", one of the files it is made from. Pick another name.");
            }

            double total = clips.Sum(c => c.Duration);
            string folder = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("That isn't a place a file can be saved.");
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(full)}.creating-{Guid.NewGuid():N}.tmp");

            try
            {
                double? gain = null;
                bool silent = false;
                bool measure = options.Normalize == AudioTrimNormalize.Peak;
                double encodeStart = measure ? 0.4 : 0;

                if (measure)
                {
                    var analysis = BuildArguments(clips, options, temp, null, analysisOnly: true);
                    var run = await RunFfmpegAsync(analysis, total, f => progress?.Report(f * encodeStart), token).ConfigureAwait(false);
                    var max = ParseMaxVolume(run);
                    gain = GainForPeak(max);
                    silent = max == null || max < -90;                 // nothing but silence: leave it as it is
                    if (silent) gain = null;
                }

                var args = BuildArguments(clips, options, temp, gain, analysisOnly: false);
                await RunFfmpegAsync(args, total, f => progress?.Report(encodeStart + f * (1 - encodeStart)), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new InvalidOperationException("ffmpeg didn't write anything. The audio may be damaged.");
                File.Move(temp, full, overwrite: true);
                progress?.Report(1);
                return new AudioTrimExportResult(full, new FileInfo(full).Length, total, gain, silent);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static void TryDelete(string path) => Smart_Tools.WPF.Controllers.SafeFileOps.TryDelete(path);

        // =================================================================
        //  ffmpeg process
        // =================================================================

        /// <summary>
        /// Runs ffmpeg (arguments include -progress pipe:1) and returns everything it wrote to its error
        /// output (volumedetect prints there). Reports the fraction done from out_time against
        /// <paramref name="totalSeconds"/>. Cancel kills the whole process tree.
        /// </summary>
        internal static async Task<string> RunFfmpegAsync(List<string> args, double totalSeconds, Action<double>? onFraction, CancellationToken token)
        {
            string exe = ConverterManager.FfmpegExe ?? throw new InvalidOperationException($"ffmpeg wasn't found in {FfmpegFolder}.");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-hide_banner");
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            var log = new StringBuilder();
            long lastActivity = DateTime.UtcNow.Ticks;
            void Touch() => Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Touch();
                if (totalSeconds > 0 && onFraction != null && ParseProgressSeconds(e.Data) is double seconds)
                    onFraction(Math.Clamp(seconds / totalSeconds, 0, 0.995));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Touch();
                lock (log)
                {
                    if (log.Length < 400_000) log.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = process.WaitForExitAsync(CancellationToken.None);
            while (true)
            {
                var done = await Task.WhenAny(exited, Task.Delay(200, CancellationToken.None)).ConfigureAwait(false);
                if (done == exited) break;

                bool stuck = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastActivity), DateTimeKind.Utc) > InactivityTimeout;
                if (token.IsCancellationRequested || stuck)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                    await exited.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("ffmpeg stopped responding and was stopped. The audio may be damaged.");
                }
            }
            process.WaitForExit();                                            // flushes the output events

            string text;
            lock (log) text = log.ToString();
            if (process.ExitCode != 0) throw new InvalidOperationException(PlainError(text));
            return text;
        }

        /// <summary>Seconds from an "out_time_us=" line of -progress output (microseconds), else null.</summary>
        public static double? ParseProgressSeconds(string line)
        {
            const string key = "out_time_us=";
            if (!line.StartsWith(key, StringComparison.Ordinal)) return null;
            return long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0 ? us / 1_000_000.0 : null;
        }

        /// <summary>A sentence for the user out of ffmpeg's log.</summary>
        public static string PlainError(string log)
        {
            if (log.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)) return "A file isn't valid audio, or it is damaged.";
            if (log.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase)
                || log.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
                || log.Contains("Stream specifier", StringComparison.OrdinalIgnoreCase)) return "A file has no sound track.";
            if (log.Contains("No such file", StringComparison.OrdinalIgnoreCase)) return "A file couldn't be found.";
            if (log.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "Windows doesn't allow writing there. Pick another folder.";
            if (log.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return "There isn't enough free space on that drive.";

            string last = log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            return last.Length > 0 ? "ffmpeg said: " + last : "ffmpeg couldn't finish.";
        }
    }
}
