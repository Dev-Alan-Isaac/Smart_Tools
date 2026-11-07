using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Image Editor backend, in three parts:
    /// <list type="bullet">
    /// <item>ImageEditingManager.cs (this file) - no side effects: rotate/flip
    /// maths, mapping a crop drawn on the turned preview back to the file,
    /// output sizes and names, listing folders, settings. Unit tested.</item>
    /// <item>ImageEditingManager.Run.cs - exporting a batch (per-image
    /// errors, cancel, unique names).</item>
    /// <item>ImageEditingManager.Imaging.cs - Magick.NET render and the
    /// WPF preview/thumbnail loaders.</item>
    /// </list>
    /// </summary>
    public sealed partial class ImageEditingManager
    {
        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".ico",
        };

        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

        // =================================================================
        //  Orientation: display = mirror? ∘ rotate(r) (source)
        // =================================================================

        private static int Norm(int degrees) => ((degrees % 360) + 360) % 360;

        /// <summary>Turn what you see a quarter clockwise.</summary>
        public static ImageOrientation RotateClockwise(ImageOrientation o) =>
            new(Norm(o.Mirror ? o.Rotation - 90 : o.Rotation + 90), o.Mirror);

        public static ImageOrientation RotateCounterClockwise(ImageOrientation o) =>
            new(Norm(o.Mirror ? o.Rotation + 90 : o.Rotation - 90), o.Mirror);

        /// <summary>Mirror what you see left-right.</summary>
        public static ImageOrientation FlipHorizontal(ImageOrientation o) => new(o.Rotation, !o.Mirror);

        /// <summary>Mirror what you see top-bottom.</summary>
        public static ImageOrientation FlipVertical(ImageOrientation o) => new(Norm(o.Rotation + 180), !o.Mirror);

        /// <summary>EXIF orientation tag (1-8) as the same kind of turn.</summary>
        public static ImageOrientation FromExif(int exif) => exif switch
        {
            2 => new(0, true),
            3 => new(180, false),
            4 => new(180, true),
            5 => new(90, true),
            6 => new(90, false),
            7 => new(270, true),
            8 => new(270, false),
            _ => ImageOrientation.Identity,
        };

        /// <summary>Where a point of the source (fractions 0..1) ends up on screen.</summary>
        public static (double X, double Y) SourceToDisplay(double u, double v, ImageOrientation o)
        {
            for (int i = 0; i < o.Rotation / 90; i++) (u, v) = (1 - v, u);   // quarter turn clockwise
            if (o.Mirror) u = 1 - u;
            return (u, v);
        }

        public static (double U, double V) DisplayToSource(double x, double y, ImageOrientation o)
        {
            if (o.Mirror) x = 1 - x;
            for (int i = 0; i < o.Rotation / 90; i++) (x, y) = (y, 1 - x);   // quarter turn back
            return (x, y);
        }

        /// <summary>A crop drawn on the turned preview, as a crop of the untouched image.</summary>
        public static CropRect DisplayToSourceRect(CropRect display, ImageOrientation o)
        {
            var a = DisplayToSource(display.Left, display.Top, o);
            var b = DisplayToSource(display.Right, display.Bottom, o);
            double l = Math.Clamp(Math.Min(a.U, b.U), 0, 1), t = Math.Clamp(Math.Min(a.V, b.V), 0, 1);
            double r = Math.Clamp(Math.Max(a.U, b.U), 0, 1), btm = Math.Clamp(Math.Max(a.V, b.V), 0, 1);
            return new CropRect(l, t, r - l, btm - t);
        }

        /// <summary>A stored crop as it looks on the turned preview.</summary>
        public static CropRect SourceToDisplayRect(CropRect source, ImageOrientation o)
        {
            var a = SourceToDisplay(source.Left, source.Top, o);
            var b = SourceToDisplay(source.Right, source.Bottom, o);
            double l = Math.Min(a.X, b.X), t = Math.Min(a.Y, b.Y);
            return new CropRect(l, t, Math.Max(a.X, b.X) - l, Math.Max(a.Y, b.Y) - t);
        }

        // =================================================================
        //  Crop marquee
        // =================================================================

        /// <summary>Crop shapes offered in the sidebar (width:height, or 0 for free).</summary>
        public static readonly IReadOnlyList<(string Key, string Label, double Ratio)> CropAspects = new[]
        {
            ("Free", "Free", 0.0),
            ("1:1", "1:1", 1.0),
            ("4:3", "4:3", 4.0 / 3),
            ("3:2", "3:2", 3.0 / 2),
            ("16:9", "16:9", 16.0 / 9),
        };

        public static double AspectFor(string? key) => CropAspects.FirstOrDefault(a => a.Key == key).Ratio;

        /// <summary>
        /// The marquee from the drag start to the pointer, in preview pixels,
        /// kept inside the image and (when a shape is chosen) at that
        /// width:height, growing from the start corner.
        /// </summary>
        public static (double Left, double Top, double Width, double Height) Marquee(
            double startX, double startY, double x, double y, double maxW, double maxH, double aspect)
        {
            startX = Math.Clamp(startX, 0, maxW);
            startY = Math.Clamp(startY, 0, maxH);
            x = Math.Clamp(x, 0, maxW);
            y = Math.Clamp(y, 0, maxH);
            double w = Math.Abs(x - startX), h = Math.Abs(y - startY);

            if (aspect > 0)
            {
                // Follow the larger movement, then fit inside the room left in that direction.
                if (w / aspect > h) h = w / aspect; else w = h * aspect;
                double roomW = x >= startX ? maxW - startX : startX;
                double roomH = y >= startY ? maxH - startY : startY;
                double shrink = Math.Min(1, Math.Min(w > 0 ? roomW / w : 1, h > 0 ? roomH / h : 1));
                w *= shrink;
                h *= shrink;
            }

            double left = x >= startX ? startX : startX - w;
            double top = y >= startY ? startY : startY - h;
            return (left, top, w, h);
        }

        /// <summary>Normalized crop in pixels, always at least 1x1 and inside the image.</summary>
        public static (int X, int Y, int Width, int Height) CropPixels(CropRect crop, int width, int height)
        {
            int x = Math.Clamp((int)Math.Round(crop.Left * width), 0, Math.Max(0, width - 1));
            int y = Math.Clamp((int)Math.Round(crop.Top * height), 0, Math.Max(0, height - 1));
            int w = Math.Clamp((int)Math.Round(crop.Width * width), 1, width - x);
            int h = Math.Clamp((int)Math.Round(crop.Height * height), 1, height - y);
            return (x, y, w, h);
        }

        // =================================================================
        //  Output size
        // =================================================================

        /// <summary>
        /// Final size from the Resize fields. Keep shape: fit inside the box
        /// (the old version stretched to exactly W x H even with "maintain
        /// aspect" ticked). Not keeping shape: the typed sides are used as
        /// they are. Smaller images aren't enlarged unless allowed.
        /// </summary>
        public static (int Width, int Height) ResolveTargetSize(int width, int height, ImageEditOptions o)
        {
            if (width <= 0 || height <= 0) return (Math.Max(1, width), Math.Max(1, height));
            int? tw = o.ResizeWidth is > 0 ? o.ResizeWidth : null;
            int? th = o.ResizeHeight is > 0 ? o.ResizeHeight : null;
            if (tw == null && th == null) return (width, height);

            if (o.KeepAspect)
            {
                double scale = Math.Min(tw is int a ? (double)a / width : double.MaxValue, th is int b ? (double)b / height : double.MaxValue);
                if (!o.AllowEnlarge) scale = Math.Min(scale, 1);
                return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            }

            int w = tw ?? width, h = th ?? height;
            if (!o.AllowEnlarge)
            {
                w = Math.Min(w, width);
                h = Math.Min(h, height);
            }
            return (Math.Max(1, w), Math.Max(1, h));
        }

        /// <summary>What an image will measure once exported: crop, then turn, then resize.</summary>
        public static (int Width, int Height) PlannedSize(int width, int height, ImageOrientation orientation, CropRect? crop, ImageEditOptions o)
        {
            if (width <= 0 || height <= 0) return (0, 0);
            if (crop is CropRect c)
            {
                var px = CropPixels(c, width, height);
                (width, height) = (px.Width, px.Height);
            }
            if (orientation.SwapsSides) (width, height) = (height, width);
            return ResolveTargetSize(width, height, o);
        }

        // =================================================================
        //  Formats and names
        // =================================================================

        public static string ExtensionFor(ImageOutputFormat format) => format switch
        {
            ImageOutputFormat.Png => ".png",
            ImageOutputFormat.Webp => ".webp",
            ImageOutputFormat.Bmp => ".bmp",
            ImageOutputFormat.Gif => ".gif",
            ImageOutputFormat.Tiff => ".tiff",
            _ => ".jpg",
        };

        public static bool UsesQuality(ImageOutputFormat format) => format is ImageOutputFormat.Jpg or ImageOutputFormat.Webp;

        /// <summary>JPG and BMP have no transparency: transparent areas become white, not black.</summary>
        public static bool NeedsFlatten(ImageOutputFormat format) => format is ImageOutputFormat.Jpg or ImageOutputFormat.Bmp;

        private static readonly char[] BadNameChars = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

        public static string CleanSuffix(string? suffix) =>
            new string((suffix ?? "").Where(c => !BadNameChars.Contains(c)).ToArray()).TrimEnd('.', ' ');

        /// <summary>
        /// "holiday_edited.jpg", or "holiday_edited (2).jpg" when that name is
        /// taken on disk or earlier in this batch. Never the source file itself.
        /// </summary>
        public static string OutputPath(string sourcePath, ImageEditOptions o, ISet<string> usedInBatch)
        {
            string stem = Path.GetFileNameWithoutExtension(sourcePath) + CleanSuffix(o.NameSuffix);
            string ext = ExtensionFor(o.Format);
            string candidate = Path.Combine(o.OutputFolder, stem + ext);
            for (int n = 2; usedInBatch.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate)
                || string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase); n++)
                candidate = Path.Combine(o.OutputFolder, $"{stem} ({n}){ext}");
            usedInBatch.Add(candidate);
            return candidate;
        }

        public static string DefaultOutputFolder()
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures)) pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(pictures, "Smart Tools", "Edited");
        }

        public static string DescribeResult(ImageEditResult r)
        {
            var parts = new List<string> { $"Saved {r.Exported:N0} image{(r.Exported == 1 ? "" : "s")} in {r.Elapsed.TotalSeconds:0.0} s" };
            if (r.Exported > 0) parts.Add(ImageEditText.FormatBytes(r.OutputBytes));
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed");
            if (r.Cancelled > 0) parts.Add($"{r.Cancelled:N0} cancelled");
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  Adding files
        // =================================================================

        public sealed class ExpandResult
        {
            public List<string> Files { get; } = new();
            public int InaccessibleFolders { get; set; }
        }

        /// <summary>
        /// Images in the given files/folders, off the UI thread. An unreadable
        /// subfolder is skipped (the old Directory.EnumerateFiles gave up on
        /// the whole folder), and linked folders aren't followed.
        /// </summary>
        public static Task<ExpandResult> ExpandAsync(IEnumerable<string> paths, bool includeSubfolders, CancellationToken token = default)
        {
            var list = paths.ToList();
            return Task.Run(() =>
            {
                var result = new ExpandResult();
                foreach (string path in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(path))
                    {
                        if (IsImage(path)) result.Files.Add(Path.GetFullPath(path));
                        continue;
                    }
                    if (!Directory.Exists(path)) continue;

                    var pending = new Stack<string>();
                    pending.Push(path);
                    while (pending.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        string dir = pending.Pop();
                        try
                        {
                            foreach (string f in Directory.EnumerateFiles(dir).Where(IsImage).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                            {
                                var attrs = File.GetAttributes(f);
                                if (!attrs.HasFlag(FileAttributes.Hidden) && !attrs.HasFlag(FileAttributes.System)) result.Files.Add(f);
                            }
                            if (!includeSubfolders) continue;
                            foreach (string sub in Directory.EnumerateDirectories(dir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                            {
                                var attrs = File.GetAttributes(sub);
                                if (attrs.HasFlag(FileAttributes.ReparsePoint) || attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) continue;
                                pending.Push(sub);
                            }
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                        {
                            result.InaccessibleFolders++;
                        }
                    }
                }
                return result;
            }, token);
        }

        // =================================================================
        //  The list (Files card rows + image list)
        // =================================================================

        /// <summary>List rows for found images; a file that vanished or can't be read is left out.</summary>
        public static List<ImageItem> CreateItems(IEnumerable<string> paths)
        {
            var items = new List<ImageItem>();
            foreach (string path in paths)
            {
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    continue;
                }
                items.Add(new ImageItem { FullPath = path, Name = Path.GetFileName(path), SizeBytes = size });
            }
            return items;
        }

        /// <summary>Adds images not already listed as one Files card row. Returns the items added.</summary>
        public static List<ImageItem> AddToList(
            IList<ImageItem> list,
            ICollection<ImageEditGroup> groups,
            IEnumerable<string> paths,
            string? folderPath,
            bool isFolder,
            bool includeSubfolders)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = CreateItems(paths.Where(p => known.Add(p)));
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new ImageEditGroup
            {
                FolderPath = folderPath ?? (folders.Count == 1 ? folders[0] : null),
                IsFolder = isFolder,
                IncludeSubfolders = includeSubfolders,
            };
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
                list.Add(item);
            }
            groups.Add(group);
            return added;
        }

        /// <summary>Takes images off the list and their Files card rows (an emptied row goes too).</summary>
        public static void RemoveFromList(IList<ImageItem> list, ICollection<ImageEditGroup> groups, IReadOnlyCollection<ImageItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<ImageItem>(items);
            foreach (var item in items) list.Remove(item);
            foreach (var group in items.Select(i => i.Group).OfType<ImageEditGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
        }

        /// <summary>Moves the chosen images one place earlier (-1) or later (+1). Returns false when nothing moved.</summary>
        public static bool Move(IList<ImageItem> list, IReadOnlyCollection<ImageItem> chosen, int direction)
        {
            if (chosen.Count == 0 || direction == 0) return false;
            var set = new HashSet<ImageItem>(chosen);
            bool moved = false;
            if (direction < 0)
            {
                for (int i = 1; i < list.Count; i++)
                    if (set.Contains(list[i]) && !set.Contains(list[i - 1]))
                    {
                        (list[i - 1], list[i]) = (list[i], list[i - 1]);
                        moved = true;
                    }
            }
            else
            {
                for (int i = list.Count - 2; i >= 0; i--)
                    if (set.Contains(list[i]) && !set.Contains(list[i + 1]))
                    {
                        (list[i + 1], list[i]) = (list[i], list[i + 1]);
                        moved = true;
                    }
            }
            return moved;
        }

        /// <summary>By folder then natural name (Photo 2 before Photo 10), or oldest file first.</summary>
        public static List<ImageItem> Sorted(IEnumerable<ImageItem> items, bool byDate)
        {
            if (!byDate)
                return items.OrderBy(i => i.Folder, NaturalStringComparer.Instance).ThenBy(i => i.Name, NaturalStringComparer.Instance).ToList();
            return items
                .Select(i => (Item: i, Modified: ModifiedOf(i.FullPath)))
                .OrderBy(x => x.Modified).ThenBy(x => x.Item.Name, NaturalStringComparer.Instance)
                .Select(x => x.Item).ToList();
        }

        private static DateTime ModifiedOf(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return DateTime.MaxValue; }
        }

        /// <summary>Deletes the file from disk: to the Recycle Bin on a local fixed drive, otherwise permanently.</summary>
        public static void DeleteImage(string path)
        {
            if (!File.Exists(path)) return;
            if (ImageViewerManager.HasRecycleBin(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }

        /// <summary>
        /// After "Include subfolders" changes: brings a folder row in line with
        /// a fresh scan. Images no longer found come off the list, new ones go
        /// at the end; the rest keep their place, tick and edits (rotation,
        /// crop) - nothing the user did to them is lost.
        /// </summary>
        public static (List<ImageItem> Added, List<ImageItem> Removed) ApplyRescan(
            IList<ImageItem> list,
            ICollection<ImageEditGroup> groups,
            ImageEditGroup group,
            IReadOnlyCollection<string> foundPaths,
            bool includeSubfolders)
        {
            var found = new HashSet<string>(foundPaths, StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            foreach (var item in gone)
            {
                list.Remove(item);
                group.Items.Remove(item);
            }

            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = CreateItems(foundPaths.Where(p => known.Add(p)));
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
                list.Add(item);
            }

            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            return (added, gone);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "image_editor.json");

        public static ImageEditorSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<ImageEditorSettings>(File.ReadAllText(file), JsonOptions) : null;
                s ??= new ImageEditorSettings();
                s.Quality = Math.Clamp(s.Quality, 1, 100);
                s.NameSuffix = CleanSuffix(s.NameSuffix);
                if (AspectFor(s.CropAspect) == 0 && s.CropAspect != "Free") s.CropAspect = "Free";
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                return new ImageEditorSettings();
            }
        }

        public static void SaveSettings(ImageEditorSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never fail the tool over them.
            }
        }
    }
}
