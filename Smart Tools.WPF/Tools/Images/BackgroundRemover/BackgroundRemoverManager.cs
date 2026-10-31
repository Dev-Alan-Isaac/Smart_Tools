using ImageMagick;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind BackgroundRemoverView: chroma-key background removal
    /// with Magick.NET. For each image: load (honouring EXIF rotation), key
    /// out the target colour within Tolerance, optionally flatten onto a
    /// colour or a replacement image, trim, and write in the chosen format.
    /// Also scans folders, renders the live before/after preview, and
    /// exports the run report. No WPF controls in here.
    ///
    /// Built for large batches (10,000+ files): bounded parallelism,
    /// per-file error isolation, incremental progress, unique output names.
    /// </summary>
    public sealed partial class BackgroundRemoverManager
    {
        // =================================================================
        //  Live preview
        // =================================================================

        /// <summary>
        /// Renders a downscaled before/after of one file with the current
        /// options - fast enough to re-run as sliders move. Throws on
        /// unreadable files (the view shows the message).
        /// </summary>
        public Task<BackgroundRemoverPreview> RenderPreviewAsync(
            string path, BackgroundRemoverOptions options, int maxSize, CancellationToken token) => Task.Run(() =>
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            int sourceWidth = (int)image.Width, sourceHeight = (int)image.Height;

            // Shrink first: keying a 24MP photo just to show a 700px preview is wasted work.
            image.Resize(new MagickGeometry((uint)maxSize, (uint)maxSize) { Greater = true });
            token.ThrowIfCancellationRequested();

            byte[] original = image.ToByteArray(MagickFormat.Png);

            using MagickImage? background = LoadReplacementBackground(options);
            MagickImage result = Render(image, options, background, backgroundLock: null);
            try
            {
                token.ThrowIfCancellationRequested();
                if (options.OutputFormat == BackgroundRemoverOutputFormat.Jpg)
                    FlattenOntoWhite(result); // show what JPG will really look like
                return new BackgroundRemoverPreview(original, result.ToByteArray(MagickFormat.Png), sourceWidth, sourceHeight);
            }
            finally
            {
                if (!ReferenceEquals(result, image)) result.Dispose();
            }
        }, token);

        // =================================================================
        //  Batch run
        // =================================================================

        public async Task<BackgroundRemoverResult> RunAsync(
            IReadOnlyList<BackgroundRemoverItem> items,
            BackgroundRemoverOptions options,
            IProgress<BackgroundRemoverProgress>? progress,
            CancellationToken cancellationToken)
        {
            int total = items.Count;
            int processed = 0, succeeded = 0, skipped = 0, failed = 0;
            var stopwatch = Stopwatch.StartNew();

            // Decide every output path up front so two sources with the same
            // name (e.g. from different subfolders into one output folder)
            // get "name (2)" instead of racing to write the same file.
            var outputPaths = AssignUniqueOutputPaths(items, options);

            // Replacement background: decode ONCE per run, not once per file.
            using MagickImage? background = LoadReplacementBackground(options);
            var backgroundLock = new object();

            // Each Magick.NET operation is already multi-threaded internally
            // (OpenMP), and each worker holds a full-size image plus copies in
            // memory - so fewer, fatter workers beat one-per-core here.
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6),
                CancellationToken = cancellationToken,
            };

            void Report(BackgroundRemoverItem item, BackgroundRemoverStatus status, string text, string output, int done) =>
                progress?.Report(new BackgroundRemoverProgress
                {
                    Item = item,
                    Status = status,
                    StatusText = text,
                    OutputPath = output,
                    Processed = done,
                    Total = total,
                    Succeeded = Volatile.Read(ref succeeded),
                    Skipped = Volatile.Read(ref skipped),
                    Failed = Volatile.Read(ref failed),
                });

            await Task.Run(() => Parallel.ForEach(items, parallel, item =>
            {
                Report(item, BackgroundRemoverStatus.Processing, "Processing…", "", Volatile.Read(ref processed));

                string outputPath = outputPaths[item];
                BackgroundRemoverStatus status;
                string text;

                try
                {
                    (status, text) = ProcessFile(item.FullPath, outputPath, options, background, backgroundLock);
                    if (status == BackgroundRemoverStatus.Skipped)
                        Interlocked.Increment(ref skipped);
                    else
                        Interlocked.Increment(ref succeeded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Corrupt image, unsupported codec, locked output... one bad
                    // file must never sink a 10,000-file batch.
                    status = BackgroundRemoverStatus.Failed;
                    text = $"Error: {ex.Message}";
                    Interlocked.Increment(ref failed);
                }

                int done = Interlocked.Increment(ref processed);
                Report(item, status, text, status == BackgroundRemoverStatus.Failed ? "" : outputPath, done);
            }), CancellationToken.None);

            stopwatch.Stop();
            return new BackgroundRemoverResult
            {
                SucceededCount = succeeded,
                SkippedCount = skipped,
                FailedCount = failed,
                Elapsed = stopwatch.Elapsed,
            };
        }

        // =================================================================
        //  Per-file pipeline
        // =================================================================

        private static (BackgroundRemoverStatus, string) ProcessFile(
            string sourcePath, string outputPath, BackgroundRemoverOptions options,
            MagickImage? background, object backgroundLock)
        {
            // Never replace the user's original - an empty suffix + same folder
            // + same format would otherwise resolve to the source path itself.
            if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                return (BackgroundRemoverStatus.Skipped, "Skipped (would overwrite source)");

            if (!options.OverwriteExisting && File.Exists(outputPath))
                return (BackgroundRemoverStatus.Skipped, "Skipped (exists)");

            using var image = new MagickImage(sourcePath);
            MagickImage result = Render(image, options, background, backgroundLock);
            try
            {
                ApplyOutputFormat(result, options);

                string? dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                result.Write(outputPath);
                return (BackgroundRemoverStatus.Removed, "Removed");
            }
            finally
            {
                if (!ReferenceEquals(result, image)) result.Dispose();
            }
        }

        /// <summary>
        /// Keys out the target colour and applies the chosen background, IN
        /// PLACE where possible (the old pipeline cloned the full-size image
        /// three times per file). Returns either <paramref name="image"/>
        /// itself or a new composite the caller must dispose.
        /// </summary>
        private static MagickImage Render(
            MagickImage image, BackgroundRemoverOptions options, MagickImage? background, object? backgroundLock)
        {
            // Phone photos store rotation in EXIF; without this they came out sideways.
            image.AutoOrient();

            image.Alpha(AlphaAction.Set);
            if (options.CleanEdges)
            {
                KeyOutColor(image, new MagickColor(options.TargetColorHex),
                    options.Tolerance / 100.0, options.EdgeSoftness / 100.0, options.EdgeChoke);
            }
            else
            {
                image.ColorFuzz = new Percentage(options.Tolerance);
                image.Transparent(new MagickColor(options.TargetColorHex));
            }

            MagickImage result = options.Mode switch
            {
                BackgroundRemoverMode.SolidColor => ComposeOntoColor(image, options.ReplacementColorHex),
                BackgroundRemoverMode.ImageReplacement => ComposeOntoImage(image, background, backgroundLock),
                _ => image,
            };

            if (options.TrimEdges)
            {
                result.Trim();
                // Trim keeps the old canvas offsets ("page"), which some viewers
                // honour and draw the image shifted - reset them (ImageMagick's +repage).
                result.ResetPage();
            }

            return result;
        }

        // =================================================================
        //  Soft colour key ("colour to alpha" + decontamination)
        // =================================================================

        private const int KeyBandRows = 256;

        /// <summary>
        /// Keys <paramref name="key"/> out of <paramref name="image"/> with a soft matte.
        /// Per pixel, <c>raw</c> is how far it is from the key (the exact mix fraction of
        /// foreground over key for black/white keys). Alpha ramps from 0 at
        /// <paramref name="tolerance"/> to 1 at tolerance + softness, and the key colour is
        /// un-mixed from the partly transparent pixels (C = K + (P - K) / alpha) so they
        /// do not leave a light/dark fringe on the new background.
        /// </summary>
        private static void KeyOutColor(MagickImage image, MagickColor key, double tolerance, double softness, double choke)
        {
            var pixels = image.GetPixels();
            if (pixels.Channels != 4)
            {
                image.ColorType = ColorType.TrueColorAlpha;
                pixels = image.GetPixels();
            }

            const double Max = 65535.0;
            double kr = key.R / Max, kg = key.G / Max, kb = key.B / Max;
            double lo = Math.Clamp(tolerance, 0, 0.98);
            double hi = Math.Min(1.0, lo + Math.Clamp(softness, 0, 1) * (1.0 - lo));

            int width = (int)image.Width, height = (int)image.Height;
            for (int y0 = 0; y0 < height; y0 += KeyBandRows)
            {
                int rows = Math.Min(KeyBandRows, height - y0);
                ushort[]? buf = pixels.GetArea(0, y0, (uint)width, (uint)rows);
                if (buf == null) continue;

                Parallel.For(0, rows, row =>
                {
                    int i = row * width * 4;
                    for (int x = 0; x < width; x++, i += 4)
                        KeyPixel(buf, i, kr, kg, kb, lo, hi);
                });

                pixels.SetArea(0, y0, (uint)width, (uint)rows, buf);
            }

            if (choke > 0.01)
            {
                // Pull the matte in by a pixel or two: kills the last ring of fringe.
                using var alpha = image.Separate(Channels.Alpha).First();
                alpha.Morphology(new MorphologySettings
                {
                    Method = MorphologyMethod.Erode,
                    Kernel = Kernel.Disk,
                    KernelArguments = Math.Min(choke, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                image.Composite(alpha, CompositeOperator.CopyAlpha);
            }
        }

        private static void KeyPixel(ushort[] buf, int i, double kr, double kg, double kb, double lo, double hi)
        {
            const double Max = 65535.0;
            double r = buf[i] / Max, g = buf[i + 1] / Max, b = buf[i + 2] / Max;

            double raw = Math.Max(KeyDistance(r, kr), Math.Max(KeyDistance(g, kg), KeyDistance(b, kb)));

            double a;
            if (hi - lo < 1e-6) a = raw > lo ? 1 : 0;
            else
            {
                double t = Math.Clamp((raw - lo) / (hi - lo), 0, 1);
                a = t * t * (3 - 2 * t);
            }

            if (a >= 1) return; // solid subject: leave exactly as it was

            if (a <= 0)
            {
                buf[i + 3] = 0;
                return;
            }

            double inv = 1.0 / a;
            buf[i] = (ushort)Math.Round(Math.Clamp(kr + (r - kr) * inv, 0, 1) * Max);
            buf[i + 1] = (ushort)Math.Round(Math.Clamp(kg + (g - kg) * inv, 0, 1) * Max);
            buf[i + 2] = (ushort)Math.Round(Math.Clamp(kb + (b - kb) * inv, 0, 1) * Max);
            buf[i + 3] = (ushort)Math.Round(a * (buf[i + 3] / Max) * Max);
        }

        /// <summary>How far channel value p is from key value k, as a fraction of the room available on that side.</summary>
        private static double KeyDistance(double p, double k) =>
            p > k ? (k >= 1 ? 0 : (p - k) / (1 - k))
                  : (k <= 0 ? 0 : (k - p) / k);

        private static MagickImage ComposeOntoColor(MagickImage source, string hex)
        {
            var canvas = new MagickImage(new MagickColor(hex), source.Width, source.Height);
            canvas.Composite(source, CompositeOperator.Over);
            return canvas;
        }

        /// <summary>
        /// Fills the frame with the replacement image, keeping ITS aspect ratio
        /// (scale to cover, then centre-crop) - the old version stretched it.
        /// </summary>
        private static MagickImage ComposeOntoImage(MagickImage source, MagickImage? background, object? backgroundLock)
        {
            if (background == null)
                throw new InvalidOperationException("Replacement background image not found.");

            MagickImage canvas;
            if (backgroundLock != null)
                lock (backgroundLock) canvas = (MagickImage)background.Clone();
            else
                canvas = (MagickImage)background.Clone();

            canvas.Resize(new MagickGeometry(source.Width, source.Height) { FillArea = true });
            canvas.Crop(source.Width, source.Height, Gravity.Center);
            canvas.ResetPage();
            canvas.Composite(source, CompositeOperator.Over);
            return canvas;
        }

        private static MagickImage? LoadReplacementBackground(BackgroundRemoverOptions options)
        {
            if (options.Mode != BackgroundRemoverMode.ImageReplacement)
                return null;

            if (string.IsNullOrWhiteSpace(options.ReplacementImagePath) || !File.Exists(options.ReplacementImagePath))
                throw new FileNotFoundException("Replacement background image not found.", options.ReplacementImagePath);

            var background = new MagickImage(options.ReplacementImagePath);
            background.AutoOrient();
            return background;
        }

        private static void ApplyOutputFormat(MagickImage image, BackgroundRemoverOptions options)
        {
            switch (options.OutputFormat)
            {
                case BackgroundRemoverOutputFormat.Jpg:
                    // JPG has no alpha - flatten onto white rather than let the
                    // codec default (often black) show through.
                    FlattenOntoWhite(image);
                    image.Format = MagickFormat.Jpg;
                    image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
                    break;

                case BackgroundRemoverOutputFormat.WebP:
                    image.Format = MagickFormat.WebP;
                    image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
                    break;

                default:
                    image.Format = MagickFormat.Png;
                    break;
            }
        }

        private static void FlattenOntoWhite(MagickImage image)
        {
            if (!image.HasAlpha) return;
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }

        // =================================================================
        //  Output naming
        // =================================================================

        public static string ExtensionFor(BackgroundRemoverOutputFormat format) => format switch
        {
            BackgroundRemoverOutputFormat.Jpg => ".jpg",
            BackgroundRemoverOutputFormat.WebP => ".webp",
            _ => ".png",
        };

        /// <summary>Pictures\Smart Tools\Background Remover - where results go until the user picks a folder (made on the first save).</summary>
        public static string DefaultFolder
        {
            get
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (string.IsNullOrEmpty(pictures)) pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(pictures, "Smart Tools", "Background Remover");
            }
        }

        /// <summary>e.g. "photo_nobg.png" - shown live under the suffix box.</summary>
        public static string ExampleOutputName(string sampleSourcePath, BackgroundRemoverOptions options) =>
            Path.GetFileName(BuildOutputPath(sampleSourcePath, options));

        private static string BuildOutputPath(string sourcePath, BackgroundRemoverOptions options)
        {
            string folder = string.IsNullOrWhiteSpace(options.OutputFolder)
                ? Path.GetDirectoryName(sourcePath) ?? ""
                : options.OutputFolder;

            string name = Path.GetFileNameWithoutExtension(sourcePath) + (options.NamingSuffix ?? "");
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return Path.Combine(folder, name + ExtensionFor(options.OutputFormat));
        }

        private static Dictionary<BackgroundRemoverItem, string> AssignUniqueOutputPaths(
            IEnumerable<BackgroundRemoverItem> items, BackgroundRemoverOptions options)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<BackgroundRemoverItem, string>();

            foreach (var item in items)
            {
                string path = BuildOutputPath(item.FullPath, options);
                if (!taken.Add(path))
                {
                    string dir = Path.GetDirectoryName(path) ?? "";
                    string stem = Path.GetFileNameWithoutExtension(path);
                    string ext = Path.GetExtension(path);
                    int n = 2;
                    do path = Path.Combine(dir, $"{stem} ({n++}){ext}");
                    while (!taken.Add(path));
                }
                map[item] = path;
            }

            return map;
        }

        // =================================================================
        //  Report export
        // =================================================================

        public void ExportReport(IEnumerable<BackgroundRemoverItem> items, string outputPath, bool asJson)
        {
            var rows = items.Select(i => new
            {
                OriginalFile = i.DisplayName,
                OriginalPath = i.FullPath,
                NewFile = string.IsNullOrEmpty(i.OutputPath) ? "" : Path.GetFileName(i.OutputPath),
                NewPath = i.OutputPath,
                Result = i.Status.ToString(),
                Details = i.StatusText,
            }).ToList();

            if (asJson)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("OriginalFile,OriginalPath,NewFile,NewPath,Result,Details");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[] { r.OriginalFile, r.OriginalPath, r.NewFile, r.NewPath, r.Result, r.Details }.Select(CsvEscape)));

            // BOM so Excel reads non-ASCII file names correctly.
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string CsvEscape(string? value)
        {
            value ??= "";
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

    }
}
