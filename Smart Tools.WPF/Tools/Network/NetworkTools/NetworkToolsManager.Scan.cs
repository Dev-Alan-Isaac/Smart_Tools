using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Network Tools - TCP port scanner.</summary>
    public static partial class NetworkToolsManager
    {
        /// <summary>Connection attempts at once. The old scanner started every port at the same time (1,000 sockets).</summary>
        public const int DefaultScanConcurrency = 200;

        /// <summary>
        /// Tries a TCP connection to each port, a limited number at a time.
        /// Open ports are reported as they're found (<paramref name="onOpen"/>)
        /// so results appear live; progress is reported at most every 100 ms.
        /// Cancelling returns what was found so far. With <paramref name="source"/>,
        /// every connection leaves from that address (that adapter).
        /// </summary>
        public static async Task<List<PortResult>> ScanPortsAsync(
            string host,
            IReadOnlyList<int> ports,
            int timeoutMs,
            IProgress<PortScanProgress>? progress,
            Action<PortResult>? onOpen,
            CancellationToken token,
            int concurrency = DefaultScanConcurrency,
            IPAddress? source = null)
        {
            var address = await ResolveAsync(host, token, source?.AddressFamily).ConfigureAwait(false);
            if (source != null) EnsureSourceUsable(source);
            var open = new List<PortResult>();
            int checkedCount = 0;
            int next = -1;
            var clock = Stopwatch.StartNew();
            long lastReport = -1000;
            var gate = new object();

            void Report(bool force)
            {
                lock (gate)
                {
                    long now = clock.ElapsedMilliseconds;
                    if (!force && now - lastReport < 100) return;
                    lastReport = now;
                    progress?.Report(new PortScanProgress(checkedCount, ports.Count, open.Count));
                }
            }

            async Task Worker()
            {
                while (!token.IsCancellationRequested)
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= ports.Count) return;
                    int port = ports[i];
                    long? latency = await TryConnectAsync(address, port, timeoutMs, token, source).ConfigureAwait(false);
                    if (latency is long ms)
                    {
                        var result = new PortResult(port, ServiceName(port), ms);
                        lock (gate) open.Add(result);
                        onOpen?.Invoke(result);
                    }
                    Interlocked.Increment(ref checkedCount);
                    Report(false);
                }
            }

            var workers = Enumerable.Range(0, Math.Clamp(concurrency, 1, Math.Max(1, ports.Count))).Select(_ => Worker()).ToList();
            await Task.WhenAll(workers).ConfigureAwait(false);
            Report(true);
            lock (gate) return open.OrderBy(p => p.Port).ToList();
        }

        /// <summary>
        /// Throws a clear error when the chosen adapter's address can't be bound
        /// (unplugged since it was picked). Without this every port's bind failed
        /// on its own and the scan reported "no open ports".
        /// </summary>
        public static void EnsureSourceUsable(IPAddress source)
        {
            try
            {
                using var probe = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(source, 0));
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"Can't send from {source} - that adapter isn't connected any more ({ex.SocketErrorCode}). Pick another under \"Send from\".", ex);
            }
        }

        /// <summary>Milliseconds to connect, or null when closed, filtered or timed out.</summary>
        public static async Task<long?> TryConnectAsync(IPAddress address, int port, int timeoutMs, CancellationToken token, IPAddress? source = null)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (source != null) socket.Bind(new IPEndPoint(source, 0));   // leave from that adapter; a bad source throws once, up front
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);
            var watch = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                return watch.ElapsedMilliseconds;
            }
            catch (OperationCanceledException)
            {
                return null;   // timed out (filtered) or the scan was cancelled
            }
            catch (SocketException)
            {
                return null;   // refused / unreachable
            }
        }

        public static string DescribeScan(string host, int total, int checkedCount, IReadOnlyList<PortResult> open, TimeSpan elapsed, bool cancelled)
        {
            string head = cancelled
                ? $"{host}: stopped after {checkedCount:N0} of {total:N0} ports"
                : $"{host}: {total:N0} port{(total == 1 ? "" : "s")} checked in {elapsed.TotalSeconds:0.0} s";
            return open.Count == 0
                ? head + " · no open ports"
                : head + $" · {open.Count} open: " + string.Join(", ", open.Take(40).Select(p => p.Label)) + (open.Count > 40 ? $" +{open.Count - 40} more" : "");
        }
    }
}
