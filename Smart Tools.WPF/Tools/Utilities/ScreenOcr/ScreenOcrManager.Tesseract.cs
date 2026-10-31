using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The optional Tesseract engine: runs the bundled tesseract.exe on a prepared picture.</summary>
    public static partial class ScreenOcrManager
    {
        private static readonly TimeSpan TesseractTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Where the bundled Tesseract lives (Engines\TESSERACT next to the .exe).</summary>
        public static string TesseractFolder { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engines", "TESSERACT");

        public static string TesseractExe { get; } = Path.Combine(TesseractFolder, "tesseract.exe");

        public static bool IsTesseractAvailable => File.Exists(TesseractExe);

        /// <summary>The downloaded (and bundled English) languages, by file name.</summary>
        public static List<ScreenOcrLanguage> TesseractLanguages()
        {
            EnsureTessdata();
            return TesseractLanguageManager.InstalledCodes()
                .Select(code => new ScreenOcrLanguage(code, TesseractData.Find(code)?.Display ?? code))
                .ToList();
        }

        /// <summary>
        /// Makes the data folder usable: the first time, copies the bundled configs and English into
        /// AppPaths.TesseractData (the engine folder itself is never written to).
        /// </summary>
        public static void EnsureTessdata()
        {
            try
            {
                string bundled = Path.Combine(TesseractFolder, "tessdata");
                if (!Directory.Exists(bundled)) return;
                CopyMissing(bundled, AppPaths.TesseractData);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Reading then fails with a clear message from the engine.
            }
        }

        private static void CopyMissing(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                string target = Path.Combine(to, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
            foreach (string dir in Directory.GetDirectories(from))
                CopyMissing(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        /// <summary>
        /// Same retry idea as the Windows engine: the original picture first, then the cleaned-up
        /// variants while the reading is poor. <paramref name="languageCode"/> is a downloaded language, or "auto" for all of them.
        /// </summary>
        private static async Task<ScreenOcrResult> RecognizeTesseractAsync(ScreenOcrImage image, string languageCode, CancellationToken token, bool enhance, Stopwatch watch)
        {
            if (!IsTesseractAvailable)
                throw new InvalidOperationException("Tesseract isn't in this copy of Smart Tools. Switch the engine back to Windows OCR in Settings › Text recognition.");

            EnsureTessdata();
            var installed = TesseractLanguageManager.InstalledCodes();
            if (installed.Count == 0)
                throw new InvalidOperationException("No Tesseract language is installed. Install one in Settings › Text recognition.");

            var codes = string.Equals(languageCode, AutoLanguageTag, StringComparison.OrdinalIgnoreCase)
                ? installed
                : new List<string> { installed.Contains(languageCode, StringComparer.OrdinalIgnoreCase) ? languageCode : installed[0] };
            string names = string.Join(", ", codes.Select(TesseractData.NameOf));

            const int max = 12000;   // Tesseract has no tight size limit like Windows OCR; this just bounds memory
            Reading? best = null;
            double bestScore = double.NegativeInfinity;

            for (int variant = 0; variant <= (enhance ? 2 : 0); variant++)
            {
                token.ThrowIfCancellationRequested();
                int v = variant;
                var (input, scale) = await Task.Run(() => Prepare(image, v, max), token).ConfigureAwait(false);

                string tsv = await RunTesseractAsync(input, string.Join('+', codes), token).ConfigureAwait(false);
                var (lines, words) = TesseractData.ParseTsv(tsv, scale);
                var reading = new Reading(lines, words, names, scale);

                double score = ReadScore(lines, out bool good);
                if (best == null || score > bestScore)
                {
                    best = reading;
                    bestScore = score;
                }
                if (good) break;
            }

            return ToResult(best!, watch.Elapsed);
        }

        private static async Task<string> RunTesseractAsync(ScreenOcrImage input, string languages, CancellationToken token)
        {
            string png = Path.Combine(Path.GetTempPath(), $"smarttools-ocr-{Guid.NewGuid():N}.png");
            try
            {
                await File.WriteAllBytesAsync(png, PngWriter.Encode(input.Width, input.Height, ToRgba(input.Pixels)), token).ConfigureAwait(false);

                var psi = new ProcessStartInfo(TesseractExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = TesseractFolder,
                };
                foreach (string arg in new[] { png, "stdout", "--tessdata-dir", AppPaths.TesseractData, "-l", languages, "--oem", "1", "--psm", "3", "tsv" })
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi) ?? throw new InvalidOperationException("Tesseract couldn't be started.");
                var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TesseractTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Tesseract took too long to read this picture. Try a smaller area.");
                }

                string text = await output.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    string message = (await error.ConfigureAwait(false)).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
                    throw new InvalidOperationException($"Tesseract couldn't read this picture. {message}".Trim());
                }
                return text;
            }
            finally
            {
                try { File.Delete(png); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        private static byte[] ToRgba(byte[] bgra)
        {
            var rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = 255;
            }
            return rgba;
        }
    }
}
