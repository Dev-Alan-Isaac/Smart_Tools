using Microsoft.VisualBasic.FileIO;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SearchOption = System.IO.SearchOption;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Engine behind SpaceAnalyzerView: scans a folder or drive into an
    /// in-memory tree (one pass - drilling down never touches disk again),
    /// builds the rows for a folder, lays out a squarified treemap, breaks
    /// space down by file type, deletes (Recycle Bin by default) and
    /// remembers the last settings.
    ///
    /// Scan rules: junctions / symbolic links are NOT followed (they made
    /// folders count twice, or loop); OneDrive online-only files are listed
    /// but don't count as space used on this PC; unreadable folders are
    /// counted so the view can say the total is incomplete.
    ///
    /// No WPF types - compiles and tests headlessly.
    /// </summary>
    public sealed class SpaceAnalyzerManager
    {
        private const long ReportIntervalMs = 120;

        private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi", "$WinREAgent", "$SysReset",
        };

        // OneDrive / cloud placeholder attributes (not all are in the FileAttributes enum).
        private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
        private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

        // =================================================================
        //  Scan
        // =================================================================

        private sealed class ScanState
        {
            public long Items;
            public long Bytes;
            public int Inaccessible;
            public int Links;
            public long LastReport;
            public required SemaphoreSlim Throttle;
            public required SpaceScanOptions Options;
            public IProgress<SpaceScanProgress>? Progress;
            public CancellationToken Token;
        }

        public async Task<SpaceScanResult> ScanAsync(SpaceScanOptions options, IProgress<SpaceScanProgress>? progress = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(options.RootPath) || !Directory.Exists(options.RootPath))
                return new SpaceScanResult { ErrorMessage = "That folder doesn't exist or isn't accessible." };

            var watch = Stopwatch.StartNew();
            using var throttle = new SemaphoreSlim(Math.Max(1, options.MaxDegreeOfParallelism));
            var state = new ScanState { Throttle = throttle, Options = options, Progress = progress, Token = token };

            string rootPath = Path.GetFullPath(options.RootPath);
            var rootInfo = new DirectoryInfo(rootPath);

            var root = new SpaceNode(rootPath, SpaceItemKind.Folder) { Modified = SafeLastWrite(rootInfo) };

            try
            {
                await Task.Run(() => FillFolderAsync(root, rootInfo, state), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new SpaceScanResult { ErrorMessage = ex.Message };
            }

            progress?.Report(new SpaceScanProgress(state.Items, state.Bytes, options.ExpectedBytes, rootPath));

            return new SpaceScanResult
            {
                Root = root,
                TotalItemsScanned = state.Items,
                InaccessibleFolders = state.Inaccessible,
                SkippedLinks = state.Links,
                Elapsed = watch.Elapsed,
            };
        }

        /// <summary>
        /// Lists one folder (holding a throttle slot only for the listing -
        /// holding it across the children's awaits could deadlock), then
        /// recurses into subfolders in parallel and totals up.
        /// </summary>
        private static async Task FillFolderAsync(SpaceNode node, DirectoryInfo dir, ScanState s)
        {
            s.Token.ThrowIfCancellationRequested();

            var subDirs = new List<(SpaceNode Node, DirectoryInfo Info)>();

            await s.Throttle.WaitAsync(s.Token).ConfigureAwait(false);
            try
            {
                IEnumerable<FileSystemInfo> entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false,
                });

                foreach (var entry in entries)
                {
                    s.Token.ThrowIfCancellationRequested();
                    FileAttributes attrs;
                    try { attrs = entry.Attributes; }
                    catch (IOException) { continue; }

                    if (!IsIncluded(attrs, s.Options)) continue;

                    if (entry is DirectoryInfo sub)
                    {
                        if (!s.Options.IncludeSubfolders) continue;
                        if (s.Options.IgnoreSystemFiles && SystemFolderNames.Contains(sub.Name)) continue;
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            Interlocked.Increment(ref s.Links); // junction / symlink: don't walk into it
                            continue;
                        }

                        var child = new SpaceNode(sub.Name, SpaceItemKind.Folder) { Modified = SafeLastWrite(sub), Parent = node };
                        node.Children.Add(child);
                        subDirs.Add((child, sub));
                    }
                    else if (entry is FileInfo file)
                    {
                        if (attrs.HasFlag(FileAttributes.ReparsePoint) && !IsCloudPlaceholder(attrs))
                        {
                            Interlocked.Increment(ref s.Links); // file symlink: its target is counted where it lives
                            continue;
                        }

                        long length;
                        try { length = file.Length; }
                        catch (IOException) { length = 0; }

                        bool cloudOnly = IsCloudPlaceholder(attrs);
                        node.Children.Add(new SpaceNode(file.Name, SpaceItemKind.File)
                        {
                            Modified = file.LastWriteTime,
                            SizeBytes = cloudOnly ? 0 : length,
                            CloudBytes = cloudOnly ? length : 0,
                            Parent = node,
                        });

                        Interlocked.Increment(ref s.Items);
                        if (!cloudOnly) Interlocked.Add(ref s.Bytes, length);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Interlocked.Increment(ref s.Inaccessible);
            }
            finally
            {
                s.Throttle.Release();
            }

            Interlocked.Increment(ref s.Items);
            MaybeReport(s, dir.FullName);

            if (subDirs.Count > 0)
                await Task.WhenAll(subDirs.Select(d => FillFolderAsync(d.Node, d.Info, s))).ConfigureAwait(false);

            Total(node);
        }

        private static void Total(SpaceNode node)
        {
            long size = 0, cloud = 0;
            int items = 0;
            foreach (var c in node.Children)
            {
                size += c.SizeBytes;
                cloud += c.CloudBytes;
                items += c.ItemCount + 1;
            }
            node.SizeBytes = size;
            node.CloudBytes = cloud;
            node.ItemCount = items;
        }

        private static void MaybeReport(ScanState s, string path)
        {
            if (s.Progress == null) return;
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref s.LastReport);
            if (now - last < ReportIntervalMs) return;
            if (Interlocked.CompareExchange(ref s.LastReport, now, last) != last) return; // another thread reported

            s.Progress.Report(new SpaceScanProgress(Interlocked.Read(ref s.Items), Interlocked.Read(ref s.Bytes), s.Options.ExpectedBytes, path));
        }

        private static bool IsIncluded(FileAttributes attrs, SpaceScanOptions options)
        {
            if (options.IgnoreSystemFiles && attrs.HasFlag(FileAttributes.System)) return false;
            if (!options.ShowHiddenItems && attrs.HasFlag(FileAttributes.Hidden)) return false;
            return true;
        }

        private static bool IsCloudPlaceholder(FileAttributes attrs) =>
            (attrs & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

        private static DateTime SafeLastWrite(FileSystemInfo info)
        {
            try { return info.LastWriteTime; }
            catch (Exception) { return DateTime.MinValue; }
        }

        // =================================================================
        //  Rows
        // =================================================================

        /// <summary>
        /// Rows for one folder, largest first. Items below
        /// <paramref name="minItemSizeBytes"/> are rolled into a single
        /// "N smaller items" row so the list and treemap still add up to the
        /// folder's total (before, they just vanished).
        /// </summary>
        public static List<SpaceListItem> BuildRows(SpaceNode folder, long minItemSizeBytes)
        {
            long total = folder.SizeBytes;
            var rows = new List<SpaceListItem>();
            var small = new List<SpaceNode>();

            foreach (var child in folder.Children.OrderByDescending(c => c.SizeBytes).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (minItemSizeBytes > 0 && child.SizeBytes < minItemSizeBytes)
                {
                    small.Add(child);
                    continue;
                }
                rows.Add(RowFor(child, total));
            }

            if (small.Count > 0)
            {
                long bytes = small.Sum(n => n.SizeBytes);
                rows.Add(new SpaceListItem
                {
                    Name = small.Count == 1 ? "1 smaller item" : $"{small.Count:N0} smaller items",
                    Kind = SpaceItemKind.Others,
                    Category = FileCategory.Other,
                    SizeBytes = bytes,
                    SizeText = FormatBytes(bytes),
                    Percent = total > 0 ? bytes * 100.0 / total : 0,
                    ItemCountText = $"{small.Count:N0}",
                    ModifiedText = "—",
                    Grouped = small,
                });
            }

            return rows;
        }

        private static SpaceListItem RowFor(SpaceNode node, long parentTotal) => new()
        {
            Name = node.Name,
            Kind = node.Kind,
            Category = node.Kind == SpaceItemKind.Folder ? FileCategory.Folder : Categorize(node.Extension),
            SizeBytes = node.SizeBytes,
            SizeText = node.IsCloudOnly ? $"{FormatBytes(node.CloudBytes)} ☁" : FormatBytes(node.SizeBytes),
            Percent = parentTotal > 0 ? node.SizeBytes * 100.0 / parentTotal : 0,
            ItemCountText = node.Kind == SpaceItemKind.Folder ? node.ItemCount.ToString("N0") : "—",
            ModifiedText = node.Modified == DateTime.MinValue ? "—" : node.Modified.ToString("yyyy-MM-dd HH:mm"),
            IsCloudOnly = node.IsCloudOnly,
            Node = node,
        };

        /// <summary>Root-to-node chain for the breadcrumb.</summary>
        public static List<SpaceNode> PathTo(SpaceNode node)
        {
            var chain = new List<SpaceNode>();
            for (var n = node; n != null; n = n.Parent) chain.Add(n);
            chain.Reverse();
            return chain;
        }

        // =================================================================
        //  Treemap (squarified - Bruls, Huizing & van Wijk)
        // =================================================================

        /// <summary>
        /// Lays items out as rectangles whose areas are proportional to
        /// their sizes, keeping them as close to square as possible (the old
        /// layout cut one direction only, producing long slivers).
        /// </summary>
        public static List<TreemapTile> Squarify(IReadOnlyList<SpaceListItem> items, double width, double height)
        {
            var tiles = new List<TreemapTile>();
            var sized = items.Where(i => i.SizeBytes > 0).OrderByDescending(i => i.SizeBytes).ToList();
            if (sized.Count == 0 || width <= 0 || height <= 0) return tiles;

            double total = sized.Sum(i => (double)i.SizeBytes);
            double scale = width * height / total;
            var areas = sized.Select(i => i.SizeBytes * scale).ToList();

            double x = 0, y = 0, w = width, h = height;
            int start = 0;

            while (start < sized.Count)
            {
                double side = Math.Min(w, h);
                int end = start + 1;
                double rowSum = areas[start];
                double worst = Worst(areas, start, end, rowSum, side);

                while (end < sized.Count)
                {
                    double nextSum = rowSum + areas[end];
                    double nextWorst = Worst(areas, start, end + 1, nextSum, side);
                    if (nextWorst > worst) break;
                    rowSum = nextSum;
                    worst = nextWorst;
                    end++;
                }

                // Lay the row along the shorter side, then shrink the free rectangle.
                if (w >= h)
                {
                    double rowWidth = rowSum / h;
                    double ty = y;
                    for (int i = start; i < end; i++)
                    {
                        double th = areas[i] / rowWidth;
                        tiles.Add(new TreemapTile(sized[i], x, ty, rowWidth, th));
                        ty += th;
                    }
                    x += rowWidth;
                    w -= rowWidth;
                }
                else
                {
                    double rowHeight = rowSum / w;
                    double tx = x;
                    for (int i = start; i < end; i++)
                    {
                        double tw = areas[i] / rowHeight;
                        tiles.Add(new TreemapTile(sized[i], tx, y, tw, rowHeight));
                        tx += tw;
                    }
                    y += rowHeight;
                    h -= rowHeight;
                }

                start = end;
            }

            return tiles;
        }

        /// <summary>Worst aspect ratio in a row laid along a side of length <paramref name="side"/>.</summary>
        private static double Worst(List<double> areas, int start, int end, double sum, double side)
        {
            double max = double.MinValue, min = double.MaxValue;
            for (int i = start; i < end; i++)
            {
                max = Math.Max(max, areas[i]);
                min = Math.Min(min, areas[i]);
            }
            double s2 = side * side, sum2 = sum * sum;
            return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
        }

        // =================================================================
        //  File types
        // =================================================================

        private static readonly Dictionary<string, FileCategory> ExtensionMap = BuildExtensionMap();

        private static Dictionary<string, FileCategory> BuildExtensionMap()
        {
            var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
            void Add(FileCategory c, string list) { foreach (var e in list.Split(' ')) map["." + e] = c; }

            Add(FileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob");
            Add(FileCategory.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 nef arw dng psd svg ico avif");
            Add(FileCategory.Audio, "mp3 wav flac aac ogg m4a wma opus aiff alac mid midi");
            Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub one pst ost");
            Add(FileCategory.Archive, "zip rar 7z tar gz tgz bz2 xz iso img vhd vhdx vmdk cab zst");
            Add(FileCategory.Code, "cs js ts jsx tsx py java cpp c h hpp go rs rb php html css scss json xml yaml yml sql sln csproj ipynb pdb obj");
            Add(FileCategory.Program, "exe dll msi msix appx sys bin so dylib jar apk");
            return map;
        }

        public static FileCategory Categorize(string extension) =>
            ExtensionMap.TryGetValue(extension ?? "", out var c) ? c : FileCategory.Other;

        public static string LabelFor(FileCategory category) => category switch
        {
            FileCategory.Video => "Videos",
            FileCategory.Image => "Pictures",
            FileCategory.Audio => "Music & audio",
            FileCategory.Document => "Documents",
            FileCategory.Archive => "Archives & disk images",
            FileCategory.Code => "Code & dev files",
            FileCategory.Program => "Programs",
            FileCategory.Folder => "Folders",
            _ => "Other",
        };

        /// <summary>Data colors (same in both themes; labels on them use black or white by contrast).</summary>
        public static string ColorFor(FileCategory category) => category switch
        {
            FileCategory.Video => "#8B5CF6",
            FileCategory.Image => "#EC4899",
            FileCategory.Audio => "#F59E0B",
            FileCategory.Document => "#3B82F6",
            FileCategory.Archive => "#F97316",
            FileCategory.Code => "#10B981",
            FileCategory.Program => "#EF4444",
            FileCategory.Folder => "#6366F1",
            _ => "#94A3B8",
        };

        /// <summary>What's inside a folder, by file type, plus each child folder's dominant type (for treemap colors).</summary>
        public sealed class FolderInsight
        {
            public required List<TypeBreakdownEntry> Breakdown { get; init; }
            public required Dictionary<SpaceNode, FileCategory> DominantByChild { get; init; }
        }

        /// <summary>Walks the folder's subtree once. Can take a moment at a drive root - call off the UI thread.</summary>
        public static FolderInsight Analyze(SpaceNode folder, CancellationToken token = default)
        {
            var totals = new long[Enum.GetValues<FileCategory>().Length];
            var counts = new int[totals.Length];
            var dominant = new Dictionary<SpaceNode, FileCategory>();

            foreach (var child in folder.Children)
            {
                token.ThrowIfCancellationRequested();
                if (child.Kind == SpaceItemKind.File)
                {
                    var c = Categorize(child.Extension);
                    totals[(int)c] += child.SizeBytes;
                    counts[(int)c]++;
                    continue;
                }

                // Per child folder: its own category totals, folded into the overall ones.
                var local = new long[totals.Length];
                var stack = new Stack<SpaceNode>();
                stack.Push(child);
                int visited = 0;
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    foreach (var c in n.Children)
                    {
                        if (c.Kind == SpaceItemKind.Folder) { stack.Push(c); continue; }
                        var cat = Categorize(c.Extension);
                        local[(int)cat] += c.SizeBytes;
                        counts[(int)cat]++;
                    }
                    if ((++visited & 0x3FF) == 0) token.ThrowIfCancellationRequested();
                }

                int best = 0;
                for (int i = 0; i < local.Length; i++)
                {
                    totals[i] += local[i];
                    if (local[i] > local[best]) best = i;
                }
                dominant[child] = local[best] > 0 ? (FileCategory)best : FileCategory.Other;
            }

            long sum = totals.Sum();
            var breakdown = Enumerable.Range(0, totals.Length)
                .Where(i => totals[i] > 0)
                .Select(i => new TypeBreakdownEntry
                {
                    Category = (FileCategory)i,
                    Bytes = totals[i],
                    FileCount = counts[i],
                    Percent = sum > 0 ? totals[i] * 100.0 / sum : 0,
                    Label = LabelFor((FileCategory)i),
                    ColorHex = ColorFor((FileCategory)i),
                })
                .OrderByDescending(e => e.Bytes)
                .ToList();

            return new FolderInsight { Breakdown = breakdown, DominantByChild = dominant };
        }

        // =================================================================
        //  Delete
        // =================================================================

        /// <summary>
        /// Whether Windows will actually send items on this path to the
        /// Recycle Bin. Network shares and some removable drives have none -
        /// there a "recycle" silently deletes for good, so the view must warn.
        /// </summary>
        public static bool HasRecycleBin(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes off the UI thread (a big folder to the Recycle Bin can
        /// take a while). Does not touch the tree - call
        /// <see cref="ApplyDeletion"/> on the UI thread with the result.
        /// </summary>
        public Task<SpaceDeleteResult> DeleteAsync(IReadOnlyList<SpaceNode> nodes, bool sendToRecycleBin, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new SpaceDeleteResult();
                foreach (var node in nodes)
                {
                    if (token.IsCancellationRequested) break;
                    string path = node.FullPath;
                    try
                    {
                        if (node.Kind == SpaceItemKind.File)
                        {
                            if (sendToRecycleBin) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                            else File.Delete(path);
                        }
                        else
                        {
                            if (sendToRecycleBin) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                            else Directory.Delete(path, recursive: true);
                        }

                        result.Deleted.Add(node);
                        result.BytesFreed += node.SizeBytes;
                    }
                    catch (Exception ex)
                    {
                        result.Failed.Add((node, ex is OperationCanceledException ? "Cancelled" : ex.Message));
                    }
                }
                return result;
            }, CancellationToken.None);

        /// <summary>Removes deleted nodes from the tree and re-totals every ancestor (no rescan needed).</summary>
        public static void ApplyDeletion(SpaceDeleteResult result)
        {
            foreach (var node in result.Deleted)
            {
                var parent = node.Parent;
                if (parent == null) continue;
                parent.Children.Remove(node);
                for (var n = parent; n != null; n = n.Parent) Total(n);
            }
        }

        // =================================================================
        //  Drives
        // =================================================================

        public static List<DriveSummary> GetDrives()
        {
            var list = new List<DriveSummary>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : $"{d.Name} ({d.VolumeLabel})";
                    list.Add(new DriveSummary
                    {
                        RootPath = d.RootDirectory.FullName,
                        Label = label,
                        TotalBytes = d.TotalSize,
                        FreeBytes = d.TotalFreeSpace,
                        DriveType = d.DriveType,
                        FreeText = $"{FormatBytes(d.TotalFreeSpace)} free of {FormatBytes(d.TotalSize)}",
                        DisplayName = $"{label} — {FormatBytes(d.TotalFreeSpace)} free of {FormatBytes(d.TotalSize)}",
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // drive went away between listing and reading - skip it
                }
            }
            return list;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "space_analyzer.json");

        public static SpaceAnalyzerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (File.Exists(file))
                    return JsonSerializer.Deserialize<SpaceAnalyzerSettings>(File.ReadAllText(file)) ?? new SpaceAnalyzerSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            return new SpaceAnalyzerSettings();
        }

        public static void SaveSettings(SpaceAnalyzerSettings settings, string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }

        // =================================================================
        //  Formatting
        // =================================================================

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double size = Math.Max(0, bytes);
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? $"{size:0} B" : $"{size:0.#} {units[unit]}";
        }
    }
}
