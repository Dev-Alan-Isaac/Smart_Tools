using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Video to Audio - ffprobe (tracks and length) and the batch run (ffmpeg with live progress).</summary>
    public static partial class VideoToAudioManager
    {
        /// <summary>Two files at a time: audio encoding is light, and more mostly fights over the disk.</summary>
        public const int Parallelism = 2;

        public static string FfmpegFolder => ConverterManager.FfmpegFolder;

        /// <summary>Null when ffmpeg and ffprobe are both there, else what's missing, in a sentence.</summary>
        public static string? MissingEngine()
        {
            bool ffmpeg = ConverterManager.FfmpegExe != null, ffprobe = ConverterManager.FfprobeExe != null;
            if (ffmpeg && ffprobe) return null;
            string which = !ffmpeg && !ffprobe ? "ffmpeg.exe and ffprobe.exe" : !ffmpeg ? "ffmpeg.exe" : "ffprobe.exe";
            return $"{which} wasn't found in {FfmpegFolder}. This tool needs it to read and extract the sound.";
        }

        // =================================================================
        //  ffprobe
        // =================================================================

        /// <summary>Length and sound tracks of one file. Never throws (except on cancel): problems go in <see cref="VtaProbe.Error"/>.</summary>
        public static async Task<VtaProbe> ProbeAsync(string path, CancellationToken token)
        {
            string? exe = ConverterManager.FfprobeExe;
            if (exe == null) return new VtaProbe(null, Array.Empty<VtaAudioTrack>(), $"ffprobe wasn't found in {FfmpegFolder}.");

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[]
            {
                "-v", "error",
                "-show_entries", "format=duration:stream=codec_type,codec_name,channels,sample_rate,bit_rate:stream_tags=language,title",
                "-of", "json", path,
            })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return new VtaProbe(null, Array.Empty<VtaAudioTrack>(), "ffprobe couldn't start: " + ex.Message);
            }

            var outTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                token.ThrowIfCancellationRequested();
                return new VtaProbe(null, Array.Empty<VtaAudioTrack>(), "Reading the file took too long - it may be damaged.");
            }

            string json = await outTask.ConfigureAwait(false);
            string err = (await errTask.ConfigureAwait(false)).Trim();
            var parsed = ParseProbe(json);
            if (parsed == null)
            {
                string reason = err.Length > 0 ? err.Split('\n').Last().Trim() : $"exit code {process.ExitCode}";
                return new VtaProbe(null, Array.Empty<VtaAudioTrack>(), "The file couldn't be read - it may be damaged or not really a video (" + reason + ").");
            }
            return parsed;
        }

        /// <summary>Turns ffprobe's JSON into tracks (numbered among the sound streams, as -map 0:a:N counts them). Null when it isn't JSON with a format.</summary>
        public static VtaProbe? ParseProbe(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("format", out var format)) return null;

                TimeSpan? duration = null;
                if (format.TryGetProperty("duration", out var d)
                    && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    && seconds > 0 && double.IsFinite(seconds))
                    duration = TimeSpan.FromSeconds(seconds);

                var tracks = new List<VtaAudioTrack>();
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        if (Str(s, "codec_type") != "audio") continue;
                        string? language = null, title = null;
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            language = Str(tags, "language");
                            title = Str(tags, "title");
                        }
                        tracks.Add(new VtaAudioTrack(
                            tracks.Count,
                            Str(s, "codec_name") ?? "audio",
                            (int)(Num(s, "channels") ?? 0),
                            (int)(Num(s, "sample_rate") ?? 0),
                            Num(s, "bit_rate"),
                            language,
                            title));
                    }
                }
                return new VtaProbe(duration, tracks, null);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>ffprobe writes some numbers as numbers and some as strings ("sample_rate": "48000").</summary>
        private static long? Num(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return null;
        }

        // =================================================================
        //  Running a batch
        // =================================================================

        /// <summary>
        /// Extracts every row in <paramref name="items"/> (call on the UI
        /// thread: rows are updated there). Each output is written to a hidden
        /// .tmp in its destination and moved into place under a name nothing
        /// else uses (<paramref name="resultPathFor"/> - the Output card's
        /// ResultPathFor - is asked again at move time, so two videos called
        /// "clip.mp4" in different folders become "clip.mp3" and "clip (2).mp3").
        /// One bad file doesn't stop the others; cancelling keeps what's finished.
        /// </summary>
        public static async Task<VtaRunResult> RunAsync(
            IReadOnlyList<VtaFileItem> items,
            VtaOptions options,
            Func<string, string, string> resultPathFor,
            IProgress<VtaProgress> progress,
            CancellationToken token)
        {
            if (ConverterManager.FfmpegExe == null)
                throw new InvalidOperationException($"ffmpeg.exe wasn't found in {FfmpegFolder}.");

            var result = new VtaRunResult();
            var clock = Stopwatch.StartNew();
            var live = new Dictionary<VtaFileItem, double>();
            int finished = 0, failed = 0;
            DateTime lastReport = DateTime.MinValue;

            void Report(string? current, bool force = false)
            {
                var now = DateTime.UtcNow;
                if (!force && now - lastReport < TimeSpan.FromMilliseconds(100)) return;   // ~10 updates a second
                lastReport = now;
                double partial = live.Values.Sum() / 100.0;
                double percent = items.Count == 0 ? 100 : Math.Min(100, (finished + partial) * 100.0 / items.Count);
                progress.Report(new VtaProgress(finished, failed, items.Count, percent, current));
            }

            using var gate = new SemaphoreSlim(Parallelism);
            var tasks = items.Select(async item =>
            {
                await gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        item.Status = VtaItemStatus.Cancelled;
                        item.Message = "Cancelled before it started.";
                        result.Cancelled++;
                        return;
                    }

                    live[item] = 0;
                    Report(item.Name, force: true);
                    var outcome = await ExtractOneAsync(item, options, resultPathFor, pc =>
                    {
                        live[item] = pc;
                        Report(item.Name);
                    }, token);

                    live.Remove(item);
                    finished++;
                    switch (outcome)
                    {
                        case VtaItemStatus.Done:
                            result.Done++;
                            result.Files.AddRange(item.OutputPaths);
                            foreach (string f in item.OutputPaths)
                            {
                                try { result.OutputBytes += new FileInfo(f).Length; }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                            }
                            break;
                        case VtaItemStatus.Cancelled:
                            result.Cancelled++;
                            break;
                        default:
                            result.Failed++;
                            failed++;
                            break;
                    }
                    Report(null, force: true);
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
            result.Elapsed = clock.Elapsed;
            return result;
        }

        /// <summary>One video: every track it should give, one after the other. Returns the row's final status.</summary>
        private static async Task<VtaItemStatus> ExtractOneAsync(
            VtaFileItem item, VtaOptions o, Func<string, string, string> resultPathFor, Action<double> onPercent, CancellationToken token)
        {
            var tracks = TracksToExtract(item, o.TrackMode);
            item.Status = VtaItemStatus.Extracting;
            item.Progress = 0;
            item.Message = "";
            var outputs = new List<string>();
            long bytes = 0;

            try
            {
                if (!File.Exists(item.FullPath)) throw new FileNotFoundException("The video isn't there any more.");
                for (int t = 0; t < tracks.Count; t++)
                {
                    token.ThrowIfCancellationRequested();
                    int index = t;
                    string name = OutputFileName(item.FullPath, tracks[t], tracks.Count, o.Format);
                    string planned = resultPathFor(item.FullPath, name);
                    string folder = Path.GetDirectoryName(planned) ?? throw new IOException("No output folder.");
                    Directory.CreateDirectory(folder);
                    string temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(name)}.extracting-{Guid.NewGuid():N}.tmp");

                    try
                    {
                        // ffmpeg reports from a pool thread: hop to the UI thread (captured here) before touching the row.
                        IProgress<double> perTrack = new Progress<double>(pc =>
                        {
                            if (item.Status != VtaItemStatus.Extracting) return;   // a late update after the row finished
                            double overall = (index * 100 + pc) / tracks.Count;
                            item.Progress = overall;
                            onPercent(overall);
                        });
                        var args = BuildArguments(item.FullPath, tracks[t], o, temp);
                        await ConverterManager.RunFfmpegAsync(args, item.Duration?.TotalSeconds ?? 0, perTrack.Report, token);
                        token.ThrowIfCancellationRequested();

                        string final = MoveIntoPlace(temp, item.FullPath, name, resultPathFor);
                        outputs.Add(final);
                        bytes += new FileInfo(final).Length;
                    }
                    finally
                    {
                        TryDelete(temp);
                    }
                }

                item.OutputPaths = outputs;
                item.ResultText = VtaText.FormatBytes(bytes);
                item.Message = outputs.Count == 1 ? "Saved as " + outputs[0] : "Saved:" + Environment.NewLine + string.Join(Environment.NewLine, outputs);
                item.Status = VtaItemStatus.Done;
            }
            catch (OperationCanceledException)
            {
                item.OutputPaths = outputs;
                item.Message = outputs.Count == 0
                    ? "Cancelled - nothing was saved for this video."
                    : "Cancelled - kept the tracks already finished:" + Environment.NewLine + string.Join(Environment.NewLine, outputs);
                item.Status = VtaItemStatus.Cancelled;
            }
            catch (Exception ex)
            {
                item.OutputPaths = outputs;
                item.Message = ex.Message;
                item.Status = VtaItemStatus.Failed;
            }
            item.Progress = 0;
            return item.Status;
        }

        /// <summary>Moves the finished .tmp to a free name (asked for again now, so nothing is ever replaced).</summary>
        private static string MoveIntoPlace(string temp, string source, string name, Func<string, string, string> resultPathFor)
        {
            for (int attempt = 0; ; attempt++)
            {
                string final = resultPathFor(source, name);
                try
                {
                    File.Move(temp, final, overwrite: false);
                    return final;
                }
                catch (IOException) when (attempt < 5 && File.Exists(final))
                {
                    // another program took the name in between: ask again
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a leftover hidden .tmp is harmless
            }
        }
    }
}
