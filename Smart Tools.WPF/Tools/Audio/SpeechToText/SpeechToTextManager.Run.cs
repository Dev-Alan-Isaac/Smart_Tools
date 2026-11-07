using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The engines: FFmpeg makes 16 kHz audio, the bundled whisper.cpp reads it.</summary>
    public static partial class SpeechToTextManager
    {
        /// <summary>A long recording on a slow PC can take a long time; this is only for a run that has gone completely silent.</summary>
        private static readonly TimeSpan WhisperInactivityTimeout = TimeSpan.FromMinutes(20);

        private const int VcRuntimeMissing = -1073741515;   // 0xC0000135: a DLL the program needs wasn't found

        /// <summary>Where the bundled whisper.cpp lives (Engines\WHISPER next to the .exe).</summary>
        public static string WhisperFolder { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engines", "WHISPER");

        public static string WhisperExe { get; } = Path.Combine(WhisperFolder, "whisper-cli.exe");

        public static bool IsWhisperAvailable => File.Exists(WhisperExe);

        /// <summary>A plain sentence for what is missing before a run can start, or null when everything is there.</summary>
        public static string? MissingEngine(string modelId)
        {
            if (ConverterManager.FfmpegExe == null) return "FFmpeg isn't installed with this copy of Smart Tools, so the sound can't be prepared.";
            if (!IsWhisperAvailable) return "The speech engine isn't in this copy of Smart Tools.";
            if (!WhisperModelManager.IsInstalled(modelId))
                return $"Install the {FindModel(modelId)?.Name ?? modelId} model first: press Install next to it in the sidebar.";
            return null;
        }

        // =================================================================
        //  The run
        // =================================================================

        /// <summary>
        /// Transcribes the rows that can run, one at a time (whisper already uses the whole CPU). A failed file is marked and the
        /// rest go on; canceling keeps finished files. Call on the UI thread: rows are changed here.
        /// </summary>
        public static async Task<SttRunResult> RunAsync(IReadOnlyList<SttFileItem> items, SttOptions options,
                                                        Func<string, string, string> resultPathFor, IProgress<SttProgress> progress, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var pending = items.Where(i => i.CanRun).ToList();
            var files = new List<string>();
            int finished = 0, failed = 0, cancelled = 0;
            DateTime lastReport = DateTime.MinValue;

            void Report(string? current, double itemPercent, bool force)
            {
                if (!force && (DateTime.UtcNow - lastReport).TotalMilliseconds < 100) return;
                lastReport = DateTime.UtcNow;
                double percent = pending.Count == 0 ? 0 : (finished + failed + itemPercent / 100.0) * 100.0 / pending.Count;
                progress.Report(new SttProgress(finished, failed, pending.Count, Math.Min(100, percent), current));
            }

            foreach (var item in pending)
            {
                if (token.IsCancellationRequested)
                {
                    item.Status = SttItemStatus.Cancelled;
                    item.Message = "Canceled before it started.";
                    cancelled++;
                    continue;
                }

                Report(item.Name, 0, force: true);
                var outcome = await TranscribeOneAsync(item, options, resultPathFor, p =>
                {
                    item.Progress = p;
                    Report(item.Name, p, force: false);
                }, token);

                switch (outcome)
                {
                    case SttItemStatus.Done: finished++; files.AddRange(item.OutputPaths); break;
                    case SttItemStatus.Cancelled: cancelled++; break;
                    default: failed++; break;
                }
                Report(item.Name, 0, force: true);
            }

            watch.Stop();
            return new SttRunResult(finished, failed, cancelled, files, watch.Elapsed);
        }

        private static async Task<SttItemStatus> TranscribeOneAsync(SttFileItem item, SttOptions options, Func<string, string, string> resultPathFor,
                                                                    Action<double> onPercent, CancellationToken token)
        {
            item.Status = SttItemStatus.Working;
            item.Progress = 0;
            item.Message = "";
            item.OutputPaths.Clear();

            string temp = Path.Combine(Path.GetTempPath(), "smarttools-stt-" + Guid.NewGuid().ToString("N"));
            string wav = temp + ".wav", json = temp + ".json";
            try
            {
                await ConverterManager.RunFfmpegAsync(BuildWavArguments(item.FullPath, wav), item.Duration ?? 0, p => onPercent(p * 0.08), token);

                var args = BuildWhisperArguments(WhisperModelManager.PathOf(options.ModelId), wav, temp, options, ThreadCount());
                await RunWhisperAsync(args, p => onPercent(8 + p * 0.92), token);

                if (!File.Exists(json)) throw new InvalidOperationException("The speech engine didn't produce a result.");
                var parsed = ParseWhisperJson(await File.ReadAllTextAsync(json, token))
                             ?? throw new InvalidOperationException("The speech engine's result couldn't be read.");

                item.Segments = parsed.Segments;
                item.Language = parsed.Language.Length > 0 ? parsed.Language : options.Language;
                item.Text = FormatTxt(parsed.Segments, options.TimestampsInText);

                if (parsed.Segments.Count == 0)
                {
                    item.Message = "No speech was found in this file.";
                }
                else
                {
                    item.OutputPaths.AddRange(WriteOutputs(item, options, resultPathFor));
                    item.Message = "Saved " + string.Join(", ", item.OutputPaths.Select(Path.GetFileName));
                }
                item.Progress = 100;
                item.Status = SttItemStatus.Done;
                return SttItemStatus.Done;
            }
            catch (OperationCanceledException)
            {
                item.Status = SttItemStatus.Cancelled;
                item.Message = "Canceled. Finished files are kept.";
                return SttItemStatus.Cancelled;
            }
            catch (Exception ex)
            {
                item.Status = SttItemStatus.Failed;
                item.Message = ex.Message;
                return SttItemStatus.Failed;
            }
            finally
            {
                TryDelete(wav);
                TryDelete(json);
            }
        }

        /// <summary>
        /// Writes the chosen formats. They share one name: if any of them is taken, all move to "name (2)", so a transcript's files stay together.
        /// <paramref name="resultPathFor"/> never returns an existing file.
        /// </summary>
        private static List<string> WriteOutputs(SttFileItem item, SttOptions options, Func<string, string, string> resultPathFor)
        {
            var formats = new List<(string Ext, string Content)>();
            if (options.WriteTxt) formats.Add((".txt", FormatTxt(item.Segments, options.TimestampsInText)));
            if (options.WriteSrt) formats.Add((".srt", FormatSrt(item.Segments)));
            if (options.WriteVtt) formats.Add((".vtt", FormatVtt(item.Segments)));
            if (formats.Count == 0) formats.Add((".txt", FormatTxt(item.Segments, options.TimestampsInText)));

            string stem = Path.GetFileNameWithoutExtension(item.FullPath);
            List<string> paths = new();
            for (int n = 1; n <= 99; n++)
            {
                string candidate = n == 1 ? stem : $"{stem} ({n})";
                paths = formats.Select(f => resultPathFor(item.FullPath, candidate + f.Ext)).ToList();
                if (paths.All(p => string.Equals(Path.GetFileNameWithoutExtension(p), candidate, StringComparison.OrdinalIgnoreCase))) break;
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            for (int i = 0; i < formats.Count; i++) AtomicFile.WriteAllText(paths[i], formats[i].Content, utf8);
            return paths;
        }

        // =================================================================
        //  whisper-cli
        // =================================================================

        /// <summary>Runs whisper-cli, turning its "progress = N%" lines into <paramref name="onPercent"/> (0..100).</summary>
        private static async Task RunWhisperAsync(List<string> args, Action<double> onPercent, CancellationToken token)
        {
            var psi = new ProcessStartInfo(WhisperExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = WhisperFolder,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            var tail = new Queue<string>();
            long lastActivity = DateTime.UtcNow.Ticks;
            void Touch() => Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);

            process.OutputDataReceived += (_, e) => { if (e.Data != null) Touch(); };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Touch();
                if (ParseProgress(e.Data) is double p)
                {
                    onPercent(p);
                    return;
                }
                lock (tail)
                {
                    tail.Enqueue(e.Data);
                    if (tail.Count > 20) tail.Dequeue();
                }
            };

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException("The speech engine couldn't start: " + ex.Message);
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = process.WaitForExitAsync(CancellationToken.None);
            while (true)
            {
                var done = await Task.WhenAny(exited, Task.Delay(200, CancellationToken.None)).ConfigureAwait(false);
                if (done == exited) break;

                bool stuck = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastActivity), DateTimeKind.Utc) > WhisperInactivityTimeout;
                if (token.IsCancellationRequested || stuck)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    await exited.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("The speech engine stopped responding and was stopped.");
                }
            }
            process.WaitForExit();   // flushes the output events

            if (process.ExitCode != 0)
            {
                List<string> lines;
                lock (tail) lines = tail.ToList();
                throw new InvalidOperationException(FriendlyWhisperError(lines, process.ExitCode));
            }
            onPercent(100);
        }

        /// <summary>A readable reason for a failed run.</summary>
        public static string FriendlyWhisperError(IReadOnlyList<string> stderrTail, int exitCode)
        {
            if (exitCode == VcRuntimeMissing)
                return "The speech engine needs the Microsoft Visual C++ Redistributable (x64), which isn't installed on this PC. Install it from Microsoft and try again.";

            string? line = stderrTail.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                           ?? stderrTail.LastOrDefault(l => l.Trim().Length > 0);
            string detail = line?.Trim() ?? $"exit code {exitCode}";
            return "The speech engine stopped with an error: " + (detail.Length > 200 ? detail[..200] + "…" : detail);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
