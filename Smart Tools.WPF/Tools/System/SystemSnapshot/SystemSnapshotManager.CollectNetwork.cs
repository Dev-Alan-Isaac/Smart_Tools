using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class SystemSnapshotManager
    {
        // =================================================================
        //  Network and ports: adapters, routes, settings, listening ports (with the program behind them), hosts file
        // =================================================================

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private List<SnapshotTable> CollectNetwork(CancellationToken token)
        {
            var tables = new List<SnapshotTable>
            {
                Guard("adapters", ReadAdapters),
                Guard("routes", ReadRoutes),
                Guard("network-config", ReadNetworkSettings),
            };
            token.ThrowIfCancellationRequested();
            tables.Add(Guard("ports", () => ReadListeningPorts(token)));
            tables.Add(Guard("hosts", ReadHosts));
            return tables;
        }

        private static SnapshotTable ReadAdapters()
        {
            var rows = NetworkToolsManager.GetAdapters(true).Select(a => new[]
            {
                a.Name, a.Description, a.Kind, a.StatusText, a.Mac,
                string.Join(", ", a.IPv4.Select(ip => $"{ip.Address}/{ip.Prefix}")),
                string.Join(", ", a.IPv6),
                string.Join(", ", a.Gateways),
                string.Join(", ", a.Dns),
                a.DnsAutomatic ? "Automatic" : "Manual",
                a.DhcpEnabled ? "DHCP" : "Static",
                a.DhcpServer ?? "",
                a.SpeedBitsPerSecond > 0 ? (a.SpeedBitsPerSecond / 1_000_000) + " Mbps" : "",
                Text(a.IsVirtual),
            }).ToList();
            return Make("adapters",
                new[] { "Name", "Description", "Type", "State", "MAC", "IPv4", "IPv6", "Gateway", "DNS", "DNS setting", "Address setting", "DHCP server", "Speed", "Virtual" },
                rows);
        }

        private static SnapshotTable ReadRoutes()
        {
            var rows = new List<string[]>();
            foreach (var route in Wmi("SELECT DestinationPrefix, NextHop, InterfaceAlias, RouteMetric, Protocol FROM MSFT_NetRoute WHERE AddressFamily=2", @"root\StandardCimv2"))
            {
                rows.Add(new[] { Prop(route, "DestinationPrefix"), Prop(route, "NextHop"), Prop(route, "InterfaceAlias"), Prop(route, "RouteMetric") });
            }
            rows.Sort((a, b) => string.CompareOrdinal(a[0], b[0]));
            return Make("routes", new[] { "Destination", "Next hop", "Interface", "Metric" }, rows, "IPv4 routes.");
        }

        private SnapshotTable ReadNetworkSettings()
        {
            var facts = new List<(string, string)>();
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            facts.Add(("Host name", ip.HostName));
            facts.Add(("DNS suffix", ip.DomainName));

            // Public / Private / Domain decides which firewall profile applies to a connection.
            foreach (var profile in Wmi("SELECT Name, InterfaceAlias, NetworkCategory, IPv4Connectivity FROM MSFT_NetConnectionProfile", @"root\StandardCimv2"))
            {
                string category = Prop(profile, "NetworkCategory") switch { "0" => "Public", "1" => "Private", "2" => "Domain", var other => other };
                facts.Add(("Network profile", $"{Prop(profile, "InterfaceAlias")}: {Prop(profile, "Name")} ({category})"));
            }

            using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            {
                facts.Add(("Proxy enabled", RegText(settings, "ProxyEnable") == "1" ? "Yes" : "No"));
                string server = RegText(settings, "ProxyServer");
                if (server.Length > 0) facts.Add(("Proxy server", _redactor.Text(server)));
                string pac = RegText(settings, "AutoConfigURL");
                if (pac.Length > 0) facts.Add(("Proxy auto-config script", pac));
                string bypass = RegText(settings, "ProxyOverride");
                if (bypass.Length > 0) facts.Add(("Proxy bypass", bypass));
            }

            foreach (var share in Wmi("SELECT Name, Path, Description FROM Win32_Share"))
                facts.Add(("Shared folder", $"{Prop(share, "Name")} -> {Prop(share, "Path")}"));

            using (var network = Registry.CurrentUser.OpenSubKey("Network"))
            {
                foreach (string letter in network?.GetSubKeyNames() ?? new string[0])
                {
                    using var drive = network!.OpenSubKey(letter);
                    facts.Add(("Mapped drive", $"{letter.ToUpperInvariant()}: -> {RegText(drive, "RemotePath")}"));
                }
            }

            return Facts("network-config", facts);
        }

        private static string ProcessPath(int pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return "";
            try
            {
                var name = new StringBuilder(1024);
                int size = name.Capacity;
                return QueryFullProcessImageNameW(handle, 0, name, ref size) ? name.ToString() : "";
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string ProcessName(int pid)
        {
            if (pid == 0) return "System Idle";
            try { return Process.GetProcessById(pid).ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
        }

        /// <summary>Every port something is listening on (TCP) or bound to (UDP), the program that owns it, and the service when it is one.</summary>
        private static SnapshotTable ReadListeningPorts(CancellationToken token)
        {
            // Several services share one svchost.exe: the PID tells which services are behind a port.
            var servicesByPid = new Dictionary<int, List<string>>();
            foreach (var service in Wmi("SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId > 0"))
            {
                int pid = Convert.ToInt32(service["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                if (!servicesByPid.TryGetValue(pid, out var list)) servicesByPid[pid] = list = new List<string>();
                list.Add(Prop(service, "Name"));
            }

            var endpoints = TcpUdpTable.ReadAll()
                .Where(e => e.Protocol.StartsWith("UDP", StringComparison.OrdinalIgnoreCase) || e.State.Equals("Listen", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.LocalPort).ThenBy(e => e.Protocol).ThenBy(e => e.LocalAddress)
                .ToList();

            var names = new Dictionary<int, (string Name, string Path)>();
            var rows = new List<string[]>();
            foreach (var e in endpoints)
            {
                token.ThrowIfCancellationRequested();
                if (!names.TryGetValue(e.Pid, out var who)) names[e.Pid] = who = (ProcessName(e.Pid), ProcessPath(e.Pid));
                rows.Add(new[]
                {
                    e.Protocol, e.LocalAddress, e.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    e.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), who.Name, who.Path,
                    servicesByPid.TryGetValue(e.Pid, out var s) ? string.Join(", ", s) : "",
                });
            }
            return Make("ports", new[] { "Protocol", "Address", "Port", "PID", "Process", "Program", "Service(s)" }, rows,
                "TCP ports in the Listening state and UDP ports in use. Programs of other users or of Windows itself may show no path without administrator rights.");
        }

        private static SnapshotTable ReadHosts()
        {
            var rows = new List<string[]>();
            string path = HostsEditorManager.HostsPath;
            if (File.Exists(path))
            {
                foreach (var row in HostsEditorManager.ParseHosts(File.ReadAllLines(path)).Where(r => r.Kind == HostsRowKind.Entry))
                    rows.Add(new[] { row.Address, row.Names, Text(row.IsEnabled), row.Comment });
            }
            return Make("hosts", new[] { "Address", "Names", "Active", "Comment" }, rows, path);
        }
    }
}
