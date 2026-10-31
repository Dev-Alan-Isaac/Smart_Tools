using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Finding videos on disk for the Video Stitcher (the same video types as Video Replay).</summary>
    public static partial class VideoEditorManager
    {
        public static string FileDialogFilter => VideoReplayManager.FileDialogFilter;

        /// <summary>
        /// Videos in a folder (and its subfolders if asked), in natural name
        /// order with each folder's own clips before its subfolders'. A
        /// folder Windows won't open is counted and skipped - the old code
        /// showed a dialog and dropped the whole folder. Hidden/system files
        /// and folder links are skipped.
        /// </summary>
        public static Task<EditFolderScan> ScanFolderAsync(string folder, bool includeSubfolders, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new EditFolderScan();
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
                            if (includeSubfolders && !attrs.HasFlag(FileAttributes.ReparsePoint)) subfolders.Add(sub);
                        }
                        else if (entry is FileInfo file && VideoReplayManager.IsVideo(file.Name))
                        {
                            try
                            {
                                result.Files.Add(new EditFile(file.FullName, file.Length, file.LastWriteTime));
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

        /// <summary>Picked or dropped files: videos kept in the order given, anything else counted as skipped.</summary>
        public static Task<(List<EditFile> Files, int Skipped)> ReadFilesAsync(IEnumerable<string> paths, CancellationToken token = default)
        {
            var list = paths.ToList();
            return Task.Run(() =>
            {
                var found = new List<EditFile>();
                int skipped = 0;
                foreach (string path in list)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists && VideoReplayManager.IsVideo(path)) found.Add(new EditFile(info.FullName, info.Length, info.LastWriteTime));
                        else skipped++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        skipped++;
                    }
                }
                return (found, skipped);
            }, token);
        }
    }
}
