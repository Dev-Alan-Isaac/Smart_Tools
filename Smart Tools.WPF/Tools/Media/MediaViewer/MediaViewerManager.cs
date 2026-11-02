using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Media Viewer backend - no WPF. Works out what a file is (picture,
    /// video or sound), turns files and folders into list entries, and
    /// formats times and sizes. Pictures reuse <see cref="ImageViewerManager"/>
    /// for their extensions and decoding.
    /// </summary>
    public static class MediaViewerManager
    {
        public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp",
        };

        public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".mid", ".midi",
        };

        public static ViewerMediaKind? Classify(string path)
        {
            string ext = Path.GetExtension(path);
            if (ImageViewerManager.ImageExtensions.Contains(ext)) return ViewerMediaKind.Image;
            if (VideoExtensions.Contains(ext)) return ViewerMediaKind.Video;
            if (AudioExtensions.Contains(ext)) return ViewerMediaKind.Audio;
            return null;
        }

        private static string Pattern(IEnumerable<string> extensions) => string.Join(";", extensions.Select(e => "*" + e));

        public static string FileDialogFilter =>
            "Media files|" + Pattern(ImageViewerManager.ImageExtensions.Concat(VideoExtensions).Concat(AudioExtensions)) +
            "|Images|" + Pattern(ImageViewerManager.ImageExtensions) +
            "|Video|" + Pattern(VideoExtensions) +
            "|Audio|" + Pattern(AudioExtensions) +
            "|All files (*.*)|*.*";

        /// <summary>
        /// Files and folders (one level, not subfolders) to list entries, in the
        /// order Explorer shows them ("IMG_2" before "IMG_10"). Things that aren't
        /// media, or can't be read, are skipped.
        /// </summary>
        public static List<MediaItem> Expand(IEnumerable<string> paths, CancellationToken token = default)
        {
            var result = new List<MediaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

            void AddFile(string file)
            {
                if (Classify(file) is ViewerMediaKind kind && seen.Add(file)) result.Add(new MediaItem(file, kind));
            }

            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(path))
                    {
                        foreach (string file in Directory.EnumerateFiles(path, "*", options).OrderBy(f => Path.GetFileName(f), RenameManager.NaturalComparer.Instance))
                            AddFile(file);
                    }
                    else if (File.Exists(path))
                    {
                        AddFile(path);
                    }
                }
                catch (Exception)
                {
                    // one unreadable folder doesn't stop the rest
                }
            }
            return result;
        }

        /// <summary>"3:07", or "1:02:03" once it reaches an hour.</summary>
        public static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes}:{time.Seconds:00}";
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
        }
    }
}
