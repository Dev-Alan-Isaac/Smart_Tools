using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Reads the keyboard and mouse straight from Windows' Raw Input, on a thread of its own with its own message window.
    /// Raw Input gives every report from the device (not the pointer position Windows works out from them), identifies keys by
    /// scan code, and sees them even when WPF would swallow them. Timing each report on a thread that does nothing else keeps the
    /// polling-rate figures honest when the UI is busy. Only input that goes to this program is recorded.
    /// </summary>
    public sealed class RawInputListener : IDisposable
    {
        // ---- native ---------------------------------------------------------------

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint Size, Style;
            public IntPtr WndProc;
            public int ClsExtra, WndExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
            public IntPtr IconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage, Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint Type, Size;
            public IntPtr Device, WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawMouse
        {
            public ushort Flags;
            public uint Buttons;      // low word: button flags, high word: wheel data
            public uint RawButtons;
            public int LastX, LastY;
            public uint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawKeyboard
        {
            public ushort MakeCode, Flags, Reserved, VKey;
            public uint Message, Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam, LParam;
            public uint Time;
            public int X, Y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string className, IntPtr instance);
        [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
        [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

        private const uint WmInput = 0x00FF, WmClose = 0x0010;
        private const uint RidInput = 0x10000003;
        private const uint RidevInputSink = 0x00000100;
        private const ushort KeyBreak = 0x01, KeyE0 = 0x02, KeyE1 = 0x04;
        private const ushort VkPause = 0x13;
        private static readonly IntPtr HwndMessage = new(-3);

        // ---- state ------------------------------------------------------------------

        private const int RingSize = 4096;

        private readonly double _frequency = Stopwatch.Frequency;
        private readonly long _origin = Stopwatch.GetTimestamp();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly WndProc _proc;
        private readonly string _className = "SmartToolsRawInput" + Guid.NewGuid().ToString("N");
        private readonly IntPtr _buffer = Marshal.AllocHGlobal(512);
        private readonly object _ringLock = new();
        private readonly double[] _moveTimes = new double[RingSize];
        private IntPtr _hwnd;
        private Exception? _startupError;
        private int _ringCount;
        private int _ringHead;
        private long _sumDx, _sumDy;
        private long _movedDx, _movedDy;
        private long _frameDx, _frameDy;
        private long _foregroundCheckedAt;
        private bool _isForeground;
        private volatile bool _enabled;
        private int _disposed;

        /// <summary>Key presses and releases, oldest first. Drained by the UI.</summary>
        public ConcurrentQueue<RawKeyEvent> KeyEvents { get; } = new();

        /// <summary>Mouse button and wheel reports, oldest first. Drained by the UI.</summary>
        public ConcurrentQueue<RawMouseEvent> ButtonEvents { get; } = new();

        /// <summary>Only record input while a window of this program has the focus. Turned off only by tests that feed input from a console.</summary>
        public bool RequireForeground { get; set; } = true;

        /// <summary>Nothing is recorded while this is false (the page isn't showing).</summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public RawInputListener()
        {
            _proc = Proc;
            _thread = new Thread(Run) { IsBackground = true, Name = "Raw input", Priority = ThreadPriority.Highest };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
            if (_startupError != null) throw new InvalidOperationException(_startupError.Message, _startupError);
        }

        /// <summary>Seconds on the listener's own steady clock (the same clock the events use).</summary>
        public double Now => (Stopwatch.GetTimestamp() - _origin) / _frequency;

        // ---- the message thread ------------------------------------------------------

        private void Run()
        {
            try
            {
                var instance = GetModuleHandleW(null);
                var cls = new WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<WndClassEx>(),
                    WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    Instance = instance,
                    ClassName = _className,
                };
                if (RegisterClassExW(ref cls) == 0) throw new InvalidOperationException("RegisterClass failed (" + Marshal.GetLastWin32Error() + ")");

                _hwnd = CreateWindowExW(0, _className, "Smart Tools raw input", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindow failed (" + Marshal.GetLastWin32Error() + ")");

                var devices = new[]
                {
                    new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink, Target = _hwnd },   // keyboard
                    new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevInputSink, Target = _hwnd },   // mouse
                };
                if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
                    throw new InvalidOperationException("RegisterRawInputDevices failed (" + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex)
            {
                _startupError = ex;
                _ready.Set();
                return;
            }

            _ready.Set();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            UnregisterClassW(_className, GetModuleHandleW(null));
        }

        private IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmInput)
            {
                double now = Now;
                if (_enabled && (!RequireForeground || IsOurForeground())) Handle(lParam, now);
                return DefWindowProcW(hWnd, msg, wParam, lParam);   // lets Windows clean up the report
            }
            if (msg == WmClose)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        /// <summary>True while a window of this program has the keyboard focus. Checked at most every 100 ms: it is the costly part at high mouse rates.</summary>
        private bool IsOurForeground()
        {
            long tick = Environment.TickCount64;
            if (tick - _foregroundCheckedAt < 100) return _isForeground;
            _foregroundCheckedAt = tick;
            var fg = GetForegroundWindow();
            _isForeground = fg != IntPtr.Zero && GetWindowThreadProcessId(fg, out uint pid) != 0 && pid == (uint)Environment.ProcessId;
            return _isForeground;
        }

        private void Handle(IntPtr lParam, double time)
        {
            uint size = 512;
            uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            if (GetRawInputData(lParam, RidInput, _buffer, ref size, headerSize) == uint.MaxValue) return;

            var header = Marshal.PtrToStructure<RawInputHeader>(_buffer);
            var data = _buffer + (int)headerSize;

            if (header.Type == 1)
            {
                var k = Marshal.PtrToStructure<RawKeyboard>(data);
                HandleKey(k, time);
            }
            else if (header.Type == 0)
            {
                var m = Marshal.PtrToStructure<RawMouse>(data);
                HandleMouse(m, time);
            }
        }

        private void HandleKey(RawKeyboard k, double time)
        {
            if (k.VKey == 0xFF) return;   // keyboard overrun / filler
            bool down = (k.Flags & KeyBreak) == 0;

            string id;
            if (k.VKey == VkPause || (k.Flags & KeyE1) != 0)
            {
                if (k.VKey != VkPause) return;   // the other half of the Pause sequence
                id = "PAUSE";
            }
            else
            {
                id = KeyboardMouseTestManager.KeyId(k.MakeCode, (k.Flags & KeyE0) != 0);
                if (KeyboardMouseTestManager.IsPhantomKey(id)) return;
            }
            KeyEvents.Enqueue(new RawKeyEvent(time, id, down));
        }

        private void HandleMouse(RawMouse m, double time)
        {
            bool absolute = (m.Flags & 0x0001) != 0;
            var buttons = (MouseButtonFlags)(m.Buttons & 0xFFFF);
            int wheel = (buttons & (MouseButtonFlags.Wheel | MouseButtonFlags.HWheel)) != 0 ? (short)(m.Buttons >> 16) : 0;
            var clickFlags = buttons & ~(MouseButtonFlags.Wheel | MouseButtonFlags.HWheel);

            if (!absolute && (m.LastX != 0 || m.LastY != 0))
            {
                lock (_ringLock)
                {
                    _moveTimes[_ringHead] = time;
                    _ringHead = (_ringHead + 1) % RingSize;
                    if (_ringCount < RingSize) _ringCount++;
                }
                Interlocked.Add(ref _sumDx, m.LastX);
                Interlocked.Add(ref _sumDy, m.LastY);
                Interlocked.Add(ref _movedDx, Math.Abs(m.LastX));
                Interlocked.Add(ref _movedDy, Math.Abs(m.LastY));
                Interlocked.Add(ref _frameDx, m.LastX);
                Interlocked.Add(ref _frameDy, m.LastY);
            }

            if (clickFlags != 0 || wheel != 0)
                ButtonEvents.Enqueue(new RawMouseEvent(time, m.LastX, m.LastY, buttons, wheel, absolute));
        }

        // ---- for the UI --------------------------------------------------------------

        /// <summary>The arrival times of the latest movement reports (up to 4096), oldest first.</summary>
        public double[] SnapshotMoveTimes()
        {
            lock (_ringLock)
            {
                var copy = new double[_ringCount];
                int start = (_ringHead - _ringCount + RingSize) % RingSize;
                for (int i = 0; i < _ringCount; i++) copy[i] = _moveTimes[(start + i) % RingSize];
                return copy;
            }
        }

        /// <summary>Forgets the stored movement reports (to start a clean polling measurement).</summary>
        public void ClearMoveTimes()
        {
            lock (_ringLock)
            {
                _ringCount = 0;
                _ringHead = 0;
            }
        }

        /// <summary>The total counts moved (absolute, both axes) since the last call; for "counts per second".</summary>
        public long TakeMovedCounts() => Interlocked.Exchange(ref _movedDx, 0) + Interlocked.Exchange(ref _movedDy, 0);

        /// <summary>The signed counts moved since the last call (for the movement indicator).</summary>
        public (long Dx, long Dy) TakeFrameDelta() => (Interlocked.Exchange(ref _frameDx, 0), Interlocked.Exchange(ref _frameDy, 0));

        /// <summary>Starts adding up the signed movement (for the DPI measurement).</summary>
        public void BeginDistance()
        {
            Interlocked.Exchange(ref _sumDx, 0);
            Interlocked.Exchange(ref _sumDy, 0);
        }

        /// <summary>The signed counts moved since <see cref="BeginDistance"/>.</summary>
        public (long Dx, long Dy) ReadDistance() => (Interlocked.Read(ref _sumDx), Interlocked.Read(ref _sumDy));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _enabled = false;
            if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            Marshal.FreeHGlobal(_buffer);
            _ready.Dispose();
        }
    }
}
