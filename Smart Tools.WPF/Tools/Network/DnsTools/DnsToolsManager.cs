using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// DNS lookup, WHOIS and traceroute - the logic. DNS questions are sent straight to the server the person picks
    /// (Windows' own, or a public one), so the same name can be compared across servers. WHOIS and traceroute are in
    /// the other partial files. Nothing is saved except the setup.
    /// </summary>
    public sealed partial class DnsToolsManager
    {
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>The record types asked for by "All common records".</summary>
        public static readonly DnsRecordType[] CommonTypes =
        {
            DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.MX, DnsRecordType.NS, DnsRecordType.TXT, DnsRecordType.SOA, DnsRecordType.CAA,
        };

        public static readonly DnsServerChoice SystemServer = new("system", "System", "");

        public static IReadOnlyList<DnsServerChoice> Servers { get; } = new[]
        {
            SystemServer,
            new DnsServerChoice("cloudflare", "Cloudflare", "1.1.1.1"),
            new DnsServerChoice("google", "Google", "8.8.8.8"),
            new DnsServerChoice("quad9", "Quad9", "9.9.9.9"),
            new DnsServerChoice("opendns", "OpenDNS", "208.67.222.222"),
            new DnsServerChoice("custom", "Custom", ""),
        };

        // =================================================================
        //  Input
        // =================================================================

        /// <summary>
        /// The host out of whatever was pasted: "https://user@www.example.com:8080/a/b?c" becomes "www.example.com",
        /// "[2001:db8::1]:443" becomes "2001:db8::1". An address stays as it is.
        /// </summary>
        public static string CleanHost(string? input)
        {
            string text = (input ?? "").Trim();
            if (text.Length == 0) return "";

            int scheme = text.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) text = text[(scheme + 3)..];

            int end = text.IndexOfAny(new[] { '/', '?', '#' });
            if (end >= 0) text = text[..end];

            int at = text.LastIndexOf('@');
            if (at >= 0) text = text[(at + 1)..];

            if (text.StartsWith('['))
            {
                int close = text.IndexOf(']');
                return close > 0 ? text[1..close] : text.Trim('[', ']');
            }

            // "host:port" has exactly one colon; an IPv6 address has several and no port.
            if (text.Count(c => c == ':') == 1) text = text[..text.IndexOf(':')];

            return text.TrimEnd('.');
        }

        public static bool IsAddress(string host) => IPAddress.TryParse(host, out _);

        // =================================================================
        //  DNS lookup
        // =================================================================

        /// <summary>The DNS servers of the adapters that are up, IPv4 first, without repeats.</summary>
        public static List<IPAddress> SystemDnsServers()
        {
            var found = new List<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var address in nic.GetIPProperties().DnsAddresses)
                    {
                        // fec0:0:0:ffff::1 and friends are site-local placeholders Windows lists when nothing is set.
                        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6SiteLocal) continue;
                        if (!found.Contains(address)) found.Add(address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // No adapter information: the caller reports that there is no server to ask.
            }
            return found.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
        }

        /// <summary>
        /// Asks for one record type, or for the common ones when <paramref name="type"/> is null. An IP address is
        /// looked up backwards (its host name) whatever the type.
        /// </summary>
        public static async Task<DnsLookupResult> LookupAsync(string input, DnsRecordType? type, DnsServerChoice server, string customServer, CancellationToken token)
        {
            var started = Stopwatch.StartNew();
            string host = CleanHost(input);

            bool reverse = IPAddress.TryParse(host, out var address);
            string name = reverse ? DnsProtocol.ReverseName(address!) : host;
            var types = reverse ? new[] { DnsRecordType.PTR } : type is { } one ? new[] { one } : CommonTypes;

            if (!DnsProtocol.TryEncodeName(name, out _))
                return new DnsLookupResult { Status = DnsLookupStatus.BadName, QueryName = name, IsReverse = reverse };

            var servers = ResolveServers(server, customServer, out string problem);
            if (servers.Count == 0)
                return new DnsLookupResult { Status = DnsLookupStatus.Unreachable, QueryName = name, IsReverse = reverse, Detail = problem };

            var records = new List<DnsRecord>();
            DnsLookupStatus failure = DnsLookupStatus.NoRecords;
            string used = servers[0].ToString();
            bool anyAnswered = false;

            for (int i = 0; i < types.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var (status, answers, from) = await AskAsync(servers, name, types[i], token).ConfigureAwait(false);
                if (from != null) used = from.ToString();

                if (status == DnsLookupStatus.Ok)
                {
                    anyAnswered = true;
                    records.AddRange(answers);
                    continue;
                }

                // "No record of this type" is only about the type; anything else (no such name, a dead server) is the
                // same for every type, so stop asking.
                if (status != DnsLookupStatus.NoRecords)
                {
                    failure = status;
                    if (!anyAnswered) break;
                }
            }

            return new DnsLookupResult
            {
                Status = records.Count > 0 ? DnsLookupStatus.Ok : failure,
                Server = used,
                Records = records,
                Elapsed = started.Elapsed,
                QueryName = name,
                IsReverse = reverse,
            };
        }

        private static List<IPAddress> ResolveServers(DnsServerChoice choice, string custom, out string problem)
        {
            problem = "";
            if (choice.Id == SystemServer.Id)
            {
                var system = SystemDnsServers();
                if (system.Count == 0) problem = "System";
                return system;
            }

            string text = choice.Id == "custom" ? CleanHost(custom) : choice.Address;
            if (IPAddress.TryParse(text, out var address)) return new List<IPAddress> { address };

            problem = "Custom";
            return new List<IPAddress>();
        }

        /// <summary>Asks the servers in order until one answers (a timeout moves on to the next).</summary>
        private static async Task<(DnsLookupStatus Status, List<DnsRecord> Answers, IPAddress? From)> AskAsync(
            List<IPAddress> servers, string name, DnsRecordType type, CancellationToken token)
        {
            DnsLookupStatus last = DnsLookupStatus.Timeout;
            IPAddress? lastServer = null;

            foreach (var server in servers.Take(3))
            {
                lastServer = server;

                // UDP loses packets now and then, so a silent server gets one more try before the next server.
                DnsProtocol.Reply? reply = null;
                var failure = DnsLookupStatus.Timeout;
                for (int attempt = 0; attempt < 2 && reply == null; attempt++)
                {
                    ushort id = (ushort)Random.Shared.Next(0, 65536);
                    byte[] query = DnsProtocol.BuildQuery(name, type, id)!;
                    try
                    {
                        reply = await ExchangeAsync(server, query, id, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        failure = DnsLookupStatus.Timeout;
                    }
                    catch (SocketException)
                    {
                        failure = DnsLookupStatus.Unreachable;
                        break;
                    }
                    catch (Exception ex) when (ex is FormatException or IOException)
                    {
                        failure = DnsLookupStatus.BadReply;
                        break;
                    }
                }

                if (reply == null)
                {
                    last = failure;
                    continue;
                }

                switch (reply.ResponseCode)
                {
                    case 0:
                        // Only the type asked for (a CNAME chain on the way to it is kept).
                        var wanted = reply.Answers.Where(r => r.Type == type || r.Type == DnsRecordType.CNAME).ToList();
                        return wanted.Any(r => r.Type == type) || (type == DnsRecordType.CNAME && wanted.Count > 0)
                            ? (DnsLookupStatus.Ok, wanted, server)
                            : (DnsLookupStatus.NoRecords, new List<DnsRecord>(), server);
                    case 3: return (DnsLookupStatus.NameNotFound, new List<DnsRecord>(), server);
                    case 5: last = DnsLookupStatus.Refused; continue;
                    default: last = DnsLookupStatus.ServerFailure; continue;
                }
            }
            return (last, new List<DnsRecord>(), lastServer);
        }

        /// <summary>One question and its reply: UDP first, then TCP when the server says the answer didn't fit.</summary>
        private static async Task<DnsProtocol.Reply?> ExchangeAsync(IPAddress server, byte[] query, ushort id, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(QueryTimeout);

            using (var udp = new UdpClient(server.AddressFamily))
            {
                udp.Connect(server, 53);
                await udp.SendAsync(query, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    var reply = DnsProtocol.ParseReply(received.Buffer);
                    if (reply.Id != id) continue;   // a late answer to an earlier question
                    if (!reply.Truncated) return reply;
                    break;
                }
            }

            using var tcp = new TcpClient(server.AddressFamily);
            await tcp.ConnectAsync(server, 53, timeout.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var framed = new byte[query.Length + 2];
            framed[0] = (byte)(query.Length >> 8);
            framed[1] = (byte)query.Length;
            query.CopyTo(framed, 2);
            await stream.WriteAsync(framed, timeout.Token).ConfigureAwait(false);

            var length = new byte[2];
            await stream.ReadExactlyAsync(length, timeout.Token).ConfigureAwait(false);
            var body = new byte[(length[0] << 8) | length[1]];
            await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
            var tcpReply = DnsProtocol.ParseReply(body);
            return tcpReply.Id == id ? tcpReply : null;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "dns_tools.json");

        public static DnsToolsSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new DnsToolsSettings();
                var s = JsonSerializer.Deserialize<DnsToolsSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new DnsToolsSettings();
                if (!Enum.IsDefined(s.Mode)) s.Mode = DnsToolMode.Dns;
                if (s.RecordType is { } t && !Enum.IsDefined(t)) s.RecordType = null;
                if (!Servers.Any(x => x.Id == s.ServerId)) s.ServerId = SystemServer.Id;
                s.MaxHops = Math.Clamp(s.MaxHops, 5, 64);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new DnsToolsSettings();
            }
        }

        public static void SaveSettings(DnsToolsSettings settings)
        {
            try
            {
                AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Remembering the setup is a convenience; losing it is not worth an error.
            }
        }
    }
}
