using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        /// Files and folders (one level, or with subfolders when asked) to list entries, in the
        /// order Explorer shows them ("IMG_2" before "IMG_10"). Things that aren't
        /// media, or can't be read, are skipped.
        /// </summary>
        public static List<MediaItem> Expand(IEnumerable<string> paths, bool includeSubfolders = false, CancellationToken token = default)
        {
            var result = new List<MediaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = includeSubfolders, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

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
                        foreach (string file in Directory.EnumerateFiles(path, "*", options).OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance))
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

        // =================================================================
        //  Settings (media_viewer.json)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        private static readonly object SettingsLock = new();

        public static string SettingsPath => Path.Combine(AppPaths.DataRoot, "media_viewer.json");

        /// <summary>Raised after the file was saved, so an open tool page can pick up what the Settings page changed.</summary>
        public static event Action? SettingsChanged;

        public static readonly IReadOnlyList<int> SlideshowChoices = new[] { 2, 4, 7, 10 };

        /// <summary>The saved settings; defaults for anything missing, unreadable or out of range. Never throws.</summary>
        public static MediaViewerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new MediaViewerSettings();
                var s = JsonSerializer.Deserialize<MediaViewerSettings>(File.ReadAllText(file), JsonOptions) ?? new MediaViewerSettings();
                s.Volume = double.IsFinite(s.Volume) ? Math.Clamp(s.Volume, 0, 1) : 0.8;
                s.Speed = ClampSpeed(double.IsFinite(s.Speed) ? s.Speed : 1);
                if (!Enum.IsDefined(s.Order)) s.Order = ViewerPlayOrder.Sequential;
                if (!SlideshowChoices.Contains(s.SlideshowSeconds)) s.SlideshowSeconds = 4;
                s.Sources = (s.Sources ?? new()).Where(x => !string.IsNullOrWhiteSpace(x?.Path)).ToList();
                s.LastItem ??= "";
                if (s.WidgetLeft is { } l && !double.IsFinite(l)) s.WidgetLeft = null;
                if (s.WidgetTop is { } t && !double.IsFinite(t)) s.WidgetTop = null;
                if (s.WidgetWidth is { } w && (!double.IsFinite(w) || w < 100)) s.WidgetWidth = null;
                if (s.WidgetHeight is { } h && (!double.IsFinite(h) || h < 100)) s.WidgetHeight = null;
                return s;
            }
            catch (Exception)
            {
                return new MediaViewerSettings();
            }
        }

        public static void SaveSettings(MediaViewerSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);

        /// <summary>
        /// Reads the file, lets <paramref name="change"/> alter only the caller's own fields, and
        /// writes it back - so the tool page and the Settings page never overwrite each other.
        /// Never throws (a failed save is logged).
        /// </summary>
        public static void UpdateSettings(Action<MediaViewerSettings> change, bool notify = true)
        {
            try
            {
                lock (SettingsLock)
                {
                    var s = LoadSettings();
                    change(s);
                    SaveSettings(s);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"Media Viewer settings not saved: {ex.Message}");
                return;
            }
            if (notify) SettingsChanged?.Invoke();
        }

        public static readonly IReadOnlyList<double> Speeds = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

        /// <summary>The nearest speed on the list.</summary>
        public static double ClampSpeed(double speed) =>
            Speeds.OrderBy(s => Math.Abs(s - speed)).First();

        public static string SpeedLabel(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";

        /// <summary>
        /// Sidebar rows: each added folder, then added files grouped by folder.
        /// Sources with nothing left in the list are left out.
        /// </summary>
        public static List<MediaQueueGroup> BuildGroups(IEnumerable<MediaSource> sources, IReadOnlyCollection<MediaItem> items)
        {
            var list = sources.ToList();
            var groups = new List<MediaQueueGroup>();
            foreach (var s in list.Where(s => s.IsFolder))
            {
                string root = s.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                int count = items.Count(i => i.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                if (count > 0) groups.Add(new MediaQueueGroup { FolderPath = s.FullPath, IsFolder = true, ItemCount = count });
            }
            var present = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            groups.AddRange(list.Where(s => !s.IsFolder && present.Contains(s.FullPath))
                .GroupBy(s => Path.GetDirectoryName(s.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new MediaQueueGroup { FolderPath = g.Key, FilePaths = g.Select(s => s.FullPath).ToList(), ItemCount = g.Count() }));
            return groups;
        }

        /// <summary>The items an added group stands for (so removing the group removes them).</summary>
        public static List<MediaItem> ItemsOf(MediaQueueGroup group, IEnumerable<MediaItem> items)
        {
            if (!group.IsFolder)
                return items.Where(i => group.FilePaths.Contains(i.Path, StringComparer.OrdinalIgnoreCase)).ToList();
            string root = group.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return items.Where(i => i.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>The list sorted by name (folder by folder, "2" before "10") or by date modified, oldest first.</summary>
        public static List<MediaItem> Sort(IEnumerable<MediaItem> items, bool byDate)
        {
            if (!byDate)
                return items.OrderBy(i => i.Folder, NaturalStringComparer.Instance)
                    .ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();
            return items
                .Select(i => (Item: i, Modified: ModifiedOf(i.Path)))
                .OrderBy(x => x.Modified).ThenBy(x => x.Item.Name, NaturalStringComparer.Instance)
                .Select(x => x.Item).ToList();
        }

        private static DateTime ModifiedOf(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return DateTime.MaxValue; }
        }

        /// <summary>
        /// Hands out the items in a random order, each once. When they've all
        /// been played it starts a new round if asked to, otherwise it stops.
        /// </summary>
        public sealed class ShuffleBag
        {
            private readonly Random _random = new();
            private readonly List<MediaItem> _left = new();
            private bool _started;

            public void Reset()
            {
                _left.Clear();
                _started = false;
            }

            /// <summary>The user picked this one themselves - it counts as played.</summary>
            public void Played(MediaItem item)
            {
                _left.Remove(item);
                _started = true;
            }

            public MediaItem? Next(IReadOnlyList<MediaItem> all, MediaItem? current, bool repeat)
            {
                var present = new HashSet<MediaItem>(all);
                _left.RemoveAll(i => !present.Contains(i));   // removed from the list meanwhile
                if (_left.Count == 0)
                {
                    if (_started && !repeat) return null;
                    _left.AddRange(all.Where(i => !ReferenceEquals(i, current) || all.Count == 1));
                }
                if (_left.Count == 0) return null;
                int at = _random.Next(_left.Count);
                var pick = _left[at];
                _left.RemoveAt(at);
                _started = true;
                return pick;
            }
        }

        /// <summary>"3:07", or "1:02:03" once it reaches an hour.</summary>
        public static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes}:{time.Seconds:00}";
        }

        // =================================================================
        //  Files Windows' media engine can't open by name
        // =================================================================

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr reserved);

        /// <summary>Where the safely named links/copies live. Emptied on close and swept on the first use of a session.</summary>
        public static string PreviewFolder => Path.Combine(Path.GetTempPath(), "Smart Tools", "media-viewer previews");

        private static bool _previewsSwept;

        /// <summary>True for names with '#' or '%': valid Uris, yet MediaElement fails to open them.</summary>
        public static bool NeedsSafeName(string path) => Path.GetFileName(path).IndexOfAny(new[] { '#', '%' }) >= 0;

        /// <summary>
        /// A path with a plain name that opens the same bytes: a hard link when the temp folder is on the same drive (instant, no
        /// extra space), else a copy. Null when neither could be made, and the caller plays the original. Call off the UI thread.
        /// </summary>
        public static string? MakeSafeAlias(string path)
        {
            try
            {
                if (!_previewsSwept) { _previewsSwept = true; ClearPreviews(); }
                Directory.CreateDirectory(PreviewFolder);
                var info = new FileInfo(path);
                string key = $"{path.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                string name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16] + info.Extension;
                string alias = Path.Combine(PreviewFolder, name);
                if (File.Exists(alias)) return alias;
                if (!CreateHardLink(alias, path, IntPtr.Zero)) File.Copy(path, alias, overwrite: false);
                return alias;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Deletes the links/copies (best effort: one still held by the player stays until the next start).</summary>
        public static void ClearPreviews()
        {
            try
            {
                if (!Directory.Exists(PreviewFolder)) return;
                foreach (string file in Directory.EnumerateFiles(PreviewFolder))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still open: the next start sweeps it */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the folder went away
            }
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
