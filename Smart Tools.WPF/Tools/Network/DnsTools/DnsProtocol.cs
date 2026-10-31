using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The DNS wire format (RFC 1035): building a question and reading the answer. Pure - no sockets, no WPF -
    /// so it can be checked against captured replies in a plain test project.
    /// </summary>
    public static class DnsProtocol
    {
        private const int HeaderLength = 12;
        private const int MaxLabelLength = 63;
        private const int MaxNameLength = 253;

        /// <summary>What a reply said, before the manager turns it into a result.</summary>
        public sealed class Reply
        {
            public ushort Id { get; init; }
            public int ResponseCode { get; init; }
            public bool Truncated { get; init; }
            public List<DnsRecord> Answers { get; init; } = new();
        }

        // =================================================================
        //  Asking
        // =================================================================

        /// <summary>
        /// A valid DNS name as bytes (length-prefixed labels). International names become their ASCII "xn--" form.
        /// False for empty names, labels over 63 characters, names over 253, or characters a host name can't have.
        /// </summary>
        public static bool TryEncodeName(string? name, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            string text = (name ?? "").Trim().TrimEnd('.');
            if (text.Length == 0) return false;

            string ascii;
            try
            {
                ascii = new IdnMapping().GetAscii(text);
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (ascii.Length == 0 || ascii.Length > MaxNameLength) return false;

            var output = new List<byte>(ascii.Length + 2);
            foreach (string label in ascii.Split('.'))
            {
                if (label.Length == 0 || label.Length > MaxLabelLength) return false;
                foreach (char c in label)
                    if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
                output.Add((byte)label.Length);
                foreach (char c in label) output.Add((byte)c);
            }
            output.Add(0);
            bytes = output.ToArray();
            return true;
        }

        /// <summary>A question for one record type, asking the server to look it up for us (recursion desired).</summary>
        public static byte[]? BuildQuery(string name, DnsRecordType type, ushort id)
        {
            if (!TryEncodeName(name, out var encoded)) return null;

            var query = new byte[HeaderLength + encoded.Length + 4];
            query[0] = (byte)(id >> 8);
            query[1] = (byte)id;
            query[2] = 0x01;   // flags: RD
            query[5] = 1;      // one question
            Array.Copy(encoded, 0, query, HeaderLength, encoded.Length);

            int p = HeaderLength + encoded.Length;
            query[p] = (byte)((ushort)type >> 8);
            query[p + 1] = (byte)(ushort)type;
            query[p + 3] = 1;  // class IN
            return query;
        }

        /// <summary>"4.3.2.1.in-addr.arpa" for 1.2.3.4, and the 32-nibble "ip6.arpa" form for IPv6.</summary>
        public static string ReverseName(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return string.Join('.', bytes.Reverse().Select(b => b.ToString(CultureInfo.InvariantCulture))) + ".in-addr.arpa";

            var sb = new StringBuilder();
            for (int i = bytes.Length - 1; i >= 0; i--)
                sb.Append((bytes[i] & 0x0F).ToString("x")).Append('.').Append((bytes[i] >> 4).ToString("x")).Append('.');
            return sb.Append("ip6.arpa").ToString();
        }

        // =================================================================
        //  Reading the reply
        // =================================================================

        /// <summary>Reads a reply. Throws <see cref="FormatException"/> when it is cut off or inconsistent.</summary>
        public static Reply ParseReply(byte[] data)
        {
            if (data.Length < HeaderLength) throw new FormatException("Short reply");

            ushort id = (ushort)((data[0] << 8) | data[1]);
            int flags = (data[2] << 8) | data[3];
            if ((flags & 0x8000) == 0) throw new FormatException("Not a reply");

            int questions = (data[4] << 8) | data[5];
            int answers = (data[6] << 8) | data[7];

            int pos = HeaderLength;
            for (int i = 0; i < questions; i++)
            {
                ReadName(data, ref pos);
                pos += 4;
                if (pos > data.Length) throw new FormatException("Cut off");
            }

            var records = new List<DnsRecord>(answers);
            for (int i = 0; i < answers; i++)
            {
                string name = ReadName(data, ref pos);
                if (pos + 10 > data.Length) throw new FormatException("Cut off");

                ushort type = (ushort)((data[pos] << 8) | data[pos + 1]);
                int ttl = (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];
                int length = (data[pos + 8] << 8) | data[pos + 9];
                pos += 10;
                if (pos + length > data.Length) throw new FormatException("Cut off");

                if (Enum.IsDefined(typeof(DnsRecordType), type) && ReadData((DnsRecordType)type, data, pos, length) is { } value)
                    records.Add(new DnsRecord((DnsRecordType)type, name, ttl < 0 ? 0 : ttl, value));
                pos += length;
            }

            return new Reply { Id = id, ResponseCode = flags & 0x0F, Truncated = (flags & 0x0200) != 0, Answers = records };
        }

        /// <summary>A name, following compression pointers (a pointer loop is an error, not a hang).</summary>
        public static string ReadName(byte[] data, ref int pos)
        {
            var sb = new StringBuilder();
            int p = pos, end = -1, jumps = 0;

            while (true)
            {
                if (p >= data.Length) throw new FormatException("Name runs off the end");
                int length = data[p];

                if (length == 0)
                {
                    p++;
                    break;
                }
                if ((length & 0xC0) == 0xC0)
                {
                    if (p + 1 >= data.Length) throw new FormatException("Bad pointer");
                    int target = ((length & 0x3F) << 8) | data[p + 1];
                    if (end < 0) end = p + 2;
                    if (++jumps > 32 || target >= data.Length) throw new FormatException("Bad pointer");
                    p = target;
                    continue;
                }
                if ((length & 0xC0) != 0 || p + 1 + length > data.Length) throw new FormatException("Bad label");

                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(data, p + 1, length));
                p += 1 + length;
            }

            pos = end >= 0 ? end : p;
            return sb.Length == 0 ? "." : sb.ToString();
        }

        /// <summary>The value of one record as text, or null when its data is damaged.</summary>
        private static string? ReadData(DnsRecordType type, byte[] data, int start, int length)
        {
            try
            {
                int pos = start, end = start + length;
                switch (type)
                {
                    case DnsRecordType.A:
                        return length == 4 ? new IPAddress(data.AsSpan(start, 4)).ToString() : null;
                    case DnsRecordType.AAAA:
                        return length == 16 ? new IPAddress(data.AsSpan(start, 16)).ToString() : null;
                    case DnsRecordType.NS:
                    case DnsRecordType.CNAME:
                    case DnsRecordType.PTR:
                        return ReadName(data, ref pos);
                    case DnsRecordType.MX:
                    {
                        int preference = (data[pos] << 8) | data[pos + 1];
                        pos += 2;
                        return $"{preference} {ReadName(data, ref pos)}";
                    }
                    case DnsRecordType.SRV:
                    {
                        int priority = (data[pos] << 8) | data[pos + 1];
                        int weight = (data[pos + 2] << 8) | data[pos + 3];
                        int port = (data[pos + 4] << 8) | data[pos + 5];
                        pos += 6;
                        return $"{priority} {weight} {port} {ReadName(data, ref pos)}";
                    }
                    case DnsRecordType.SOA:
                    {
                        string primary = ReadName(data, ref pos), mailbox = ReadName(data, ref pos);
                        uint Read32() { uint v = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]); pos += 4; return v; }
                        uint serial = Read32(), refresh = Read32(), retry = Read32(), expire = Read32(), minimum = Read32();
                        return $"{primary} {mailbox} {serial} {refresh} {retry} {expire} {minimum}";
                    }
                    case DnsRecordType.TXT:
                    {
                        // Several strings in one record are one value (long SPF and DKIM texts are split at 255 bytes).
                        var text = new StringBuilder();
                        while (pos < end)
                        {
                            int n = data[pos++];
                            if (pos + n > end) return null;
                            text.Append(Encoding.UTF8.GetString(data, pos, n));
                            pos += n;
                        }
                        return "\"" + text + "\"";
                    }
                    case DnsRecordType.CAA:
                    {
                        int flags = data[pos], tagLength = data[pos + 1];
                        string tag = Encoding.ASCII.GetString(data, pos + 2, tagLength);
                        string value = Encoding.UTF8.GetString(data, pos + 2 + tagLength, end - (pos + 2 + tagLength));
                        return $"{flags} {tag} \"{value}\"";
                    }
                    default:
                        return null;
                }
            }
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException)
            {
                return null;
            }
        }
    }
}
