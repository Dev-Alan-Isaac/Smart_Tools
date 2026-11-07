using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Background Remover: finding images, building queue items and keeping
    /// the queue and the Files card rows in step. No Magick.NET in here, so
    /// it is unit-tested on its own.
    /// </summary>
    public sealed partial class BackgroundRemoverManager
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp",
        };

        public static IReadOnlyCollection<string> SupportedExtensions => Extensions;

        public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

        public static string ImageFileFilter =>
            "Images|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|All files|*.*";

        // =================================================================
        //  Queue building
        // =================================================================

        /// <summary>
        /// Every supported image in a folder, off the UI thread. Inaccessible
        /// subfolders are skipped instead of aborting the whole scan.
        /// </summary>
        public Task<List<string>> FindImagesAsync(string folder, bool includeSubfolders) => Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSubfolders,
                IgnoreInaccessible = true,
            };
            return Directory.EnumerateFiles(folder, "*", options)
                .Where(IsSupported)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        /// <summary>Builds queue items (reads file sizes) off the UI thread.</summary>
        public Task<List<BackgroundRemoverItem>> CreateItemsAsync(IEnumerable<string> paths) => Task.Run(() =>
            paths.Select(path =>
            {
                long size = 0;
                try { size = new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* moved/locked since queued */ }

                return new BackgroundRemoverItem
                {
                    FullPath = path,
                    DisplayName = Path.GetFileName(path),
                    SizeBytes = size,
                    SizeDisplay = FormatBytes(size),
                };
            }).ToList());

        /// <summary>
        /// Picked or dropped files the tool can open. The Add files dialog has
        /// an "All files" choice, and the old code queued whatever it got (a
        /// .txt then failed at run time); drops were already filtered.
        /// </summary>
        public static List<string> SupportedFiles(IEnumerable<string> paths) =>
            paths.Where(p => IsSupported(p) && File.Exists(p)).ToList();

        // =================================================================
        //  The list (Files card groups + queue)
        // =================================================================

        /// <summary>
        /// Adds items not already queued as one Files card row (a folder, or
        /// loose files). The duplicate check runs here, on the UI thread after
        /// the sizes were read - the old check ran before the await, so the
        /// same image dropped twice in quick succession was queued twice.
        /// Returns the items actually added.
        /// </summary>
        public static List<BackgroundRemoverItem> AddToList(
            BulkObservableCollection<BackgroundRemoverItem> list,
            ICollection<BackgroundRemoverGroup> groups,
            IReadOnlyList<BackgroundRemoverItem> items,
            string? folderPath,
            bool isFolder,
            bool includeSubfolders)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = items.Where(i => known.Add(i.FullPath)).ToList();
            if (added.Count == 0) return added;

            var folders = added.Select(a => Path.GetDirectoryName(a.FullPath) ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new BackgroundRemoverGroup
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

        /// <summary>Takes items off the queue and their Files card rows (an emptied row goes too).</summary>
        public static void RemoveFromList(
            BulkObservableCollection<BackgroundRemoverItem> list,
            ICollection<BackgroundRemoverGroup> groups,
            IReadOnlyCollection<BackgroundRemoverItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<BackgroundRemoverItem>(items);
            list.RemoveWhere(gone.Contains);
            foreach (var group in items.Select(i => i.Group).OfType<BackgroundRemoverGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
        }

        /// <summary>Moves the chosen items one place earlier (-1) or later (+1). Returns false when nothing moved.</summary>
        public static bool Move(IList<BackgroundRemoverItem> list, IReadOnlyCollection<BackgroundRemoverItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<BackgroundRemoverItem>(chosen);
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

        /// <summary>By folder then natural name (Photo 2 before Photo 10), or oldest file first.</summary>
        public static List<BackgroundRemoverItem> Sorted(IEnumerable<BackgroundRemoverItem> items, bool byDate)
        {
            static string FolderOf(BackgroundRemoverItem i) => Path.GetDirectoryName(i.FullPath) ?? "";
            if (!byDate)
                return items.OrderBy(FolderOf, NaturalStringComparer.Instance).ThenBy(i => i.DisplayName, NaturalStringComparer.Instance).ToList();
            return items
                .Select(i => (Item: i, Modified: ModifiedOf(i.FullPath)))
                .OrderBy(x => x.Modified).ThenBy(x => x.Item.DisplayName, NaturalStringComparer.Instance)
                .Select(x => x.Item).ToList();
        }

        private static DateTime ModifiedOf(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return DateTime.MaxValue; }
        }

        /// <summary>Deletes the file from disk: to the Recycle Bin on a local fixed drive, otherwise permanently.</summary>
        public static void DeleteImage(string path)
        {
            if (!File.Exists(path)) return;
            if (ImageViewerManager.HasRecycleBin(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }

        /// <summary>
        /// After "Include subfolders" changes: brings a folder row in line with
        /// a fresh scan. Images no longer found come off the queue, new ones go
        /// at the end; the rest keep their place, tick and status.
        /// <paramref name="newItems"/> are items built for found paths not yet
        /// queued (any already queued are skipped here).
        /// </summary>
        public static (List<BackgroundRemoverItem> Added, List<BackgroundRemoverItem> Removed) ApplyRescan(
            BulkObservableCollection<BackgroundRemoverItem> list,
            ICollection<BackgroundRemoverGroup> groups,
            BackgroundRemoverGroup group,
            IReadOnlyCollection<string> foundPaths,
            IReadOnlyList<BackgroundRemoverItem> newItems,
            bool includeSubfolders)
        {
            var found = new HashSet<string>(foundPaths, StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            if (gone.Count > 0)
            {
                var goneSet = new HashSet<BackgroundRemoverItem>(gone);
                list.RemoveWhere(goneSet.Contains);
                group.Items.RemoveAll(goneSet.Contains);
            }

            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = newItems.Where(i => found.Contains(i.FullPath) && known.Add(i.FullPath)).ToList();
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
            }
            if (added.Count > 0) list.AddRange(added);

            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            return (added, gone);
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:0.#} {units[unit]}";
        }
    }
}
