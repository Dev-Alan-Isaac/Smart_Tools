using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Smart_Tools.WPF.Engines.Midi
{
    // =====================================================================
    //  MIDI engine for MIDI Instruments: the Windows multimedia API
    //  (winmm.dll) through P/Invoke - no NuGet, nothing to copy next to the
    //  app (winmm.dll ships with every Windows). Output goes to any MIDI
    //  synth Windows knows, normally "Microsoft GS Wavetable Synth" (all 128
    //  General MIDI instruments and the GS drum kits). Input comes from a
    //  connected MIDI keyboard.
    //
    //  The manager talks to IMidiOut, so tests use a fake and never need a
    //  sound device.
    // =====================================================================

    /// <summary>A MIDI device Windows lists (its number can change when devices are plugged in or out, the name doesn't).</summary>
    public sealed record MidiDeviceInfo(int Index, string Name)
    {
        // The device combo boxes show the closed-box text via ToString; the default record text is "MidiDeviceInfo { Index = 0, ... }".
        public override string ToString() => Name;
    }

    /// <summary>One message from a MIDI keyboard: status byte, two data bytes, and the driver's time stamp.</summary>
    public readonly record struct MidiInMessage(byte Status, byte Data1, byte Data2, uint TimestampMs);

    /// <summary>A winmm call failed; the message is Windows' own text for the error.</summary>
    public sealed class MidiDeviceException : Exception
    {
        public int Code { get; }
        public MidiDeviceException(string message, int code) : base(message) => Code = code;
    }

    /// <summary>Where notes go. Send may be called from any thread.</summary>
    public interface IMidiOut : IDisposable
    {
        string Name { get; }

        /// <summary>A short (channel) message: status, data1, data2.</summary>
        void Send(int status, int data1, int data2);

        /// <summary>Silences every note on every channel (the driver's own panic).</summary>
        void Reset();
    }

    public static class MidiDevices
    {
        /// <summary>Synths and MIDI out ports. Empty when winmm isn't there (not Windows) or has none.</summary>
        public static IReadOnlyList<MidiDeviceInfo> Outputs()
        {
            var list = new List<MidiDeviceInfo>();
            try
            {
                int count = WinMm.midiOutGetNumDevs();
                for (int i = 0; i < count; i++)
                {
                    var caps = new WinMm.MIDIOUTCAPS();
                    if (WinMm.midiOutGetDevCapsW((UIntPtr)(uint)i, ref caps, Marshal.SizeOf<WinMm.MIDIOUTCAPS>()) == 0)
                        list.Add(new MidiDeviceInfo(i, caps.szPname ?? $"MIDI out {i + 1}"));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // not Windows
            }
            return list;
        }

        /// <summary>Connected MIDI keyboards and in ports.</summary>
        public static IReadOnlyList<MidiDeviceInfo> Inputs()
        {
            var list = new List<MidiDeviceInfo>();
            try
            {
                int count = WinMm.midiInGetNumDevs();
                for (int i = 0; i < count; i++)
                {
                    var caps = new WinMm.MIDIINCAPS();
                    if (WinMm.midiInGetDevCapsW((UIntPtr)(uint)i, ref caps, Marshal.SizeOf<WinMm.MIDIINCAPS>()) == 0)
                        list.Add(new MidiDeviceInfo(i, caps.szPname ?? $"MIDI in {i + 1}"));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // not Windows
            }
            return list;
        }

        internal static string ErrorText(int code, bool input)
        {
            var text = new StringBuilder(256);
            try
            {
                int r = input
                    ? WinMm.midiInGetErrorTextW(code, text, text.Capacity)
                    : WinMm.midiOutGetErrorTextW(code, text, text.Capacity);
                if (r == 0 && text.Length > 0) return text.ToString();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // fall through
            }
            return $"MIDI error {code}.";
        }
    }

    /// <summary>A MIDI output opened through winmm.</summary>
    public sealed class WinMmMidiOut : IMidiOut
    {
        private IntPtr _handle;
        private readonly object _gate = new();

        public string Name { get; }

        private WinMmMidiOut(IntPtr handle, string name)
        {
            _handle = handle;
            Name = name;
        }

        /// <summary>Opens a device by its number. Throws MidiDeviceException (e.g. another app holds an out port).</summary>
        public static WinMmMidiOut Open(MidiDeviceInfo device)
        {
            int r = WinMm.midiOutOpen(out IntPtr handle, (uint)device.Index, IntPtr.Zero, IntPtr.Zero, WinMm.CALLBACK_NULL);
            if (r != 0) throw new MidiDeviceException(MidiDevices.ErrorText(r, input: false), r);
            return new WinMmMidiOut(handle, device.Name);
        }

        public void Send(int status, int data1, int data2)
        {
            uint msg = (uint)(status & 0xFF) | (uint)(data1 & 0x7F) << 8 | (uint)(data2 & 0x7F) << 16;
            lock (_gate)
            {
                if (_handle != IntPtr.Zero) WinMm.midiOutShortMsg(_handle, msg);
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                if (_handle != IntPtr.Zero) WinMm.midiOutReset(_handle);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero) return;
                WinMm.midiOutReset(_handle);
                WinMm.midiOutClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// A MIDI input opened through winmm. Windows calls back on its own
    /// thread, where almost no other API may be used, so the callback only
    /// queues the message and wakes a worker thread; MessageReceived is
    /// raised from that worker (never the UI thread).
    /// </summary>
    public sealed class WinMmMidiIn : IDisposable
    {
        private IntPtr _handle;
        private readonly WinMm.MidiInProc _callback;   // kept alive: winmm holds a pointer to it
        private readonly ConcurrentQueue<MidiInMessage> _queue = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _worker;
        private volatile bool _closing;

        public string Name { get; }

        /// <summary>Raised on a background thread for each channel message (note, controller, pitch bend...).</summary>
        public event Action<MidiInMessage>? MessageReceived;

        private WinMmMidiIn(string name)
        {
            Name = name;
            _callback = OnMidiIn;
            _worker = new Thread(Pump) { IsBackground = true, Name = "MIDI in", Priority = ThreadPriority.AboveNormal };
        }

        public static WinMmMidiIn Open(MidiDeviceInfo device)
        {
            var input = new WinMmMidiIn(device.Name);
            int r = WinMm.midiInOpen(out input._handle, (uint)device.Index, input._callback, IntPtr.Zero, WinMm.CALLBACK_FUNCTION);
            if (r != 0) throw new MidiDeviceException(MidiDevices.ErrorText(r, input: true), r);
            input._worker.Start();
            r = WinMm.midiInStart(input._handle);
            if (r != 0)
            {
                input.Dispose();
                throw new MidiDeviceException(MidiDevices.ErrorText(r, input: true), r);
            }
            return input;
        }

        private void OnMidiIn(IntPtr hMidiIn, uint wMsg, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (wMsg != WinMm.MIM_DATA || _closing) return;
            uint packed = (uint)param1.ToInt64();
            _queue.Enqueue(new MidiInMessage((byte)(packed & 0xFF), (byte)((packed >> 8) & 0x7F), (byte)((packed >> 16) & 0x7F), (uint)param2.ToInt64()));
            _wake.Set();
        }

        private void Pump()
        {
            while (!_closing)
            {
                _wake.WaitOne(200);
                while (_queue.TryDequeue(out var m))
                {
                    try
                    {
                        MessageReceived?.Invoke(m);
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"MIDI in handler failed: {ex.Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero && _closing) return;
            _closing = true;
            if (_handle != IntPtr.Zero)
            {
                WinMm.midiInStop(_handle);
                WinMm.midiInReset(_handle);
                WinMm.midiInClose(_handle);
                _handle = IntPtr.Zero;
            }
            _wake.Set();
            if (_worker.IsAlive && Thread.CurrentThread != _worker) _worker.Join(500);
        }
    }

    /// <summary>
    /// While held, Windows' timer ticks every 1 ms instead of ~15.6 ms, so
    /// playback of a recording keeps its rhythm. Always disposed after play.
    /// </summary>
    public sealed class MidiTimerResolution : IDisposable
    {
        private bool _active;

        public MidiTimerResolution()
        {
            try
            {
                _active = WinMm.timeBeginPeriod(1) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _active = false;
            }
        }

        public void Dispose()
        {
            if (!_active) return;
            _active = false;
            WinMm.timeEndPeriod(1);
        }
    }

    internal static class WinMm
    {
        public const uint CALLBACK_NULL = 0x00000;
        public const uint CALLBACK_FUNCTION = 0x30000;
        public const uint MIM_DATA = 0x3C3;

        public delegate void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MIDIOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public ushort wTechnology;
            public ushort wVoices;
            public ushort wNotes;
            public ushort wChannelMask;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MIDIINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwSupport;
        }

        [DllImport("winmm.dll")] public static extern int midiOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int midiOutGetDevCapsW(UIntPtr uDeviceID, ref MIDIOUTCAPS caps, int cbMidiOutCaps);
        [DllImport("winmm.dll")] public static extern int midiOutOpen(out IntPtr handle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern int midiOutShortMsg(IntPtr handle, uint message);
        [DllImport("winmm.dll")] public static extern int midiOutReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiOutClose(IntPtr handle);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int midiOutGetErrorTextW(int error, StringBuilder text, int size);

        [DllImport("winmm.dll")] public static extern int midiInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int midiInGetDevCapsW(UIntPtr uDeviceID, ref MIDIINCAPS caps, int cbMidiInCaps);
        [DllImport("winmm.dll")] public static extern int midiInOpen(out IntPtr handle, uint deviceId, MidiInProc callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern int midiInStart(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiInStop(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiInReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiInClose(IntPtr handle);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int midiInGetErrorTextW(int error, StringBuilder text, int size);

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);
    }
}
