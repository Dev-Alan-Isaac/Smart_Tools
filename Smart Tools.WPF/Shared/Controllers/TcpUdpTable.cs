using Smart_Tools.WPF.Models;
using System.Net;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Windows' table of TCP and UDP endpoints with the process that owns each (the data behind <c>netstat -ano</c>),
    /// read from iphlpapi. IPv4 and IPv6. Works without administrator rights. A moment in time: short-lived
    /// connections can come and go between two readings.
    /// </summary>
    public static class TcpUdpTable
    {
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int UDP_TABLE_OWNER_PID = 1;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        private static readonly string[] TcpStates =
            ["", "Closed", "Listen", "SynSent", "SynReceived", "Established", "FinWait1", "FinWait2", "CloseWait", "Closing", "LastAck", "TimeWait", "DeleteTcb"];

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        /// <summary>Every TCP and UDP endpoint on this PC. Call off the UI thread.</summary>
        public static List<EndpointUse> ReadAll()
        {
            var all = new List<EndpointUse>();
            ReadTcp(AF_INET, all);
            ReadTcp(AF_INET6, all);
            ReadUdp(AF_INET, all);
            ReadUdp(AF_INET6, all);
            return all;
        }

        private static int PortFromNetwork(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);

        private static void ReadTcp(int family, List<EndpointUse> into)
        {
            IntPtr table = ReadTable((IntPtr buf, ref int size) => GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0));
            if (table == IntPtr.Zero) return;
            try
            {
                int count = Marshal.ReadInt32(table);
                int rowSize = family == AF_INET ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = table + 4 + i * rowSize;
                    if (family == AF_INET)
                    {
                        int state = Marshal.ReadInt32(row);
                        var local = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                        int localPort = PortFromNetwork(Marshal.ReadInt32(row, 8));
                        var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                        int remotePort = PortFromNetwork(Marshal.ReadInt32(row, 16));
                        int pid = Marshal.ReadInt32(row, 20);
                        into.Add(new("TCP", local.ToString(), localPort, remote.ToString(), remotePort, StateName(state), pid));
                    }
                    else
                    {
                        var local = ReadIPv6(row);
                        int localPort = PortFromNetwork(Marshal.ReadInt32(row, 20));
                        var remote = ReadIPv6(row + 24);
                        int remotePort = PortFromNetwork(Marshal.ReadInt32(row, 44));
                        int state = Marshal.ReadInt32(row, 48);
                        int pid = Marshal.ReadInt32(row, 52);
                        into.Add(new("TCPv6", local.ToString(), localPort, remote.ToString(), remotePort, StateName(state), pid));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        private static void ReadUdp(int family, List<EndpointUse> into)
        {
            IntPtr table = ReadTable((IntPtr buf, ref int size) => GetExtendedUdpTable(buf, ref size, false, family, UDP_TABLE_OWNER_PID, 0));
            if (table == IntPtr.Zero) return;
            try
            {
                int count = Marshal.ReadInt32(table);
                int rowSize = family == AF_INET ? 12 : 28;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = table + 4 + i * rowSize;
                    if (family == AF_INET)
                    {
                        var local = new IPAddress((uint)Marshal.ReadInt32(row));
                        int localPort = PortFromNetwork(Marshal.ReadInt32(row, 4));
                        int pid = Marshal.ReadInt32(row, 8);
                        into.Add(new("UDP", local.ToString(), localPort, "*", 0, string.Empty, pid));
                    }
                    else
                    {
                        var local = ReadIPv6(row);
                        int localPort = PortFromNetwork(Marshal.ReadInt32(row, 20));
                        int pid = Marshal.ReadInt32(row, 24);
                        into.Add(new("UDPv6", local.ToString(), localPort, "*", 0, string.Empty, pid));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        private static string StateName(int state) => state > 0 && state < TcpStates.Length ? TcpStates[state] : state.ToString();

        private static IPAddress ReadIPv6(IntPtr at)
        {
            var bytes = new byte[16];
            Marshal.Copy(at, bytes, 0, 16);
            return new IPAddress(bytes);
        }

        private delegate int TableCall(IntPtr buffer, ref int size);

        /// <summary>Calls a size-then-fill table API (retrying if the table grew in between). Caller frees.</summary>
        private static IntPtr ReadTable(TableCall call)
        {
            int size = 0;
            call(IntPtr.Zero, ref size);
            for (int attempt = 0; attempt < 4 && size > 0; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                int result = call(buffer, ref size);
                if (result == 0) return buffer;
                Marshal.FreeHGlobal(buffer);
                if (result != ERROR_INSUFFICIENT_BUFFER) return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }
    }
}
