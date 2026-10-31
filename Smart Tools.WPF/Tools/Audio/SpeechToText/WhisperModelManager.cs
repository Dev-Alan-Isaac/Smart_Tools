using Smart_Tools.WPF.Models;
using System.IO;
using System.Net.Http;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Installs and removes Whisper speech models. A model is one ggml-*.bin file in AppPaths.WhisperModels, downloaded
    /// only when the user asks (75 MB to 1.5 GB). The engine itself ships with the app.
    /// </summary>
    public static class WhisperModelManager
    {
        private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartTools");
            return client;
        }

        public static string PathOf(string id) =>
            Path.Combine(AppPaths.WhisperModels, SpeechToTextManager.FindModel(id)?.FileName ?? $"ggml-{id}.bin");

        /// <summary>Smaller than this is a cut-off or error page, not a model.</summary>
        private static long MinimumBytes(SttModelInfo info) => (long)(info.ApproxMb * 1024L * 1024L * 0.7);

        public static long SizeOf(string id)
        {
            try { return new FileInfo(PathOf(id)) is { Exists: true } f ? f.Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        public static bool IsInstalled(string id) =>
            SpeechToTextManager.FindModel(id) is { } info && SizeOf(id) >= MinimumBytes(info);

        public static List<string> InstalledIds() =>
            SpeechToTextManager.Models.Where(m => IsInstalled(m.Id)).Select(m => m.Id).ToList();

        /// <summary>
        /// Downloads one model. Returns null on success, or a short reason. Cancelling throws
        /// <see cref="OperationCanceledException"/> and leaves nothing behind. <paramref name="progress"/> is 0..1.
        /// </summary>
        public static async Task<string?> InstallAsync(string id, IProgress<double>? progress, CancellationToken token)
        {
            if (SpeechToTextManager.FindModel(id) is not { } info) return "That model isn't in the list.";

            string target = PathOf(id);
            string temp = target + ".tmp";
            try
            {
                Directory.CreateDirectory(AppPaths.WhisperModels);
                using var response = await Http.GetAsync(BaseUrl + info.FileName, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return $"The download failed (the server answered {(int)response.StatusCode}).";

                long total = response.Content.Headers.ContentLength ?? 0;
                await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    var lastReport = System.Diagnostics.Stopwatch.StartNew();
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

                if (SizeOfFile(temp) < MinimumBytes(info))
                {
                    TryDelete(temp);
                    return "The download was cut short. Check your connection and try again.";
                }

                File.Move(temp, target, overwrite: true);
                progress?.Report(1);
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
                    ? "Couldn't reach the download server. Check your internet connection and try again."
                    : "Couldn't save the model. Check that there is free disk space.";
            }
        }

        /// <summary>Deletes a model. Returns false when the file couldn't be removed.</summary>
        public static bool Remove(string id)
        {
            try
            {
                string file = PathOf(id);
                if (File.Exists(file)) File.Delete(file);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static long SizeOfFile(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
