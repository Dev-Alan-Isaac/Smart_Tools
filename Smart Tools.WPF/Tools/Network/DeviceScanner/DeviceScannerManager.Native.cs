using System.Net;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Windows-only half: an ARP request sent out of one specific adapter. Ping
    /// follows the routing table, so with several adapters (Wi-Fi + a USB Ethernet
    /// dongle) it can leave through the wrong one, or be dropped by the device's
    /// firewall. ARP is link-level: whatever sits behind the chosen adapter answers.
    /// </summary>
    public sealed partial class DeviceScannerManager
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macLength);

        /// <summary>MAC of <paramref name="target"/> as seen from the adapter owning <paramref name="source"/>, or null. Blocks up to ~3 s when nobody answers.</summary>
        private static string? ArpProbe(uint target, uint source)
        {
            try
            {
                var mac = new byte[6];
                uint length = (uint)mac.Length;
                int result = SendARP(BitConverter.ToUInt32(ToBytes(target), 0), BitConverter.ToUInt32(ToBytes(source), 0), mac, ref length);
                return result == 0 && length == 6 ? NormalizeMac(string.Join("-", mac.Select(b => b.ToString("X2")))) : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
            {
                return null;
            }
        }

        private static bool SameSubnet(uint a, uint b, int prefix)
        {
            if (prefix <= 0 || prefix > 32) return false;
            uint mask = uint.MaxValue << (32 - prefix);
            return (a & mask) == (b & mask);
        }
    }
}
