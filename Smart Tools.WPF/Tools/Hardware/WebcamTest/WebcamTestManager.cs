using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Behind WebcamTestView: lists cameras and their sizes by asking the bundled FFmpeg (DirectShow), and runs
    /// the preview as an FFmpeg process that writes raw frames to a pipe. Nothing is recorded or saved. The view
    /// reads the newest frame on a timer with <see cref="TryGetFrame"/>.
    ///
    /// Threads: frames are read on a background task and copied into one shared buffer behind a lock.
    /// <see cref="Stopped"/> is raised on the thread that created the manager (the UI thread).
    /// </summary>
    public sealed partial class WebcamTestManager : IDisposable
    {
        private readonly object _gate = new();
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;
        private readonly StringBuilder _errors = new();

        private Process? _process;
        private CancellationTokenSource? _cts;
        private Task? _reader;

        private byte[] _front = Array.Empty<byte>();
        private long _frames;
        private double _fps;
        private double _brightness;
        private int _windowFrames;
        private DateTime _windowStart;

        public bool IsRunning => _process != null;
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>The preview ended without being stopped (unplugged, taken by another app): a plain reason. UI thread.</summary>
        public event Action<string>? Stopped;

        // =================================================================
        //  Finding cameras
        // =================================================================

        /// <summary>The cameras DirectShow knows. Empty if there are none or FFmpeg is missing.</summary>
        public static async Task<List<WebcamDevice>> ListDevicesAsync()
        {
            string output = await RunFfmpegForTextAsync("-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy");
            return ParseVideoDevices(output);
        }

        /// <summary>The sizes a camera offers. Empty if it won't say.</summary>
        public static async Task<List<WebcamMode>> ListModesAsync(WebcamDevice device)
        {
            string output = await RunFfmpegForTextAsync("-hide_banner", "-list_options", "true", "-f", "dshow", "-i", "video=" + device.Name);
            return ParseModes(output);
        }

        /// <summary>Runs FFmpeg and returns what it printed on the error channel (where it writes its lists). Gives up after a few seconds.</summary>
        private static async Task<string> RunFfmpegForTextAsync(params string[] args)
        {
            if (!FfmpegLocator.IsAvailable) return "";
            try
            {
                var psi = new ProcessStartInfo(FfmpegLocator.FfmpegExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (string arg in args) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi);
                if (process == null) return "";
                var read = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(8000))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
                    return "";
                }
                return await read;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return "";
            }
        }

        // =================================================================
        //  The preview
        // =================================================================

        /// <summary>Starts the preview. Throws <see cref="InvalidOperationException"/> with a plain reason if it can't.</summary>
        public void Start(WebcamDevice device, WebcamMode mode)
        {
            if (_process != null) throw new InvalidOperationException("Already running.");
            if (!FfmpegLocator.IsAvailable) throw new FileNotFoundException("FFmpeg is missing.");

            var psi = new ProcessStartInfo(FfmpegLocator.FfmpegExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-fflags", "nobuffer", "-flags", "low_delay",
                "-f", "dshow", "-video_size", $"{mode.Width}x{mode.Height}", "-framerate", mode.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-i", "video=" + device.Name,
                "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1",
            })
                psi.ArgumentList.Add(arg);

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            if (process == null) throw new InvalidOperationException("FFmpeg did not start.");

            lock (_gate)
            {
                Width = mode.Width;
                Height = mode.Height;
                _front = new byte[mode.Width * mode.Height * 3];
                _frames = 0;
                _fps = 0;
                _brightness = 0;
                _windowFrames = 0;
                _windowStart = DateTime.Now;
                _errors.Clear();
            }
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_gate)
                {
                    if (_errors.Length < 4000) _errors.AppendLine(e.Data);
                }
            };
            process.BeginErrorReadLine();

            _process = process;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int width = mode.Width, height = mode.Height;
            _reader = Task.Run(() => ReadFrames(process, width, height, token));
        }

        private void ReadFrames(Process process, int width, int height, CancellationToken token)
        {
            var buffer = new byte[width * height * 3];
            var stream = process.StandardOutput.BaseStream;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    stream.ReadExactly(buffer);
                    double brightness = AverageBrightness(buffer, width, height);
                    lock (_gate)
                    {
                        Buffer.BlockCopy(buffer, 0, _front, 0, buffer.Length);
                        _frames++;
                        _brightness = brightness;
                        _windowFrames++;
                        double seconds = (DateTime.Now - _windowStart).TotalSeconds;
                        if (seconds >= 1)
                        {
                            _fps = _windowFrames / seconds;
                            _windowFrames = 0;
                            _windowStart = DateTime.Now;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The pipe closed: either we stopped it, or FFmpeg ended by itself.
            }

            if (token.IsCancellationRequested) return;
            string reason;
            lock (_gate) reason = ScreenRecorderManager.PlainError(_errors.ToString());
            if (reason.Length == 0) reason = "The camera stopped sending pictures.";
            _context?.Post(_ =>
            {
                if (!ReferenceEquals(_process, process)) return;   // already stopped or replaced
                Teardown();
                Stopped?.Invoke(reason);
            }, null);
        }

        /// <summary>
        /// Copies the newest frame into <paramref name="destination"/> (BGR24, <see cref="Width"/> × <see cref="Height"/>) if it is
        /// newer than <paramref name="lastFrame"/>. Returns false when there is nothing new.
        /// </summary>
        public bool TryGetFrame(byte[] destination, ref long lastFrame)
        {
            lock (_gate)
            {
                if (_frames == 0 || _frames == lastFrame || destination.Length < _front.Length) return false;
                Buffer.BlockCopy(_front, 0, destination, 0, _front.Length);
                lastFrame = _frames;
                return true;
            }
        }

        public WebcamStats Stats()
        {
            lock (_gate) return new WebcamStats(_frames, _fps, _brightness);
        }

        /// <summary>Stops the preview and lets go of the camera.</summary>
        public void Stop() => Teardown();

        private void Teardown()
        {
            var process = _process;
            var cts = _cts;
            var reader = _reader;
            _process = null;
            _cts = null;
            _reader = null;
            if (process == null) return;

            cts?.Cancel();
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            try { reader?.Wait(1000); }
            catch (AggregateException) { /* the reader only ends early on a closed pipe */ }
            process.Dispose();
            cts?.Dispose();
        }

        public void Dispose() => Teardown();
    }
}
