using Smart_Tools.WPF.Controllers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Smart_Tools.WPF.Views
{
    // =====================================================================
    //  Video Replay - the two playback engines behind one interface.
    //  VLC (Engines\VLC) plays everything directly, WebM included.
    //  MediaElement (Windows' own player) is the fallback when VLC isn't
    //  there; formats it can't open get a preview copy first.
    // =====================================================================

    /// <summary>What the view needs from a player. Events are raised on the UI thread.</summary>
    internal interface IReplayBackend : IDisposable
    {
        /// <summary>"VLC" or "Windows" (shown in the tooltip on the stage).</summary>
        string Name { get; }

        /// <summary>True when every format plays directly (no preview copies).</summary>
        bool PlaysEverything { get; }

        /// <summary>The element that shows the picture.</summary>
        FrameworkElement Surface { get; }

        bool HasMedia { get; }
        TimeSpan Position { get; set; }
        TimeSpan? Duration { get; }

        void Open(string path);
        void Play();
        void Pause();
        void Stop();
        void SetSpeed(double speed);
        void SetVolume(double volume);
        void SetMute(bool mute);

        /// <summary>Called by the view's 250 ms timer (VLC reports its state this way).</summary>
        void Poll();

        event Action? Opened;
        event Action? Ended;
        event Action<string>? Failed;
    }

    // =====================================================================
    //  Windows' MediaElement
    // =====================================================================

    internal sealed class MediaElementBackend : IReplayBackend
    {
        private readonly MediaElement _player;
        private double _speed = 1;

        public MediaElementBackend(MediaElement player)
        {
            _player = player;
            _player.MediaOpened += (_, _) =>
            {
                _player.SpeedRatio = _speed;   // MediaElement forgets the speed when the source changes
                Opened?.Invoke();
            };
            _player.MediaEnded += (_, _) => Ended?.Invoke();
            _player.MediaFailed += (_, e) => Failed?.Invoke(e.ErrorException?.Message ?? "Windows couldn't play it.");
        }

        public string Name => "Windows";
        public bool PlaysEverything => false;
        public FrameworkElement Surface => _player;
        public bool HasMedia => _player.Source != null;

        public TimeSpan Position
        {
            get => _player.Position;
            set => _player.Position = value;
        }

        public TimeSpan? Duration => _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : null;

        public void Open(string path)
        {
            _player.Source = new Uri(path);
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }

        public void Play() => _player.Play();
        public void Pause() => _player.Pause();

        public void Stop()
        {
            _player.Stop();
            _player.Source = null;   // releases the file
        }

        public void SetSpeed(double speed)
        {
            _speed = speed;
            _player.SpeedRatio = speed;
        }

        public void SetVolume(double volume) => _player.Volume = volume;
        public void SetMute(bool mute) => _player.IsMuted = mute;
        public void Poll() { }

        public event Action? Opened;
        public event Action? Ended;
        public event Action<string>? Failed;

        public void Dispose() => Stop();
    }

    // =====================================================================
    //  VLC
    // =====================================================================

    internal sealed class VlcBackend : IReplayBackend
    {
        private readonly VlcPlayer _vlc;
        private readonly VlcVideoHost _host;
        private bool _hasMedia;
        private bool _openedRaised;
        private bool _endRaised;
        private double _speed = 1, _volume = 1;
        private bool _mute;
        private long? _pendingSeekMs;
        private int _playingPolls;

        // Right after Open, VLC 3 still answers with the PREVIOUS clip's state (Ended)
        // and time until its new input starts. Taking that for the new clip made good
        // clips "end" at once and cascade through the playlist. So state and time are
        // only trusted once this clip has been seen starting.
        private bool _mediaStarted;
        private bool _staleLogged;
        private DateTime _openedAtUtc;
        private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(3);

        public VlcBackend(VlcVideoHost host)
        {
            _host = host;
            _vlc = new VlcPlayer();
            if (host.Handle != IntPtr.Zero) _vlc.SetWindow(host.Handle);
            host.HandleCreated += h => _vlc.SetWindow(h);
        }

        public string Name => "VLC";
        public bool PlaysEverything => true;
        public FrameworkElement Surface => _host;
        public bool HasMedia => _hasMedia;

        public TimeSpan Position
        {
            get
            {
                if (!_mediaStarted) return TimeSpan.Zero;   // a time VLC still holds from the previous clip
                long ms = _vlc.TimeMs;
                return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
            }
            set
            {
                long ms = (long)value.TotalMilliseconds;
                if (!_openedRaised) _pendingSeekMs = ms;   // VLC ignores seeks until the file is open
                else _vlc.TimeMs = ms;
            }
        }

        public TimeSpan? Duration
        {
            get
            {
                long ms = _vlc.LengthMs;
                return ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;
            }
        }

        public void Open(string path)
        {
            _openedRaised = false;
            _endRaised = false;
            _pendingSeekMs = null;
            _playingPolls = 0;
            _mediaStarted = false;
            _staleLogged = false;
            _openedAtUtc = DateTime.UtcNow;
            _hasMedia = true;
            _vlc.Open(path);
        }

        public void Play()
        {
            _endRaised = false;
            if (_vlc.State is VlcPlayer.VlcState.Ended or VlcPlayer.VlcState.Stopped)
            {
                _openedRaised = false;
                _playingPolls = 0;
                _mediaStarted = false;   // starting again after the end: the old "Ended" is stale until it restarts
                _staleLogged = false;
                _openedAtUtc = DateTime.UtcNow;
            }
            _vlc.Play();   // resumes, or starts again after the end (decided on VLC's command thread)
        }

        public void Pause() => _vlc.Pause();

        public void Stop()
        {
            _hasMedia = false;
            _openedRaised = false;
            _mediaStarted = false;
            _vlc.Stop();
        }

        public void SetSpeed(double speed)
        {
            _speed = speed;
            _vlc.SetRate(speed);
        }

        public void SetVolume(double volume)
        {
            _volume = volume;
            _vlc.SetVolume(volume);
        }

        public void SetMute(bool mute)
        {
            _mute = mute;
            _vlc.SetMute(mute);
        }

        /// <summary>
        /// Turns VLC's state into Opened / Ended / Failed, once each. Opened
        /// waits for the length, or 1 s of playing - WebM recorded in a
        /// browser often has no length stored at all.
        /// </summary>
        public void Poll()
        {
            if (!_hasMedia) return;
            if (_vlc.TakeCommandError() is string error)
            {
                _hasMedia = false;
                Failed?.Invoke(error);
                return;
            }
            if (_vlc.IsBusy) return;   // a command is still running - judge the state afterwards
            var state = _vlc.State;
            if (state is VlcPlayer.VlcState.Opening or VlcPlayer.VlcState.Buffering or VlcPlayer.VlcState.Playing or VlcPlayer.VlcState.Paused)
                _mediaStarted = true;
            else if (!_mediaStarted && (state is VlcPlayer.VlcState.Ended or VlcPlayer.VlcState.Error))
            {
                // Not started yet, so this is most likely the previous clip's leftover state.
                if (DateTime.UtcNow - _openedAtUtc < StartGrace)
                {
                    if (!_staleLogged)
                    {
                        _staleLogged = true;
                        Smart_Tools.WPF.Log.Info($"VLC still reports {state} from the previous clip - ignoring it until the new clip starts.");
                    }
                    return;
                }
                if (state == VlcPlayer.VlcState.Ended && _vlc.LengthMs > 0)
                {
                    _mediaStarted = true;   // a very short clip that ended between two polls - a real end
                }
                else
                {
                    // Three seconds and it never started: a real failure, not a leftover.
                    _hasMedia = false;
                    Failed?.Invoke(state == VlcPlayer.VlcState.Error
                        ? VlcPlayer.ErrorMessage ?? "VLC couldn't play it."
                        : "VLC never started this file.");
                    return;
                }
            }
            if (state == VlcPlayer.VlcState.Playing) _playingPolls++;
            switch (state)
            {
                case VlcPlayer.VlcState.Playing or VlcPlayer.VlcState.Paused when !_openedRaised && (_vlc.LengthMs > 0 || _playingPolls >= 4):
                    _openedRaised = true;
                    // Audio settings only stick once VLC's audio output exists.
                    _vlc.SetRate(_speed);
                    _vlc.SetVolume(_volume);
                    _vlc.SetMute(_mute);
                    if (_pendingSeekMs is long ms)
                    {
                        _vlc.TimeMs = ms;
                        _pendingSeekMs = null;
                    }
                    Opened?.Invoke();
                    break;
                case VlcPlayer.VlcState.Ended when !_endRaised:
                    _endRaised = true;
                    Ended?.Invoke();
                    break;
                case VlcPlayer.VlcState.Error:
                    _hasMedia = false;
                    Failed?.Invoke(VlcPlayer.ErrorMessage ?? "VLC couldn't play it.");
                    break;
            }
        }

        public event Action? Opened;
        public event Action? Ended;
        public event Action<string>? Failed;

        /// <summary>Waits (at most <paramref name="timeout"/>) for queued commands such as a stop to finish.</summary>
        public bool WaitForIdle(TimeSpan timeout) => _vlc.WaitForIdle(timeout);

        /// <summary>Stops and frees the player, waiting at most <paramref name="timeout"/>. False = VLC was stuck.</summary>
        public bool Close(TimeSpan timeout)
        {
            _hasMedia = false;
            return _vlc.Close(timeout);
        }

        public void Dispose() => _vlc.Dispose();
    }

    /// <summary>
    /// The last step of shutting VLC down at exit, shared by every view that
    /// uses it. If VLC didn't let go in time, the app ends its own process
    /// when it exits, so a stuck VLC thread can't keep it running.
    /// </summary>
    internal static class VlcShutdown
    {
        private static bool _armed;

        public static void Finish(bool clean)
        {
            if (clean || _armed) return;
            _armed = true;
            VlcPlayer.Log("VLC didn't shut down cleanly - ending the app process at exit.");
            if (System.Windows.Application.Current is { } app)
                app.Exit += (_, _) => System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
    }

    /// <summary>
    /// A plain black native window for VLC to draw into. WPF content can't
    /// sit on top of it (the "airspace" rule), so the view keeps its banners
    /// outside the video. Double-click and right-click are passed back to
    /// WPF as events, since VLC's window doesn't take mouse input.
    /// </summary>
    internal sealed class VlcVideoHost : HwndHost
    {
        public event Action<IntPtr>? HandleCreated;

        /// <summary>Raised just before the window goes (leaving the page, closing) - stop VLC drawing into it first.</summary>
        public event Action? Destroying;
        public event Action? DoubleClicked;
        public event Action? RightClicked;
        public event Action? Clicked;

        private const string ClassName = "SmartToolsVlcSurface";
        private static readonly WndProcDelegate DefProc = DefWindowProcW;   // kept alive for the class
        private static bool _registered;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            if (!_registered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = CS_DBLCLKS,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DefProc),
                    hInstance = GetModuleHandleW(null),
                    hbrBackground = GetStockObject(BLACK_BRUSH),
                    lpszClassName = ClassName,
                };
                RegisterClassExW(ref wc);   // fails harmlessly if already registered
                _registered = true;
            }

            IntPtr hwnd = CreateWindowExW(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Couldn't create the video window (error " + Marshal.GetLastWin32Error() + ").");
            HandleCreated?.Invoke(hwnd);
            return new HandleRef(this, hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Destroying?.Invoke();
            DestroyWindow(hwnd.Handle);
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_LBUTTONDOWN:
                    Clicked?.Invoke();
                    break;
                case WM_LBUTTONDBLCLK:
                    DoubleClicked?.Invoke();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    RightClicked?.Invoke();
                    handled = true;
                    break;
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        // ---------------- Win32 ----------------

        private const uint CS_DBLCLKS = 0x0008;
        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
        private const int BLACK_BRUSH = 4;
        private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? name);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int obj);
    }
}
