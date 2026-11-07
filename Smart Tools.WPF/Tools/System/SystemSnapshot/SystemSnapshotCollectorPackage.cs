using System.IO;
using System.IO.Compression;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The stand-alone System Snapshot Collector (the <c>SystemSnapshot.Collector</c> project) as it ships inside the app:
    /// one zip with a README, a Windows 10/11 program that needs nothing installed, and a Windows 7+ program. This class
    /// unpacks it into a new folder on a USB drive or anywhere else, to scan a PC that does not have Smart Tools.
    /// (Not part of the files the Collector itself links, so it may use WPF-era APIs.)
    /// </summary>
    public static class SystemSnapshotCollectorPackage
    {
        /// <summary>Where the build copies the zip (see the SystemSnapshot.Collector item in the project file).</summary>
        public static string ZipPath => Path.Combine(AppContext.BaseDirectory, "Engines", "SystemSnapshot", "SystemSnapshot-Collector.zip");

        /// <summary>False in a build made without the Collector's <c>dist</c> folder.</summary>
        public static bool IsAvailable => File.Exists(ZipPath);

        private const int BufferSize = 1 << 20;

        /// <summary>
        /// Unpacks the collector into a NEW folder inside <paramref name="destination"/> (a taken name becomes "(2)"; nothing
        /// that exists is touched). Written under a temporary name and renamed when complete, so a cancel, a full stick or an
        /// unplugged drive never leaves a half-made folder that looks finished. The result's <c>ExePath</c> is the new folder.
        /// </summary>
        public static Task<InstallResult> SaveAsync(string destination, IProgress<CopyProgress>? progress, CancellationToken token) =>
            Task.Run(() =>
            {
                string? temp = null;
                try
                {
                    if (!IsAvailable) return new InstallResult(false, false, Loc.T("Str_SysSnap_CollectorMissing", "The collector is not included in this build."), null);

                    string target = SafeFileOps.UniquePath(Path.Combine(destination, Loc.T("Str_SysSnap_CollectorFolder", "System Snapshot Collector")));
                    temp = target + ".part-" + Guid.NewGuid().ToString("N")[..8];

                    using var zip = ZipFile.OpenRead(ZipPath);
                    long total = zip.Entries.Sum(e => e.Length), done = 0;
                    Directory.CreateDirectory(temp);
                    string tempRoot = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                    var buffer = new byte[BufferSize];

                    foreach (var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();

                        // The zip keeps everything under one top folder; the new folder replaces it.
                        string relative = StripTopFolder(entry.FullName);
                        if (relative.Length == 0) continue;

                        string path = Path.GetFullPath(Path.Combine(temp, relative));
                        if (!path.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the new folder

                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                        {
                            Directory.CreateDirectory(path);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using (var input = entry.Open())
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, read);
                                done += read;
                                progress?.Report(new CopyProgress(done, total, Path.GetFileName(path)));
                            }
                            output.Flush(flushToDisk: true);   // a stick can be pulled right after "Done"
                        }
                    }

                    Directory.Move(temp, target);
                    temp = null;
                    return new InstallResult(true, false, "", target);
                }
                catch (OperationCanceledException)
                {
                    return new InstallResult(false, true, "", null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    return new InstallResult(false, false, ex.Message, null);
                }
                finally
                {
                    if (temp != null)
                    {
                        try { Directory.Delete(temp, recursive: true); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover ".part-" folder is harmless */ }
                    }
                }
            }, CancellationToken.None);

        /// <summary>"Top\a\b.exe" or "Top/a/b.exe" becomes "a\b.exe"; a bare top folder becomes "".</summary>
        internal static string StripTopFolder(string entryName)
        {
            string name = entryName.Replace('/', '\\').TrimStart('\\');
            int slash = name.IndexOf('\\');
            return slash < 0 ? "" : name[(slash + 1)..];
        }
    }
}
