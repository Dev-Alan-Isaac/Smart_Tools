using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>An open PDF that pages can be copied from.</summary>
    public interface IPdfSource : IDisposable
    {
        int PageCount { get; }
    }

    /// <summary>A new PDF being put together.</summary>
    public interface IPdfWriter : IDisposable
    {
        int PageCount { get; }

        /// <summary>Copies the given pages (1-based, in that order); <paramref name="bookmark"/> marks the first of them.</summary>
        void AddPages(IPdfSource source, IReadOnlyList<int> pages, string? bookmark);

        void Save(string path);
    }

    /// <summary>
    /// The PDF library. The real one (PdfSharp + ImageMagick) is in
    /// PdfToolkitManager.Pdf.cs; tests use a fake, so merge order, splitting,
    /// naming, failures and cancelling are checked without PdfSharp.
    /// </summary>
    public interface IPdfEngine
    {
        IPdfSource Open(string path);
        IPdfWriter NewDocument();

        /// <summary>Writes a smaller copy of <paramref name="source"/> to <paramref name="target"/>.</summary>
        PdfCompressOutcome Compress(string source, string target, PdfCompressLevel level, CancellationToken token);
    }

    /// <summary>
    /// PDF Toolkit backend, split in three:
    /// <list type="bullet">
    /// <item>PdfToolkitManager.cs (this file) - page ranges, file names, the
    /// list, settings, and the runs (given an <see cref="IPdfEngine"/>).</item>
    /// <item>PdfToolkitManager.Files.cs - finding PDFs in folders.</item>
    /// <item>PdfToolkitManager.Pdf.cs - the PdfSharp engine, including
    /// re-compressing the photos inside a PDF with ImageMagick.</item>
    /// </list>
    /// </summary>
    public static partial class PdfToolkitManager
    {
        // =================================================================
        //  Page ranges
        // =================================================================

        private static readonly Regex RangeToken = new(@"^(\d*)(?:(-)(\d*))?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads "1-3, 5, 8-" (8- = to the end, -3 = 1 to 3). Commas,
        /// semicolons or spaces separate. Anything not understood is listed
        /// as a problem - the old parser silently dropped it, so "1-3,5-x"
        /// made fewer files without saying why.
        /// </summary>
        public static PdfRangeParse ParseRanges(string? text)
        {
            var result = new PdfRangeParse();
            if (string.IsNullOrWhiteSpace(text)) return result;

            string normal = text.Replace('–', '-').Replace('—', '-');
            normal = Regex.Replace(normal, @"\s*-\s*", "-");
            foreach (string token in Regex.Split(normal.Trim(), @"[,;\s]+"))
            {
                if (token.Length == 0) continue;
                var m = RangeToken.Match(token);
                if (!m.Success || (m.Groups[1].Length == 0 && m.Groups[3].Length == 0))
                {
                    result.Problems.Add($"\"{token}\" isn't a page or range");
                    continue;
                }
                bool isRange = m.Groups[2].Success;
                if (!TryPage(m.Groups[1].Value, 1, out int start) || !TryPage(m.Groups[3].Value, isRange ? 0 : start, out int end))
                {
                    result.Problems.Add($"\"{token}\" has a page number that's too big");
                    continue;
                }
                if (start == 0 || (end == 0 && !isRange))
                {
                    result.Problems.Add($"\"{token}\": pages start at 1");
                    continue;
                }
                if (isRange && m.Groups[3].Length > 0 && end == 0)
                {
                    result.Problems.Add($"\"{token}\": pages start at 1");
                    continue;
                }
                if (end != 0 && end < start)
                {
                    result.Problems.Add($"\"{token}\" goes backwards");
                    continue;
                }
                result.Ranges.Add(new PdfPageRange(start, end));
            }
            return result;
        }

        private static bool TryPage(string digits, int ifBlank, out int page)
        {
            if (digits.Length == 0)
            {
                page = ifBlank;
                return true;
            }
            return int.TryParse(digits, out page) && page < 1_000_000;
        }

        /// <summary>
        /// Fits ranges to a PDF: "8-" ends at the last page, a range running
        /// past the end is cut short, and one that starts past the end is
        /// reported (the old code dropped it without a word).
        /// </summary>
        public static (List<(int Start, int End)> Ranges, List<string> Outside) ResolveRanges(IEnumerable<PdfPageRange> ranges, int pageCount)
        {
            var fitted = new List<(int, int)>();
            var outside = new List<string>();
            foreach (var r in ranges)
            {
                if (r.Start > pageCount)
                {
                    outside.Add(r.ToString());
                    continue;
                }
                int end = r.ToEnd ? pageCount : Math.Min(r.End, pageCount);
                fitted.Add((r.Start, end));
            }
            return (fitted, outside);
        }

        /// <summary>
        /// The files one PDF splits into: a name part and the pages of each.
        /// Page numbers in names are padded ("page07") so files sort in page
        /// order everywhere.
        /// </summary>
        public static List<(string Label, int[] Pages)> SplitGroups(PdfSplitMode mode, int pageCount, int everyN, IReadOnlyList<(int Start, int End)> ranges, bool rangesInOneFile)
        {
            var groups = new List<(string, int[])>();
            if (pageCount <= 0) return groups;
            int width = pageCount.ToString().Length;
            string P(int n) => n.ToString().PadLeft(width, '0');
            string Label(int s, int e) => s == e ? $"page{P(s)}" : $"pages{P(s)}-{P(e)}";
            int[] Span(int s, int e) => Enumerable.Range(s, e - s + 1).ToArray();

            switch (mode)
            {
                case PdfSplitMode.EveryPage:
                    for (int p = 1; p <= pageCount; p++) groups.Add((Label(p, p), new[] { p }));
                    break;

                case PdfSplitMode.EveryNPages:
                    int n = Math.Max(1, everyN);
                    for (int s = 1; s <= pageCount; s += n)
                    {
                        int e = Math.Min(pageCount, s + n - 1);
                        groups.Add((Label(s, e), Span(s, e)));
                    }
                    break;

                default:
                    if (ranges.Count == 0) break;
                    if (rangesInOneFile)
                        groups.Add(("selected pages", ranges.SelectMany(r => Span(r.Start, r.End)).Distinct().ToArray()));   // a page typed twice goes in once
                    else
                        foreach (var (s, e) in ranges) groups.Add((Label(s, e), Span(s, e)));
                    break;
            }
            return groups;
        }

        public static string DescribeRanges(PdfRangeParse parse)
        {
            if (parse.Problems.Count > 0) return string.Join(" · ", parse.Problems);
            if (parse.Ranges.Count == 0) return "Type pages, e.g. 1-3, 5, 8-  (8- means 8 to the end)";
            return $"{parse.Ranges.Count} {PdfToolText.Plural(parse.Ranges.Count, "range", "ranges")}: {string.Join(", ", parse.Ranges)}";
        }

        // =================================================================
        //  Files and names
        // =================================================================

        public const string DefaultMergeName = "Merged.pdf";

        /// <summary>Documents\Smart Tools\PDFs - the same place Images to PDF saves.</summary>
        public static string DefaultFolder => ImageToPdfManager.DefaultFolder;

        /// <summary>Folder + name into one .pdf path; blanks use the defaults, characters Windows forbids become "_".</summary>
        public static string ResolveMergePath(string? folder, string? fileName)
        {
            string dir = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder.Trim();
            string name = SafeName(string.IsNullOrWhiteSpace(fileName) ? DefaultMergeName : fileName.Trim());
            if (name.Length == 0 || name.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) name = DefaultMergeName;
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            return Path.Combine(dir, name);
        }

        public static string SafeName(string name) =>
            new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');

        /// <summary>"Report.pdf" -> "Report (2).pdf" when taken (the old code started at " (1)", unlike every other tool).</summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        /// <summary>"Report.pdf" split into several files -> folder "Report - split" (" (2)" when taken), so 300 pages don't flood the output folder.</summary>
        public static string SplitFolder(string outputFolder, string sourcePath) =>
            UniqueFolder(Path.Combine(outputFolder, SafeName(Path.GetFileNameWithoutExtension(sourcePath)) + " - split"));

        public static string UniqueFolder(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int n = 2; ; n++)
            {
                string candidate = $"{path} ({n})";
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
        }

        public static string SplitFileName(string sourcePath, string label) =>
            $"{SafeName(Path.GetFileNameWithoutExtension(sourcePath))}_{label}.pdf";

        public static string CompressedPath(string outputFolder, string sourcePath) =>
            Path.Combine(outputFolder, SafeName(Path.GetFileNameWithoutExtension(sourcePath)) + "_compressed.pdf");

        public static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>JPEG quality and the longest side kept for pictures inside the PDF.</summary>
        public static (int JpegQuality, int MaxSide) CompressPreset(PdfCompressLevel level) => level switch
        {
            PdfCompressLevel.Light => (85, 3000),
            PdfCompressLevel.Strong => (55, 1500),
            _ => (72, 2200),
        };

        /// <summary>Only swap a picture (or keep a whole copy) when it saves at least this share.</summary>
        public static bool WorthKeeping(long before, long after, double minSaving) => after > 0 && after <= before * (1 - minSaving);

        public static string SizeChange(long before, long after)
        {
            double pct = before > 0 ? (after - before) * 100.0 / before : 0;
            return $"{PdfToolText.FormatBytes(before)} → {PdfToolText.FormatBytes(after)} ({pct:+0;−0}%)";
        }

        private static string TempPath(string dir, string stem, string runId, int n) =>
            Path.Combine(dir, $".{SafeName(stem)}.creating-{runId}-{n}.tmp");

        // =================================================================
        //  The list
        // =================================================================

        /// <summary>Adds PDFs not already listed (a set makes the check instant). Returns the new items.</summary>
        public static List<PdfToolItem> AddToList(IList<PdfToolItem> list, IEnumerable<PdfFileFound> files)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<PdfToolItem>();
            foreach (var f in files)
            {
                if (!known.Add(f.FullPath)) continue;
                var item = new PdfToolItem { FullPath = f.FullPath, SizeBytes = f.SizeBytes, Modified = f.Modified, Number = list.Count + 1 };
                list.Add(item);
                added.Add(item);
            }
            return added;
        }

        /// <summary>Adds PDFs not already listed as one Files card row (a folder, or loose files). Returns the new items.</summary>
        public static List<PdfToolItem> AddToList(
            IList<PdfToolItem> list,
            ICollection<PdfToolkitGroup> groups,
            IEnumerable<PdfFileFound> files,
            string? folderPath,
            bool isFolder,
            bool includeSubfolders)
        {
            var added = AddToList(list, files);
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new PdfToolkitGroup
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
            groups.Add(group);
            return added;
        }

        /// <summary>Takes PDFs off the list and their Files card rows (an emptied row goes too).</summary>
        public static void RemoveFromList(IList<PdfToolItem> list, ICollection<PdfToolkitGroup> groups, IReadOnlyCollection<PdfToolItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<PdfToolItem>(items);
            foreach (var item in items) list.Remove(item);
            foreach (var group in items.Select(i => i.Group).OfType<PdfToolkitGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
            Renumber(list);
        }

        /// <summary>
        /// After "Include subfolders" changes: brings a folder row in line
        /// with a fresh scan. PDFs no longer found come off the list, new ones
        /// go at the end; the rest keep their place (the merge order the user
        /// may have set), tick and result.
        /// </summary>
        public static (List<PdfToolItem> Added, List<PdfToolItem> Removed) ApplyRescan(
            IList<PdfToolItem> list,
            ICollection<PdfToolkitGroup> groups,
            PdfToolkitGroup group,
            IReadOnlyList<PdfFileFound> files,
            bool includeSubfolders)
        {
            var found = new HashSet<string>(files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            foreach (var item in gone)
            {
                list.Remove(item);
                group.Items.Remove(item);
            }

            var added = AddToList(list, files);
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
            }

            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            Renumber(list);
            return (added, gone);
        }

        public static void Renumber(IList<PdfToolItem> list)
        {
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        }

        /// <summary>Moves the chosen files one place up (-1) or down (+1) as a block, keeping their order. Returns true if anything moved.</summary>
        public static bool Move(IList<PdfToolItem> list, IReadOnlyCollection<PdfToolItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<PdfToolItem>(chosen);
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

        /// <summary>Name order ("Part 2" before "Part 10") or date order, oldest first.</summary>
        public static List<PdfToolItem> Sorted(IEnumerable<PdfToolItem> items, bool byDate) => byDate
            ? items.OrderBy(i => i.Modified).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList()
            : items.OrderBy(i => i.Name, NaturalStringComparer.Instance).ThenBy(i => i.Folder, NaturalStringComparer.Instance).ToList();

        public static string DescribeList(IReadOnlyCollection<PdfToolItem> items)
        {
            if (items.Count == 0) return "No PDFs";
            int included = items.Count(i => i.IsIncluded);
            string text = $"{items.Count:N0} {PdfToolText.Plural(items.Count, "PDF", "PDFs")} · {PdfToolText.FormatBytes(items.Sum(i => i.SizeBytes))}";
            return included == items.Count ? text : $"{text} · {included:N0} ticked";
        }

        public static string DescribeResult(PdfToolOperation op, PdfToolResult r)
        {
            if (r.Error != null) return r.Error;
            var parts = new List<string>();
            if (r.Cancelled)
            {
                parts.Add(op == PdfToolOperation.Merge || r.Succeeded == 0
                    ? "Cancelled - nothing was saved"
                    : $"Cancelled - {r.Succeeded:N0} {PdfToolText.Plural(r.Succeeded, "PDF was", "PDFs were")} already finished and kept");
            }
            else
            {
                parts.Add(op switch
                {
                    PdfToolOperation.Merge => r.Files.Count == 1
                        ? $"Saved {Path.GetFileName(r.Files[0])} · {r.Pages:N0} {PdfToolText.Plural(r.Pages, "page", "pages")} from {r.Succeeded:N0} {PdfToolText.Plural(r.Succeeded, "PDF", "PDFs")}"
                        : "No PDF was made",
                    PdfToolOperation.Split => $"Split {r.Succeeded:N0} {PdfToolText.Plural(r.Succeeded, "PDF", "PDFs")} into {r.Files.Count:N0} {PdfToolText.Plural(r.Files.Count, "file", "files")}",
                    _ => r.Succeeded > 0
                        ? $"Compressed {r.Succeeded:N0} {PdfToolText.Plural(r.Succeeded, "PDF", "PDFs")} · {SizeChange(r.BytesBefore, r.BytesAfter)}"
                        : "No PDF could be made smaller",
                });
                parts.Add($"{r.Elapsed.TotalSeconds:0.0} s");
            }
            if (r.Skipped > 0) parts.Add($"{r.Skipped:N0} already as small as {PdfToolText.Plural(r.Skipped, "it gets", "they get")}");
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed - hover the status for why");
            return string.Join(" · ", parts);
        }

        /// <summary>One line a person can act on, instead of a library's exception text.</summary>
        public static string PlainError(Exception ex)
        {
            string m = ex.Message;
            if (ex is FileNotFoundException or DirectoryNotFoundException) return "The file is no longer there.";
            if (ex is UnauthorizedAccessException) return "Windows didn't allow the file to be opened or saved.";
            if (ex is IOException && m.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                return "The file is open in another program - close it and try again.";
            if (ex is OutOfMemoryException) return "The PDF is too large to process.";
            if (m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("encrypt", StringComparison.OrdinalIgnoreCase) || m.Contains("protected", StringComparison.OrdinalIgnoreCase))
                return "The PDF is password-protected or locked against changes.";
            if (m.Contains("not a valid PDF", StringComparison.OrdinalIgnoreCase) || m.Contains("invalid PDF", StringComparison.OrdinalIgnoreCase) || m.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase))
                return "This doesn't look like a valid PDF (it may be damaged).";
            return m.Split('\n')[0].Trim();
        }

        // =================================================================
        //  Runs
        // =================================================================

        /// <summary>
        /// Runs the chosen operation on the ticked PDFs, in list order. Every
        /// file is written under a temporary name and renamed at the end, so
        /// Cancel or an error never leaves half a PDF (the old Merge wrote
        /// straight to the final name). Split and Compress finish one PDF at
        /// a time: on Cancel, PDFs already finished are kept and the one in
        /// progress leaves nothing behind.
        /// </summary>
        public static Task<PdfToolResult> RunAsync(
            IReadOnlyList<PdfToolItem> items,
            PdfToolOptions options,
            IPdfEngine engine,
            IProgress<PdfToolProgress>? progress = null,
            CancellationToken token = default) =>
            Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var result = new PdfToolResult();
                var work = items.Where(i => i.IsIncluded).ToList();
                foreach (var item in work)
                {
                    item.Status = PdfItemStatus.Ready;
                    item.Message = "";
                    item.Result = "";
                    item.Outputs.Clear();
                }
                try
                {
                    if (work.Count == 0)
                    {
                        result.Error = "Tick at least one PDF first.";
                        return result;
                    }
                    if (options.Operation == PdfToolOperation.Merge && work.Count < 2)
                    {
                        result.Error = "Tick at least two PDFs to merge.";
                        return result;
                    }
                    if (options.Operation == PdfToolOperation.Split && options.SplitMode == PdfSplitMode.Ranges)
                    {
                        var parse = ParseRanges(options.Ranges);
                        if (!parse.IsValid)
                        {
                            result.Error = parse.Problems.Count > 0 ? "Check the pages: " + string.Join(" · ", parse.Problems) : "Type the pages to keep, e.g. 1-3, 5, 8-";
                            return result;
                        }
                    }

                    Directory.CreateDirectory(options.OutputFolder);
                    switch (options.Operation)
                    {
                        case PdfToolOperation.Merge:
                            Merge(work, options, engine, result, progress, token);
                            break;
                        case PdfToolOperation.Split:
                            Split(work, options, engine, result, progress, token);
                            break;
                        default:
                            Compress(work, options, engine, result, progress, token);
                            break;
                    }

                    if (result.Cancelled)
                        foreach (var item in work.Where(i => i.Status is PdfItemStatus.Ready or PdfItemStatus.Working))
                        {
                            item.Status = PdfItemStatus.Cancelled;
                            item.Message = "Cancelled before this one was finished - nothing was saved for it.";
                        }
                    return result;
                }
                finally
                {
                    result.Elapsed = clock.Elapsed;
                }
            });

        private static void Report(IProgress<PdfToolProgress>? progress, int done, int total, string name) =>
            progress?.Report(new PdfToolProgress(done, total, name));

        private static void Fail(PdfToolItem item, PdfToolResult result, Exception ex)
        {
            item.Status = PdfItemStatus.Failed;
            item.Message = PlainError(ex);
            result.Failed++;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // best effort - a hidden .tmp left behind is harmless
            }
        }

        // ---------------- Merge ----------------

        private static void Merge(List<PdfToolItem> work, PdfToolOptions options, IPdfEngine engine, PdfToolResult result, IProgress<PdfToolProgress>? progress, CancellationToken token)
        {
            string target = Path.GetFullPath(ResolveMergePath(options.OutputFolder, options.MergeFileName));
            if (options.OverwriteAllowed && work.Any(i => SamePath(i.FullPath, target)))
            {
                result.Error = "The merged PDF can't replace one of the PDFs being merged - choose another name.";
                return;
            }

            string temp = TempPath(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target), Guid.NewGuid().ToString("N")[..8], 1);
            int done = 0;
            using var writer = engine.NewDocument();
            try
            {
                foreach (var item in work)
                {
                    if (token.IsCancellationRequested) break;
                    item.Status = PdfItemStatus.Working;
                    try
                    {
                        using var source = engine.Open(item.FullPath);
                        item.PageCount = source.PageCount;
                        if (source.PageCount == 0) throw new InvalidDataException("This PDF has no pages.");
                        int first = writer.PageCount + 1;
                        string? bookmark = options.AddBookmarks ? Path.GetFileNameWithoutExtension(item.FullPath) : null;
                        writer.AddPages(source, Enumerable.Range(1, source.PageCount).ToArray(), bookmark);
                        item.Status = PdfItemStatus.Done;
                        item.Result = writer.PageCount == first ? $"Page {first:N0}" : $"Pages {first:N0}-{writer.PageCount:N0}";
                        item.Message = $"{item.Result} of the merged PDF";
                        result.Succeeded++;
                    }
                    catch (Exception ex)
                    {
                        Fail(item, result, ex);
                    }
                    Report(progress, ++done, work.Count, item.Name);
                }

                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    foreach (var item in work.Where(i => i.Status == PdfItemStatus.Done))
                    {
                        item.Status = PdfItemStatus.Cancelled;
                        item.Message = "Cancelled - the merged PDF wasn't saved.";
                        item.Result = "";
                    }
                    result.Succeeded = 0;
                    return;
                }
                if (writer.PageCount == 0)
                {
                    result.Error = "None of the ticked PDFs could be read, so nothing was saved. Hover a row's status for the reason.";
                    return;
                }

                // Read before saving: PdfSharp throws "The document was already saved and cannot be
                // modified anymore" when PageCount is asked after Save (the merged file was written, but the run failed).
                int mergedPages = writer.PageCount;
                writer.Save(temp);
                string final = options.OverwriteAllowed ? target : UniquePath(target);
                File.Move(temp, final, overwrite: options.OverwriteAllowed);
                result.Files.Add(final);
                result.Pages = mergedPages;
                foreach (var item in work.Where(i => i.Status == PdfItemStatus.Done)) item.Outputs.Add(final);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        // ---------------- Split ----------------

        private static void Split(List<PdfToolItem> work, PdfToolOptions options, IPdfEngine engine, PdfToolResult result, IProgress<PdfToolProgress>? progress, CancellationToken token)
        {
            var typed = options.SplitMode == PdfSplitMode.Ranges ? ParseRanges(options.Ranges).Ranges : new List<PdfPageRange>();
            string runId = Guid.NewGuid().ToString("N")[..8];
            int done = 0;

            foreach (var item in work)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    return;
                }
                item.Status = PdfItemStatus.Working;
                var temps = new List<(string Temp, string Name)>();
                try
                {
                    using var source = engine.Open(item.FullPath);
                    int pages = source.PageCount;
                    item.PageCount = pages;
                    if (pages == 0) throw new InvalidDataException("This PDF has no pages.");

                    var (ranges, outside) = ResolveRanges(typed, pages);
                    var groups = SplitGroups(options.SplitMode, pages, options.EveryNPages, ranges, options.RangesInOneFile);
                    string note = outside.Count > 0
                        ? $"Not in this {pages:N0}-page PDF: {string.Join(", ", outside)}."
                        : "";
                    if (groups.Count == 0)
                    {
                        item.Status = PdfItemStatus.Skipped;
                        item.Message = note.Length > 0 ? note + " Nothing to split." : "Nothing to split.";
                        result.Skipped++;
                        Report(progress, ++done, work.Count, item.Name);
                        continue;
                    }

                    for (int g = 0; g < groups.Count; g++)
                    {
                        if (token.IsCancellationRequested) break;
                        using var writer = engine.NewDocument();
                        writer.AddPages(source, groups[g].Pages, null);
                        string temp = TempPath(options.OutputFolder, Path.GetFileNameWithoutExtension(item.FullPath), runId, result.Files.Count + temps.Count + 1);
                        temps.Add((temp, SplitFileName(item.FullPath, groups[g].Label)));
                        writer.Save(temp);
                        if (groups.Count > 8 && (g + 1) % 4 == 0) Report(progress, done, work.Count, $"{item.Name} · {g + 1:N0} of {groups.Count:N0}");
                    }
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        return;   // finally removes this PDF's unfinished files
                    }

                    // One file goes next to the others; several get their own folder.
                    string folder = temps.Count == 1 ? options.OutputFolder : SplitFolder(options.OutputFolder, item.FullPath);
                    Directory.CreateDirectory(folder);
                    foreach (var (temp, name) in temps)
                    {
                        string final = UniquePath(Path.Combine(folder, name));
                        File.Move(temp, final);
                        item.Outputs.Add(final);
                        result.Files.Add(final);
                    }
                    temps.Clear();

                    item.Status = PdfItemStatus.Done;
                    item.Result = $"{item.Outputs.Count:N0} {PdfToolText.Plural(item.Outputs.Count, "file", "files")}";
                    item.Message = (item.Outputs.Count > 1 ? $"Saved in the folder \"{Path.GetFileName(folder)}\"" : $"Saved as {Path.GetFileName(item.Outputs[0])}")
                                   + (note.Length > 0 ? ". " + note : "");
                    result.Succeeded++;
                    result.Pages += groups.Sum(gr => gr.Pages.Length);
                }
                catch (Exception ex)
                {
                    Fail(item, result, ex);
                }
                finally
                {
                    foreach (var (temp, _) in temps) TryDelete(temp);
                }
                Report(progress, ++done, work.Count, item.Name);
            }
        }

        // ---------------- Compress ----------------

        private static void Compress(List<PdfToolItem> work, PdfToolOptions options, IPdfEngine engine, PdfToolResult result, IProgress<PdfToolProgress>? progress, CancellationToken token)
        {
            string runId = Guid.NewGuid().ToString("N")[..8];
            int done = 0;
            foreach (var item in work)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    return;
                }
                item.Status = PdfItemStatus.Working;
                string temp = TempPath(options.OutputFolder, Path.GetFileNameWithoutExtension(item.FullPath), runId, done + 1);
                try
                {
                    long before = new FileInfo(item.FullPath).Length;
                    int pages;
                    using (var source = engine.Open(item.FullPath)) pages = source.PageCount;
                    item.PageCount = pages;

                    var outcome = engine.Compress(item.FullPath, temp, options.CompressLevel, token);
                    long after = new FileInfo(temp).Length;

                    // Check the copy before trusting it: it must open and have every page.
                    int copyPages;
                    using (var check = engine.Open(temp)) copyPages = check.PageCount;
                    if (copyPages != pages) throw new InvalidDataException($"The smaller copy had {copyPages:N0} of {pages:N0} pages, so it wasn't kept. The original is untouched.");

                    if (!WorthKeeping(before, after, 0.02))
                    {
                        item.Status = PdfItemStatus.Skipped;
                        item.Result = "No saving";
                        item.Message = outcome.ImagesRecompressed > 0
                            ? "A copy wouldn't be smaller, so none was saved."
                            : outcome.ImagesKept == 0
                                ? "No pictures found in this PDF (it is text and shapes only), so there is nothing to shrink. No copy was saved."
                                : $"{outcome.ImagesKept:N0} {PdfToolText.Plural(outcome.ImagesKept, "picture was", "pictures were")} found but none could be made smaller (already tight, or a kind that is left alone). Try Strong. No copy was saved.";
                        result.Skipped++;
                    }
                    else
                    {
                        string final = UniquePath(CompressedPath(options.OutputFolder, item.FullPath));
                        File.Move(temp, final);
                        item.Outputs.Add(final);
                        result.Files.Add(final);
                        item.Status = PdfItemStatus.Done;
                        item.Result = SizeChange(before, after);
                        item.Message = $"Saved as {Path.GetFileName(final)} · {outcome.ImagesRecompressed:N0} {PdfToolText.Plural(outcome.ImagesRecompressed, "picture", "pictures")} made smaller";
                        result.Succeeded++;
                        result.BytesBefore += before;
                        result.BytesAfter += after;
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = true;
                    return;
                }
                catch (Exception ex)
                {
                    Fail(item, result, ex);
                }
                finally
                {
                    TryDelete(temp);
                }
                Report(progress, ++done, work.Count, item.Name);
            }
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "pdf_toolkit.json");

        public static PdfToolSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new PdfToolSettings();
                var s = JsonSerializer.Deserialize<PdfToolSettings>(File.ReadAllText(file), JsonOptions) ?? new PdfToolSettings();
                if (!Enum.IsDefined(s.Operation)) s.Operation = PdfToolOperation.Merge;
                if (!Enum.IsDefined(s.SplitMode)) s.SplitMode = PdfSplitMode.EveryPage;
                if (!Enum.IsDefined(s.CompressLevel)) s.CompressLevel = PdfCompressLevel.Balanced;
                s.EveryNPages = Math.Clamp(s.EveryNPages, 1, 9999);
                return s;
            }
            catch (Exception)
            {
                return new PdfToolSettings();
            }
        }

        public static void SaveSettings(PdfToolSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
