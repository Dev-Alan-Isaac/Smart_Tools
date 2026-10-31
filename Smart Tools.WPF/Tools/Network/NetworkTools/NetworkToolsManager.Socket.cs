using Smart_Tools.WPF.Models;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Network Tools - one TCP connection or UDP socket, like Hercules.
    /// Events are raised on a background thread; the view marshals them.
    /// Each session is its own object, so a late "closed" from an old
    /// connection can't tear down a new one (the old view could).
    /// </summary>
    public sealed class SocketSession : IAsyncDisposable
    {
        public SocketProtocol Protocol { get; }
        public string Host { get; }
        public int Port { get; }
        public int LocalPort { get; private set; }

        /// <summary>The local address it's bound to when an adapter was chosen (null = Windows picks).</summary>
        public IPAddress? Source { get; private set; }
        public IPEndPoint? Remote { get; private set; }
        public bool IsOpen { get; private set; }
        public long BytesSent => Interlocked.Read(ref _sent);
        public long BytesReceived => Interlocked.Read(ref _received);

        /// <summary>Data that arrived (and who from, for UDP).</summary>
        public event Action<SocketSession, byte[], IPEndPoint?>? Received;

        /// <summary>The session ended: null = closed by us, otherwise the reason.</summary>
        public event Action<SocketSession, string?>? Closed;

        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private UdpClient? _udp;
        private readonly CancellationTokenSource _cts = new();
        private Task? _readLoop;
        private long _sent, _received;
        private int _closed;

        private SocketSession(SocketProtocol protocol, string host, int port)
        {
            Protocol = protocol;
            Host = host;
            Port = port;
        }

        /// <summary>
        /// Connects with a timeout (the old view could hang ~21 s with no way to
        /// stop it). With <paramref name="source"/>, the connection leaves from that
        /// address (that adapter).
        /// </summary>
        public static async Task<SocketSession> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken token, IPAddress? source = null)
        {
            var s = new SocketSession(SocketProtocol.Tcp, host, port);
            var address = await NetworkToolsManager.ResolveAsync(host, token, source?.AddressFamily).ConfigureAwait(false);
            var tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
            if (source != null)
            {
                try
                {
                    tcp.Client.Bind(new IPEndPoint(source, 0));
                }
                catch
                {
                    tcp.Dispose();
                    throw;
                }
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            try
            {
                await tcp.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new TimeoutException($"No answer from {host}:{port} after {timeout.TotalSeconds:0} s.");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
            s._tcp = tcp;
            s.Source = source;
            s._stream = tcp.GetStream();
            s.Remote = (IPEndPoint?)tcp.Client.RemoteEndPoint;
            s.LocalPort = ((IPEndPoint)tcp.Client.LocalEndPoint!).Port;
            s.IsOpen = true;
            s._readLoop = Task.Run(s.TcpReadLoopAsync);
            return s;
        }

        /// <summary>
        /// Opens a UDP socket bound to <paramref name="localPort"/> (0 = any)
        /// in the same address family as the target (the old view could pick
        /// an IPv6 target for an IPv4 socket, and every send failed). With
        /// <paramref name="source"/>, it's bound to that address (that adapter).
        /// </summary>
        public static async Task<SocketSession> OpenUdpAsync(string host, int port, int localPort, CancellationToken token, IPAddress? source = null)
        {
            var s = new SocketSession(SocketProtocol.Udp, host, port);
            var address = await NetworkToolsManager.ResolveAsync(host, token, source?.AddressFamily).ConfigureAwait(false);
            var any = address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            var udp = new UdpClient(new IPEndPoint(source ?? any, localPort));
            if (OperatingSystem.IsWindows())
            {
                // Without this, one "port unreachable" reply makes Windows fail every later receive (WSAECONNRESET).
                const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
                try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch (SocketException) { }
            }
            s._udp = udp;
            s.Source = source;
            s.Remote = new IPEndPoint(address, port);
            s.LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            s.IsOpen = true;
            s._readLoop = Task.Run(s.UdpReadLoopAsync);
            return s;
        }

        public async Task SendAsync(byte[] data, CancellationToken token = default)
        {
            if (!IsOpen) throw new InvalidOperationException("Not connected.");
            if (_stream != null)
                await _stream.WriteAsync(data, token).ConfigureAwait(false);
            else if (_udp != null && Remote != null)
                await _udp.SendAsync(data, Remote, token).ConfigureAwait(false);
            Interlocked.Add(ref _sent, data.Length);
        }

        private async Task TcpReadLoopAsync()
        {
            var buffer = new byte[16 * 1024];
            string? reason = null;
            try
            {
                while (true)
                {
                    int read = await _stream!.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        reason = "Closed by the other side.";
                        break;
                    }
                    Interlocked.Add(ref _received, read);
                    Received?.Invoke(this, buffer[..read], null);
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // We closed it - not an error (the old view logged "Read error" on every disconnect).
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                reason = ex.InnerException is SocketException se ? Friendly(se) : ex.Message;
            }
            Finish(reason);
        }

        private async Task UdpReadLoopAsync()
        {
            string? reason = null;
            try
            {
                while (true)
                {
                    try
                    {
                        var result = await _udp!.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                        Interlocked.Add(ref _received, result.Buffer.Length);
                        Received?.Invoke(this, result.Buffer, result.RemoteEndPoint);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        // "Port unreachable" from the last send - keep listening.
                    }
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                reason = ex.Message;
            }
            Finish(reason);
        }

        private void Finish(string? reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            IsOpen = false;
            _stream?.Dispose();
            _tcp?.Dispose();
            _udp?.Dispose();
            Closed?.Invoke(this, reason);
        }

        public static string Friendly(SocketException ex) => ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Refused - nothing is listening on that port.",
            SocketError.ConnectionReset => "The other side reset the connection.",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => "Can't reach that address from this network.",
            SocketError.TimedOut => "Timed out.",
            SocketError.AddressAlreadyInUse => "That local port is already in use - pick another or use 0.",
            SocketError.AccessDenied => "Access denied - a firewall or another program is blocking it.",
            _ => ex.Message,
        };

        /// <summary>Closes the session; waits briefly for the read loop.</summary>
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _stream?.Dispose();
            _tcp?.Dispose();
            _udp?.Dispose();
            if (_readLoop != null) await Task.WhenAny(_readLoop, Task.Delay(1000)).ConfigureAwait(false);
            Finish(null);
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Decodes received bytes as UTF-8 across packets - a character split
    /// between two reads used to come out as two "�".
    /// </summary>
    public sealed class StreamTextDecoder
    {
        private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();

        public string Decode(byte[] data)
        {
            var chars = new char[_decoder.GetCharCount(data, 0, data.Length, flush: false)];
            int n = _decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
            return new string(chars, 0, n);
        }
    }
}
