using Smart_Tools.WPF.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class FileDifferencesManager
    {
        /// <summary>Bigger files are compared byte for byte only (no line list).</summary>
        public const long MaxTextBytes = 64L * 1024 * 1024;

        private const int SniffBytes = 8192;
        private const int StreamBuffer = 1 << 20;

        // =================================================================
        //  Reading files
        // =================================================================

        /// <summary>Reads a file off the UI thread. Throws IOException / UnauthorizedAccessException with the reason.</summary>
        public static Task<FileDiffDocument> LoadAsync(string path, CancellationToken cancellationToken) =>
            Task.Run(() => Load(path, cancellationToken), cancellationToken);

        /// <summary>
        /// A BOM decides the encoding first (UTF-8/16/32); only then is a NUL
        /// byte taken as "binary" (the other order treats every UTF-16 file as
        /// binary). Without a BOM: UTF-8, or Latin-1 when that turns into
        /// lots of replacement characters (old Windows-1252 text).
        /// </summary>
        public static FileDiffDocument Load(string path, CancellationToken cancellationToken = default)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException("The file isn't there any more.", path);

            if (info.Length > MaxTextBytes)
            {
                return new FileDiffDocument
                {
                    Path = info.FullName,
                    Size = info.Length,
                    LastWriteTime = info.LastWriteTime,
                    Hash = HashFile(info.FullName, cancellationToken),
                    IsTooLarge = true,
                };
            }

            byte[] bytes;
            using (var stream = OpenShared(info.FullName))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            cancellationToken.ThrowIfCancellationRequested();

            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            var (encoding, bomLength, encodingName) = DetectBom(bytes);

            string text;
            if (encoding == null)
            {
                if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, SniffBytes)) >= 0)
                {
                    return new FileDiffDocument
                    {
                        Path = info.FullName,
                        Size = bytes.Length,
                        LastWriteTime = info.LastWriteTime,
                        Hash = hash,
                        IsBinary = true,
                    };
                }

                text = new UTF8Encoding(false, false).GetString(bytes);
                encodingName = "UTF-8";
                if (CountReplacements(text) > Math.Max(8, text.Length / 200))
                {
                    text = Encoding.Latin1.GetString(bytes);
                    encodingName = "ANSI (Latin-1)";
                }
            }
            else
            {
                text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
            }

            var (lines, lineEnding, endsWithNewline) = SplitLines(text);

            return new FileDiffDocument
            {
                Path = info.FullName,
                Size = bytes.Length,
                LastWriteTime = info.LastWriteTime,
                Hash = hash,
                Lines = lines,
                EncodingName = encodingName,
                LineEnding = lineEnding,
                EndsWithNewline = endsWithNewline,
            };
        }

        private static FileStream OpenShared(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamBuffer, FileOptions.SequentialScan);

        private static (Encoding? Encoding, int BomLength, string Name) DetectBom(byte[] b)
        {
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) return (new UTF32Encoding(false, true), 4, "UTF-32 LE");
            if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) return (new UTF32Encoding(true, true), 4, "UTF-32 BE");
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false), 3, "UTF-8 BOM");
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, true), 2, "UTF-16 LE");
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, true), 2, "UTF-16 BE");
            return (null, 0, "");
        }

        private static int CountReplacements(string text)
        {
            int count = 0;
            foreach (char c in text) if (c == '�') count++;
            return count;
        }

        /// <summary>
        /// Lines without their line breaks, which kind of break the file uses,
        /// and whether it ends with one. A single final break doesn't make an
        /// extra empty line, so "a\n" and "a" have the same lines (the
        /// difference is reported as a note instead).
        /// </summary>
        internal static (string[] Lines, string LineEnding, bool EndsWithNewline) SplitLines(string text)
        {
            if (text.Length == 0) return (Array.Empty<string>(), "", false);

            var lines = new List<string>();
            int crlf = 0, lf = 0, cr = 0;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\r' && c != '\n') continue;

                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else if (c == '\r') cr++;
                else lf++;
                start = i + 1;
            }

            bool endsWithNewline = start == text.Length;
            if (!endsWithNewline) lines.Add(text[start..]);

            int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
            string ending = kinds > 1 ? "Mixed" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : cr > 0 ? "CR" : "";

            return (lines.ToArray(), ending, endsWithNewline);
        }

        private static string HashFile(string path, CancellationToken cancellationToken)
        {
            using var stream = OpenShared(path);
            using var sha = SHA256.Create();
            var buffer = new byte[StreamBuffer];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes:N0} B" : $"{value:0.#} {units[unit]}";
        }

        // =================================================================
        //  Binary / too big: byte comparison
        // =================================================================

        /// <summary>Same bytes or not, and where they first differ.</summary>
        private static FileDiffResult CompareBytes(FileDiffDocument a, FileDiffDocument b, CancellationToken cancellationToken)
        {
            if (a.Hash == b.Hash && a.Size == b.Size)
                return new FileDiffResult { Outcome = FileDiffOutcome.BinaryIdentical, SimilarityPercent = 100 };

            return new FileDiffResult
            {
                Outcome = FileDiffOutcome.BinaryDifferent,
                FirstDifferentByte = FirstDifference(a.Path, b.Path, cancellationToken),
            };
        }

        /// <summary>Offset of the first byte that differs; null when one file is the start of the other.</summary>
        internal static long? FirstDifference(string pathA, string pathB, CancellationToken cancellationToken)
        {
            using var sa = OpenShared(pathA);
            using var sb = OpenShared(pathB);
            var bufA = new byte[StreamBuffer];
            var bufB = new byte[StreamBuffer];
            long offset = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int ra = sa.ReadAtLeast(bufA, bufA.Length, throwOnEndOfStream: false);
                int rb = sb.ReadAtLeast(bufB, bufB.Length, throwOnEndOfStream: false);
                int common = Math.Min(ra, rb);

                int mismatch = bufA.AsSpan(0, common).CommonPrefixLength(bufB.AsSpan(0, common));
                if (mismatch < common) return offset + mismatch;
                if (ra != rb || ra == 0) return null;   // one ended first, or both did

                offset += common;
            }
        }

        // =================================================================
        //  Saving the diff (never overwrites)
        // =================================================================

        /// <summary>"report vs report-new 2026-10-10 14-32-05.diff"</summary>
        public static string SuggestedDiffName(string nameA, string nameB) =>
            $"{Path.GetFileNameWithoutExtension(nameA)} vs {Path.GetFileNameWithoutExtension(nameB)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.diff";

        /// <summary>
        /// Writes to a temp file next to the target, then moves it into place
        /// without overwriting: a taken name gets the date and time, then
        /// " (2)", " (3)"... Returns the path actually written.
        /// </summary>
        public static Task<string> SaveDiffAsync(string requestedPath, string content) =>
            Task.Run(() =>
            {
                string folder = Path.GetDirectoryName(requestedPath)!;
                Directory.CreateDirectory(folder);
                string temp = Path.Combine(folder, $".{Path.GetFileName(requestedPath)}.{Guid.NewGuid():N}.part");
                File.WriteAllText(temp, content, new UTF8Encoding(false));

                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        string target = FreeName(requestedPath);
                        try
                        {
                            File.Move(temp, target, overwrite: false);
                            return target;
                        }
                        catch (IOException) when (File.Exists(target) && attempt < 20)
                        {
                            // Another program took the name between the check and the move.
                        }
                    }
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            });

        internal static string FreeName(string path)
        {
            if (!File.Exists(path)) return path;

            string folder = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);

            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            if (!name.EndsWith(stamp, StringComparison.Ordinal)) name = $"{name} {stamp}";

            string candidate = Path.Combine(folder, name + ext);
            for (int n = 2; File.Exists(candidate); n++)
                candidate = Path.Combine(folder, $"{name} ({n}){ext}");
            return candidate;
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\file_differences.json)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "file_differences.json");

        /// <summary>Missing, damaged or unknown values fall back to the defaults.</summary>
        public static FileDiffSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new FileDiffSettings();
                var settings = JsonSerializer.Deserialize<FileDiffSettings>(File.ReadAllText(file), JsonOptions) ?? new FileDiffSettings();
                if (!Enum.IsDefined(settings.ViewMode)) settings.ViewMode = FileDiffViewMode.SideBySide;
                return settings;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new FileDiffSettings();
            }
        }

        public static void SaveSettings(FileDiffSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);
    }
}
