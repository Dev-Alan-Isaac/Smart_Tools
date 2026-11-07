using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Installs and removes Tesseract languages. A language is one .traineddata file (tessdata_fast, a few MB)
    /// in AppPaths.TesseractData. English comes with the app and can't be removed.
    /// </summary>
    public static class TesseractLanguageManager
    {
        private const string BaseUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";
        private const string Extension = ".traineddata";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartTools");
            return client;
        }

        /// <summary>The language codes that have a file in the data folder, sorted ("eng" first).</summary>
        public static List<string> InstalledCodes()
        {
            try
            {
                if (!Directory.Exists(AppPaths.TesseractData)) return new List<string>();
                return Directory.GetFiles(AppPaths.TesseractData, "*" + Extension)
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .Where(code => !code.Equals("osd", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(code => code != "eng").ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        public static bool IsInstalled(string code) => InstalledCodes().Contains(code, StringComparer.OrdinalIgnoreCase);

        /// <summary>Size of an installed language file in bytes, or 0.</summary>
        public static long SizeOf(string code)
        {
            try { return new FileInfo(Path.Combine(AppPaths.TesseractData, code + Extension)) is { Exists: true } f ? f.Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        /// <summary>
        /// Downloads one language. Returns null on success, or a short reason. Cancelling throws
        /// <see cref="OperationCanceledException"/> and leaves nothing behind. <paramref name="progress"/> is 0..1.
        /// </summary>
        public static async Task<string?> InstallAsync(string code, IProgress<double>? progress, CancellationToken token)
        {
            if (TesseractData.Find(code) == null) return Loc.T("Str_Tess_ErrNotListed", "That language isn't in the list.");

            ScreenOcrManager.EnsureTessdata();
            string target = Path.Combine(AppPaths.TesseractData, code + Extension);
            string temp = target + ".tmp";
            try
            {
                Directory.CreateDirectory(AppPaths.TesseractData);
                using var response = await Http.GetAsync(BaseUrl + code + Extension, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return $"The download failed (the server answered {(int)response.StatusCode}).";

                long total = response.Content.Headers.ContentLength ?? 0;
                await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    var lastReport = Stopwatch.StartNew();
                    int read;
                    while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        done += read;
                        if (total > 0 && lastReport.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report((double)done / total);
                            lastReport.Restart();
                        }
                    }
                }

                File.Move(temp, target, overwrite: true);
                progress?.Report(1);

                if (!await ListsLanguageAsync(code, token).ConfigureAwait(false))
                {
                    TryDelete(target);
                    return Loc.T("Str_Tess_ErrUnusable", "The file was downloaded but Tesseract can't use it. Try again.");
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                return ex is HttpRequestException
                    ? Loc.T("Str_Download_ErrServer", "Couldn't reach the download server. Check your internet connection and try again.")
                    : Loc.T("Str_Tess_ErrSave", "Couldn't save the language file. Check that there is free disk space.");
            }
        }

        /// <summary>Deletes a language. English stays. Returns false when the file couldn't be removed.</summary>
        public static bool Remove(string code)
        {
            if (code.Equals("eng", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string file = Path.Combine(AppPaths.TesseractData, code + Extension);
                if (File.Exists(file)) File.Delete(file);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Asks Tesseract itself whether it lists the language (catches a corrupt download).</summary>
        private static async Task<bool> ListsLanguageAsync(string code, CancellationToken token)
        {
            if (!ScreenOcrManager.IsTesseractAvailable) return true;   // nothing to ask; the file is kept
            var psi = new ProcessStartInfo(ScreenOcrManager.TesseractExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = ScreenOcrManager.TesseractFolder,
            };
            foreach (string arg in new[] { "--tessdata-dir", AppPaths.TesseractData, "--list-langs" }) psi.ArgumentList.Add(arg);

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return true;
                var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string text = await output.ConfigureAwait(false);
                return text.Split('\n').Any(l => l.Trim().Equals(code, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                token.ThrowIfCancellationRequested();
                return true;   // couldn't ask - don't throw a good download away
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
