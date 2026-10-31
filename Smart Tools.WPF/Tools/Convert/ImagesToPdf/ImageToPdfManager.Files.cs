using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Finding images on disk for Images to PDF.</summary>
    public static partial class ImageToPdfManager
    {
        /// <summary>Everything ImageMagick reads that people use for photos and scans (the old list missed HEIC, AVIF, GIF and JFIF).</summary>
        public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif",
        };

        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

        public const string FileDialogFilter =
            "Images|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif|All files|*.*";

        /// <summary>
        /// Images in a folder (and its subfolders if asked), in natural name
        /// order with each folder's own images before its subfolders'. A
        /// folder Windows won't open is counted and skipped - the old code
        /// lost the whole folder over one. Hidden/system files and folder
        /// links are skipped.
        /// </summary>
        public static Task<ImagePdfScan> ScanFolderAsync(string folder, bool includeSubfolders, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new ImagePdfScan();
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
                                result.Files.Add(new ImagePdfFile(file.FullName, file.Length, file.LastWriteTime));
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

        /// <summary>Picked or dropped files that exist and are images, in the order given. Dropped folders are scanned.</summary>
        public static Task<List<ImagePdfFile>> ReadFilesAsync(IEnumerable<string> paths, bool includeSubfolders, CancellationToken token = default)
        {
            var list = paths.ToList();
            return Task.Run(async () =>
            {
                var found = new List<ImagePdfFile>();
                foreach (string path in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                    {
                        var scan = await ScanFolderAsync(path, includeSubfolders, token).ConfigureAwait(false);
                        found.AddRange(scan.Files);
                        continue;
                    }
                    if (!IsImage(path)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists) found.Add(new ImagePdfFile(info.FullName, info.Length, info.LastWriteTime));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        // unreadable path - skip
                    }
                }
                return found;
            }, token);
        }
    }
}
