using System.Diagnostics;
using System.IO;
using System.Text;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    public sealed class WebAudioSettings
    {
        public WebAudioFormat Format { get; set; } = WebAudioFormat.Mp3;
        public WebAudioQuality Quality { get; set; } = WebAudioQuality.Best;
        public bool OpenFolderWhenDone { get; set; } = true;
    }

    public sealed record WebAudioProgress(double Percent, string Speed, string Eta);

    public sealed record WebAudioInfoResult(WebAudioInfo? Info, WebAudioError Error, string Text);

    public sealed record WebAudioResult(bool Ok, bool Cancelled, WebAudioError Error, string Text, IReadOnlyList<string> Files);

    /// <summary>
    /// Saving the sound of a web link as an audio file, through the bundled yt-dlp (Engines\YTDLP) and FFmpeg. The network is only used
    /// when the user presses a button. Rules: nothing is ever replaced (the sound is made in a temp folder, then moved under a free name),
    /// a stopped or failed run removes only its own temp folder, and yt-dlp's input is closed so it can never wait for a keypress.
    /// </summary>
    public static class WebAudioManager
    {
        private const string SettingsName = "web_audio";

        public static WebAudioSettings LoadSettings(string? path = null) => JsonSettings.Load<WebAudioSettings>(SettingsName, path);

        public static void SaveSettings(WebAudioSettings settings, string? path = null) => JsonSettings.Save(SettingsName, settings, path);

        public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smart Tools", "Web Audio");

        // =================================================================
        //  The programs
        // =================================================================

        public static string BundledExe { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engines", "YTDLP", "yt-dlp.exe");

        /// <summary>A copy the user updated lives here (Engines\ can be read-only after an install).</summary>
        public static string UpdatedExe { get; } = Path.Combine(AppPaths.DataRoot, "YtDlp", "yt-dlp.exe");

        public static string ExePath => File.Exists(UpdatedExe) ? UpdatedExe : BundledExe;

        public static bool IsAvailable => File.Exists(ExePath) && File.Exists(FfmpegLocator.FfmpegExe);

        public static async Task<(int Code, string Output, bool Cancelled)> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onLine, CancellationToken token)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            using var process = new Process { StartInfo = info };
            process.Start();
            process.StandardInput.Close();
            var output = new StringBuilder();
            object gate = new();

            async Task Pump(StreamReader reader)
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (gate)
                        if (output.Length < 8 * 1024 * 1024) output.AppendLine(line);
                    onLine?.Invoke(line);
                }
            }

            using var stop = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // it ended on its own just now
                }
            });
            await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError)).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return (process.ExitCode, output.ToString(), token.IsCancellationRequested);
        }

        // =================================================================
        //  A link
        // =================================================================

        public static async Task<WebAudioInfoResult> GetInfoAsync(string url, CancellationToken token)
        {
            var run = await RunAsync(ExePath, WebAudioMath.InfoArgs(url), null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var info = run.Code == 0 ? WebAudioMath.ParseInfo(run.Output) : null;
            if (info != null) return new WebAudioInfoResult(info, WebAudioError.None, "");
            return new WebAudioInfoResult(null, WebAudioMath.Classify(run.Output), WebAudioMath.LastError(run.Output));
        }

        public static async Task<WebAudioResult> DownloadAsync(string url, string destination, WebAudioSettings settings, double? start, double? end,
            IProgress<WebAudioProgress>? progress, CancellationToken token)
        {
            string temp = Path.Combine(Path.GetTempPath(), "Smart Tools", "web-audio", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var args = WebAudioMath.DownloadArgs(url, temp, FfmpegLocator.Folder, settings.Format, settings.Quality, start, end);
                var run = await RunAsync(ExePath, args, line =>
                {
                    if (progress != null && WebAudioMath.ParseProgress(line) is { } percent)
                    {
                        var (speed, eta) = WebAudioMath.ParseSpeedEta(line);
                        progress.Report(new WebAudioProgress(percent, speed, eta));
                    }
                }, token).ConfigureAwait(false);

                if (run.Cancelled) return new WebAudioResult(false, true, WebAudioError.None, "", Array.Empty<string>());
                string ext = "." + WebAudioMath.Extension(settings.Format);
                var made = Directory.GetFiles(temp).Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).ToList();
                if (run.Code != 0 || made.Count == 0)
                {
                    var kind = WebAudioMath.Classify(run.Output);
                    string text = WebAudioMath.LastError(run.Output);
                    return new WebAudioResult(false, false, kind, text.Length > 0 ? text : $"yt-dlp stopped with code {run.Code}.", Array.Empty<string>());
                }

                Directory.CreateDirectory(destination);
                var saved = new List<string>();
                foreach (string file in made)
                {
                    string target = SafeFileOps.UniquePath(Path.Combine(destination, Path.GetFileName(file)));   // a name that is taken becomes " (2)"
                    File.Move(file, target);
                    saved.Add(target);
                }
                return new WebAudioResult(true, false, WebAudioError.None, "", saved);
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a leftover temp folder is harmless
                }
            }
        }

        // =================================================================
        //  The program itself
        // =================================================================

        public static async Task<string> VersionAsync(CancellationToken token)
        {
            var run = await RunAsync(ExePath, new[] { "--version" }, null, token).ConfigureAwait(false);
            return run.Code == 0 ? run.Output.Trim() : "";
        }

        /// <summary>Lets yt-dlp update a copy of itself in the user's data folder (the app's own copy is never changed). Returns yt-dlp's words.</summary>
        public static async Task<(bool Ok, string Text)> UpdateAsync(CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdatedExe)!);
            if (!File.Exists(UpdatedExe)) File.Copy(BundledExe, UpdatedExe);
            var run = await RunAsync(UpdatedExe, WebAudioMath.UpdateArgs(), null, token).ConfigureAwait(false);
            string text = run.Output.Trim();
            return (run.Code == 0, text.Length > 600 ? text[^600..] : text);
        }
    }
}
