using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The Windows-only half of the Game Controller Tester: XInput (Xbox-style pads) and the legacy
    /// joystick API (every other HID game device, whether it is on USB or Bluetooth), plus a fast poller.
    /// </summary>
    public static partial class GameControllerManager
    {
        // =================================================================
        //  Native
        // =================================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputVibration
        {
            public ushort LeftMotor, RightMotor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputCapabilities
        {
            public byte Type;
            public byte SubType;
            public ushort Flags;
            public XInputGamepad Gamepad;
            public XInputVibration Vibration;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputBattery
        {
            public byte Type;
            public byte Level;
        }

        [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XInputState state);
        [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint index, ref XInputVibration vibration);
        [DllImport("xinput1_4.dll")] private static extern uint XInputGetCapabilities(uint index, uint flags, out XInputCapabilities caps);
        [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte deviceType, out XInputBattery battery);

        [StructLayout(LayoutKind.Sequential)]
        private struct JoyInfoEx
        {
            public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct JoyCaps
        {
            public ushort Mid, Pid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
            public uint XMin, XMax, YMin, YMax, ZMin, ZMax, NumButtons, PeriodMin, PeriodMax,
                RMin, RMax, UMin, UMax, VMin, VMax, Caps, MaxAxes, NumAxes, MaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxD;
        }

        [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
        [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint id, ref JoyInfoEx info);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint joyGetDevCapsW(UIntPtr id, ref JoyCaps caps, uint size);
        private const uint JoyReturnAll = 0xFF;
        private const uint ErrorSuccess = 0;

        // Button bits as XInput reports them, in the order the tester shows them.
        public static readonly (int Bit, string Label)[] XInputButtons =
        {
            (12, "A"), (13, "B"), (14, "X"), (15, "Y"), (8, "LB"), (9, "RB"), (5, "Back"), (4, "Start"), (6, "L3"), (7, "R3"),
            (0, "↑"), (1, "↓"), (2, "←"), (3, "→"),
        };

        // =================================================================
        //  Finding controllers
        // =================================================================

        /// <summary>Every controller Windows reports right now: the four XInput slots, then the other game devices.</summary>
        public static List<ControllerInfo> ListControllers()
        {
            var list = new List<ControllerInfo>();

            for (uint slot = 0; slot < 4; slot++)
            {
                if (!TryXInputCaps(slot, out var caps)) continue;
                string kind = XInputKind(caps.SubType);
                bool wireless = (caps.Flags & 0x0002) != 0;
                int battery = BatteryPercent(slot);
                string detail = wireless ? "Wireless (XInput)" : "Wired or dongle (XInput)";
                list.Add(new ControllerInfo($"xi:{slot}", ControllerApi.XInput, (int)slot, $"{kind} {slot + 1}", detail, 6, 14, true, wireless, battery));
            }

            bool haveXInput = list.Count > 0;
            try
            {
                uint count = joyGetNumDevs();
                for (uint id = 0; id < count && id < 16; id++)
                {
                    var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnAll };
                    if (joyGetPosEx(id, ref info) != ErrorSuccess) continue;

                    var caps = new JoyCaps();
                    if (joyGetDevCapsW((UIntPtr)id, ref caps, (uint)Marshal.SizeOf<JoyCaps>()) != ErrorSuccess) continue;

                    string name = OemName(caps.RegKey) ?? caps.ProductName;
                    if (string.IsNullOrWhiteSpace(name)) name = $"Game controller {id + 1}";

                    // Windows lists Xbox pads here too; they are already shown through XInput.
                    if (haveXInput && (name.Contains("xbox", StringComparison.OrdinalIgnoreCase) || name.Contains("xinput", StringComparison.OrdinalIgnoreCase))) continue;

                    string detail = $"HID · VID_{caps.Mid:X4} PID_{caps.Pid:X4}";
                    list.Add(new ControllerInfo($"joy:{id}", ControllerApi.Joystick, (int)id, name, detail,
                        (int)caps.NumAxes, (int)caps.NumButtons, (caps.Caps & 0x10) != 0, false, -1));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Info($"Joystick API: {ex.Message}");
            }

            return list;
        }

        private static bool TryXInputCaps(uint slot, out XInputCapabilities caps)
        {
            caps = default;
            try
            {
                return XInputGetCapabilities(slot, 0, out caps) == ErrorSuccess;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static string XInputKind(byte subType) => subType switch
        {
            2 => "Racing wheel",
            3 => "Arcade stick",
            4 => "Flight stick",
            5 => "Dance pad",
            6 or 7 or 11 => "Guitar",
            8 => "Drum kit",
            19 => "Arcade pad",
            _ => "Xbox-style controller",
        };

        private static int BatteryPercent(uint slot)
        {
            try
            {
                if (XInputGetBatteryInformation(slot, 0, out var b) != ErrorSuccess) return -1;
                if (b.Type is 0 or 1 or 0xFF) return -1;   // none, wired or unknown
                return b.Level switch { 0 => 5, 1 => 30, 2 => 65, _ => 100 };
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return -1;
            }
        }

        private static string? OemName(string regKey)
        {
            if (string.IsNullOrEmpty(regKey)) return null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\" + regKey);
                return key?.GetValue("OEMName") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        // =================================================================
        //  Reading
        // =================================================================

        /// <summary>One reading of the controller, or <see cref="ControllerState.Disconnected"/>.</summary>
        public static ControllerState Read(ControllerInfo controller)
        {
            try
            {
                return controller.Api == ControllerApi.XInput ? ReadXInput((uint)controller.Slot) : ReadJoystick((uint)controller.Slot);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return ControllerState.Disconnected;
            }
        }

        private static ControllerState ReadXInput(uint slot)
        {
            if (XInputGetState(slot, out var s) != ErrorSuccess) return ControllerState.Disconnected;
            var g = s.Gamepad;
            var axes = new[] { NormalizeStick(g.ThumbLX), NormalizeStick(g.ThumbLY), NormalizeStick(g.ThumbRX), NormalizeStick(g.ThumbRY) };
            return new ControllerState(true, s.PacketNumber, axes, NormalizeTrigger(g.LeftTrigger), NormalizeTrigger(g.RightTrigger), g.Buttons, -1);
        }

        private static ControllerState ReadJoystick(uint id)
        {
            var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnAll };
            if (joyGetPosEx(id, ref info) != ErrorSuccess) return ControllerState.Disconnected;

            var caps = new JoyCaps();
            joyGetDevCapsW((UIntPtr)id, ref caps, (uint)Marshal.SizeOf<JoyCaps>());
            double Ax(uint raw, uint min, uint max) => NormalizeAxis(raw, min, max);

            var all = new[]
            {
                Ax(info.X, caps.XMin, caps.XMax), -Ax(info.Y, caps.YMin, caps.YMax),   // up is positive, like XInput
                Ax(info.Z, caps.ZMin, caps.ZMax), -Ax(info.R, caps.RMin, caps.RMax),
                Ax(info.U, caps.UMin, caps.UMax), -Ax(info.V, caps.VMin, caps.VMax),
            };
            int count = (int)Math.Clamp(caps.NumAxes, 2, 6);
            var axes = all.Take(count).ToArray();
            int pov = info.Pov is 0xFFFF or 0xFFFFFFFF ? -1 : (int)(info.Pov / 100);
            return new ControllerState(true, 0, axes, 0, 0, info.Buttons, pov);
        }

        /// <summary>Spins the two motors (0..1 each). Xbox-style pads only; a no-op elsewhere. Call with 0, 0 to stop.</summary>
        public static void SetVibration(ControllerInfo controller, double left, double right)
        {
            if (controller.Api != ControllerApi.XInput) return;
            var v = new XInputVibration
            {
                LeftMotor = (ushort)(Math.Clamp(left, 0, 1) * 65535),
                RightMotor = (ushort)(Math.Clamp(right, 0, 1) * 65535),
            };
            try
            {
                XInputSetState((uint)controller.Slot, ref v);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Info($"XInput: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads one controller about every millisecond on its own thread, so short movements and fast report rates
    /// aren't missed, and keeps the arrival time of every new report. The UI reads <see cref="Latest"/> on its own timer.
    /// While recording, every report (and at least one reading every 10 ms) is kept for the rest, range and trigger tests.
    /// </summary>
    public sealed class ControllerPoller : IDisposable
    {
        private const double MinRecordGap = 0.010;

        private readonly ControllerInfo _controller;
        private readonly Thread _thread;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _lock = new();
        private readonly List<double> _arrivals = new();
        private List<TimedControllerState>? _recording;
        private volatile bool _stop;
        private volatile ControllerState _latest = ControllerState.Disconnected;

        public ControllerPoller(ControllerInfo controller)
        {
            _controller = controller;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Controller poller", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public ControllerState Latest => _latest;

        /// <summary>Reports per second this controller sends, from the last ~400 new reports; null until it has moved enough.</summary>
        public double? ReportRate
        {
            get
            {
                double[] copy;
                lock (_lock) copy = _arrivals.ToArray();
                return GameControllerManager.ReportRateHz(copy);
            }
        }

        public void StartRecording()
        {
            lock (_lock) _recording = new List<TimedControllerState>(4096);
        }

        /// <summary>Stops recording and hands back everything captured.</summary>
        public List<TimedControllerState> StopRecording()
        {
            lock (_lock)
            {
                var result = _recording ?? new List<TimedControllerState>();
                _recording = null;
                return result;
            }
        }

        private void Loop()
        {
            GameControllerManagerTimer.Begin();
            try
            {
                ControllerState? previous = null;
                double lastRecorded = -1;
                while (!_stop)
                {
                    var state = GameControllerManager.Read(_controller);
                    double now = _clock.Elapsed.TotalSeconds;
                    bool changed = previous == null || Differs(previous, state);

                    if (changed)
                    {
                        lock (_lock)
                        {
                            _arrivals.Add(now);
                            if (_arrivals.Count > 400) _arrivals.RemoveRange(0, _arrivals.Count - 400);
                        }
                    }

                    var recording = _recording;
                    if (recording != null && state.Connected && (changed || now - lastRecorded >= MinRecordGap))
                    {
                        lock (_lock) _recording?.Add(new TimedControllerState(now, state));
                        lastRecorded = now;
                    }

                    _latest = state;
                    previous = state;
                    Thread.Sleep(1);
                }
            }
            finally
            {
                GameControllerManagerTimer.End();
            }
        }

        private static bool Differs(ControllerState a, ControllerState b)
        {
            if (a.Connected != b.Connected) return true;
            if (a.Packet != b.Packet) return true;   // XInput counts every change
            if (a.Buttons != b.Buttons || a.Pov != b.Pov) return true;
            if (a.Axes.Length != b.Axes.Length) return true;
            for (int i = 0; i < a.Axes.Length; i++) if (a.Axes[i] != b.Axes[i]) return true;
            return a.LeftTrigger != b.LeftTrigger || a.RightTrigger != b.RightTrigger;
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(500);
        }
    }

    /// <summary>Asks Windows for a 1 ms timer while a poller runs (without it Sleep(1) can take 15 ms).</summary>
    internal static class GameControllerManagerTimer
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        public static void Begin() { try { timeBeginPeriod(1); } catch (DllNotFoundException) { } }
        public static void End() { try { timeEndPeriod(1); } catch (DllNotFoundException) { } }
    }
}
