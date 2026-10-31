using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Spectrogram Generator: pictures of a file's sound (time across, pitch
    /// up, loudness as colour) made by ffmpeg's showspectrumpic filter. No WPF
    /// here (images are loaded in SpectrogramManager.Render.cs), so this file
    /// is tested against the real ffmpeg in a plain test project.
    ///
    /// Fixed against the old SpectogramManager:
    ///  - "Frequency scale" set showspectrumpic's <c>scale</c>, which is the
    ///    colour (loudness) curve; the frequency axis is <c>fscale</c>. The
    ///    axis never changed - Linear just made the picture darker.
    ///  - Min/max frequency used high/low-pass filters, which only fade the
    ///    sound: the picture still showed 0 Hz to the top, with the "removed"
    ///    tones still visible. Now <c>start</c>/<c>stop</c> crop the axis.
    ///  - A max above what the file holds left the top of the picture empty;
    ///    it is now limited to half the sample rate, with a note.
    ///  - Videos without sound gave ffmpeg's "Cannot find a matching stream";
    ///    the probe now says "This file has no sound track" before running.
    ///  - Stop didn't stop running ffmpeg processes (up to 10 minutes each,
    ///    and they kept going after the app closed); single files had no Stop.
    ///  - Every picture was left in %TEMP% for good; working copies now live
    ///    in one session folder that is deleted on close (and old ones on start).
    ///  - Batch rows held full-size images (gigabytes at 10,000 files) and only
    ///    appeared at the end; cancelling threw away everything already made.
    ///  - Auto Save always wrote "spectrogram.png" over the last one. Saves now
    ///    use the source file's name and add " (2)" instead of replacing.
    ///  - The folder scan followed folder links: a link back to a parent made
    ///    it list the same files over and over until the path got too long.
    ///    The batch list's duplicate check was also O(n²) and case-sensitive.
    ///  - Width/height had no upper limit (ffmpeg would try 100000 × 100000).
    /// </summary>
    public static class SpectrogramManager
    {
        // =================================================================
        //  Limits, presets, file types
        // =================================================================

        public const int MinWidth = 128, MaxWidth = 8192;
        public const int MinHeight = 64, MaxHeight = 4096;

        /// <summary>The legend frame ffmpeg adds around the spectrum (measured: the same at every size).</summary>
        public const int LegendExtraWidth = 282, LegendExtraHeight = 128;

        /// <summary>How long one file may take before it's treated as stuck.</summary>
        public static TimeSpan PerFileTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>ffmpeg processes at once in batch mode.</summary>
        public static int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

        public static readonly IReadOnlyList<(string Label, int Width, int Height)> SizePresets = new[]
        {
            ("Small", 800, 400),
            ("Medium", 1024, 512),
            ("Full HD", 1920, 1080),
            ("4K", 3840, 2160),
        };

        /// <summary>Frequency range chips. Null = no limit at that end.</summary>
        public static readonly IReadOnlyList<(string Key, string Label, int? Min, int? Max, string Hint)> RangePresets = new[]
        {
            ("Full", "Full", (int?)null, (int?)null, "Everything the file holds"),
            ("Speech", "Speech", 80, 8000, "80 Hz – 8 kHz: where voices sit"),
            ("Bass", "Bass", 20, 500, "20 – 500 Hz: kick drums, bass lines, hum"),
            ("Highs", "Highs", 2000, null, "2 kHz and up: cymbals, hiss, a cut-off from lossy encoding"),
        };

        public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".aac", ".ogg", ".oga", ".m4a", ".wma", ".opus", ".aif", ".aiff", ".alac", ".ac3", ".amr", ".mka", ".wv", ".ape",
        };

        public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp",
        };

        public static bool IsSupported(string path) =>
            AudioExtensions.Contains(Path.GetExtension(path)) || VideoExtensions.Contains(Path.GetExtension(path));

        public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

        public static string FileDialogFilter =>
            "Audio and video|" + string.Join(";", AudioExtensions.Concat(VideoExtensions).Select(e => "*" + e)) +
            "|Audio|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) +
            "|Video|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) +
            "|All files|*.*";

        public static string ExtensionFor(SpectrogramExportFormat format) => format switch
        {
            SpectrogramExportFormat.Jpg => ".jpg",
            SpectrogramExportFormat.Bmp => ".bmp",
            _ => ".png",
        };

        /// <summary>The colour stops of each palette, sampled from ffmpeg's own output (quiet → loud), for the sidebar swatches.</summary>
        public static IReadOnlyList<string> PaletteStops(SpectrogramColorScheme scheme) => scheme switch
        {
            SpectrogramColorScheme.Intensity => new[] { "#000000", "#4F0171", "#950159", "#D3011C", "#FA5D00", "#FFDB2B", "#FFFFD2" },
            SpectrogramColorScheme.Rainbow => new[] { "#000000", "#111487", "#0E98E0", "#0DAA4F", "#8ED00C", "#EA8C28", "#D82618" },
            SpectrogramColorScheme.Magma => new[] { "#000000", "#521A61", "#A53064", "#CC3D5B", "#F65D58", "#F19E6A", "#F6E7BC" },
            SpectrogramColorScheme.Viridis => new[] { "#000000", "#386B8A", "#2E8A89", "#34A27D", "#52B769", "#85CA4B", "#C5DF40" },
            SpectrogramColorScheme.Plasma => new[] { "#000000", "#4E1A96", "#6E2590", "#8F2F89", "#BC7032", "#E1C325", "#F6F2B3" },
            SpectrogramColorScheme.Cividis => new[] { "#000000", "#163257", "#293F64", "#43536D", "#757671", "#B6AC64", "#F8F2B8" },
            SpectrogramColorScheme.Fire => new[] { "#000000", "#2E2D32", "#44362B", "#634024", "#96531C", "#C7872F", "#F2E3C6" },
            SpectrogramColorScheme.Fiery => new[] { "#000000", "#5D0F0D", "#870C07", "#C23D1C", "#E7902F", "#E5D866", "#F6F7E5" },
            SpectrogramColorScheme.Fruit => new[] { "#000000", "#0B412A", "#177E15", "#729E0D", "#F0AF3B", "#F77F31", "#E84114" },
            SpectrogramColorScheme.Cool => new[] { "#000000", "#0043C9", "#005EAA", "#627989", "#C89367", "#FFAE46", "#FFC925" },
            SpectrogramColorScheme.Green => new[] { "#000000", "#004A2B", "#007343", "#009D5B", "#00C673", "#21E5A3", "#CFFBEC" },
            SpectrogramColorScheme.Terrain => new[] { "#000000", "#1139B3", "#28BE77", "#40FF39", "#B9FF1C", "#FFE71C", "#FFF3C3" },
            SpectrogramColorScheme.Nebulae => new[] { "#100515", "#1A1325", "#251A2A", "#3B2733", "#9A5452", "#D65F57", "#F6D3D1" },
            SpectrogramColorScheme.Moreland => new[] { "#162688", "#6786D8", "#9AB2E0", "#C4C8D3", "#D7C5B9", "#E4A288", "#D46955" },
            _ => new[] { "#000000", "#404241", "#636564", "#888A89", "#ACAEAD", "#D1D3D2", "#F5F7F6" },   // Channel (mono = grey; each channel gets its own colour)
        };

        public static string PaletteHint(SpectrogramColorScheme scheme) => scheme switch
        {
            SpectrogramColorScheme.Intensity => "ffmpeg's default - quiet purple to loud yellow-white.",
            SpectrogramColorScheme.Viridis or SpectrogramColorScheme.Cividis => "Easy to read for colour-blind viewers, and prints well in grey.",
            SpectrogramColorScheme.Magma or SpectrogramColorScheme.Plasma => "Smooth and even - small differences in loudness stay visible.",
            SpectrogramColorScheme.Channel => "Each channel gets its own colour - shows stereo differences.",
            SpectrogramColorScheme.Rainbow or SpectrogramColorScheme.Terrain or SpectrogramColorScheme.Fruit => "Bright and punchy - eye-catching, but steps can look like edges.",
            _ => "",
        };

        // =================================================================
        //  Reading the sidebar
        // =================================================================

        /// <summary>Checks the typed sizes and range. Returns null and a plain error when something is off.</summary>
        public static SpectrogramOptions? TryBuildOptions(
            string widthText, string heightText, string minText, string maxText,
            SpectrogramColorScheme scheme, SpectrogramFrequencyScale scale, SpectrogramChannelMode channels,
            bool legend, SpectrogramExportFormat format, out string? error)
        {
            error = null;
            if (!int.TryParse(widthText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int width) || width < MinWidth || width > MaxWidth)
            {
                error = $"Width must be a whole number from {MinWidth} to {MaxWidth:N0}.";
                return null;
            }
            if (!int.TryParse(heightText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int height) || height < MinHeight || height > MaxHeight)
            {
                error = $"Height must be a whole number from {MinHeight} to {MaxHeight:N0}.";
                return null;
            }

            int? min = ParseHz(minText, out bool minOk);
            if (!minOk)
            {
                error = "Lowest frequency must be a whole number of Hz (or blank for 0 Hz).";
                return null;
            }
            int? max = ParseHz(maxText, out bool maxOk);
            if (!maxOk || max == 0)
            {
                error = "Highest frequency must be a whole number of Hz above 0 (or blank for everything).";
                return null;
            }
            if (min is { } lo && max is { } hi && lo >= hi)
            {
                error = "The lowest frequency must be below the highest.";
                return null;
            }

            return new SpectrogramOptions
            {
                WidthPx = width,
                HeightPx = height,
                MinFrequencyHz = min is 0 ? null : min,
                MaxFrequencyHz = max,
                ColorScheme = scheme,
                FrequencyScale = scale,
                ChannelMode = channels,
                ShowLegend = legend,
                OutputFormat = format,
            };
        }

        /// <summary>Blank = null. Accepts "8000", "8 000", "8k", "8 kHz", "12.5k".</summary>
        public static int? ParseHz(string? text, out bool ok)
        {
            ok = true;
            string t = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("hz", "");
            if (t.Length == 0) return null;
            double factor = 1;
            if (t.EndsWith('k'))
            {
                factor = 1000;
                t = t[..^1];
            }
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 && v * factor <= 1_000_000)
                return (int)Math.Round(v * factor);
            ok = false;
            return null;
        }

        /// <summary>Final picture size: the spectrum area plus the legend frame when it's shown.</summary>
        public static (int Width, int Height) OutputSize(SpectrogramOptions o) =>
            o.ShowLegend ? (o.WidthPx + LegendExtraWidth, o.HeightPx + LegendExtraHeight) : (o.WidthPx, o.HeightPx);

        /// <summary>A key for "made with these settings" - a change marks finished rows as stale.</summary>
        public static string SettingsKey(SpectrogramOptions o) =>
            string.Join("|", o.WidthPx, o.HeightPx, o.ColorScheme, o.FrequencyScale, o.ChannelMode, o.MinFrequencyHz, o.MaxFrequencyHz, o.ShowLegend, o.OutputFormat);

        // =================================================================
        //  ffmpeg command
        // =================================================================

        /// <summary>
        /// The frequency range ffmpeg is given, fitted to the file: the top
        /// can't pass half the sample rate (there's nothing above it), and a
        /// bottom above that is an error. Null start/stop = ffmpeg's default
        /// (0 Hz / the top); a bottom with no top gets the real top.
        /// </summary>
        public static (int? Start, int? Stop, string? Note, string? Error) FitRange(SpectrogramOptions o, int sampleRate)
        {
            int nyquist = sampleRate > 0 ? sampleRate / 2 : int.MaxValue;
            int? start = o.MinFrequencyHz is > 0 ? o.MinFrequencyHz : null;
            int? stop = o.MaxFrequencyHz;
            string? note = null;

            if (start is { } s && s >= nyquist)
                return (null, null, null, $"This file holds sound up to {SpectrogramText.Hertz(nyquist)} only, so there's nothing above {SpectrogramText.Hertz(s)} to show. Lower the bottom of the range.");

            if (stop is { } hi && hi > nyquist)
            {
                note = $"Top limited to {SpectrogramText.Hertz(nyquist)} - this file ({sampleRate / 1000.0:0.#} kHz) holds nothing higher.";
                stop = null;
            }
            // ffmpeg's "stop=0" means "to the top" only when start is 0 too; with a
            // start it fails ("Stop frequency should be greater than start").
            if (start != null && stop == null && sampleRate > 0) stop = nyquist;
            return (start, stop, note, null);
        }

        public static string ColorName(SpectrogramColorScheme scheme) => scheme.ToString().ToLowerInvariant();

        /// <summary>The showspectrumpic filter. Pure, so the exact options are unit-tested.</summary>
        public static string BuildFilter(SpectrogramOptions o, int? start, int? stop)
        {
            var parts = new List<string>
            {
                $"s={Math.Clamp(o.WidthPx, MinWidth, MaxWidth)}x{Math.Clamp(o.HeightPx, MinHeight, MaxHeight)}",
                $"mode={(o.ChannelMode == SpectrogramChannelMode.Separate ? "separate" : "combined")}",
                $"color={ColorName(o.ColorScheme)}",
                $"fscale={(o.FrequencyScale == SpectrogramFrequencyScale.Logarithmic ? "log" : "lin")}",   // the frequency axis (the old code set "scale", the colour curve)
                "scale=log",                                                                                  // loudness in dB - what people expect
                $"legend={(o.ShowLegend ? 1 : 0)}",
            };
            if (start is { } s) parts.Add($"start={s}");
            if (stop is { } e) parts.Add($"stop={e}");
            return $"[0:a:0]showspectrumpic={string.Join(":", parts)}[img]";
        }

        /// <summary>Arguments as a list (no hand-quoting - the old string broke on unusual names).</summary>
        public static List<string> BuildArguments(string input, string output, SpectrogramOptions o, int? start, int? stop)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error", "-i", input, "-filter_complex", BuildFilter(o, start, stop), "-map", "[img]", "-frames:v", "1" };
            if (o.OutputFormat == SpectrogramExportFormat.Jpg) args.AddRange(new[] { "-q:v", "2" });   // best JPG quality
            args.Add(output);
            return args;
        }

        /// <summary>Turns ffmpeg's log into a plain sentence.</summary>
        public static string PlainError(string log, int exitCode)
        {
            string l = log ?? "";
            if (l.Contains("matches no streams") || l.Contains("Cannot find a matching stream") || l.Contains("does not contain any stream"))
                return "This file has no sound track.";
            if (l.Contains("Invalid data found when processing input") || l.Contains("Error opening input"))
                return "ffmpeg couldn't read this file - it may be damaged or not really audio/video.";
            if (l.Contains("No such file or directory"))
                return "The file couldn't be found - it may have been moved or deleted.";
            if (l.Contains("Permission denied"))
                return "Windows didn't allow reading this file (or writing the picture).";
            if (l.Contains("Cannot allocate memory") || l.Contains("Out of memory"))
                return "Not enough memory for a picture this big - try a smaller size.";
            if (l.Contains("Stop frequency should be greater than start"))
                return "The lowest frequency must be below the highest.";
            string last = l.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).LastOrDefault(s => s.Length > 0) ?? "";
            return last.Length > 0 ? $"ffmpeg stopped with an error: {last}" : $"ffmpeg stopped with error code {exitCode}.";
        }

        // =================================================================
        //  Probe + generate
        // =================================================================

        /// <summary>
        /// Duration, sample rate and channels of the first audio track, or a
        /// plain reason: "no sound track" (a video without audio) is told apart
        /// from "couldn't read" (a damaged file). Throws when ffprobe is missing.
        /// </summary>
        public static async Task<(SpectrogramMediaInfo? Info, string? Error)> ProbeAsync(string path, CancellationToken token)
        {
            string exe = VideoReplayManager.FfprobeExe ?? throw new FileNotFoundException($"ffprobe wasn't found in {VideoReplayManager.FfmpegFolder}.");
            var (exit, stdout, stderr) = await RunAsync(exe, new[]
            {
                "-v", "error", "-select_streams", "a:0",
                "-show_entries", "stream=sample_rate,channels:format=duration",
                "-of", "default=noprint_wrappers=1", path,
            }, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            if (exit == TimedOutExitCode) return (null, "ffprobe couldn't read this file in time - it may be on a slow or sleeping drive.");
            if (exit != 0)
            {
                // ffprobe -v error often says only "<file>: Invalid argument" for a file it can't make sense of.
                string reason = PlainError(stderr, exit);
                return (null, reason.StartsWith("ffmpeg stopped", StringComparison.Ordinal)
                    ? "ffmpeg couldn't read this file - it may be damaged or not really audio/video."
                    : reason);
            }
            var info = ParseProbe(stdout);
            return info != null ? (info, null) : (null, "This file has no sound track.");
        }

        /// <summary>Parses ffprobe's key=value lines. No sample_rate line = no audio track.</summary>
        public static SpectrogramMediaInfo? ParseProbe(string output)
        {
            int rate = 0, channels = 0;
            TimeSpan? duration = null;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq], value = line[(eq + 1)..];
                switch (key)
                {
                    case "sample_rate": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rate); break;
                    case "channels": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out channels); break;
                    case "duration":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) && sec > 0 && double.IsFinite(sec))
                            duration = TimeSpan.FromSeconds(sec);
                        break;
                }
            }
            return rate > 0 ? new SpectrogramMediaInfo(duration, rate, channels) : null;
        }

        /// <summary>
        /// Makes one picture in <paramref name="workFolder"/> (a working copy -
        /// <see cref="SaveCopy"/> puts it where the user wants). Never throws
        /// for a bad file: the result carries a plain reason. Cancelling stops
        /// ffmpeg straight away.
        /// </summary>
        public static async Task<SpectrogramResult> GenerateAsync(string source, SpectrogramOptions options, string workFolder, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                return SpectrogramResult.Failed(source, "The file couldn't be found - it may have been moved or deleted.");
            string? ffmpeg = VideoReplayManager.FfmpegExe;
            if (ffmpeg == null || VideoReplayManager.FfprobeExe == null)
                return SpectrogramResult.Failed(source, $"ffmpeg wasn't found. Put ffmpeg.exe and ffprobe.exe in {VideoReplayManager.FfmpegFolder}.");

            string? output = null;
            try
            {
                var (info, probeError) = await ProbeAsync(source, token).ConfigureAwait(false);
                if (info == null) return SpectrogramResult.Failed(source, probeError ?? "This file has no sound track.");

                var (start, stop, note, rangeError) = FitRange(options, info.SampleRate);
                if (rangeError != null) return SpectrogramResult.Failed(source, rangeError, info);

                Directory.CreateDirectory(workFolder);
                output = Path.Combine(workFolder, $"{Guid.NewGuid():N}{ExtensionFor(options.OutputFormat)}");
                var (exit, _, stderr) = await RunAsync(ffmpeg, BuildArguments(source, output, options, start, stop), PerFileTimeout, token).ConfigureAwait(false);

                if (exit == TimedOutExitCode)
                {
                    TryDelete(output);
                    return SpectrogramResult.Failed(source, $"ffmpeg took longer than {PerFileTimeout.TotalMinutes:0} minutes and was stopped.", info);
                }
                if (exit != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    TryDelete(output);
                    return SpectrogramResult.Failed(source, PlainError(stderr, exit), info);
                }

                return new SpectrogramResult { Success = true, SourcePath = source, ImagePath = output, Info = info, RangeNote = note };
            }
            catch (OperationCanceledException)
            {
                if (output != null) TryDelete(output);
                return new SpectrogramResult { SourcePath = source, Cancelled = true, Error = "Stopped." };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                if (output != null) TryDelete(output);
                return SpectrogramResult.Failed(source, ex is UnauthorizedAccessException ? "Windows didn't allow this - check the file and the temp folder." : ex.Message);
            }
        }

        /// <summary>
        /// Makes pictures for every row that needs one, a few at a time. Rows
        /// update as they finish (status, duration, image), so results appear
        /// live and Stop keeps everything already made. One bad file never
        /// stops the rest. <paramref name="onFinished"/> runs on the caller's
        /// context for each success (the view uses it to auto-save).
        /// </summary>
        public static async Task<(int Done, int Failed, bool Cancelled)> RunBatchAsync(
            IReadOnlyList<SpectrogramItem> items, SpectrogramOptions options, string workFolder,
            IProgress<SpectrogramProgress>? progress, Func<SpectrogramItem, Task>? onFinished, CancellationToken token)
        {
            string key = SettingsKey(options);
            var todo = items.Where(i => NeedsWork(i, key)).ToList();
            int done = 0, failed = 0, finished = 0;
            var context = SynchronizationContext.Current;
            var gate = new SemaphoreSlim(Math.Max(1, MaxParallelism));

            void OnUi(Action action)
            {
                if (context == null) action();
                else context.Post(_ => action(), null);
            }

            foreach (var item in todo)
            {
                item.Status = SpectrogramStatus.Waiting;
                item.Message = "Waiting";
            }
            progress?.Report(new SpectrogramProgress(0, todo.Count, 0, ""));

            async Task Work(SpectrogramItem item)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    OnUi(() =>
                    {
                        item.Status = SpectrogramStatus.Working;
                        item.Message = "Making…";
                    });

                    var result = await GenerateAsync(item.FullPath, options, workFolder, token).ConfigureAwait(false);
                    if (result.Cancelled) return;

                    if (result.Success) Interlocked.Increment(ref done);
                    else Interlocked.Increment(ref failed);
                    int count = Interlocked.Increment(ref finished);

                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    OnUi(async () =>
                    {
                        try
                        {
                            string? old = item.ImagePath;
                            item.Result = result;
                            item.DurationText = result.Info?.DurationText ?? item.DurationText;
                            item.ImagePath = result.ImagePath;
                            item.Thumbnail = null;
                            item.ThumbnailRequested = false;
                            item.SavedPath = null;
                            item.MadeWith = result.Success ? key : null;
                            item.Status = result.Success ? SpectrogramStatus.Done : SpectrogramStatus.Failed;
                            item.Message = result.Success ? result.RangeNote ?? "Done" : result.Error ?? "Failed";
                            if (old != null && old != result.ImagePath) TryDelete(old);
                            if (result.Success && onFinished != null) await onFinished(item);
                            progress?.Report(new SpectrogramProgress(count, todo.Count, Volatile.Read(ref failed), item.FileName));
                        }
                        finally
                        {
                            tcs.TrySetResult();
                        }
                    });
                    await tcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }

            bool cancelled = false;
            try
            {
                await Task.WhenAll(todo.Select(Work)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            cancelled |= token.IsCancellationRequested;

            if (cancelled)
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                OnUi(() =>
                {
                    foreach (var item in todo.Where(i => i.Status is SpectrogramStatus.Waiting or SpectrogramStatus.Working))
                    {
                        item.Status = SpectrogramStatus.Cancelled;
                        item.Message = "Stopped";
                    }
                    tcs.TrySetResult();
                });
                await tcs.Task.ConfigureAwait(false);
            }
            return (done, failed, cancelled);
        }

        /// <summary>A row needs (re)making when it has no picture, failed, was stopped, or was made with other settings.</summary>
        public static bool NeedsWork(SpectrogramItem item, string settingsKey) =>
            item.Status != SpectrogramStatus.Done || item.ImagePath == null || item.MadeWith != settingsKey || !File.Exists(item.ImagePath);

        // =================================================================
        //  Process
        // =================================================================

        public const int TimedOutExitCode = -2;

        /// <summary>Runs a tool and collects its output. Cancelling or timing out kills it (and anything it started).</summary>
        public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken token)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(Trim(e.Data)); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
                token.ThrowIfCancellationRequested();
                return (TimedOutExitCode, "", "timed out");
            }
            process.WaitForExit();   // flush the async readers
            lock (stdout) lock (stderr) return (process.ExitCode, stdout.ToString(), stderr.ToString());

            static string Trim(string s) => s.Length > 2000 ? s[..2000] : s;
        }

        // =================================================================
        //  Files: scanning, naming, saving, the temp folder
        // =================================================================

        /// <summary>
        /// Every supported file under a folder, in natural order. Folder links
        /// (junctions, symlinks) are not followed - with a link back to a
        /// parent the old scan listed the same files dozens of times.
        /// Unreadable folders are counted.
        /// </summary>
        public static Task<(List<string> Files, int Inaccessible)> ScanFolderAsync(string folder, bool includeSubfolders, CancellationToken token) =>
            Task.Run(() =>
            {
                var files = new List<string>();
                int inaccessible = 0;
                var pending = new Stack<string>();
                pending.Push(folder);
                var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    string dir = pending.Pop();
                    try
                    {
                        var here = Directory.EnumerateFiles(dir, "*", options).Where(IsSupported).ToList();
                        here.Sort(VideoReplayManager.NaturalComparer.Instance);
                        files.AddRange(here);
                        if (!includeSubfolders) continue;
                        var subs = Directory.EnumerateDirectories(dir, "*", options).ToList();
                        subs.Sort(VideoReplayManager.NaturalComparer.Instance);
                        for (int i = subs.Count - 1; i >= 0; i--) pending.Push(subs[i]);   // reversed so they come off in order
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        inaccessible++;
                    }
                }
                return (files, inaccessible);
            }, token);

        /// <summary>Adds paths not already in the list (case-insensitive, O(1) each - the old check was O(n²)). Returns how many were added.</summary>
        public static int AddToList(ICollection<SpectrogramItem> list, HashSet<string> known, IEnumerable<string> paths)
        {
            var fresh = new List<SpectrogramItem>();
            foreach (string p in paths)
            {
                string full;
                try { full = Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (!IsSupported(full) || !known.Add(full)) continue;
                fresh.Add(new SpectrogramItem(full));
            }
            if (list is BulkObservableCollection<SpectrogramItem> bulk) bulk.AddRange(fresh);
            else foreach (var item in fresh) list.Add(item);
            return fresh.Count;
        }

        public static string DefaultFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Smart Tools", "Spectrograms");

        /// <summary>"<source name>.png" with characters Windows won't take replaced; a typed name wins when given.</summary>
        public static string OutputFileName(string sourcePath, string? typedName, SpectrogramExportFormat format)
        {
            string name = string.IsNullOrWhiteSpace(typedName) ? Path.GetFileNameWithoutExtension(sourcePath) : typedName.Trim();
            string ext = ExtensionFor(format);
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
            foreach (char c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }))
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0) name = "spectrogram";
            if (name.Length > 150) name = name[..150];
            return name + ext;
        }

        /// <summary>The path itself if free, else "name (2).ext", "name (3).ext"…</summary>
        public static string UniquePath(string path, ISet<string>? reserved = null)
        {
            bool Taken(string p) => File.Exists(p) || Directory.Exists(p) || (reserved?.Contains(p) ?? false);
            if (!Taken(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{name} ({n}){ext}");
                if (!Taken(candidate)) return candidate;
            }
        }

        private static readonly object SaveLock = new();

        /// <summary>
        /// Copies a working picture to <paramref name="destination"/> via a
        /// temp name. Without <paramref name="overwrite"/> (auto-save, batch
        /// export) a clash gets " (2)"; Save As passes true because the user
        /// already agreed to replace. Returns the path actually written.
        /// </summary>
        public static string SaveCopy(string workingImage, string destination, bool overwrite)
        {
            if (!File.Exists(workingImage)) throw new FileNotFoundException("The picture is no longer in the temp folder - make it again.");
            string folder = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
            File.Copy(workingImage, temp, overwrite: true);
            try
            {
                lock (SaveLock)
                {
                    string target = overwrite ? destination : UniquePath(destination);
                    File.Move(temp, target, overwrite);
                    return target;
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>Saves every finished row into one folder, named after its source, never replacing a file. Returns (saved, skipped).</summary>
        public static Task<(int Saved, int Failed)> ExportAsync(IReadOnlyList<SpectrogramItem> items, string folder, IProgress<SpectrogramProgress>? progress, CancellationToken token) =>
            Task.Run(() =>
            {
                int saved = 0, failed = 0, n = 0;
                var ready = items.Where(i => i.Status == SpectrogramStatus.Done && i.ImagePath != null).ToList();
                foreach (var item in ready)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string target = Path.Combine(folder, OutputFileName(item.FullPath, null, FormatOf(item.ImagePath!)));
                        item.SavedPath = SaveCopy(item.ImagePath!, target, overwrite: false);
                        saved++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                    }
                    progress?.Report(new SpectrogramProgress(++n, ready.Count, failed, item.FileName));
                }
                return (saved, failed);
            }, token);

        public static SpectrogramExportFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SpectrogramExportFormat.Jpg,
            ".bmp" => SpectrogramExportFormat.Bmp,
            _ => SpectrogramExportFormat.Png,
        };

        /// <summary>%TEMP%\Smart Tools\Spectrograms - each session gets its own folder inside.</summary>
        public static string WorkRoot => Path.Combine(Path.GetTempPath(), "Smart Tools", "Spectrograms");

        public static string NewSessionFolder() => Path.Combine(WorkRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);

        /// <summary>Deletes working folders older than <paramref name="olderThan"/> (left by a crash), never the current one.</summary>
        public static int CleanOldSessions(TimeSpan olderThan, string? keep = null, string? root = null)
        {
            int removed = 0;
            string dir = root ?? WorkRoot;
            if (!Directory.Exists(dir)) return 0;
            foreach (string session in Directory.EnumerateDirectories(dir))
            {
                if (keep != null && string.Equals(Path.GetFullPath(session), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(session) < olderThan) continue;
                    if (new DirectoryInfo(session).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    Directory.Delete(session, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // in use by another copy of the app - next time
                }
            }
            return removed;
        }

        public static void DeleteFolder(string? folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // =================================================================
        //  Words
        // =================================================================

        public static string DescribeBatch(int done, int failed, bool cancelled, int total)
        {
            var parts = new List<string> { $"Made {done:N0} of {total:N0} {SpectrogramText.Plural(total, "spectrogram", "spectrograms")}" };
            if (failed > 0) parts.Add($"{failed:N0} couldn't be made (hover the row for why)");
            string text = string.Join(" · ", parts) + ".";
            return cancelled ? "Stopped. " + text + " Finished ones are kept." : text;
        }

        public static string PlainSaveError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "Windows didn't allow saving there - pick another folder.",
            DirectoryNotFoundException => "That folder isn't there any more.",
            IOException io when io.HResult == unchecked((int)0x80070070) => "The disk is full.",
            _ => ex.Message,
        };

        // =================================================================
        //  Settings (%AppData%\Smart Tools\spectrogram.json - no paths)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "spectrogram.json");

        public static SpectrogramSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new SpectrogramSettings();
                var s = JsonSerializer.Deserialize<SpectrogramSettings>(File.ReadAllText(file), JsonOptions) ?? new SpectrogramSettings();
                s.WidthPx = Math.Clamp(s.WidthPx, MinWidth, MaxWidth);
                s.HeightPx = Math.Clamp(s.HeightPx, MinHeight, MaxHeight);
                if (s.MinFrequencyHz is < 0) s.MinFrequencyHz = null;
                if (s.MaxFrequencyHz is <= 0) s.MaxFrequencyHz = null;
                if (s.MinFrequencyHz is { } lo && s.MaxFrequencyHz is { } hi && lo >= hi) (s.MinFrequencyHz, s.MaxFrequencyHz) = (null, null);
                if (!Enum.IsDefined(s.ColorScheme)) s.ColorScheme = SpectrogramColorScheme.Intensity;
                if (!Enum.IsDefined(s.FrequencyScale)) s.FrequencyScale = SpectrogramFrequencyScale.Linear;
                if (!Enum.IsDefined(s.ChannelMode)) s.ChannelMode = SpectrogramChannelMode.Combined;
                if (!Enum.IsDefined(s.OutputFormat)) s.OutputFormat = SpectrogramExportFormat.Png;
                if (!Enum.IsDefined(s.Mode)) s.Mode = SpectrogramMode.Single;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new SpectrogramSettings();
            }
        }

        public static void SaveSettings(SpectrogramSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
            }
        }
    }
}
