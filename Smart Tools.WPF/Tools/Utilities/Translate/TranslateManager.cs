using Smart_Tools.WPF.Models;
using System.IO;
using System.Net.Http;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Offline translation with Opus-MT models (Helsinki-NLP) run by ONNX Runtime. A model is one folder per language
    /// pair in AppPaths.TranslateModels, downloaded only when the user asks. This half is the catalog and the
    /// download; TranslateManager.Engine.cs is the tokenizer and decoder. Created on the UI thread; owns the loaded
    /// model, so call <see cref="Dispose"/> (it can be reloaded afterwards).
    /// </summary>
    public sealed partial class TranslateManager : IDisposable
    {
        private const string HubUrl = "https://huggingface.co/";

        /// <summary>Remote file in the repo → name on disk. All of them must be present for a pair to count as installed.</summary>
        private static readonly (string Remote, string Local)[] ModelFiles =
        {
            ("onnx/encoder_model_quantized.onnx", "encoder.onnx"),
            ("onnx/decoder_model_merged_quantized.onnx", "decoder.onnx"),
            ("source.spm", "source.spm"),
            ("vocab.json", "vocab.json"),
            ("config.json", "config.json"),
        };

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartTools");
            return client;
        }

        public static string FolderOf(TranslateLanguagePair pair) => Path.Combine(AppPaths.TranslateModels, pair.Id);

        public static bool IsInstalled(TranslateLanguagePair pair)
        {
            try { return ModelFiles.All(f => new FileInfo(Path.Combine(FolderOf(pair), f.Local)) is { Exists: true, Length: > 0 }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        /// <summary>Bytes on disk for the pair (0 when it isn't installed).</summary>
        public static long SizeOf(TranslateLanguagePair pair)
        {
            try { return Directory.Exists(FolderOf(pair)) ? new DirectoryInfo(FolderOf(pair)).EnumerateFiles().Sum(f => f.Length) : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        /// <summary>
        /// Downloads one pair into a temporary folder, then renames it, so a pair is either complete or absent.
        /// Returns null on success, or a short reason. Cancelling throws <see cref="OperationCanceledException"/> and
        /// leaves nothing behind. <paramref name="progress"/> is 0..1 (throttled to ~10/s).
        /// </summary>
        public async Task<string?> InstallAsync(TranslateLanguagePair pair, IProgress<double>? progress, CancellationToken token)
        {
            // The Translate page and Settings can both offer the same download; only one may run.
            lock (Installing)
            {
                if (!Installing.Add(pair.Id)) return "That model is already downloading.";
            }
            try
            {
                return await InstallCoreAsync(pair, progress, token).ConfigureAwait(true);
            }
            finally
            {
                lock (Installing) Installing.Remove(pair.Id);
            }
        }

        private static readonly HashSet<string> Installing = new();

        private async Task<string?> InstallCoreAsync(TranslateLanguagePair pair, IProgress<double>? progress, CancellationToken token)
        {
            string target = FolderOf(pair);
            string temp = target + ".installing";
            long expected = Math.Max(1, pair.ApproxMb * 1024L * 1024L);
            long finishedFiles = 0;
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
                Directory.CreateDirectory(temp);

                var lastReport = System.Diagnostics.Stopwatch.StartNew();
                foreach (var (remote, local) in ModelFiles)
                {
                    using var response = await Http.GetAsync($"{HubUrl}{pair.Repo}/resolve/main/{remote}", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        TryDeleteFolder(temp);
                        return $"The download failed (the server answered {(int)response.StatusCode}).";
                    }

                    long total = response.Content.Headers.ContentLength ?? 0;
                    long done = 0;
                    string file = Path.Combine(temp, local);
                    await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    await using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    {
                        var buffer = new byte[1 << 20];
                        int read;
                        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            done += read;
                            if (lastReport.ElapsedMilliseconds >= 100)
                            {
                                progress?.Report(Math.Min(0.99, (double)(finishedFiles + done) / expected));
                                lastReport.Restart();
                            }
                        }
                    }

                    // A known length that doesn't match means the connection dropped.
                    if (done == 0 || (total > 0 && done != total))
                    {
                        TryDeleteFolder(temp);
                        return "The download was cut short. Check your connection and try again.";
                    }
                    finishedFiles += done;
                }

                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(temp, target);
                progress?.Report(1);
                return null;
            }
            catch (OperationCanceledException)
            {
                TryDeleteFolder(temp);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                TryDeleteFolder(temp);
                return ex is HttpRequestException
                    ? "Couldn't reach the download server. Check your internet connection and try again."
                    : "Couldn't save the model. Check that there is free disk space.";
            }
        }

        /// <summary>Deletes a pair's model. Returns false when the files couldn't be removed (or a translation is using them).</summary>
        public bool Remove(TranslateLanguagePair pair)
        {
            if (Volatile.Read(ref _active) > 0) return false;
            ReleaseIfLoaded(pair.Id);
            try
            {
                if (Directory.Exists(FolderOf(pair))) Directory.Delete(FolderOf(pair), recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void TryDeleteFolder(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
