using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Management;
using System.Security.Cryptography.X509Certificates;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class SystemSnapshotManager
    {
        // =================================================================
        //  Software: programs, the runtimes legacy software leans on, Windows features, environment variables
        // =================================================================

        private sealed record InstalledProgram(string Name, string Version, string Publisher, string InstallDate, string Location, long SizeKb, string Scope);

        private List<SnapshotTable> CollectSoftware(CancellationToken token)
        {
            var programs = new List<InstalledProgram>();
            var tables = new List<SnapshotTable>();

            tables.Add(Guard("programs", () => ReadPrograms(programs)));
            token.ThrowIfCancellationRequested();
            tables.Add(Guard("runtimes", () => ReadRuntimes(programs)));
            tables.Add(Guard("features", () => ReadWindowsFeatures(token)));
            tables.Add(Guard("env", ReadEnvironment));
            tables.Add(Guard("path", ReadPathFolders));
            return tables;
        }

        private static IEnumerable<InstalledProgram> ProgramsIn(RegistryKey? root, string scope)
        {
            using var uninstall = root?.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (string id in uninstall?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var app = uninstall!.OpenSubKey(id);
                string name = RegText(app, "DisplayName");
                // updates and hidden helper entries are not programs a person installed
                if (name.Length == 0 || RegText(app, "SystemComponent") == "1" || RegText(app, "ParentKeyName").Length > 0) continue;
                long.TryParse(RegText(app, "EstimatedSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size);
                yield return new InstalledProgram(name, RegText(app, "DisplayVersion"), RegText(app, "Publisher"), RegText(app, "InstallDate"), RegText(app, "InstallLocation"), size, scope);
            }
        }

        private static SnapshotTable ReadPrograms(List<InstalledProgram> into)
        {
            using var machine64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

            into.AddRange(ProgramsIn(machine64, "All users (64-bit)"));
            into.AddRange(ProgramsIn(machine32, "All users (32-bit)"));
            into.AddRange(ProgramsIn(user, "Current user"));
            into.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            var rows = into.Select(p => new[]
            {
                p.Name, p.Version, p.Publisher, p.InstallDate, p.Location,
                p.SizeKb > 0 ? FormatBytes(p.SizeKb * 1024.0) : "", p.Scope,
            }).ToList();
            return Make("programs", new[] { "Name", "Version", "Publisher", "Installed", "Location", "Size", "For" }, rows,
                "Programs listed in Windows' uninstall lists. Microsoft Store apps are not included.");
        }

        private static string DotNetFrameworkName(int release) => release switch
        {
            >= 533320 => "4.8.1", >= 528040 => "4.8", >= 461808 => "4.7.2", >= 461308 => "4.7.1", >= 460798 => "4.7",
            >= 394802 => "4.6.2", >= 394254 => "4.6.1", >= 393295 => "4.6", >= 379893 => "4.5.2", >= 378675 => "4.5.1", >= 378389 => "4.5",
            _ => "4.x (release " + release + ")",
        };

        private static SnapshotTable ReadRuntimes(List<InstalledProgram> programs)
        {
            var rows = new List<string[]>();

            using (var full = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                if (full?.GetValue("Release") is int release) rows.Add(new[] { ".NET Framework", DotNetFrameworkName(release), "Registry" });
            }
            foreach (var (key, label) in new[] { (@"v3.5", ".NET Framework 3.5 (includes 2.0 and 3.0)"), (@"v2.0.50727", ".NET Framework 2.0") })
            {
                using var old = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\" + key);
                if (old?.GetValue("Install") is int installed && installed == 1) rows.Add(new[] { label, RegText(old, "Version"), "Registry" });
            }

            // .NET 5 and later (and .NET Core): one value per installed version under each shared framework
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var installed = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions");
                foreach (string arch in installed?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var shared = installed!.OpenSubKey(arch + @"\sharedfx");
                    foreach (string framework in shared?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var versions = shared!.OpenSubKey(framework);
                        foreach (string version in versions?.GetValueNames() ?? Array.Empty<string>())
                            rows.Add(new[] { $"{framework} ({arch})", version, "Registry" });
                    }
                }
            }

            using (var ps = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine"))
            {
                string version = RegText(ps, "PowerShellVersion");
                if (version.Length > 0) rows.Add(new[] { "Windows PowerShell", version, "Registry" });
            }

            // Runtimes that install like any program: the list is the source.
            string[] markers = { "Visual C++", "Java", "JRE", "JDK", "WebView2", "DirectX", "OpenAL", "PowerShell 7", "Node.js", "Python", "Microsoft .NET", "Windows Desktop Runtime", "ASP.NET Core", "SQL Server" };
            foreach (var program in programs.Where(p => markers.Any(m => p.Name.Contains(m, StringComparison.OrdinalIgnoreCase))).DistinctBy(p => (p.Name, p.Version)))
                rows.Add(new[] { program.Name, program.Version, "Installed programs" });

            return Make("runtimes", new[] { "Runtime", "Version", "Found in" }, rows,
                "Old programs often need a specific .NET Framework, Visual C++ or Java version: install the same ones on the new PC first.");
        }

        private static SnapshotTable ReadWindowsFeatures(CancellationToken token)
        {
            var rows = new List<string[]>();
            foreach (var feature in Wmi("SELECT Name, Caption, InstallState FROM Win32_OptionalFeature"))
            {
                token.ThrowIfCancellationRequested();
                string state = Prop(feature, "InstallState") switch { "1" => "Enabled", "2" => "Disabled", "3" => "Absent", var other => other };
                rows.Add(new[] { Prop(feature, "Name"), Prop(feature, "Caption"), state });
            }
            rows.Sort((a, b) => string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase));
            return Make("features", new[] { "Feature", "Description", "State" }, rows,
                rows.Count == 0 ? "Windows did not list its optional features. Run the tool as administrator to try again." : "Optional Windows features such as .NET 3.5, SMB 1, Telnet, IIS, Hyper-V or WSL.");
        }

        private SnapshotTable ReadEnvironment()
        {
            var manager = new EnvironmentVariablesManager();
            var rows = new List<string[]>();
            foreach (var scope in new[] { EnvScope.System, EnvScope.User })
            {
                foreach (var variable in manager.Load(scope).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                    rows.Add(new[] { scope == EnvScope.System ? "System" : "User", variable.Name, _redactor.Value(variable.Name, variable.Value), Text(variable.IsExpandable) });
            }
            return Make("env", new[] { "Scope", "Name", "Value", "Uses %references%" }, rows,
                Note("Values are shown as stored, with %REFERENCES% not expanded.", RedactionNote()));
        }

        private static SnapshotTable ReadPathFolders()
        {
            var rows = new List<string[]>();
            foreach (var (scope, target) in new[] { ("System", EnvironmentVariableTarget.Machine), ("User", EnvironmentVariableTarget.User) })
            {
                string path = Environment.GetEnvironmentVariable("Path", target) ?? "";
                int order = 0;
                foreach (string folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    order++;
                    string real = Environment.ExpandEnvironmentVariables(folder);
                    rows.Add(new[] { scope, order.ToString(CultureInfo.InvariantCulture), folder, Text(Directory.Exists(real)) });
                }
            }
            return Make("path", new[] { "Scope", "Order", "Folder", "Exists" }, rows,
                "The order matters: Windows uses the first match. A folder that does not exist on the new PC is a sign something has to be installed.");
        }

        // =================================================================
        //  Certificates and drivers
        // =================================================================

        private static List<SnapshotTable> CollectCertificates(CancellationToken token)
        {
            var rows = new List<string[]>();
            foreach (var (location, label) in new[] { (StoreLocation.CurrentUser, "Current user"), (StoreLocation.LocalMachine, "This PC") })
            {
                token.ThrowIfCancellationRequested();
                using var store = new X509Store(StoreName.My, location);
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    foreach (var cert in store.Certificates)
                    {
                        // whether there is a private key is listed; the key itself is never touched
                        rows.Add(new[] { label, cert.Subject, cert.Issuer, cert.Thumbprint, cert.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Text(cert.HasPrivateKey) });
                    }
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
                {
                    rows.Add(new[] { label, "(could not be opened: " + ex.Message + ")", "", "", "", "" });
                }
            }
            return new List<SnapshotTable>
            {
                Make("certs", new[] { "Store", "Subject", "Issuer", "Thumbprint", "Expires", "Has private key" }, rows,
                    "Personal certificates only. Private keys are never read or exported."),
            };
        }

        private static List<SnapshotTable> CollectDrivers(CancellationToken token)
        {
            var rows = new List<string[]>();
            foreach (var driver in Wmi("SELECT DeviceName, Manufacturer, DriverVersion, DriverDate, InfName, DeviceClass FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL"))
            {
                token.ThrowIfCancellationRequested();
                string maker = Prop(driver, "Manufacturer");
                if (maker.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || maker.Length == 0) continue;

                string date = "";
                try { if (driver["DriverDate"] is string raw) date = ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
                catch (ArgumentOutOfRangeException) { }
                rows.Add(new[] { Prop(driver, "DeviceName"), maker, Prop(driver, "DriverVersion"), date, Prop(driver, "DeviceClass"), Prop(driver, "InfName") });
            }
            rows.Sort((a, b) => string.Compare(a[0], b[0], StringComparison.CurrentCultureIgnoreCase));
            return new List<SnapshotTable>
            {
                Make("drivers", new[] { "Device", "Maker", "Version", "Date", "Class", "INF file" }, rows, "Drivers from makers other than Microsoft."),
            };
        }
    }
}
