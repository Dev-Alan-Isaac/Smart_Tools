using ImageMagick;
using ImageMagick.Formats;
using System.IO;

namespace Smart_Tools.WPF
{
    /// <summary>
    /// The one way tools open a picture they are going to process in bulk: read the header first (a few KB),
    /// ask <see cref="MemoryBudget"/> for what the decode will really cost, then decode. Disposing the
    /// result frees the pixels AND gives the bytes back. For JPEG a reduced size is decoded straight from
    /// the file (libjpeg scales by 1/2, 1/4, 1/8 while reading), so a 40 MP photo costs a fraction.
    /// </summary>
    public sealed class LoadedImage : IDisposable
    {
        public MagickImage Image { get; }
        /// <summary>Width and height in the file, before any reduction.</summary>
        public uint SourceWidth { get; }
        public uint SourceHeight { get; }
        private MemoryBudget.Lease _lease;

        internal LoadedImage(MagickImage image, uint sw, uint sh, MemoryBudget.Lease lease)
        {
            Image = image; SourceWidth = sw; SourceHeight = sh; _lease = lease;
        }

        public void Dispose()
        {
            Image.Dispose();
            _lease.Dispose();
            _lease = default;
        }
    }

    public static class ImageLoad
    {
        // Q16 keeps 4 channels x 2 bytes per pixel; working copies (resize, rotate) add roughly half again.
        private const int BytesPerPixel = 12;

        /// <summary>Tells ImageMagick to use disk instead of RAM past the budget. Call once at startup.</summary>
        public static void ConfigureLimits()
        {
            try
            {
                ResourceLimits.Memory = (ulong)MemoryBudget.Total;
            }
            catch (Exception ex) when (ex is MagickException or InvalidOperationException)
            {
                Log.Warn("Could not set the ImageMagick resource limits: " + ex.Message);
            }
        }

        public static long EstimateBytes(ulong width, ulong height)
            => (long)Math.Min(long.MaxValue / 2.0, (double)width * height * BytesPerPixel);

        /// <summary>
        /// Opens a picture under the byte budget. <paramref name="maxSide"/> (0 = full size) is the longest side
        /// the caller needs: JPEGs are decoded already reduced; other formats are decoded whole (the budget
        /// still guards them) and the caller resizes. Throws what Magick throws for unreadable files.
        /// </summary>
        public static LoadedImage Open(string path, CancellationToken ct, uint maxSide = 0)
        {
            ct.ThrowIfCancellationRequested();

            uint w = 0, h = 0;
            try
            {
                var info = new MagickImageInfo(path);
                w = info.Width; h = info.Height;
            }
            catch (MagickException) { /* unknown header: estimate from the file size below */ }

            bool jpeg = IsJpeg(path);
            long estimate;
            var settings = new MagickReadSettings();
            if (w > 0 && h > 0)
            {
                double scale = 1;
                if (jpeg && maxSide > 0 && Math.Max(w, h) > maxSide)
                {
                    // libjpeg only scales by 1/2, 1/4, 1/8: it lands between maxSide and 2 x maxSide.
                    scale = (double)maxSide / Math.Max(w, h);
                    settings.SetDefines(new JpegReadDefines { Size = new MagickGeometry(maxSide, maxSide) });
                }
                double f = Math.Min(1.0, scale * 2);
                estimate = EstimateBytes((ulong)Math.Ceiling(w * f), (ulong)Math.Ceiling(h * f));
            }
            else
            {
                long len = 0;
                try { len = new FileInfo(path).Length; } catch (IOException) { }
                estimate = Math.Max(16L << 20, len * 10);
            }

            var lease = MemoryBudget.Acquire(estimate, ct);
            try
            {
                var image = new MagickImage(path, settings);
                return new LoadedImage(image, w, h, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private static bool IsJpeg(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpe", StringComparison.OrdinalIgnoreCase);
        }
    }
}
