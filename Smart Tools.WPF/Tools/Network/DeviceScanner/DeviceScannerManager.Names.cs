using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Name lookups that ask the device itself (unicast mDNS and NetBIOS), for
    /// devices the router's DNS can't name. Pure sockets - no WPF types.
    /// </summary>
    public sealed partial class DeviceScannerManager
    {
        private const int DirectLookupTimeoutMs = 1500;

        /// <summary>First name found by mDNS or NetBIOS, or null. Never throws except cancellation.</summary>
        private static async Task<string?> ResolveNameDirectAsync(string ipText, CancellationToken token)
        {
            if (!IPAddress.TryParse(ipText, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;

            var lookups = new List<Task<string?>> { QueryMdnsAsync(ip, token), QueryNetBiosAsync(ip, token) };
            while (lookups.Count > 0)
            {
                var finished = await Task.WhenAny(lookups).ConfigureAwait(false);
                lookups.Remove(finished);
                string? name = finished.IsCompletedSuccessfully ? finished.Result : null;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return null;
        }

        // ---------------- Roku: the name set on the device (External Control Protocol, port 8060) ----------------

        private static readonly HttpClient RokuHttp = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

        private static async Task<string?> QueryRokuFriendlyNameAsync(string ip, CancellationToken token)
        {
            try
            {
                string xml = await RokuHttp.GetStringAsync($"http://{ip}:8060/query/device-info", token).ConfigureAwait(false);
                foreach (string tag in new[] { "user-device-name", "friendly-device-name" })
                {
                    var m = System.Text.RegularExpressions.Regex.Match(xml, $"<{tag}>(.*?)</{tag}>");
                    string name = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                    if (m.Success && name.Length > 0) return name;
                }
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return null;
            }
        }

        // ---------------- mDNS: reverse PTR asked straight to the device (UDP 5353) ----------------

        private static async Task<string?> QueryMdnsAsync(IPAddress ip, CancellationToken token)
        {
            try
            {
                byte[] b = ip.GetAddressBytes();
                string arpa = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";

                var packet = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };   // id 0, flags 0, 1 question
                foreach (string label in arpa.Split('.'))
                {
                    packet.Add((byte)label.Length);
                    packet.AddRange(Encoding.ASCII.GetBytes(label));
                }
                packet.AddRange(new byte[] { 0, 0, 12, 0, 1 });                         // end of name, PTR, IN

                using var udp = new UdpClient(AddressFamily.InterNetwork);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(DirectLookupTimeoutMs);
                await udp.SendAsync(packet.ToArray(), new IPEndPoint(ip, 5353), cts.Token).ConfigureAwait(false);

                while (true)
                {
                    var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    if (!reply.RemoteEndPoint.Address.Equals(ip)) continue;
                    string? name = ParseMdnsPtr(reply.Buffer);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return null;
            }
        }

        private static string? ParseMdnsPtr(byte[] m)
        {
            if (m.Length < 12) return null;
            int questions = m[4] << 8 | m[5];
            int answers = (m[6] << 8 | m[7]) + (m[8] << 8 | m[9]) + (m[10] << 8 | m[11]);
            int pos = 12;
            for (int i = 0; i < questions; i++)
            {
                SkipName(m, ref pos);
                pos += 4;
            }
            for (int i = 0; i < answers && pos + 10 <= m.Length; i++)
            {
                SkipName(m, ref pos);
                if (pos + 10 > m.Length) return null;
                int type = m[pos] << 8 | m[pos + 1];
                int length = m[pos + 8] << 8 | m[pos + 9];
                pos += 10;
                if (type == 12)
                {
                    int p = pos;
                    string name = ReadName(m, ref p);
                    if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
                    return name.Length > 0 ? name : null;
                }
                pos += length;
            }
            return null;
        }

        private static void SkipName(byte[] m, ref int pos)
        {
            while (pos < m.Length)
            {
                byte len = m[pos];
                if (len == 0) { pos++; return; }
                if ((len & 0xC0) == 0xC0) { pos += 2; return; }
                pos += len + 1;
            }
        }

        private static string ReadName(byte[] m, ref int pos)
        {
            var parts = new List<string>();
            int p = pos;
            bool jumped = false;
            for (int guard = 0; guard < 64 && p < m.Length; guard++)
            {
                byte len = m[p];
                if (len == 0) { if (!jumped) pos = p + 1; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    if (p + 1 >= m.Length) break;
                    if (!jumped) pos = p + 2;
                    jumped = true;
                    p = (len & 0x3F) << 8 | m[p + 1];
                    continue;
                }
                if (p + 1 + len > m.Length) break;
                parts.Add(Encoding.UTF8.GetString(m, p + 1, len));
                p += len + 1;
            }
            return string.Join('.', parts);
        }

        // ---------------- NetBIOS node status (UDP 137) ----------------

        private static async Task<string?> QueryNetBiosAsync(IPAddress ip, CancellationToken token)
        {
            try
            {
                var packet = new List<byte> { 0x4E, 0x42, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x20, (byte)'C', (byte)'K' };
                for (int i = 0; i < 30; i++) packet.Add((byte)'A');                    // encoded wildcard name "*"
                packet.AddRange(new byte[] { 0, 0, 0x21, 0, 1 });                      // end of name, NBSTAT, IN

                using var udp = new UdpClient(AddressFamily.InterNetwork);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(DirectLookupTimeoutMs);
                await udp.SendAsync(packet.ToArray(), new IPEndPoint(ip, 137), cts.Token).ConfigureAwait(false);

                while (true)
                {
                    var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    if (!reply.RemoteEndPoint.Address.Equals(ip)) continue;
                    byte[] m = reply.Buffer;
                    // header 12 + name 34 + type/class/ttl/rdlength 10, then the name count
                    if (m.Length < 57) continue;
                    int count = m[56];
                    for (int i = 0; i < count && 57 + (i + 1) * 18 <= m.Length; i++)
                    {
                        int at = 57 + i * 18;
                        byte suffix = m[at + 15];
                        bool group = (m[at + 16] & 0x80) != 0;
                        if (suffix == 0x00 && !group)
                        {
                            string name = Encoding.ASCII.GetString(m, at, 15).Trim();
                            if (name.Length > 0) return name;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return null;
            }
        }
    }
}
