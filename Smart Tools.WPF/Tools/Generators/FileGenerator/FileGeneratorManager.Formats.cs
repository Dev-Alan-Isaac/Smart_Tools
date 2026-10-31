using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  File Generator: one writer per format. Plain .NET only - no PdfSharp
    //  or Magick.NET - so every format is checked in the test project (PDF
    //  with qpdf, images with Pillow, DOCX as a zip).
    //
    //  Every format lands on the exact size asked for, with a VALID file:
    //   - TXT, BIN, DAT, custom: the fill itself (a pattern now carries on
    //     across buffers - the old one restarted every 8 KB).
    //   - CSV: the last row's value is lengthened; JSON / XML: spaces before
    //     the closing bracket/tag (the old ones stopped up to a row short).
    //   - PDF: comment lines after the header (offsets stay right).
    //   - PNG: a private ancillary chunk ("fiLl") before IEND.
    //   - JPG: comment (COM) segments.
    //   - DOCX: one stored (uncompressed) zip entry, measured first.
    //   - MP4: an "ftyp" + "free" box pair - a real MP4 container with no
    //     video in it (players open it and say it's empty).
    //  Only a target smaller than the format's minimum (a PDF can't be 10
    //  bytes) gives a bigger file; the grid says so.
    // =====================================================================
    public static partial class FileGeneratorManager
    {
        private const int ChunkSize = 1024 * 1024;

        private const string LoremIpsum =
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ";

        /// <summary>Writes one file's content to <paramref name="s"/>; <paramref name="written"/> gets the running byte count.</summary>
        public static void WriteContent(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            target = Math.Max(0, target);
            switch (o.Format)
            {
                case FileGenFormat.Txt: WriteTxt(s, target, o, now, written, ct); break;
                case FileGenFormat.Csv: WriteCsv(s, target, o, now, written, ct); break;
                case FileGenFormat.Json: WriteJson(s, target, o, now, written, ct); break;
                case FileGenFormat.Xml: WriteXml(s, target, o, now, written, ct); break;
                case FileGenFormat.Pdf: WritePdf(s, target, o, now, written, ct); break;
                case FileGenFormat.Docx: WriteDocx(s, target, o, now, written, ct); break;
                case FileGenFormat.Png: WritePng(s, target, o, now, written, ct); break;
                case FileGenFormat.Jpg: WriteJpg(s, target, o, now, written, ct); break;
                case FileGenFormat.Mp4: WriteMp4(s, target, written, ct); break;
                default: WriteFill(s, target, o, text: false, written, ct, 0); break;
            }
        }

        private static string PatternOrDefault(FileGenOptions o) => string.IsNullOrEmpty(o.PatternText) ? "SmartTools" : o.PatternText;

        private static string MetadataLine(FileGenOptions o, DateTime now) =>
            $"Generated {now:yyyy-MM-dd HH:mm:ss} by Smart Tools" + (string.IsNullOrWhiteSpace(o.MetadataTag) ? "" : $" - {o.MetadataTag}");

        // =================================================================
        //  Fill (raw formats and TXT)
        // =================================================================

        /// <summary>Endless bytes for the chosen fill. A pattern carries on across chunks (offset kept between calls).</summary>
        private sealed class Filler
        {
            private readonly FileGenFill _fill;
            private readonly byte[] _pattern;
            private readonly bool _text;
            private long _offset;
            private int _column;

            public Filler(FileGenOptions o, bool text)
            {
                _fill = o.Fill;
                _text = text;
                _pattern = o.Fill switch
                {
                    FileGenFill.RepeatedPattern => Encoding.UTF8.GetBytes(PatternOrDefault(o) + (text ? " " : "")),
                    FileGenFill.PlaceholderText => Encoding.UTF8.GetBytes(LoremIpsum),
                    FileGenFill.Zeroes when text => Encoding.ASCII.GetBytes(new string('0', 63) + "\n"),
                    _ => new byte[] { 0 },
                };
            }

            public void Fill(Span<byte> buffer)
            {
                if (_fill == FileGenFill.RandomBytes)
                {
                    Random.Shared.NextBytes(buffer);
                    if (_text) MakeReadable(buffer);
                    return;
                }
                if (_fill == FileGenFill.Zeroes && !_text)
                {
                    buffer.Clear();
                    return;
                }
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = _pattern[(int)((_offset + i) % _pattern.Length)];
                _offset += buffer.Length;
            }

            private const string Readable = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789    ";

            /// <summary>Random bytes → random letters and spaces, with a line break about every 80 characters.</summary>
            private void MakeReadable(Span<byte> buffer)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (++_column >= 80)
                    {
                        buffer[i] = (byte)'\n';
                        _column = 0;
                    }
                    else buffer[i] = (byte)Readable[buffer[i] % Readable.Length];
                }
            }
        }

        private static void WriteFill(Stream s, long count, FileGenOptions o, bool text, Action<long>? written, CancellationToken ct, long already)
        {
            var filler = new Filler(o, text);
            var buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(1, count))];
            long done = 0;
            while (done < count)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, count - done);
                var span = buffer.AsSpan(0, n);
                filler.Fill(span);
                if (text && done + n == count) FixUtf8Tail(span);
                s.Write(span);
                done += n;
                written?.Invoke(already + done);
            }
        }

        /// <summary>The file may end in the middle of a multi-byte character (é, 漢): spaces replace the broken bytes, so the text stays valid UTF-8.</summary>
        public static void FixUtf8Tail(Span<byte> span)
        {
            int i = span.Length - 1, back = 0;
            while (i >= 0 && back < 4 && (span[i] & 0xC0) == 0x80)
            {
                i--;
                back++;
            }
            if (i < 0 || span[i] < 0x80) return;   // ends on ASCII or on a full sequence's continuation run that started earlier
            int need = span[i] >= 0xF0 ? 4 : span[i] >= 0xE0 ? 3 : span[i] >= 0xC0 ? 2 : 1;
            if (back + 1 < need)
                for (int j = i; j < span.Length; j++) span[j] = (byte)' ';
        }

        private static void WriteBytes(Stream s, byte[] bytes, ref long done, Action<long>? written)
        {
            s.Write(bytes);
            done += bytes.Length;
            written?.Invoke(done);
        }

        // =================================================================
        //  TXT
        // =================================================================

        private static void WriteTxt(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            long done = 0;
            if (o.InjectMetadata)
            {
                byte[] header = Encoding.UTF8.GetBytes("# " + MetadataLine(o, now) + "\n");
                int n = (int)Math.Min(header.Length, target);
                var span = header.AsSpan(0, n).ToArray();
                if (n < header.Length) FixUtf8Tail(span);
                WriteBytes(s, span, ref done, written);
            }
            WriteFill(s, target - done, o, text: true, written, ct, done);
        }

        // =================================================================
        //  CSV - complete rows; the last row is lengthened to hit the size exactly
        // =================================================================

        private static string FillerValue(FileGenOptions o) => o.Fill switch
        {
            FileGenFill.PlaceholderText => "Lorem",
            FileGenFill.RepeatedPattern => PatternOrDefault(o),
            FileGenFill.Zeroes => "0",
            _ => "Row",
        };

        public static string CsvValue(string v) =>
            v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v != v.Trim() ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        private static void WriteCsv(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            var enc = new UTF8Encoding(false);
            using var w = new BufferedStream(s, ChunkSize);
            long done = 0;
            void Put(string text)
            {
                byte[] b = enc.GetBytes(text);
                w.Write(b);
                done += b.Length;
            }

            if (o.InjectMetadata) Put("# " + MetadataLine(o, now).Replace("\r", " ").Replace("\n", " ") + "\r\n");
            Put("Id,Name,Value,Timestamp\r\n");

            string value = FillerValue(o);
            string stamp = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            // The random column is always 5 digits, so a row's length only depends on its number and padding.
            string Row(long n, string pad) => $"{n},{CsvValue(value + n + pad)},{Random.Shared.Next(0, 100000):D5},{stamp}\r\n";

            long row = 1;
            while (true)
            {
                if ((row & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    written?.Invoke(done);
                }
                string line = Row(row, "");
                long lineBytes = enc.GetByteCount(line);
                if (done + lineBytes + lineBytes + 16 > target) break;   // keep room for the final, lengthened row
                Put(line);
                row++;
            }

            // The last row's value is lengthened so the row - and the file - end exactly on the target.
            long remaining = target - done;
            long minimal = enc.GetByteCount(Row(row, ""));
            if (remaining >= minimal)
                Put(Row(row, new string('x', (int)Math.Min(remaining - minimal, int.MaxValue / 4))));
            w.Flush();
            written?.Invoke(done);
        }

        // =================================================================
        //  JSON - items, then spaces before the closing bracket
        // =================================================================

        private static void WriteJson(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            var enc = new UTF8Encoding(false);
            using var w = new BufferedStream(s, ChunkSize);
            long done = 0;
            void Put(string text)
            {
                byte[] b = enc.GetBytes(text);
                w.Write(b);
                done += b.Length;
            }

            // JsonSerializer escapes everything that must be escaped (tabs, line breaks, quotes...).
            string opening = o.InjectMetadata
                ? "{\n  \"generated\": " + JsonSerializer.Serialize(now.ToString("o", CultureInfo.InvariantCulture))
                  + ",\n  \"by\": \"Smart Tools\",\n  \"tag\": " + JsonSerializer.Serialize(o.MetadataTag ?? "") + ",\n  \"items\": [\n"
                : "[\n";
            string closing = o.InjectMetadata ? "\n  ]\n}\n" : "\n]\n";
            string value = FillerValue(o);
            Put(opening);
            long closingBytes = enc.GetByteCount(closing);

            long index = 0;
            while (true)
            {
                if ((index & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    written?.Invoke(done);
                }
                string chunk = (index == 0 ? "    " : ",\n    ") + "{\"id\": " + index + ", \"value\": " + JsonSerializer.Serialize(value + index) + "}";
                long bytes = enc.GetByteCount(chunk);
                if (done + bytes + closingBytes > target) break;
                Put(chunk);
                index++;
            }
            PadSpaces(w, target - done - closingBytes, ref done, ct);
            Put(closing);
            w.Flush();
            written?.Invoke(done);
        }

        private static void PadSpaces(Stream w, long count, ref long done, CancellationToken ct)
        {
            if (count <= 0) return;
            var spaces = new byte[(int)Math.Min(ChunkSize, count)];
            Array.Fill(spaces, (byte)' ');
            while (count > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(spaces.Length, count);
                w.Write(spaces, 0, n);
                count -= n;
                done += n;
            }
        }

        // =================================================================
        //  XML - items, then spaces before the closing tag
        // =================================================================

        /// <summary>Escapes &amp; &lt; &gt; " ' and drops characters XML 1.0 doesn't allow (the old escape missed quotes, so a tag with " broke the file).</summary>
        public static string XmlText(string? value)
        {
            var sb = new StringBuilder();
            foreach (char c in value ?? "")
                if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF)) sb.Append(c);
            return SecurityElement.Escape(sb.ToString()) ?? "";
        }

        private static void WriteXml(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            var enc = new UTF8Encoding(false);
            using var w = new BufferedStream(s, ChunkSize);
            long done = 0;
            void Put(string text)
            {
                byte[] b = enc.GetBytes(text);
                w.Write(b);
                done += b.Length;
            }

            Put("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Items"
                + (o.InjectMetadata ? $" generated=\"{now.ToString("o", CultureInfo.InvariantCulture)}\" by=\"Smart Tools\" tag=\"{XmlText(o.MetadataTag)}\"" : "")
                + ">\n");
            const string closing = "</Items>\n";
            string value = XmlText(FillerValue(o));

            long index = 0;
            while (true)
            {
                if ((index & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    written?.Invoke(done);
                }
                string entry = $"  <Item id=\"{index}\">{value}{index}</Item>\n";
                if (done + enc.GetByteCount(entry) + closing.Length > target) break;
                Put(entry);
                index++;
            }
            PadSpaces(w, target - done - closing.Length, ref done, ct);
            Put(closing);
            w.Flush();
            written?.Invoke(done);
        }

        // =================================================================
        //  PDF - a small valid PDF written by hand; comment lines make up the size
        // =================================================================

        private static string PdfLiteral(string text)
        {
            var sb = new StringBuilder("(");
            foreach (char c in text)
            {
                char ch = c is >= ' ' and <= '~' ? c : '?';   // the built-in Helvetica only has Latin letters
                if (ch is '(' or ')' or '\\') sb.Append('\\');
                sb.Append(ch);
            }
            return sb.Append(')').ToString();
        }

        /// <summary>A PDF text string that can hold any character (UTF-16 with a byte-order mark, as hex).</summary>
        private static string PdfUnicode(string text) =>
            "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

        /// <summary>The PDF's first two lines and its objects (numbered from 1; object 6 is the document info).</summary>
        private static (string Header, List<string> Objects) PdfParts(FileGenOptions o, DateTime now)
        {
            string line1 = o.InjectMetadata ? MetadataLine(o, now) : "Generated by Smart Tools File Generator";
            string content = $"BT /F1 14 Tf 56 780 Td {PdfLiteral(line1)} Tj ET\nBT /F1 10 Tf 56 760 Td {PdfLiteral("A test file - its size was set on purpose.")} Tj ET\n";
            string title = o.InjectMetadata && !string.IsNullOrWhiteSpace(o.MetadataTag) ? o.MetadataTag : "Generated test file";
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
                $"<< /Producer (Smart Tools File Generator) /CreationDate (D:{now:yyyyMMddHHmmss}) /Title {PdfUnicode(title)}"
                    + (o.InjectMetadata ? $" /Keywords {PdfUnicode(o.MetadataTag ?? "")} /Creator (Smart Tools)" : "") + " >>",
            };
            return ("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n", objects);
        }

        /// <summary>Everything after the padding: the objects, the cross-reference table (offsets include the padding) and the trailer.</summary>
        private static string PdfBody(List<string> objects, long bodyStart)
        {
            var sb = new StringBuilder();
            var offsets = new long[objects.Count];
            for (int i = 0; i < objects.Count; i++)
            {
                offsets[i] = bodyStart + sb.Length;   // Latin-1: one byte per character
                sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            long xref = bodyStart + sb.Length;
            sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (long off in offsets) sb.Append($"{off:D10} 00000 n \n");
            sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return sb.ToString();
        }

        /// <summary>
        /// Header, then padding as comment lines (a lone line break for a
        /// 1-byte remainder), then the objects - so the cross-reference
        /// offsets already include the padding and the PDF stays valid.
        /// </summary>
        private static void WritePdf(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            var (header, objects) = PdfParts(o, now);
            var latin1 = Encoding.Latin1;
            // The startxref number grows a digit as the padding grows, so settle the padding in a few rounds.
            long pad = 0;
            for (int round = 0; round < 6; round++)
            {
                long next = target - header.Length - PdfBody(objects, header.Length + pad).Length;
                if (next <= 0) { pad = 0; break; }
                if (next == pad) break;
                pad = next;
            }

            using var w = new BufferedStream(s, ChunkSize);
            long done = 0;
            w.Write(latin1.GetBytes(header));
            done += header.Length;

            var line = new byte[255];
            Array.Fill(line, (byte)'x');
            line[0] = (byte)'%';
            long left = pad;
            long lines = 0;
            while (left > 0)
            {
                if (left == 1)
                {
                    w.WriteByte((byte)'\n');
                    left = 0;
                    break;
                }
                int len = (int)Math.Min(left, 255);
                if (left - len == 1) len--;      // keep the last line at least 2 bytes ("%\n")
                w.Write(line, 0, len - 1);
                w.WriteByte((byte)'\n');
                left -= len;
                if ((++lines & 4095) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    written?.Invoke(done + pad - left);
                }
            }
            done += pad;
            byte[] body = latin1.GetBytes(PdfBody(objects, header.Length + pad));
            w.Write(body);
            done += body.Length;
            w.Flush();
            written?.Invoke(done);
        }

        // =================================================================
        //  DOCX - a minimal Word document; one stored entry makes up the size
        // =================================================================

        private const string DocxRelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        private static void WriteDocxParts(ZipArchive zip, FileGenOptions o, DateTime now, long padding, Action<long>? written, CancellationToken ct)
        {
            bool meta = o.InjectMetadata;
            string text = XmlText(meta ? MetadataLine(o, now) : "Generated by Smart Tools File Generator");
            Text(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"bin\" ContentType=\"application/octet-stream\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                (meta ? "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" : "") +
                "</Types>");
            Text(zip, "_rels/.rels",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"{DocxRelsNs}\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                (meta ? "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" : "") +
                "</Relationships>");
            Text(zip, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                $"<w:body><w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p></w:body></w:document>");
            if (meta)
            {
                string stamp = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                Text(zip, "docProps/core.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                    "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                    $"<dc:title>{XmlText(o.MetadataTag)}</dc:title><dc:creator>Smart Tools</dc:creator><cp:keywords>{XmlText(o.MetadataTag)}</cp:keywords>" +
                    $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{stamp}</dcterms:created></cp:coreProperties>");
            }

            // Always present (even empty) so the measured size and the real size have the same entries.
            var entry = zip.CreateEntry("docProps/padding.bin", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var es = entry.Open();
            if (padding <= 0) return;
            var buffer = new byte[(int)Math.Min(ChunkSize, padding)];
            Random.Shared.NextBytes(buffer);
            long done = 0;
            while (done < padding)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, padding - done);
                es.Write(buffer, 0, n);
                done += n;
                written?.Invoke(done);
            }

            static void Text(ZipArchive zip, string name, string content)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var st = e.Open();
                st.Write(new UTF8Encoding(false).GetBytes(content));
            }
        }

        private static void WriteDocx(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            long baseSize;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                    WriteDocxParts(zip, o, now, 0, null, CancellationToken.None);
                baseSize = ms.Length;
            }
            long padding = Math.Max(0, target - baseSize);
            using var real = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true);
            WriteDocxParts(real, o, now, padding, written, ct);
        }

        // =================================================================
        //  PNG - a solid-colour 800×600 image; a private chunk makes up the size
        // =================================================================

        private const int ImageWidth = 640, ImageHeight = 480;

        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        private static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static void PngChunk(Stream s, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);
            byte[] t = Encoding.ASCII.GetBytes(type);
            s.Write(t);
            s.Write(data);
            uint crc = Crc(Crc(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, crc);
            s.Write(len);
        }

        private static (byte R, byte G, byte B) RandomColour() =>
            ((byte)Random.Shared.Next(40, 216), (byte)Random.Shared.Next(40, 216), (byte)Random.Shared.Next(40, 216));

        public static byte[] BuildPngBody(FileGenOptions o, DateTime now, (byte R, byte G, byte B) colour)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), ImageWidth);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), ImageHeight);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 2;   // truecolour
            PngChunk(ms, "IHDR", ihdr);

            if (o.InjectMetadata)
            {
                PngChunk(ms, "iTXt", ITxt("Software", "Smart Tools File Generator"));
                PngChunk(ms, "iTXt", ITxt("Comment", MetadataLine(o, now)));
                PngChunk(ms, "iTXt", ITxt("Creation Time", now.ToString("R", CultureInfo.InvariantCulture)));
            }

            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var row = new byte[1 + ImageWidth * 3];
                    for (int x = 0; x < ImageWidth; x++)
                    {
                        row[1 + x * 3] = colour.R;
                        row[2 + x * 3] = colour.G;
                        row[3 + x * 3] = colour.B;
                    }
                    for (int y = 0; y < ImageHeight; y++) z.Write(row);
                }
                PngChunk(ms, "IDAT", raw.ToArray());
            }
            return ms.ToArray();

            static byte[] ITxt(string key, string text)
            {
                var b = new List<byte>(Encoding.Latin1.GetBytes(key)) { 0, 0, 0, 0, 0 };   // keyword\0, no compression, method, empty language\0, empty translated keyword\0
                b.AddRange(Encoding.UTF8.GetBytes(text));
                return b.ToArray();
            }
        }

        private static void WritePng(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            byte[] body = BuildPngBody(o, now, RandomColour());
            const int iendSize = 12, chunkOverhead = 12;
            long done = 0;
            WriteBytes(s, body, ref done, written);

            long fill = target - body.Length - iendSize;
            long trailing = 0;
            if (fill is > 0 and < chunkOverhead)
            {
                trailing = fill;   // too small for a chunk: a few bytes after IEND (every reader stops at IEND)
                fill = 0;
            }
            // Ancillary, private, safe-to-copy chunk "fiLl" (viewers skip chunks they don't know).
            var buffer = Array.Empty<byte>();
            while (fill >= chunkOverhead)
            {
                ct.ThrowIfCancellationRequested();
                long data = Math.Min(fill - chunkOverhead, 64L * 1024 * 1024);
                if (fill - chunkOverhead - data is > 0 and < chunkOverhead) data -= chunkOverhead;   // leave room for one more full chunk
                if (buffer.Length < data)
                {
                    buffer = new byte[data];
                    Random.Shared.NextBytes(buffer);
                }
                PngChunk(s, "fiLl", buffer.AsSpan(0, (int)data));
                fill -= data + chunkOverhead;
                done += data + chunkOverhead;
                written?.Invoke(done);
            }
            PngChunk(s, "IEND", ReadOnlySpan<byte>.Empty);
            done += iendSize;
            if (trailing > 0)
            {
                s.Write(new byte[trailing]);
                done += trailing;
            }
            written?.Invoke(done);
        }

        // =================================================================
        //  JPG - a solid-colour 800×600 baseline JPEG (written here, no
        //  library); comment segments make up the size
        // =================================================================

        // Every block is the same flat colour, so after the first block every DC difference is 0: give
        // "0" a 1-bit code (the other 11 categories get 5-bit codes, none all 1s). AC blocks are always
        // empty, so one code (end of block) is enough. Keeps the smallest JPG under 2 KB.
        private static readonly byte[] DcLumBits = { 1, 0, 0, 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] DcChromaBits = DcLumBits;
        private static readonly byte[] DcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        private static readonly byte[] AcBits = { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] AcValues = { 0x00 };

        /// <summary>Canonical Huffman codes (code, length) by symbol.</summary>
        private static Dictionary<byte, (int Code, int Length)> HuffmanCodes(byte[] bits, byte[] values)
        {
            var map = new Dictionary<byte, (int, int)>();
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                for (int i = 0; i < bits[len - 1]; i++) map[values[k++]] = (code++, len);
                code <<= 1;
            }
            return map;
        }

        private sealed class BitWriter
        {
            private readonly List<byte> _out = new();
            private int _acc, _count;

            public void Write(int value, int length)
            {
                for (int i = length - 1; i >= 0; i--)
                {
                    _acc = (_acc << 1) | ((value >> i) & 1);
                    if (++_count == 8) Flush();
                }
            }

            private void Flush()
            {
                _out.Add((byte)_acc);
                if (_acc == 0xFF) _out.Add(0);   // byte stuffing
                _acc = 0;
                _count = 0;
            }

            public byte[] Finish()
            {
                while (_count != 0) Write(1, 1);   // pad with 1-bits
                return _out.ToArray();
            }
        }

        public static (byte[] Head, byte[] Tail) BuildJpeg(FileGenOptions o, DateTime now, (byte R, byte G, byte B) c)
        {
            double y = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            double cb = 128 - 0.168736 * c.R - 0.331264 * c.G + 0.5 * c.B;
            double cr = 128 + 0.5 * c.R - 0.418688 * c.G - 0.081312 * c.B;
            int[] dc = { (int)Math.Round(8 * (y - 128)), (int)Math.Round(8 * (cb - 128)), (int)Math.Round(8 * (cr - 128)) };

            var head = new MemoryStream();
            void Seg(byte marker, byte[] data)
            {
                head.WriteByte(0xFF);
                head.WriteByte(marker);
                head.WriteByte((byte)((data.Length + 2) >> 8));
                head.WriteByte((byte)((data.Length + 2) & 0xFF));
                head.Write(data);
            }
            head.Write(new byte[] { 0xFF, 0xD8 });                                                       // SOI
            Seg(0xE0, new byte[] { (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });   // APP0 JFIF
            if (o.InjectMetadata) Seg(0xFE, Encoding.UTF8.GetBytes(Truncate(MetadataLine(o, now), 60000)));       // COM

            // Padding comments go here (see WriteJpg) - "Head" ends right after the metadata.
            var tail = new MemoryStream();
            void TSeg(byte marker, byte[] data)
            {
                tail.WriteByte(0xFF);
                tail.WriteByte(marker);
                tail.WriteByte((byte)((data.Length + 2) >> 8));
                tail.WriteByte((byte)((data.Length + 2) & 0xFF));
                tail.Write(data);
            }
            // Quantisation tables of all 1s (the image is one flat colour - nothing to lose).
            var dqt = new byte[1 + 64 + 1 + 64];
            Array.Fill(dqt, (byte)1);
            dqt[0] = 0;
            dqt[65] = 1;
            TSeg(0xDB, dqt);
            TSeg(0xC0, new byte[]
            {
                8, ImageHeight >> 8, ImageHeight & 0xFF, ImageWidth >> 8, ImageWidth & 0xFF, 3,
                1, 0x22, 0,   2, 0x11, 1,   3, 0x11, 1,   // 4:2:0 - one 16×16 block of brightness per colour block
            });
            TSeg(0xC4, Dht(0x00, DcLumBits, DcValues));
            TSeg(0xC4, Dht(0x10, AcBits, AcValues));
            TSeg(0xC4, Dht(0x01, DcChromaBits, DcValues));
            TSeg(0xC4, Dht(0x11, AcBits, AcValues));
            TSeg(0xDA, new byte[] { 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 });

            var dcLum = HuffmanCodes(DcLumBits, DcValues);
            var dcChroma = HuffmanCodes(DcChromaBits, DcValues);
            var ac = HuffmanCodes(AcBits, AcValues);
            var bits = new BitWriter();
            int mcus = (ImageWidth / 16) * (ImageHeight / 16);
            var previous = new int[3];
            int[] blockComponents = { 0, 0, 0, 0, 1, 2 };   // per MCU: four Y blocks, then Cb, then Cr
            for (int m = 0; m < mcus; m++)
            {
                foreach (int comp in blockComponents)
                {
                    int diff = dc[comp] - previous[comp];
                    previous[comp] = dc[comp];
                    int magnitude = Math.Abs(diff);
                    int category = magnitude == 0 ? 0 : (int)Math.Floor(Math.Log2(magnitude)) + 1;
                    var (code, len) = (comp == 0 ? dcLum : dcChroma)[(byte)category];
                    bits.Write(code, len);
                    if (category > 0) bits.Write(diff < 0 ? diff + (1 << category) - 1 : diff, category);
                    var (eob, eobLen) = ac[0x00];
                    bits.Write(eob, eobLen);
                }
            }
            tail.Write(bits.Finish());
            tail.Write(new byte[] { 0xFF, 0xD9 });                                                       // EOI
            return (head.ToArray(), tail.ToArray());

            static byte[] Dht(byte classAndId, byte[] bitsTable, byte[] values) => new[] { classAndId }.Concat(bitsTable).Concat(values).ToArray();
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        private static void WriteJpg(Stream s, long target, FileGenOptions o, DateTime now, Action<long>? written, CancellationToken ct)
        {
            var (head, tail) = BuildJpeg(o, now, RandomColour());
            long done = 0;
            WriteBytes(s, head, ref done, written);

            long fill = target - head.Length - tail.Length;
            long trailing = 0;
            if (fill is > 0 and < 4)
            {
                trailing = fill;   // too small for a segment: a few bytes after EOI (readers stop at EOI)
                fill = 0;
            }
            const int maxSegment = 65535 + 2;   // FF FE + 2 length bytes + up to 65533 data bytes
            var data = new byte[65533];
            Array.Fill(data, (byte)'x');
            using (var w = new BufferedStream(s, ChunkSize))
            {
                while (fill >= 4)
                {
                    long seg = Math.Min(fill, maxSegment);
                    if (fill - seg is > 0 and < 4) seg -= 4;
                    int dataLen = (int)seg - 4;
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFE);
                    w.WriteByte((byte)((dataLen + 2) >> 8));
                    w.WriteByte((byte)((dataLen + 2) & 0xFF));
                    w.Write(data, 0, dataLen);
                    fill -= seg;
                    done += seg;
                    if ((done & 0x3FFFFF) < maxSegment)
                    {
                        ct.ThrowIfCancellationRequested();
                        written?.Invoke(done);
                    }
                }
                w.Write(tail);
                done += tail.Length;
                if (trailing > 0)
                {
                    w.Write(new byte[trailing]);
                    done += trailing;
                }
            }
            written?.Invoke(done);
        }

        // =================================================================
        //  MP4 - an empty but valid container: ftyp + free
        // =================================================================

        private static void WriteMp4(Stream s, long target, Action<long>? written, CancellationToken ct)
        {
            var ftyp = new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0,
                                    (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1' };
            long done = 0;
            WriteBytes(s, ftyp, ref done, written);
            long free = Math.Max(8, target - ftyp.Length);
            if (free > uint.MaxValue)
            {
                if (free < 16) free = 16;
                // 64-bit size: size field = 1, then the real size after the type.
                var head = new byte[16];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, 1);
                Encoding.ASCII.GetBytes("free").CopyTo(head, 4);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(8), free);
                WriteBytes(s, head, ref done, written);
                PadZeros(s, free - 16, ref done, written, ct);
            }
            else
            {
                var head = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, (uint)free);
                Encoding.ASCII.GetBytes("free").CopyTo(head, 4);
                WriteBytes(s, head, ref done, written);
                PadZeros(s, free - 8, ref done, written, ct);
            }
        }

        private static void PadZeros(Stream s, long count, ref long done, Action<long>? written, CancellationToken ct)
        {
            var zeros = new byte[(int)Math.Min(ChunkSize, Math.Max(1, count))];
            while (count > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(zeros.Length, count);
                s.Write(zeros, 0, n);
                count -= n;
                done += n;
                written?.Invoke(done);
            }
        }
    }
}
