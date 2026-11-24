using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Reads an image's header and EXIF into plain values (Magick.NET in MetadataExtractManager.Images.cs; a fake in tests).</summary>
    public interface IImageMetadataReader
    {
        ImageMetadataRaw Read(string path);
    }

    /// <summary>
    /// Metadata Extractor: what video, audio and image files say about
    /// themselves. Video and audio via ffprobe (JSON), images via an
    /// <see cref="IImageMetadataReader"/>. No WPF and no Magick.NET here, so it
    /// is tested against the real ffprobe in a plain test project.
    ///
    /// Fixed against the old MetadataExtracManager:
    ///  - Every video with a sound track failed: ffprobe writes sample_rate as
    ///    a string and the old GetInt called TryGetInt32 on it, which throws.
    ///  - ffprobe's output was read stdout-then-stderr (a full stderr pipe
    ///    deadlocks), the 15 s timeout came after the blocking read and never
    ///    killed anything, and Stop didn't stop running ffprobe processes.
    ///  - The folder scan threw out of the middle of the loop on an unreadable
    ///    folder or a folder link (unhandled - the app closed), followed links,
    ///    and ran on the UI thread; adding was O(n²) for big folders.
    ///  - Cover art in music/video files was reported as the video picture;
    ///    VFR phone video showed r_frame_rate's 90000 fps; 24 h+ durations lost
    ///    their days; GPS with a 0/0 fraction showed "NaN°".
    ///  - CSV export crashed when a file had two fields with the same name,
    ///    let cells like "=HYPERLINK(...)" run as Excel formulas, and XML
    ///    export crashed on control characters found in EXIF text. Exports
    ///    wrote straight over the target (half-written on failure).
    ///  - Binary EXIF (MakerNote) was dumped as thousands of numbers.
    ///  - Audio files weren't supported at all.
    /// </summary>
    public static partial class MetadataExtractManager
    {
        // =================================================================
        //  File types
        // =================================================================

        public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ts", ".m2ts", ".mts", ".vob",
        };

        public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".alac", ".ape", ".wv", ".mka", ".ac3", ".dsf",
        };

        public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2",
        };

        public static MetadataKind? KindOf(string path)
        {
            string ext = Path.GetExtension(path);
            if (VideoExtensions.Contains(ext)) return MetadataKind.Video;
            if (AudioExtensions.Contains(ext)) return MetadataKind.Audio;
            if (ImageExtensions.Contains(ext)) return MetadataKind.Image;
            return null;
        }

        public static string FileDialogFilter
        {
            get
            {
                static string Pattern(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*" + e));
                return $"Video, audio and images|{Pattern(VideoExtensions.Concat(AudioExtensions).Concat(ImageExtensions))}" +
                       $"|Video|{Pattern(VideoExtensions)}|Audio|{Pattern(AudioExtensions)}|Images|{Pattern(ImageExtensions)}|All files|*.*";
            }
        }

        /// <summary>Set by the view (Magick.NET) or by tests (a fake).</summary>
        public static IImageMetadataReader? ImageReader { get; set; }

        /// <summary>ffprobe is a process per file: more than this just thrashes.</summary>
        public static int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

        public static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

        // =================================================================
        //  Building the list (off the UI thread)
        // =================================================================

        /// <summary>
        /// Every supported file under a folder, natural order. Folder links are
        /// not followed and unreadable folders are counted, not fatal - the old
        /// lazy EnumerateFiles threw from inside the loop (outside its try) and
        /// closed the app.
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
                        var here = Directory.EnumerateFiles(dir, "*", options).Where(f => KindOf(f) != null).ToList();
                        here.Sort(VideoReplayManager.NaturalComparer.Instance);
                        files.AddRange(here);
                        if (!includeSubfolders) continue;
                        var subs = Directory.EnumerateDirectories(dir, "*", options).ToList();
                        subs.Sort(VideoReplayManager.NaturalComparer.Instance);
                        for (int i = subs.Count - 1; i >= 0; i--) pending.Push(subs[i]);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        inaccessible++;
                    }
                }
                return (files, inaccessible);
            }, token);

        /// <summary>Makes list rows for supported files that exist (size and date read off the UI thread).</summary>
        public static Task<List<MetadataFileItem>> CreateItemsAsync(IEnumerable<string> paths, CancellationToken token) =>
            Task.Run(() =>
            {
                var items = new List<MetadataFileItem>();
                foreach (string p in paths)
                {
                    token.ThrowIfCancellationRequested();
                    if (KindOf(p) is not { } kind) continue;
                    try
                    {
                        var info = new FileInfo(p);
                        if (!info.Exists) continue;
                        items.Add(new MetadataFileItem(info.FullName, kind, info.Length, info.LastWriteTime));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        // unreadable path - leave it out
                    }
                }
                return items;
            }, token);

        /// <summary>Adds rows not already listed (case-insensitive, O(1) each). Returns how many were added.</summary>
        public static int AddToList(ICollection<MetadataFileItem> list, HashSet<string> known, IEnumerable<MetadataFileItem> items)
        {
            var fresh = items.Where(i => known.Add(i.FullPath)).ToList();
            if (list is BulkObservableCollection<MetadataFileItem> bulk) bulk.AddRange(fresh);
            else foreach (var item in fresh) list.Add(item);
            return fresh.Count;
        }

        // =================================================================
        //  The run
        // =================================================================

        /// <summary>
        /// The rows a click on Extract works on: anything not yet read (or
        /// that failed, was stopped, or was skipped but is now included). When
        /// everything is already read, all of them (read again).
        /// </summary>
        public static List<MetadataFileItem> SelectForRun(IReadOnlyList<MetadataFileItem> items, MetadataExtractOptions options)
        {
            var pending = items.Where(i => i.Status != MetadataStatus.Done && (options.Includes(i.Kind) || i.Status != MetadataStatus.Skipped)).ToList();
            return pending.Count > 0 ? pending : items.ToList();
        }

        /// <summary>
        /// Reads every given row, a few at a time. Rows update as they finish
        /// (posted to the caller's context), so the list fills in live; Stop
        /// ends running ffprobe processes and keeps what's done. One bad file
        /// never stops the rest.
        /// </summary>
        public static async Task<(int Done, int Failed, int Skipped, bool Cancelled)> ExtractAsync(
            IReadOnlyList<MetadataFileItem> items, MetadataExtractOptions options, IProgress<MetadataProgress>? progress, CancellationToken token)
        {
            int done = 0, failed = 0, skipped = 0, finished = 0;
            int total = items.Count;
            var context = SynchronizationContext.Current;
            void OnUi(Action action)
            {
                if (context == null) action();
                else context.Post(_ => action(), null);
            }

            foreach (var item in items)
            {
                item.Status = MetadataStatus.Waiting;
                item.Message = "Waiting";
            }
            progress?.Report(new MetadataProgress(0, total, 0, ""));

            var gate = new SemaphoreSlim(Math.Max(1, MaxParallelism));
            async Task Work(MetadataFileItem item)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!options.Includes(item.Kind))
                    {
                        Interlocked.Increment(ref skipped);
                        int n0 = Interlocked.Increment(ref finished);
                        OnUi(() =>
                        {
                            item.Status = MetadataStatus.Skipped;
                            item.Message = $"Skipped - {item.Kind.ToString().ToLowerInvariant()} files are switched off";
                            progress?.Report(new MetadataProgress(n0, total, Volatile.Read(ref failed), item.Name));
                        });
                        return;
                    }

                    OnUi(() =>
                    {
                        item.Status = MetadataStatus.Working;
                        item.Message = "Reading…";
                    });

                    var result = await ReadAsync(item.FullPath, item.Kind, token).ConfigureAwait(false);
                    if (result.Success) Interlocked.Increment(ref done);
                    else Interlocked.Increment(ref failed);
                    int n = Interlocked.Increment(ref finished);

                    OnUi(() =>
                    {
                        item.Fields = result.Success ? result.Fields : null;
                        item.Summary = result.Summary;
                        item.Latitude = result.Latitude;
                        item.Longitude = result.Longitude;
                        item.Status = result.Success ? MetadataStatus.Done : MetadataStatus.Failed;
                        item.Message = result.Error ?? "Read";
                        progress?.Report(new MetadataProgress(n, total, Volatile.Read(ref failed), item.Name));
                    });
                }
                finally
                {
                    gate.Release();
                }
            }

            bool cancelled = false;
            try
            {
                await Task.WhenAll(items.Select(Work)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            cancelled |= token.IsCancellationRequested;

            var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OnUi(() =>
            {
                if (cancelled)
                {
                    foreach (var item in items.Where(i => i.Status is MetadataStatus.Waiting or MetadataStatus.Working))
                    {
                        item.Status = MetadataStatus.Cancelled;
                        item.Message = "Stopped";
                    }
                }
                flushed.TrySetResult();
            });
            await flushed.Task.ConfigureAwait(false);   // every posted row update has landed before the caller reads the rows
            return (done, failed, skipped, cancelled);
        }

        /// <summary>Reads one file. Never throws for a bad file (the result carries a plain reason); throws OperationCanceledException on Stop.</summary>
        public static async Task<MetadataReadResult> ReadAsync(string path, MetadataKind kind, CancellationToken token)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) return MetadataReadResult.Failed("The file isn't there any more - it may have been moved or deleted.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return MetadataReadResult.Failed("Windows didn't allow reading this file.");
            }

            if (kind == MetadataKind.Image)
            {
                if (ImageReader is not { } reader) return MetadataReadResult.Failed("The image reader isn't available.");
                try
                {
                    var raw = await Task.Run(() => reader.Read(path), token).ConfigureAwait(false);
                    return BuildImageFields(raw, info.FullName, info.Length, info.LastWriteTime);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return MetadataReadResult.Failed(PlainImageError(ex));
                }
            }

            var (json, error) = await ProbeJsonAsync(path, token).ConfigureAwait(false);
            if (error != null) return MetadataReadResult.Failed(error);
            try
            {
                return BuildMediaFields(json!, info.FullName, info.Length, info.LastWriteTime, kind);
            }
            catch (JsonException)
            {
                return MetadataReadResult.Failed("ffprobe's answer couldn't be understood - the file may be damaged.");
            }
        }

        public static string PlainImageError(Exception ex)
        {
            string m = ex.Message;
            if (ex is UnauthorizedAccessException || m.Contains("denied", StringComparison.OrdinalIgnoreCase)) return "Windows didn't allow reading this file.";
            if (m.Contains("no decode delegate", StringComparison.OrdinalIgnoreCase) || m.Contains("NoDecodeDelegate", StringComparison.OrdinalIgnoreCase))
                return "This image type can't be read on this PC.";
            if (m.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || m.Contains("improper", StringComparison.OrdinalIgnoreCase) || m.Contains("insufficient image data", StringComparison.OrdinalIgnoreCase))
                return "The image looks damaged - its header couldn't be read.";
            return "The image couldn't be read: " + m.Split('\n')[0].Trim();
        }

        // =================================================================
        //  ffprobe
        // =================================================================

        /// <summary>ffprobe's JSON for the file, or a plain reason it couldn't be read.</summary>
        public static async Task<(string? Json, string? Error)> ProbeJsonAsync(string path, CancellationToken token)
        {
            string? exe = VideoReplayManager.FfprobeExe;
            if (exe == null) return (null, $"ffprobe wasn't found. Put ffprobe.exe in {VideoReplayManager.FfmpegFolder}.");
            var (exit, stdout, stderr) = await RunAsync(exe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, ProbeTimeout, token).ConfigureAwait(false);
            if (exit == TimedOutExitCode) return (null, $"ffprobe took longer than {ProbeTimeout.TotalSeconds:0} seconds - the file may be on a slow or sleeping drive.");
            if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return (null, PlainProbeError(stderr));
            return (stdout, null);
        }

        public static string PlainProbeError(string stderr)
        {
            string s = stderr ?? "";
            if (s.Contains("Invalid data found") || s.Contains("Invalid argument") || s.Contains("moov atom not found"))
                return "ffprobe couldn't read this file - it may be damaged, incomplete, or not really this type.";
            if (s.Contains("No such file")) return "The file isn't there any more - it may have been moved or deleted.";
            if (s.Contains("Permission denied")) return "Windows didn't allow reading this file.";
            string last = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            return last.Length > 0 ? "ffprobe: " + last : "ffprobe couldn't read this file.";
        }

        public const int TimedOutExitCode = -2;

        /// <summary>Runs a tool, reading stdout and stderr at the same time (no pipe deadlock). Stop or the time limit kills it and anything it started.</summary>
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
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

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
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stdout, stderr.Length > 4000 ? stderr[..4000] : stderr);
        }

        // =================================================================
        //  Video and audio: ffprobe JSON → lines
        // =================================================================

        private static readonly HashSet<string> NoiseTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "major_brand", "minor_version", "compatible_brands", "handler_name", "vendor_id", "creation_time",
            "location", "location-eng", "com.apple.quicktime.location.iso6709", "duration", "language", "_statistics_tags",
            "_statistics_writing_app", "_statistics_writing_date_utc", "number_of_frames", "number_of_bytes", "bps",
        };

        private static readonly (string Key, string Label, string Group)[] FriendlyTags =
        {
            ("title", "Title", "Tags"),
            ("artist", "Artist", "Tags"),
            ("album_artist", "Album artist", "Tags"),
            ("album", "Album", "Tags"),
            ("track", "Track", "Tags"),
            ("disc", "Disc", "Tags"),
            ("date", "Year / date", "Tags"),
            ("year", "Year", "Tags"),
            ("genre", "Genre", "Tags"),
            ("composer", "Composer", "Tags"),
            ("comment", "Comment", "Tags"),
            ("description", "Description", "Tags"),
            ("synopsis", "Synopsis", "Tags"),
            ("show", "Show", "Tags"),
            ("episode_id", "Episode", "Tags"),
            ("copyright", "Copyright", "Tags"),
            ("encoder", "Made with", "General"),
            ("encoded_by", "Encoded by", "General"),
            ("com.apple.quicktime.make", "Make", "Camera"),
            ("com.apple.quicktime.model", "Model", "Camera"),
            ("com.apple.quicktime.software", "Software", "Camera"),
            ("com.android.manufacturer", "Make", "Camera"),
            ("com.android.model", "Model", "Camera"),
            ("com.android.version", "Android version", "Camera"),
        };

        /// <summary>Builds the lines for a video or audio file from ffprobe's JSON. Pure, so it's tested with real and made-up JSON.</summary>
        public static MetadataReadResult BuildMediaFields(string json, string fullPath, long size, DateTime modified, MetadataKind kind)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var rows = new List<MetadataField>();
            void Add(string group, string property, string? value)
            {
                string v = Clean(value);
                if (v.Length > 0) rows.Add(new MetadataField(group, property, v));
            }

            var format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            var tags = Tags(format);
            var streams = root.TryGetProperty("streams", out var s) && s.ValueKind == JsonValueKind.Array ? s.EnumerateArray().ToList() : new List<JsonElement>();

            // Cover art is a "video" stream marked attached_pic - it's not the video.
            bool IsCover(JsonElement st) => Num(Child(st, "disposition"), "attached_pic") == 1;
            var video = streams.FirstOrDefault(st => Str(st, "codec_type") == "video" && !IsCover(st));
            var cover = streams.FirstOrDefault(st => Str(st, "codec_type") == "video" && IsCover(st));
            var audios = streams.Where(st => Str(st, "codec_type") == "audio").ToList();
            var subs = streams.Where(st => Str(st, "codec_type") == "subtitle").ToList();
            bool hasVideo = video.ValueKind == JsonValueKind.Object;

            double duration = Num(format, "duration");
            if (duration <= 0 && hasVideo) duration = Num(video, "duration");
            if (duration <= 0 && audios.Count > 0) duration = Num(audios[0], "duration");

            // ---- General ----
            AddGeneral(rows, fullPath, size, modified, hasVideo ? "Video" : kind == MetadataKind.Image ? "Image" : "Audio");
            string container = Str(format, "format_long_name");
            Add("General", "Container", container.Length > 0 ? container : Str(format, "format_name"));
            Add("General", "Duration", MetadataText.Duration(duration));
            double overall = Num(format, "bit_rate");
            if (overall > 0) Add("General", "Overall bitrate", Bitrate(overall));
            if (tags.TryGetValue("creation_time", out string? created)) Add("General", "Created", FriendlyTime(created));

            var summary = new List<string>();

            // ---- Video ----
            if (hasVideo)
            {
                int width = (int)Num(video, "width"), height = (int)Num(video, "height");
                int rotation = Rotation(video);
                string codec = CodecName(Str(video, "codec_name"));
                string profile = Str(video, "profile");
                Add("Video", "Codec", profile.Length > 0 && !profile.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? $"{codec} · {profile}" : codec);
                if (width > 0 && height > 0)
                {
                    bool sideways = Math.Abs(rotation) % 180 == 90;
                    int shownW = sideways ? height : width, shownH = sideways ? width : height;
                    Add("Video", "Resolution", sideways ? $"{shownW} × {shownH} as shown ({width} × {height} stored)" : $"{width} × {height}{QualityName(width, height)}");
                    string dar = Str(video, "display_aspect_ratio");
                    Add("Video", "Aspect ratio", dar.Length > 0 && dar != "0:1" && !sideways ? dar : AspectRatio(shownW, shownH));
                    summary.Add($"{shownW} × {shownH}");
                }
                if (rotation != 0) Add("Video", "Rotation", $"Turned {Math.Abs(rotation)}° {(rotation > 0 ? "anticlockwise" : "clockwise")} - players show it upright");
                double fps = FrameRate(Str(video, "r_frame_rate"), Str(video, "avg_frame_rate"));
                if (fps > 0) Add("Video", "Frame rate", $"{fps.ToString(fps % 1 == 0 ? "0" : "0.###", CultureInfo.InvariantCulture)} fps");
                double vbr = Num(video, "bit_rate");
                if (vbr > 0) Add("Video", "Bitrate", Bitrate(vbr));
                int bits = (int)Num(video, "bits_per_raw_sample");
                string pix = Str(video, "pix_fmt");
                if (bits <= 0) bits = pix.Contains("12") ? 12 : pix.Contains("10") ? 10 : pix.Length > 0 ? 8 : 0;
                if (bits > 0) Add("Video", "Colour depth", $"{bits}-bit");
                Add("Video", "HDR", HdrName(Str(video, "color_transfer"), Str(video, "color_primaries")));
                summary.Add(codec);
            }

            // ---- Audio ----
            if (audios.Count > 0)
            {
                var a = audios[0];
                Add("Audio", "Codec", CodecName(Str(a, "codec_name")));
                Add("Audio", "Channels", ChannelsName((int)Num(a, "channels"), Str(a, "channel_layout")));
                double rate = Num(a, "sample_rate");   // a string in ffprobe's JSON - the old code's TryGetInt32 threw on it
                if (rate > 0) Add("Audio", "Sample rate", KiloHertz(rate));
                double abr = Num(a, "bit_rate");
                if (abr <= 0 && !hasVideo) abr = overall;
                if (abr > 0) Add("Audio", "Bitrate", Bitrate(abr));
                int abits = (int)Num(a, "bits_per_raw_sample");
                if (abits <= 0) abits = (int)Num(a, "bits_per_sample");
                if (abits > 0) Add("Audio", "Bit depth", $"{abits}-bit");
                if (audios.Count > 1) Add("Audio", "Audio tracks", $"{audios.Count} ({Languages(audios)})");
                else if (Language(a) is { } lang) Add("Audio", "Language", lang);
                if (!hasVideo)
                {
                    summary.Add(CodecName(Str(a, "codec_name")));
                    if (rate > 0) summary.Add(KiloHertz(rate));
                }
            }
            if (subs.Count > 0) Add(hasVideo ? "Video" : "Audio", "Subtitles", $"{subs.Count} ({Languages(subs)})");

            if (cover.ValueKind == JsonValueKind.Object)
            {
                int cw = (int)Num(cover, "width"), ch = (int)Num(cover, "height");
                Add("Tags", "Cover art", $"{cw} × {ch} {CodecName(Str(cover, "codec_name"))}");
            }

            // ---- Tags (container first, then the first audio stream's - Ogg/FLAC keep them there) ----
            var allTags = new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase);
            if (audios.Count > 0)
                foreach (var (k, v) in Tags(audios[0]))
                    allTags.TryAdd(k, v);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, label, group) in FriendlyTags)
            {
                if (!allTags.TryGetValue(key, out string? v) || string.IsNullOrWhiteSpace(v)) continue;
                if (rows.Any(r => r.Group == group && r.Property == label)) continue;   // e.g. "date" and "year" both present
                Add(group, label, v);
                used.Add(key);
            }

            // ---- Location ----
            double? lat = null, lon = null;
            string? iso = allTags.TryGetValue("com.apple.quicktime.location.iso6709", out var q) ? q
                        : allTags.TryGetValue("location", out var l1) ? l1
                        : allTags.TryGetValue("location-eng", out var l2) ? l2 : null;
            if (ParseIso6709(iso) is { } loc)
            {
                (lat, lon) = (loc.Lat, loc.Lon);
                Add("Location", "GPS coordinates", FormatLatLon(loc.Lat, loc.Lon));
                if (loc.Alt is { } alt && alt != 0) Add("Location", "Altitude", Altitude(alt));
            }

            foreach (var (key, value) in allTags)
            {
                if (used.Contains(key) || NoiseTags.Contains(key) || key.StartsWith("_statistics", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value)) continue;
                Add("Tags", key, value);
            }

            if (!hasVideo && allTags.TryGetValue("title", out string? title) && !string.IsNullOrWhiteSpace(title))
                summary.Insert(0, allTags.TryGetValue("artist", out string? artist) && !string.IsNullOrWhiteSpace(artist) ? $"{Clean(artist)} – {Clean(title)}" : Clean(title));
            if (duration > 0) summary.Insert(hasVideo ? 1 : summary.Count, MetadataText.Duration(duration));

            return new MetadataReadResult(Dedupe(rows), string.Join(" · ", summary.Where(x => x.Length > 0)), lat, lon);
        }

        private static void AddGeneral(List<MetadataField> rows, string fullPath, long size, DateTime modified, string type)
        {
            rows.Add(new MetadataField("General", "File name", Path.GetFileName(fullPath)));
            rows.Add(new MetadataField("General", "Folder", Path.GetDirectoryName(fullPath) ?? ""));
            rows.Add(new MetadataField("General", "Type", type));
            rows.Add(new MetadataField("General", "Size", $"{MetadataText.Bytes(size)} ({size:N0} bytes)"));
            if (modified > DateTime.MinValue) rows.Add(new MetadataField("General", "Modified", modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        // ---- JSON helpers (strings and numbers alike - ffprobe writes many numbers as strings) ----

        private static JsonElement Child(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) ? v : default;

        public static string Str(JsonElement el, string prop)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
        }

        public static double Num(JsonElement el, string prop) =>
            double.TryParse(Str(el, prop), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : 0;

        private static Dictionary<string, string> Tags(JsonElement el)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tags = Child(el, "tags");
            if (tags.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in tags.EnumerateObject())
                result.TryAdd(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
            return result;
        }

        private static string? Language(JsonElement stream)
        {
            var t = Tags(stream);
            return t.TryGetValue("language", out var l) && l.Length > 0 && !l.Equals("und", StringComparison.OrdinalIgnoreCase) ? l : null;
        }

        private static string Languages(IEnumerable<JsonElement> streams)
        {
            var langs = streams.Select(st => Language(st) ?? "unknown language").ToList();
            return string.Join(", ", langs);
        }

        /// <summary>Rotation from the display matrix (newer ffmpeg) or the old "rotate" tag. Positive = anticlockwise.</summary>
        public static int Rotation(JsonElement video)
        {
            var side = Child(video, "side_data_list");
            if (side.ValueKind == JsonValueKind.Array)
                foreach (var sd in side.EnumerateArray())
                    if (Num(sd, "rotation") is var r && r != 0) return (int)Math.Round(r);
            var tags = Tags(video);
            if (tags.TryGetValue("rotate", out var rot) && int.TryParse(rot, NumberStyles.Integer, CultureInfo.InvariantCulture, out int deg) && deg % 360 != 0)
                return -(deg % 360);   // "rotate" is clockwise
            return 0;
        }

        // =================================================================
        //  Wording (pure, tested)
        // =================================================================

        /// <summary>
        /// Frame rate: r_frame_rate when sensible, else the average. Phones
        /// record variable frame rate, where r_frame_rate can be the 90000 Hz
        /// timebase - the old code showed "90000 fps".
        /// </summary>
        public static double FrameRate(string rFrameRate, string avgFrameRate)
        {
            double r = Fraction(rFrameRate), avg = Fraction(avgFrameRate);
            double fps = r > 0 && r <= 240 ? r : avg > 0 && avg <= 240 ? avg : 0;
            return Math.Round(fps, 3);
        }

        public static double Fraction(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var parts = value.Split('/');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den))
                return den == 0 ? 0 : num / den;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double single) && double.IsFinite(single) ? single : 0;
        }

        public static string KiloHertz(double hz) => $"{hz / 1000:0.###} kHz";

        public static string Bitrate(double bps) =>
            bps <= 0 ? "" : bps >= 1_000_000 ? $"{bps / 1_000_000:0.##} Mbps" : $"{bps / 1000:0} kbps";

        public static string QualityName(int width, int height)
        {
            int shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
            string name = longSide >= 7680 ? "8K" : longSide >= 3840 ? "4K" : shortSide >= 1440 ? "1440p" : shortSide >= 1080 ? "Full HD" : shortSide >= 720 ? "HD" : shortSide >= 480 ? "SD" : "";
            return name.Length > 0 ? $" ({name})" : "";
        }

        public static string AspectRatio(int width, int height)
        {
            if (width <= 0 || height <= 0) return "";
            double ratio = (double)width / height;
            (double R, string Name)[] known = { (16.0 / 9, "16:9"), (9.0 / 16, "9:16"), (4.0 / 3, "4:3"), (3.0 / 4, "3:4"), (21.0 / 9, "21:9"), (1, "1:1"), (3.0 / 2, "3:2"), (2.0 / 3, "2:3") };
            foreach (var (r, name) in known)
                if (Math.Abs(ratio - r) < 0.02) return name;
            int a = width, b = height;
            while (b != 0) (a, b) = (b, a % b);
            int rw = width / a, rh = height / a;
            return rw <= 40 && rh <= 40 ? $"{rw}:{rh}" : $"{ratio.ToString("0.##", CultureInfo.InvariantCulture)}:1";
        }

        public static string CodecName(string codec) => codec.ToLowerInvariant() switch
        {
            "" => "",
            "h264" => "H.264 (AVC)",
            "hevc" => "H.265 (HEVC)",
            "av1" => "AV1",
            "vp8" => "VP8",
            "vp9" => "VP9",
            "mpeg4" => "MPEG-4 Part 2",
            "mpeg2video" => "MPEG-2",
            "prores" => "Apple ProRes",
            "mjpeg" => "JPEG",
            "png" => "PNG",
            "aac" => "AAC",
            "mp3" or "mp3float" => "MP3",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            "flac" => "FLAC",
            "alac" => "Apple Lossless (ALAC)",
            "ac3" => "Dolby Digital (AC-3)",
            "eac3" => "Dolby Digital Plus (E-AC-3)",
            "truehd" => "Dolby TrueHD",
            "dts" => "DTS",
            "wmav2" => "Windows Media Audio",
            "pcm_s16le" or "pcm_s16be" => "PCM 16-bit (uncompressed)",
            "pcm_s24le" or "pcm_s24be" => "PCM 24-bit (uncompressed)",
            "pcm_s32le" or "pcm_f32le" => "PCM 32-bit (uncompressed)",
            var other => other.ToUpperInvariant(),
        };

        public static string ChannelsName(int channels, string layout)
        {
            string l = layout.ToLowerInvariant();
            if (l.StartsWith("5.1")) return "5.1 surround";
            if (l.StartsWith("7.1")) return "7.1 surround";
            return channels switch
            {
                1 => "Mono",
                2 => "Stereo",
                6 => "5.1 surround",
                8 => "7.1 surround",
                > 0 => $"{channels} channels",
                _ => "",
            };
        }

        public static string HdrName(string transfer, string primaries) => transfer.ToLowerInvariant() switch
        {
            "smpte2084" => "HDR10 (PQ)",
            "arib-std-b67" => "HLG",
            _ => primaries.Equals("bt2020", StringComparison.OrdinalIgnoreCase) ? "Wide colour (BT.2020), not HDR" : "",
        };

        /// <summary>"2024-07-14T09:30:00.000000Z" → local "2024-07-14 11:30:00".</summary>
        public static string FriendlyTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                return t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return value.Trim();
        }

        /// <summary>EXIF dates are "2024:07:14 09:30:00" - shown as "2024-07-14 09:30:00".</summary>
        public static string ExifDate(string? value)
        {
            string v = Clean(value);
            return Regex.IsMatch(v, @"^\d{4}:\d{2}:\d{2}") ? v[..4] + "-" + v[5..7] + "-" + v[8..] : v;
        }

        private static readonly Regex Iso6709Pattern = new(@"^(?<lat>[+-]\d+(?:\.\d+)?)(?<lon>[+-]\d+(?:\.\d+)?)(?<alt>[+-]\d+(?:\.\d+)?)?(?:CRS[^/]*)?/?$", RegexOptions.Compiled);

        /// <summary>ISO 6709 ("+40.6892-074.0445+010.000/") as used by phone video. Null when missing, malformed or out of range.</summary>
        public static (double Lat, double Lon, double? Alt)? ParseIso6709(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = Iso6709Pattern.Match(raw.Trim());
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups["lat"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) return null;
            if (!double.TryParse(m.Groups["lon"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) return null;
            if (!ValidLatLon(lat, lon)) return null;
            double? alt = m.Groups["alt"].Success && double.TryParse(m.Groups["alt"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ? a : null;
            return (lat, lon, alt);
        }

        public static bool ValidLatLon(double lat, double lon) =>
            double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 && !(lat == 0 && lon == 0);

        public static string FormatLatLon(double lat, double lon) =>
            string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(lat):0.00000}° {(lat < 0 ? "S" : "N")}, {Math.Abs(lon):0.00000}° {(lon < 0 ? "W" : "E")}");

        public static string Altitude(double metres) =>
            string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(metres):0.#} m {(metres < 0 ? "below" : "above")} sea level");

        /// <summary>OpenStreetMap link for "Show on map".</summary>
        public static string MapUrl(double lat, double lon) =>
            string.Create(CultureInfo.InvariantCulture, $"https://www.openstreetmap.org/?mlat={lat:0.######}&mlon={lon:0.######}#map=16/{lat:0.######}/{lon:0.######}");

        /// <summary>Removes control characters (EXIF text often ends in NULs) and trims; very long values are cut.</summary>
        public static string Clean(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '\t' || c == '\n') sb.Append(' ');
                else if (!char.IsControl(c) && !(c >= '￾')) sb.Append(c);
            }
            string s = sb.ToString().Trim();
            return s.Length > 2000 ? s[..2000] + "…" : s;
        }

        /// <summary>Same property twice in a group (EXIF IFD0 + IFD1…) gets " (2)" - the old CSV export crashed on duplicates.</summary>
        public static List<MetadataField> Dedupe(IEnumerable<MetadataField> rows)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var result = new List<MetadataField>();
            foreach (var r in rows)
            {
                string key = r.Group + "\u0001" + r.Property;
                if (seen.TryGetValue(key, out int n))
                {
                    seen[key] = n + 1;
                    result.Add(r with { Property = $"{r.Property} ({n + 1})" });
                }
                else
                {
                    seen[key] = 1;
                    result.Add(r);
                }
            }
            return result;
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\metadata_extract.json - no paths)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "metadata_extract.json");

        public static MetadataSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new MetadataSettings();
                var s = JsonSerializer.Deserialize<MetadataSettings>(File.ReadAllText(file), SettingsJson) ?? new MetadataSettings();
                if (!Enum.IsDefined(s.ExportFormat)) s.ExportFormat = MetadataExportFormat.Json;
                if (!s.IncludeVideo && !s.IncludeAudio && !s.IncludeImages) s.IncludeVideo = s.IncludeAudio = s.IncludeImages = true;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new MetadataSettings();
            }
        }

        public static void SaveSettings(MetadataSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, SettingsJson);

        public static string DescribeRun(int done, int failed, int skipped, bool cancelled)
        {
            var parts = new List<string> { $"Read {done:N0} {MetadataText.Plural(done, "file", "files")}" };
            if (failed > 0) parts.Add($"{failed:N0} couldn't be read (hover the row for why)");
            if (skipped > 0) parts.Add($"{skipped:N0} skipped (type switched off)");
            string text = string.Join(" · ", parts) + ".";
            return cancelled ? "Stopped. " + text + " What was read is kept." : text;
        }
    }
}
