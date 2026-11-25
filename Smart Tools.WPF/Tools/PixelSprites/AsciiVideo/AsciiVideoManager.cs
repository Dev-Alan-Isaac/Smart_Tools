using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    public enum AsciiVideoStage { Downloading, Reading, Converting, Gif, Sound }

    public sealed record AsciiVideoProgress(AsciiVideoStage Stage, double Percent, int Frame, int Frames);

    public enum AsciiVideoProblem { None, NoEngine, NotVideo, TooLong, Download, Failed }

    public sealed record AsciiVideoResult(bool Ok, bool Cancelled, AsciiVideoProblem Problem, string Detail, IReadOnlyList<string> Outputs, string Title);

    /// <summary>
    /// Makes an ASCII video: (optionally) downloads the video with yt-dlp, decodes it with FFmpeg to one grey pixel per character, turns each
    /// frame into characters, stamps them into a picture with a pre-drawn monospace font, and streams those pictures into FFmpeg for the MP4
    /// (with the original sound), plus a GIF and a text file with a console player. Nothing is written to disk frame by frame, nothing is
    /// ever replaced, and a stopped or failed run deletes what it made. No WPF types.
    /// </summary>
    public static class AsciiVideoManager
    {
        private const string SettingsName = "ascii_video";
        private const string FontName = "Consolas";

        public static AsciiVideoSettings LoadSettings(string? path = null) => JsonSettings.Load<AsciiVideoSettings>(SettingsName, path);

        public static void SaveSettings(AsciiVideoSettings settings, string? path = null) => JsonSettings.Save(SettingsName, settings, path);

        public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smart Tools", "ASCII Video");

        public static bool IsAvailable => File.Exists(FfmpegLocator.FfmpegExe) && File.Exists(FfmpegLocator.FfprobeExe);

        public static bool CanDownload => File.Exists(WebAudioManager.ExePath);

        // =================================================================
        //  The font
        // =================================================================

        /// <summary>The monospace font's cell width / height (about 0.5): how much taller than wide a character is.</summary>
        public static double CellAspect()
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            using var font = new Font(FontName, 100, GraphicsUnit.Pixel);
            var size = g.MeasureString("M", font, PointF.Empty, StringFormat.GenericTypographic);
            return Math.Clamp(size.Width / font.GetHeight(g), 0.3, 0.9);
        }

        /// <summary>Each character of the ramp drawn once, white on black, in one cell: its coverage (0..255) row by row.</summary>
        public static Dictionary<char, byte[]> Masks(string ramp, AsciiLayout layout)
        {
            var masks = new Dictionary<char, byte[]>();
            using var probe = new Bitmap(1, 1);
            using var pg = Graphics.FromImage(probe);
            using var unit = new Font(FontName, 100, GraphicsUnit.Pixel);
            float em = (float)(layout.CellHeight / (unit.GetHeight(pg) / 100.0));
            using var font = new Font(FontName, em, GraphicsUnit.Pixel);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();

            foreach (char ch in ramp)
            {
                if (ch == ' ' || masks.ContainsKey(ch)) continue;
                using var bmp = new Bitmap(layout.CellWidth, layout.CellHeight, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var size = g.MeasureString(ch.ToString(), font, PointF.Empty, format);
                    g.DrawString(ch.ToString(), font, Brushes.White, (layout.CellWidth - size.Width) / 2f, 0, format);
                }
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[data.Stride];
                    var mask = new byte[layout.CellWidth * layout.CellHeight];
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                        for (int x = 0; x < bmp.Width; x++) mask[y * layout.CellWidth + x] = row[x * 4 + 2];   // the red channel = brightness
                    }
                    masks[ch] = mask;
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            return masks;
        }

        // =================================================================
        //  Getting the video
        // =================================================================

        public static async Task<AsciiVideoProbe?> ProbeAsync(string input, CancellationToken token)
        {
            var run = await WebAudioManager.RunAsync(FfmpegLocator.FfprobeExe, AsciiVideoMath.ProbeArgs(input), null, token).ConfigureAwait(false);
            return run.Code == 0 ? AsciiVideoMath.ParseProbe(run.Output) : null;
        }

        /// <summary>Downloads one video (at most 720p) into <paramref name="folder"/>. Returns the file, or null and yt-dlp's complaint.</summary>
        public static async Task<(string? File, WebAudioError Error, string Text)> DownloadAsync(string url, string folder, IProgress<AsciiVideoProgress>? progress, CancellationToken token)
        {
            Directory.CreateDirectory(folder);
            var run = await WebAudioManager.RunAsync(WebAudioManager.ExePath, AsciiVideoMath.DownloadArgs(url, folder, FfmpegLocator.Folder), line =>
            {
                if (progress != null && WebAudioMath.ParseProgress(line) is { } p) progress.Report(new AsciiVideoProgress(AsciiVideoStage.Downloading, p, 0, 0));
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string? file = Directory.GetFiles(folder).Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (run.Code == 0 && file != null) return (file, WebAudioError.None, "");
            return (null, WebAudioMath.Classify(run.Output), WebAudioMath.LastError(run.Output));
        }

        /// <summary>One frame as text, for the preview: at <paramref name="at"/> seconds, or a third of the way in (the start is often black).</summary>
        public static async Task<string?> PreviewAsync(string input, double? at, AsciiVideoSettings s, CancellationToken token)
        {
            var probe = await ProbeAsync(input, token).ConfigureAwait(false);
            if (probe == null) return null;
            var layout = AsciiVideoMath.Layout(s.Columns, probe.Width, probe.Height, CellAspect());
            double when = at ?? (probe.DurationSeconds > 0 ? probe.DurationSeconds / 3 : 0);
            using var decoder = Start(FfmpegLocator.FfmpegExe, AsciiVideoMath.DecodeArgs(input, when > 0 ? when : null, 2.0 / s.Fps, s.Fps, layout));
            decoder.StandardInput.Close();
            _ = decoder.StandardError.ReadToEndAsync(CancellationToken.None);
            using var stop = token.Register(() => Kill(decoder));
            var cells = new byte[layout.CellBytes];
            int got = await decoder.StandardOutput.BaseStream.ReadAtLeastAsync(cells, cells.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
            Kill(decoder);
            if (got < cells.Length) return null;
            return AsciiVideoMath.ToLines(AsciiVideoMath.FrameToChars(cells, AsciiVideoMath.RampText(s.Ramp), s.WhiteOnBlack), layout.Columns);
        }

        // =================================================================
        //  Making it
        // =================================================================

        /// <summary>
        /// Converts <paramref name="input"/> (a video file) into the outputs chosen in <paramref name="s"/>, in <paramref name="folder"/>, named
        /// after <paramref name="title"/>. <paramref name="preview"/> gets the text of about one frame a second while it works.
        /// </summary>
        public static async Task<AsciiVideoResult> ConvertAsync(string input, string title, string folder, AsciiVideoSettings s, double? start, double? end,
            IProgress<AsciiVideoProgress>? progress, Action<string>? preview, CancellationToken token)
        {
            progress?.Report(new AsciiVideoProgress(AsciiVideoStage.Reading, 0, 0, 0));
            var probe = await ProbeAsync(input, token).ConfigureAwait(false);
            if (probe == null) return Fail(AsciiVideoProblem.NotVideo, "", title);

            double from = start ?? 0;
            double length = (end ?? probe.DurationSeconds) - from;
            if (probe.DurationSeconds > 0 && end == null && probe.DurationSeconds - from > AsciiVideoMath.MaxSeconds) return Fail(AsciiVideoProblem.TooLong, "", title);
            if (length > AsciiVideoMath.MaxSeconds) return Fail(AsciiVideoProblem.TooLong, "", title);
            double? lengthArg = end != null ? length : null;
            double? startArg = from > 0 ? from : null;
            int expected = Math.Max(1, (int)Math.Ceiling(Math.Max(0.1, length) * s.Fps));

            var layout = AsciiVideoMath.Layout(s.Columns, probe.Width, probe.Height, CellAspect());
            string ramp = AsciiVideoMath.RampText(s.Ramp);
            var masks = Masks(ramp, layout);

            Directory.CreateDirectory(folder);
            string stem = OutputFolderManager.SafeName(title.Length > 0 ? title : "video") + " ascii";
            var made = new List<string>();
            string? mp4 = null, textFolder = null, gif = null;
            bool needMp4 = s.MakeMp4 || s.MakeGif;
            bool sound = s.KeepSound && probe.HasAudio;
            try
            {
                if (needMp4) mp4 = s.MakeMp4 ? SafeFileOps.UniquePath(Path.Combine(folder, stem + ".mp4")) : Path.Combine(Path.GetTempPath(), "Smart Tools", "ascii-video", Guid.NewGuid().ToString("N") + ".mp4");
                if (needMp4) Directory.CreateDirectory(Path.GetDirectoryName(mp4!)!);
                if (s.MakeText)
                {
                    textFolder = UniqueFolder(Path.Combine(folder, stem + " text"));
                    Directory.CreateDirectory(textFolder);
                    made.Add(textFolder);
                }
                if (s.MakeMp4) made.Add(mp4!);

                int frames = await PipeFramesAsync(input, startArg, lengthArg, s, layout, ramp, masks, mp4, sound, textFolder, expected, progress, preview, token).ConfigureAwait(false);
                if (frames == 0) return Fail(AsciiVideoProblem.Failed, "No frames came out of the video.", title, made);

                if (textFolder != null)
                {
                    if (sound)
                    {
                        progress?.Report(new AsciiVideoProgress(AsciiVideoStage.Sound, 100, frames, frames));
                        await RunCheckedAsync(FfmpegLocator.FfmpegExe, AsciiVideoMath.SoundArgs(input, startArg, lengthArg, Path.Combine(textFolder, "sound.wav")), token).ConfigureAwait(false);
                    }
                    File.WriteAllText(Path.Combine(textFolder, "play.ps1"), AsciiVideoMath.PlayerScript(), new UTF8Encoding(true));
                    File.WriteAllText(Path.Combine(textFolder, "play.cmd"), AsciiVideoMath.PlayerCmd(), Encoding.ASCII);
                }
                if (s.MakeGif)
                {
                    progress?.Report(new AsciiVideoProgress(AsciiVideoStage.Gif, 100, frames, frames));
                    gif = SafeFileOps.UniquePath(Path.Combine(folder, stem + ".gif"));
                    made.Add(gif);
                    await RunCheckedAsync(FfmpegLocator.FfmpegExe, AsciiVideoMath.GifArgs(mp4!, s.Fps, gif), token).ConfigureAwait(false);
                }
                if (!s.MakeMp4 && mp4 != null) SafeFileOps.TryDelete(mp4);

                var outputs = new List<string>();
                if (s.MakeMp4) outputs.Add(mp4!);
                if (gif != null) outputs.Add(gif);
                if (textFolder != null) outputs.Add(Path.Combine(textFolder, "play.cmd"));
                return new AsciiVideoResult(true, false, AsciiVideoProblem.None, "", outputs, title);
            }
            catch (OperationCanceledException)
            {
                CleanUp(made, mp4);
                return new AsciiVideoResult(false, true, AsciiVideoProblem.None, "", Array.Empty<string>(), title);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Log.Warn("ASCII video: the conversion failed", ex);
                CleanUp(made, mp4);
                return Fail(AsciiVideoProblem.Failed, ex.Message, title);
            }
        }

        private static AsciiVideoResult Fail(AsciiVideoProblem problem, string detail, string title, List<string>? made = null)
        {
            if (made != null) CleanUp(made, null);
            return new AsciiVideoResult(false, false, problem, detail, Array.Empty<string>(), title);
        }

        /// <summary>The decode -> characters -> picture -> encode loop. Frames go through memory only. Returns how many frames were made.</summary>
        private static async Task<int> PipeFramesAsync(string input, double? start, double? length, AsciiVideoSettings s, AsciiLayout layout, string ramp,
            Dictionary<char, byte[]> masks, string? mp4, bool sound, string? textFolder, int expected,
            IProgress<AsciiVideoProgress>? progress, Action<string>? preview, CancellationToken token)
        {
            using var decoder = Start(FfmpegLocator.FfmpegExe, AsciiVideoMath.DecodeArgs(input, start, length, s.Fps, layout));
            using var encoder = mp4 == null ? null : Start(FfmpegLocator.FfmpegExe, AsciiVideoMath.EncodeArgs(input, start, length, s.Fps, layout, sound, mp4));
            decoder.StandardInput.Close();
            var decoderErrors = decoder.StandardError.ReadToEndAsync(CancellationToken.None);
            var encoderErrors = encoder?.StandardError.ReadToEndAsync(CancellationToken.None);
            using var stop = token.Register(() =>
            {
                Kill(decoder);
                if (encoder != null) Kill(encoder);
            });

            StreamWriter? text = null;
            string? textPath = textFolder == null ? null : Path.Combine(textFolder, "frames.txt");
            if (textPath != null)
            {
                text = new StreamWriter(textPath, false, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
                await text.WriteLineAsync(CountedHeader(layout, s.Fps, 0)).ConfigureAwait(false);   // the count is filled in at the end
            }

            int frames = 0;
            try
            {
                var cells = new byte[layout.CellBytes];
                var picture = new byte[layout.Width * layout.Height];
                var source = decoder.StandardOutput.BaseStream;
                var sink = encoder?.StandardInput.BaseStream;
                while (true)
                {
                    int got = await source.ReadAtLeastAsync(cells, cells.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
                    if (got < cells.Length) break;   // the end (a partial last frame is dropped)
                    var chars = AsciiVideoMath.FrameToChars(cells, ramp, s.WhiteOnBlack);
                    string? lines = text != null || (preview != null && frames % s.Fps == 0) ? AsciiVideoMath.ToLines(chars, layout.Columns) : null;
                    if (text != null) await text.WriteAsync(lines).ConfigureAwait(false);
                    if (sink != null)
                    {
                        AsciiVideoMath.Compose(chars, layout, masks, s.WhiteOnBlack, picture);
                        await sink.WriteAsync(picture, token).ConfigureAwait(false);
                    }
                    frames++;
                    if (preview != null && frames % s.Fps == 1 && lines != null) preview(lines);
                    if (frames % 5 == 0) progress?.Report(new AsciiVideoProgress(AsciiVideoStage.Converting, Math.Min(99, frames * 100.0 / expected), frames, expected));
                }
                sink?.Close();
            }
            finally
            {
                if (text != null) await text.DisposeAsync().ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            await decoder.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (encoder != null) await encoder.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (decoder.ExitCode != 0) throw new InvalidOperationException(FirstLine(await decoderErrors.ConfigureAwait(false), "FFmpeg could not read the video."));
            if (encoder != null && encoder.ExitCode != 0) throw new InvalidOperationException(FirstLine(await encoderErrors!.ConfigureAwait(false), "FFmpeg could not write the video."));

            if (textPath != null)
            {
                // fill in the real frame count (same length: the count was written as ten digits)
                string header = CountedHeader(layout, s.Fps, frames);
                using var fs = new FileStream(textPath, FileMode.Open, FileAccess.Write);
                fs.Write(Encoding.ASCII.GetBytes(header));
            }
            return frames;
        }

        /// <summary>The header with the frame count as ten digits, so it can be rewritten in place once the real count is known.</summary>
        private static string CountedHeader(AsciiLayout layout, int fps, int frames) =>
            AsciiVideoMath.Header(layout.Columns, layout.Rows, fps, 0)[..^1] + frames.ToString("D10", System.Globalization.CultureInfo.InvariantCulture);

        private static string FirstLine(string text, string fallback)
        {
            string line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return line.Length > 0 ? line : fallback;
        }

        private static Process Start(string exe, IReadOnlyList<string> args)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            return Process.Start(info) ?? throw new InvalidOperationException("FFmpeg did not start.");
        }

        private static void Kill(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }

        private static async Task RunCheckedAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            var run = await WebAudioManager.RunAsync(exe, args, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (run.Code != 0) throw new InvalidOperationException(FirstLine(run.Output, "FFmpeg stopped with an error."));
        }

        private static string UniqueFolder(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int n = 2; ; n++)
            {
                string candidate = $"{path} ({n})";
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
        }

        /// <summary>Removes what this run created (they did not exist before it: every name was checked to be free).</summary>
        private static void CleanUp(IEnumerable<string> made, string? tempMp4)
        {
            foreach (string path in made)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else SafeFileOps.TryDelete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a file still held: left for the user
                }
            }
            if (tempMp4 != null && tempMp4.Contains(Path.Combine("Smart Tools", "ascii-video"), StringComparison.OrdinalIgnoreCase)) SafeFileOps.TryDelete(tempMp4);
        }
    }
}
