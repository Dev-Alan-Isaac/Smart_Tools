using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Base64 &amp; URL Encoder / Decoder logic. No WPF here, so it compiles in a plain net8.0
    /// project. Four jobs: Base64 (standard and URL-safe, with or without padding), URL /
    /// percent encoding (a component or a whole URL), HTML entities, and files to and from
    /// Base64 (with data: URIs). Every conversion takes the text encoding explicitly
    /// (UTF-8, UTF-16 or Latin-1) and reports a bad input with the position of the first
    /// bad character instead of throwing.
    /// </summary>
    public static partial class EncodeToolManager
    {
        /// <summary>Text longer than this is refused (the boxes would not cope, and nobody types that).</summary>
        public const int MaxInputChars = 5_000_000;

        private const string Unreserved = "-._~";
        private const string UrlStructure = ":/?#[]@!$&'()*+,;=";

        // =================================================================
        //  Entry point for text
        // =================================================================

        public static EncodeToolResult Convert(string? input, EncodeToolOptions options)
        {
            input ??= "";
            if (input.Length > MaxInputChars) return EncodeToolResult.Fail("TooLarge");
            if (input.Length == 0) return new EncodeToolResult();

            return options.Mode switch
            {
                EncodeToolMode.Url => options.Direction == EncodeToolDirection.Encode ? UrlEncode(input, options) : UrlDecode(input, options),
                EncodeToolMode.Html => options.Direction == EncodeToolDirection.Encode ? HtmlEncode(input, options) : HtmlDecode(input),
                // File + Decode takes Base64 text like Base64 mode does; File + Encode goes through EncodeBytes.
                _ => options.Direction == EncodeToolDirection.Encode ? Base64Encode(input, options) : Base64Decode(input, options),
            };
        }

        public static Encoding GetEncoding(EncodeToolTextEncoding encoding) => encoding switch
        {
            EncodeToolTextEncoding.Utf16 => new UnicodeEncoding(false, false),
            EncodeToolTextEncoding.Latin1 => Encoding.Latin1,
            _ => new UTF8Encoding(false),
        };

        private static Encoding GetStrictEncoding(EncodeToolTextEncoding encoding) => encoding switch
        {
            EncodeToolTextEncoding.Utf16 => new UnicodeEncoding(false, false, true),
            EncodeToolTextEncoding.Latin1 => Encoding.Latin1,
            _ => new UTF8Encoding(false, true),
        };

        // =================================================================
        //  Base64
        // =================================================================

        public static EncodeToolResult Base64Encode(string input, EncodeToolOptions options)
        {
            var enc = GetEncoding(options.TextEncoding);
            byte[] bytes = enc.GetBytes(input);
            var notes = new List<string>();
            if (options.TextEncoding == EncodeToolTextEncoding.Latin1 && input.Any(c => c > 255)) notes.Add("Latin1Lossy");
            string text = FormatBase64(System.Convert.ToBase64String(bytes), options);
            return new EncodeToolResult { Output = text, Bytes = bytes, InputBytes = bytes.Length, OutputBytes = text.Length, Notes = notes };
        }

        /// <summary>Applies the alphabet, padding and 76-column wrapping to plain Base64.</summary>
        public static string FormatBase64(string standard, EncodeToolOptions options)
        {
            string s = standard;
            if (options.UrlSafeAlphabet) s = s.Replace('+', '-').Replace('/', '_');
            if (!options.Padding) s = s.TrimEnd('=');
            if (options.WrapLines && s.Length > 76)
            {
                var sb = new StringBuilder(s.Length + s.Length / 76 * 2 + 2);
                for (int i = 0; i < s.Length; i += 76)
                {
                    if (i > 0) sb.Append("\r\n");
                    sb.Append(s, i, Math.Min(76, s.Length - i));
                }
                s = sb.ToString();
            }
            return s;
        }

        /// <summary>
        /// Reads Base64 in either alphabet, with or without padding, with line breaks and spaces,
        /// and with a <c>data:...;base64,</c> prefix. Anything else is reported with its position.
        /// </summary>
        public static EncodeToolResult Base64Decode(string input, EncodeToolOptions options)
        {
            var notes = new List<string>();
            string mime = "";
            int start = 0;

            // data: URI prefix
            int lead = 0;
            while (lead < input.Length && char.IsWhiteSpace(input[lead])) lead++;
            if (input.AsSpan(lead).StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = input.IndexOf(',', lead);
                if (comma < 0) return EncodeToolResult.Fail("DataUriNoComma", lead, "data:");
                string head = input.Substring(lead + 5, comma - lead - 5);
                var parts = head.Split(';');
                if (!parts.Any(p => p.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase)))
                    return EncodeToolResult.Fail("DataUriNotBase64", lead, "data:");
                mime = parts[0].Trim();
                start = comma + 1;
                notes.Add("DataUri");
            }

            // Scan: validate characters, find the padding, remember which alphabet was used.
            int dataChars = 0, pad = 0, firstPad = -1;
            bool usesStd = false, usesUrl = false;
            int firstStd = -1, firstUrl = -1;
            for (int i = start; i < input.Length; i++)
            {
                char c = input[i];
                if (c is ' ' or '\r' or '\n' or '\t') continue;
                if (c == '=')
                {
                    if (firstPad < 0) firstPad = i;
                    if (++pad > 2) return EncodeToolResult.Fail("B64BadPadding", i, "=");
                    continue;
                }
                if (pad > 0) return EncodeToolResult.Fail("B64BadPadding", i, c.ToString());   // data after the padding
                if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') { dataChars++; continue; }
                if (c is '+' or '/') { usesStd = true; if (firstStd < 0) firstStd = i; dataChars++; continue; }
                if (c is '-' or '_') { usesUrl = true; if (firstUrl < 0) firstUrl = i; dataChars++; continue; }
                return EncodeToolResult.Fail("B64BadChar", i, Printable(input, i));
            }
            if (usesStd && usesUrl)
            {
                int at = Math.Max(firstStd, firstUrl);          // the character that broke the alphabet
                return EncodeToolResult.Fail("B64Mixed", at, input[at].ToString());
            }
            if (usesUrl) notes.Add("UrlSafeAlphabet");

            if (dataChars % 4 == 1) return EncodeToolResult.Fail("B64BadLength", Math.Max(0, input.Length - 1));
            if (pad > 0 && (dataChars + pad) % 4 != 0) return EncodeToolResult.Fail("B64BadPadding", firstPad, "=");
            if (pad == 0 && dataChars % 4 != 0) notes.Add("PaddingMissing");

            // Build the clean standard string and decode it.
            var clean = new StringBuilder(dataChars + 3);
            for (int i = start; i < input.Length; i++)
            {
                char c = input[i];
                if (c is ' ' or '\r' or '\n' or '\t' or '=') continue;
                clean.Append(c == '-' ? '+' : c == '_' ? '/' : c);
            }
            while (clean.Length % 4 != 0) clean.Append('=');

            byte[] bytes;
            try
            {
                bytes = System.Convert.FromBase64String(clean.ToString());
            }
            catch (FormatException)
            {
                return EncodeToolResult.Fail("B64BadChar", start);
            }

            return FromBytes(bytes, options.TextEncoding, input.Length, mime, notes);
        }

        // =================================================================
        //  URL / percent encoding
        // =================================================================

        public static EncodeToolResult UrlEncode(string input, EncodeToolOptions options)
        {
            var enc = GetEncoding(options.TextEncoding);
            var notes = new List<string>();
            bool fullUrl = options.UrlScope == EncodeToolUrlScope.FullUrl;
            var sb = new StringBuilder(input.Length + input.Length / 4);
            bool keptEscape = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c < 128 && (char.IsAsciiLetterOrDigit(c) || Unreserved.Contains(c))) { sb.Append(c); continue; }
                if (c == ' ' && options.SpaceAsPlus) { sb.Append('+'); continue; }
                if (fullUrl)
                {
                    if (UrlStructure.Contains(c)) { sb.Append(c); continue; }
                    // A real %XX that is already there stays (encoding it again would turn %20 into %2520).
                    if (c == '%' && i + 2 < input.Length && IsHex(input[i + 1]) && IsHex(input[i + 2]))
                    {
                        sb.Append(c);
                        keptEscape = true;
                        continue;
                    }
                }

                // Everything else goes out as bytes - a surrogate pair is one character.
                int length = char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]) ? 2 : 1;
                foreach (byte b in enc.GetBytes(input.Substring(i, length)))
                    sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                i += length - 1;
            }

            if (keptEscape) notes.Add("KeptEscapes");
            if (options.TextEncoding == EncodeToolTextEncoding.Latin1 && input.Any(c => c > 255)) notes.Add("Latin1Lossy");
            string text = sb.ToString();
            return new EncodeToolResult { Output = text, Bytes = enc.GetBytes(input), InputBytes = enc.GetByteCount(input), OutputBytes = text.Length, Notes = notes };
        }

        public static EncodeToolResult UrlDecode(string input, EncodeToolOptions options)
        {
            var enc = GetEncoding(options.TextEncoding);
            var bytes = new List<byte>(input.Length);
            var notes = new List<string>();
            bool sawPlus = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '%')
                {
                    if (i + 2 >= input.Length) return EncodeToolResult.Fail("UrlBadPercent", i, input.Substring(i));
                    if (!IsHex(input[i + 1]) || !IsHex(input[i + 2]))
                        return EncodeToolResult.Fail("UrlBadPercent", i, input.Substring(i, 3));
                    bytes.Add((byte)(HexValue(input[i + 1]) * 16 + HexValue(input[i + 2])));
                    i += 2;
                }
                else if (c == '+')
                {
                    sawPlus = true;
                    if (options.SpaceAsPlus) bytes.AddRange(enc.GetBytes(" "));
                    else bytes.AddRange(enc.GetBytes("+"));
                }
                else
                {
                    int length = char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]) ? 2 : 1;
                    bytes.AddRange(enc.GetBytes(input.Substring(i, length)));
                    i += length - 1;
                }
            }

            if (sawPlus && !options.SpaceAsPlus) notes.Add("PlusKept");
            return FromBytes(bytes.ToArray(), options.TextEncoding, input.Length, "", notes);
        }

        private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

        // =================================================================
        //  HTML entities
        // =================================================================

        public static EncodeToolResult HtmlEncode(string input, EncodeToolOptions options)
        {
            var sb = new StringBuilder(input.Length + 16);
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default:
                        if (options.HtmlAllNonAscii && c > 126)
                        {
                            int cp = c;
                            if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                                cp = char.ConvertToUtf32(c, input[++i]);
                            sb.Append("&#").Append(cp.ToString(CultureInfo.InvariantCulture)).Append(';');
                        }
                        else sb.Append(c);
                        break;
                }
            }
            string text = sb.ToString();
            return new EncodeToolResult { Output = text, InputBytes = Encoding.UTF8.GetByteCount(input), OutputBytes = Encoding.UTF8.GetByteCount(text) };
        }

        public static EncodeToolResult HtmlDecode(string input)
        {
            string text = WebUtility.HtmlDecode(input);
            var notes = new List<string>();
            if (text == input) notes.Add("NoEntities");
            return new EncodeToolResult { Output = text, InputBytes = Encoding.UTF8.GetByteCount(input), OutputBytes = Encoding.UTF8.GetByteCount(text), Notes = notes };
        }

        // =================================================================
        //  Files <-> Base64
        // =================================================================

        /// <summary>Base64 (or a data: URI) for a file's bytes.</summary>
        public static EncodeToolResult EncodeBytes(byte[] data, string fileName, EncodeToolOptions options)
        {
            string b64 = System.Convert.ToBase64String(data);
            string mime = MimeForFile(fileName);
            string text;
            if (options.DataUri)
                text = "data:" + mime + ";base64," + (options.UrlSafeAlphabet ? b64.Replace('+', '-').Replace('/', '_') : b64);
            else
                text = FormatBase64(b64, options);
            return new EncodeToolResult
            {
                Output = text,
                Bytes = data,
                InputBytes = data.Length,
                OutputBytes = text.Length,
                Mime = mime,
                DetectedKind = DetectKind(data),
                IsText = true,
            };
        }

        // =================================================================
        //  Shared helpers
        // =================================================================

        /// <summary>Turns decoded bytes into a result: valid text, text with replacement marks, or binary.</summary>
        private static EncodeToolResult FromBytes(byte[] bytes, EncodeToolTextEncoding encoding, int inputLength, string mime, List<string> notes)
        {
            string kind = DetectKind(bytes);
            bool strictOk = TryDecodeStrict(bytes, encoding, out string strict);
            bool binary = kind.Length > 0 || LooksBinary(bytes, encoding);
            string output;
            if (binary)
            {
                output = "";
                notes.Add("Binary");
            }
            else if (strictOk)
            {
                output = strict;
            }
            else
            {
                output = GetEncoding(encoding).GetString(bytes);          // replacement marks where the bytes do not fit
                notes.Add("NotValidText");
            }
            return new EncodeToolResult
            {
                Output = output,
                Bytes = bytes,
                IsText = !binary && strictOk,
                InputBytes = inputLength,
                OutputBytes = bytes.Length,
                DetectedKind = kind,
                Mime = mime,
                Notes = notes,
            };
        }

        private static bool TryDecodeStrict(byte[] bytes, EncodeToolTextEncoding encoding, out string text)
        {
            try
            {
                text = GetStrictEncoding(encoding).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = "";
                return false;
            }
        }

        /// <summary>NUL or other control bytes (not tab / line breaks) in what should be text. UTF-16 is full of zero bytes, so it is only judged by validity.</summary>
        private static bool LooksBinary(byte[] bytes, EncodeToolTextEncoding encoding)
        {
            if (encoding == EncodeToolTextEncoding.Utf16) return false;
            int n = Math.Min(bytes.Length, 8192);
            for (int i = 0; i < n; i++)
            {
                byte b = bytes[i];
                if (b < 0x20 && b is not (9 or 10 or 13)) return true;
            }
            return false;
        }

        /// <summary>The character at <paramref name="index"/> in a form that can be shown (control characters get a name).</summary>
        private static string Printable(string s, int index)
        {
            char c = s[index];
            if (char.IsControl(c) || c == ' ') return "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
            return c.ToString();
        }

        /// <summary>A short name for what the bytes start with, or empty. Codes are looked up as <c>Str_EncTool_Kind_{code}</c>.</summary>
        public static string DetectKind(byte[] b)
        {
            static bool Starts(byte[] data, params byte[] magic) => data.Length >= magic.Length && data.AsSpan(0, magic.Length).SequenceEqual(magic);
            if (b.Length < 3) return "";
            if (Starts(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "Png";
            if (Starts(b, 0xFF, 0xD8, 0xFF)) return "Jpeg";
            if (Starts(b, (byte)'G', (byte)'I', (byte)'F', (byte)'8')) return "Gif";
            if (Starts(b, (byte)'%', (byte)'P', (byte)'D', (byte)'F')) return "Pdf";
            if (Starts(b, 0x50, 0x4B, 0x03, 0x04)) return "Zip";
            if (Starts(b, 0x1F, 0x8B)) return "Gzip";
            if (Starts(b, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "SevenZip";
            if (Starts(b, (byte)'R', (byte)'a', (byte)'r', (byte)'!')) return "Rar";
            if (Starts(b, (byte)'O', (byte)'g', (byte)'g', (byte)'S')) return "Ogg";
            if (Starts(b, (byte)'I', (byte)'D', (byte)'3')) return "Mp3";
            if (Starts(b, 0x00, 0x00, 0x01, 0x00)) return "Ico";
            if (b.Length >= 12 && Starts(b, (byte)'R', (byte)'I', (byte)'F', (byte)'F') && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "Webp";
            if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return "Mp4";
            return "";
        }

        public static string ExtensionForKind(string kind) => kind switch
        {
            "Png" => ".png", "Jpeg" => ".jpg", "Gif" => ".gif", "Pdf" => ".pdf", "Zip" => ".zip", "Gzip" => ".gz",
            "SevenZip" => ".7z", "Rar" => ".rar", "Ogg" => ".ogg", "Mp3" => ".mp3", "Ico" => ".ico", "Webp" => ".webp", "Mp4" => ".mp4",
            _ => "",
        };

        public static string MimeForFile(string? fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
                ".svg" => "image/svg+xml", ".ico" => "image/x-icon", ".bmp" => "image/bmp", ".avif" => "image/avif",
                ".pdf" => "application/pdf", ".json" => "application/json", ".xml" => "application/xml", ".zip" => "application/zip",
                ".gz" => "application/gzip", ".txt" => "text/plain", ".html" or ".htm" => "text/html", ".css" => "text/css",
                ".js" => "text/javascript", ".csv" => "text/csv", ".md" => "text/markdown",
                ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".flac" => "audio/flac",
                ".mp4" => "video/mp4", ".webm" => "video/webm", ".woff" => "font/woff", ".woff2" => "font/woff2", ".ttf" => "font/ttf",
                _ => "application/octet-stream",
            };
        }

        /// <summary>A canvas for the hex pane: offset, hex bytes and the ASCII column, 16 bytes a line.</summary>
        public static string HexDump(byte[]? bytes, int maxBytes = 512)
        {
            if (bytes == null || bytes.Length == 0) return "";
            int n = Math.Min(bytes.Length, maxBytes);
            var sb = new StringBuilder(n * 4 + 64);
            for (int row = 0; row < n; row += 16)
            {
                sb.Append(row.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    if (row + i < n) sb.Append(bytes[row + i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    else sb.Append("   ");
                    if (i == 7) sb.Append(' ');
                }
                sb.Append(" |");
                for (int i = 0; i < 16 && row + i < n; i++)
                {
                    byte b = bytes[row + i];
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                sb.Append('|');
                if (row + 16 < n) sb.Append('\n');
            }
            if (bytes.Length > n) sb.Append("\n... ").Append(bytes.Length - n).Append(" more bytes");
            return sb.ToString();
        }
    }
}
