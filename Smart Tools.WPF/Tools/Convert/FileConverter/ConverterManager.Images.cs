using ImageMagick;
using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Image conversion with Magick.NET, following <see cref="ImagePlan"/>.</summary>
    public sealed partial class ConverterManager
    {
        private static MagickFormat MagickFormatFor(ConvertImageFormat format) => format switch
        {
            ConvertImageFormat.Png => MagickFormat.Png,
            ConvertImageFormat.Webp => MagickFormat.WebP,
            ConvertImageFormat.Avif => MagickFormat.Avif,
            ConvertImageFormat.Bmp => MagickFormat.Bmp,
            ConvertImageFormat.Tiff => MagickFormat.Tiff,
            ConvertImageFormat.Gif => MagickFormat.Gif,
            ConvertImageFormat.Ico => MagickFormat.Ico,
            _ => MagickFormat.Jpg,
        };

        /// <summary>
        /// Longest side each format can store, or 0 for no limit. A big SVG (20000 px, or sized in
        /// mm) saved fine as PNG but failed as WebP: "width or height exceeds limit".
        /// </summary>
        public static int MaxSideFor(ConvertImageFormat format) => format switch
        {
            ConvertImageFormat.Webp => 16383,
            ConvertImageFormat.Avif => 16384,
            ConvertImageFormat.Jpg => 65500,
            ConvertImageFormat.Gif => 65535,
            ConvertImageFormat.Ico => 256,
            _ => 0,
        };

        private static void ConvertImageCore(string input, string output, ImagePlan plan, CancellationToken token)
        {
            try
            {
                ConvertImageCoreInner(input, output, plan, token);
            }
            catch (MagickException ex)
            {
                // The raw Magick text ("...@ error/webp.c/WriteWEBPImage/1160") says nothing to the user.
                throw new InvalidOperationException($"Couldn't save {Path.GetFileName(input)} as {plan.Format.ToString().ToUpperInvariant()}: {ex.Message.Split('\n')[0]}", ex);
            }
        }

        private static void ConvertImageCoreInner(string input, string output, ImagePlan plan, CancellationToken token)
        {
            var format = MagickFormatFor(plan.Format);

            // GIF/WEBP -> GIF/WEBP keeps every frame (the old version kept only the first).
            // An SVG is one picture: skip reading it twice.
            if (plan.KeepAnimation && !string.Equals(Path.GetExtension(input), ".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var frames = new MagickImageCollection(input);
                if (frames.Count > 1)
                {
                    frames.Coalesce();   // full frames, so resizing doesn't smear partial ones
                    foreach (var frame in frames)
                    {
                        token.ThrowIfCancellationRequested();
                        Prepare(frame, plan);
                    }
                    if (plan.Format == ConvertImageFormat.Gif) frames.OptimizeTransparency();
                    frames.Write(output, format);
                    return;
                }
            }

            token.ThrowIfCancellationRequested();
            using var image = new MagickImage(input);
            Prepare(image, plan);
            image.Write(output, format);
        }

        /// <summary>
        /// Not generic: MagickColors.White is the package's own colour type
        /// (Q16 = ushort), which a generic IMagickImage&lt;T&gt; won't accept.
        /// Using Magick.NET-Q8? Change ushort to byte.
        /// </summary>
        private static void Prepare(IMagickImage<ushort> image, ImagePlan plan)
        {
            // Phone photos store "rotate me" in EXIF; bake it in so the
            // result isn't sideways in apps that ignore the tag.
            image.AutoOrient();

            // Only ever shrink, and keep the aspect ratio.
            double scale = 1;
            if (plan.MaxWidth > 0 && image.Width > plan.MaxWidth) scale = Math.Min(scale, (double)plan.MaxWidth / image.Width);
            if (plan.MaxHeight > 0 && image.Height > plan.MaxHeight) scale = Math.Min(scale, (double)plan.MaxHeight / image.Height);

            // Whatever the user asked for, never go past what the output format can hold.
            int formatMax = MaxSideFor(plan.Format);
            if (formatMax > 0)
            {
                double longest = Math.Max(image.Width, image.Height) * scale;
                if (longest > formatMax) scale *= formatMax / longest;
            }
            if (scale < 1)
            {
                uint w = (uint)Math.Max(1, Math.Round(image.Width * scale));
                uint h = (uint)Math.Max(1, Math.Round(image.Height * scale));
                image.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
            }

            // JPG/BMP have no transparency - white, not black, behind transparent areas.
            if (plan.FlattenOnWhite && image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }

            if (plan.Quality is int q) image.Quality = (uint)q;
        }
    }
}