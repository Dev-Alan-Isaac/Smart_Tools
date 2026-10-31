using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class HashVerifierManager
    {
        // Exports never overwrite: a name clash gets a timestamp, written via a temp file moved with overwrite:false.

        public static string DefaultChecksumFileName(HashAlgorithmKind kind) => kind switch
        {
            HashAlgorithmKind.Md5 => "checksums.md5",
            HashAlgorithmKind.Sha1 => "checksums.sha1",
            HashAlgorithmKind.Sha256 => "checksums.sha256",
            HashAlgorithmKind.Sha384 => "checksums.sha384",
            HashAlgorithmKind.Sha512 => "checksums.sha512",
            _ => "checksums.sfv",
        };

        /// <summary>
        /// The text of a checksum file for the rows that have <paramref name="kind"/> computed: GNU format
        /// ("hash *relative/path", readable by sha256sum -c), or SFV for CRC32. Paths are relative to
        /// <paramref name="baseFolder"/> (the rows' common folder when empty), with forward slashes.
        /// </summary>
        public static string BuildChecksumFile(IEnumerable<HashFileResult> rows, HashAlgorithmKind kind, string? baseFolder = null)
        {
            var list = rows.Where(r => r.TryGetHash(kind, out _)).ToList();
            string root = string.IsNullOrWhiteSpace(baseFolder) ? CommonFolder(list.Select(r => r.FullPath)) : baseFolder!;

            var sb = new StringBuilder();
            foreach (var row in list.OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                row.TryGetHash(kind, out string hash);
                string path = RelativeTo(root, row.FullPath).Replace('\\', '/');
                if (kind == HashAlgorithmKind.Crc32) sb.Append(path).Append(' ').Append(hash.ToUpperInvariant()).Append('\n');
                else sb.Append(hash).Append(" *").Append(path).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Saves a checksum file; returns the path actually written.</summary>
        public static string SaveChecksumFile(IEnumerable<HashFileResult> rows, HashAlgorithmKind kind, string path, string? baseFolder = null)
        {
            string text = BuildChecksumFile(rows, kind, baseFolder);
            if (text.Length == 0) throw new InvalidOperationException($"No file has a {DisplayName(kind)} hash yet.");
            return WriteWithoutOverwriting(path, text);
        }

        public static string BuildCsv(IEnumerable<HashFileResult> rows)
        {
            var kinds = AllAlgorithms;
            var sb = new StringBuilder();
            sb.Append("File,Folder,Size (bytes),Result,Details,Expected");
            foreach (var k in kinds) sb.Append(',').Append(DisplayName(k));
            sb.Append("\r\n");

            foreach (var r in rows)
            {
                sb.Append(CsvCell(r.FileName)).Append(',').Append(CsvCell(r.Folder)).Append(',')
                  .Append(r.SizeBytes).Append(',').Append(CsvCell(r.Verdict.ToString())).Append(',')
                  .Append(CsvCell(r.Details)).Append(',').Append(CsvCell(r.Expected));
                foreach (var k in kinds) sb.Append(',').Append(CsvCell(r.GetHash(k)));
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        public static string SaveCsv(IEnumerable<HashFileResult> rows, string path) =>
            WriteWithoutOverwriting(path, BuildCsv(rows), withBom: true);

        /// <summary>CSV cell: quoted when needed; a leading = + - @ is defused so a spreadsheet won't run it as a formula.</summary>
        public static string CsvCell(string? value)
        {
            string v = value ?? "";
            if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
            return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        private static string WriteWithoutOverwriting(string path, string content, bool withBom = false)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            Directory.CreateDirectory(dir);

            string temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, content, new UTF8Encoding(withBom));
            try
            {
                string target = path;
                if (File.Exists(target))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    string ext = Path.GetExtension(path);
                    target = Path.Combine(dir, $"{name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{ext}");
                    for (int n = 2; File.Exists(target); n++)
                        target = Path.Combine(dir, $"{name} {DateTime.Now:yyyy-MM-dd HH-mm-ss} ({n}){ext}");
                }
                File.Move(temp, target, overwrite: false);
                return target;
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { /* leave it */ }
                throw;
            }
        }

        private static string CommonFolder(IEnumerable<string> paths)
        {
            string[]? common = null;
            foreach (string p in paths)
            {
                string[] parts = (Path.GetDirectoryName(p) ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.None);
                if (common == null) { common = parts; continue; }
                int n = 0;
                while (n < common.Length && n < parts.Length && string.Equals(common[n], parts[n], StringComparison.OrdinalIgnoreCase)) n++;
                common = common[..n];
            }
            return common == null ? "" : string.Join(Path.DirectorySeparatorChar, common);
        }

        private static string RelativeTo(string root, string path)
        {
            if (root.Length == 0) return path;
            try
            {
                string rel = Path.GetRelativePath(root, path);
                return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel;
            }
            catch (ArgumentException)
            {
                return path;
            }
        }
    }
}
