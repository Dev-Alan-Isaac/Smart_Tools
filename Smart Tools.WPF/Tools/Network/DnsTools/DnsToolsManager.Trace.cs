using Smart_Tools.WPF.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Traceroute: pings the destination with a Time To Live of 1, 2, 3... Each router that drops the packet
    /// answers with its own address, which draws the path. Three probes per hop are sent together.
    /// </summary>
    public sealed partial class DnsToolsManager
    {
        private const int ProbesPerHop = 3;
        private const int ProbeTimeoutMs = 2000;
        private static readonly TimeSpan NameTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Runs the trace. Each hop is reported as soon as it is known, and its host name later if names are on.
        /// Cancelling returns what was found so far.
        /// </summary>
        public static async Task<TraceSummary> TraceAsync(string input, int maxHops, bool resolveNames, IProgress<TraceUpdate> progress, CancellationToken token)
        {
            string host = CleanHost(input);
            if (host.Length == 0 || (!IPAddress.TryParse(host, out _) && !DnsProtocol.TryEncodeName(host, out _)))
                return new TraceSummary { Status = TraceStatus.BadHost };

            IPAddress target;
            try
            {
                target = IPAddress.TryParse(host, out var parsed) ? parsed : await FindAddressAsync(host, token).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return new TraceSummary { Status = TraceStatus.BadHost };
            }

            int hops = 0;
            bool reached = false;
            var names = new List<Task>();

            try
            {
                for (int ttl = 1; ttl <= maxHops && !reached; ttl++)
                {
                    token.ThrowIfCancellationRequested();
                    var probes = await Task.WhenAll(Enumerable.Range(0, ProbesPerHop).Select(_ => ProbeAsync(target, ttl, token))).ConfigureAwait(false);

                    var answered = probes.FirstOrDefault(p => p.From != null);
                    reached = probes.Any(p => p.Status == IPStatus.Success);
                    var hop = new TraceHop
                    {
                        Number = ttl,
                        Address = answered.From?.ToString() ?? "",
                        Times = probes.Select(p => p.From != null ? (long?)p.RoundTrip : null).ToArray(),
                        IsDestination = reached,
                    };
                    hops = ttl;
                    progress.Report(new TraceUpdate(hop, null));

                    if (resolveNames && answered.From != null) names.Add(ResolveNameAsync(hop, answered.From, progress, token));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Stopped: keep the hops found so far.
            }

            try
            {
                await Task.WhenAll(names).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Names that were still being looked up when the trace was stopped.
            }

            return new TraceSummary
            {
                Status = reached ? TraceStatus.Reached : TraceStatus.NotReached,
                Target = target.ToString(),
                HopCount = hops,
            };
        }

        private static async Task<IPAddress> FindAddressAsync(string host, CancellationToken token)
        {
            var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault()
                ?? throw new SocketException((int)SocketError.HostNotFound);
        }

        /// <summary>One packet with a limited lifetime. A router that drops it (or the destination) answers; silence is null.</summary>
        private static async Task<(IPStatus Status, IPAddress? From, long RoundTrip)> ProbeAsync(IPAddress target, int ttl, CancellationToken token)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(target, ProbeTimeoutMs, new byte[32], new PingOptions(ttl, true)).WaitAsync(token).ConfigureAwait(false);

                bool heardFrom = reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded
                    && reply.Address != null && !reply.Address.Equals(IPAddress.Any) && !reply.Address.Equals(IPAddress.IPv6Any);
                return (reply.Status, heardFrom ? reply.Address : null, reply.RoundtripTime);
            }
            catch (PingException)
            {
                return (IPStatus.Unknown, null, 0);
            }
        }

        private static async Task ResolveNameAsync(TraceHop hop, IPAddress address, IProgress<TraceUpdate> progress, CancellationToken token)
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(address).WaitAsync(NameTimeout, token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != address.ToString())
                    progress.Report(new TraceUpdate(hop, entry.HostName));
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                // Most routers have no name: the address alone is the normal case.
            }
        }
    }
}
