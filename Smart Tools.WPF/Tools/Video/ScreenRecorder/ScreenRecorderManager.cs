using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Screen Recorder - runs the bundled FFmpeg (gdigrab for the picture, DirectShow for a microphone) and finishes the file
    /// cleanly: stopping sends FFmpeg a "q", which lets it write the end of the MP4; only if that takes too long is it killed.
    /// A recording is one or more parts (fragmented MP4s, so a crash still leaves playable parts): switching the recorded screen
    /// or a Windows security prompt (UAC, lock screen) ends one part and starts the next, and stopping joins them into the final
    /// file. Create on the UI thread: events are posted back to the captured context. The planning half (arguments, sizes,
    /// parsing) is in <c>ScreenRecorderManager.Plan.cs</c>; watching for security prompts is in <c>.Desktop.cs</c>.
    /// </summary>
    public sealed partial class ScreenRecorderManager : IDisposable
    {
        private const int StartupCheckMs = 1200;
        private const int StopWaitMs = 8000;
        private const int JoinTimeoutMs = 10 * 60 * 1000;
        private const int KeepErrorChars = 4000;

        /// <summary>An empty fragmented MP4 is just a header; anything under a few KB holds no video.</summary>
        private const long MinVideoBytes = 4096;

        private readonly SynchronizationContext? _context = SynchronizationContext.Current;
        private readonly StringBuilder _errors = new();
        private readonly Stopwatch _clock = new();
        private readonly SemaphoreSlim _gate = new(1, 1);   // one change at a time: switch, pause, resume, stop
        private readonly List<string> _parts = new();       // finished parts, in order
        private Process? _process;                          // the part being recorded now (null while paused)
        private RecordOptions? _options;                    // what the recording started with; Region changes on a screen switch
        private SecurityPromptWatcher? _watcher;
        private string _finalPath = "", _partsFolder = "", _partPath = "";
        private TimeSpan _accumulated;                      // time in the finished parts
        private int _partIndex;
        private bool _active, _stopping, _starting, _partEnding, _paused;

        /// <summary>Raised (on the UI thread) when FFmpeg stops by itself, for example because the recorded window closed.</summary>
        public event Action<RecordingResult>? EndedUnexpectedly;

        /// <summary>Raised (on the UI thread) when recording pauses for, or resumes after, a Windows security prompt.</summary>
        public event Action<bool>? PausedChanged;

        public static bool IsAvailable => FfmpegLocator.IsAvailable;

        public bool IsRecording => _active && !_stopping;

        /// <summary>Recording is waiting for a Windows security prompt (UAC, lock screen) to close.</summary>
        public bool IsPaused => _paused;

        /// <summary>Time recorded so far. It doesn't count the time a security prompt was up.</summary>
        public TimeSpan Elapsed => _accumulated + _clock.Elapsed;

        // =================================================================
        //  Start and stop
        // =================================================================

        /// <summary>Starts recording. False with a plain-words reason when FFmpeg is missing or exits straight away (a bad window, a busy microphone, ...).</summary>
        public async Task<(bool Ok, string Error)> StartAsync(RecordOptions options)
        {
            if (!FfmpegLocator.IsAvailable) return (false, "FFmpeg isn't installed with this copy of Smart Tools.");
            if (_active) return (false, "A recording is already running.");

            _finalPath = options.OutputPath;
            _partsFolder = PartsFolderFor(_finalPath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_finalPath))!);
                Directory.CreateDirectory(_partsFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return (false, ex.Message);
            }

            _options = options;
            _parts.Clear();
            _accumulated = TimeSpan.Zero;
            _clock.Reset();
            _partIndex = 0;
            _stopping = false;
            _paused = false;
            _active = true;

            var (ok, error) = await LaunchPartAsync();
            if (!ok)
            {
                ResetSession();
                TryDeleteFolder(_partsFolder);
                return (false, error);
            }

            if (options.SkipSecurityPrompts) _watcher = new SecurityPromptWatcher(OnSecurityPromptChanged);
            return (true, "");
        }

        /// <summary>Stops, joins the parts, and finishes the file. The result says whether a playable video was written.</summary>
        public async Task<RecordingResult> StopAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (!_active) return new RecordingResult(false, _finalPath, 0, TimeSpan.Zero, "Nothing is being recorded.");

                _stopping = true;
                DisposeWatcher();
                string note = await EndPartAsync();
                var result = await JoinAsync(note);
                ResetSession();
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Switches the recorded screen without stopping the recording: the current part ends and a new one starts on the other
        /// screen (null = all screens). About a second of video is lost in the switch. Only for the "whole screen" target.
        /// </summary>
        public async Task<(bool Ok, string Error)> SwitchScreenAsync(RecordRegion? region)
        {
            await _gate.WaitAsync();
            try
            {
                if (!_active || _stopping || _options == null) return (false, "Nothing is being recorded.");
                if (_options.Target != RecordTarget.Screen) return (false, "Only a whole screen can be switched.");

                var previous = _options;
                _options = previous with { Region = region };
                if (_paused) return (true, "");   // the new screen is used when recording resumes

                await EndPartAsync();
                var (ok, error) = await LaunchPartAsync();
                if (ok) return (true, "");

                // The new screen couldn't be recorded: go back to the old one, or finish with what there is.
                _options = previous;
                var (back, _) = await LaunchPartAsync();
                if (!back) await FinishAfterFailureAsync("");
                return (false, error);
            }
            finally
            {
                _gate.Release();
            }
        }

        // =================================================================
        //  Parts
        // =================================================================

        /// <summary>Starts FFmpeg on the next part. The same start-up check as always: a bad window or a busy microphone is reported here.</summary>
        private async Task<(bool Ok, string Error)> LaunchPartAsync()
        {
            var options = _options!;
            _partIndex++;
            _partPath = PartPath(_partsFolder, _partIndex);

            var psi = new ProcessStartInfo(FfmpegLocator.FfmpegExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // FFmpeg isn't marked DPI-aware; on a scaled display it would otherwise capture a blurry, stretched picture.
            psi.Environment["__COMPAT_LAYER"] = "HighDpiAware";
            foreach (string arg in BuildArguments(options with { OutputPath = _partPath })) psi.ArgumentList.Add(arg);

            _errors.Clear();
            _starting = true;   // FFmpeg may exit at once (a bad window): this method reports that itself, so OnExited stays out of it
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_errors)
                {
                    _errors.AppendLine(e.Data);
                    if (_errors.Length > KeepErrorChars) _errors.Remove(0, _errors.Length - KeepErrorChars);
                }
            };
            process.Exited += (_, _) => OnExited(process);

            try
            {
                process.Start();
                process.BeginErrorReadLine();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _starting = false;
                process.Dispose();
                return (false, ex.Message);
            }
            _process = process;

            await Task.Delay(StartupCheckMs);
            if (process.HasExited)
            {
                await Task.Delay(100);   // let the last error lines arrive
                string reason = PlainError(ErrorText());
                _starting = false;
                _process = null;
                process.Dispose();
                TryDelete(_partPath);
                return (false, reason.Length > 0 ? reason : "FFmpeg stopped right after starting.");
            }

            _clock.Restart();
            _starting = false;
            if (process.HasExited) HandleUnexpectedEnd(process);   // it ended in the instant between the check and here
            return (true, "");
        }

        /// <summary>Finishes the part being recorded (if any) and keeps it. Returns a note about anything that went wrong.</summary>
        private async Task<string> EndPartAsync()
        {
            var process = _process;
            if (process == null) return "";

            _partEnding = true;
            _clock.Stop();
            _accumulated += _clock.Elapsed;
            _clock.Reset();
            string note = "";

            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteAsync("q");
                    await process.StandardInput.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    // FFmpeg already closed its input (it is stopping on its own).
                }

                bool exited = await Task.Run(() => process.WaitForExit(StopWaitMs));
                if (!exited)
                {
                    note = "FFmpeg didn't finish in time and was stopped. The end of the video may be missing.";
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    await Task.Run(() => process.WaitForExit(2000));
                }
            }

            string errors = PlainError(ErrorText());
            _process = null;
            try { process.Dispose(); }
            catch (InvalidOperationException) { }
            _partEnding = false;

            if (FileSize(_partPath) >= MinVideoBytes) _parts.Add(_partPath);
            else TryDelete(_partPath);
            return note.Length > 0 ? note : errors;
        }

        private void OnExited(Process process)
        {
            if (_starting || _stopping || _partEnding || !ReferenceEquals(process, _process)) return;
            HandleUnexpectedEnd(process);
        }

        /// <summary>FFmpeg ended on its own: the recorded window closed, the microphone went away, or it crashed.</summary>
        private void HandleUnexpectedEnd(Process process) => Post(() => _ = FinishUnexpectedlyAsync(process));

        private async Task FinishUnexpectedlyAsync(Process process)
        {
            await _gate.WaitAsync();
            try
            {
                if (!_active || _stopping || !ReferenceEquals(process, _process)) return;
                await FinishAfterFailureAsync("");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Ends the recording with whatever was recorded (call with the gate held) and tells the view it ended by itself.</summary>
        private async Task FinishAfterFailureAsync(string extraNote)
        {
            _stopping = true;
            DisposeWatcher();
            string note = await EndPartAsync();
            var result = await JoinAsync(note.Length > 0 ? note : extraNote);
            ResetSession();
            Post(() => EndedUnexpectedly?.Invoke(result));
        }

        // =================================================================
        //  Security prompts (UAC, lock screen): left out of the video
        // =================================================================

        private void OnSecurityPromptChanged(bool active) => Post(() => _ = HandleSecurityPromptAsync(active));

        private async Task HandleSecurityPromptAsync(bool active)
        {
            await _gate.WaitAsync();
            try
            {
                if (!_active || _stopping || _options == null) return;

                if (active && !_paused)
                {
                    _paused = true;
                    await EndPartAsync();
                    PausedChanged?.Invoke(true);
                }
                else if (!active && _paused)
                {
                    var (ok, error) = await LaunchPartAsync();
                    _paused = false;
                    if (!ok)
                    {
                        await FinishAfterFailureAsync(error);
                        return;
                    }
                    PausedChanged?.Invoke(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private void DisposeWatcher()
        {
            var watcher = _watcher;
            _watcher = null;
            watcher?.Dispose();
        }

        // =================================================================
        //  Joining the parts
        // =================================================================

        /// <summary>Turns the parts into the final file. When joining fails the parts folder is kept and named in the message.</summary>
        private async Task<RecordingResult> JoinAsync(string note)
        {
            var duration = _accumulated;
            var parts = _parts.Where(File.Exists).ToList();
            if (parts.Count == 0)
            {
                TryDeleteFolder(_partsFolder);
                return new RecordingResult(false, _finalPath, 0, duration, note.Length > 0 ? note : "Nothing was recorded.");
            }

            string? problem = null;
            if (parts.Count == 1)
            {
                try { File.Move(parts[0], _finalPath, overwrite: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = ex.Message; }
            }
            else
            {
                problem = await JoinPartsAsync(parts, _finalPath);
            }

            if (problem != null)
            {
                TryDelete(_finalPath);
                return new RecordingResult(false, _finalPath, 0, duration,
                    $"Joining the pieces failed, so they were left in \"{_partsFolder}\". {problem}".Trim());
            }

            long bytes = FileSize(_finalPath);
            if (bytes < MinVideoBytes)
            {
                TryDelete(_finalPath);
                TryDeleteFolder(_partsFolder);
                return new RecordingResult(false, _finalPath, 0, duration, note.Length > 0 ? note : "Nothing was recorded.");
            }

            TryDeleteFolder(_partsFolder);
            return new RecordingResult(true, _finalPath, bytes, duration, note);
        }

        /// <summary>Joins two or more parts with FFmpeg. Null on success, or a short reason.</summary>
        private async Task<string?> JoinPartsAsync(List<string> parts, string output)
        {
            var sizes = new List<(int Width, int Height)>();
            foreach (string part in parts)
            {
                var probe = await RunToolAsync(FfmpegLocator.FfprobeExe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", part }, 30_000);
                var size = probe.ExitCode == 0 ? ParseSize(probe.Output) : null;
                if (size is not { } s) return "Couldn't read the size of one piece.";
                sizes.Add(s);
            }

            string listFile = Path.Combine(_partsFolder, "parts.txt");
            try
            {
                await File.WriteAllTextAsync(listFile, BuildConcatListText(parts));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }

            var options = _options!;
            var args = BuildMergeArguments(listFile, parts, output, sizes, options.Quality, options.Fps, !string.IsNullOrWhiteSpace(options.Microphone));
            var run = await RunToolAsync(FfmpegLocator.FfmpegExe, args, JoinTimeoutMs);
            if (run.ExitCode == 0) return null;
            string reason = PlainError(run.Error);
            return reason.Length > 0 ? reason : "FFmpeg couldn't join them.";
        }

        private static async Task<(int ExitCode, string Output, string Error)> RunToolAsync(string exe, IEnumerable<string> args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return (-1, "", "");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(timeoutMs);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    return (-1, "", "It took too long.");
                }
                return (process.ExitCode, await output, await error);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return (-1, "", ex.Message);
            }
        }

        private string ErrorText()
        {
            lock (_errors) return _errors.ToString();
        }

        private void ResetSession()
        {
            _active = false;
            _stopping = false;
            _paused = false;
            _options = null;
            _parts.Clear();
            _accumulated = TimeSpan.Zero;
            _clock.Reset();
        }

        private static long FileSize(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (path.Length > 0 && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static void TryDeleteFolder(string path)
        {
            try
            {
                if (path.Length > 0 && Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private void Post(Action action)
        {
            if (_context == null) action();
            else _context.Post(_ => action(), null);
        }

        /// <summary>Closing the app mid-recording: finish the part, join what there is, and don't leave FFmpeg running.</summary>
        public void Dispose()
        {
            if (!_active) return;
            _stopping = true;
            DisposeWatcher();
            try
            {
                Task.Run(async () =>
                {
                    await EndPartAsync();
                    await JoinAsync("");
                }).Wait(60_000);
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException) { }
            ResetSession();
        }

        // =================================================================
        //  What can be recorded
        // =================================================================

        /// <summary>All screens together, then each monitor. Pixels on the virtual desktop, the way FFmpeg wants them.</summary>
        public static List<RecordScreenInfo> ListScreens()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var list = new List<RecordScreenInfo>();
            var all = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (screens.Length > 1)
                list.Add(new RecordScreenInfo { Name = $"All screens ({all.Width} × {all.Height})", Region = null });

            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                list.Add(new RecordScreenInfo
                {
                    Name = $"Screen {i + 1}{(screens[i].Primary ? " (main)" : "")} ({b.Width} × {b.Height})",
                    Region = new RecordRegion(b.X, b.Y, b.Width, b.Height),
                    IsPrimary = screens[i].Primary,
                });
            }
            return list;
        }

        /// <summary>The microphones DirectShow knows, by asking FFmpeg. Empty if there are none or FFmpeg is missing.</summary>
        public static async Task<List<string>> ListMicrophonesAsync()
        {
            if (!FfmpegLocator.IsAvailable) return new List<string>();
            try
            {
                var psi = new ProcessStartInfo(FfmpegLocator.FfmpegExe, "-hide_banner -list_devices true -f dshow -i dummy")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var process = Process.Start(psi);
                if (process == null) return new List<string>();
                var read = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(8000))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    return new List<string>();
                }
                return ParseAudioDevices(await read);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new List<string>();
            }
        }

        // ---- windows ----

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        private const int GwlExStyle = -20, WsExToolWindow = 0x80, WsExAppWindow = 0x40000, DwmwaCloaked = 14;

        /// <summary>Open, visible, titled windows (not minimized, not hidden by Windows), sorted by title.</summary>
        public static List<RecordWindowInfo> ListWindows()
        {
            var list = new List<RecordWindowInfo>();
            EnumWindows((handle, _) =>
            {
                try
                {
                    if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
                    int length = GetWindowTextLength(handle);
                    if (length == 0) return true;

                    int style = GetWindowLong(handle, GwlExStyle);
                    if ((style & WsExToolWindow) != 0 && (style & WsExAppWindow) == 0) return true;   // floating tool windows, tray popups
                    if (DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // on another virtual desktop, or suspended

                    if (!GetWindowRect(handle, out var r) || r.Right - r.Left < 50 || r.Bottom - r.Top < 50) return true;

                    var text = new StringBuilder(length + 1);
                    GetWindowText(handle, text, text.Capacity);
                    string title = text.ToString();
                    if (title == "Program Manager") return true;
                    list.Add(new RecordWindowInfo { Handle = handle.ToInt64(), Title = title });
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or OverflowException)
                {
                    // One odd window must not stop the list.
                }
                return true;
            }, IntPtr.Zero);
            return list.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>True while the window still exists and isn't minimized (a minimized window records as a blank picture).</summary>
        public static bool IsWindowRecordable(long handle) => handle != 0 && IsWindow(new IntPtr(handle)) && !IsIconic(new IntPtr(handle));
    }
}
