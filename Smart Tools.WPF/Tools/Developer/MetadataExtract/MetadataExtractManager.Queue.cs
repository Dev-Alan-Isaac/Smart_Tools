using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The sidebar queue: one entry per added folder (with or without
    /// subfolders, as it was when added) and one per parent folder of loose
    /// files. Every row remembers its entry, so removing an entry removes
    /// just its rows - rows already read elsewhere keep their results.
    /// </summary>
    public static partial class MetadataExtractManager
    {
        private static bool SamePath(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>Adds a scanned folder's rows under one entry (reused if the same folder was added the same way). Returns rows added.</summary>
        public static int AddFolderToQueue(ICollection<MetadataFileItem> list, HashSet<string> known, IList<MetadataQueueGroup> groups,
            string folder, bool includeSubfolders, IEnumerable<MetadataFileItem> items)
        {
            var group = groups.FirstOrDefault(g => g.IsFolder && g.IncludeSubfolders == includeSubfolders && SamePath(g.FolderPath, folder));
            bool isNew = group == null;
            group ??= new MetadataQueueGroup { FolderPath = folder, IsFolder = true, IncludeSubfolders = includeSubfolders };
            int added = AddToGroup(list, known, group, items);
            if (isNew && group.Count > 0) groups.Add(group);   // a folder with nothing new adds no entry
            return added;
        }

        /// <summary>Adds loose files, one entry per parent folder. Returns rows added.</summary>
        public static int AddFilesToQueue(ICollection<MetadataFileItem> list, HashSet<string> known, IList<MetadataQueueGroup> groups, IEnumerable<MetadataFileItem> items)
        {
            int added = 0;
            foreach (var byFolder in items.GroupBy(i => i.Folder, StringComparer.OrdinalIgnoreCase))
            {
                var group = groups.FirstOrDefault(g => !g.IsFolder && SamePath(g.FolderPath, byFolder.Key));
                bool isNew = group == null;
                group ??= new MetadataQueueGroup { FolderPath = byFolder.Key, IsFolder = false };
                added += AddToGroup(list, known, group, byFolder);
                if (isNew && group.Count > 0) groups.Add(group);
            }
            return added;
        }

        private static int AddToGroup(ICollection<MetadataFileItem> list, HashSet<string> known, MetadataQueueGroup group, IEnumerable<MetadataFileItem> items)
        {
            var fresh = new List<MetadataFileItem>();
            foreach (var item in items)
            {
                if (!known.Add(item.FullPath)) continue;   // already listed (maybe under another entry)
                item.Group = group;
                fresh.Add(item);
            }
            if (fresh.Count == 0) return 0;
            if (list is BulkObservableCollection<MetadataFileItem> bulk) bulk.AddRange(fresh);
            else foreach (var item in fresh) list.Add(item);
            group.SingleFileName = group.Count + fresh.Count == 1 ? fresh[0].Name : null;
            group.Count += fresh.Count;
            return fresh.Count;
        }

        /// <summary>Takes rows off the list (the files stay where they are); entries left empty go too.</summary>
        public static void RemoveFromQueue(IList<MetadataFileItem> list, HashSet<string> known, IList<MetadataQueueGroup> groups, IReadOnlyCollection<MetadataFileItem> remove)
        {
            if (remove.Count == 0) return;
            var gone = remove.ToHashSet();
            var touched = new HashSet<MetadataQueueGroup>();
            foreach (var item in gone)
            {
                known.Remove(item.FullPath);
                if (item.Group is { } g)
                {
                    g.Count = Math.Max(0, g.Count - 1);
                    touched.Add(g);
                }
            }

            if (gone.Count == list.Count) list.Clear();
            else if (list is System.Collections.ObjectModel.ObservableCollection<MetadataFileItem> && gone.Count > 200)
            {
                // Many removals: rebuild once instead of one change notification per row.
                var keep = list.Where(i => !gone.Contains(i)).ToList();
                list.Clear();
                if (list is BulkObservableCollection<MetadataFileItem> bulk) bulk.AddRange(keep);
                else foreach (var i in keep) list.Add(i);
            }
            else foreach (var item in gone) list.Remove(item);

            foreach (var g in touched)
            {
                if (g.Count <= 0) groups.Remove(g);
                else if (!g.IsFolder) g.SingleFileName = g.Count == 1 ? list.FirstOrDefault(i => i.Group == g)?.Name : null;
            }
        }

        /// <summary>Removes an entry and every row it added.</summary>
        public static int RemoveGroup(IList<MetadataFileItem> list, HashSet<string> known, IList<MetadataQueueGroup> groups, MetadataQueueGroup group)
        {
            var rows = list.Where(i => i.Group == group).ToList();
            RemoveFromQueue(list, known, groups, rows);
            groups.Remove(group);
            return rows.Count;
        }

        public static void ClearQueue(IList<MetadataFileItem> list, HashSet<string> known, IList<MetadataQueueGroup> groups)
        {
            list.Clear();
            known.Clear();
            groups.Clear();
        }
    }
}
