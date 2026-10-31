using System.IO;
using System.IO.Compression;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// A PNG writer small enough to test without any imaging library: 8-bit RGBA, no filtering, one IDAT chunk.
    /// Used where a tool builds its own pixels (Pixel Art Editor, Sprite Sheet Packer) and needs a lossless file.
    /// No WPF types.
    /// </summary>
    public static class PngWriter
    {
        /// <summary>Straight-alpha RGBA bytes, row by row, to a PNG file's bytes.</summary>
        public static byte[] Encode(int width, int height, byte[] rgba)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;      // bit depth
            header[9] = 6;      // colour type: RGBA
            WriteChunk(ms, "IHDR", header);

            using var raw = new MemoryStream();
            var row = new byte[width * 4 + 1];                    // first byte is the filter type, 0 = none
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, y * width * 4, row, 1, width * 4);
                raw.Write(row, 0, row.Length);
            }
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw.GetBuffer(), 0, (int)raw.Length);
            WriteChunk(ms, "IDAT", packed.ToArray());
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            stream.Write(length);
            var body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            stream.Write(body);
            var crc = new byte[4];
            WriteBigEndian(crc, 0, Crc32(body));
            stream.Write(crc);
        }

        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }

    /// <summary>Writes a file so a half-written one never looks like a result.</summary>
    public static class SafeFile
    {
        /// <summary>
        /// Writes through a hidden temporary name in the same folder and moves it into place. The path comes from a
        /// Save dialog (which already asked about replacing) or from a unique-name check. Returns null on success, otherwise the reason.
        /// </summary>
        public static string? WriteAllBytes(string path, byte[] data)
        {
            string? folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return "No folder";
            string temp = Path.Combine(folder, "." + Path.GetFileName(path) + ".creating-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return ex.Message;
            }
        }
    }
}
