using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// "Send from": the adapter Ping, the port scanner and the TCP/UDP client
    /// use. Automatic lets Windows pick (its route table); choosing an adapter
    /// binds to that adapter's IPv4 address, so a USB Ethernet link can be
    /// tested on its own while Wi-Fi is also connected.
    /// </summary>
    public static partial class NetworkToolsManager
    {
        public static SourceChoice AutomaticSource { get; } = new("", "Automatic", "Windows picks the adapter", null, "");

        /// <summary>
        /// Automatic first, then each connected adapter with an IPv4 address
        /// (one choice per address). 169.254.x.x addresses are kept - that's what
        /// a direct cable to a device gets - but marked.
        /// </summary>
        public static List<SourceChoice> BuildSourceChoices(IEnumerable<AdapterInfo> adapters)
        {
            var list = new List<SourceChoice> { AutomaticSource };
            foreach (var a in adapters.Where(a => a.State == AdapterState.Connected))
            {
                foreach (var (address, prefix) in a.IPv4)
                {
                    if (!TryParseIPv4(address, out var ip)) continue;
                    bool linkLocal = address.StartsWith("169.254.", StringComparison.Ordinal);
                    string detail = $"{a.Kind} · {address}/{prefix}" + (linkLocal ? " · no DHCP (direct link)" : a.Gateways.Count > 0 ? " · internet" : "");
                    string id = a.IPv4.Count > 1 ? $"{a.Id}|{address}" : a.Id;
                    list.Add(new SourceChoice(id, a.Name, detail, ip, a.Kind));
                }
            }
            return list;
        }

        /// <summary>The remembered choice if it's still there (adapter connected), else Automatic.</summary>
        public static SourceChoice PickSource(IReadOnlyList<SourceChoice> choices, string? savedId)
        {
            if (string.IsNullOrEmpty(savedId)) return choices.FirstOrDefault(c => c.IsAutomatic) ?? AutomaticSource;
            return choices.FirstOrDefault(c => c.Id == savedId)
                   ?? choices.FirstOrDefault(c => !c.IsAutomatic && savedId.StartsWith(c.Id.Split('|')[0], StringComparison.OrdinalIgnoreCase))
                   ?? choices.FirstOrDefault(c => c.IsAutomatic) ?? AutomaticSource;
        }

        /// <summary>The source choices right now (reads the adapters; call off the UI thread).</summary>
        public static List<SourceChoice> GetSourceChoices() => BuildSourceChoices(GetAdapters(includeVirtual: true));

        // =================================================================
        //  Ping from a chosen address (iphlpapi IcmpSendEcho2Ex)
        // =================================================================

        /// <summary>
        /// One echo request from <paramref name="source"/> to <paramref name="destination"/>
        /// (both IPv4). Blocking for up to the timeout - run it off the UI thread.
        /// </summary>
        public static PingSample PingFrom(IPAddress source, IPAddress destination, int sequence, int timeoutMs)
        {
            if (source.AddressFamily != AddressFamily.InterNetwork || destination.AddressFamily != AddressFamily.InterNetwork)
                return new PingSample(sequence, destination.ToString(), "Choosing an adapter works for IPv4 only", null, null);

            IntPtr handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return new PingSample(sequence, destination.ToString(), new Win32Exception(Marshal.GetLastWin32Error()).Message, null, null);
            byte[] payload = new byte[32];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 23);   // like ping.exe
            int replySize = Marshal.SizeOf<ICMP_ECHO_REPLY>() + payload.Length + 8 + 16;
            IntPtr reply = Marshal.AllocHGlobal(replySize);
            try
            {
                uint count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    ToNetworkOrder(source), ToNetworkOrder(destination), payload, (ushort)payload.Length,
                    IntPtr.Zero, reply, (uint)replySize, (uint)timeoutMs);
                if (count == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    return new PingSample(sequence, destination.ToString(), DescribeIcmpError(error), null, null);
                }
                var r = Marshal.PtrToStructure<ICMP_ECHO_REPLY>(reply);
                string from = new IPAddress(BitConverter.GetBytes(r.Address)).ToString();
                return r.Status == 0
                    ? new PingSample(sequence, from, "Reply", r.RoundTripTime, r.Options.Ttl)
                    : new PingSample(sequence, destination.ToString(), DescribeIcmpError((int)r.Status), null, null);
            }
            finally
            {
                Marshal.FreeHGlobal(reply);
                IcmpCloseHandle(handle);
            }
        }

        /// <summary>11000-11050 are IP_STATUS codes (the same numbers as IPStatus); others are Windows errors.</summary>
        public static string DescribeIcmpError(int code) => code switch
        {
            >= 11000 and <= 11050 => Describe((IPStatus)code),
            87 => "That adapter's address can't be used to send right now",   // ERROR_INVALID_PARAMETER: source not on this PC
            1231 or 1232 => "Network unreachable from that adapter",
            _ => new Win32Exception(code).Message,
        };

        /// <summary>IPv4 address as the in_addr Windows expects (bytes in network order, read as a little-endian uint).</summary>
        public static uint ToNetworkOrder(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

        [StructLayout(LayoutKind.Sequential)]
        private struct IP_OPTION_INFORMATION
        {
            public byte Ttl;
            public byte Tos;
            public byte Flags;
            public byte OptionsSize;
            public IntPtr OptionsData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICMP_ECHO_REPLY
        {
            public uint Address;
            public uint Status;
            public uint RoundTripTime;
            public ushort DataSize;
            public ushort Reserved;
            public IntPtr Data;
            public IP_OPTION_INFORMATION Options;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern IntPtr IcmpCreateFile();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern bool IcmpCloseHandle(IntPtr handle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpSendEcho2Ex(
            IntPtr icmpHandle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext,
            uint sourceAddress, uint destinationAddress, byte[] requestData, ushort requestSize,
            IntPtr requestOptions, IntPtr replyBuffer, uint replySize, uint timeout);
    }
}
