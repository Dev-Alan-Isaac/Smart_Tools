using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  Folder Tree Exporter: scans a folder (read-only) and writes its
    //  structure as a text tree, Markdown, JSON or CSV. It never changes
    //  the scanned folder, never follows links (no loops), lists folders it
    //  can't open instead of failing, and never replaces an earlier export.
    // =====================================================================
    public static partial class FolderTreeManager
    {
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

        private static readonly HashSet<string> NoiseFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".svn", ".hg", ".vs", ".idea", "node_modules", "bin", "obj", "__pycache__", "$RECYCLE.BIN", "System Volume Information",
        };

        // =================================================================
        //  Scanning
        // =================================================================

        public static Task<FolderTreeResult> ScanAsync(FolderTreeOptions o, IProgress<FolderTreeProgress>? progress, CancellationToken token) =>
            Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var dir = new DirectoryInfo(o.Folder);
                var root = new FolderTreeNode { Name = dir.Name.Length > 0 ? dir.Name : o.Folder, FullPath = dir.FullName, IsFolder = true, Modified = SafeTime(dir) };
                var result = new FolderTreeResult { Root = root };
                long lastReport = -1;

                void Report(string current)
                {
                    if (clock.ElapsedMilliseconds - lastReport < ReportEvery.TotalMilliseconds) return;
                    lastReport = clock.ElapsedMilliseconds;
                    progress?.Report(new FolderTreeProgress(result.Folders, result.Files, current));
                }

                // node == null: past the depth limit - only the size and file count are added up, nothing is listed.
                (long Size, long Files) Walk(DirectoryInfo folder, FolderTreeNode? node, int depth)
                {
                    token.ThrowIfCancellationRequested();
                    Report(folder.Name);

                    List<FileSystemInfo> entries;
                    try
                    {
                        entries = folder.EnumerateFileSystemInfos().ToList();
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        result.Unreadable++;
                        if (node != null) node.Note = "no access";
                        return (0, 0);
                    }

                    long total = 0, fileCount = 0;
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        FileAttributes attrs;
                        try
                        {
                            attrs = entry.Attributes;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }
                        if (!o.IncludeHidden && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                        bool isFolder = (attrs & FileAttributes.Directory) != 0;
                        if (isFolder)
                        {
                            if (o.SkipNoise && NoiseFolders.Contains(entry.Name)) continue;
                            var sub = (DirectoryInfo)entry;
                            bool isLink = (attrs & FileAttributes.ReparsePoint) != 0;
                            bool list = node != null && (o.MaxDepth == 0 || depth < o.MaxDepth);

                            FolderTreeNode? child = null;
                            if (list)
                            {
                                child = new FolderTreeNode { Name = sub.Name, FullPath = sub.FullName, IsFolder = true, Modified = SafeTime(sub) };
                                node!.Children.Add(child);
                                result.Folders++;
                            }
                            if (isLink)
                            {
                                if (child != null) child.Note = "link - not followed";
                                continue;
                            }
                            var (size, count) = Walk(sub, child, depth + 1);
                            if (child != null)
                            {
                                child.Size = size;
                                child.FileCount = count;
                            }
                            total += size;
                            fileCount += count;
                        }
                        else
                        {
                            long length = 0;
                            try
                            {
                                length = ((FileInfo)entry).Length;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                // listed with size 0; the file may be gone or locked
                            }
                            total += length;
                            fileCount++;
                            result.Bytes += length;
                            if (node != null && o.IncludeFiles && (o.MaxDepth == 0 || depth < o.MaxDepth))
                            {
                                node.Children.Add(new FolderTreeNode { Name = entry.Name, FullPath = entry.FullName, Size = length, Modified = SafeTime(entry) });
                                result.Files++;
                            }
                        }
                    }
                    return (total, fileCount);
                }

                (root.Size, root.FileCount) = Walk(dir, root, 0);
                result.Elapsed = clock.Elapsed;
                progress?.Report(new FolderTreeProgress(result.Folders, result.Files, ""));
                return result;
            }, token);

        private static DateTime SafeTime(FileSystemInfo info)
        {
            try
            {
                return info.LastWriteTime;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return default;
            }
        }

        // =================================================================
        //  Rendering
        // =================================================================

        public static string Render(FolderTreeResult r, FolderTreeOptions o) => o.Format switch
        {
            FolderTreeFormat.Markdown => RenderMarkdown(r, o),
            FolderTreeFormat.Html => RenderHtml(r, o),
            FolderTreeFormat.Json => RenderJson(r, o),
            FolderTreeFormat.Xml => RenderXml(r, o),
            FolderTreeFormat.Yaml => RenderYaml(r, o),
            FolderTreeFormat.Csv => RenderCsv(r, o),
            FolderTreeFormat.Paths => RenderPaths(r),
            _ => RenderText(r, o),   // Text, and the preview of the drawn formats (PDF, picture)
        };

        public static string FileExtension(FolderTreeFormat f) => f switch
        {
            FolderTreeFormat.Markdown => ".md",
            FolderTreeFormat.Html => ".html",
            FolderTreeFormat.Json => ".json",
            FolderTreeFormat.Xml => ".xml",
            FolderTreeFormat.Yaml => ".yaml",
            FolderTreeFormat.Csv => ".csv",
            FolderTreeFormat.Pdf => ".pdf",
            FolderTreeFormat.Image => ".png",
            _ => ".txt",
        };

        internal static string Meta(FolderTreeNode n, FolderTreeOptions o)
        {
            var parts = new List<string>();
            if (o.ShowSize) parts.Add(CopyQueueManager.FormatSize(n.Size));
            if (o.ShowCount && n.IsFolder) parts.Add(FilesText.Files((int)Math.Min(n.FileCount, int.MaxValue)));
            if (o.ShowDate && n.Modified != default) parts.Add(n.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (n.Note != null) parts.Add(n.Note);
            return parts.Count == 0 ? "" : "  [" + string.Join(" · ", parts) + "]";
        }

        internal static IEnumerable<FolderTreeNode> Sorted(FolderTreeNode n) =>
            n.Children.OrderByDescending(c => c.IsFolder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        internal static string Footer(FolderTreeResult r) =>
            $"{FilesText.Folders(r.Folders)}, {FilesText.Files(r.Files)}, {CopyQueueManager.FormatSize(r.Bytes)}"
            + (r.Unreadable > 0 ? Loc.N(r.Unreadable, "Str_FolderTree_FooterUnreadableOne", " ({0:N0} folder could not be opened)", "Str_FolderTree_FooterUnreadableMany", " ({0:N0} folders could not be opened)", r.Unreadable) : "");

        private static string RenderText(FolderTreeResult r, FolderTreeOptions o)
        {
            var sb = new StringBuilder();
            sb.Append(r.Root.FullPath).Append(Meta(r.Root, o)).AppendLine();
            void Emit(FolderTreeNode n, string prefix)
            {
                var kids = Sorted(n).ToList();
                for (int i = 0; i < kids.Count; i++)
                {
                    bool last = i == kids.Count - 1;
                    var k = kids[i];
                    sb.Append(prefix).Append(last ? "└── " : "├── ").Append(k.Name).Append(k.IsFolder ? "/" : "").Append(Meta(k, o)).AppendLine();
                    if (k.IsFolder) Emit(k, prefix + (last ? "    " : "│   "));
                }
            }
            Emit(r.Root, "");
            sb.AppendLine().AppendLine(Footer(r));
            return sb.ToString();
        }

        private static string RenderMarkdown(FolderTreeResult r, FolderTreeOptions o)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(MdName(r.Root.Name)).AppendLine().AppendLine();
            sb.Append("`").Append(r.Root.FullPath.Replace('`', '\'')).Append('`').AppendLine().AppendLine();
            void Emit(FolderTreeNode n, int level)
            {
                foreach (var k in Sorted(n))
                {
                    sb.Append(' ', level * 2).Append("- ");
                    sb.Append(k.IsFolder ? $"**{MdName(k.Name)}/**" : MdName(k.Name));
                    string meta = Meta(k, o).Trim();
                    if (meta.Length > 0) sb.Append(" _").Append(meta.Trim('[', ']')).Append('_');
                    sb.AppendLine();
                    if (k.IsFolder) Emit(k, level + 1);
                }
            }
            Emit(r.Root, 0);
            sb.AppendLine().AppendLine(Footer(r));
            return sb.ToString();
        }

        private static string MdName(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            foreach (char c in name)
            {
                if ("\\`*_{}[]<>()#+-.!|".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string RenderJson(FolderTreeResult r, FolderTreeOptions o)
        {
            object Node(FolderTreeNode n)
            {
                var d = new Dictionary<string, object?>
                {
                    ["name"] = n.Name,
                    ["type"] = n.IsFolder ? "folder" : "file",
                };
                if (o.ShowSize) d["size"] = n.Size;
                if (o.ShowCount && n.IsFolder) d["fileCount"] = n.FileCount;
                if (o.ShowDate && n.Modified != default) d["modified"] = n.Modified.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                if (n.Note != null) d["note"] = n.Note;
                if (n.IsFolder) d["children"] = Sorted(n).Select(Node).ToList();
                return d;
            }
            return JsonSerializer.Serialize(Node(r.Root), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        private static string RenderCsv(FolderTreeResult r, FolderTreeOptions o)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Path,Name,Type,Depth,Size,Modified,Note,FileCount");
            void Emit(FolderTreeNode n, string rel, int depth)
            {
                foreach (var k in Sorted(n))
                {
                    string path = rel.Length == 0 ? k.Name : rel + "/" + k.Name;
                    sb.AppendJoin(',',
                        Csv(path), Csv(k.Name), k.IsFolder ? "Folder" : "File", depth.ToString(CultureInfo.InvariantCulture),
                        o.ShowSize ? k.Size.ToString(CultureInfo.InvariantCulture) : "",
                        o.ShowDate && k.Modified != default ? k.Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "",
                        Csv(k.Note ?? ""),
                        o.ShowCount && k.IsFolder ? k.FileCount.ToString(CultureInfo.InvariantCulture) : "").AppendLine();
                    if (k.IsFolder) Emit(k, path, depth + 1);
                }
            }
            Emit(r.Root, "", 1);
            return sb.ToString();
        }

        /// <summary>Quoted when needed, and a leading = + - @ is defused so Excel doesn't run a file name as a formula.</summary>
        private static string Csv(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        // =================================================================
        //  Saving
        // =================================================================

        /// <summary>
        /// Writes under a temporary name, then moves it into place without overwriting: if the name is taken
        /// (or taken meanwhile) the next free "name (2)" is used. Returns the file actually written.
        /// </summary>
        public static Task<string> SaveAsync(string content, string path) =>
            SaveCoreAsync(temp => File.WriteAllText(temp, content, new UTF8Encoding(true)), path);   // BOM so Excel and Notepad read accents right

        /// <summary>The same never-overwrite save for a binary result (PDF, picture).</summary>
        public static Task<string> SaveBytesAsync(byte[] data, string path) =>
            SaveCoreAsync(temp => File.WriteAllBytes(temp, data), path);

        private static Task<string> SaveCoreAsync(Action<string> writeTemp, string path) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Directory.CreateDirectory(folder);
                string temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    writeTemp(temp);
                    for (int attempt = 0; ; attempt++)
                    {
                        string target = SafeFileOps.UniquePath(path);
                        try
                        {
                            File.Move(temp, target, overwrite: false);
                            return target;
                        }
                        catch (IOException) when (File.Exists(target) && attempt < 20)
                        {
                            // someone took the name between the check and the move - try the next one
                        }
                    }
                }
                finally
                {
                    SafeFileOps.DeleteQuietly(temp);
                }
            });
    }
}
