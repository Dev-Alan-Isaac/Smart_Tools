using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    // The list half of the sprite packer: finding images, the Files card rows, ordering.
    // Kept apart from SpritePackerManager.cs so the packing maths compiles on its own.
    public static partial class SpritePackerManager
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp",
        };

        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

        public sealed class ScanResult
        {
            public List<string> Files { get; } = new();
            public int InaccessibleFolders { get; set; }
        }

        /// <summary>
        /// Images in the given files and folders, off the UI thread. Linked folders are never followed, hidden and system
        /// items are left alone, and an unreadable subfolder is skipped (and counted) instead of ending the scan.
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
                        if (IsImage(full) && seen.Add(full)) result.Files.Add(full);
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
                            foreach (string f in Directory.EnumerateFiles(dir).Where(IsImage).OrderBy(f => f, NaturalStringComparer.Instance))
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

        // =================================================================
        //  The list (Files card rows + sprite list)
        // =================================================================

        /// <summary>List rows for found images; a file that vanished or can't be read is left out.</summary>
        public static List<SpriteItem> CreateItems(IEnumerable<string> paths)
        {
            var items = new List<SpriteItem>();
            foreach (string path in paths)
            {
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    continue;
                }
                items.Add(new SpriteItem { FullPath = path, Name = Path.GetFileName(path), SizeBytes = size });
            }
            return items;
        }

        /// <summary>Adds images not already listed as one Files card row (up to <see cref="MaxSprites"/> in all). Returns the items added.</summary>
        public static List<SpriteItem> AddToList(IList<SpriteItem> list, ICollection<SpriteGroup> groups, IEnumerable<string> paths,
            string? folderPath, bool isFolder, bool includeSubfolders)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            int room = Math.Max(0, MaxSprites - list.Count);
            var added = CreateItems(paths.Where(p => known.Add(p)).Take(room));
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new SpriteGroup
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

        /// <summary>Takes images off the list and their Files card rows (an emptied row goes too).</summary>
        public static void RemoveFromList(IList<SpriteItem> list, ICollection<SpriteGroup> groups, IReadOnlyCollection<SpriteItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<SpriteItem>(items);
            foreach (var item in items) list.Remove(item);
            foreach (var group in items.Select(i => i.Group).OfType<SpriteGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
        }

        /// <summary>
        /// After "Include subfolders" changes: brings a folder row in line with a fresh scan. Images no longer found come off, new ones go
        /// at the end; the rest keep their place and tick.
        /// </summary>
        public static (List<SpriteItem> Added, List<SpriteItem> Removed) ApplyRescan(IList<SpriteItem> list, ICollection<SpriteGroup> groups,
            SpriteGroup group, IReadOnlyCollection<string> foundPaths, bool includeSubfolders)
        {
            var found = new HashSet<string>(foundPaths, StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            foreach (var item in gone)
            {
                list.Remove(item);
                group.Items.Remove(item);
            }

            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            int room = Math.Max(0, MaxSprites - list.Count);
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

        /// <summary>Moves the chosen sprites one place earlier (-1) or later (+1). Returns false when nothing moved.</summary>
        public static bool Move(IList<SpriteItem> list, IReadOnlyCollection<SpriteItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<SpriteItem>(chosen);
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
            return moved;
        }

        /// <summary>By folder then natural name (Walk 2 before Walk 10).</summary>
        public static List<SpriteItem> SortedByName(IEnumerable<SpriteItem> items) =>
            items.OrderBy(i => i.Folder, NaturalStringComparer.Instance).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();
    }
}
