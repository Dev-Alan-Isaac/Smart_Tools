using Microsoft.VisualBasic.FileIO;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>A folder (or whole branch) that holds no files at all. <see cref="Nested"/> = how many empty folders are inside it.</summary>
    public sealed record EmptyFolder(string Path, int Nested)
    {
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
        public string Parent => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\', '/')) ?? "";
        public string Detail => Nested > 0 ? $"{Parent}  ·  plus {Nested} empty folder{(Nested == 1 ? "" : "s")} inside" : Parent;
    }

    public sealed record EmptyFolderScan(List<EmptyFolder> Folders, int Inaccessible, int FoldersLooked);

    public sealed record EmptyFolderDelete(int Deleted, List<string> Failed, int NoLongerEmpty);

    /// <summary>
    /// Finds folders with nothing in them: no files (hidden and system files count), and every folder inside is also empty.
    /// Only the outermost empty folder of a branch is listed. Links are never followed (a link counts as something being
    /// there), a folder that can't be read counts as not empty, and the folder you chose is never listed itself.
    /// Deleting goes to the Recycle Bin. No WPF types.
    /// </summary>
    public static class EmptyFolderManager
    {
        private static readonly EnumerationOptions Everything = new()
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        public static EmptyFolderScan Find(IEnumerable<string> roots, CancellationToken token = default)
        {
            var found = new List<EmptyFolder>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int inaccessible = 0, looked = 0;

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string full = Path.GetFullPath(root);
                if (!seen.Add(full)) continue;
                Scan(full, isRoot: true, found, ref inaccessible, ref looked, token);
            }

            // A folder added twice (once inside another) is listed once.
            var unique = found
                .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return new EmptyFolderScan(unique, inaccessible, looked);
        }

        /// <summary>Returns (empty, emptyFoldersInside). When this folder is not empty, its empty children are added to <paramref name="found"/>.</summary>
        private static (bool Empty, int Nested) Scan(string dir, bool isRoot, List<EmptyFolder> found, ref int inaccessible, ref int looked, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            looked++;
            try
            {
                bool hasFiles = Directory.EnumerateFiles(dir, "*", Everything).Any();
                return Descend(dir, hasFiles, isRoot, found, ref inaccessible, ref looked, token);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                inaccessible++;
                return (false, 0);
            }
        }

        private static (bool Empty, int Nested) Descend(string dir, bool hasFiles, bool isRoot, List<EmptyFolder> found, ref int inaccessible, ref int looked, CancellationToken token)
        {
            var candidates = new List<EmptyFolder>();
            bool allChildrenEmpty = true;
            int nested = 0;

            // Subfolders are visited even when this folder holds files, because they can be empty on their own.
            foreach (string sub in Directory.EnumerateDirectories(dir, "*", Everything))
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(sub); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    inaccessible++;
                    allChildrenEmpty = false;
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { allChildrenEmpty = false; continue; }   // a link: leave it alone

                var (empty, childNested) = Scan(sub, isRoot: false, candidates, ref inaccessible, ref looked, token);
                if (empty)
                {
                    candidates.Add(new EmptyFolder(sub, childNested));
                    nested += childNested + 1;
                }
                else allChildrenEmpty = false;
            }

            bool thisEmpty = !hasFiles && allChildrenEmpty && !isRoot;
            if (!thisEmpty)
            {
                // Keep the outermost empty branches under this folder, drop the ones nested in another candidate.
                // Walk up each candidate's parents and look them up in a set (was every candidate against every other).
                var paths = new HashSet<string>(candidates.Select(c => c.Path.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidates)
                {
                    bool nestedInOther = false;
                    string? up = Path.GetDirectoryName(c.Path.TrimEnd('\\', '/'));
                    while (!string.IsNullOrEmpty(up) && up.Length > dir.TrimEnd('\\', '/').Length)
                    {
                        if (paths.Contains(up)) { nestedInOther = true; break; }
                        up = Path.GetDirectoryName(up);
                    }
                    if (!nestedInOther) found.Add(c);
                }
            }
            return thisEmpty ? (true, nested) : (false, 0);
        }

        private static bool IsInside(string path, string parent) =>
            path.Length > parent.Length && path.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the folder still holds no files anywhere inside it (checked again just before deleting).</summary>
        public static bool IsStillEmpty(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return false;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = true };
                return !Directory.EnumerateFiles(path, "*", options).Any();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>Sends the folders to the Recycle Bin. A folder that is no longer empty is left alone.</summary>
        public static EmptyFolderDelete Delete(IEnumerable<string> folders, CancellationToken token = default)
        {
            int deleted = 0, notEmpty = 0;
            var failed = new List<string>();
            // Deepest first, so a branch and something inside it both selected is handled once.
            foreach (string path in folders.OrderByDescending(p => p.Length))
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(path)) { deleted++; continue; }   // already gone (e.g. inside a branch removed a moment ago)
                if (!IsStillEmpty(path)) { notEmpty++; continue; }
                try
                {
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
                {
                    failed.Add(path);
                }
            }
            return new EmptyFolderDelete(deleted, failed, notEmpty);
        }
    }
}
