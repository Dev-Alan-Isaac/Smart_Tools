using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The decisions behind a resize: new size, file name, and the quality search for a target size. Pure: no Magick, no files, no WPF.</summary>
    public sealed partial class BulkImageResizerManager
    {
        public const int MinQuality = 20;

        /// <summary>The longest side each saved format can hold (WebP and JPG have hard limits).</summary>
        public static int MaxSideFor(string extension) => extension.ToLowerInvariant() switch
        {
            ".webp" => 16383,
            ".jpg" or ".jpeg" => 65500,
            _ => int.MaxValue,
        };

        /// <summary>
        /// The size after resizing, keeping the proportions. With <see cref="ResizeOptions.OnlyShrink"/> a picture is never made
        /// bigger. Never below 1 pixel and never above what <paramref name="outputExtension"/> can store.
        /// </summary>
        public static (int Width, int Height) ComputeSize(int srcWidth, int srcHeight, ResizeOptions o, string outputExtension)
        {
            if (srcWidth <= 0 || srcHeight <= 0) return (Math.Max(srcWidth, 1), Math.Max(srcHeight, 1));

            double scale = o.Mode switch
            {
                BulkResizeMode.Percent => Math.Clamp(o.Percent, 1, 400) / 100.0,
                BulkResizeMode.Width => o.Width > 0 ? (double)o.Width / srcWidth : 1,
                BulkResizeMode.Height => o.Height > 0 ? (double)o.Height / srcHeight : 1,
                _ => Math.Min(o.Width > 0 ? (double)o.Width / srcWidth : double.MaxValue, o.Height > 0 ? (double)o.Height / srcHeight : double.MaxValue),
            };
            if (scale == double.MaxValue) scale = 1;   // fit within with no limits at all
            if (o.OnlyShrink) scale = Math.Min(scale, 1);

            int max = MaxSideFor(outputExtension);
            double longest = Math.Max(srcWidth, srcHeight) * scale;
            if (longest > max) scale *= max / longest;

            int w = (int)Math.Max(1, Math.Round(srcWidth * scale));
            int h = (int)Math.Max(1, Math.Round(srcHeight * scale));
            return (w, h);
        }

        /// <summary>The extension to save with: the chosen format's, or the original's when it can be written (JPEG stays .jpg).</summary>
        public static string OutputExtension(string sourcePath, ResizeFormat format)
        {
            switch (format)
            {
                case ResizeFormat.Jpg: return ".jpg";
                case ResizeFormat.Png: return ".png";
                case ResizeFormat.WebP: return ".webp";
            }
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".jpe" => ".jpg",
                ".png" or ".webp" or ".bmp" or ".gif" => ext,
                ".tif" or ".tiff" => ".tif",
                ".svg" => ".png",
                _ => ".jpg",   // HEIC, RAW, AVIF, ...: no good way to write them back, so a widely supported JPG
            };
        }

        /// <summary>True for formats where a quality setting changes the file size.</summary>
        public static bool IsLossy(string extension) => extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".webp";

        /// <summary>
        /// Magick's error text ("insufficient image data in file `C:\...\a.jpg' @ error/jpeg.c/ReadOneJPEGImage/1555") without the
        /// source-code location, and with the file's name instead of its full path.
        /// </summary>
        public static string PlainMessage(string raw, string path)
        {
            string text = raw.Split('\n')[0];
            int at = text.IndexOf(" @ ", StringComparison.Ordinal);
            if (at > 0) text = text[..at];
            if (path.Length > 0) text = text.Replace(path, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
            return text.Trim();
        }

        /// <summary>"photo" + "-small" + ".jpg". Characters Windows doesn't allow in a file name are dropped from the suffix.</summary>
        public static string OutputFileName(string sourcePath, string suffix, string extension)
        {
            string clean = string.Concat(suffix.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0));
            return Path.GetFileNameWithoutExtension(sourcePath) + clean + extension;
        }

        /// <summary>
        /// The highest quality (at most <paramref name="startQuality"/>, at least <see cref="MinQuality"/>) whose file fits
        /// <paramref name="targetBytes"/>, found by halving the range. <paramref name="sizeAt"/> encodes at a quality and
        /// returns the byte count. Returns the minimum when even that is too big; <paramref name="fits"/> says which.
        /// </summary>
        public static int FindQuality(Func<int, long> sizeAt, long targetBytes, int startQuality, out bool fits)
        {
            int high = Math.Clamp(startQuality, MinQuality, 100), low = MinQuality;
            if (sizeAt(high) <= targetBytes) { fits = true; return high; }
            if (sizeAt(low) > targetBytes) { fits = false; return low; }

            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (sizeAt(mid) <= targetBytes) low = mid;
                else high = mid;
            }
            fits = true;
            return low;
        }

        /// <summary>
        /// Claims <paramref name="wanted"/>, or "name (2)", "name (3)", ... when another worker already claimed it. Call under a lock:
        /// two pictures with the same name from different folders can otherwise pick the same free name before either is written.
        /// </summary>
        public static string ReserveName(string wanted, ISet<string> reserved)
        {
            string folder = Path.GetDirectoryName(wanted) ?? "", name = Path.GetFileNameWithoutExtension(wanted), ext = Path.GetExtension(wanted);
            string candidate = wanted;
            for (int n = 2; reserved.Contains(candidate) || File.Exists(candidate); n++)
                candidate = Path.Combine(folder, $"{name} ({n}){ext}");
            reserved.Add(candidate);
            return candidate;
        }

        public static bool IsPictureFile(string path) => PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        public static readonly HashSet<string> PictureExtensions = new()
        {
            ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".svg",
        };

        /// <summary>Pictures from files and folders (folders optionally with everything inside). Unreadable folders are skipped. Safe to call off the UI thread.</summary>
        public static List<string> ScanPictures(IEnumerable<string> paths, bool includeSubfolders, CancellationToken token)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = includeSubfolders, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(path))
                    {
                        if (IsPictureFile(path) && seen.Add(path)) found.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        foreach (string file in Directory.EnumerateFiles(path, "*", options))
                        {
                            token.ThrowIfCancellationRequested();
                            if (IsPictureFile(file) && seen.Add(file)) found.Add(file);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A folder we can't read is skipped; the others are still added.
                }
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }
    }
}
