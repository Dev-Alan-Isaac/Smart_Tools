using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// System Snapshot - reads a lot about this PC (system, network, firewall, services, startup, software...) into plain tables
    /// that can be browsed and exported, so the same setup can be rebuilt on another PC. It only READS: nothing is changed,
    /// and passwords, Wi-Fi keys, product keys and saved credentials are never read.
    /// This half runs the scan and has the shared helpers; the collectors are in <c>SystemSnapshotManager.Collect*.cs</c>,
    /// the exporters in <c>SystemSnapshotManager.Export.cs</c>. No WPF types here.
    /// </summary>
    public sealed partial class SystemSnapshotManager
    {
        public const string SettingsName = "system_snapshot";
        public const string Hidden = "(hidden)";

        /// <summary>The groups, in scan order. Each group is one checkbox and one step of progress.</summary>
        public static readonly IReadOnlyList<SnapshotGroup> Groups = new[]
        {
            new SnapshotGroup("system", "System and hardware", "Laptop", true, new[] { "os", "systeminfo", "drives", "disks", "users", "security" }),
            new SnapshotGroup("network", "Network and ports", "Lan", true, new[] { "adapters", "routes", "network-config", "ports", "hosts" }),
            new SnapshotGroup("firewall", "Firewall", "ShieldHalfFull", true, new[] { "firewall-profiles", "firewall-rules" }),
            new SnapshotGroup("services", "Services, startup and tasks", "CogSyncOutline", true, new[] { "services", "startup", "tasks" }),
            new SnapshotGroup("software", "Software and runtimes", "ApplicationCogOutline", true, new[] { "programs", "runtimes", "features", "env", "path" }),
            new SnapshotGroup("certs", "Certificates", "CertificateOutline", true, new[] { "certs" }),
            new SnapshotGroup("drivers", "Third-party drivers (slow)", "ChipOutline", false, new[] { "drivers" }),
        };

        private readonly Redactor _redactor = new();

        // The English title and icon of every section, so a failed group still leaves a named (and explained) table behind.
        private static readonly Dictionary<string, (string Title, string Icon)> SectionInfo = new()
        {
            ["os"] = ("Windows and PC", "MicrosoftWindows"),
            ["systeminfo"] = ("systeminfo (raw)", "TextBoxOutline"),
            ["drives"] = ("Drives and volumes", "Harddisk"),
            ["disks"] = ("Physical disks", "Harddisk"),
            ["users"] = ("Local users and groups", "AccountMultipleOutline"),
            ["security"] = ("Security software", "ShieldCheckOutline"),
            ["adapters"] = ("Network adapters", "IpNetworkOutline"),
            ["routes"] = ("Routes", "SignDirection"),
            ["network-config"] = ("Network settings", "LanConnect"),
            ["ports"] = ("Listening ports", "LanPending"),
            ["hosts"] = ("Hosts file", "FileDocumentEditOutline"),
            ["firewall-profiles"] = ("Firewall profiles", "ShieldHalfFull"),
            ["firewall-rules"] = ("Firewall rules", "ShieldLockOutline"),
            ["services"] = ("Services", "CogSyncOutline"),
            ["startup"] = ("Startup items", "RocketLaunchOutline"),
            ["tasks"] = ("Scheduled tasks", "CalendarClock"),
            ["programs"] = ("Installed programs", "ApplicationOutline"),
            ["runtimes"] = ("Runtimes and dependencies", "PackageVariantClosed"),
            ["features"] = ("Windows features", "ToggleSwitchOutline"),
            ["env"] = ("Environment variables", "VariableBox"),
            ["path"] = ("PATH folders", "FolderArrowRightOutline"),
            ["certs"] = ("Certificates (personal stores)", "CertificateOutline"),
            ["drivers"] = ("Third-party drivers", "ChipOutline"),
        };

        public static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // =================================================================
        //  The scan
        // =================================================================

        /// <summary>
        /// Runs the chosen groups one after another on a worker thread. A group that fails becomes tables with a note and the
        /// scan goes on. Cancelling returns what was finished so far (<see cref="SystemSnapshot.Complete"/> = false).
        /// </summary>
        public Task<SystemSnapshot> ScanAsync(SnapshotOptions options, IProgress<SnapshotProgress>? progress, CancellationToken token) =>
            Task.Run(() => ScanCore(options, progress, token));

        private SystemSnapshot ScanCore(SnapshotOptions options, IProgress<SnapshotProgress>? progress, CancellationToken token)
        {
            var chosen = Groups.Where(g => options.Groups.Contains(g.Id)).ToList();
            var tables = new List<SnapshotTable>();
            bool complete = true;

            for (int i = 0; i < chosen.Count; i++)
            {
                var group = chosen[i];
                if (token.IsCancellationRequested) { complete = false; break; }
                progress?.Report(new SnapshotProgress(i, chosen.Count, group.Title, null));

                List<SnapshotTable> made;
                try
                {
                    made = Collect(group.Id, options, token);
                }
                catch (OperationCanceledException)
                {
                    complete = false;
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Info($"System Snapshot - {group.Id}: {ex.Message}");
                    made = group.SectionIds.Select(id => Failed(id, ex.Message)).ToList();
                }

                tables.AddRange(made);
                progress?.Report(new SnapshotProgress(i + 1, chosen.Count, group.Title, made));
            }

            return new SystemSnapshot(DateTime.Now, Environment.MachineName, Environment.UserName, IsAdministrator(), complete, tables);
        }

        private List<SnapshotTable> Collect(string groupId, SnapshotOptions options, CancellationToken token) => groupId switch
        {
            "system" => CollectSystem(token),
            "network" => CollectNetwork(token),
            "firewall" => CollectFirewall(options, token),
            "services" => CollectServices(options, token),
            "software" => CollectSoftware(token),
            "certs" => CollectCertificates(token),
            "drivers" => CollectDrivers(token),
            _ => new List<SnapshotTable>(),
        };

        // =================================================================
        //  Building tables
        // =================================================================

        private static SnapshotTable Make(string id, string[] headers, List<string[]> rows, string? note = null)
        {
            var (title, icon) = SectionInfo.TryGetValue(id, out var info) ? info : (id, "TableLarge");
            return new SnapshotTable(id, title, icon, headers, rows, note);
        }

        /// <summary>A section that could not be read: an empty table that says why.</summary>
        private static SnapshotTable Failed(string id, string reason) => Make(id, new[] { "Item" }, new List<string[]>(), "Could not be read: " + reason);

        /// <summary>A two-column Item / Value table.</summary>
        private static SnapshotTable Facts(string id, List<(string Item, string Value)> facts, string? note = null) =>
            Make(id, new[] { "Item", "Value" }, facts.Select(f => new[] { f.Item, f.Value }).ToList(), note);

        private static string Note(params string?[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        public static string Text(object? value) => value switch
        {
            null => "",
            bool b => b ? "Yes" : "No",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString()?.ReplaceLineEndings(" ").Trim() ?? "",
        };

        public static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
            return bytes.ToString(u == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[u];
        }

        // =================================================================
        //  Secrets: never read on purpose, and masked if they show up in a value
        // =================================================================

        /// <summary>
        /// Env vars, task arguments and startup commands are normal things to list, but people do put tokens in them.
        /// A value is masked when its name says it is a secret, or when an argument looks like password=..., token=...
        /// </summary>
        public sealed class Redactor
        {
            private static readonly Regex SecretName = new(
                @"(^|[_\-.])(KEY|TOKEN|SECRET|PASSWORD|PASSWD|PWD|PASS|CREDENTIALS?)($|[_\-.])|CONNECTIONSTRING|APIKEY|ACCESSKEY|PRIVATEKEY",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            private static readonly Regex SecretArgument = new(
                @"(?i)((?:password|passwd|pwd|pass|token|secret|api[-_]?key)\s*[=:]\s*)(""[^""]*""|'[^']*'|\S+)",
                RegexOptions.Compiled);

            public int Count { get; private set; }

            public string Value(string name, string value)
            {
                if (value.Length > 0 && SecretName.IsMatch(name)) { Count++; return Hidden; }
                return Text(value);
            }

            public string Text(string text)
            {
                if (text.Length == 0) return text;
                string result = SecretArgument.Replace(text, m => { Count++; return m.Groups[1].Value + Hidden; });
                return result;
            }
        }

        private string? RedactionNote() => _redactor.Count > 0 ? $"{_redactor.Count} value(s) that look like secrets are shown as {Hidden}." : null;

        // =================================================================
        //  WMI and processes
        // =================================================================

        /// <summary>Runs a WMI query and gives back every result; a missing class or no access gives an empty list.</summary>
        private static List<ManagementBaseObject> Wmi(string wql, string scope = @"root\cimv2")
        {
            var list = new List<ManagementBaseObject>();
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, wql) { Options = { Timeout = TimeSpan.FromSeconds(60) } };
                foreach (ManagementBaseObject o in searcher.Get()) list.Add(o);
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                Log.Info($"System Snapshot - WMI '{wql}': {ex.Message}");
            }
            return list;
        }

        private static void KillTree(Process process)
        {
#if NET45
            process.Kill();
#else
            process.Kill(true);
#endif
        }

        private static string Prop(ManagementBaseObject o, string name)
        {
            try { return Text(o[name]); }
            catch (ManagementException) { return ""; }
        }

        /// <summary>Starts a program, waits for it (or kills it after the time) and returns what it printed.</summary>
        private static string Run(string exe, string args, int timeoutMs, Encoding? encoding = null)
        {
#if !NET45
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // the console code page (850, 437...) needs this
#endif
            var start = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding ?? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException(exe + " did not start");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { KillTree(process); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw new TimeoutException($"{exe} took longer than {timeoutMs / 1000} s");
            }
            output.Wait(2000);
            error.Wait(2000);
            return output.Result;
        }
    }
}
