using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Finding PDFs on disk for the PDF Toolkit.</summary>
    public static partial class PdfToolkitManager
    {
        public const string FileDialogFilter = "PDF files|*.pdf|All files|*.*";

        public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// PDFs in a folder (and its subfolders if asked), in natural name
        /// order with each folder's own files before its subfolders'. A
        /// folder Windows won't open is counted and skipped. Hidden/system
        /// files and folder links are skipped, and so are this tool's own
        /// unfinished ".creating-…tmp" files.
        /// </summary>
        public static Task<PdfFolderScan> ScanFolderAsync(string folder, bool includeSubfolders, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var result = new PdfFolderScan();
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
                        else if (entry is FileInfo file && IsPdf(file.Name))
                        {
                            try
                            {
                                result.Files.Add(new PdfFileFound(file.FullName, file.Length, file.LastWriteTime));
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

        /// <summary>Picked or dropped paths: PDFs kept in the order given, folders scanned, anything else skipped.</summary>
        public static Task<(List<PdfFileFound> Files, int Skipped, int InaccessibleFolders)> ReadPathsAsync(IEnumerable<string> paths, bool includeSubfolders, CancellationToken token = default)
        {
            var list = paths.ToList();
            return Task.Run(async () =>
            {
                var found = new List<PdfFileFound>();
                int skipped = 0, inaccessible = 0;
                foreach (string path in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                    {
                        var scan = await ScanFolderAsync(path, includeSubfolders, token).ConfigureAwait(false);
                        found.AddRange(scan.Files);
                        inaccessible += scan.InaccessibleFolders;
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists && IsPdf(path)) found.Add(new PdfFileFound(info.FullName, info.Length, info.LastWriteTime));
                        else skipped++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        skipped++;
                    }
                }
                return (found, skipped, inaccessible);
            }, token);
        }
    }
}
