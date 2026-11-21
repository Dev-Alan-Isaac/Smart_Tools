using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controllers
{
    // The WPF half of the watermarker: drawing the mark as a transparent picture. Every engine (pictures, PDFs, videos)
    // lays this same picture over the file, so the preview, the pictures, the pages and the videos all look alike.
    public static partial class WatermarkManager
    {
        /// <summary>The mark, drawn once at the size of the area it covers. <see cref="X"/> and <see cref="Y"/> say where that area starts in the file.</summary>
        public sealed record Stamp(BitmapSource Bitmap, int X, int Y);

        /// <summary>A file bigger than this (in pixels) is refused rather than risk running out of memory.</summary>
        public const long MaxStampPixels = 150_000_000;

        /// <summary>Every installed font, by name, for the font list.</summary>
        public static List<string> FontNames() =>
            Fonts.SystemFontFamilies
                .Select(f => f.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag), out string? local) ? local : f.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>The app's copy of a logo as a frozen picture. Null when it is missing or not a picture.</summary>
        public static BitmapSource? LoadLogo(string? logoFile)
        {
            if (string.IsNullOrEmpty(logoFile)) return null;
            try
            {
                using var stream = new FileStream(LogoPath(logoFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                          or FileFormatException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>True when the file opens as a picture (checked before a logo is accepted).</summary>
        public static bool IsPicture(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                          or FileFormatException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Draws the mark for a file of the given size: one copy on its spot, or the tiled pattern, turned and at the chosen opacity,
        /// cropped to the area the copies cover. Null when there is nothing to draw (no text, no logo, or the mark misses the file).
        /// Needs an STA thread (the UI thread, or <see cref="RunOnSta{T}"/>).
        /// </summary>
        public static Stamp? RenderStamp(int targetWidth, int targetHeight, WatermarkSettings settings, BitmapSource? logo)
        {
            // Several picture workers run at once; WPF's drawing is not safe from many threads at the same moment, and a stamp takes
            // milliseconds, so the drawing takes turns while the heavy work (decoding, compositing, saving) stays parallel.
            lock (WpfDrawLock) return RenderStampCore(targetWidth, targetHeight, settings, logo);
        }

        private static readonly object WpfDrawLock = new();

        private static Stamp? RenderStampCore(int targetWidth, int targetHeight, WatermarkSettings settings, BitmapSource? logo)
        {
            var s = Normalize(settings);
            if (targetWidth <= 0 || targetHeight <= 0) return null;
            if ((long)targetWidth * targetHeight > MaxStampPixels) throw new InvalidOperationException("This file is too large to mark.");

            Drawing? mark;
            double mw, mh;
            if (s.Kind == WatermarkKind.Text)
            {
                if (string.IsNullOrWhiteSpace(s.Text)) return null;
                (mark, mw, mh) = TextMark(s, WatermarkManager.TextPixelSize(targetWidth, targetHeight, s));
            }
            else
            {
                if (logo == null || logo.PixelWidth <= 0) return null;
                mw = ImagePixelWidth(targetWidth, s);
                mh = Math.Max(1, mw * logo.PixelHeight / logo.PixelWidth);
                var group = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
                using (var dc = group.Open()) dc.DrawImage(logo, new Rect(0, 0, mw, mh));
                group.Freeze();
                mark = group;
            }

            var copies = Layout(targetWidth, targetHeight, mw, mh, s);
            var area = CoveredArea(targetWidth, targetHeight, mw, mh, copies, s.Rotation);
            if (area.Width <= 0 || area.Height <= 0) return null;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushOpacity(s.OpacityPercent / 100.0);
                foreach (var c in copies)
                {
                    dc.PushTransform(new TranslateTransform(c.CenterX - area.X, c.CenterY - area.Y));
                    dc.PushTransform(new RotateTransform(s.Rotation));
                    dc.PushTransform(new TranslateTransform(-mw / 2, -mh / 2));
                    dc.DrawDrawing(mark);
                    dc.Pop();
                    dc.Pop();
                    dc.Pop();
                }
                dc.Pop();
            }

            var bitmap = new RenderTargetBitmap(area.Width, area.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return new Stamp(bitmap, area.X, area.Y);
        }

        /// <summary>The text as a drawing: its size in pixels, with room around it for the outline.</summary>
        private static (Drawing Mark, double Width, double Height) TextMark(WatermarkSettings s, double emSize)
        {
            TryParseColor(s.Color, out byte r, out byte g, out byte b);
            var fill = new SolidColorBrush(Color.FromRgb(r, g, b));
            fill.Freeze();
            var typeface = new Typeface(new FontFamily(s.FontFamily), s.Italic ? FontStyles.Italic : FontStyles.Normal,
                s.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(s.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, emSize, fill, 1.0)
            {
                TextAlignment = TextAlignment.Center,
            };

            double pad = s.Outline ? Math.Max(1, emSize * 0.07) : 0;
            double w = Math.Max(1, text.WidthIncludingTrailingWhitespace) + 2 * pad, h = Math.Max(1, text.Height) + 2 * pad;
            text.MaxTextWidth = Math.Max(1, text.WidthIncludingTrailingWhitespace);

            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                var origin = new Point(pad, pad);
                if (s.Outline)
                {
                    Brush edge = IsLightColor(s.Color) ? Brushes.Black : Brushes.White;
                    var pen = new Pen(edge, pad * 2) { LineJoin = PenLineJoin.Round };
                    pen.Freeze();
                    dc.DrawGeometry(null, pen, text.BuildGeometry(origin));
                }
                dc.DrawText(text, origin);
            }
            group.Freeze();
            return (group, w, h);
        }

        /// <summary>Straight-alpha PNG bytes of a stamp (what ffmpeg, ImageMagick and PDFsharp read).</summary>
        public static byte[] ToPng(BitmapSource bitmap)
        {
            lock (WpfDrawLock)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Runs drawing work on its own STA thread (WPF drawing must not run on a thread-pool thread).</summary>
        public static Task<T> RunOnSta<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (OperationCanceledException oce) { tcs.SetCanceled(oce.CancellationToken); }
                catch (Exception ex) { tcs.SetException(ex); }
                finally
                {
                    // WPF makes a Dispatcher for any thread that touches a WPF object. Left running it keeps this thread's
                    // event and thread handles alive for good (about 8 per file in a batch), so shut it down with the work.
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }
    }
}
