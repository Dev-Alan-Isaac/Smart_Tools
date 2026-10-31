using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Real network measurements. Ping first (ICMP, or timed HTTP requests
    /// when ICMP is blocked - the old test just showed N/A), then download
    /// and upload for a fixed time over several connections at once, with
    /// the round trip measured again while downloading (latency under load).
    /// Both directions report live speeds; a data limit protects metered
    /// connections.
    /// </summary>
    public sealed class SpeedTestEngine : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Func<string, CancellationToken, Task<double?>> _icmp;
        private readonly byte[] _uploadBlock = new byte[1 << 20];

        /// <param name="handler">Tests pass a handler for a local server; normally a fresh SocketsHttpHandler.</param>
        /// <param name="icmp">One ping in ms (null = no reply); tests pass a fake.</param>
        public SpeedTestEngine(HttpMessageHandler? handler = null, Func<string, CancellationToken, Task<double?>>? icmp = null)
        {
            _http = new HttpClient(handler ?? new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 16,
                AutomaticDecompression = DecompressionMethods.None,   // count the bytes that really crossed the line
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,   // each phase has its own time limit (the old fixed 60 s failed big tests on slow lines)
            };
            _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartTools-SpeedTest/1.0");
            _icmp = icmp ?? PingOnceAsync;
            Random.Shared.NextBytes(_uploadBlock);   // random, so nothing along the way can compress it
        }

        public void Dispose() => _http.Dispose();

        // =================================================================
        //  The whole test
        // =================================================================

        /// <summary>Runs ping, download and (if the server takes it) upload. Throws with a plain reason when the server can't be reached at all.</summary>
        public async Task<SpeedTestResult> RunAsync(SpeedTestOptions options, IProgress<NetSpeedSample>? progress = null, CancellationToken token = default)
        {
            var server = options.Server;
            bool upload = options.MeasureUpload && server.CanUpload;
            double latencyEnd = InternetSpeedTestManager.LatencySharePercent;
            double downloadEnd = InternetSpeedTestManager.DownloadEndPercent(upload);

            progress?.Report(new NetSpeedSample(SpeedPhase.Latency, 0, 0, 0, 0, "Measuring ping…"));
            var latency = await MeasureLatencyAsync(server, 8, token).ConfigureAwait(false);
            progress?.Report(new NetSpeedSample(SpeedPhase.Latency, 0, 0, latencyEnd, 0, latency == null ? "Ping didn't answer - measuring speed anyway…" : $"Ping {latency.MedianMs:0} ms"));

            // Round trips while the download runs, measured the same way as the idle ping.
            var loaded = new List<double>();
            var (downMbps, downBytes) = await TransferAsync(false, options, progress, latencyEnd, downloadEnd - latencyEnd, token,
                latency == null ? null : t => ProbeWhileLoadedAsync(server, latency.Method, loaded, t)).ConfigureAwait(false);

            double upMbps = -1;
            long upBytes = 0;
            if (upload) (upMbps, upBytes) = await TransferAsync(true, options, progress, downloadEnd, 100 - downloadEnd, token, null).ConfigureAwait(false);

            var result = new SpeedTestResult
            {
                ServerName = server.Display,
                PingMs = latency?.MedianMs ?? -1,
                JitterMs = latency?.JitterMs ?? -1,
                LoadedPingMs = loaded.Count >= 2 ? InternetSpeedTestManager.Median(loaded) : -1,
                DownloadMbps = downMbps,
                UploadMbps = upMbps,
                BytesUsed = downBytes + upBytes,
                LatencyMethod = latency?.Method ?? "",
            };
            progress?.Report(new NetSpeedSample(SpeedPhase.Done, 0, 0, 100, result.BytesUsed, "Done"));
            return result;
        }

        // =================================================================
        //  Latency
        // =================================================================

        /// <summary>Median and jitter of several round trips - ICMP when it answers, else timed HTTP requests. Null when neither works.</summary>
        public async Task<LatencyResult?> MeasureLatencyAsync(SpeedTestServer server, int count, CancellationToken token)
        {
            var icmp = new List<double>();
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (await _icmp(server.PingHost, token).ConfigureAwait(false) is double ms) icmp.Add(ms);
                if (i == 2 && icmp.Count == 0) break;   // blocked - don't wait for the rest
            }
            if (icmp.Count >= 3) return InternetSpeedTestManager.Summarise(icmp, "ICMP ping");

            var http = new List<double>();
            await HttpRoundTripAsync(server, token).ConfigureAwait(false);   // first request pays for DNS and TLS - not counted
            for (int i = 0; i < count; i++)
                if (await HttpRoundTripAsync(server, token).ConfigureAwait(false) is double ms) http.Add(ms);
            return InternetSpeedTestManager.Summarise(http, "HTTP round trip");
        }

        private async Task ProbeWhileLoadedAsync(SpeedTestServer server, string method, List<double> into, CancellationToken token)
        {
            await Task.Delay(1000, token).ConfigureAwait(false);   // let the download get going first
            while (!token.IsCancellationRequested)
            {
                double? ms = method.StartsWith("ICMP", StringComparison.Ordinal)
                    ? await _icmp(server.PingHost, token).ConfigureAwait(false)
                    : await HttpRoundTripAsync(server, token).ConfigureAwait(false);
                if (ms is double v) lock (into) into.Add(v);
                await Task.Delay(400, token).ConfigureAwait(false);
            }
        }

        /// <summary>Time to the response headers of a tiny request on a warm connection; null on failure.</summary>
        private async Task<double?> HttpRoundTripAsync(SpeedTestServer server, CancellationToken token)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await _http.GetAsync(InternetSpeedTestManager.CacheBust(server.LatencyUrl), HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                double ms = sw.Elapsed.TotalMilliseconds;
                return response.IsSuccessStatusCode ? ms : null;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null;   // this probe timed out
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        private static async Task<double?> PingOnceAsync(string host, CancellationToken token)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(1500), null, null, token).ConfigureAwait(false);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;   // blocked, or no such host
            }
        }

        // =================================================================
        //  Download / upload
        // =================================================================

        /// <summary>
        /// Moves data over <see cref="SpeedTestOptions.Streams"/> connections
        /// until the time is up (or the data limit is reached), sampling the
        /// running total ten times a second. Returns the steady speed and the
        /// bytes moved.
        /// </summary>
        private async Task<(double Mbps, long Bytes)> TransferAsync(
            bool upload, SpeedTestOptions options, IProgress<NetSpeedSample>? progress,
            double percentStart, double percentSpan, CancellationToken token,
            Func<CancellationToken, Task>? alongside)
        {
            var phase = upload ? SpeedPhase.Upload : SpeedPhase.Download;
            string verb = upload ? "Uploading" : "Downloading";
            long total = 0;      // bytes sent or received (data used, limit)
            long confirmed = 0;  // upload: bytes the server confirmed it received - what the speed is measured on
            Exception? lastError = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var samples = new List<(double Seconds, long Bytes)>();
            var completions = new List<(double Seconds, long Bytes)>();   // upload: the moment each request was confirmed
            var clock = Stopwatch.StartNew();
            double duration = Math.Max(0.5, options.Duration.TotalSeconds);
            long limit = options.DataLimitBytes;

            void Count(int n) => Interlocked.Add(ref total, n);
            bool LimitReached() => limit > 0 && Interlocked.Read(ref total) >= limit;
            long Measured() => upload ? Interlocked.Read(ref confirmed) : Interlocked.Read(ref total);

            async Task Worker()
            {
                int failures = 0;
                long chunk = InternetSpeedTestManager.FirstUploadChunkBytes;
                while (!stop.IsCancellationRequested && !LimitReached())
                {
                    try
                    {
                        if (upload)
                        {
                            var sent = Stopwatch.StartNew();
                            await UploadOnceAsync(options.Server, chunk, Count, LimitReached, stop.Token).ConfigureAwait(false);
                            long sum = Interlocked.Add(ref confirmed, chunk);
                            lock (completions) completions.Add((clock.Elapsed.TotalSeconds, sum));
                            chunk = InternetSpeedTestManager.NextUploadChunk(chunk, sent.Elapsed);
                        }
                        else
                        {
                            await DownloadOnceAsync(options.Server, Count, LimitReached, stop.Token).ConfigureAwait(false);
                        }
                        failures = 0;
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                    {
                        lastError = ex;
                        if (++failures >= 3) return;   // this connection keeps failing - let the others carry on
                        await Task.Delay(200, stop.Token).ConfigureAwait(false);
                    }
                }
            }

            var workers = Enumerable.Range(0, Math.Max(1, options.Streams)).Select(_ => Task.Run(Worker)).ToList();
            var side = alongside != null ? Task.Run(() => alongside(stop.Token)) : Task.CompletedTask;
            try
            {
                while (true)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    double t = clock.Elapsed.TotalSeconds;
                    long b = Interlocked.Read(ref total);
                    samples.Add((t, Measured()));
                    double share = Math.Max(t / duration, limit > 0 ? (double)b / limit : 0);
                    progress?.Report(new NetSpeedSample(phase, t, InternetSpeedTestManager.RecentMbps(samples, upload ? 2.0 : 1.0), percentStart + Math.Min(1, share) * percentSpan, b,
                        $"{verb}… {SpeedText.FormatBytes(b)}"));
                    if (t >= duration || LimitReached() || workers.All(w => w.IsCompleted)) break;
                }
            }
            finally
            {
                stop.Cancel();   // ends requests still in flight
                try
                {
                    await Task.WhenAll(workers.Append(side)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // workers end by cancellation; their errors are kept in lastError
                }
            }
            token.ThrowIfCancellationRequested();
            long moved = Interlocked.Read(ref total);
            if (Measured() == 0)
            {
                // A very slow line may not finish even the first small upload in time - that's a result, not an error.
                // lastError is never a real failure here: the check above already re-threw if the
                // caller's own token was cancelled, so any exception left in lastError is just the
                // last in-flight request being aborted by our own stop.Cancel() at the normal end of
                // this phase (an IOException, not the clean OperationCanceledException one might
                // expect) - never a reason to discard the bytes that genuinely got through.
                if (upload && moved > 0) return (InternetSpeedTestManager.ToMbps(moved, clock.Elapsed.TotalSeconds), moved);
                throw lastError ?? new IOException($"Nothing could be {(upload ? "uploaded" : "downloaded")}.");
            }
            // Upload: measure between real completion moments, not the sampling ticks (a request counts all at once).
            if (upload)
            {
                List<(double Seconds, long Bytes)> done;
                lock (completions) done = completions.OrderBy(c => c.Seconds).ThenBy(c => c.Bytes).ToList();
                if (done.Count >= 4) return (InternetSpeedTestManager.SteadyMbps(done), moved);
            }
            return (InternetSpeedTestManager.SteadyMbps(samples), moved);
        }

        private async Task DownloadOnceAsync(SpeedTestServer server, Action<int> count, Func<bool> limitReached, CancellationToken token)
        {
            string url = InternetSpeedTestManager.DownloadRequestUrl(server, InternetSpeedTestManager.DownloadChunkBytes);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                count(read);
                if (limitReached()) return;
            }
        }

        private async Task UploadOnceAsync(SpeedTestServer server, long bytes, Action<int> count, Func<bool> limitReached, CancellationToken token)
        {
            using var content = new CountingContent(_uploadBlock, bytes, count, limitReached);
            using var response = await _http.PostAsync(server.UploadUrl, content, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// An upload body that counts bytes as they're written (data used and
        /// the limit). The speed itself counts a request only once the server
        /// has confirmed it: bytes handed to the connection sit in Windows'
        /// buffers first, which read 15-40 % high in testing.
        /// </summary>
        private sealed class CountingContent : HttpContent
        {
            private readonly byte[] _block;
            private readonly long _length;
            private readonly Action<int> _count;
            private readonly Func<bool> _limitReached;

            public CountingContent(byte[] block, long length, Action<int> count, Func<bool> limitReached)
            {
                (_block, _length, _count, _limitReached) = (block, length, count, limitReached);
                Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                Headers.ContentLength = length;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
            {
                long sent = 0;
                const int Piece = 64 * 1024;
                while (sent < _length)
                {
                    int n = (int)Math.Min(Piece, _length - sent);
                    await stream.WriteAsync(_block.AsMemory((int)(sent % (_block.Length - Piece)), n), token).ConfigureAwait(false);
                    sent += n;
                    _count(n);
                    if (_limitReached()) throw new OperationCanceledException("Data limit reached.");
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _length;
                return true;
            }
        }
    }
}
