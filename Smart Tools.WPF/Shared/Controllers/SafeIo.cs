using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Reads of the user's files and folders that never throw because something vanished: a file or folder that was
    /// deleted, moved, locked or made unreadable after the tool picked it is skipped, not turned into a failure or a wait.
    /// Tools go through here instead of calling Directory.Enumerate* / FileInfo.Length / File.ReadAllBytes directly.
    /// </summary>
    public static class SafeIo
    {
        /// <summary>A missing file or folder (also a missing drive or share), as opposed to a real fault.</summary>
        public static bool IsGone(Exception ex) =>
            ex is FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException;

        /// <summary>Anything that means "this one path can't be used right now, go on with the next".</summary>
        public static bool IsSkippable(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

        /// <summary>
        /// Files under <paramref name="folder"/>. A folder that is missing, unreadable or that disappears while it is
        /// being read ends the list quietly and calls <paramref name="onSkipped"/> once; inaccessible subfolders are skipped.
        /// </summary>
        public static IEnumerable<string> EnumerateFiles(string folder, bool recurse, Action? onSkipped = null,
            string pattern = "*", FileAttributes attributesToSkip = FileAttributes.ReparsePoint)
            => Enumerate(folder, recurse, onSkipped, pattern, attributesToSkip, directories: false);

        /// <summary>Same as <see cref="EnumerateFiles"/> for folders.</summary>
        public static IEnumerable<string> EnumerateDirectories(string folder, bool recurse, Action? onSkipped = null,
            string pattern = "*", FileAttributes attributesToSkip = FileAttributes.ReparsePoint)
            => Enumerate(folder, recurse, onSkipped, pattern, attributesToSkip, directories: true);

        private static IEnumerable<string> Enumerate(string folder, bool recurse, Action? onSkipped, string pattern,
            FileAttributes attributesToSkip, bool directories)
        {
            IEnumerator<string> e;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = recurse,
                    IgnoreInaccessible = true,
                    AttributesToSkip = attributesToSkip,
                };
                e = directories
                    ? Directory.EnumerateDirectories(folder, pattern, options).GetEnumerator()
                    : Directory.EnumerateFiles(folder, pattern, options).GetEnumerator();
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                onSkipped?.Invoke();
                yield break;
            }

            using (e)
            {
                while (true)
                {
                    string current;
                    try
                    {
                        if (!e.MoveNext()) yield break;
                        current = e.Current;
                    }
                    catch (Exception ex) when (IsSkippable(ex))
                    {
                        onSkipped?.Invoke();   // the folder vanished or became unreadable part-way: keep what was found
                        yield break;
                    }
                    yield return current;
                }
            }
        }

        /// <summary>The size of a file, or false when it is gone, locked or unreadable.</summary>
        public static bool TryGetLength(string path, out long length)
        {
            length = 0;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                length = info.Length;
                return true;
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                return false;
            }
        }

        /// <summary>The whole file, or false when it is gone, locked or unreadable.</summary>
        public static bool TryReadAllBytes(string path, out byte[] bytes)
        {
            bytes = [];
            try
            {
                bytes = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                return false;
            }
        }

        /// <summary>The last-write time of a file, or false when it is gone or unreadable.</summary>
        public static bool TryGetWriteTime(string path, out DateTime utc)
        {
            utc = default;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                utc = info.LastWriteTimeUtc;
                return true;
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                return false;
            }
        }
    }
}
