using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class SystemSnapshotManager
    {
        // =================================================================
        //  System and hardware: Windows, the PC, drives, local users, security software
        // =================================================================

        private List<SnapshotTable> CollectSystem(CancellationToken token)
        {
            var tables = new List<SnapshotTable>();
            tables.Add(Guard("os", () => ReadWindowsAndPc(token)));
            token.ThrowIfCancellationRequested();
            tables.Add(Guard("systeminfo", ReadSystemInfoRaw));
            token.ThrowIfCancellationRequested();
            tables.Add(Guard("drives", ReadVolumes));
            tables.Add(Guard("disks", ReadPhysicalDisks));
            token.ThrowIfCancellationRequested();
            tables.Add(Guard("users", () => ReadLocalUsers(token)));
            tables.Add(Guard("security", ReadSecuritySoftware));
            return tables;
        }

        /// <summary>One table of a group: if it throws, it becomes an empty table with the reason, and the rest carry on.</summary>
        private static SnapshotTable Guard(string id, Func<SnapshotTable> read)
        {
            try
            {
                return read();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Info($"System Snapshot - {id}: {ex.Message}");
                return Failed(id, ex.Message);
            }
        }

#if NET45
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        private static long UptimeMilliseconds() => (long)GetTickCount64();
#else
        private static long UptimeMilliseconds() => Environment.TickCount64;
#endif

        private static string RegText(RegistryKey? key, string name) => key?.GetValue(name) is { } v ? Text(v) : "";

        private SnapshotTable ReadWindowsAndPc(CancellationToken token)
        {
            var facts = new List<(string, string)>
            {
                ("Computer name", Environment.MachineName),
                ("User", $"{Environment.UserDomainName}\\{Environment.UserName}"),
                ("Running as administrator", Text(IsAdministrator())),
            };

            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                string build = RegText(key, "CurrentBuild"), ubr = RegText(key, "UBR");
                // Windows 11 still writes "Windows 10 ..." in ProductName; builds from 22000 are Windows 11.
                string product = RegText(key, "ProductName");
                if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buildNumber) && buildNumber >= 22000)
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
                facts.Add(("Windows", product));
                facts.Add(("Edition", RegText(key, "EditionID")));
                facts.Add(("Version", RegText(key, "DisplayVersion")));
                facts.Add(("Build", ubr.Length > 0 ? $"{build}.{ubr}" : build));
                if (key?.GetValue("InstallDate") is int seconds)
                    facts.Add(("Installed on", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }

            #if NET45
            facts.Add(("Architecture", Environment.Is64BitOperatingSystem ? "X64" : "X86"));
#else
            facts.Add(("Architecture", RuntimeInformation.OSArchitecture.ToString()));
#endif
            facts.Add(("Last started", (DateTime.Now - TimeSpan.FromMilliseconds(UptimeMilliseconds())).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            facts.Add(("Time zone", TimeZoneInfo.Local.DisplayName));
            facts.Add(("Display language", CultureInfo.InstalledUICulture.Name));
            facts.Add(("Regional format", CultureInfo.CurrentCulture.Name));

            using (var control = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control"))
            {
                if (control?.GetValue("PEFirmwareType") is int firmware) facts.Add(("Firmware", firmware == 2 ? "UEFI" : "Legacy BIOS"));
                using var secure = control?.OpenSubKey("SecureBoot\\State");
                if (secure?.GetValue("UEFISecureBootEnabled") is int sb) facts.Add(("Secure Boot", sb == 1 ? "On" : "Off"));
            }

            token.ThrowIfCancellationRequested();
            var specs = SystemMonitorManager.ReadSpecsAsync().GetAwaiter().GetResult();
            // The system monitor repeats a few of the rows above (Windows, Version, Build): keep the ones read from the registry.
            var have = facts.Select(f => f.Item1).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stat in specs.Pc)
                if (have.Add(stat.Label)) facts.Add((stat.Label, stat.Value));
            if (specs.CpuName != null)
            {
                facts.Add(("Processor", specs.CpuName));
                facts.Add(("Cores / threads", $"{specs.CpuCores} / {specs.CpuThreads}"));
                if (specs.Virtualization is bool virt) facts.Add(("Virtualization", virt ? "On" : "Off"));
            }
            if (specs.InstalledMemoryBytes > 0)
                facts.Add(("Memory", $"{FormatBytes(specs.InstalledMemoryBytes)} {specs.MemoryType} {(specs.MemorySpeedMts > 0 ? specs.MemorySpeedMts + " MT/s" : "")}".Trim()));
            foreach (var gpu in specs.Gpus)
                facts.Add(("Graphics", $"{gpu.Key} (driver {gpu.Value.DriverVersion ?? "?"})"));

            return Facts("os", facts);
        }

        /// <summary>The same text `systeminfo` prints, kept as it is so it can sit next to the structured tables.</summary>
        private static SnapshotTable ReadSystemInfoRaw()
        {
            string text = Run("systeminfo.exe", "", 90_000);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Select(l => new[] { l }).ToList();
            return Make("systeminfo", new[] { "Line" }, lines, "Output of the systeminfo command, in this PC's language.");
        }

        private static SnapshotTable ReadVolumes()
        {
            var rows = new List<string[]>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    bool ready = drive.IsReady;
                    rows.Add(new[]
                    {
                        drive.Name,
                        ready ? drive.VolumeLabel : "",
                        drive.DriveType.ToString(),
                        ready ? drive.DriveFormat : "(not ready)",
                        ready ? FormatBytes(drive.TotalSize) : "",
                        ready ? FormatBytes(drive.AvailableFreeSpace) : "",
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rows.Add(new[] { drive.Name, "", drive.DriveType.ToString(), "(not available)", "", "" });
                }
            }
            return Make("drives", new[] { "Drive", "Label", "Type", "File system", "Size", "Free" }, rows);
        }

        private static SnapshotTable ReadPhysicalDisks()
        {
            var specs = SystemMonitorManager.ReadSpecsAsync().GetAwaiter().GetResult();
            var rows = specs.Disks.OrderBy(d => d.Key)
                .Select(d => new[] { $"Disk {d.Key}", d.Value.Model, d.Value.Media ?? "", d.Value.Bus ?? "", FormatBytes(d.Value.SizeBytes) })
                .ToList();
            return Make("disks", new[] { "Disk", "Model", "Type", "Bus", "Size" }, rows);
        }

        private static SnapshotTable ReadLocalUsers(CancellationToken token)
        {
            // The Administrators group has a fixed SID in every language; its name does not.
            var adminNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in Wmi("SELECT Name FROM Win32_Group WHERE SID='S-1-5-32-544'"))
            {
                string name = Prop(group, "Name");
                foreach (var member in Wmi($"ASSOCIATORS OF {{Win32_Group.Domain='{Environment.MachineName}',Name='{name}'}} WHERE AssocClass=Win32_GroupUser"))
                    adminNames.Add(Prop(member, "Name"));
            }

            token.ThrowIfCancellationRequested();
            var rows = new List<string[]>();
            foreach (var user in Wmi("SELECT Name, FullName, Disabled, Lockout, PasswordRequired, PasswordExpires, SID FROM Win32_UserAccount WHERE LocalAccount=True"))
            {
                string name = Prop(user, "Name");
                rows.Add(new[]
                {
                    name, Prop(user, "FullName"),
                    Text(adminNames.Contains(name)),
                    Text(!Equals(user["Disabled"], true)),
                    Text(user["PasswordRequired"]), Prop(user, "SID"),
                });
            }
            return Make("users", new[] { "Account", "Full name", "Administrator", "Enabled", "Password required", "SID" }, rows,
                "Local accounts only. No password information is read.");
        }

        private static SnapshotTable ReadSecuritySoftware()
        {
            var rows = new List<string[]>();
            foreach (var (cls, label) in new[] { ("AntiVirusProduct", "Antivirus"), ("AntiSpywareProduct", "Anti-spyware"), ("FirewallProduct", "Firewall") })
            {
                foreach (var product in Wmi($"SELECT displayName, productState, pathToSignedProductExe FROM {cls}", @"root\SecurityCenter2"))
                {
                    int state = product["productState"] is { } s ? Convert.ToInt32(s, CultureInfo.InvariantCulture) : 0;
                    rows.Add(new[]
                    {
                        label, Prop(product, "displayName"),
                        (state & 0x1000) != 0 ? "On" : "Off",
                        (state & 0x10) == 0 ? "Up to date" : "Out of date",
                        Prop(product, "pathToSignedProductExe"),
                    });
                }
            }
            return Make("security", new[] { "Type", "Product", "State", "Signatures", "Program" }, rows,
                rows.Count == 0 ? "Windows Security Center reported nothing (it does not exist on Windows Server)." : null);
        }
    }
}
