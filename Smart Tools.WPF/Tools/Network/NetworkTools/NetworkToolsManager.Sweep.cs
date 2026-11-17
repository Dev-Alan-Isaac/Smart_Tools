using Smart_Tools.WPF.Models;
using System.Net;
using System.Net.Sockets;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Port Scanner - finding the devices on a network and the targets the scanner offers (this PC, each adapter's network).</summary>
    public static partial class NetworkToolsManager
    {
        /// <summary>A sweep over more addresses than this asks first (a /22 is 1,022 hosts).</summary>
        public const int MaxSweepHosts = 1024;

        /// <summary>Hosts checked at once while looking for live devices.</summary>
        public const int SweepConcurrency = 64;

        /// <summary>Ports tried on a device that does not answer a ping (many firewalls block it): web, file sharing, SSH, remote desktop, DNS, printers.</summary>
        public static readonly int[] DiscoveryPorts = { 80, 443, 445, 22, 135, 139, 3389, 8080, 53, 9100 };

        // =================================================================
        //  Addresses
        // =================================================================

        private static uint MaskOf(int prefix) => prefix <= 0 ? 0 : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);

        /// <summary>The network address ("192.168.1.20", 24 -> 192.168.1.0).</summary>
        public static IPAddress NetworkAddress(IPAddress ip, int prefix) => FromUInt(ToUInt(ip) & MaskOf(prefix));

        /// <summary>How many hosts a /<paramref name="prefix"/> network has (network and broadcast addresses left out; /31 = 2, /32 = 1).</summary>
        public static long HostCount(int prefix) => prefix switch
        {
            >= 32 => 1,
            31 => 2,
            <= 0 => (1L << 32) - 2,
            _ => (1L << (32 - prefix)) - 2,
        };

        /// <summary>The host addresses of the network that holds <paramref name="ip"/> (IPv4, at most <paramref name="max"/>).</summary>
        public static List<IPAddress> HostsIn(IPAddress ip, int prefix, int max = MaxSweepHosts * 4)
        {
            var list = new List<IPAddress>();
            if (ip.AddressFamily != AddressFamily.InterNetwork || prefix < 0 || prefix > 32) return list;
            uint mask = MaskOf(prefix), network = ToUInt(ip) & mask, broadcast = network | ~mask;
            uint first = prefix >= 31 ? network : network + 1, last = prefix >= 31 ? broadcast : broadcast - 1;
            for (long v = first; v <= last && list.Count < max; v++) list.Add(FromUInt((uint)v));
            return list;
        }

        /// <summary>"10.0.0.0/24" or "10.0.0.5/26" (IPv4 only).</summary>
        public static bool TryParseCidr(string? text, out IPAddress ip, out int prefix)
        {
            ip = IPAddress.None;
            prefix = 0;
            string[] parts = (text ?? "").Trim().Split('/');
            if (parts.Length != 2) return false;
            if (!IPAddress.TryParse(parts[0].Trim(), out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
            if (!int.TryParse(parts[1].Trim(), out prefix) || prefix is < 0 or > 32) return false;
            ip = parsed;
            return true;
        }

        // =================================================================
        //  Targets
        // =================================================================

        public const string ThisPcId = "pc";
        public const string CustomId = "custom";

        /// <summary>This PC, one network per connected adapter address, then Custom. Reads the adapters: call off the UI thread.</summary>
        public static List<ScanTarget> GetScanTargets(string thisPcLabel, string thisPcDetail, string customLabel, string customDetail, Func<string, string> networkDetail)
        {
            var list = new List<ScanTarget> { new(ThisPcId, ScanTargetKind.ThisPc, thisPcLabel, thisPcDetail) };
            foreach (var a in GetAdapters(includeVirtual: true).Where(a => a.State == AdapterState.Connected))
            {
                foreach (var (address, prefix) in a.IPv4)
                {
                    if (!TryParseIPv4(address, out var ip) || prefix is < 8 or > 30) continue;
                    string network = NetworkAddress(ip, prefix) + "/" + prefix;
                    string id = $"net|{a.Id}|{address}";
                    list.Add(new ScanTarget(id, ScanTargetKind.Network, a.Name, networkDetail(network), ip, prefix, a.Kind));
                }
            }
            list.Add(new ScanTarget(CustomId, ScanTargetKind.Custom, customLabel, customDetail));
            return list;
        }

        /// <summary>The remembered target if it is still there, else This PC.</summary>
        public static ScanTarget PickTarget(IReadOnlyList<ScanTarget> targets, string? savedId) =>
            targets.FirstOrDefault(t => t.Id == savedId) ?? targets[0];

        /// <summary>Loopback and every IPv4 address this PC has on a connected adapter, with the adapter's name.</summary>
        public static List<ScanHost> LocalHosts(string loopbackLabel)
        {
            var hosts = new List<ScanHost> { new(loopbackLabel, IPAddress.Loopback, null) };
            foreach (var a in GetAdapters(includeVirtual: true).Where(a => a.State == AdapterState.Connected))
                foreach (var (address, _) in a.IPv4)
                    if (TryParseIPv4(address, out var ip) && hosts.All(h => !h.Address.Equals(ip)))
                        hosts.Add(new ScanHost(a.Name, ip, null));
            return hosts;
        }

        // =================================================================
        //  Finding the devices that answer
        // =================================================================

        /// <summary>
        /// Which of <paramref name="candidates"/> answer: an ICMP echo (when <paramref name="source"/> is known), else a
        /// TCP connect to a few common ports. Hosts are checked <see cref="SweepConcurrency"/> at a time; found hosts are
        /// reported as they appear. Cancelling returns what was found so far.
        /// </summary>
        public static async Task<List<IPAddress>> FindLiveHostsAsync(
            IReadOnlyList<IPAddress> candidates,
            IPAddress? source,
            int timeoutMs,
            IProgress<SweepProgress>? progress,
            Action<IPAddress>? onFound,
            CancellationToken token)
        {
            if (source != null) EnsureSourceUsable(source);
            var found = new List<IPAddress>();
            int next = -1, checkedCount = 0;
            var gate = new object();
            int wait = Math.Clamp(timeoutMs, 200, 1000);

            async Task<bool> Answers(IPAddress ip)
            {
                if (source != null && (await Task.Run(() => PingFrom(source, ip, 0, wait), token).ConfigureAwait(false)).Success) return true;
                foreach (int port in DiscoveryPorts)
                {
                    if (token.IsCancellationRequested) return false;
                    if (await TryConnectAsync(ip, port, Math.Min(wait, 400), token, source).ConfigureAwait(false) != null) return true;
                }
                return false;
            }

            async Task Worker()
            {
                while (!token.IsCancellationRequested)
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= candidates.Count) return;
                    var ip = candidates[i];
                    bool up = false;
                    try { up = await Answers(ip).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
                    int done = Interlocked.Increment(ref checkedCount);
                    int count;
                    if (up)
                    {
                        lock (gate) { found.Add(ip); count = found.Count; }
                        onFound?.Invoke(ip);
                    }
                    else
                    {
                        lock (gate) count = found.Count;
                    }
                    if (done % 8 == 0 || done == candidates.Count) progress?.Report(new SweepProgress(done, candidates.Count, count));
                }
            }

            await Task.WhenAll(Enumerable.Range(0, Math.Clamp(SweepConcurrency, 1, Math.Max(1, candidates.Count))).Select(_ => Worker())).ConfigureAwait(false);
            lock (gate) return found.OrderBy(ToUInt).ToList();
        }
    }
}
