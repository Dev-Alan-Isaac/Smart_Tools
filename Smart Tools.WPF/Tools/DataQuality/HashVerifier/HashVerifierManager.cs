using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Hash &amp; Checksum Verifier backend - pure logic (hashing, checksum-list parsing, comparing, file listing, settings).
    /// The run is in HashVerifierManager.Run.cs, exports in HashVerifierManager.Export.cs.
    /// Created on the UI thread; row updates are posted back to the captured SynchronizationContext.
    /// </summary>
    public sealed partial class HashVerifierManager
    {
        public static readonly HashAlgorithmKind[] AllAlgorithms =
        {
            HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256,
            HashAlgorithmKind.Sha384, HashAlgorithmKind.Sha512, HashAlgorithmKind.Crc32,
        };

        // =================================================================
        //  Algorithms
        // =================================================================

        public static string DisplayName(HashAlgorithmKind kind) => kind switch
        {
            HashAlgorithmKind.Md5 => "MD5",
            HashAlgorithmKind.Sha1 => "SHA-1",
            HashAlgorithmKind.Sha256 => "SHA-256",
            HashAlgorithmKind.Sha384 => "SHA-384",
            HashAlgorithmKind.Sha512 => "SHA-512",
            _ => "CRC32",
        };

        /// <summary>Length of the hash in hex characters.</summary>
        public static int HexLength(HashAlgorithmKind kind) => kind switch
        {
            HashAlgorithmKind.Md5 => 32,
            HashAlgorithmKind.Sha1 => 40,
            HashAlgorithmKind.Sha256 => 64,
            HashAlgorithmKind.Sha384 => 96,
            HashAlgorithmKind.Sha512 => 128,
            _ => 8,
        };

        /// <summary>Which algorithm a hash of this length belongs to (null if no supported algorithm has that length).</summary>
        public static HashAlgorithmKind? DetectByLength(string hex) => hex.Length switch
        {
            8 => HashAlgorithmKind.Crc32,
            32 => HashAlgorithmKind.Md5,
            40 => HashAlgorithmKind.Sha1,
            64 => HashAlgorithmKind.Sha256,
            96 => HashAlgorithmKind.Sha384,
            128 => HashAlgorithmKind.Sha512,
            _ => null,
        };

        /// <summary>The algorithm a checksum file's extension names (.sha256, .md5, .sfv ...), or null.</summary>
        public static HashAlgorithmKind? DetectByExtension(string? fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "md5" or "md5sum" => HashAlgorithmKind.Md5,
                "sha1" or "sha1sum" => HashAlgorithmKind.Sha1,
                "sha256" or "sha256sum" => HashAlgorithmKind.Sha256,
                "sha384" or "sha384sum" => HashAlgorithmKind.Sha384,
                "sha512" or "sha512sum" => HashAlgorithmKind.Sha512,
                "sfv" or "crc" or "crc32" => HashAlgorithmKind.Crc32,
                _ => null,
            };
        }

        private static HashAlgorithmKind? ParseAlgorithmName(string name) =>
            name.Replace("-", "").Replace("_", "").ToUpperInvariant() switch
            {
                "MD5" => HashAlgorithmKind.Md5,
                "SHA1" => HashAlgorithmKind.Sha1,
                "SHA256" => HashAlgorithmKind.Sha256,
                "SHA384" => HashAlgorithmKind.Sha384,
                "SHA512" => HashAlgorithmKind.Sha512,
                "CRC32" => HashAlgorithmKind.Crc32,
                _ => null,
            };

        // =================================================================
        //  Hash text
        // =================================================================

        public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        /// <summary>
        /// Cleans a pasted hash: drops spaces, colons, dashes and a leading 0x, lower-cases it.
        /// Returns "" if what is left is not hex.
        /// </summary>
        public static string NormalizeHash(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];

            // A hash copied from a tool often arrives grouped ("ab cd ef ..." or "ab:cd:ef:...").
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c is ' ' or ':' or '-' or '\t' or '\r' or '\n') continue;
                sb.Append(c);
            }
            string compact = sb.ToString();
            return IsHex(compact) ? compact.ToLowerInvariant() : "";
        }

        private static bool IsHex(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        public static bool HashesEqual(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // =================================================================
        //  Checksum lists (SHA256SUMS, file.sha256, .md5, .sfv, BSD style)
        // =================================================================

        private static readonly Regex BsdLine = new(
            @"^(?<algo>[A-Za-z0-9_-]+)\s*\((?<file>.+)\)\s*=\s*(?<hash>[0-9A-Fa-f]+)\s*$", RegexOptions.Compiled);

        private static readonly Regex GnuLine = new(
            @"^\\?(?<hash>[0-9A-Fa-f]+)[ \t]+[*]?(?<file>.+?)\s*$", RegexOptions.Compiled);

        private static readonly Regex SfvLine = new(
            @"^(?<file>.+?)[ \t]+(?<hash>[0-9A-Fa-f]{8})\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Reads a checksum list. <paramref name="listFileName"/> (e.g. "ubuntu.iso.sha256") gives the algorithm
        /// from its extension and, for a file holding only a hash, the name of the file it belongs to.
        /// Unreadable lines are skipped and counted in <paramref name="skippedLines"/>.
        /// </summary>
        public static List<HashChecksumEntry> ParseChecksumList(string text, string? listFileName, out int skippedLines)
        {
            var entries = new List<HashChecksumEntry>();
            skippedLines = 0;
            var fromExtension = DetectByExtension(listFileName);

            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line[0] is '#' or ';') continue;

                var bsd = BsdLine.Match(line);
                if (bsd.Success)
                {
                    string hash = bsd.Groups["hash"].Value.ToLowerInvariant();
                    var algo = ParseAlgorithmName(bsd.Groups["algo"].Value) ?? DetectByLength(hash);
                    if (algo is { } a && hash.Length == HexLength(a))
                        entries.Add(new HashChecksumEntry(hash, bsd.Groups["file"].Value, a));
                    else
                        skippedLines++;
                    continue;
                }

                if (IsHex(line))
                {
                    // A file holding only the hash: "<name>.sha256" is the checksum of "<name>".
                    var algo = DetectByLength(line);
                    string? target = listFileName != null && fromExtension != null
                        ? Path.GetFileNameWithoutExtension(listFileName)
                        : null;
                    if (algo is { }) entries.Add(new HashChecksumEntry(line.ToLowerInvariant(), target, algo));
                    else skippedLines++;
                    continue;
                }

                var gnu = GnuLine.Match(line);
                if (gnu.Success && DetectByLength(gnu.Groups["hash"].Value) is { } gnuAlgo)
                {
                    entries.Add(new HashChecksumEntry(gnu.Groups["hash"].Value.ToLowerInvariant(),
                        gnu.Groups["file"].Value, fromExtension is { } ext && HexLength(ext) == gnu.Groups["hash"].Length ? ext : gnuAlgo));
                    continue;
                }

                var sfv = SfvLine.Match(line);
                if (sfv.Success)
                {
                    entries.Add(new HashChecksumEntry(sfv.Groups["hash"].Value.ToLowerInvariant(), sfv.Groups["file"].Value, HashAlgorithmKind.Crc32));
                    continue;
                }

                skippedLines++;
            }
            return entries;
        }

        /// <summary>The algorithm to use for an entry (its own, or the one its hash length says).</summary>
        public static HashAlgorithmKind AlgorithmOf(HashChecksumEntry entry) =>
            entry.Algorithm ?? DetectByLength(entry.Hash) ?? HashAlgorithmKind.Sha256;

        /// <summary>Path in the form used to compare list entries with disk files: forward slashes, no "./" prefix.</summary>
        public static string NormalizeListPath(string path)
        {
            string p = path.Replace('\\', '/').Trim();
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
            return p.TrimStart('/');
        }

        // =================================================================
        //  Hashing
        // =================================================================

        /// <summary>Hashes a stream once for every requested algorithm. <paramref name="onBytes"/> gets the size of each chunk read.</summary>
        public static Dictionary<HashAlgorithmKind, string> ComputeStream(
            Stream stream, IReadOnlyCollection<HashAlgorithmKind> algorithms, Action<int>? onBytes, CancellationToken ct)
        {
            var hashers = new Dictionary<HashAlgorithmKind, IncrementalHash>();
            HashCrc32? crc = null;
            foreach (var kind in algorithms.Distinct())
            {
                if (kind == HashAlgorithmKind.Crc32) crc = new HashCrc32();
                else hashers[kind] = IncrementalHash.CreateHash(ToName(kind));
            }

            try
            {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = buffer.AsSpan(0, read);
                    foreach (var h in hashers.Values) h.AppendData(chunk);
                    crc?.Append(chunk);
                    onBytes?.Invoke(read);
                }

                var result = new Dictionary<HashAlgorithmKind, string>();
                foreach (var (kind, h) in hashers) result[kind] = ToHex(h.GetHashAndReset());
                if (crc != null) result[HashAlgorithmKind.Crc32] = crc.HashHex;
                return result;
            }
            finally
            {
                foreach (var h in hashers.Values) h.Dispose();
            }
        }

        /// <summary>Hashes a text (used by the tests and for pasted content).</summary>
        public static Dictionary<HashAlgorithmKind, string> ComputeBytes(byte[] data, IReadOnlyCollection<HashAlgorithmKind> algorithms)
        {
            using var ms = new MemoryStream(data);
            return ComputeStream(ms, algorithms, null, CancellationToken.None);
        }

        private static HashAlgorithmName ToName(HashAlgorithmKind kind) => kind switch
        {
            HashAlgorithmKind.Md5 => HashAlgorithmName.MD5,
            HashAlgorithmKind.Sha1 => HashAlgorithmName.SHA1,
            HashAlgorithmKind.Sha256 => HashAlgorithmName.SHA256,
            HashAlgorithmKind.Sha384 => HashAlgorithmName.SHA384,
            _ => HashAlgorithmName.SHA512,
        };

        // =================================================================
        //  File listing (the Files card)
        // =================================================================

        public sealed record ListResult(List<string> Files, int Unreadable);

        /// <summary>Turns the added files/folders into a flat de-duplicated file list. Runs off the UI thread.</summary>
        public static Task<ListResult> ExpandAsync(IReadOnlyList<HashSource> sources, bool includeSubfolders, CancellationToken ct) =>
            Task.Run(() =>
            {
                var files = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int unreadable = 0;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = includeSubfolders,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                };

                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!source.IsFolder)
                    {
                        if (File.Exists(source.FullPath) && seen.Add(source.FullPath)) files.Add(source.FullPath);
                        continue;
                    }
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(source.FullPath, "*", options))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (seen.Add(f)) files.Add(f);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        unreadable++;
                    }
                }
                return new ListResult(files, unreadable);
            }, ct);

        /// <summary>The Files card rows: one per added file group and one per folder.</summary>
        public static List<HashQueueGroup> BuildQueueGroups(IReadOnlyList<HashSource> sources)
        {
            var groups = new List<HashQueueGroup>();
            foreach (var s in sources.Where(s => s.IsFolder))
            {
                groups.Add(new HashQueueGroup
                {
                    Name = Path.GetFileName(s.FullPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : s.FullPath,
                    Subtitle = "Folder",
                    ToolTip = s.FullPath,
                    IsFolder = true,
                    FolderPath = s.FullPath,
                });
            }

            var loose = sources.Where(s => !s.IsFolder).Select(s => s.FullPath).ToList();
            foreach (var path in loose)
            {
                groups.Add(new HashQueueGroup
                {
                    Name = Path.GetFileName(path),
                    Subtitle = Path.GetDirectoryName(path) ?? "",
                    ToolTip = path,
                    FilePaths = new List<string> { path },
                });
            }
            return groups;
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{bytes:N0} B" : $"{v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[i]}";
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "hash_verifier.json");

        public static HashVerifierSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new HashVerifierSettings();
                var s = JsonSerializer.Deserialize<HashVerifierSettings>(File.ReadAllText(file), JsonOptions) ?? new HashVerifierSettings();
                s.Algorithms = (s.Algorithms ?? new()).Where(a => Enum.IsDefined(a)).Distinct().ToList();
                if (s.Algorithms.Count == 0) s.Algorithms.Add(HashAlgorithmKind.Sha256);
                if (!Enum.IsDefined(s.Mode)) s.Mode = HashVerifyMode.Compute;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new HashVerifierSettings();
            }
        }

        public static void SaveSettings(HashVerifierSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);
    }

    /// <summary>Table-driven CRC-32 (IEEE 802.3, the one ZIP/PNG/SFV use).</summary>
    internal sealed class HashCrc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _crc = 0xFFFFFFFF;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            uint c = _crc;
            foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            _crc = c;
        }

        public string HashHex => (~_crc).ToString("x8", CultureInfo.InvariantCulture);
    }
}
