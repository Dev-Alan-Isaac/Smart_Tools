using ImageMagick;
using ImageMagick.Formats;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Image Metadata Cleaner - finds the hidden details in pictures (location, camera, date, software) and writes clean
    /// copies. Originals are never changed. JPG files are cleaned without re-encoding (the pixels are copied byte for
    /// byte, so there is no quality loss); other formats are re-written with Magick.NET. A copy is written to a temporary
    /// name and moved into place when complete. Create on the UI thread: updates are posted back to the captured context.
    /// </summary>
    public sealed class MetadataCleanerManager
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
        private static readonly string[] Cleanable = { ".jpg", ".jpeg", ".jpe", ".png", ".webp", ".tif", ".tiff" };
        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        // =================================================================
        //  Reading what is inside
        // =================================================================

        /// <summary>Looks inside a picture (only its header is read). Null when it can't be read.</summary>
        public static MetaFound? ReadFound(string path)
        {
            try
            {
                using var image = new MagickImage();
                image.Ping(path);
                var exif = image.GetExifProfile();

                bool location = exif != null && HasLocationTags(exif);
                string camera = "", taken = "";
                if (exif != null)
                {
                    string make = exif.GetValue(ExifTag.Make)?.Value?.Trim() ?? "";
                    string model = exif.GetValue(ExifTag.Model)?.Value?.Trim() ?? "";
                    camera = model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}".Trim();
                    taken = FormatTaken(exif.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif.GetValue(ExifTag.DateTime)?.Value);
                }

                bool other = image.ProfileNames.Any(n => n.Equals("xmp", StringComparison.OrdinalIgnoreCase) || n.Equals("iptc", StringComparison.OrdinalIgnoreCase) || n.Equals("8bim", StringComparison.OrdinalIgnoreCase))
                    || (exif != null && (exif.GetValue(ExifTag.Software) != null || exif.GetValue(ExifTag.Artist) != null || exif.GetValue(ExifTag.Copyright) != null || exif.GetValue(ExifTag.UserComment) != null || exif.GetValue(ExifTag.ImageDescription) != null));
                return new MetaFound(location, camera, taken, other);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return null;
            }
        }

        private static bool HasLocationTags(IExifProfile exif) =>
            exif.Values.Any(v => v.Tag.ToString().StartsWith("GPS", StringComparison.OrdinalIgnoreCase));

        /// <summary>"2024:05:01 12:30:00" as "2024-05-01 12:30". Anything else is shown as it is.</summary>
        public static string FormatTaken(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            if (raw.Length >= 16 && raw[4] == ':' && raw[7] == ':') return $"{raw[..4]}-{raw.Substring(5, 2)}-{raw.Substring(8, 2)} {raw.Substring(11, 5)}";
            return raw;
        }

        public static bool CanClean(string path) => Cleanable.Contains(Path.GetExtension(path).ToLowerInvariant());

        // =================================================================
        //  Cleaning
        // =================================================================

        /// <summary>
        /// Cleans every item into a new file. Errors are kept per picture (the rest carry on); cancelling stops after the
        /// pictures already being worked on, and what was finished stays finished.
        /// </summary>
        public async Task RunAsync(IReadOnlyList<MetaItem> items, CleanMode mode, string suffix, string? outputFolder, bool sameAsSource, IProgress<CleanProgress>? progress, CancellationToken token)
        {
            var gate = new object();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int done = 0, failed = 0, skipped = 0;
            var lastReport = DateTime.MinValue;

            void Report(bool force)
            {
                if (progress == null) return;
                CleanProgress snapshot;
                lock (gate)
                {
                    var now = DateTime.UtcNow;
                    if (!force && now - lastReport < ProgressInterval) return;
                    lastReport = now;
                    snapshot = new CleanProgress(done + failed + skipped, items.Count, failed, skipped);
                }
                progress.Report(snapshot);
            }

            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                CancellationToken = token,
            };

            try
            {
                await Parallel.ForEachAsync(items, parallel, async (item, ct) =>
                {
                    await Task.Run(() =>
                    {
                        Post(() => item.Status = CleanStatus.Working);
                        try
                        {
                            if (!CanClean(item.Path)) throw new NotSupportedException("This picture type can't be cleaned yet (JPG, PNG, WebP and TIFF can).");
                            if (item.IsAlreadyClean) throw new NotSupportedException("Nothing to remove: no hidden details were found.");

                            string ext = Path.GetExtension(item.Path);
                            string target;
                            lock (gate) target = BulkImageResizerManager.ReserveName(OutputFolderManager.ResultPath(item.Path, outputFolder, sameAsSource, BulkImageResizerManager.OutputFileName(item.Path, suffix, ext)), reserved);

                            string note = CleanOne(item.Path, target, mode, ct);
                            long bytes = new FileInfo(target).Length;
                            lock (gate) done++;
                            Post(() =>
                            {
                                item.NewPath = target;
                                item.NewBytes = bytes;
                                item.Message = note;
                                item.Status = CleanStatus.Done;
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            Post(() => item.Status = CleanStatus.Pending);
                            throw;
                        }
                        catch (NotSupportedException ex)
                        {
                            lock (gate) skipped++;
                            Post(() => { item.Message = ex.Message; item.Status = CleanStatus.Skipped; });
                        }
                        catch (Exception ex) when (IsExpected(ex))
                        {
                            lock (gate) failed++;
                            string reason = ex is TypeInitializationException or DllNotFoundException or BadImageFormatException
                                ? "The picture library couldn't be loaded, so nothing can be cleaned."
                                : BulkImageResizerManager.PlainMessage(ex.Message, item.Path);
                            Post(() => { item.Message = reason; item.Status = CleanStatus.Failed; });
                        }
                        Report(false);
                    }, ct);
                });
            }
            catch (OperationCanceledException)
            {
                // Cancelled: the pictures that finished keep their results.
            }
            Report(true);
        }

        /// <summary>Writes the clean copy and returns a note for the results list (empty when there is nothing to say).</summary>
        private static string CleanOne(string input, string output, CleanMode mode, CancellationToken token)
        {
            string ext = Path.GetExtension(input).ToLowerInvariant();
            bool jpeg = ext is ".jpg" or ".jpeg" or ".jpe";
            string note = "";
            string temp = output + ".tmp";
            try
            {
                if (jpeg && mode == CleanMode.Everything && !NeedsRotationKept(input))
                {
                    // The whole JPEG is held (original + stripped copy) while it is rewritten: ask the budget first.
                    using var bytesLease = MemoryBudget.Acquire(new FileInfo(input).Length * 2, token);
                    byte[] clean = JpegStripper.Strip(File.ReadAllBytes(input));
                    token.ThrowIfCancellationRequested();
                    File.WriteAllBytes(temp, clean);
                }
                else
                {
                    if (MagickImageInfo.ReadCollection(input).Skip(1).Any())
                        throw new NotSupportedException("Animated and multi-page pictures can't be cleaned here.");

                    using var loaded = ImageLoad.Open(input, token);
                    var image = loaded.Image;
                    token.ThrowIfCancellationRequested();
                    uint quality = image.Quality;

                    if (mode == CleanMode.Everything)
                    {
                        image.AutoOrient();   // bake the rotation in, so the result isn't sideways once the tag is gone
                        foreach (string name in image.ProfileNames.ToList())
                            if (!name.Equals("icc", StringComparison.OrdinalIgnoreCase) && !name.Equals("icm", StringComparison.OrdinalIgnoreCase))
                                image.RemoveProfile(name);
                    }
                    else
                    {
                        var exif = image.GetExifProfile();
                        if (exif != null)
                        {
                            foreach (var tag in exif.Values.Where(v => v.Tag.ToString().StartsWith("GPS", StringComparison.OrdinalIgnoreCase)).Select(v => v.Tag).ToList())
                                exif.RemoveValue(tag);
                            image.SetProfile(exif);
                        }
                        image.RemoveProfile("xmp");   // XMP can carry its own copy of the location
                    }

                    if (jpeg || ext == ".webp") image.Quality = quality > 0 ? quality : 90;
                    if (jpeg) note = "Saved again as a new JPG (rotation kept, tiny quality change).";
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        image.Write(stream);
                    token.ThrowIfCancellationRequested();
                }
                File.Move(temp, output);
            }
            catch
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
            return note;
        }

        /// <summary>True when the JPG has a rotation tag: removing it byte-for-byte would leave the picture sideways.</summary>
        private static bool NeedsRotationKept(string path)
        {
            try
            {
                using var image = new MagickImage();
                image.Ping(path);
                var value = image.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
                return value is > 1;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return true;   // can't tell: take the careful path
            }
        }

        private static bool IsExpected(Exception ex) =>
            ex is MagickException or IOException or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException or TypeInitializationException or DllNotFoundException or BadImageFormatException or InvalidDataException;

        private void Post(Action action)
        {
            if (_context == null) action();
            else _context.Post(_ => action(), null);
        }

        // =================================================================
        //  Settings
        // =================================================================

        public static string SettingsPath => Path.Combine(AppPaths.DataRoot, "metadata-cleaner.json");

        public static MetaCleanSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return Sanitize(JsonSerializer.Deserialize<MetaCleanSettings>(File.ReadAllText(SettingsPath), SettingsJson) ?? new MetaCleanSettings());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A damaged file: start from defaults.
            }
            return new MetaCleanSettings();
        }

        public static void SaveSettings(MetaCleanSettings settings)
        {
            try
            {
                AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(Sanitize(settings), SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never fail the tool over them.
            }
        }

        public static MetaCleanSettings Sanitize(MetaCleanSettings s)
        {
            s.Suffix = string.Concat((s.Suffix ?? "").Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0));
            return s;
        }
    }

    /// <summary>
    /// Removes the hidden-details segments (Exif, XMP, IPTC, comments, thumbnails) from a JPG without touching the picture
    /// data. Kept: the basic header, the color profile (ICC) and Adobe's color-transform marker, which a viewer needs to
    /// show the right colors.
    /// </summary>
    public static class JpegStripper
    {
        public static byte[] Strip(byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) throw new InvalidDataException("This is not a JPG file.");

            using var output = new MemoryStream(data.Length);
            output.Write(data, 0, 2);   // SOI
            int i = 2;
            while (i + 1 < data.Length)
            {
                if (data[i] != 0xFF) throw new InvalidDataException("The JPG is damaged.");
                byte marker = data[i + 1];
                if (marker == 0xFF) { i++; continue; }   // padding

                // Markers without a length
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
                {
                    output.Write(data, i, 2);
                    i += 2;
                    continue;
                }
                if (marker == 0xD9)   // EOI
                {
                    output.Write(data, i, 2);
                    i += 2;
                    break;
                }

                if (i + 3 >= data.Length) throw new InvalidDataException("The JPG is damaged.");
                int length = (data[i + 2] << 8) | data[i + 3];
                int end = i + 2 + length;
                if (length < 2 || end > data.Length) throw new InvalidDataException("The JPG is damaged.");

                if (marker == 0xDA)   // start of scan: the rest is picture data, copied as it is
                {
                    output.Write(data, i, data.Length - i);
                    return output.ToArray();
                }

                if (Keep(marker, data, i + 4, end))
                    output.Write(data, i, end - i);
                i = end;
            }
            if (i < data.Length) output.Write(data, i, data.Length - i);
            return output.ToArray();
        }

        private static bool Keep(byte marker, byte[] data, int payloadStart, int end)
        {
            if (marker == 0xFE) return false;                    // comment
            if (marker < 0xE0 || marker > 0xEF) return true;     // not an application segment (tables, frame header, ...)
            if (marker == 0xE0) return true;                     // JFIF header
            if (marker == 0xEE) return true;                     // Adobe color transform
            if (marker == 0xE2)                                  // ICC color profile only
            {
                const string icc = "ICC_PROFILE";
                if (end - payloadStart < icc.Length) return false;
                for (int k = 0; k < icc.Length; k++)
                    if (data[payloadStart + k] != icc[k]) return false;
                return true;
            }
            return false;   // Exif and XMP (E1), Photoshop/IPTC (ED), everything else
        }
    }
}
