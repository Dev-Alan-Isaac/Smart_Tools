using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Logic behind the shared Output card (Controls/OutputFolderCard): is
    /// the folder usable, how much room is left, what a dropped item means,
    /// the default folder, and names that never overwrite. Pure - no WPF -
    /// and unit-tested. The card remembers the folder for the session only.
    /// </summary>
    public static class OutputFolderManager
    {
        /// <summary>Below this the card warns (amber) but saving still works.</summary>
        public const long LowSpaceBytes = 1L * 1024 * 1024 * 1024;

        /// <summary>
        /// Checks a folder. Touches the disk (and a network share can be slow),
        /// so the card calls it off the UI thread. Never throws.
        /// </summary>
        public static OutputFolderStatus Describe(string? folder, bool sameAsSource = false)
        {
            if (sameAsSource)
                return new OutputFolderStatus(OutputFolderState.SameAsSource, "Each result is saved next to its source file", null);
            if (string.IsNullOrWhiteSpace(folder))
                return new OutputFolderStatus(OutputFolderState.None, "Choose where the results go", null);

            string full;
            try
            {
                full = Path.GetFullPath(folder.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                return new OutputFolderStatus(OutputFolderState.Missing, "That isn't a folder path", null);
            }

            try
            {
                if (Directory.Exists(full))
                {
                    long? free = FreeSpace(full);
                    if (!CanWrite(full))
                        return new OutputFolderStatus(OutputFolderState.NotWritable, "Windows won't let the app save here - choose another folder", free);
                    if (free is long f && f < LowSpaceBytes)
                        return new OutputFolderStatus(OutputFolderState.LowSpace, $"Only {FormatBytes(f)} free", free);
                    return new OutputFolderStatus(OutputFolderState.Ready, free is long ok ? $"{FormatBytes(ok)} free" : "Ready", free);
                }

                // Not there: fine if it can be made - the nearest folder above it that exists is
                // writable (EnsureFolder creates every missing level, e.g. Pictures\Smart Tools\GIF Maker).
                string? parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                while (parent != null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
                if (parent != null && CanWrite(parent))
                    return new OutputFolderStatus(OutputFolderState.WillBeCreated, "Made on the first save", FreeSpace(parent));
                return new OutputFolderStatus(OutputFolderState.Missing, "This folder isn't there any more (unplugged drive?)", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new OutputFolderStatus(OutputFolderState.Missing, "This folder can't be reached right now", null);
            }
        }

        /// <summary>Free bytes on the drive holding the folder, or null if it can't be told (some network shares).</summary>
        public static long? FreeSpace(string folder)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                var drive = new DriveInfo(root);
                return drive.IsReady ? drive.AvailableFreeSpace : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Really tries: creates and deletes a hidden probe file. Checking the
        /// folder's attributes isn't enough (permissions, read-only shares,
        /// Controlled Folder Access).
        /// </summary>
        public static bool CanWrite(string folder)
        {
            string probe = Path.Combine(folder, $".smarttools-{Guid.NewGuid():N}.probe");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>"12.4 GB", "830 MB", "4 KB".</summary>
        public static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return u == 0 ? $"{bytes:N0} bytes" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[u];
        }

        /// <summary>The folder's own name for the card's first line ("D:\" for a drive root).</summary>
        public static string DisplayName(string folder)
        {
            string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? folder : name;
        }

        /// <summary>What a drop means: the first folder dropped, or the folder a dropped file is in. Null if nothing usable.</summary>
        public static string? FolderFromDrop(IEnumerable<string>? paths)
        {
            foreach (string p in paths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (Directory.Exists(p)) return p;
                if (File.Exists(p)) return Path.GetDirectoryName(p);
            }
            return null;
        }

        /// <summary>
        /// Where a tool saves until the user picks: Documents\Smart Tools\&lt;tool&gt;
        /// (made on the first save). Never a drive root or the app folder.
        /// </summary>
        public static string DefaultFolder(string toolName, string? documents = null)
        {
            string docs = documents ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Path.GetTempPath();
            return Path.Combine(docs, "Smart Tools", SafeName(toolName));
        }

        /// <summary>
        /// The folder results go to now: the one the user chose, else the
        /// tool's own default (<paramref name="defaultFolder"/>, or
        /// Documents\Smart Tools\&lt;tool&gt;). Null when results go next to
        /// their sources, or nothing is chosen and defaults are off.
        /// </summary>
        public static string? EffectiveFolder(string? chosen, bool sameAsSource, string? defaultFolder, string? toolName, bool useDefault = true)
        {
            if (sameAsSource) return null;
            if (!string.IsNullOrWhiteSpace(chosen)) return chosen.Trim();
            if (!useDefault) return null;
            if (!string.IsNullOrWhiteSpace(defaultFolder)) return defaultFolder;
            return string.IsNullOrWhiteSpace(toolName) ? null : DefaultFolder(toolName);
        }

        /// <summary>A name Windows accepts as a folder or file name (bad characters become "-", no trailing dot or space).</summary>
        public static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (name ?? "").Trim().Select(c => invalid.Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '/' or '\\' ? '-' : c).ToArray();
            string s = new string(chars).TrimEnd('.', ' ');
            return s.Length == 0 ? "Output" : s;
        }

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// What's typed in File name, made into a file name Windows accepts:
        /// a name, not a path (folders typed in are dropped), bad characters
        /// become "-", no trailing dot or space, Windows' reserved names get a
        /// "_" in front, the extension is added when missing, and empty means
        /// <paramref name="fallbackName"/>.
        /// </summary>
        public static string CleanFileName(string? typed, string extension, string fallbackName)
        {
            string ext = string.IsNullOrWhiteSpace(extension) ? "" : (extension.StartsWith('.') ? extension : "." + extension);
            string name = (typed ?? "").Trim();
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name[(slash + 1)..];
            if (ext.Length > 0 && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
            name = name.Trim();
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(fallbackName);
            name = SafeName(name);
            if (ReservedNames.Contains(name) || ReservedNames.Contains(name.Split('.')[0])) name = "_" + name;
            if (name.Length > 200) name = name[..200].TrimEnd('.', ' ');
            return name + ext;
        }

        /// <summary>
        /// The file a single-file tool saves: folder + cleaned name, made unique
        /// (" (2)") so an earlier file is never replaced. Null without a folder.
        /// </summary>
        public static string? ResolveFilePath(string? folder, string? typedName, string extension, string fallbackName)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            return UniquePath(Path.Combine(folder, CleanFileName(typedName, extension, fallbackName)));
        }

        /// <summary>The card's line under the file name: "Saves as D:\Exports\Holiday (2).pdf" (shortened in the middle).</summary>
        public static string SaveAsText(string? resolvedPath, bool askEachTime, int maxLength = 60)
        {
            if (askEachTime) return "You'll choose where to save each time";
            return resolvedPath == null ? "" : "Saves as " + ShortenMiddle(resolvedPath, maxLength);
        }

        /// <summary>The path itself if free, else " (2)", " (3)"... before the extension. Results never replace a file.</summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string folder = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(folder, $"{name} ({n}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Where one result goes: the chosen folder, or next to its source.
        /// Made unique, so an earlier result is never overwritten.
        /// </summary>
        public static string ResultPath(string sourcePath, string? outputFolder, bool sameAsSource, string newFileName)
        {
            string folder = sameAsSource || string.IsNullOrWhiteSpace(outputFolder)
                ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ""
                : outputFolder;
            return UniquePath(Path.Combine(folder, newFileName));
        }

        /// <summary>The card's example line: "e.g. D:\Exports\photo-converted.png" (shortened in the middle when long).</summary>
        public static string ExampleText(string? folder, bool sameAsSource, string exampleFileName, int maxLength = 60)
        {
            if (string.IsNullOrWhiteSpace(exampleFileName)) return "";
            string where = sameAsSource || string.IsNullOrWhiteSpace(folder) ? exampleFileName : Path.Combine(folder, exampleFileName);
            if (sameAsSource) where = "…" + Path.DirectorySeparatorChar + exampleFileName;
            return "e.g. " + ShortenMiddle(where, maxLength);
        }

        public static string ShortenMiddle(string text, int maxLength)
        {
            if (text.Length <= maxLength || maxLength < 9) return text;
            int keepEnd = maxLength * 2 / 3;
            int keepStart = maxLength - keepEnd - 1;
            return text[..keepStart] + "…" + text[^keepEnd..];
        }
    }
}
