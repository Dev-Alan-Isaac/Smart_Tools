using Smart_Tools.WPF.Models;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class SystemSnapshotManager
    {
        // =================================================================
        //  Firewall: the three profiles and every rule, through Windows Firewall's own COM object (HNetCfg.FwPolicy2).
        //  netsh prints in the PC's language and would have to be parsed; the COM object gives the values themselves.
        // =================================================================

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string source, StringBuilder result, int size, IntPtr reserved);

        private const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;

        private List<SnapshotTable> CollectFirewall(SnapshotOptions options, CancellationToken token)
        {
            Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new NotSupportedException("Windows Firewall is not available on this PC.");
            object policy = Activator.CreateInstance(type)!;
            try
            {
                return new List<SnapshotTable>
                {
                    Guard("firewall-profiles", () => ReadFirewallProfiles(type, policy)),
                    Guard("firewall-rules", () => ReadFirewallRules(policy, options, token)),
                };
            }
            finally
            {
                Marshal.FinalReleaseComObject(policy);
            }
        }

        /// <summary>Reads a property that takes an argument (FirewallEnabled[profile]), which `dynamic` cannot do on a late-bound COM object.</summary>
        private static object? Indexed(Type type, object target, string property, int profile) =>
            type.InvokeMember(property, BindingFlags.GetProperty, null, target, new object[] { profile });

        private static SnapshotTable ReadFirewallProfiles(Type type, object policy)
        {
            int current = Convert.ToInt32(type.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null), CultureInfo.InvariantCulture);
            var rows = new List<string[]>();
            foreach (var (profile, name) in new[] { (ProfileDomain, "Domain"), (ProfilePrivate, "Private"), (ProfilePublic, "Public") })
            {
                rows.Add(new[]
                {
                    name,
                    Text((current & profile) != 0),
                    Text(Indexed(type, policy, "FirewallEnabled", profile) is true),
                    Convert.ToInt32(Indexed(type, policy, "DefaultInboundAction", profile), CultureInfo.InvariantCulture) == 0 ? "Block" : "Allow",
                    Convert.ToInt32(Indexed(type, policy, "DefaultOutboundAction", profile), CultureInfo.InvariantCulture) == 0 ? "Block" : "Allow",
                    Text(Indexed(type, policy, "BlockAllInboundTraffic", profile) is true),
                    Text(Indexed(type, policy, "NotificationsDisabled", profile) is true),
                });
            }
            return Make("firewall-profiles", new[] { "Profile", "In use now", "Firewall on", "Inbound by default", "Outbound by default", "Block all inbound", "Notifications off" }, rows,
                "Public, Private and Domain are chosen per network (see Network settings).");
        }

        private static string Resolve(string text)
        {
            if (!text.StartsWith('@')) return text;
            var result = new StringBuilder(512);
            return SHLoadIndirectString(text, result, result.Capacity, IntPtr.Zero) == 0 ? result.ToString() : text;
        }

        private static string ProfilesText(int mask)
        {
            if ((mask & 0x7FFFFFFF) == 0x7FFFFFFF) return "All";
            var names = new List<string>();
            if ((mask & ProfileDomain) != 0) names.Add("Domain");
            if ((mask & ProfilePrivate) != 0) names.Add("Private");
            if ((mask & ProfilePublic) != 0) names.Add("Public");
            return names.Count == 3 ? "All" : string.Join(", ", names);
        }

        private static string ProtocolText(int protocol) => protocol switch
        {
            6 => "TCP", 17 => "UDP", 256 => "Any", 1 => "ICMPv4", 58 => "ICMPv6", _ => protocol.ToString(CultureInfo.InvariantCulture),
        };

        private SnapshotTable ReadFirewallRules(object policy, SnapshotOptions options, CancellationToken token)
        {
            dynamic rules = ((dynamic)policy).Rules;
            var rows = new List<string[]>();
            int total = 0, builtIn = 0;

            foreach (dynamic rule in (IEnumerable)rules)
            {
                token.ThrowIfCancellationRequested();
                total++;
                string rawName = Text((object?)rule.Name), rawGroup = Text((object?)rule.Grouping);

                // Rules that Windows ships have names like "@FirewallAPI.dll,-25001". Everything else was added by a person or an installer.
                bool windows = rawName.StartsWith('@') || rawGroup.StartsWith('@');
                if (windows) builtIn++;
                if (windows && !options.IncludeBuiltInFirewallRules) continue;

                rows.Add(new[]
                {
                    Resolve(rawName),
                    Resolve(rawGroup),
                    windows ? "Windows" : "Custom / app",
                    Convert.ToInt32((object)rule.Direction, CultureInfo.InvariantCulture) == 1 ? "Inbound" : "Outbound",
                    Convert.ToInt32((object)rule.Action, CultureInfo.InvariantCulture) == 1 ? "Allow" : "Block",
                    Text((object)rule.Enabled),
                    ProfilesText(Convert.ToInt32((object)rule.Profiles, CultureInfo.InvariantCulture)),
                    ProtocolText(Convert.ToInt32((object)rule.Protocol, CultureInfo.InvariantCulture)),
                    Text((object?)rule.LocalPorts), Text((object?)rule.RemotePorts),
                    Text((object?)rule.LocalAddresses), Text((object?)rule.RemoteAddresses),
                    Text((object?)rule.ApplicationName), Text((object?)rule.ServiceName),
                });
            }

            rows.Sort((a, b) => string.Compare(a[0], b[0], StringComparison.CurrentCultureIgnoreCase));
            string note = options.IncludeBuiltInFirewallRules
                ? $"All {total} rules."
                : $"{rows.Count} rules added by people or installers. {builtIn} rules that Windows ships were left out (tick \"Include Windows' own firewall rules\" to list them).";
            return Make("firewall-rules",
                new[] { "Name", "Group", "Source", "Direction", "Action", "Enabled", "Profiles", "Protocol", "Local ports", "Remote ports", "Local addresses", "Remote addresses", "Program", "Service" },
                rows, note);
        }
    }
}
