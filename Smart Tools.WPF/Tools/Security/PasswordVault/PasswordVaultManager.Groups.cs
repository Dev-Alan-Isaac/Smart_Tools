using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Groups the entry list by tag path (Games/Launchers). Portable: no WPF.</summary>
    public sealed partial class PasswordVaultManager
    {
        public const int MaxGroupDepth = 4;

        /// <summary>The key of the section for entries without a tag.</summary>
        public const string UngroupedKey = "\0";

        /// <summary>
        /// The group an entry belongs to: its first non-empty tag, split on / and \ into levels
        /// ("Games/Launchers, Work" gives Games, Launchers). Empty = no tag = ungrouped.
        /// </summary>
        public static string[] GroupPath(string? tags)
        {
            if (string.IsNullOrWhiteSpace(tags)) return Array.Empty<string>();
            foreach (string tag in tags.Split(','))
            {
                var parts = tag.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .Take(MaxGroupDepth)
                    .ToArray();
                if (parts.Length > 0) return parts;
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// The list rows: a header for every group level followed by its entries (parent entries before sub-groups,
        /// favorites first, then by title), the ungrouped entries last. Rows under a collapsed header are left out
        /// unless <paramref name="expandAll"/>. <paramref name="collapsed"/> holds lower-cased path keys.
        /// </summary>
        public static List<object> BuildRows(IEnumerable<VaultEntry> entries, ISet<string> collapsed, bool expandAll, string ungroupedName)
        {
            var items = entries.Select(e => (Entry: e, Path: GroupPath(e.Tags))).ToList();
            // Spelling of a header (Games vs games) comes from the first entry that uses it, in the caller's order, so favorites don't decide it.
            var names = new Dictionary<string, string>();
            foreach (var (_, path) in items)
                for (int i = 0; i < path.Length; i++) names.TryAdd(KeyOf(path, i), path[i]);

            items.Sort((a, b) =>
            {
                int c = ComparePaths(a.Path, b.Path);
                if (c != 0) return c;
                c = b.Entry.Favorite.CompareTo(a.Entry.Favorite);
                return c != 0 ? c : StringComparer.CurrentCultureIgnoreCase.Compare(a.Entry.Title, b.Entry.Title);
            });

            bool anyGrouped = items.Any(i => i.Path.Length > 0);

            // Entries under each header, subgroups included.
            var counts = new Dictionary<string, int>();
            foreach (var (_, path) in items)
            {
                if (path.Length == 0) { if (anyGrouped) counts[UngroupedKey] = counts.GetValueOrDefault(UngroupedKey) + 1; continue; }
                for (int i = 0; i < path.Length; i++) counts[KeyOf(path, i)] = counts.GetValueOrDefault(KeyOf(path, i)) + 1;
            }

            var rows = new List<object>();
            var seen = new HashSet<string>();
            foreach (var (entry, path) in items)
            {
                bool blocked = false;
                if (path.Length == 0)
                {
                    if (anyGrouped)
                    {
                        if (seen.Add(UngroupedKey))
                            rows.Add(new VaultGroupRow { Name = ungroupedName, PathKey = UngroupedKey, Depth = 0, Count = counts[UngroupedKey], IsExpanded = IsOpen(UngroupedKey) });
                        blocked = !IsOpen(UngroupedKey);
                    }
                    entry.ListDepth = anyGrouped ? 1 : 0;
                }
                else
                {
                    for (int i = 0; i < path.Length && !blocked; i++)
                    {
                        string key = KeyOf(path, i);
                        if (seen.Add(key))
                            rows.Add(new VaultGroupRow { Name = names[key], PathKey = key, Depth = i, Count = counts[key], IsExpanded = IsOpen(key) });
                        if (!IsOpen(key)) blocked = true;
                    }
                    entry.ListDepth = path.Length;
                }
                if (!blocked) rows.Add(entry);
            }
            return rows;

            bool IsOpen(string key) => expandAll || !collapsed.Contains(key);
        }

        /// <summary>Every group path in use (Games, Games/Launchers, ...), each level included, sorted.</summary>
        public static List<string> ExistingGroups(IEnumerable<VaultEntry> entries)
        {
            var map = new Dictionary<string, string>();
            foreach (var e in entries)
            {
                var path = GroupPath(e.Tags);
                for (int i = 0; i < path.Length; i++) map.TryAdd(KeyOf(path, i), string.Join("/", path.Take(i + 1)));
            }
            return map.Values.OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// The tags with the group (the first non-empty tag) replaced by <paramref name="newGroup"/> (empty = ungrouped).
        /// The other tags stay. A tag list that had no group gets the new one in front.
        /// </summary>
        public static string WithGroup(string? tags, string newGroup)
        {
            string group = string.Join("/", newGroup.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).Take(MaxGroupDepth));
            var rest = (tags ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            int firstGroupTag = rest.FindIndex(t => t.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Any(p => p.Trim().Length > 0));
            if (firstGroupTag >= 0) rest.RemoveAt(firstGroupTag);
            if (group.Length > 0) rest.Insert(Math.Max(firstGroupTag, 0), group);
            return string.Join(", ", rest);
        }

        private static string KeyOf(string[] path, int level) => string.Join("/", path.Take(level + 1)).ToLowerInvariant();

        /// <summary>Segment by segment; a parent comes before its children; no tag sorts last.</summary>
        private static int ComparePaths(string[] a, string[] b)
        {
            if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 0 : a.Length == 0 ? 1 : -1;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int c = StringComparer.CurrentCultureIgnoreCase.Compare(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
