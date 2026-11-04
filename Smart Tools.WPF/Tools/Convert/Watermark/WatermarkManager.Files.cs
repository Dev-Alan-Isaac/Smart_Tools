using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    // The list half of the watermarker: finding files and the Files card rows. Kept apart from WatermarkManager.cs
    // so the layout maths compiles on its own.
    //
    // Built for big lists (100,000 files and more): a scan reads each folder once and brings the sizes with it, rows are
    // added and removed as one change (BulkObservableCollection), and nothing here touches the disk once the scan is done.
    public static partial class WatermarkManager
    {
        /// <summary>Most files one batch holds. The list is virtualised and changed in bulk, so this is a sanity limit, not a speed one.</summary>
        public const int MaxFiles = 100_000;

        public sealed class ScanResult
        {
            public List<string> Files { get; } = new();

            /// <summary>Size of each found file, read together with its name, so adding it to the list needs no further disk access.</summary>
            public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

            public int InaccessibleFolders { get; set; }
            public int Unsupported { get; set; }
        }

        /// <summary>
        /// Pictures, videos and PDFs in the given files and folders, off the UI thread. Linked folders are never followed, hidden and system items
        /// are left alone, our own leftovers (".name.creating-....tmp") are skipped, and an unreadable subfolder is counted instead of ending the scan.
        /// <paramref name="onFound"/> is told the running count about ten times a second, so a huge tree shows life instead of a frozen page.
        /// </summary>
        public static Task<ScanResult> ScanAsync(IEnumerable<string> paths, bool includeSubfolders, CancellationToken token = default, Action<int>? onFound = null)
        {
            var list = paths.ToList();
            return Task.Run(() =>
            {
                var result = new ScanResult();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var clock = Stopwatch.StartNew();
                long lastReport = 0;

                void Found(string full, long size)
                {
                    if (!seen.Add(full)) return;
                    result.Files.Add(full);
                    result.Sizes[full] = size;
                    if (onFound != null && clock.ElapsedMilliseconds - lastReport >= 100)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        onFound(result.Files.Count);
                    }
                }

                foreach (string path in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(path))
                    {
                        string full = Path.GetFullPath(path);
                        if (!IsSupported(full)) { result.Unsupported++; continue; }
                        try { Found(full, new FileInfo(full).Length); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { /* vanished meanwhile */ }
                        continue;
                    }
                    if (!Directory.Exists(path)) continue;

                    var pending = new Stack<string>();
                    pending.Push(path);
                    while (pending.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        string dir = pending.Pop();
                        try
                        {
                            var info = new DirectoryInfo(dir);
                            // FileInfo objects from an enumeration already carry attributes and length: no second disk call per file.
                            var files = new List<FileInfo>();
                            foreach (var f in info.EnumerateFiles())
                            {
                                if (!IsSupported(f.Name)) continue;
                                if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                                files.Add(f);
                            }
                            files.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.FullName, b.FullName));
                            foreach (var f in files) Found(f.FullName, f.Length);

                            if (!includeSubfolders) continue;
                            var subs = new List<string>();
                            foreach (var d in info.EnumerateDirectories())
                            {
                                if ((d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                                subs.Add(d.FullName);
                            }
                            subs.Sort(StringComparer.OrdinalIgnoreCase);
                            for (int i = subs.Count - 1; i >= 0; i--) pending.Push(subs[i]);   // popped in name order
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                        {
                            result.InaccessibleFolders++;
                        }
                    }
                }
                onFound?.Invoke(result.Files.Count);
                return result;
            }, token);
        }

        /// <summary>
        /// List rows for found files. <paramref name="sizes"/> (from the scan) saves a disk call per file; a path without a known size is looked up,
        /// and a file that vanished or can't be read is left out.
        /// </summary>
        public static List<WatermarkItem> CreateItems(IEnumerable<string> paths, IReadOnlyDictionary<string, long>? sizes = null)
        {
            var items = new List<WatermarkItem>();
            foreach (string path in paths)
            {
                if (KindOf(path) is not { } kind) continue;
                long size;
                if (sizes != null && sizes.TryGetValue(path, out long known)) size = known;
                else
                {
                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                        continue;
                    }
                }
                items.Add(new WatermarkItem { FullPath = path, Name = Path.GetFileName(path), Kind = kind, SizeBytes = size, Listed = true });
            }
            return items;
        }

        /// <summary>Puts items in the list as ONE change when the list can (a Bulk list), else one by one.</summary>
        private static void AppendAll(IList<WatermarkItem> list, List<WatermarkItem> items)
        {
            if (items.Count == 0) return;
            if (list is BulkObservableCollection<WatermarkItem> bulk) bulk.AddRange(items);
            else foreach (var item in items) list.Add(item);
        }

        private static void RemoveAll(IList<WatermarkItem> list, HashSet<WatermarkItem> gone)
        {
            if (gone.Count == 0) return;
            if (list is BulkObservableCollection<WatermarkItem> bulk) bulk.RemoveWhere(gone.Contains);
            else for (int i = list.Count - 1; i >= 0; i--) if (gone.Contains(list[i])) list.RemoveAt(i);
        }

        /// <summary>Adds files not already listed as one Files card row (up to <see cref="MaxFiles"/> in all). Returns the items added.</summary>
        public static List<WatermarkItem> AddToList(IList<WatermarkItem> list, ICollection<WatermarkGroup> groups, IEnumerable<string> paths,
            string? folderPath, bool isFolder, bool includeSubfolders, IReadOnlyDictionary<string, long>? sizes = null)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            int room = Math.Max(0, MaxFiles - list.Count);
            var added = CreateItems(paths.Where(p => known.Add(p)).Take(room), sizes);
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new WatermarkGroup
            {
                FolderPath = folderPath ?? (folders.Count == 1 ? folders[0] : null),
                IsFolder = isFolder,
                IncludeSubfolders = includeSubfolders,
            };
            foreach (var item in added) item.Group = group;
            group.Items.AddRange(added);
            AppendAll(list, added);
            groups.Add(group);
            return added;
        }

        /// <summary>Takes files off the list and their Files card rows (an emptied row goes too).</summary>
        public static void RemoveFromList(IList<WatermarkItem> list, ICollection<WatermarkGroup> groups, IReadOnlyCollection<WatermarkItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<WatermarkItem>(items);
            foreach (var item in gone) item.Listed = false;
            RemoveAll(list, gone);
            foreach (var group in items.Select(i => i.Group).OfType<WatermarkGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
        }

        /// <summary>After "Include subfolders" changes: brings a folder row in line with a fresh scan. Files no longer found come off, new ones go at the end.</summary>
        public static (List<WatermarkItem> Added, List<WatermarkItem> Removed) ApplyRescan(IList<WatermarkItem> list, ICollection<WatermarkGroup> groups,
            WatermarkGroup group, IReadOnlyCollection<string> foundPaths, bool includeSubfolders, IReadOnlyDictionary<string, long>? sizes = null)
        {
            var found = new HashSet<string>(foundPaths, StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            var goneSet = new HashSet<WatermarkItem>(gone);
            foreach (var item in gone) item.Listed = false;
            RemoveAll(list, goneSet);
            group.Items.RemoveAll(goneSet.Contains);

            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            int room = Math.Max(0, MaxFiles - list.Count);
            var added = CreateItems(foundPaths.Where(p => known.Add(p)).Take(room), sizes);
            foreach (var item in added) item.Group = group;
            group.Items.AddRange(added);
            AppendAll(list, added);

            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            return (added, gone);
        }
    }
}
