using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// A small wrapper over VLC's own library (libvlc 3.x), loaded from
    /// Engines\VLC next to the app - no NuGet packages. Plays WebM, MKV, FLV,
    /// AV1 and everything else VLC can, with hardware decoding and its own
    /// audio/video sync.
    ///
    /// Only the calls Video Replay needs are declared. There are no libvlc
    /// event callbacks: the view polls <see cref="State"/> on its position
    /// timer, which avoids calling back into VLC from VLC's own threads
    /// (a classic libvlc deadlock) and keeps everything on the UI thread.
    /// </summary>
    public sealed class VlcPlayer : IDisposable
    {
        /// <summary>libvlc_state_t (VLC 3.x).</summary>
        public enum VlcState
        {
            Idle = 0,
            Opening = 1,
            Buffering = 2,
            Playing = 3,
            Paused = 4,
            Stopped = 5,
            Ended = 6,
            Error = 7
        }

        public static string VlcFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engines", "VLC");

        /// <summary>Set by tests or a settings page to use another VLC folder.</summary>
        public static string? FolderOverride { get; set; }

        private static string Folder => FolderOverride ?? VlcFolder;

        /// <summary>Whether Engines\VLC has what's needed (doesn't load anything).</summary>
        public static bool IsInstalled => FindProblems(Folder).Count == 0;

        /// <summary>
        /// What's missing or wrong in a VLC folder, each with the full path
        /// checked. Empty = looks complete. Also spots the usual mistakes:
        /// the files one folder too deep, an empty plugins folder, or
        /// plugins\plugins.
        /// </summary>
        public static List<string> FindProblems(string folder)
        {
            var problems = new List<string>();
            if (!Directory.Exists(folder))
            {
                problems.Add($"The folder {folder} doesn't exist.");
                return problems;
            }
            foreach (var name in new[] { "libvlc.dll", "libvlccore.dll" })
                if (!File.Exists(Path.Combine(folder, name)))
                    problems.Add($"{name} is missing from {folder}.");

            string plugins = Path.Combine(folder, "plugins");
            if (!Directory.Exists(plugins))
                problems.Add($"The plugins folder is missing: {plugins}.");
            else if (Directory.Exists(Path.Combine(plugins, "plugins")))
                problems.Add($"The plugins were copied one level too deep ({plugins}\\plugins) - move its contents up one level.");
            else if (!HasAnyDll(plugins))
                problems.Add($"The plugins folder {plugins} has no plugin DLLs in it (expected subfolders like codec, demux, video_output).");

            // Copied the whole "vlc-3.0.x" folder inside Engines\VLC?
            if (problems.Count > 0)
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(folder))
                        if (File.Exists(Path.Combine(sub, "libvlc.dll")))
                        {
                            problems.Add($"Found libvlc.dll one folder too deep, in {sub} - move that folder's contents up into {folder}.");
                            break;
                        }
                }
                catch (Exception ex) { Smart_Tools.WPF.Log.Debug("VLC folder check failed: " + ex.Message); }
            }
            return problems;
        }

        private static bool HasAnyDll(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// When the app's own Engines\VLC is incomplete, looks for one in the
        /// folders above it (bin\Debug\net8.0-windows -> the project folder).
        /// Finding it there means the files aren't being copied to the
        /// build output. Null when there's nothing to point at.
        /// </summary>
        public static string? FindProjectCopy(string? baseDir = null)
        {
            try
            {
                var dir = new DirectoryInfo(baseDir ?? AppDomain.CurrentDomain.BaseDirectory).Parent;
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "Engines", "VLC");
                    if (File.Exists(Path.Combine(candidate, "libvlc.dll"))) return candidate;
                }
            }
            catch (Exception ex) { Smart_Tools.WPF.Log.Debug("VLC folder search failed: " + ex.Message); }
            return null;
        }

        // =================================================================
        //  Loading the library from Engines\VLC
        // =================================================================

        private static readonly object LoadGate = new();
        private static bool _resolverSet;
        private static IntPtr _libvlc;
        private static IntPtr _instance;
        private static string? _loadError;

        /// <summary>VLC's own version string once loaded, e.g. "3.0.21 Vetinari".</summary>
        public static string? Version { get; private set; }

        /// <summary>How long the first start took (VLC scans its plugins the first time).</summary>
        public static TimeSpan StartTime { get; private set; }

        /// <summary>
        /// Where each start attempt is written, so "why isn't VLC used?"
        /// has an answer: %AppData%\Smart Tools\vlc.log.
        /// </summary>
        public static string LogPath => Path.Combine(
            AppPaths.DataRoot, "vlc.log");

        /// <summary>
        /// Loads libvlc once per app run and creates the shared VLC instance.
        /// Slow the first time (VLC scans its plugins) - call it off the UI
        /// thread. Returns null on success, or why VLC can't be used. Never
        /// throws.
        /// </summary>
        public static string? EnsureLoaded()
        {
            lock (LoadGate)
            {
                if (_instance != IntPtr.Zero) return null;
                if (_loadError != null) return _loadError;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string? result;
                try
                {
                    result = LoadCore();
                }
                catch (Exception ex)
                {
                    result = $"VLC couldn't be loaded ({ex.GetType().Name}): {ex.Message}";
                }
                StartTime = clock.Elapsed;
                _loadError = result;
                WriteLog(result);
                return result;
            }
        }

        private static string? LoadCore()
        {
            var problems = FindProblems(Folder);
            if (problems.Count > 0)
            {
                string text = "VLC isn't ready: " + string.Join(" ", problems);
                if (FolderOverride == null && FindProjectCopy() is string copy)
                    text += $" VLC is in {copy} but isn't copied to the build output - set its files to \"Copy if newer\" (see Engines\\VLC in the .csproj).";
                return text;
            }

            string libvlcPath = Path.Combine(Folder, "libvlc.dll");
            bool dll64 = Is64BitDll(libvlcPath);
            if (!Environment.Is64BitProcess && dll64)
                return "The VLC files are 64-bit but the app runs as 32-bit - use the win32 VLC zip, or build the app as x64.";
            if (Environment.Is64BitProcess && !dll64)
                return "The VLC files are 32-bit but the app runs as 64-bit - use the win64 VLC zip.";

            // libvlccore first by full path, so libvlc finds it (Windows wouldn't look in Engines\VLC otherwise).
            NativeLibrary.Load(Path.Combine(Folder, "libvlccore.dll"));
            _libvlc = NativeLibrary.Load(libvlcPath);
            if (!_resolverSet)
            {
                _resolverSet = true;
                try
                {
                    NativeLibrary.SetDllImportResolver(typeof(VlcPlayer).Assembly, Resolve);
                }
                catch (InvalidOperationException)
                {
                    // The app already set a resolver - fine: Windows reuses the libvlc.dll loaded above by name.
                }
            }

            // Only VLC 3.x: VLC 4 changed several of the calls declared below.
            IntPtr v = Native.libvlc_get_version();
            Version = v == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(v);
            if (Version != null && !Version.StartsWith("3.", StringComparison.Ordinal))
                return $"This is VLC {Version} - Video Replay needs the VLC 3.0.x files (VLC 4 changed its library).";

            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(Folder, "plugins"));

            string[] args =
            {
                "--no-video-title-show",   // no file name flashing over the picture
                "--no-osd",
                "--no-snapshot-preview",
                "--no-stats",
                "--quiet",
                "--no-lua",
                "--avcodec-hw=any",         // hardware decoding where the PC has it
            };
            _instance = NewInstance(args);
            if (_instance == IntPtr.Zero)
                return "VLC didn't start: " + (LastError() ?? "unknown error") + ". Check the plugins folder is complete.";
            return null;
        }

        /// <summary>
        /// libvlc_new takes a C array of UTF-8 strings. .NET can't marshal a
        /// string[] as UTF-8 by itself (MarshalDirectiveException), so the
        /// array is built by hand and freed afterwards - VLC copies the
        /// options while it starts.
        /// </summary>
        internal static IntPtr NewInstance(string[] args)
        {
            var argv = new IntPtr[args.Length];
            try
            {
                for (int i = 0; i < args.Length; i++)
                    argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                return Native.libvlc_new(argv.Length, argv);
            }
            finally
            {
                foreach (var p in argv)
                    if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
        }

        private static void WriteLog(string? result)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                string line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {(result == null ? "OK" : "FAILED")}  " +
                    $"folder={Folder}  process={(Environment.Is64BitProcess ? "64" : "32")}-bit  " +
                    $"version={Version ?? "-"}  took={StartTime.TotalSeconds:0.0}s" +
                    (result == null ? "" : "  why=" + result) + Environment.NewLine;
                File.AppendAllText(LogPath, line);
            }
            catch (Exception) { }   // logging must never break playback
        }

        /// <summary>Why VLC can't be used this run (null = fine, or not tried yet).</summary>
        public static string? LoadError => _loadError;

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
            name == Native.Lib && _libvlc != IntPtr.Zero ? _libvlc : IntPtr.Zero;

        /// <summary>Reads the PE header - a wrong-bitness DLL otherwise fails with a cryptic BadImageFormatException.</summary>
        private static bool Is64BitDll(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs);
                fs.Position = 0x3C;
                fs.Position = br.ReadInt32() + 4;
                return br.ReadUInt16() is 0x8664 or 0xAA64;   // x64 or ARM64
            }
            catch (Exception)
            {
                return Environment.Is64BitProcess;   // can't tell - let the loader decide
            }
        }

        private static string? LastError()
        {
            IntPtr p = Native.libvlc_errmsg();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }

        // =================================================================
        //  Shutting VLC down
        // =================================================================

        /// <summary>
        /// Releases the shared VLC instance at app exit, on its own thread,
        /// giving up after <paramref name="timeout"/>. Call after every
        /// player is closed. False = VLC didn't let go in time.
        /// </summary>
        public static bool Shutdown(TimeSpan timeout)
        {
            IntPtr instance;
            // VLC may still be starting (its first plugin scan holds this lock) - don't wait for it past the timeout.
            if (!Monitor.TryEnter(LoadGate, timeout))
            {
                Log("VLC was still starting at exit.");
                return false;
            }
            try
            {
                instance = _instance;
                _instance = IntPtr.Zero;
                _loadError ??= "VLC was shut down.";
            }
            finally
            {
                Monitor.Exit(LoadGate);
            }
            if (instance == IntPtr.Zero) return true;
            bool done = RunWithTimeout(() => Native.libvlc_release(instance), timeout, "VLC release");
            if (!done) Log($"VLC didn't release within {timeout.TotalSeconds:0.#} s at exit.");
            return done;
        }

        /// <summary>Runs <paramref name="work"/> on a background thread and waits at most <paramref name="timeout"/> (the wait keeps Windows messages flowing).</summary>
        private static bool RunWithTimeout(Action work, TimeSpan timeout, string name)
        {
            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception) { /* nothing useful to do at exit */ }
            })
            { IsBackground = true, Name = name };
            thread.Start();
            return thread.Join(timeout);
        }

        /// <summary>Adds a line to vlc.log.</summary>
        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
            catch (Exception) { }
            Smart_Tools.WPF.Log.Info("[vlc.log] " + text);   // same line in the app-wide log, next to what the tools were doing
        }

        // =================================================================
        //  One player
        //
        //  Every command that can wait (open, play, stop, seek...) runs on
        //  this player's own command thread, never the UI thread. VLC's
        //  stop waits for its video thread, and on Windows that thread sends
        //  messages to the window it draws in - which belongs to the UI
        //  thread. A stop called from the UI thread therefore waited forever
        //  (closing the app while a clip played left it running). While a
        //  command is running, the getters answer from the last known values
        //  instead of calling into VLC, since some of them wait on a stop too.
        // =================================================================

        private IntPtr _player;
        private readonly BlockingCollection<Action> _commands = new();
        private readonly Thread _worker;
        private int _pending;
        private string? _commandError;
        private bool _closed;

        // Last known values, served while a command is running.
        private VlcState _lastState = VlcState.Idle;
        private long _lastTime = -1;
        private long _lastLength;

        /// <summary>Call <see cref="EnsureLoaded"/> first.</summary>
        public VlcPlayer()
        {
            if (_instance == IntPtr.Zero) throw new InvalidOperationException(_loadError ?? "VLC isn't loaded.");
            _player = Native.libvlc_media_player_new(_instance);
            if (_player == IntPtr.Zero) throw new InvalidOperationException("VLC couldn't create a player: " + LastError());
            _worker = new Thread(RunCommands) { IsBackground = true, Name = "VLC commands" };
            _worker.Start();
        }

        private bool Alive => _player != IntPtr.Zero && !_closed;

        /// <summary>True while a command is queued or running (the getters then return the last known values).</summary>
        public bool IsBusy => Volatile.Read(ref _pending) > 0;

        /// <summary>Why the last command failed (e.g. the file couldn't be opened), once; null when none did.</summary>
        public string? TakeCommandError() => Interlocked.Exchange(ref _commandError, null);

        private void RunCommands()
        {
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    command();
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _commandError, ex.Message);
                    Smart_Tools.WPF.Log.Warn("VLC command failed", ex);
                }
                finally
                {
                    // A libvlc call that blocks (stop waiting on the video thread) shows up here.
                    double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (ms >= 1000) Smart_Tools.WPF.Log.Warn($"VLC command took {ms:0} ms (queued behind it: {Volatile.Read(ref _pending) - 1})");
                    Interlocked.Decrement(ref _pending);
                }
            }
        }

        private void Post(Action<IntPtr> command)
        {
            if (!Alive) return;
            IntPtr player = _player;
            Interlocked.Increment(ref _pending);
            try
            {
                _commands.Add(() => command(player));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pending);   // closed meanwhile
            }
        }

        /// <summary>
        /// Waits until the queued commands have run, at most
        /// <paramref name="timeout"/>. Used before VLC's window is destroyed,
        /// so VLC isn't still drawing into it. The wait keeps Windows
        /// messages flowing, so it can't lock up like a direct stop did.
        /// </summary>
        public bool WaitForIdle(TimeSpan timeout)
        {
            if (!Alive) return true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var done = new ManualResetEvent(false);
            Post(_ =>
            {
                try { done.Set(); }
                catch (ObjectDisposedException) { }   // the wait already gave up
            });
            if (!done.WaitOne(timeout))
            {
                Smart_Tools.WPF.Log.Warn($"VLC player still busy after {timeout.TotalSeconds:0.#} s (queued commands: {Volatile.Read(ref _pending)})");
                return false;
            }
            // The marker has run; its bookkeeping finishes a moment later.
            var left = timeout - clock.Elapsed;
            return SpinWait.SpinUntil(() => !IsBusy, left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        /// <summary>The native window VLC draws into. Mouse and keys go to the app, not VLC.</summary>
        public void SetWindow(IntPtr hwnd) => Post(p =>
        {
            Native.libvlc_media_player_set_hwnd(p, hwnd);
            Native.libvlc_video_set_mouse_input(p, 0);
            Native.libvlc_video_set_key_input(p, 0);
        });

        /// <summary>Loads a file and starts playing it. A failure shows up in <see cref="TakeCommandError"/>.</summary>
        public void Open(string path)
        {
            if (!Alive) throw new ObjectDisposedException(nameof(VlcPlayer));
            Smart_Tools.WPF.Log.Info($"VLC open (queued commands: {Volatile.Read(ref _pending)}): {path}");
            _lastState = VlcState.Opening;
            _lastTime = 0;
            _lastLength = 0;
            Post(p =>
            {
                Native.libvlc_media_player_stop(p);
                _savedAudioTrack = int.MinValue; _trackIsOff = false;   // a track id from the previous file means nothing for this one
                IntPtr media = Native.libvlc_media_new_path(_instance, path);
                if (media == IntPtr.Zero) throw new InvalidOperationException("VLC couldn't open the file: " + (LastError() ?? "unknown error"));
                Native.libvlc_media_player_set_media(p, media);
                Native.libvlc_media_release(media);   // the player keeps its own reference
                if (Native.libvlc_media_player_play(p) != 0)
                    throw new InvalidOperationException("VLC couldn't play the file: " + (LastError() ?? "unknown error"));
            });
        }

        /// <summary>Plays, resumes after a pause, or starts again after the end.</summary>
        public void Play() => Post(p =>
        {
            var state = (VlcState)Native.libvlc_media_player_get_state(p);
            if (state == VlcState.Paused)
            {
                Native.libvlc_media_player_set_pause(p, 0);
                return;
            }
            // After the end, VLC needs a stop before it can play again from the start.
            if (state is VlcState.Ended or VlcState.Stopped or VlcState.Error) Native.libvlc_media_player_stop(p);
            Native.libvlc_media_player_play(p);
        });

        public void Pause() => Post(p => Native.libvlc_media_player_set_pause(p, 1));

        public void Resume() => Post(p => Native.libvlc_media_player_set_pause(p, 0));

        /// <summary>Stops and lets go of the file (without waiting).</summary>
        public void Stop()
        {
            if (!Alive) return;
            _lastState = VlcState.Stopped;
            _lastTime = -1;
            _lastLength = 0;
            Post(p =>
            {
                Native.libvlc_media_player_stop(p);
                Native.libvlc_media_player_set_media(p, IntPtr.Zero);
            });
        }

        public VlcState State
        {
            get
            {
                if (!Alive) return VlcState.Stopped;
                if (IsBusy) return _lastState;
                return _lastState = (VlcState)Native.libvlc_media_player_get_state(_player);
            }
        }

        /// <summary>Position in ms (-1 when nothing is loaded). Setting it seeks.</summary>
        public long TimeMs
        {
            get
            {
                if (!Alive) return -1;
                if (IsBusy) return _lastTime;
                return _lastTime = Native.libvlc_media_player_get_time(_player);
            }
            set
            {
                long ms = Math.Max(0, value);
                _lastTime = ms;   // the slider shows the new spot straight away
                Post(p => Native.libvlc_media_player_set_time(p, ms));
            }
        }

        /// <summary>Length in ms (0 or -1 until known).</summary>
        public long LengthMs
        {
            get
            {
                if (!Alive) return 0;
                if (IsBusy) return _lastLength;
                return _lastLength = Native.libvlc_media_player_get_length(_player);
            }
        }

        public void SetRate(double rate) => Post(p => Native.libvlc_media_player_set_rate(p, (float)rate));

        /// <summary>0-1 (VLC takes 0-100).</summary>
        public void SetVolume(double volume)
        {
            int v = (int)Math.Round(Math.Clamp(volume, 0, 1) * 100);
            Post(p => Native.libvlc_audio_set_volume(p, v));
        }

        public void SetMute(bool mute) => Post(p => Native.libvlc_audio_set_mute(p, mute ? 1 : 0));

        private int _savedAudioTrack = int.MinValue;
        private bool _trackIsOff;

        /// <summary>
        /// Silences only this player by switching its audio track off. libvlc's mute/volume
        /// act on the app-wide Windows audio session, so they would silence every player.
        /// </summary>
        public void SetAudioTrackOff(bool off) => Post(p =>
        {
            if (off)
            {
                int current = Native.libvlc_audio_get_track(p);
                if (current >= 0) _savedAudioTrack = current;
                Native.libvlc_audio_set_track(p, -1);
                _trackIsOff = true;
            }
            else if (_trackIsOff)
            {
                _trackIsOff = false;
                int track = _savedAudioTrack != int.MinValue ? _savedAudioTrack : FirstAudioTrack(p);
                _savedAudioTrack = int.MinValue;
                if (track >= 0) Native.libvlc_audio_set_track(p, track);
            }
        });

        /// <summary>First real audio track of the current media (id >= 0), or -1. Used when nothing was saved before the track was switched off.</summary>
        private static int FirstAudioTrack(IntPtr p)
        {
            IntPtr head = Native.libvlc_audio_get_track_description(p);
            if (head == IntPtr.Zero) return -1;
            try
            {
                for (IntPtr node = head; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size == 8 ? 16 : 8))
                {
                    int id = Marshal.ReadInt32(node);   // libvlc_track_description_t: int i_id; char* psz_name; next*
                    if (id >= 0) return id;
                }
                return -1;
            }
            finally { Native.libvlc_track_description_list_release(head); }
        }

        public static string? ErrorMessage => _instance == IntPtr.Zero ? null : LastError();

        /// <summary>
        /// Stops and frees the player on its command thread, waiting at most
        /// <paramref name="timeout"/>. False = VLC didn't finish in time (the
        /// thread is left behind; it's a background thread, so it can't keep
        /// the app running by itself).
        /// </summary>
        public bool Close(TimeSpan timeout)
        {
            if (_closed || _player == IntPtr.Zero) return true;
            IntPtr player = _player;
            Interlocked.Increment(ref _pending);
            _commands.Add(() =>
            {
                Native.libvlc_media_player_stop(player);
                Native.libvlc_media_player_release(player);
            });
            _commands.CompleteAdding();
            _closed = true;
            _player = IntPtr.Zero;
            bool done = _worker.Join(timeout);
            if (!done) Log($"The VLC player didn't stop within {timeout.TotalSeconds:0.#} s.");
            return done;
        }

        public void Dispose() => Close(TimeSpan.FromSeconds(3));

        // =================================================================
        //  libvlc 3.x functions used
        // =================================================================

        private static class Native
        {
            public const string Lib = "libvlc";

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr libvlc_new(int argc, IntPtr[] argv);   // argv = UTF-8 strings, see NewInstance

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_release(IntPtr instance);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr libvlc_errmsg();

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr libvlc_get_version();

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr libvlc_media_new_path(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_media_release(IntPtr media);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr libvlc_media_player_new(IntPtr instance);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_media_player_release(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_media_player_set_media(IntPtr player, IntPtr media);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int libvlc_media_player_play(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_media_player_set_pause(IntPtr player, int doPause);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_media_player_stop(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_media_player_set_hwnd(IntPtr player, IntPtr hwnd);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int libvlc_media_player_get_state(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern long libvlc_media_player_get_time(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_media_player_set_time(IntPtr player, long timeMs);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern long libvlc_media_player_get_length(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int libvlc_media_player_set_rate(IntPtr player, float rate);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int libvlc_audio_set_volume(IntPtr player, int volume);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_audio_set_mute(IntPtr player, int mute);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int libvlc_audio_get_track(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int libvlc_audio_set_track(IntPtr player, int track);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr libvlc_audio_get_track_description(IntPtr player);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_track_description_list_release(IntPtr list);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_video_set_mouse_input(IntPtr player, uint on);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void libvlc_video_set_key_input(IntPtr player, uint on);
        }
    }
}