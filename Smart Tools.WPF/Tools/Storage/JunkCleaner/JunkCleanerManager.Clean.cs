using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Deleting junk for the Junk File Cleaner.</summary>
    public static partial class JunkCleanerManager
    {
        public static IRecycleBin DefaultRecycleBin { get; } = new ShellRecycleBin();

        /// <summary>
        /// Deletes the ticked files (and empties the Recycle Bin if ticked)
        /// off the UI thread - the old clean ran on the UI thread and froze
        /// the window on a big temp folder. A file that changed since the
        /// scan is left alone; one that's open in another program is
        /// skipped and said so; folders the clean emptied are removed.
        /// Deleted files leave their row; the rest keep the reason.
        /// </summary>
        public static Task<JunkCleanResult> CleanAsync(
            IReadOnlyList<JunkGroup> groups,
            IRecycleBin? recycleBin = null,
            IProgress<JunkProgress>? progress = null,
            CancellationToken token = default) =>
            Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var result = new JunkCleanResult();
                recycleBin ??= DefaultRecycleBin;
                bool admin = IsAdministrator();
                var files = groups.Where(g => !g.IsRecycleBin)
                    .SelectMany(g => g.Items.Where(f => f.IsSelected && f.Status != JunkFileStatus.Deleted))
                    .ToList();
                var bin = groups.FirstOrDefault(g => g.IsRecycleBin && g.SelectedCount > 0);
                int total = files.Count + (bin != null ? 1 : 0);
                var emptied = new Dictionary<JunkGroup, HashSet<string>>();
                int done = 0;
                var throttle = Stopwatch.StartNew();

                foreach (var file in files)
                {
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    var group = file.Group!;
                    try
                    {
                        var info = new FileInfo(file.FullPath);
                        if (!info.Exists)
                        {
                            file.Status = JunkFileStatus.Deleted;   // already gone - nothing to free
                        }
                        else if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LastWriteTime != file.Modified || info.Length != file.SizeBytes)
                        {
                            // Rewritten since the scan: it may not be junk any more.
                            file.Status = JunkFileStatus.Changed;
                            file.Message = "Changed after the scan - left alone. Scan again to include it.";
                            result.Changed++;
                        }
                        else
                        {
                            if (info.IsReadOnly) info.IsReadOnly = false;
                            info.Delete();
                            file.Status = JunkFileStatus.Deleted;
                            result.Deleted++;
                            result.BytesFreed += file.SizeBytes;
                            if (group.Location.RemoveEmptyFolders)
                            {
                                if (!emptied.TryGetValue(group, out var dirs)) emptied[group] = dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                dirs.Add(info.DirectoryName!);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var (status, message) = Classify(ex, group.Location.NeedsAdmin, admin);
                        file.Status = status;
                        file.Message = message;
                        switch (status)
                        {
                            case JunkFileStatus.Deleted: break;
                            case JunkFileStatus.InUse: result.InUse++; break;
                            case JunkFileStatus.NeedsAdmin: result.NeedsAdmin++; break;
                            default: result.Failed++; break;
                        }
                    }
                    done++;
                    if (progress != null && (done == files.Count || throttle.ElapsedMilliseconds >= 100))
                    {
                        throttle.Restart();
                        progress.Report(new JunkProgress(done * 100.0 / total, $"Deleting… {done:N0} of {total:N0}"));
                    }
                }

                if (bin != null && !result.Cancelled)
                {
                    progress?.Report(new JunkProgress(done * 100.0 / Math.Max(1, total), "Emptying the Recycle Bin…", Indeterminate: true));
                    try
                    {
                        recycleBin.Empty();
                        result.RecycleBinEmptied = true;
                        result.BytesFreed += bin.RecycleBinBytes;
                        bin.RecycleBinItems = 0;
                        bin.RecycleBinBytes = 0;
                    }
                    catch (Exception ex)
                    {
                        result.RecycleBinError = ex.Message;
                    }
                }

                foreach (var (group, dirs) in emptied) result.FoldersRemoved += RemoveEmptyFolders(group.Location.Roots, dirs);

                foreach (var g in groups)
                {
                    if (!g.IsRecycleBin) g.Items = g.Items.Where(f => f.Status != JunkFileStatus.Deleted).ToList();
                    g.Refresh();
                }
                result.Elapsed = clock.Elapsed;
                progress?.Report(new JunkProgress(100, "Done"));
                return result;
            });

        /// <summary>Tells apart "open in another program", "needs administrator" and real failures (the old clean lumped them all into one error count).</summary>
        public static (JunkFileStatus Status, string Message) Classify(Exception ex, bool locationNeedsAdmin, bool isAdmin)
        {
            const int SharingViolation = 32, LockViolation = 33;
            int code = ex.HResult & 0xFFFF;
            return ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => (JunkFileStatus.Deleted, ""),
                IOException when code is SharingViolation or LockViolation => (JunkFileStatus.InUse, "Open in another program - skipped. Close it and clean again."),
                UnauthorizedAccessException when locationNeedsAdmin && !isAdmin => (JunkFileStatus.NeedsAdmin, "Windows only lets an administrator delete this. Run Smart Tools as administrator."),
                UnauthorizedAccessException => (JunkFileStatus.Failed, "Windows didn't allow it to be deleted (it may be in use by a system program)."),
                _ => (JunkFileStatus.Failed, ex.Message.Split('\n')[0].Trim()),
            };
        }

        /// <summary>
        /// Removes folders the clean emptied, and parents that became empty,
        /// never a root itself and never through a link. Returns how many.
        /// </summary>
        public static int RemoveEmptyFolders(IEnumerable<string> roots, IEnumerable<string> touched)
        {
            var rootSet = new HashSet<string>(roots.Select(r => Path.GetFullPath(r).TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (string start in touched.OrderByDescending(d => d.Length))
            {
                string? dir = Path.GetFullPath(start).TrimEnd('\\', '/');
                while (dir != null && !rootSet.Contains(dir) && rootSet.Any(r => dir.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.EnumerateFileSystemInfos().Any()) break;
                        info.Delete();
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return removed;
        }

        // =================================================================
        //  The Recycle Bin (shell)
        // =================================================================

        private sealed class ShellRecycleBin : IRecycleBin
        {
            public (long Items, long Bytes)? Query()
            {
                if (!OperatingSystem.IsWindows()) return null;
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : null;
            }

            public void Empty()
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Recycle Bin is a Windows feature.");
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                // 0x8000FFFF (E_UNEXPECTED) means it was already empty.
                if (hr != 0 && unchecked((uint)hr) != 0x8000FFFF)
                    throw new IOException($"Windows couldn't empty it (error 0x{hr:X8}).");
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

            [StructLayout(LayoutKind.Sequential)]
            private struct SHQUERYRBINFO
            {
                public int cbSize;
                public long i64Size;
                public long i64NumItems;
            }

            private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
        }
    }
}
