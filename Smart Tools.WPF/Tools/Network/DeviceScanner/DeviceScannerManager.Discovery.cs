using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Network-wide name discovery: one mDNS (Bonjour) browse and one SSDP (UPnP)
    /// search, started with the scan. Most devices announce a name they chose
    /// themselves this way - Google Home / Chromecast, Echo, phones and tablets
    /// (Apple), TVs, printers, cameras, smart plugs, Macs and PCs - which is the
    /// name the router list shows too. Pure sockets, no WPF types.
    /// </summary>
    public sealed partial class DeviceScannerManager
    {
        private const int MdnsListenMs = 3000;
        private const int SsdpListenMs = 2000;

        /// <summary>Per IP: Friendly = a name the owner/maker chose; Host = the device's mDNS hostname (weaker).</summary>
        private sealed class DiscoveredNames
        {
            public Dictionary<string, string> Friendly { get; } = new();
            public Dictionary<string, string> Host { get; } = new();
        }

        private sealed class MdnsInfo
        {
            public string? Fn;                                        // TXT fn= (Google Cast friendly name)
            public string? Host;                                      // from SRV / A records
            public readonly List<(string Service, string Label)> Instances = new();
        }

        // Services a device advertises, roughly best-name-first.
        private static readonly string[] MdnsServices =
        {
            "_googlecast._tcp", "_airplay._tcp", "_companion-link._tcp", "_homekit._tcp", "_hap._tcp",
            "_amzn-wplay._tcp", "_spotify-connect._tcp", "_raop._tcp", "_device-info._tcp", "_sleep-proxy._udp",
            "_ipp._tcp", "_printer._tcp", "_http._tcp", "_smb._tcp", "_rfb._tcp", "_workstation._tcp",
            "_services._dns-sd._udp",
        };

        // Instance names of these services are not device names ("host [mac]", the browse meta-query...).
        private static readonly string[] MdnsGenericServices = { "_workstation", "_services", "_sleep-proxy" };

        private static readonly HttpClient SsdpHttp = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

        private static async Task<DiscoveredNames> DiscoverNamesAsync(string? localAddress, CancellationToken token)
        {
            var result = new DiscoveredNames();
            IPAddress? local = IPAddress.TryParse(localAddress, out var l) ? l : null;
            try
            {
                var mdns = BrowseMdnsAsync(local, token);
                var ssdp = BrowseSsdpAsync(local, token);
                var mdnsInfo = await mdns.ConfigureAwait(false);
                var ssdpNames = await ssdp.ConfigureAwait(false);

                foreach (var (ip, info) in mdnsInfo)
                {
                    string? friendly = Clean(info.Fn) ?? BestInstance(info);
                    if (friendly != null) result.Friendly[ip] = friendly;
                    string? host = Clean(info.Host);
                    if (host != null) result.Host[ip] = host;
                }
                foreach (var (ip, name) in ssdpNames)
                    result.Friendly.TryAdd(ip, name);
            }
            catch (Exception)
            {
                // discovery is a bonus - the per-device lookups still run
            }
            return result;
        }

        private static string? Clean(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
            return name.Length > 0 ? name : null;
        }

        private static string? BestInstance(MdnsInfo info)
        {
            foreach (string service in MdnsServices)
            {
                string key = service[..service.IndexOf('.')];
                if (MdnsGenericServices.Contains(key)) continue;
                foreach (var (svc, label) in info.Instances)
                {
                    if (svc != key) continue;
                    string name = label;
                    int at = name.IndexOf('@');                       // _raop: "AABBCCDDEEFF@Living Room"
                    if (at >= 0 && at < 14) name = name[(at + 1)..];
                    if (Clean(name) is string good) return good;
                }
            }
            return null;
        }

        // ---------------- mDNS browse (multicast, answers asked to come back unicast) ----------------

        private static async Task<Dictionary<string, MdnsInfo>> BrowseMdnsAsync(IPAddress? local, CancellationToken token)
        {
            var devices = new Dictionary<string, MdnsInfo>();
            try
            {
                using var udp = local != null ? new UdpClient(new IPEndPoint(local, 0)) : new UdpClient(AddressFamily.InterNetwork);
                if (local == null) udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                else udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(MdnsListenMs);

                byte[] query = BuildMdnsQuery();
                var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                await udp.SendAsync(query, group, cts.Token).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                        await udp.SendAsync(query, group, cts.Token).ConfigureAwait(false);   // second try: sleepy devices
                    }
                    catch (Exception) { /* done listening */ }
                }, CancellationToken.None);

                while (true)
                {
                    var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    string ip = reply.RemoteEndPoint.Address.ToString();
                    if (!devices.TryGetValue(ip, out var info)) devices[ip] = info = new MdnsInfo();
                    try { ParseMdnsResponse(reply.Buffer, reply.RemoteEndPoint.Address, info); }
                    catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { /* malformed packet */ }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // listen window over (or no multicast on this adapter)
            }
            return devices;
        }

        private static byte[] BuildMdnsQuery()
        {
            var packet = new List<byte> { 0, 0, 0, 0, (byte)(MdnsServices.Length >> 8), (byte)MdnsServices.Length, 0, 0, 0, 0, 0, 0 };
            foreach (string service in MdnsServices)
            {
                foreach (string label in (service + ".local").Split('.'))
                {
                    packet.Add((byte)label.Length);
                    packet.AddRange(Encoding.ASCII.GetBytes(label));
                }
                packet.AddRange(new byte[] { 0, 0, 12, 0x80, 1 });                       // end, PTR, IN + unicast-response bit
            }
            return packet.ToArray();
        }

        private static void ParseMdnsResponse(byte[] m, IPAddress source, MdnsInfo info)
        {
            if (m.Length < 12 || (m[2] & 0x80) == 0) return;                              // not a response
            int questions = m[4] << 8 | m[5];
            int records = (m[6] << 8 | m[7]) + (m[8] << 8 | m[9]) + (m[10] << 8 | m[11]);
            int pos = 12;
            for (int i = 0; i < questions; i++)
            {
                SkipName(m, ref pos);
                pos += 4;
            }

            for (int i = 0; i < records && pos + 10 <= m.Length; i++)
            {
                string owner = ReadName(m, ref pos);
                if (pos + 10 > m.Length) return;
                int type = m[pos] << 8 | m[pos + 1];
                int length = m[pos + 8] << 8 | m[pos + 9];
                int data = pos + 10;
                pos = data + length;
                if (pos > m.Length) return;

                switch (type)
                {
                    case 12:                                                              // PTR: service -> instance
                    {
                        int p = data;
                        AddInstance(info, ReadName(m, ref p));
                        break;
                    }
                    case 33:                                                              // SRV: instance -> host
                    {
                        AddInstance(info, owner);
                        int p = data + 6;
                        info.Host ??= ReadName(m, ref p);
                        break;
                    }
                    case 16:                                                              // TXT: key=value strings
                    {
                        AddInstance(info, owner);
                        int p = data;
                        while (p < data + length)
                        {
                            int len = m[p++];
                            if (len == 0 || p + len > data + length) break;
                            string kv = Encoding.UTF8.GetString(m, p, len);
                            p += len;
                            if (kv.StartsWith("fn=", StringComparison.OrdinalIgnoreCase)) info.Fn ??= kv[3..];
                        }
                        break;
                    }
                    case 1 when length == 4:                                              // A: host -> ip
                        if (new IPAddress(m.AsSpan(data, 4)).Equals(source)) info.Host = owner;
                        break;
                }
            }
        }

        /// <summary>"Living Room._googlecast._tcp.local" -> (service "_googlecast", label "Living Room").</summary>
        private static void AddInstance(MdnsInfo info, string instance)
        {
            int at = instance.IndexOf("._", StringComparison.Ordinal);
            if (at <= 0) return;
            string label = instance[..at];
            string service = instance[(at + 1)..];
            int dot = service.IndexOf('.');
            if (dot > 0) service = service[..dot];
            if (!info.Instances.Contains((service, label))) info.Instances.Add((service, label));
        }

        // ---------------- SSDP / UPnP (TVs, cameras, media boxes, some smart plugs) ----------------

        private static async Task<Dictionary<string, string>> BrowseSsdpAsync(IPAddress? local, CancellationToken token)
        {
            var locations = new Dictionary<string, string>();
            var names = new Dictionary<string, string>();
            try
            {
                using var udp = local != null ? new UdpClient(new IPEndPoint(local, 0)) : new UdpClient(AddressFamily.InterNetwork);
                if (local == null) udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                else udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(SsdpListenMs);

                byte[] search = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
                await udp.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), cts.Token).ConfigureAwait(false);

                try
                {
                    while (true)
                    {
                        var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        string ip = reply.RemoteEndPoint.Address.ToString();
                        if (locations.ContainsKey(ip)) continue;
                        var m = Regex.Match(Encoding.UTF8.GetString(reply.Buffer), @"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        if (m.Success) locations[ip] = m.Groups[1].Value;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // listen window over
                }

                // The description XML holds <friendlyName>. Only fetch from the device that answered.
                await Task.WhenAll(locations.Select(async pair =>
                {
                    try
                    {
                        if (!Uri.TryCreate(pair.Value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.Host != pair.Key) return;
                        string xml = await SsdpHttp.GetStringAsync(uri, token).ConfigureAwait(false);
                        var fn = Regex.Match(xml, @"<friendlyName>(.*?)</friendlyName>", RegexOptions.Singleline);
                        string name = WebUtility.HtmlDecode(fn.Groups[1].Value).Trim();
                        if (fn.Success && name.Length > 0) lock (names) names[pair.Key] = name;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { /* no description */ }
                })).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // no multicast on this adapter
            }
            return names;
        }
    }
}
