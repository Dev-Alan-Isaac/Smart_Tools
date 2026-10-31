using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using WinBitmapAlphaMode = global::Windows.Graphics.Imaging.BitmapAlphaMode;
using WinBitmapPixelFormat = global::Windows.Graphics.Imaging.BitmapPixelFormat;
using WinLanguage = global::Windows.Globalization.Language;
using WinOcrEngine = global::Windows.Media.Ocr.OcrEngine;
using WinSoftwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Windows-only half: screen capture (GDI) and the Windows OCR engine.</summary>
    public static partial class ScreenOcrManager
    {
        // =================================================================
        //  Languages
        // =================================================================

        /// <summary>The OCR languages installed in Windows (an OCR language comes with its language pack).</summary>
        public static List<ScreenOcrLanguage> AvailableLanguages()
        {
            try
            {
                return WinOcrEngine.AvailableRecognizerLanguages
                    .Select(l => new ScreenOcrLanguage(l.LanguageTag, l.DisplayName))
                    .OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeLoadException)
            {
                return new List<ScreenOcrLanguage>();   // very old Windows, or the OCR component removed
            }
        }

        /// <summary>The language "Same as Windows" resolves to right now, or null when Windows has none for OCR.</summary>
        public static ScreenOcrLanguage? ProfileLanguage()
        {
            try
            {
                var engine = WinOcrEngine.TryCreateFromUserProfileLanguages();
                return engine == null ? null : new ScreenOcrLanguage(engine.RecognizerLanguage.LanguageTag, engine.RecognizerLanguage.DisplayName);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeLoadException)
            {
                return null;
            }
        }

        // =================================================================
        //  Screen
        // =================================================================

        /// <summary>
        /// A picture of every monitor at once (the virtual screen), in the same
        /// pixels the selection overlay shows. The overlay is drawn over this
        /// frozen picture, so what the user selects is exactly what gets read -
        /// even text that changed or a menu that closed meanwhile.
        /// </summary>
        public static ScreenOcrImage CaptureVirtualScreen()
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            var image = ToImage(bitmap);
            FlattenOnWhite(image);   // GDI leaves alpha at 0 on some drivers
            return image;
        }

        // =================================================================
        //  Reading
        // =================================================================

        /// <summary>
        /// Reads the text in an image. <paramref name="languageTag"/> empty = the
        /// languages Windows is set to. Throws <see cref="InvalidOperationException"/>
        /// with a plain reason when no engine can be made.
        /// </summary>
        /// <remarks>
        /// With <paramref name="enhance"/> a poor reading is retried on a cleaned-up copy of the picture, then
        /// on a denoised, bigger one; the best reading wins. <see cref="AutoLanguageTag"/> tries every installed
        /// language (after the first pass only the two most promising ones are kept).
        /// </remarks>
        public static async Task<ScreenOcrResult> RecognizeAsync(ScreenOcrImage image, string languageTag, CancellationToken token, bool enhance = true,
                                                                 ScreenOcrEngineKind engineKind = ScreenOcrEngineKind.Windows)
        {
            var watch = Stopwatch.StartNew();
            if (engineKind == ScreenOcrEngineKind.Tesseract)
                return await RecognizeTesseractAsync(image, languageTag, token, enhance, watch).ConfigureAwait(false);

            var engines = string.Equals(languageTag, AutoLanguageTag, StringComparison.OrdinalIgnoreCase)
                ? CreateAllEngines()
                : new List<WinOcrEngine> { CreateEngine(languageTag) };

            int max = (int)Math.Min(WinOcrEngine.MaxImageDimension, int.MaxValue);
            Reading? best = null;
            double bestScore = double.NegativeInfinity;

            for (int variant = 0; variant <= (enhance ? 2 : 0); variant++)
            {
                token.ThrowIfCancellationRequested();
                int v = variant;
                var (input, scale) = await Task.Run(() => Prepare(image, v, max), token).ConfigureAwait(false);

                var scores = new List<(WinOcrEngine Engine, double Score)>();
                bool good = false;
                foreach (var engine in engines)
                {
                    token.ThrowIfCancellationRequested();
                    var reading = await ReadOnce(engine, input, scale, token).ConfigureAwait(false);
                    double score = ReadScore(reading.Lines, out bool isGood);
                    scores.Add((engine, score));
                    if (best == null || score > bestScore)
                    {
                        best = reading;
                        bestScore = score;
                        good = isGood;
                    }
                }

                if (good) break;
                if (engines.Count > 2)
                    engines = scores.OrderByDescending(s => s.Score).Take(2).Select(s => s.Engine).ToList();
            }

            return ToResult(best!, watch.Elapsed);
        }

        private sealed record Reading(List<string> Lines, List<ScreenOcrWord> Words, string LanguageName, double Scale);

        private static ScreenOcrResult ToResult(Reading reading, TimeSpan elapsed) => new()
        {
            Text = string.Join(Environment.NewLine, reading.Lines),
            Lines = reading.Lines,
            Words = reading.Words,
            LanguageName = reading.LanguageName,
            Scale = reading.Scale,
            Elapsed = elapsed,
        };

        /// <summary>The picture for one attempt: 0 as is, 1 cleaned up, 2 denoised, cleaned up, and read bigger.</summary>
        private static (ScreenOcrImage Image, double Scale) Prepare(ScreenOcrImage image, int variant, int max)
        {
            double scale = ReadScale(image.Width, image.Height, max, larger: variant == 2);
            var scaled = Math.Abs(scale - 1) < 0.001 ? image : Resize(image, scale);
            return variant switch
            {
                1 => (Enhance(scaled), scale),
                2 => (Enhance(Denoise(scaled)), scale),
                _ => (scaled, scale),
            };
        }

        private static async Task<Reading> ReadOnce(WinOcrEngine engine, ScreenOcrImage input, double scale, CancellationToken token)
        {
            using var bitmap = WinSoftwareBitmap.CreateCopyFromBuffer(
                input.Pixels.AsBuffer(), WinBitmapPixelFormat.Bgra8, input.Width, input.Height, WinBitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);

            var lines = result.Lines.Select(l => l.Text).ToList();
            var words = result.Lines
                .SelectMany(l => l.Words)
                .Select(w => new ScreenOcrWord(w.Text, w.BoundingRect.X / scale, w.BoundingRect.Y / scale, w.BoundingRect.Width / scale, w.BoundingRect.Height / scale))
                .ToList();
            return new Reading(lines, words, engine.RecognizerLanguage.DisplayName, scale);
        }

        /// <summary>One engine per installed OCR language, the one Windows is set to first.</summary>
        private static List<WinOcrEngine> CreateAllEngines()
        {
            string? preferred = ProfileLanguage()?.Tag;
            var engines = AvailableLanguages()
                .OrderByDescending(l => string.Equals(l.Tag, preferred, StringComparison.OrdinalIgnoreCase))
                .Select(l => WinOcrEngine.TryCreateFromLanguage(new WinLanguage(l.Tag)))
                .OfType<WinOcrEngine>()
                .ToList();
            if (engines.Count == 0)
                throw new InvalidOperationException("Windows has no OCR language installed. Add one in Settings › Time & language › Language & region (add a language, then make sure \"Optical character recognition\" is included).");
            return engines;
        }

        private static WinOcrEngine CreateEngine(string languageTag)
        {
            WinOcrEngine? engine;
            if (string.IsNullOrWhiteSpace(languageTag))
            {
                engine = WinOcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null)
                    throw new InvalidOperationException("Windows has no OCR language installed for your display language. Add one in Settings › Time & language › Language & region (add a language, then make sure \"Optical character recognition\" is included), or pick an installed language here.");
                return engine;
            }

            var language = new WinLanguage(languageTag);
            if (!WinOcrEngine.IsLanguageSupported(language))
                throw new InvalidOperationException($"OCR for {language.DisplayName} isn't installed. Add it in Settings › Time & language › Language & region, or pick another language.");
            engine = WinOcrEngine.TryCreateFromLanguage(language);
            return engine ?? throw new InvalidOperationException($"Windows couldn't start OCR for {language.DisplayName}.");
        }

        // =================================================================
        //  GDI helpers
        // =================================================================

        private static ScreenOcrImage Resize(ScreenOcrImage image, double scale)
        {
            int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
            using var source = ToBitmap(image);
            using var target = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(target))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                using var wrap = new ImageAttributes();
                wrap.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe along the edges
                g.DrawImage(source, new Rectangle(0, 0, w, h), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrap);
            }
            return ToImage(target);
        }

        private static Bitmap ToBitmap(ScreenOcrImage image)
        {
            var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < image.Height; row++)
                    Marshal.Copy(image.Pixels, row * image.Stride, data.Scan0 + row * data.Stride, image.Stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        private static ScreenOcrImage ToImage(Bitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            var pixels = new byte[w * h * 4];
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < h; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * w * 4, w * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return new ScreenOcrImage(pixels, w, h);
        }
    }
}
