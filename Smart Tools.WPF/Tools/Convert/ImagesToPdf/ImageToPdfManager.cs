using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Builds one PDF page at a time. The real one (PdfSharp) is in
    /// ImageToPdfManager.Pdf.cs; tests use a fake, so page order, file
    /// splitting and naming are checked without PdfSharp.
    /// </summary>
    public interface IImagePdfBuilder : IDisposable
    {
        int ImagePages { get; }
        void AddTitlePage(string title, ImagePdfOptions options);
        void AddImagePage(ImagePdfPrepared image, ImagePdfOptions options, int pageNumber);
        void Save(string path);
    }

    /// <summary>
    /// Images to PDF backend, split in three:
    /// <list type="bullet">
    /// <item>ImageToPdfManager.cs (this file) - no side effects except the
    /// run itself: page layout maths, quality presets, the list, file names,
    /// splitting, settings, and the run (given a way to prepare images and
    /// build pages).</item>
    /// <item>ImageToPdfManager.Files.cs - finding images in folders.</item>
    /// <item>ImageToPdfManager.Pdf.cs - ImageMagick (preparing images),
    /// PdfSharp (pages) and WPF (sizes for the list).</item>
    /// </list>
    /// </summary>
    public static partial class ImageToPdfManager
    {
        // =================================================================
        //  Page layout (points: 72 per inch)
        // =================================================================

        /// <summary>Portrait size of each paper size.</summary>
        public static (double Width, double Height) PaperPoints(ImagePdfPageSize size) => size switch
        {
            ImagePdfPageSize.Letter => (612, 792),
            ImagePdfPageSize.Legal => (612, 1008),
            ImagePdfPageSize.A3 => (841.89, 1190.55),
            ImagePdfPageSize.A5 => (419.53, 595.28),
            _ => (595.28, 841.89),   // A4, and the title page when pages match their image
        };

        public static double MarginPoints(ImagePdfMargins margins) => margins switch
        {
            ImagePdfMargins.None => 0,
            ImagePdfMargins.Narrow => 36,
            ImagePdfMargins.Wide => 108,
            _ => 72,
        };

        /// <summary>JPEG quality and the longest side kept (0 = full size).</summary>
        public static (int JpegQuality, int MaxSide) QualityPreset(ImagePdfQuality quality) => quality switch
        {
            ImagePdfQuality.High => (95, 0),
            ImagePdfQuality.Small => (55, 2000),
            _ => (75, 3500),
        };

        /// <summary>Landscape page for this image? Auto follows the image's shape.</summary>
        public static bool UseLandscape(ImagePdfOrientation orientation, int imageWidth, int imageHeight) => orientation switch
        {
            ImagePdfOrientation.Landscape => true,
            ImagePdfOrientation.Auto => imageWidth > imageHeight,
            _ => false,
        };

        /// <summary>
        /// Print size of a length in pixels. A scan that says 96-2400 dpi
        /// prints at its real size; anything else (missing, or the 72 most
        /// cameras write without meaning it) counts as 96 dpi, as before.
        /// </summary>
        public static double PixelsToPoints(int pixels, double dpi) => pixels * 72.0 / SaneDpi(dpi);

        public static double SaneDpi(double dpi) => double.IsFinite(dpi) && dpi >= 96 && dpi <= 2400 ? dpi : 96;

        /// <summary>The largest page PDF readers handle (200 inches).</summary>
        public const double MaxPagePoints = 14400;

        /// <summary>Where the image goes on the page. Fit scales it to the space inside the margins; otherwise only too-big images shrink. Always centred.</summary>
        /// <paramref name="footer"/> keeps that much room at the bottom (page numbers) when the margin is smaller.
        public static (double X, double Y, double Width, double Height) PlaceImage(
            double pageWidth, double pageHeight, double margin, double imageWidth, double imageHeight, bool fit, double footer = 0)
        {
            double bottom = Math.Max(margin, footer);
            double cw = Math.Max(1, pageWidth - margin * 2), ch = Math.Max(1, pageHeight - margin - bottom);
            double w = Math.Max(0.01, imageWidth), h = Math.Max(0.01, imageHeight);
            if (fit || w > cw || h > ch)
            {
                double scale = Math.Min(cw / w, ch / h);
                w *= scale;
                h *= scale;
            }
            return (margin + (cw - w) / 2, margin + (ch - h) / 2, w, h);
        }

        /// <summary>Room kept for the page-number line.</summary>
        public const double FooterPoints = 30;

        /// <summary>Page size for "same as image": the image plus margins, shrunk to fit PDF limits.</summary>
        public static (double Width, double Height) MatchImagePage(double imageWidth, double imageHeight, double margin)
        {
            double w = Math.Max(1, imageWidth), h = Math.Max(1, imageHeight);
            double room = MaxPagePoints - margin * 2;
            double scale = Math.Min(1, Math.Min(room / w, room / h));
            return (w * scale + margin * 2, h * scale + margin * 2);
        }

        /// <summary>
        /// Splits a title into lines no wider than <paramref name="maxWidth"/>
        /// (a long title used to run off the page). Words longer than a line
        /// are broken.
        /// </summary>
        public static List<string> WrapText(string text, Func<string, double> measure, double maxWidth)
        {
            var lines = new List<string>();
            string line = "";
            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (measure(candidate) <= maxWidth)
                {
                    line = candidate;
                    continue;
                }
                if (line.Length > 0) lines.Add(line);
                line = word;
                while (measure(line) > maxWidth && line.Length > 1)
                {
                    int cut = line.Length - 1;
                    while (cut > 1 && measure(line[..cut]) > maxWidth) cut--;
                    lines.Add(line[..cut]);
                    line = line[cut..];
                }
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        // =================================================================
        //  Files and names
        // =================================================================

        public const string DefaultFileName = "Images.pdf";

        /// <summary>Documents\Smart Tools\PDFs, made on the first save (same layout as Convert: Library\Smart Tools\Tool).</summary>
        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smart Tools", "PDFs");

        /// <summary>Folder + name into one .pdf path; blanks use the defaults, characters Windows forbids become "_".</summary>
        public static string ResolveOutputPath(string? folder, string? fileName)
        {
            string dir = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder.Trim();
            string name = string.IsNullOrWhiteSpace(fileName) ? DefaultFileName : fileName.Trim();
            name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
            if (name.Length == 0 || name.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) name = DefaultFileName;
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            return Path.Combine(dir, name);
        }

        /// <summary>"Images.pdf" -> "Images (2).pdf" when taken.</summary>
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

        /// <summary>"Images.pdf", part 2 -> "Images_2.pdf".</summary>
        public static string PartPath(string path, int part) =>
            Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}_{part}{Path.GetExtension(path)}");

        /// <summary>Start a new file before this image would push the current one past the limit (never leave a file empty).</summary>
        public static bool ShouldStartNewFile(long bytesSoFar, long nextImageBytes, long limitBytes, int imagePagesInFile) =>
            limitBytes > 0 && imagePagesInFile > 0 && bytesSoFar + nextImageBytes > limitBytes;

        // =================================================================
        //  The list
        // =================================================================

        /// <summary>Adds files not already listed as one sidebar group (a set makes the check instant). Returns the new items.</summary>
        public static List<ImagePdfItem> AddToList(
            BulkObservableCollection<ImagePdfItem> list,
            ICollection<ImagePdfGroup> groups,
            IReadOnlyList<ImagePdfFile> files,
            string? folderPath,
            bool isFolder,
            bool includeSubfolders)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<ImagePdfItem>();
            foreach (var f in files)
            {
                if (!known.Add(f.FullPath)) continue;
                added.Add(new ImagePdfItem { FullPath = f.FullPath, SizeBytes = f.SizeBytes, Modified = f.Modified, Number = list.Count + added.Count + 1 });
            }
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new ImagePdfGroup
            {
                FolderPath = folderPath ?? (folders.Count == 1 ? folders[0] : null),
                IsFolder = isFolder,
                IncludeSubfolders = includeSubfolders,
            };
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
            }
            list.AddRange(added);
            groups.Add(group);
            return added;
        }

        /// <summary>Removes images from the list and their sidebar groups (empty groups go - the old sidebar kept stale counts).</summary>
        public static void RemoveFromList(BulkObservableCollection<ImagePdfItem> list, ICollection<ImagePdfGroup> groups, IReadOnlyCollection<ImagePdfItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<ImagePdfItem>(items);
            list.RemoveWhere(gone.Contains);
            foreach (var group in items.Select(i => i.Group).OfType<ImagePdfGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
            Renumber(list);
        }

        /// <summary>
        /// After "Include subfolders" changes: brings a folder group in line
        /// with a fresh scan. Images no longer found are taken off the list,
        /// new ones go at the end; the rest keep their place and their tick
        /// (the user may have put the pages in order already).
        /// </summary>
        public static (int Added, int Removed) ApplyRescan(
            BulkObservableCollection<ImagePdfItem> list,
            ICollection<ImagePdfGroup> groups,
            ImagePdfGroup group,
            IReadOnlyList<ImagePdfFile> files,
            bool includeSubfolders)
        {
            var found = new HashSet<string>(files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            if (gone.Count > 0)
            {
                var goneSet = new HashSet<ImagePdfItem>(gone);
                list.RemoveWhere(goneSet.Contains);
                group.Items.RemoveAll(goneSet.Contains);
            }

            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<ImagePdfItem>();
            foreach (var f in files)
            {
                if (!known.Add(f.FullPath)) continue;
                var item = new ImagePdfItem { FullPath = f.FullPath, SizeBytes = f.SizeBytes, Modified = f.Modified, Group = group };
                added.Add(item);
                group.Items.Add(item);
            }
            if (added.Count > 0) list.AddRange(added);

            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            Renumber(list);
            return (added.Count, gone.Count);
        }

        public static void Renumber(IList<ImagePdfItem> list)
        {
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        }

        /// <summary>
        /// Moves the chosen images one place up (-1) or down (+1) as a block,
        /// keeping their order; ones already at the edge stay. Returns true if
        /// anything moved. The old view had no way to change the page order.
        /// </summary>
        public static bool Move(IList<ImagePdfItem> list, IReadOnlyCollection<ImagePdfItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<ImagePdfItem>(chosen);
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

        /// <summary>Page order by name ("IMG_2" before "IMG_10") or by date taken/modified, oldest first.</summary>
        public static List<ImagePdfItem> Sorted(IEnumerable<ImagePdfItem> items, bool byDate) => byDate
            ? items.OrderBy(i => i.Modified).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList()
            : items.OrderBy(i => i.Folder, NaturalStringComparer.Instance).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();

        public static string DescribeList(IReadOnlyCollection<ImagePdfItem> items)
        {
            if (items.Count == 0) return "No images";
            int included = items.Count(i => i.IsIncluded);
            string text = $"{items.Count:N0} {ImagePdfText.Plural(items.Count, "image", "images")} · {ImagePdfText.FormatBytes(items.Sum(i => i.SizeBytes))}";
            return included == items.Count ? text : $"{text} · {included:N0} in the PDF";
        }

        public static string DescribeResult(ImagePdfResult r)
        {
            if (r.Cancelled) return "Cancelled - no PDF was saved.";
            if (r.Files.Count == 0) return r.Error ?? "No PDF was made.";
            var parts = new List<string>
            {
                r.Files.Count == 1
                    ? $"Saved {Path.GetFileName(r.Files[0])} · {r.Pages:N0} {ImagePdfText.Plural(r.Pages, "page", "pages")}"
                    : $"Saved {r.Files.Count} files ({Path.GetFileName(r.Files[0])} …) · {r.Pages:N0} pages",
                $"{r.Elapsed.TotalSeconds:0.0} s",
            };
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} {ImagePdfText.Plural(r.Failed, "image", "images")} couldn't be read");
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  The run
        // =================================================================

        /// <summary>
        /// Makes the PDF from the ticked images, in list order. Images are
        /// prepared a few at a time in parallel while pages are added in
        /// order. A new file starts before one would pass the size limit.
        /// Everything is written to temporary names and only renamed into
        /// place at the end, so a failure or Cancel never leaves half a PDF -
        /// and a run where every image failed writes nothing (it used to save
        /// a PDF holding just the title page and call it a success).
        /// </summary>
        public static Task<ImagePdfResult> RunAsync(
            IReadOnlyList<ImagePdfItem> items,
            ImagePdfOptions options,
            Func<string, ImagePdfOptions, ImagePdfPrepared> prepare,
            Func<IImagePdfBuilder> newBuilder,
            IProgress<ImagePdfProgress>? progress = null,
            CancellationToken token = default,
            int parallel = 0) =>
            Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                var result = new ImagePdfResult();
                var work = items.Where(i => i.IsIncluded).ToList();
                foreach (var item in work)
                {
                    item.Status = ImagePdfStatus.Ready;
                    item.Message = "";
                }
                if (work.Count == 0)
                {
                    result.Error = "Tick at least one image to put in the PDF.";
                    return result;
                }

                string finalPath = Path.GetFullPath(options.OutputPath);
                string dir = Path.GetDirectoryName(finalPath)!;
                Directory.CreateDirectory(dir);
                string runId = Guid.NewGuid().ToString("N")[..8];
                string TempPath(int part) => Path.Combine(dir, $".{Path.GetFileNameWithoutExtension(finalPath)}.creating-{runId}-{part}.tmp");

                long limit = options.SplitMegabytes > 0 ? options.SplitMegabytes * 1024L * 1024L : 0;
                var temps = new List<string>();
                IImagePdfBuilder? doc = null;
                long bytes = 0;
                int pageNumber = 0, processed = 0, next = 0;
                bool titleDone = false;
                int window = parallel > 0 ? parallel : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                var pending = new Queue<(ImagePdfItem Item, Task<ImagePdfPrepared> Task)>();

                void Fill()
                {
                    while (pending.Count < window && next < work.Count && !token.IsCancellationRequested)
                    {
                        var item = work[next++];
                        pending.Enqueue((item, Task.Run(() => prepare(item.FullPath, options))));
                    }
                }

                void Report(ImagePdfItem item) => progress?.Report(new ImagePdfProgress(++processed, work.Count, item.Name));

                void SaveCurrent()
                {
                    string temp = TempPath(temps.Count + 1);
                    doc!.Save(temp);
                    temps.Add(temp);
                    doc.Dispose();
                    doc = null;
                    bytes = 0;
                }

                try
                {
                    Fill();
                    while (pending.Count > 0 && !token.IsCancellationRequested)
                    {
                        var (item, task) = pending.Dequeue();
                        item.Status = ImagePdfStatus.Working;
                        ImagePdfPrepared prepared;
                        try
                        {
                            prepared = await task.ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            item.Status = ImagePdfStatus.Failed;
                            item.Message = PlainError(ex);
                            result.Failed++;
                            Report(item);
                            Fill();
                            continue;
                        }
                        Fill();

                        if (doc != null && ShouldStartNewFile(bytes, prepared.Jpeg.Length, limit, doc.ImagePages)) SaveCurrent();
                        if (doc == null)
                        {
                            doc = newBuilder();
                            if (options.AddTitlePage && !titleDone)
                            {
                                // One title for the whole set, even when it's split into several files.
                                doc.AddTitlePage(string.IsNullOrWhiteSpace(options.Title) ? "Images" : options.Title.Trim(), options);
                                titleDone = true;
                            }
                        }

                        try
                        {
                            doc.AddImagePage(prepared, options, pageNumber + 1);
                        }
                        catch (Exception ex)
                        {
                            item.Status = ImagePdfStatus.Failed;
                            item.Message = PlainError(ex);
                            result.Failed++;
                            Report(item);
                            continue;
                        }
                        pageNumber++;
                        bytes += prepared.Jpeg.Length;
                        item.Status = ImagePdfStatus.Added;
                        item.Message = temps.Count > 0 || limit > 0 ? $"Page {pageNumber} (file {temps.Count + 1})" : $"Page {pageNumber}";
                        result.Pages++;
                        Report(item);
                    }

                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        foreach (var item in work.Where(i => i.Status is ImagePdfStatus.Ready or ImagePdfStatus.Working or ImagePdfStatus.Added))
                        {
                            item.Status = ImagePdfStatus.Cancelled;
                            item.Message = "Cancelled - no PDF was saved.";
                        }
                        foreach (var (_, task) in pending) _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        return result;
                    }

                    if (doc != null && doc.ImagePages > 0) SaveCurrent();
                    if (temps.Count == 0)
                    {
                        result.Error = "None of the ticked images could be read, so no PDF was made. Hover a row's status for the reason.";
                        return result;
                    }

                    // Only now do the files get their real names.
                    if (temps.Count == 1)
                    {
                        string target = options.OverwriteAllowed ? finalPath : UniquePath(finalPath);
                        File.Move(temps[0], target, overwrite: options.OverwriteAllowed);
                        result.Files.Add(target);
                    }
                    else
                    {
                        for (int i = 0; i < temps.Count; i++)
                        {
                            string target = UniquePath(PartPath(finalPath, i + 1));
                            File.Move(temps[i], target);
                            result.Files.Add(target);
                        }
                    }
                    temps.Clear();
                    return result;
                }
                finally
                {
                    doc?.Dispose();
                    foreach (string temp in temps)
                    {
                        try
                        {
                            if (File.Exists(temp)) File.Delete(temp);
                        }
                        catch (Exception)
                        {
                            // best effort
                        }
                    }
                    result.Elapsed = clock.Elapsed;
                }
            });

        /// <summary>One line a person can act on, instead of an exception's first line.</summary>
        public static string PlainError(Exception ex) => ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => "The file is no longer there.",
            UnauthorizedAccessException => "Windows didn't allow the file to be read.",
            OutOfMemoryException => "The image is too large to process.",
            _ => ex.Message.Split('\n')[0].Trim(),
        };

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "image_to_pdf.json");

        public static readonly IReadOnlyList<int> SplitChoices = new[] { 0, 10, 20, 30 };

        public static ImagePdfSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ImagePdfSettings();
                var s = JsonSerializer.Deserialize<ImagePdfSettings>(File.ReadAllText(file), JsonOptions) ?? new ImagePdfSettings();
                if (!Enum.IsDefined(s.PageSize)) s.PageSize = ImagePdfPageSize.A4;
                if (!Enum.IsDefined(s.Orientation)) s.Orientation = ImagePdfOrientation.Portrait;
                if (!Enum.IsDefined(s.Margins)) s.Margins = ImagePdfMargins.Normal;
                if (!Enum.IsDefined(s.Quality)) s.Quality = ImagePdfQuality.Balanced;
                if (!SplitChoices.Contains(s.SplitMegabytes)) s.SplitMegabytes = 30;
                return s;
            }
            catch (Exception)
            {
                return new ImagePdfSettings();
            }
        }

        public static void SaveSettings(ImagePdfSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
