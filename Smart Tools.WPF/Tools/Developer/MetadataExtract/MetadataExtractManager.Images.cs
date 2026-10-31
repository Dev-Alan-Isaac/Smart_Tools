using ImageMagick;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Reads an image's header and EXIF with Magick.NET (Ping - the pixels
    /// are never decoded) and hands back plain values; the wording is built in
    /// MetadataExtractManager.ImageFields.cs. Kept tiny so everything else is
    /// tested without Magick.NET.
    /// </summary>
    public sealed class MagickImageMetadataReader : IImageMetadataReader
    {
        public ImageMetadataRaw Read(string path)
        {
            using var image = new MagickImage();
            image.Ping(path);

            double dx = 0, dy = 0;
            string unit = "";
            try
            {
                var density = image.Density;
                dx = density.X;
                dy = density.Y;
                unit = density.Units switch
                {
                    DensityUnit.PixelsPerCentimeter => "px/cm",
                    DensityUnit.PixelsPerInch => "DPI",
                    _ => "",
                };
            }
            catch (MagickException)
            {
                // not every format has a density
            }

            string icc = "";
            try
            {
                var profile = image.GetColorProfile();
                if (profile != null) icc = string.IsNullOrWhiteSpace(profile.Description) ? profile.ColorSpace.ToString() : profile.Description!;
            }
            catch (MagickException)
            {
            }

            var exif = new List<(string, object?)>();
            var exifProfile = image.GetExifProfile();
            if (exifProfile != null)
            {
                foreach (var value in exifProfile.Values)
                {
                    object? raw;
                    try { raw = value.GetValue(); }
                    catch (Exception ex) when (ex is MagickException or InvalidCastException or ArgumentException) { continue; }
                    exif.Add((value.Tag.ToString(), Plain(raw)));
                }
            }

            return new ImageMetadataRaw
            {
                Format = image.Format.ToString(),
                Width = (int)image.Width,
                Height = (int)image.Height,
                Depth = (int)image.Depth,
                DensityX = dx,
                DensityY = dy,
                DensityUnit = unit,
                Compression = image.Compression.ToString(),
                Orientation = (int)image.Orientation,
                ColorSpace = image.ColorSpace.ToString(),
                IccProfile = icc,
                Exif = exif,
            };
        }

        /// <summary>Magick.NET's EXIF types as plain values (fractions as <see cref="MetadataRational"/>; anything else as text).</summary>
        private static object? Plain(object? value) => value switch
        {
            null => null,
            string s => s,
            byte[] b => b,
            Rational r => new MetadataRational(r.Numerator, r.Denominator),
            SignedRational r => new MetadataRational(r.Numerator, r.Denominator),
            Rational[] a => a.Select(r => new MetadataRational(r.Numerator, r.Denominator)).ToArray(),
            SignedRational[] a => a.Select(r => new MetadataRational(r.Numerator, r.Denominator)).ToArray(),
            ushort or short or uint or int or ulong or long or float or double or byte or sbyte => value,
            ushort[] or short[] or uint[] or int[] or ulong[] or long[] or float[] or double[] => value,
            Array a => a.Cast<object?>().Select(o => o?.ToString() ?? "").ToArray(),
            _ => value.ToString(),
        };
    }
}
