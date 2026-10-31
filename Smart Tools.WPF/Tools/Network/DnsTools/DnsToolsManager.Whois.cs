using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>WHOIS: ask IANA which server knows the name's ending (or the address), ask that server, follow one referral.</summary>
    public sealed partial class DnsToolsManager
    {
        private const string IanaWhois = "whois.iana.org";
        private const int MaxWhoisBytes = 256 * 1024;
        private static readonly TimeSpan WhoisTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Which server answers for an ending ("com" -> whois.verisign-grs.com), kept for the session. Empty = none.</summary>
        private static readonly ConcurrentDictionary<string, string> ServerForEnding = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex NotFoundText = new(
            @"^\s*[%#>]*\s*(no match|not found|no entries found|no data found|domain not found|nothing found|no object found|status:\s*free|object does not exist)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ReferralLine = new(
            @"^[ \t]*(?:registrar whois server|referralserver|whois server|refer)[ \t]*:[ \t]*(?:r?whois://)?([a-z0-9.-]+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// Looks up a domain or an IP address. For "www.example.com" it tries that name, then the names above it
        /// ("example.com"), because registries only know the registered name.
        /// </summary>
        public static async Task<WhoisResult> WhoisAsync(string input, CancellationToken token)
        {
            string host = CleanHost(input);
            bool isAddress = IPAddress.TryParse(host, out _);
            if (host.Length == 0 || (!isAddress && !DnsProtocol.TryEncodeName(host, out _)))
                return new WhoisResult { Status = WhoisStatus.BadQuery, Query = host };

            var candidates = new List<string>();
            if (isAddress) candidates.Add(host);
            else
            {
                var labels = new IdnMapping().GetAscii(host).Split('.');
                if (labels.Length < 2) return new WhoisResult { Status = WhoisStatus.BadQuery, Query = host };
                for (int skip = 0; skip <= labels.Length - 2; skip++) candidates.Add(string.Join('.', labels.Skip(skip)));
            }

            WhoisResult? last = null;
            foreach (string candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                last = await WhoisOneAsync(candidate, isAddress, token).ConfigureAwait(false);
                if (last.Status != WhoisStatus.NotFound) return last;
            }
            return last!;
        }

        private static async Task<WhoisResult> WhoisOneAsync(string query, bool isAddress, CancellationToken token)
        {
            try
            {
                string ending = isAddress ? query : query[(query.LastIndexOf('.') + 1)..];
                string? server = await FindServerAsync(ending, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(server))
                    return new WhoisResult { Status = WhoisStatus.NoServer, Query = query, Detail = ending };

                string raw = await WhoisQueryAsync(server, FormatQuery(server, query), token).ConfigureAwait(false);

                // A thin registry record names the registrar's own server, which has the details.
                var referral = ReferralLine.Match(raw);
                if (referral.Success && !referral.Groups[1].Value.Equals(server, StringComparison.OrdinalIgnoreCase))
                {
                    string next = referral.Groups[1].Value;
                    try
                    {
                        string more = await WhoisQueryAsync(next, FormatQuery(next, query), token).ConfigureAwait(false);

                        // Use the registrar's record only when it says at least as much as the registry's (a registrar
                        // such as IANA's own answers with a stub for the names it reserves).
                        if (more.Trim().Length > 0 && !NotFoundText.IsMatch(more) && ParseWhoisFields(more).Count >= ParseWhoisFields(raw).Count)
                        {
                            raw = more;
                            server = next;
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !token.IsCancellationRequested)
                    {
                        // The registrar's server is down or private: the registry's record is still worth showing.
                    }
                }

                if (NotFoundText.IsMatch(raw) || raw.Trim().Length == 0)
                    return new WhoisResult { Status = WhoisStatus.NotFound, Query = query, Server = server, Raw = raw };

                var fields = ParseWhoisFields(raw);
                return new WhoisResult
                {
                    Status = WhoisStatus.Ok,
                    Query = query,
                    Server = server,
                    Raw = raw,
                    Fields = fields,
                    ExpiryDate = ParseWhoisDate(fields.FirstOrDefault(f => f.Kind == WhoisFieldKind.Expires)?.Value),
                };
            }
            catch (Exception ex) when (ex is SocketException or System.IO.IOException or OperationCanceledException && !token.IsCancellationRequested)
            {
                return new WhoisResult { Status = WhoisStatus.Failed, Query = query, Detail = ex is OperationCanceledException ? "Timeout" : ex.Message };
            }
        }

        /// <summary>The WHOIS server for a name ending or an address, from IANA; "" when the ending has none.</summary>
        private static async Task<string?> FindServerAsync(string ending, CancellationToken token)
        {
            if (ServerForEnding.TryGetValue(ending, out var known)) return known;

            string answer = await WhoisQueryAsync(IanaWhois, ending, token).ConfigureAwait(false);
            var match = Regex.Match(answer, @"^[ \t]*(?:whois|refer)[ \t]*:[ \t]*([a-z0-9.-]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            string server = match.Success ? match.Groups[1].Value : "";
            ServerForEnding[ending] = server;
            return server;
        }

        /// <summary>Some servers want a keyword in front of the name; the rest take the name alone.</summary>
        public static string FormatQuery(string server, string query) => server.ToLowerInvariant() switch
        {
            "whois.verisign-grs.com" => "domain " + query,
            "whois.denic.de" => "-T dn,ace " + query,
            _ => query,
        };

        private static async Task<string> WhoisQueryAsync(string server, string query, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(WhoisTimeout);

            using var client = new TcpClient();
            await client.ConnectAsync(server, 43, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(query + "\r\n"), timeout.Token).ConfigureAwait(false);

            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[8192];
            int read;
            while (buffer.Length < MaxWhoisBytes && (read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                buffer.Write(chunk, 0, read);

            byte[] bytes = buffer.ToArray();
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }

        // =================================================================
        //  Reading the text (pure)
        // =================================================================

        private static readonly (WhoisFieldKind Kind, string[] Keys)[] FieldKeys =
        {
            (WhoisFieldKind.Registrar, new[] { "registrar", "registrar name", "sponsoring registrar" }),
            (WhoisFieldKind.Created, new[] { "creation date", "created", "created on", "registered", "registered on", "domain registration date" }),
            (WhoisFieldKind.Updated, new[] { "updated date", "last updated", "last modified", "last update", "modified", "changed" }),
            (WhoisFieldKind.Expires, new[] { "registry expiry date", "registrar registration expiration date", "expiry date", "expiration date", "expires", "expire date", "expires on", "paid-till", "renewal date" }),
            (WhoisFieldKind.NameServer, new[] { "name server", "nserver", "nameserver", "name servers" }),
            (WhoisFieldKind.Status, new[] { "domain status", "status" }),
            (WhoisFieldKind.Organization, new[] { "registrant organization", "registrant", "org-name", "orgname", "organization", "organisation", "owner", "org", "descr" }),
            (WhoisFieldKind.Country, new[] { "registrant country", "country" }),
            (WhoisFieldKind.NetName, new[] { "netname" }),
            (WhoisFieldKind.Range, new[] { "netrange", "inetnum", "inet6num", "cidr" }),
        };

        /// <summary>
        /// Picks the useful lines out of WHOIS text. Registries word the same thing differently ("Creation Date",
        /// "created", "Registered on"), so this goes by a table of the usual keys. Single values take the first line
        /// that matches; name servers and statuses collect every distinct one.
        /// </summary>
        public static List<WhoisField> ParseWhoisFields(string raw)
        {
            var fields = new List<WhoisField>();
            var seenSingle = new HashSet<WhoisFieldKind>();
            var seenMulti = new HashSet<(WhoisFieldKind, string)>();

            foreach (string rawLine in raw.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] is '%' or '#' or '>') continue;

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string key = line[..colon].Trim().ToLowerInvariant();
                string value = line[(colon + 1)..].Trim();
                if (value.Length == 0) continue;

                foreach (var (kind, keys) in FieldKeys)
                {
                    if (Array.IndexOf(keys, key) < 0) continue;

                    if (kind is WhoisFieldKind.NameServer or WhoisFieldKind.Status)
                    {
                        // "ns1.example.com 192.0.2.1" and "clientTransferProhibited https://icann.org/..." keep their first word.
                        string word = value.Split(' ', 2)[0];
                        string shown = kind == WhoisFieldKind.NameServer ? word.ToLowerInvariant() : word;
                        if (seenMulti.Count < 24 && seenMulti.Add((kind, shown))) fields.Add(new WhoisField(kind, shown));
                    }
                    else if (seenSingle.Add(kind))
                    {
                        fields.Add(new WhoisField(kind, value));
                    }
                    break;
                }
            }
            return fields;
        }

        /// <summary>The date out of a WHOIS value, or null when it is in a shape we don't know.</summary>
        public static DateTimeOffset? ParseWhoisDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
        }
    }
}
