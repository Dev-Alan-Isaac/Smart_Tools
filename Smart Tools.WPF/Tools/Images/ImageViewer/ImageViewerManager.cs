using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Image Viewer backend, split in two:
    /// <list type="bullet">
    /// <item>ImageViewerManager.cs (this file) - no WPF: finding images,
    /// the list and its groups, browse order, zoom and rotation maths, the
    /// countdown ring's shape and settings. Unit-tested on its own.</item>
    /// <item>ImageViewerManager.Imaging.cs - decoding (native first,
    /// ImageMagick for HEIC/AVIF and anything WPF can't read), photo
    /// orientation, thumbnails and saving.</item>
    /// </list>
    /// </summary>
    public static partial class ImageViewerManager
    {
        public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".ico",
        };

        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

        public static string FileDialogFilter =>
            "Images|" + string.Join(";", ImageExtensions.Select(e => "*" + e)) + "|All files (*.*)|*.*";

        // =================================================================
        //  Finding images
        // =================================================================

        /// <summary>
        /// Lists the images in a folder off the UI thread, sorted the way
        /// Explorer shows them ("IMG_2" before "IMG_10"), folder by folder.
        /// One unreadable subfolder no longer stops the rest (the old
        /// EnumerateFiles threw halfway through and crashed the view), hidden
        /// and system files are skipped, and junctions aren't followed.
        /// </summary>
        public static Task<ImageFolderScan> ScanFolderAsync(string folder, bool includeSubfolders, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new ImageFolderScan();
                if (!Directory.Exists(folder))
                {
                    result.Missing = true;
                    return result;
                }

                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(folder));
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var dir = pending.Pop();
                    FileSystemInfo[] entries;
                    try
                    {
                        entries = dir.GetFileSystemInfos();
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        result.InaccessibleFolders++;
                        continue;
                    }

                    var subfolders = new List<DirectoryInfo>();
                    foreach (var entry in entries.OrderBy(e => e.Name, NaturalStringComparer.Instance))
                    {
                        FileAttributes attrs;
                        try
                        {
                            attrs = entry.Attributes;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }
                        if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) continue;

                        if (entry is DirectoryInfo sub)
                        {
                            if (!includeSubfolders) continue;
                            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                            {
                                result.LinksSkipped++;
                                continue;
                            }
                            subfolders.Add(sub);
                        }
                        else if (entry is FileInfo file && IsImage(file.Name))
                        {
                            try
                            {
                                result.Files.Add(new ImageFileFound(file.FullName, file.Length));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                // vanished while listing
                            }
                        }
                    }
                    for (int i = subfolders.Count - 1; i >= 0; i--) pending.Push(subfolders[i]);
                }
                return result;
            }, token);

        /// <summary>Picked or dropped files that exist (and are images, unless <paramref name="imagesOnly"/> is off), in the order given.</summary>
        public static Task<List<ImageFileFound>> ReadFilesAsync(IEnumerable<string> paths, CancellationToken token = default, bool imagesOnly = true)
        {
            var list = paths.ToList();
            return Task.Run(() =>
            {
                var found = new List<ImageFileFound>();
                foreach (string path in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (imagesOnly && !IsImage(path)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists) found.Add(new ImageFileFound(info.FullName, info.Length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        // unreadable path - skip it
                    }
                }
                return found;
            }, token);
        }

        // =================================================================
        //  The list
        // =================================================================

        /// <summary>
        /// Adds images not already in the list as one sidebar group. A set
        /// makes the "already there?" check instant - the old one scanned the
        /// whole list for every file, which crawled on big folders. Returns
        /// the new items (empty when every image was already listed).
        /// </summary>
        public static List<ImageQueueItem> AddToList(
            BulkObservableCollection<ImageQueueItem> queue,
            ICollection<ImageQueueGroup> groups,
            IReadOnlyList<ImageFileFound> files,
            string? folderPath,
            bool isFolder,
            bool includeSubfolders)
        {
            var known = new HashSet<string>(queue.Select(q => q.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<ImageQueueItem>();
            foreach (var f in files)
            {
                if (!known.Add(f.FullPath)) continue;
                added.Add(new ImageQueueItem { FullPath = f.FullPath, SizeBytes = f.SizeBytes, Number = queue.Count + added.Count + 1 });
            }
            if (added.Count == 0) return added;

            // Files from one folder are labelled with that folder.
            string? label = folderPath ?? CommonFolder(added.Select(a => a.Folder));
            var group = new ImageQueueGroup { FolderPath = label, IsFolder = isFolder, IncludeSubfolders = includeSubfolders };
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
            }
            queue.AddRange(added);
            groups.Add(group);
            return added;
        }

        private static string? CommonFolder(IEnumerable<string> folders)
        {
            var distinct = folders.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }

        /// <summary>Removes images from the list and their groups (empty groups go). Numbers are updated.</summary>
        public static void RemoveFromList(BulkObservableCollection<ImageQueueItem> queue, ICollection<ImageQueueGroup> groups, IReadOnlyCollection<ImageQueueItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<ImageQueueItem>(items);
            queue.RemoveWhere(gone.Contains);
            foreach (var group in items.Select(i => i.Group).OfType<ImageQueueGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
            Renumber(queue);
        }

        public static void Renumber(IList<ImageQueueItem> queue)
        {
            for (int i = 0; i < queue.Count; i++) queue[i].Number = i + 1;
        }

        /// <summary>
        /// Moves the chosen images one place earlier (-1) or later (+1); a block already
        /// at the edge stays put. Returns false when nothing moved.
        /// </summary>
        public static bool Move(IList<ImageQueueItem> list, IReadOnlyCollection<ImageQueueItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<ImageQueueItem>(chosen);
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

        /// <summary>By folder then natural name (Photo 2 before Photo 10), or oldest file first.</summary>
        public static List<ImageQueueItem> Sorted(IEnumerable<ImageQueueItem> items, bool byDate)
        {
            if (!byDate)
                return items.OrderBy(i => i.Folder, NaturalStringComparer.Instance).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();
            return items
                .Select(i => (Item: i, Modified: ModifiedOf(i.FullPath)))
                .OrderBy(x => x.Modified).ThenBy(x => x.Item.Name, NaturalStringComparer.Instance)
                .Select(x => x.Item).ToList();
        }

        /// <summary>True when deleting this file goes to the Recycle Bin (local fixed drives only).</summary>
        public static bool HasRecycleBin(string path)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Deletes the file from disk: to the Recycle Bin where there is one, otherwise permanently.</summary>
        public static void DeleteImage(string path)
        {
            if (!File.Exists(path)) return;
            if (HasRecycleBin(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }

        private static DateTime ModifiedOf(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return DateTime.MaxValue; }
        }

        /// <summary>After removing the image on screen: the one that took its place, else the one before, else none.</summary>
        public static ImageQueueItem? NeighbourAfterRemoval(IReadOnlyList<ImageQueueItem> queue, int removedIndex) =>
            queue.Count == 0 ? null : queue[Math.Clamp(removedIndex, 0, queue.Count - 1)];

        public static string DescribeList(IReadOnlyCollection<ImageQueueItem> items)
        {
            if (items.Count == 0) return "No images";
            long bytes = items.Sum(i => i.SizeBytes);
            return $"{items.Count:N0} {ImageViewerText.Plural(items.Count, "image", "images")} · {ImageViewerText.FormatBytes(bytes)}";
        }

        // =================================================================
        //  Zoom and rotation
        // =================================================================

        public const double MinZoom = 0.05;
        public const double MaxZoom = 16.0;
        public const double ZoomStep = 1.25;

        public static double ClampZoom(double zoom) => Math.Clamp(double.IsFinite(zoom) ? zoom : 1, MinZoom, MaxZoom);

        /// <summary>
        /// The zoom that fits the (possibly turned) image in the viewport.
        /// Never enlarges: a small image shows at 100% instead of being blown
        /// up to 800% and blurred (the old fit did that).
        /// </summary>
        public static double FitZoom(double viewportWidth, double viewportHeight, int pixelWidth, int pixelHeight, double rotation)
        {
            if (viewportWidth <= 0 || viewportHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return 1.0;
            bool sideways = IsSideways(rotation);
            double w = sideways ? pixelHeight : pixelWidth;
            double h = sideways ? pixelWidth : pixelHeight;
            return ClampZoom(Math.Min(1.0, Math.Min(viewportWidth / w, viewportHeight / h)));
        }

        public static bool IsSideways(double rotation) => Math.Abs(NormalizeAngle(rotation, 0) % 180) == 90;

        public static double NormalizeAngle(double angle, double delta) => ((angle + delta) % 360 + 360) % 360;

        /// <summary>
        /// New scroll offset so the point under the mouse stays put when
        /// zooming from <paramref name="oldZoom"/> to <paramref name="newZoom"/>.
        /// </summary>
        public static double OffsetForZoomAt(double offset, double pointer, double oldZoom, double newZoom) =>
            Math.Max(0, (offset + pointer) * (newZoom / oldZoom) - pointer);

        /// <summary>The next preset step up or down (…, 50%, 67%, 100%, 150%, 200%, …) - round numbers instead of 1.25ⁿ.</summary>
        public static double StepZoom(double current, int direction)
        {
            double[] steps = { 0.05, 0.1, 0.125, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16 };
            if (direction > 0) return steps.FirstOrDefault(s => s > current * 1.001, MaxZoom);
            return steps.LastOrDefault(s => s < current * 0.999, MinZoom);
        }

        /// <summary>
        /// How to turn the pixels for an EXIF orientation (1-8) so the photo
        /// stands the right way up. Phone photos taken upright store sideways
        /// pixels plus this tag - the old viewer ignored it, so they showed
        /// lying on their side.
        /// </summary>
        public static (int Rotate, bool FlipHorizontal) OrientationTransform(int exifOrientation) => exifOrientation switch
        {
            2 => (0, true),
            3 => (180, false),
            4 => (180, true),
            5 => (90, true),
            6 => (90, false),
            7 => (270, true),
            8 => (270, false),
            _ => (0, false),
        };

        // =================================================================
        //  Browse order
        // =================================================================

        /// <summary>The slideshow speeds offered, in seconds.</summary>
        public static readonly IReadOnlyList<int> SlideshowSpeeds = new[] { 2, 3, 5, 10 };

        public static int ClampSlideshowSeconds(int seconds) =>
            SlideshowSpeeds.OrderBy(s => Math.Abs(s - seconds)).First();

        // =================================================================
        //  Countdown ring (the shape; the WPF geometry is built in .Imaging.cs)
        // =================================================================

        /// <summary>Matches the ring in the XAML: a 62 px circle centred in 68 px.</summary>
        public const double RingRadius = 31, RingCenter = 34;

        /// <summary>Start and end of the arc for a sweep, clockwise from 12 o'clock. Null when there's nothing to draw yet.</summary>
        public static (double StartX, double StartY, double EndX, double EndY, bool LargeArc)? RingArc(double sweepDegrees)
        {
            if (!(sweepDegrees > 0.5)) return null;
            sweepDegrees = Math.Min(sweepDegrees, 359.5);   // one arc can't close a full circle
            double start = -90 * Math.PI / 180, end = (-90 + sweepDegrees) * Math.PI / 180;
            return (RingCenter + RingRadius * Math.Cos(start), RingCenter + RingRadius * Math.Sin(start),
                    RingCenter + RingRadius * Math.Cos(end), RingCenter + RingRadius * Math.Sin(end),
                    sweepDegrees > 180);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "image_viewer.json");

        public static ImageViewerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ImageViewerSettings();
                var s = JsonSerializer.Deserialize<ImageViewerSettings>(File.ReadAllText(file), JsonOptions) ?? new ImageViewerSettings();
                if (!Enum.IsDefined(s.BrowseMode)) s.BrowseMode = ImageBrowseMode.Sequential;
                s.SlideshowSeconds = ClampSlideshowSeconds(s.SlideshowSeconds);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                return new ImageViewerSettings();
            }
        }

        public static void SaveSettings(ImageViewerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never fail the tool over them.
            }
        }
    }

    /// <summary>
    /// Previous / next image for Sequential and Shuffle, tracked by item -
    /// the old viewer tracked a row number, so removing an image above the
    /// current one made it jump to, highlight or remove the wrong image.
    /// Shuffle plays every image once per round, and a new round never
    /// starts with the image just shown.
    /// </summary>
    public sealed class ImageBrowseOrder<T> where T : class
    {
        private readonly Random _random;
        private readonly List<T> _order = new();
        private int _position = -1;

        public ImageBrowseOrder(Random? random = null) => _random = random ?? Random.Shared;

        public ImageBrowseMode Mode { get; set; } = ImageBrowseMode.Sequential;

        /// <summary>Forget the shuffle (after the list changes or the mode switches).</summary>
        public void Reset()
        {
            _order.Clear();
            _position = -1;
        }

        public T? Step(IReadOnlyList<T> items, T? current, int direction)
        {
            if (items.Count == 0) return null;
            if (Mode == ImageBrowseMode.Sequential)
            {
                int index = current == null ? -1 : IndexOf(items, current);
                if (index < 0) return direction >= 0 ? items[0] : items[^1];
                return items[((index + direction) % items.Count + items.Count) % items.Count];
            }

            if (_order.Count != items.Count || (current != null && !_order.Contains(current)) || !SameItems(items))
                NewRound(items, current, keepCurrentFirst: true);
            _position = current == null ? -1 : _order.IndexOf(current);
            _position += direction;
            if (_position >= _order.Count)
            {
                NewRound(items, current, keepCurrentFirst: false);
                _position = 0;
            }
            else if (_position < 0)
            {
                if (current == null)
                {
                    _position = 0;
                }
                else
                {
                    NewRound(items, current, keepCurrentFirst: false);
                    _position = _order.Count - 1;
                }
            }
            return _order[_position];
        }

        /// <summary>One pass with a set - comparing every image with every other was far too slow for 10,000 images.</summary>
        private bool SameItems(IReadOnlyList<T> items)
        {
            var set = new HashSet<object>(items, ReferenceEqualityComparer.Instance);
            return _order.All(set.Contains);
        }

        private static int IndexOf(IReadOnlyList<T> items, T item)
        {
            for (int i = 0; i < items.Count; i++)
                if (ReferenceEquals(items[i], item)) return i;
            return -1;
        }

        private void NewRound(IReadOnlyList<T> items, T? current, bool keepCurrentFirst)
        {
            _order.Clear();
            _order.AddRange(items);
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            if (current == null || _order.Count < 2) return;
            int at = _order.IndexOf(current);
            if (at < 0) return;
            if (keepCurrentFirst)
            {
                // Mid-round: the current image counts as already shown.
                _order.RemoveAt(at);
                _order.Insert(0, current);
            }
            else if (at == 0 || at == _order.Count - 1)
            {
                // New round: never show the same image twice in a row, going either way.
                int swap = _order.Count / 2;
                (_order[at], _order[swap]) = (_order[swap], _order[at]);
            }
        }
    }

    /// <summary>"IMG_2" before "IMG_10", then case-insensitive - the order Explorer shows.</summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (a == null || b == null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
