using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Image metadata in words, from the plain values an
    /// <see cref="IImageMetadataReader"/> returns. No Magick.NET here, so the
    /// wording is tested with made-up EXIF (GPS with 0/0 fractions, binary
    /// MakerNotes, NUL-padded text, duplicate tags…).
    /// </summary>
    public static partial class MetadataExtractManager
    {
        /// <summary>EXIF tags shown with friendly names; the rest go in the "EXIF" section as they are.</summary>
        private static readonly HashSet<string> CuratedExif = new(StringComparer.OrdinalIgnoreCase)
        {
            "Make", "Model", "LensModel", "LensMake", "ExposureTime", "FNumber", "ISOSpeedRatings", "PhotographicSensitivity",
            "FocalLength", "FocalLengthIn35mmFilm", "Flash", "DateTimeOriginal", "DateTimeDigitized", "DateTime", "Software",
            "Artist", "Copyright", "ImageDescription", "GPSLatitude", "GPSLatitudeRef", "GPSLongitude", "GPSLongitudeRef",
            "GPSAltitude", "GPSAltitudeRef", "Orientation", "XResolution", "YResolution", "ResolutionUnit", "ExposureBiasValue",
            "ExposureProgram", "WhiteBalance", "MeteringMode", "PixelXDimension", "PixelYDimension",
        };

        public static MetadataReadResult BuildImageFields(ImageMetadataRaw raw, string fullPath, long size, DateTime modified)
        {
            var rows = new List<MetadataField>();
            void Add(string group, string property, string? value)
            {
                string v = Clean(value);
                if (v.Length > 0) rows.Add(new MetadataField(group, property, v));
            }

            var exif = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, value) in raw.Exif) exif.TryAdd(tag, value);   // first wins (IFD0 before the thumbnail's IFD1)
            object? Tag(string name) => exif.TryGetValue(name, out var v) ? v : null;

            // ---- General ----
            AddGeneral(rows, fullPath, size, modified, "Image");
            Add("General", "Format", raw.Format.ToUpperInvariant());
            Add("General", "Edited with", ExifText(Tag("Software")));

            // ---- Image ----
            if (raw.Width > 0 && raw.Height > 0)
            {
                double mp = raw.Width * (double)raw.Height / 1_000_000;
                Add("Image", "Dimensions", $"{raw.Width} × {raw.Height}" + (mp >= 0.1 ? $" ({mp:0.#} MP)" : ""));
                Add("Image", "Aspect ratio", AspectRatio(raw.Width, raw.Height));
            }
            if (raw.Depth > 0) Add("Image", "Colour depth", $"{raw.Depth}-bit per channel");
            if (raw.DensityX > 0 && raw.DensityY > 0)
                Add("Image", "Resolution", $"{raw.DensityX:0.#} × {raw.DensityY:0.#} {(raw.DensityUnit.Length > 0 ? raw.DensityUnit : "DPI")}");
            if (raw.Compression.Length > 0 && !raw.Compression.Equals("Undefined", StringComparison.OrdinalIgnoreCase)) Add("Image", "Compression", raw.Compression);
            int orientation = raw.Orientation > 0 ? raw.Orientation : (int)(AsDouble(Tag("Orientation")) ?? 0);
            Add("Image", "Orientation", OrientationName(orientation));
            if (raw.ColorSpace.Length > 0 && !raw.ColorSpace.Equals("Undefined", StringComparison.OrdinalIgnoreCase)) Add("Image", "Colour space", raw.ColorSpace);
            Add("Image", "Colour profile (ICC)", raw.IccProfile);

            // ---- Camera ----
            string make = ExifText(Tag("Make")), model = ExifText(Tag("Model"));
            Add("Camera", "Make", make);
            Add("Camera", "Model", model);
            string lens = ExifText(Tag("LensModel"));
            Add("Camera", "Lens", lens);
            if (AsDouble(Tag("ExposureTime")) is { } exposure && exposure > 0) Add("Camera", "Shutter speed", ExposureTime(exposure));
            if (AsDouble(Tag("FNumber")) is { } fNumber && fNumber > 0) Add("Camera", "Aperture", string.Create(CultureInfo.InvariantCulture, $"f/{fNumber:0.#}"));
            string iso = ExifText(Tag("ISOSpeedRatings"));
            if (iso.Length == 0) iso = ExifText(Tag("PhotographicSensitivity"));
            Add("Camera", "ISO", iso);
            if (AsDouble(Tag("FocalLength")) is { } focal && focal > 0)
            {
                double? eq35 = AsDouble(Tag("FocalLengthIn35mmFilm"));
                Add("Camera", "Focal length", $"{focal:0.#} mm" + (eq35 is > 0 && Math.Abs(eq35.Value - focal) > 0.5 ? $" ({eq35:0} mm in 35 mm terms)" : ""));
            }
            if (AsDouble(Tag("ExposureBiasValue")) is { } bias && bias != 0) Add("Camera", "Exposure compensation", $"{bias:+0.#;-0.#} EV");
            if (AsDouble(Tag("Flash")) is { } flash) Add("Camera", "Flash", ((int)flash & 1) == 1 ? "Fired" : "Didn't fire");
            Add("Camera", "Taken", ExifDate(ExifText(Tag("DateTimeOriginal"))));
            Add("Camera", "Artist", ExifText(Tag("Artist")));
            Add("Camera", "Copyright", ExifText(Tag("Copyright")));
            Add("Camera", "Description", ExifText(Tag("ImageDescription")));

            // ---- Location ----
            double? lat = DegreesFromDms(Tag("GPSLatitude")), lon = DegreesFromDms(Tag("GPSLongitude"));
            if (lat is { } la && lon is { } lo)
            {
                if (ExifText(Tag("GPSLatitudeRef")).StartsWith("S", StringComparison.OrdinalIgnoreCase)) la = -la;
                if (ExifText(Tag("GPSLongitudeRef")).StartsWith("W", StringComparison.OrdinalIgnoreCase)) lo = -lo;
                if (ValidLatLon(la, lo))
                {
                    lat = la;
                    lon = lo;
                    Add("Location", "GPS coordinates", FormatLatLon(la, lo));
                    if (AsDouble(Tag("GPSAltitude")) is { } alt && double.IsFinite(alt))
                        Add("Location", "Altitude", Altitude((AsDouble(Tag("GPSAltitudeRef")) ?? 0) == 1 ? -alt : alt));
                }
                else lat = lon = null;
            }
            else lat = lon = null;

            // ---- Everything else the file carries ----
            foreach (var (tag, value) in raw.Exif)
            {
                if (CuratedExif.Contains(tag)) continue;
                Add("EXIF", tag, ExifText(value));
            }

            var summary = new List<string>();
            if (raw.Width > 0 && raw.Height > 0) summary.Add($"{raw.Width} × {raw.Height}");
            string camera = model.Length == 0 ? make : make.Length == 0 || model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
            if (camera.Length > 0) summary.Add(camera);
            else if (raw.Format.Length > 0) summary.Add(raw.Format.ToUpperInvariant());

            return new MetadataReadResult(Dedupe(rows), string.Join(" · ", summary), lat, lon);
        }

        /// <summary>A raw EXIF value as text: fractions as numbers, arrays joined, binary summarised (the old dump printed every byte).</summary>
        public static string ExifText(object? value)
        {
            switch (value)
            {
                case null:
                    return "";
                case string s:
                    return Clean(s);
                case MetadataRational r:
                    return double.IsFinite(r.Value) ? r.Value.ToString("0.####", CultureInfo.InvariantCulture) : "";
                case byte[] bytes:
                    return BytesText(bytes);
                case Array array:
                    var parts = array.Cast<object?>().Select(ExifText).Where(p => p.Length > 0).ToList();
                    if (parts.Count == 0) return "";
                    return parts.Count > 16 ? string.Join(", ", parts.Take(16)) + $", … ({parts.Count} values)" : string.Join(", ", parts);
                case IFormattable f:
                    return f.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return Clean(value.ToString());
            }
        }

        /// <summary>Bytes that are really text (ExifVersion "0232") as text; anything else as "binary data, N bytes".</summary>
        public static string BytesText(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            int end = bytes.Length;
            while (end > 0 && bytes[end - 1] == 0) end--;
            bool printable = end > 0 && end <= 256 && bytes.Take(end).All(b => b >= 0x20 && b < 0x7F);
            return printable ? Encoding.ASCII.GetString(bytes, 0, end).Trim() : $"Binary data, {bytes.Length:N0} bytes";
        }

        public static double? AsDouble(object? value) => value switch
        {
            null => null,
            MetadataRational r => double.IsFinite(r.Value) ? r.Value : null,
            string s => double.TryParse(s.Trim('\0', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null,
            Array { Length: > 0 } a => AsDouble(a.GetValue(0)),
            IConvertible c when value is not bool => SafeConvert(c),
            _ => null,
        };

        private static double? SafeConvert(IConvertible c)
        {
            try
            {
                double d = c.ToDouble(CultureInfo.InvariantCulture);
                return double.IsFinite(d) ? d : null;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }

        /// <summary>Degrees from EXIF's degrees/minutes/seconds fractions. Null when missing or when a 0/0 fraction makes it meaningless (the old code showed "NaN°").</summary>
        public static double? DegreesFromDms(object? value)
        {
            if (value is not Array { Length: >= 1 } parts) return AsDouble(value);
            double total = 0;
            double[] scale = { 1, 60, 3600 };
            for (int i = 0; i < Math.Min(3, parts.Length); i++)
            {
                var part = parts.GetValue(i);
                double? v = part is MetadataRational r ? r.Value : AsDouble(part);
                if (v is not { } n || !double.IsFinite(n)) return i == 0 ? null : total;   // a missing seconds part still leaves a usable position
                total += n / scale[i];
            }
            return total;
        }

        public static string ExposureTime(double seconds) =>
            seconds >= 1 || seconds <= 0 ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#} s") : $"1/{Math.Round(1 / seconds):0} s";

        /// <summary>EXIF orientation 1-8 in words (6 is how most phones store portrait photos).</summary>
        public static string OrientationName(int orientation) => orientation switch
        {
            1 => "Normal",
            2 => "Mirrored",
            3 => "Upside down (turned 180°)",
            4 => "Upside down, mirrored",
            5 => "Turned 90° anticlockwise, mirrored",
            6 => "Turned 90° clockwise (usual for portrait phone photos)",
            7 => "Turned 90° clockwise, mirrored",
            8 => "Turned 90° anticlockwise",
            _ => "",
        };
    }
}
