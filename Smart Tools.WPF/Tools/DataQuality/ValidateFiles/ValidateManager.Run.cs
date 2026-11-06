using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Validate Files - reading folders, running a scan, quarantine and reports.</summary>
    public sealed partial class ValidateManager
    {
        // =================================================================
        //  Queue expansion
        // =================================================================

        public sealed class QueueResult
        {
            public List<QueuedFile> Files { get; } = new();

            /// <summary>Subfolders that couldn't be opened.</summary>
            public int Inaccessible { get; set; }

            /// <summary>Junctions/linked folders not followed (the old walk followed them - loops never ended).</summary>
            public int Links { get; set; }

            /// <summary>Added files/folders that no longer exist.</summary>
            public int Missing { get; set; }
        }

        /// <summary>Lists the added files and folders off the UI thread (the old view listed them on it).</summary>
        public static Task<QueueResult> ExpandQueueAsync(IEnumerable<ValidationSource> sources, bool includeSubfolders, CancellationToken token = default)
        {
            var list = sources.ToList();
            return Task.Run(() =>
            {
                var result = new QueueResult();
                foreach (var source in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (source.IsFolder)
                    {
                        if (!Directory.Exists(source.FullPath))
                        {
                            result.Missing++;
                            continue;
                        }
                        WalkFolder(source.FullPath, includeSubfolders, result, token);
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(source.FullPath);
                        if (!info.Exists)
                        {
                            result.Missing++;
                            continue;
                        }
                        result.Files.Add(new QueuedFile(info.FullName, null, info.Length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        result.Missing++;
                    }
                }
                return result;
            }, token);
        }

        private static void WalkFolder(string root, bool includeSubfolders, QueueResult result, CancellationToken token)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            string rootFull = Path.GetFullPath(root);

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
                    result.Inaccessible++;
                    continue;
                }

                foreach (var entry in entries)
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
                    if (attrs.HasFlag(FileAttributes.System)) continue;

                    if (entry is DirectoryInfo sub)
                    {
                        if (!includeSubfolders) continue;
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            result.Links++;
                            continue;
                        }
                        pending.Push(sub);
                    }
                    else if (entry is FileInfo file)
                    {
                        long length;
                        try
                        {
                            length = file.Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }
                        result.Files.Add(new QueuedFile(file.FullName, rootFull, length));
                    }
                }
            }
        }

        // =================================================================
        //  Running
        // =================================================================

        /// <summary>Files at once for a folder's drive: SSD = cores, anything else = 2 (the view decides).</summary>
        public static int DefaultParallelism(bool solidState) => solidState ? Math.Clamp(Environment.ProcessorCount, 2, 12) : 2;

        /// <summary>
        /// Checks every pending row, updating each one live. Every file is
        /// independent: one that can't be read doesn't stop the scan.
        /// Cancelling keeps what was checked and marks the rest Cancelled
        /// (the old version threw every result away). Progress is reported
        /// at most every 100 ms (it was once per file - thousands of UI
        /// updates a second on fast drives).
        /// </summary>
        public async Task<ValidationRunResult> RunAsync(
            IReadOnlyList<FileValidationResult> rows,
            ValidationOptions options,
            IProgress<ValidationProgress>? progress = null,
            CancellationToken token = default)
        {
            var started = Stopwatch.StartNew();
            var work = rows.Where(IsPending).ToList();
            var result = new ValidationRunResult();
            int total = work.Count;
            if (total == 0) return result;

            foreach (var row in work)
            {
                row.Status = ValidationStatus.Pending;
                row.Details = "";
            }

            int done = 0;
            long lastReport = -1000;
            string current = "";
            var gate = new object();

            void Report(bool force)
            {
                lock (gate)
                {
                    long now = started.ElapsedMilliseconds;
                    if (!force && now - lastReport < 100) return;
                    lastReport = now;
                    progress?.Report(new ValidationProgress(done, total, current, done * 100.0 / total));
                }
            }

            int parallel = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : DefaultParallelism(true);
            int next = -1;
            var o = options.Clone();

            var workers = Enumerable.Range(0, Math.Min(parallel, total)).Select(_ => Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= total) return;
                    var row = work[i];
                    lock (gate) current = row.FileName;
                    row.Status = ValidationStatus.Checking;
                    try
                    {
                        CheckFile(row, o, token);
                    }
                    catch (OperationCanceledException)
                    {
                        row.Status = ValidationStatus.Cancelled;
                        row.Details = Loc.T("Str_Validate_DetNotChecked", "Not checked - the scan was cancelled.");
                        return;
                    }
                    lock (gate) done++;
                    Report(false);
                }
            }, CancellationToken.None)).ToList();

            await Task.WhenAll(workers).ConfigureAwait(false);

            foreach (var row in work)
            {
                switch (row.Status)
                {
                    case ValidationStatus.Valid: result.Valid++; break;
                    case ValidationStatus.Warning: result.Warning++; break;
                    case ValidationStatus.Corrupted: result.Corrupted++; break;
                    case ValidationStatus.Unknown: result.Unknown++; break;
                    default:
                        row.Status = ValidationStatus.Cancelled;
                        if (row.Details.Length == 0) row.Details = Loc.T("Str_Validate_DetNotChecked", "Not checked - the scan was cancelled.");
                        result.Cancelled++;
                        break;
                }
            }
            Report(true);
            result.Elapsed = started.Elapsed;
            return result;
        }

        // =================================================================
        //  Quarantine
        // =================================================================

        public sealed class QuarantineResult
        {
            public int Moved { get; set; }
            public List<string> Failed { get; } = new();
            public string? LogPath { get; set; }
        }

        /// <summary>"name (2).ext" until free.</summary>
        public static string UniquePath(string path, Func<string, bool> exists)
        {
            if (!exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!exists(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Moves files into the quarantine folder (never overwriting), and
        /// appends "original path -> new path" to "quarantine log.csv" there
        /// so every file can be put back.
        /// </summary>
        public static Task<QuarantineResult> QuarantineAsync(IReadOnlyList<FileValidationResult> rows, string folder, CancellationToken token = default)
        {
            return Task.Run(() =>
            {
                var result = new QuarantineResult();
                Directory.CreateDirectory(folder);
                var log = new StringBuilder();
                // Only damaged files are ever moved, whatever the caller passes in.
                foreach (var row in rows.Where(r => r.QuarantinedTo == null && r.Status is ValidationStatus.Corrupted or ValidationStatus.Warning))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(row.FullPath)) throw new FileNotFoundException("it no longer exists");
                        string dest = UniquePath(Path.Combine(folder, row.FileName), File.Exists);
                        File.Move(row.FullPath, dest);
                        row.QuarantinedTo = dest;
                        row.Details = Loc.F("Str_Validate_DetQuarantined", "Moved to quarantine: {0}. ", dest) + row.Details;
                        row.IsSelected = false;
                        log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{CsvEscape(row.FullPath)},{CsvEscape(dest)}");
                        result.Moved++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.Failed.Add($"{row.FileName}: {ex.Message}");
                    }
                }

                if (log.Length > 0)
                {
                    string logPath = Path.Combine(folder, "quarantine log.csv");
                    try
                    {
                        if (!File.Exists(logPath)) File.WriteAllText(logPath, "Moved at,Original path,Quarantine path" + Environment.NewLine, Encoding.UTF8);
                        File.AppendAllText(logPath, log.ToString(), Encoding.UTF8);
                        result.LogPath = logPath;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // the files are moved; the log is a convenience
                    }
                }
                return result;
            }, token);
        }

        // =================================================================
        //  Reports
        // =================================================================

        /// <summary>
        /// Writes a CSV report (UTF-8 with BOM so Excel shows accents) via a
        /// temp file. The old auto-report went next to the .exe, which is
        /// read-only under Program Files, and failed silently.
        /// </summary>
        public static string SaveReport(IEnumerable<FileValidationResult> rows, string? path = null)
        {
            path ??= Path.Combine(ReportsFolder, $"Validation report {DateTime.Now:yyyy-MM-dd HH-mm-ss}.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, BuildCsvReport(rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(temp, path, overwrite: true);
            return path;
        }
    }
}
