using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The WPF side of the Spectrogram Generator: loading pictures for the
    /// preview and thumbnails, and the palette swatches. Kept apart so
    /// SpectrogramManager.cs builds (and is tested) without WPF.
    /// </summary>
    public static class SpectrogramImaging
    {
        /// <summary>
        /// Loads a picture fully into memory and freezes it, so the file isn't
        /// locked (it can be deleted or replaced) and any thread can use it.
        /// <paramref name="decodeWidth"/> &gt; 0 decodes a small copy for
        /// thumbnails - a full picture per row costs gigabytes at 10,000 rows.
        /// </summary>
        public static BitmapSource? Load(string? path, int decodeWidth = 0)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        private static readonly Dictionary<SpectrogramColorScheme, Brush> PaletteCache = new();

        /// <summary>A left-to-right gradient of the palette (quiet → loud), frozen and cached.</summary>
        public static Brush PaletteBrush(SpectrogramColorScheme scheme)
        {
            lock (PaletteCache)
            {
                if (PaletteCache.TryGetValue(scheme, out var cached)) return cached;
                var stops = SpectrogramManager.PaletteStops(scheme);
                var brush = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0.5), EndPoint = new System.Windows.Point(1, 0.5) };
                for (int i = 0; i < stops.Count; i++)
                    brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stops[i]), stops.Count == 1 ? 0 : i / (double)(stops.Count - 1)));
                brush.Freeze();
                PaletteCache[scheme] = brush;
                return brush;
            }
        }
    }
}
