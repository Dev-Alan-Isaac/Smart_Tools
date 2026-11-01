using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One subnet, worked out. Addresses are text; counts are exact (IPv6 counts get very large).</summary>
    public sealed record SubnetInfo(
        bool IsIPv6, string Address, int Prefix, string Network, string? Mask, string? Wildcard,
        string? Broadcast, string FirstHost, string LastHost, BigInteger Total, BigInteger Usable, string Cidr, string? IpType)
    {
        public IEnumerable<(string Label, string Value)> Rows()
        {
            yield return ("Address", Address);
            yield return ("Network", Network + "/" + Prefix.ToString(CultureInfo.InvariantCulture));
            if (Mask != null) yield return ("Subnet mask", Mask);
            if (Wildcard != null) yield return ("Wildcard mask", Wildcard);
            if (Broadcast != null) yield return ("Broadcast", Broadcast);
            yield return ("First usable", FirstHost);
            yield return ("Last usable", LastHost);
            yield return ("Total addresses", Total.ToString("N0", CultureInfo.InvariantCulture));
            yield return ("Usable hosts", Usable.ToString("N0", CultureInfo.InvariantCulture));
            if (IpType != null) yield return ("Type", IpType);
        }
    }

    public sealed record SubnetPiece(string Cidr, string Range, string Broadcast);

    /// <summary>Subnet / CIDR calculator for IPv4 and IPv6. Pure logic, no WPF types.</summary>
    public static class SubnetCalculatorManager
    {
        public const int MaxSplitRows = 256;

        /// <summary>
        /// Reads "192.168.1.10/24", "192.168.1.10 255.255.255.0", "10.0.0.1" (a /32 host) or an IPv6 "2001:db8::/32".
        /// Null error means success.
        /// </summary>
        public static SubnetInfo? Calculate(string? text, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "Type an address, for example 192.168.1.10/24."; return null; }

            string s = text.Trim();
            string addressPart;
            string? prefixPart = null;
            int slash = s.IndexOf('/');
            if (slash >= 0)
            {
                addressPart = s[..slash].Trim();
                prefixPart = s[(slash + 1)..].Trim();
            }
            else
            {
                var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                addressPart = parts[0];
                if (parts.Length == 2) prefixPart = parts[1];
                else if (parts.Length > 2) { error = "Use address/prefix, or address and mask."; return null; }
            }

            if (!TryParseAddress(addressPart, out var ip)) { error = "That isn't a valid IP address."; return null; }
            bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
            int bits = v6 ? 128 : 32;

            int prefix = bits;
            if (prefixPart != null)
            {
                if (!TryParsePrefix(prefixPart, v6, out prefix, out error)) return null;
            }

            return Build(ip, prefix);
        }

        private static bool TryParseAddress(string text, out IPAddress ip)
        {
            ip = IPAddress.None;
            if (text.Contains(':'))
                return IPAddress.TryParse(text, out ip!) && ip.AddressFamily == AddressFamily.InterNetworkV6;
            var octets = text.Split('.');
            if (octets.Length != 4 || octets.Any(o => o.Length == 0 || o.Length > 3 || !o.All(char.IsAsciiDigit))) return false;
            return IPAddress.TryParse(text, out ip!) && ip.AddressFamily == AddressFamily.InterNetwork;
        }

        private static bool TryParsePrefix(string text, bool v6, out int prefix, out string? error)
        {
            error = null;
            prefix = 0;
            int max = v6 ? 128 : 32;
            if (!v6 && text.Contains('.'))
            {
                // A dotted mask: 255.255.255.0 (ones first, then zeros).
                if (!TryParseAddress(text, out var mask)) { error = "That isn't a valid subnet mask."; return false; }
                uint value = BitConverter.ToUInt32(mask.GetAddressBytes().Reverse().ToArray(), 0);
                int ones = BitOperations.PopCount(value);
                uint expected = ones == 0 ? 0 : uint.MaxValue << (32 - ones);
                if (value != expected) { error = "A subnet mask has all its 1s first, then all its 0s."; return false; }
                prefix = ones;
                return true;
            }
            if (text.StartsWith('/')) text = text[1..];
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > max)
            {
                error = $"The prefix must be a number from 0 to {max}.";
                return false;
            }
            return true;
        }

        private static SubnetInfo Build(IPAddress ip, int prefix)
        {
            bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
            byte[] addr = ip.GetAddressBytes();
            int bits = addr.Length * 8;
            var all = (BigInteger.One << bits) - 1;
            var hostBits = bits - prefix;
            var mask = prefix == 0 ? BigInteger.Zero : (all >> hostBits) << hostBits;
            var value = ToBig(addr);
            var network = value & mask;
            var last = network | (all ^ mask);
            var total = BigInteger.One << hostBits;

            BigInteger first, lastUsable, usable;
            if (v6) { first = network; lastUsable = last; usable = total; }
            else if (prefix == 32) { first = lastUsable = network; usable = 1; }
            else if (prefix == 31) { first = network; lastUsable = last; usable = 2; }
            else { first = network + 1; lastUsable = last - 1; usable = total - 2; }

            string Text(BigInteger n) => new IPAddress(FromBig(n, addr.Length)).ToString();
            return new SubnetInfo(
                v6, ip.ToString(), prefix, Text(network),
                v6 ? null : Text(mask), v6 ? null : Text(all ^ mask), v6 ? null : Text(last),
                Text(first), Text(lastUsable), total, usable,
                Text(network) + "/" + prefix.ToString(CultureInfo.InvariantCulture),
                v6 ? null : DescribeV4(addr));
        }

        private static string? DescribeV4(byte[] a) => a switch
        {
            [10, ..] => "Private (10.0.0.0/8)",
            [172, >= 16 and <= 31, ..] => "Private (172.16.0.0/12)",
            [192, 168, ..] => "Private (192.168.0.0/16)",
            [127, ..] => "Loopback",
            [169, 254, ..] => "Link-local (APIPA)",
            [100, >= 64 and <= 127, ..] => "Shared (carrier-grade NAT)",
            [>= 224 and <= 239, ..] => "Multicast",
            [>= 240, ..] => "Reserved",
            [0, ..] => "This network",
            _ => "Public",
        };

        /// <summary>Splits a network into equal subnets of <paramref name="newPrefix"/> (at most <see cref="MaxSplitRows"/> are listed).</summary>
        public static (List<SubnetPiece> Pieces, BigInteger TotalCount, string? Error) Split(SubnetInfo info, int newPrefix)
        {
            int bits = info.IsIPv6 ? 128 : 32;
            if (newPrefix <= info.Prefix || newPrefix > bits)
                return (new List<SubnetPiece>(), 0, $"Pick a prefix from /{info.Prefix + 1} to /{bits}.");

            var ip = IPAddress.Parse(info.Network);
            byte[] bytes = ip.GetAddressBytes();
            var start = ToBig(bytes);
            var size = BigInteger.One << (bits - newPrefix);
            var count = BigInteger.One << (newPrefix - info.Prefix);
            var pieces = new List<SubnetPiece>();
            for (int i = 0; i < MaxSplitRows && i < count; i++)
            {
                var net = start + size * i;
                var end = net + size - 1;
                string a = new IPAddress(FromBig(net, bytes.Length)).ToString();
                string b = new IPAddress(FromBig(end, bytes.Length)).ToString();
                pieces.Add(new SubnetPiece(a + "/" + newPrefix.ToString(CultureInfo.InvariantCulture), a + " - " + b, info.IsIPv6 ? "" : b));
            }
            return (pieces, count, null);
        }

        public static string ToText(SubnetInfo info, IEnumerable<SubnetPiece>? pieces)
        {
            var sb = new StringBuilder();
            foreach (var (label, value) in info.Rows()) sb.AppendLine($"{label}: {value}");
            if (pieces != null)
            {
                sb.AppendLine();
                foreach (var p in pieces) sb.AppendLine($"{p.Cidr}  {p.Range}");
            }
            return sb.ToString();
        }

        private static BigInteger ToBig(byte[] bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

        private static byte[] FromBig(BigInteger value, int length)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (bytes.Length == length) return bytes;
            var padded = new byte[length];
            Buffer.BlockCopy(bytes, Math.Max(0, bytes.Length - length), padded, Math.Max(0, length - bytes.Length), Math.Min(length, bytes.Length));
            return padded;
        }
    }
}
