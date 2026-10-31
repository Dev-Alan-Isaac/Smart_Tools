using Smart_Tools.WPF.Models;
using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Validate Files - the checks for one file. Each check adds to a
    /// <see cref="Findings"/>: problems make a file Corrupted, warnings a
    /// Warning, and "couldn't check" an Unknown - which is not the same as
    /// broken (the old version called locked files and codec gaps corrupted).
    /// </summary>
    public sealed partial class ValidateManager
    {
        private const int HeaderSize = 512;

        /// <summary>Collects what the checks found and turns it into a status and one readable line.</summary>
        internal sealed class Findings
        {
            public List<string> Problems { get; } = new();
            public List<string> Warnings { get; } = new();
            public List<string> CantCheck { get; } = new();
            public List<string> Notes { get; } = new();

            public void Apply(FileValidationResult row, string okText)
            {
                var (status, lead) =
                    Problems.Count > 0 ? (ValidationStatus.Corrupted, Problems) :
                    Warnings.Count > 0 ? (ValidationStatus.Warning, Warnings) :
                    CantCheck.Count > 0 ? (ValidationStatus.Unknown, CantCheck) :
                    (ValidationStatus.Valid, new List<string> { okText });

                var parts = lead.Concat(status == ValidationStatus.Corrupted ? Warnings : Enumerable.Empty<string>()).Concat(Notes).Select(Sentence);
                row.Details = string.Join(" ", parts);
                row.Status = status;
            }

            private static string Sentence(string s)
            {
                s = s.Trim();
                if (s.Length == 0) return s;
                s = char.ToUpperInvariant(s[0]) + s[1..];
                return s.EndsWith('.') || s.EndsWith(')') ? s : s + ".";
            }
        }

        /// <summary>Checks one file and sets its Status and Details. Never throws (except on cancel).</summary>
        public static void CheckFile(FileValidationResult row, ValidationOptions o, CancellationToken token)
        {
            var f = new Findings();
            try
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(row.FullPath);
                if (!info.Exists)
                {
                    f.CantCheck.Add("the file no longer exists (moved or deleted since it was listed)");
                    f.Apply(row, "");
                    return;
                }
                if (info.Length == 0)
                {
                    f.Problems.Add("the file is empty (0 bytes)");
                    f.Apply(row, "");
                    return;
                }

                byte[] header;
                try
                {
                    header = ReadHead(row.FullPath, HeaderSize);
                }
                catch (UnauthorizedAccessException)
                {
                    f.CantCheck.Add("access denied - Windows won't let this app read it");
                    f.Apply(row, "");
                    return;
                }
                catch (IOException ex) when (IsInUse(ex))
                {
                    f.CantCheck.Add("it's open in another program - close it and check again");
                    f.Apply(row, "");
                    return;
                }
                catch (IOException ex)
                {
                    f.Problems.Add("it can't be read from the disk (" + ex.Message.TrimEnd('.') + ")");
                    f.Apply(row, "");
                    return;
                }

                if (!WantsFormatChecks(row.Category, o))
                {
                    f.Apply(row, row.Category == ValidationKind.Other
                        ? "Readable. There are no format checks for this kind of file"
                        : $"Readable. {row.Category} checks are turned off");
                    return;
                }

                bool checkHeader = row.Category switch
                {
                    ValidationKind.Image => o.ImageCheckHeader,
                    ValidationKind.Video => o.VideoCheckContainer,
                    ValidationKind.Audio => o.AudioCheckHeader,
                    ValidationKind.Document => o.DocCheckStructure,
                    _ => true,
                };
                if (checkHeader && !HeaderMatches(row.FullPath, header))
                {
                    if (WrongExtensionNote(row.FullPath, header) is string note)
                        f.Warnings.Add(note + " - rename it so programs open it correctly");
                    else
                        f.Problems.Add($"the start of the file isn't a valid {ExtLabel(row.FullPath)} header - it's damaged or not really a {ExtLabel(row.FullPath)} file");
                    f.Apply(row, "");
                    return;
                }

                string ok = row.Category switch
                {
                    ValidationKind.Image => CheckImage(row.FullPath, header, info.Length, o, f, token),
                    ValidationKind.Video => CheckVideo(row.FullPath, o, f, token),
                    ValidationKind.Audio => CheckAudio(row.FullPath, header, info.Length, o, f, token),
                    ValidationKind.Document => CheckDocument(row.FullPath, header, info.Length, o, f, token),
                    ValidationKind.Archive => CheckArchive(row.FullPath, info.Length, o, f, token),
                    _ => "Readable",
                };
                f.Apply(row, ok);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                f.CantCheck.Add("access denied part-way through");
                f.Apply(row, "");
            }
            catch (IOException ex) when (IsInUse(ex))
            {
                f.CantCheck.Add("it was opened by another program during the check");
                f.Apply(row, "");
            }
            catch (Exception ex)
            {
                f.CantCheck.Add("the check itself failed: " + ex.Message.TrimEnd('.'));
                f.Apply(row, "");
            }
        }

        private static string ExtLabel(string path) =>
            Path.GetExtension(path) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() : "file";

        internal static bool IsInUse(IOException ex) => (ex.HResult & 0xFFFF) is 0x20 or 0x21;   // sharing / lock violation

        /// <summary>Opened with full sharing: checking must never block the user's other programs.</summary>
        internal static FileStream OpenShared(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

        internal static byte[] ReadHead(string path, int count)
        {
            using var fs = OpenShared(path);
            var buffer = new byte[(int)Math.Min(count, fs.Length)];
            int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read == buffer.Length ? buffer : buffer[..read];
        }

        internal static byte[] ReadTail(string path, int count)
        {
            using var fs = OpenShared(path);
            int n = (int)Math.Min(count, fs.Length);
            fs.Position = fs.Length - n;
            var buffer = new byte[n];
            fs.ReadExactly(buffer);
            return buffer;
        }

        /// <summary>Tail with trailing zero/space padding removed (some tools pad files).</summary>
        private static ReadOnlySpan<byte> TrimPadding(byte[] tail)
        {
            int end = tail.Length;
            while (end > 0 && tail[end - 1] is 0 or 0x20 or 0x0A or 0x0D or 0x09) end--;
            return tail.AsSpan(0, end);
        }

        // =================================================================
        //  Pictures
        // =================================================================

        /// <summary>What decoding a picture found (from Windows' codecs, or a test stub).</summary>
        public readonly record struct ImageProbe(int Width, int Height, string? Error = null, bool Unsupported = false, string? ExifError = null);

        /// <summary>Tests replace the decoder (WIC needs Windows).</summary>
        public static Func<string, bool, bool, ImageProbe>? ImageDecoderOverride { get; set; }

        private static string CheckImage(string path, byte[] header, long length, ValidationOptions o, Findings f, CancellationToken token)
        {
            string kind = Sniff(header) ?? "";

            // A cut-off JPEG still "decodes" (the missing part turns grey), so the end marker is checked separately.
            if (o.ImageCheckTruncated)
            {
                var tail = TrimPadding(ReadTail(path, 64 * 1024));
                bool ended = kind switch
                {
                    "JPEG" => tail.IndexOf(new byte[] { 0xFF, 0xD9 }) >= 0,
                    "PNG" => tail.Length >= 8 && tail[Math.Max(0, tail.Length - 64)..].IndexOf("IEND"u8) >= 0,
                    "GIF" => tail.Length > 0 && tail[^1] == 0x3B,
                    _ => true,
                };
                if (!ended) f.Problems.Add("the file is cut short (the end-of-image marker is missing), so part of the picture is lost");
            }

            token.ThrowIfCancellationRequested();
            if (!(o.ImageFullDecode || o.ImageFlagSmall || o.ImageCheckExif)) return "Header OK";

            bool exif = o.ImageCheckExif && kind is "JPEG" or "TIFF";
            var probe = ImageDecoderOverride != null ? ImageDecoderOverride(path, o.ImageFullDecode, exif) : DecodeImageCore(path, o.ImageFullDecode, exif);

            if (probe.Unsupported)
            {
                f.CantCheck.Add($"Windows has no decoder for this {ExtLabel(path)} picture, so the pixels couldn't be checked (the header is fine)");
                return "";
            }
            if (probe.Error != null)
            {
                f.Problems.Add("the picture data is damaged: " + probe.Error.TrimEnd('.'));
                return "";
            }
            if (o.ImageFlagSmall && probe.Width > 0 && (probe.Width < 50 || probe.Height < 50))
                f.Warnings.Add($"it's unusually small ({probe.Width} x {probe.Height}) - maybe a thumbnail or placeholder");
            if (probe.ExifError != null)
                f.Warnings.Add("the photo information (EXIF) is damaged: " + probe.ExifError.TrimEnd('.'));

            return probe.Width > 0
                ? $"{probe.Width} x {probe.Height}, {(o.ImageFullDecode ? "every pixel decoded" : "header read")}"
                : "Header OK";
        }

        private static partial ImageProbe DecodeImageCore(string path, bool fullDecode, bool readExif);

        // =================================================================
        //  Audio (header part - decoding is in .Media.cs)
        // =================================================================

        private static string CheckAudio(string path, byte[] header, long length, ValidationOptions o, Findings f, CancellationToken token)
        {
            if (o.AudioCheckTruncated && Sniff(header) == "WAV" && WavExpectedLength(path) is long expected && expected > length)
                f.Problems.Add($"the file is cut short: it should be {ValidateText.FormatBytes(expected)} but is {ValidateText.FormatBytes(length)}");

            return CheckAudioStreams(path, o, f, token);
        }

        /// <summary>
        /// Where the WAV's data chunk says the file ends, or null when unknown.
        /// Streaming placeholders (0 / 0xFFFFFFFF) are ignored - the old
        /// uint sum overflowed on them and then passed anything.
        /// </summary>
        public static long? WavExpectedLength(string path)
        {
            byte[] head = ReadHead(path, 64 * 1024);
            if (head.Length < 12) return null;
            long pos = 12;
            while (pos + 8 <= head.Length)
            {
                string id = Encoding.ASCII.GetString(head, (int)pos, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan((int)pos + 4));
                if (id == "data")
                    return size is 0 or 0xFFFFFFFF ? null : pos + 8 + size;
                if (size == 0xFFFFFFFF) return null;
                pos += 8 + size + (size & 1);
            }
            return null;
        }

        // =================================================================
        //  Documents
        // =================================================================

        private static readonly string[] OoxmlMainParts = { "word/document.xml", "xl/workbook.xml", "ppt/presentation.xml" };

        private static string CheckDocument(string path, byte[] header, long length, ValidationOptions o, Findings f, CancellationToken token)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string? kind = Sniff(header);
            switch (ext)
            {
                case ".pdf":
                    return CheckPdf(path, header, length, o, f);
                case ".txt":
                    return "Readable text";
                case ".rtf":
                    if (o.DocCheckStructure)
                    {
                        var end = TrimPadding(ReadTail(path, 256));
                        if (end.Length == 0 || end[^1] != (byte)'}') f.Problems.Add("the file is cut short (the closing } is missing)");
                    }
                    return "RTF structure OK";
                case ".doc" or ".xls" or ".ppt":
                    return "Header OK. Older Office files can only be checked this far";
            }

            // Zip-based: Office Open XML and OpenDocument.
            if (kind == "OLE")
            {
                // An encrypted .docx is an OLE file, not a zip - the old version said "package is corrupt".
                if (o.DocFlagPassword) f.Warnings.Add("it's password-protected, so its contents can't be checked");
                else f.CantCheck.Add("it's password-protected, so its contents can't be checked");
                return "";
            }

            ZipArchive zip;
            try
            {
                zip = new ZipArchive(OpenShared(path), ZipArchiveMode.Read, leaveOpen: false);
            }
            catch (InvalidDataException)
            {
                f.Problems.Add("the document package is damaged (its file list can't be read) - usually the file was cut short");
                return "";
            }

            using (zip)
            {
                bool odf = ext is ".odt" or ".ods" or ".odp";
                var names = new HashSet<string>(zip.Entries.Select(e => e.FullName), StringComparer.OrdinalIgnoreCase);
                if (o.DocCheckStructure)
                {
                    if (odf ? !names.Contains("mimetype") : !names.Contains("[Content_Types].xml"))
                        f.Problems.Add(odf ? "the OpenDocument 'mimetype' part is missing" : "the [Content_Types].xml part is missing - it isn't a valid Office file");
                    else if (odf ? !names.Contains("content.xml") : !OoxmlMainParts.Any(names.Contains))
                        f.Problems.Add("the main document part is missing");
                }

                var damaged = new List<string>();
                var badPictures = new List<string>();
                var badFonts = new List<string>();
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    bool picture = (entry.FullName.Contains("/media/", StringComparison.OrdinalIgnoreCase) || entry.FullName.StartsWith("Pictures/", StringComparison.OrdinalIgnoreCase))
                                   && ImageExts.Contains(Path.GetExtension(entry.Name));
                    bool font = entry.FullName.Contains("/fonts/", StringComparison.OrdinalIgnoreCase)
                                && Path.GetExtension(entry.Name).ToLowerInvariant() is ".odttf" or ".ttf" or ".otf" or ".fntdata";

                    bool read = o.DocVerifyOpens || (picture && o.DocCheckEmbeddedImages) || (font && o.DocCheckEmbeddedFonts);
                    if (!read) continue;
                    try
                    {
                        using var s = entry.Open();
                        byte[] head = new byte[HeaderSize];
                        int n = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                        DrainStream(s, token);   // reading to the end checks the part's CRC
                        if (picture && o.DocCheckEmbeddedImages && !HeaderMatches(entry.Name, head[..n])) badPictures.Add(entry.Name);
                        if (font && o.DocCheckEmbeddedFonts && entry.Length == 0) badFonts.Add(entry.Name);
                    }
                    catch (InvalidDataException)
                    {
                        damaged.Add(entry.FullName);
                    }
                }

                if (damaged.Count > 0) f.Problems.Add($"{damaged.Count} part{(damaged.Count == 1 ? " is" : "s are")} damaged ({Join(damaged)})");
                if (badPictures.Count > 0) f.Warnings.Add($"{badPictures.Count} embedded picture{(badPictures.Count == 1 ? " is" : "s are")} damaged ({Join(badPictures)})");
                if (badFonts.Count > 0) f.Warnings.Add($"{badFonts.Count} embedded font{(badFonts.Count == 1 ? " is" : "s are")} empty ({Join(badFonts)})");
                return $"Structure OK{(o.DocVerifyOpens ? ", all " + names.Count + " parts read" : "")}";
            }
        }

        private static string Join(List<string> items) => string.Join(", ", items.Take(3)) + (items.Count > 3 ? $" +{items.Count - 3} more" : "");

        /// <summary>Reads only the start and end - the old version loaded whole (possibly GB) PDFs into memory.</summary>
        private static string CheckPdf(string path, byte[] header, long length, ValidationOptions o, Findings f)
        {
            string tail = Encoding.Latin1.GetString(ReadTail(path, 8192));
            if (o.DocCheckStructure)
            {
                if (!tail.Contains("%%EOF", StringComparison.Ordinal))
                {
                    f.Problems.Add("the file is cut short (the %%EOF end marker is missing)");
                    return "";
                }
            }

            if (o.DocVerifyOpens)
            {
                long? xref = PdfStartXref(tail);
                bool ok = false;
                if (xref is long at && at >= 0 && at < length)
                {
                    using var fs = OpenShared(path);
                    fs.Position = at;
                    var buf = new byte[32];
                    int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                    string s = Encoding.Latin1.GetString(buf, 0, n).TrimStart();
                    ok = s.StartsWith("xref", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+\s+\d+\s+obj");
                }
                if (!ok) f.Warnings.Add("the cross-reference table is damaged - most viewers can repair it, but re-saving the PDF is a good idea");
            }

            if (o.DocFlagPassword && PdfTrailerDeclaresEncrypt(tail))
                f.Warnings.Add("it's encrypted (it may need a password to open)");   // the old version called this "corrupted"

            return "PDF structure OK";
        }

        // =================================================================
        //  Archives
        // =================================================================

        /// <summary>Flag an archive as a possible bomb when it expands 100x AND to more than 500 MB.</summary>
        public static bool LooksLikeBomb(long compressed, long uncompressed) =>
            compressed > 0 && uncompressed >= 500L * 1024 * 1024 && (double)uncompressed / compressed >= 100;

        private static string CheckArchive(string path, long length, ValidationOptions o, Findings f, CancellationToken token)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            string ext = Path.GetExtension(name);
            bool test = o.ArchiveVerifyCrc || o.ArchiveTestExtract;

            if (ext == ".zip") return CheckZip(path, o, test, f, token);
            if (ext is ".gz" or ".tgz") return CheckGzip(path, length, isTar: ext == ".tgz" || name.EndsWith(".tar.gz", StringComparison.Ordinal), o, test, f, token);
            if (ext == ".tar")
            {
                if (!test) return "Header OK";
                using var fs = OpenShared(path);
                int entries = ReadTar(fs, f, token);
                return $"{entries:N0} entr{(entries == 1 ? "y" : "ies")} read";
            }
            return "Header OK. The contents of this kind of archive need 7-Zip to test";
        }

        private static string CheckZip(string path, ValidationOptions o, bool test, Findings f, CancellationToken token)
        {
            ZipArchive zip;
            try
            {
                zip = new ZipArchive(OpenShared(path), ZipArchiveMode.Read, leaveOpen: false);
            }
            catch (InvalidDataException)
            {
                f.Problems.Add("the archive's file list can't be read - usually the file was cut short");
                return "";
            }

            using (zip)
            {
                var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                long compressed = files.Sum(e => e.CompressedLength);
                long uncompressed = files.Sum(e => e.Length);
                int encrypted = files.Count(e => e.IsEncrypted);

                // Checked BEFORE extracting - the old version decompressed a bomb fully first.
                if (o.ArchiveCheckBomb && LooksLikeBomb(compressed, uncompressed))
                {
                    f.Warnings.Add($"possible zip bomb: {ValidateText.FormatBytes(compressed)} expands to {ValidateText.FormatBytes(uncompressed)} ({uncompressed / Math.Max(1, compressed):N0}x) - not extracted");
                    return "";
                }

                var damaged = new List<string>();
                if (test)
                {
                    foreach (var entry in files.Where(e => !e.IsEncrypted))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            using var s = entry.Open();
                            DrainStream(s, token);   // reading to the end checks the CRC
                        }
                        catch (InvalidDataException)
                        {
                            damaged.Add(entry.FullName);
                        }
                    }
                }

                if (damaged.Count > 0) f.Problems.Add($"{damaged.Count} of {files.Count} entries are damaged ({Join(damaged)})");
                if (encrypted > 0)
                {
                    string text = $"{encrypted} entr{(encrypted == 1 ? "y is" : "ies are")} password-protected and couldn't be tested";
                    if (o.ArchiveFlagPassword) f.Warnings.Add(text);   // the old version called these "corrupted"
                    else f.Notes.Add(text);
                }

                int nested = files.Count(e => ArchiveExts.Contains(Path.GetExtension(e.Name)));
                if (o.ArchiveCheckBomb && nested > 0) f.Notes.Add($"contains {nested} archive{(nested == 1 ? "" : "s")} (not opened)");
                return test ? $"{files.Count - encrypted:N0} entr{(files.Count - encrypted == 1 ? "y" : "ies")} extracted and CRC-checked" : $"{files.Count:N0} entries listed";
            }
        }

        private static string CheckGzip(string path, long length, bool isTar, ValidationOptions o, bool test, Findings f, CancellationToken token)
        {
            // The last 4 bytes hold the original size (mod 4 GB).
            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(ReadTail(path, 4));
            if (o.ArchiveCheckBomb && LooksLikeBomb(length, declared))
            {
                f.Warnings.Add($"possible decompression bomb: expands to {ValidateText.FormatBytes(declared)} - not extracted");
                return "";
            }
            if (!test) return "Header OK";

            using var fs = OpenShared(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var counter = new CountingStream(gz);
            int entries = 0;
            try
            {
                if (isTar) entries = ReadTar(counter, f, token);
                DrainStream(counter, token);
            }
            catch (InvalidDataException ex)
            {
                f.Problems.Add("the compressed data is damaged (" + ex.Message.TrimEnd('.') + ")");
                return "";
            }

            // .NET doesn't report a gzip that simply stops early; the stored size does.
            if (counter.Total < uint.MaxValue && counter.Total < declared)
                f.Problems.Add($"the file is cut short: it should expand to {ValidateText.FormatBytes(declared)} but only {ValidateText.FormatBytes(counter.Total)} is there");

            return isTar ? $"{entries:N0} entries decompressed and read" : $"Decompressed OK ({ValidateText.FormatBytes(counter.Total)})";
        }

        private static int ReadTar(Stream stream, Findings f, CancellationToken token)
        {
            int count = 0;
            try
            {
                using var reader = new TarReader(stream, leaveOpen: true);
                while (reader.GetNextEntry(copyData: false) is TarEntry entry)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.DataStream != null) DrainStream(entry.DataStream, token);
                    count++;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
            {
                f.Problems.Add($"the archive is damaged after {count} entr{(count == 1 ? "y" : "ies")} ({ex.Message.TrimEnd('.')})");
            }
            return count;
        }

        private static void DrainStream(Stream s, CancellationToken token)
        {
            byte[] buffer = new byte[81920];
            while (s.Read(buffer, 0, buffer.Length) > 0) token.ThrowIfCancellationRequested();
        }

        private sealed class CountingStream(Stream inner) : Stream
        {
            public long Total { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = inner.Read(buffer, offset, count);
                Total += n;
                return n;
            }

            public override int Read(Span<byte> buffer)
            {
                int n = inner.Read(buffer);
                Total += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => Total; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
