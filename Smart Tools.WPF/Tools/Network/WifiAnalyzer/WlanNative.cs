using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Thin wrapper over Windows' Native Wifi API (wlanapi.dll). Results are read at fixed byte offsets of the
    /// documented structures (WLAN_BSS_ENTRY 360 bytes, WLAN_AVAILABLE_NETWORK 628, WLAN_INTERFACE_INFO 532,
    /// WLAN_PROFILE_INFO 516) so nothing depends on struct packing. Language independent, unlike parsing netsh.
    /// </summary>
    internal static class WlanNative
    {
        private const string Dll = "wlanapi.dll";

        public const int ErrorSuccess = 0;
        public const int ErrorAccessDenied = 5;
        public const int ErrorServiceNotActive = 1062;

        [DllImport(Dll)] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
        [DllImport(Dll)] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport(Dll)] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport(Dll)] private static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
        [DllImport(Dll)] private static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, int securityEnabled, IntPtr reserved, out IntPtr list);
        [DllImport(Dll)] private static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport(Dll)] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int opType);
        [DllImport(Dll)] private static extern uint WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
        [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern uint WlanGetProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);
        [DllImport(Dll)] private static extern void WlanFreeMemory(IntPtr memory);

        public sealed record Adapter(Guid Id, string Description, int State);

        public sealed record Bss(string Ssid, string Bssid, int Rssi, int Quality, int FrequencyKhz, int PhyType);

        public sealed record Available(string Ssid, bool Secured, int Auth, int Cipher, bool Connected);

        public sealed record Connection(string ProfileName, string Ssid, string Bssid, int PhyType, int Quality, long RxKbps, long TxKbps);

        /// <summary>An open session with the Wi-Fi service. Dispose it.</summary>
        public sealed class Session : IDisposable
        {
            private IntPtr _handle;

            private Session(IntPtr handle) => _handle = handle;

            /// <summary>Opens a session; null with the Windows error code when the Wi-Fi service isn't there.</summary>
            public static Session? Open(out int error)
            {
                error = (int)WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
                return error == ErrorSuccess ? new Session(handle) : null;
            }

            public List<Adapter> Adapters()
            {
                var list = new List<Adapter>();
                if (WlanEnumInterfaces(_handle, IntPtr.Zero, out var p) != ErrorSuccess) return list;
                try
                {
                    int count = Marshal.ReadInt32(p, 0);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = p + 8 + i * 532;
                        var guid = new Guid(ReadBytes(item, 0, 16));
                        string desc = Marshal.PtrToStringUni(item + 16, 256).Split('\0')[0];
                        list.Add(new Adapter(guid, desc, Marshal.ReadInt32(item, 528)));
                    }
                }
                finally { WlanFreeMemory(p); }
                return list;
            }

            /// <summary>Asks the adapter to look for networks again. The results arrive a few seconds later.</summary>
            public uint Scan(Guid iface) => WlanScan(_handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            public List<Bss> BssList(Guid iface, out uint error)
            {
                var result = new List<Bss>();
                error = WlanGetNetworkBssList(_handle, ref iface, IntPtr.Zero, 3, 0, IntPtr.Zero, out var p);
                if (error != ErrorSuccess) return result;
                try
                {
                    int count = Marshal.ReadInt32(p, 4);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr e = p + 8 + i * 360;
                        int ssidLen = Math.Min(32, Marshal.ReadInt32(e, 0));
                        string ssid = System.Text.Encoding.UTF8.GetString(ReadBytes(e, 4, ssidLen));
                        string bssid = string.Join(":", ReadBytes(e, 40, 6).Select(b => b.ToString("X2")));
                        result.Add(new Bss(ssid, bssid, Marshal.ReadInt32(e, 56), (int)Marshal.ReadInt32(e, 60), Marshal.ReadInt32(e, 92), Marshal.ReadInt32(e, 52)));
                    }
                }
                finally { WlanFreeMemory(p); }
                return result;
            }

            public List<Available> AvailableList(Guid iface)
            {
                var result = new List<Available>();
                if (WlanGetAvailableNetworkList(_handle, ref iface, 3, IntPtr.Zero, out var p) != ErrorSuccess) return result;
                try
                {
                    int count = Marshal.ReadInt32(p, 0);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr e = p + 8 + i * 628;
                        int ssidLen = Math.Min(32, Marshal.ReadInt32(e, 512));
                        string ssid = System.Text.Encoding.UTF8.GetString(ReadBytes(e, 516, ssidLen));
                        result.Add(new Available(ssid, Marshal.ReadInt32(e, 608) != 0, Marshal.ReadInt32(e, 612), Marshal.ReadInt32(e, 616), (Marshal.ReadInt32(e, 620) & 1) != 0));
                    }
                }
                finally { WlanFreeMemory(p); }
                return result;
            }

            /// <summary>The current connection of the adapter, or null when it isn't connected.</summary>
            public Connection? CurrentConnection(Guid iface)
            {
                if (WlanQueryInterface(_handle, ref iface, 7, IntPtr.Zero, out _, out var p, out _) != ErrorSuccess) return null;
                try
                {
                    if (Marshal.ReadInt32(p, 0) != 1) return null;   // wlan_interface_state_connected
                    string profile = Marshal.PtrToStringUni(p + 8, 256).Split('\0')[0];
                    int ssidLen = Math.Min(32, Marshal.ReadInt32(p, 520));
                    string ssid = System.Text.Encoding.UTF8.GetString(ReadBytes(p, 524, ssidLen));
                    string bssid = string.Join(":", ReadBytes(p, 560, 6).Select(b => b.ToString("X2")));
                    return new Connection(profile, ssid, bssid, Marshal.ReadInt32(p, 568), Marshal.ReadInt32(p, 576),
                        (uint)Marshal.ReadInt32(p, 580), (uint)Marshal.ReadInt32(p, 584));
                }
                finally { WlanFreeMemory(p); }
            }

            public List<string> ProfileNames(Guid iface)
            {
                var names = new List<string>();
                if (WlanGetProfileList(_handle, ref iface, IntPtr.Zero, out var p) != ErrorSuccess) return names;
                try
                {
                    int count = Marshal.ReadInt32(p, 0);
                    for (int i = 0; i < count; i++)
                        names.Add(Marshal.PtrToStringUni(p + 8 + i * 516, 256).Split('\0')[0]);
                }
                finally { WlanFreeMemory(p); }
                return names;
            }

            /// <summary>The profile as XML. With <paramref name="withKey"/> the password is included when Windows allows it (else error 5).</summary>
            public string? ProfileXml(Guid iface, string name, bool withKey, out uint error)
            {
                uint flags = withKey ? 4u : 0u;   // WLAN_PROFILE_GET_PLAINTEXT_KEY
                error = WlanGetProfile(_handle, ref iface, name, IntPtr.Zero, out var p, ref flags, out _);
                if (error != ErrorSuccess) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { WlanFreeMemory(p); }
            }

            public void Dispose()
            {
                if (_handle == IntPtr.Zero) return;
                WlanCloseHandle(_handle, IntPtr.Zero);
                _handle = IntPtr.Zero;
            }
        }

        private static byte[] ReadBytes(IntPtr p, int offset, int length)
        {
            var b = new byte[Math.Max(0, length)];
            if (b.Length > 0) Marshal.Copy(p + offset, b, 0, b.Length);
            return b;
        }
    }
}
