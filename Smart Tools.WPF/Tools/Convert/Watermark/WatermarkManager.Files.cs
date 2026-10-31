using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    // The list half of the watermarker: finding files and the Files card rows. Kept apart from WatermarkManager.cs
    // so the layout maths compiles on its own.
    public static partial class WatermarkManager
    {
        /// <summary>Most files one batch holds (a longer list makes the page slow and a run take hours).</summary>
        public const int MaxFiles = 5000;

        public sealed class ScanResult
        {
            public List<string> Files { get; } = new();
            public int InaccessibleFolders { get; set; }
            public int Unsupported { get; set; }
        }

        /// <summary>
        /// Pictures, videos and PDFs in the given files and folders, off the UI thread. Linked folders are never followed, hidden and system items
        /// are left alone, our own leftovers (".name.creating-....tmp") are skipped, and an unreadable subfolder is counted instead of ending the scan.
        /// </summary>
        public static Task<ScanResult> ScanAsync(IEnumerable<string> paths, bool includeSubfolders, CancellationToken token = default)
        {
            var list = paths.ToList();
            return Task.Run(() =>
            {
                var result = new ScanResult();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(path))
                    {
                        string full = Path.GetFullPath(path);
                        if (!IsSupported(full)) result.Unsupported++;
                        else if (seen.Add(full)) result.Files.Add(full);
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
                            foreach (string f in Directory.EnumerateFiles(dir).Where(IsSupported).OrderBy(f => f, NaturalStringComparer.Instance))
                            {
                                var attrs = File.GetAttributes(f);
                                if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) continue;
                                if (seen.Add(f)) result.Files.Add(f);
                            }
                            if (!includeSubfolders) continue;
                            foreach (string sub in Directory.EnumerateDirectories(dir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                            {
                                var attrs = File.GetAttributes(sub);
                                if (attrs.HasFlag(FileAttributes.ReparsePoint) || attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) continue;
                                pending.Push(sub);
                            }
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                        {
                            result.InaccessibleFolders++;
                        }
                    }
                }
                return result;
            }, token);
        }

        /// <summary>List rows for found files; a file that vanished or can't be read is left out.</summary>
        public static List<WatermarkItem> CreateItems(IEnumerable<string> paths)
        {
            var items = new List<WatermarkItem>();
            foreach (string path in paths)
            {
                if (KindOf(path) is not { } kind) continue;
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    continue;
                }
                items.Add(new WatermarkItem { FullPath = path, Name = Path.GetFileName(path), Kind = kind, SizeBytes = size });
            }
            return items;
        }

        /// <summary>Adds files not already listed as one Files card row (up to <see cref="MaxFiles"/> in all). Returns the items added.</summary>
        public static List<WatermarkItem> AddToList(IList<WatermarkItem> list, ICollection<WatermarkGroup> groups, IEnumerable<string> paths,
            string? folderPath, bool isFolder, bool includeSubfolders)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            int room = Math.Max(0, MaxFiles - list.Count);
            var added = CreateItems(paths.Where(p => known.Add(p)).Take(room));
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new WatermarkGroup
            {
                FolderPath = folderPath ?? (folders.Count == 1 ? folders[0] : null),
                IsFolder = isFolder,
                IncludeSubfolders = includeSubfolders,
            };
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
                list.Add(item);
            }
            groups.Add(group);
            return added;
        }

        /// <summary>Takes files off the list and their Files card rows (an emptied row goes too).</summary>
        public static void RemoveFromList(IList<WatermarkItem> list, ICollection<WatermarkGroup> groups, IReadOnlyCollection<WatermarkItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<WatermarkItem>(items);
            foreach (var item in items) list.Remove(item);
            foreach (var group in items.Select(i => i.Group).OfType<WatermarkGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
        }

        /// <summary>After "Include subfolders" changes: brings a folder row in line with a fresh scan. Files no longer found come off, new ones go at the end.</summary>
        public static (List<WatermarkItem> Added, List<WatermarkItem> Removed) ApplyRescan(IList<WatermarkItem> list, ICollection<WatermarkGroup> groups,
            WatermarkGroup group, IReadOnlyCollection<string> foundPaths, bool includeSubfolders)
        {
            var found = new HashSet<string>(foundPaths, StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            foreach (var item in gone)
            {
                list.Remove(item);
                group.Items.Remove(item);
            }

            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            int room = Math.Max(0, MaxFiles - list.Count);
            var added = CreateItems(foundPaths.Where(p => known.Add(p)).Take(room));
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
                list.Add(item);
            }

            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            return (added, gone);
        }
    }
}
