using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Hosts File Editor - loads, checks and saves C:\Windows\System32\drivers\etc\hosts. Every save keeps a copy of
    /// the previous file in %AppData%\Smart Tools\hosts-backups (the newest ten). Writing the file needs administrator
    /// rights; without them <see cref="Save"/> says so instead of failing halfway.
    /// </summary>
    public sealed partial class HostsEditorManager
    {
        private const int KeepBackups = 10;

        public static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

        public static string BackupFolder => Path.Combine(AppPaths.DataRoot, "hosts-backups");

        public List<HostsRow> Rows { get; private set; } = new();

        public IEnumerable<HostsRow> Entries => Rows.Where(r => r.Kind == HostsRowKind.Entry);

        public static bool IsAdministrator
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>Reads the hosts file (or <paramref name="path"/>, for a backup). Throws IOException / UnauthorizedAccessException.</summary>
        public void Load(string? path = null)
        {
            using var stream = new FileStream(path ?? HostsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
            Rows = ParseHosts(lines);
        }

        public void Add(HostsRow row) => Rows.Add(row);

        public void Remove(HostsRow row) => Rows.Remove(row);

        /// <summary>Backs up the current file, then writes the rows. <paramref name="error"/> is plain text for <see cref="HostsSaveResult.Failed"/>.</summary>
        public HostsSaveResult Save(out string? error)
        {
            error = null;
            if (Entries.Any(r => Validate(r) != HostsProblem.None)) return HostsSaveResult.HasProblems;
            try
            {
                Backup();
                // Written in place (not temp + move) so the file keeps its permissions.
                File.WriteAllText(HostsPath, FormatHosts(Rows), new UTF8Encoding(false));
            }
            catch (UnauthorizedAccessException)
            {
                return HostsSaveResult.NeedsAdministrator;
            }
            catch (IOException ex)
            {
                error = ex.Message;
                return HostsSaveResult.Failed;
            }

            foreach (var row in Rows)
            {
                row.Raw = FormatRow(row);
                row.Modified = false;
            }
            return HostsSaveResult.Saved;
        }

        private static void Backup()
        {
            if (!File.Exists(HostsPath)) return;
            try
            {
                Directory.CreateDirectory(BackupFolder);
                File.Copy(HostsPath, Path.Combine(BackupFolder, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}.bak"), overwrite: true);
                foreach (var old in ListBackups().Skip(KeepBackups))
                    File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A backup problem must not stop the save the user asked for.
            }
        }

        /// <summary>Backup files, newest first.</summary>
        public static List<string> ListBackups()
        {
            try
            {
                return Directory.Exists(BackupFolder)
                    ? Directory.EnumerateFiles(BackupFolder, "hosts-*.bak").OrderByDescending(f => f, StringComparer.Ordinal).ToList()
                    : new List<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        /// <summary>Clears Windows' cached name lookups so a changed hosts file applies at once.</summary>
        public static async Task<bool> FlushDnsAsync()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false });
                if (process == null) return false;
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
    }
}
