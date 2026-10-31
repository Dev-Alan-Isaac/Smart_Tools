using Smart_Tools.WPF.Engines.Midi;
using Smart_Tools.WPF.Models;
using System.Diagnostics;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Behind MidiInstrumentsView: which notes are sounding, on which
    /// channel, who is holding them, sustain, recording and playback. Sends
    /// through an <see cref="IMidiOut"/> (winmm in the app, a fake in tests).
    ///
    /// Notes can arrive from the UI thread (mouse, computer keyboard) and
    /// from the MIDI-in thread at the same time, so all state is behind one
    /// lock; events are raised after the lock is let go, on whichever thread
    /// caused them - the view marshals to the UI thread.
    /// </summary>
    public sealed partial class MidiInstrumentsManager : IDisposable
    {
        public const string NoInput = "none";
        public const string PreferredSynthName = "Microsoft GS Wavetable Synth";

        private readonly object _gate = new();
        private readonly Func<MidiDeviceInfo, IMidiOut> _openOutput;
        private readonly Func<MidiDeviceInfo, Action<MidiInMessage>, IDisposable> _openInput;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private IMidiOut? _out;
        private IDisposable? _in;
        private MidiInstrument _instrument = MidiInstrumentCatalog.Default;
        private int _velocity = 100;
        private int _volume = 110;
        private int _reverb = 40;

        // note -> channel it started on. A note is always stopped on the
        // channel it started on, even if the instrument changed meanwhile
        // (sending the note-off on the new channel would leave it ringing).
        private readonly Dictionary<int, int> _sounding = new();

        // keys physically down, and who holds them
        private readonly Dictionary<int, MidiNoteSource> _held = new();

        // let go while sustain was on: still sounding until sustain ends
        private readonly HashSet<int> _sustained = new();

        // computer key -> the note it started (so changing octave while a key
        // is down still stops the right note, and auto-repeat is ignored)
        private readonly Dictionary<string, int> _computerKeys = new(StringComparer.Ordinal);

        private bool _sustainLatched;
        private bool _sustainPedal;

        private MidiRecording? _recording;
        private long _recordStartMs;

        private CancellationTokenSource? _playCts;
        private readonly HashSet<int> _playbackNotes = new();

        /// <summary>A key went down (true) or up (false) - for lighting the on-screen piano. Any thread.</summary>
        public event Action<int, bool>? KeyStateChanged;

        /// <summary>A note started: note, velocity, who played it. Any thread.</summary>
        public event Action<int, int, MidiNoteSource>? NotePlayed;

        /// <summary>A program change from the MIDI keyboard picked another instrument. Any thread.</summary>
        public event Action<MidiInstrument>? InstrumentChangedFromDevice;

        public MidiInstrumentsManager()
            : this(device => WinMmMidiOut.Open(device), OpenWinMmInput)
        {
        }

        /// <summary>For tests: supply the device openers.</summary>
        public MidiInstrumentsManager(Func<MidiDeviceInfo, IMidiOut> openOutput, Func<MidiDeviceInfo, Action<MidiInMessage>, IDisposable> openInput)
        {
            _openOutput = openOutput;
            _openInput = openInput;
        }

        private static IDisposable OpenWinMmInput(MidiDeviceInfo device, Action<MidiInMessage> handler)
        {
            var input = WinMmMidiIn.Open(device);
            input.MessageReceived += handler;
            return input;
        }

        private long Now => _clock.ElapsedMilliseconds;

        // =================================================================
        //  Devices
        // =================================================================

        public static IReadOnlyList<MidiDeviceInfo> Outputs() => MidiDevices.Outputs();

        public static IReadOnlyList<MidiDeviceInfo> Inputs() => MidiDevices.Inputs();

        /// <summary>The saved synth if it's still there, else Windows' GS synth, else the first one.</summary>
        public static MidiDeviceInfo? PreferredOutput(IReadOnlyList<MidiDeviceInfo> devices, string? savedName) =>
            devices.FirstOrDefault(d => d.Name == savedName)
            ?? devices.FirstOrDefault(d => d.Name.Contains(PreferredSynthName, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault();

        /// <summary>The saved keyboard if it's plugged in; the first keyboard if none was ever chosen; nothing if "None" was chosen.</summary>
        public static MidiDeviceInfo? PreferredInput(IReadOnlyList<MidiDeviceInfo> devices, string? savedName)
        {
            if (savedName == NoInput) return null;
            if (string.IsNullOrEmpty(savedName)) return devices.FirstOrDefault();
            return devices.FirstOrDefault(d => d.Name == savedName);
        }

        public string? OutputName
        {
            get { lock (_gate) return _out?.Name; }
        }

        public string? InputName { get; private set; }

        /// <summary>Opens a synth (closing the current one cleanly first). Returns null, or why it couldn't open.</summary>
        public string? OpenOutput(MidiDeviceInfo? device)
        {
            List<int> released;
            string? error = null;
            lock (_gate)
            {
                released = SilenceLocked();
                _out?.Dispose();
                _out = null;
                if (device != null)
                {
                    try
                    {
                        _out = _openOutput(device);
                        SendSetupLocked(0);
                        SendSetupLocked(MidiInstrumentCatalog.DrumChannel);
                    }
                    catch (Exception ex) when (ex is MidiDeviceException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
                    {
                        error = ex is MidiDeviceException ? ex.Message : "MIDI isn't available on this computer.";
                    }
                }
            }
            RaiseUps(released);
            return error;
        }

        /// <summary>Starts listening to a MIDI keyboard (null stops). Returns null, or why it couldn't open.</summary>
        public string? OpenInput(MidiDeviceInfo? device)
        {
            _in?.Dispose();
            _in = null;
            InputName = null;
            ReleaseSource(MidiNoteSource.MidiKeyboard);
            if (device == null) return null;
            try
            {
                _in = _openInput(device, HandleInput);
                InputName = device.Name;
                return null;
            }
            catch (Exception ex) when (ex is MidiDeviceException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                return ex is MidiDeviceException
                    ? ex.Message + " It may be in use by another app."
                    : "MIDI isn't available on this computer.";
            }
        }

        // =================================================================
        //  Sound settings
        // =================================================================

        public MidiInstrument Instrument
        {
            get { lock (_gate) return _instrument; }
        }

        public int Velocity
        {
            get { lock (_gate) return _velocity; }
            set { lock (_gate) _velocity = Math.Clamp(value, 1, 127); }
        }

        public int Volume
        {
            get { lock (_gate) return _volume; }
            set
            {
                lock (_gate)
                {
                    _volume = Math.Clamp(value, 0, 127);
                    SendLocked(Control(0, CcVolume, _volume));
                    SendLocked(Control(MidiInstrumentCatalog.DrumChannel, CcVolume, _volume));
                }
            }
        }

        public int Reverb
        {
            get { lock (_gate) return _reverb; }
            set
            {
                lock (_gate)
                {
                    _reverb = Math.Clamp(value, 0, 127);
                    SendLocked(Control(0, CcReverb, _reverb));
                    SendLocked(Control(MidiInstrumentCatalog.DrumChannel, CcReverb, _reverb));
                }
            }
        }

        /// <summary>Picks the sound for the next notes. Notes already ringing finish on their own channel.</summary>
        public void SetInstrument(MidiInstrument instrument)
        {
            lock (_gate)
            {
                _instrument = instrument;
                SendLocked(ProgramChange(instrument.Channel, instrument.Program));
            }
        }

        private void SendSetupLocked(int channel)
        {
            var program = channel == _instrument.Channel ? _instrument.Program : 0;
            SendLocked(ProgramChange(channel, program));
            SendLocked(Control(channel, CcVolume, _volume));
            SendLocked(Control(channel, CcReverb, _reverb));
        }

        // =================================================================
        //  Playing
        // =================================================================

        /// <summary>Starts a note (again, if it was already sounding).</summary>
        public void NoteDown(int note, int velocity, MidiNoteSource source)
        {
            if (note is < 0 or > 127) return;
            int v = Math.Clamp(velocity, 1, 127);
            lock (_gate)
            {
                if (_sounding.TryGetValue(note, out int oldChannel)) SendLocked(NoteOff(oldChannel, note));
                _sustained.Remove(note);
                int channel = _instrument.Channel;
                SendLocked(NoteOn(channel, note, v));
                _sounding[note] = channel;
                _held[note] = source;
            }
            KeyStateChanged?.Invoke(note, true);
            NotePlayed?.Invoke(note, v, source);
        }

        /// <summary>Lets go of a key. With sustain on, the note keeps sounding until sustain ends.</summary>
        public void NoteUp(int note)
        {
            lock (_gate)
            {
                if (!_held.Remove(note)) return;   // not down: a stray note-off does nothing
                if (SustainOnLocked) _sustained.Add(note);
                else StopLocked(note);
            }
            KeyStateChanged?.Invoke(note, false);
        }

        /// <summary>Uses the slider's velocity.</summary>
        public void NoteDown(int note, MidiNoteSource source) => NoteDown(note, Velocity, source);

        /// <summary>
        /// A computer key went down. Returns the note it started, or null
        /// (not a note key, or the key's auto-repeat - holding a key used to
        /// restart the note many times a second).
        /// </summary>
        public int? ComputerKeyDown(string keyName, int octave)
        {
            int? note;
            lock (_gate)
            {
                if (_computerKeys.ContainsKey(keyName)) return null;   // auto-repeat
                note = NoteForComputerKey(keyName, octave);
                if (note == null) return null;
                _computerKeys[keyName] = note.Value;
            }
            NoteDown(note.Value, MidiNoteSource.ComputerKeyboard);
            return note;
        }

        /// <summary>A computer key came up: stops the note that key started, whatever the octave is now.</summary>
        public int? ComputerKeyUp(string keyName)
        {
            int note;
            lock (_gate)
            {
                if (!_computerKeys.Remove(keyName, out note)) return null;
                // another source may hold the same note: only let go if the keyboard holds it
                if (!_held.TryGetValue(note, out var source) || source != MidiNoteSource.ComputerKeyboard) return note;
            }
            NoteUp(note);
            return note;
        }

        public bool IsComputerKeyDown(string keyName)
        {
            lock (_gate) return _computerKeys.ContainsKey(keyName);
        }

        /// <summary>The Sustain switch on the sidebar.</summary>
        public bool SustainLatched
        {
            get { lock (_gate) return _sustainLatched; }
            set => SetSustain(latched: value, pedal: null);
        }

        /// <summary>Space bar or a keyboard's pedal (CC 64).</summary>
        public void SetSustainPedal(bool down) => SetSustain(latched: null, pedal: down);

        public bool SustainOn
        {
            get { lock (_gate) return SustainOnLocked; }
        }

        private bool SustainOnLocked => _sustainLatched || _sustainPedal;

        private void SetSustain(bool? latched, bool? pedal)
        {
            lock (_gate)
            {
                if (latched != null) _sustainLatched = latched.Value;
                if (pedal != null) _sustainPedal = pedal.Value;
                if (SustainOnLocked) return;
                foreach (int note in _sustained.Where(n => !_held.ContainsKey(n)).ToList()) StopLocked(note);
                _sustained.Clear();
            }
        }

        /// <summary>
        /// Lets go of everything one source holds - when the window loses
        /// focus a key-up never arrives, which used to leave notes stuck.
        /// </summary>
        public void ReleaseSource(MidiNoteSource source)
        {
            List<int> notes;
            lock (_gate)
            {
                notes = _held.Where(h => h.Value == source).Select(h => h.Key).ToList();
                if (source == MidiNoteSource.ComputerKeyboard)
                {
                    _computerKeys.Clear();
                    _sustainPedal = false;
                }
            }
            foreach (int note in notes) NoteUp(note);
            if (source == MidiNoteSource.ComputerKeyboard) SetSustain(null, false);
        }

        /// <summary>Stops every sound now (the panic button): note-offs, "all notes off" on every channel, then the synth is set up again.</summary>
        public void Panic()
        {
            List<int> released;
            lock (_gate)
            {
                released = SilenceLocked();
                for (int ch = 0; ch < 16; ch++)
                {
                    SendLocked(Control(ch, CcAllNotesOff, 0));
                    SendLocked(Control(ch, CcAllSoundOff, 0));
                }
                _sustainLatched = false;
                _sustainPedal = false;
                SendSetupLocked(0);
                SendSetupLocked(MidiInstrumentCatalog.DrumChannel);
            }
            RaiseUps(released);
        }

        /// <summary>Note-offs for everything sounding; clears all state. Returns the notes that were lit.</summary>
        private List<int> SilenceLocked()
        {
            var lit = _held.Keys.Concat(_playbackNotes).Distinct().ToList();
            foreach (var (note, channel) in _sounding) SendLocked(NoteOff(channel, note));
            _sounding.Clear();
            _held.Clear();
            _sustained.Clear();
            _computerKeys.Clear();
            _playbackNotes.Clear();
            return lit;
        }

        private void StopLocked(int note)
        {
            if (_sounding.Remove(note, out int channel)) SendLocked(NoteOff(channel, note));
        }

        private void RaiseUps(IEnumerable<int> notes)
        {
            foreach (int note in notes) KeyStateChanged?.Invoke(note, false);
        }

        public IReadOnlyCollection<int> SoundingNotes
        {
            get { lock (_gate) return _sounding.Keys.ToList(); }
        }

        // =================================================================
        //  MIDI keyboard
        // =================================================================

        /// <summary>
        /// A message from the MIDI keyboard. Notes play on the chosen
        /// instrument's channel whatever channel the keyboard sends on;
        /// the pedal drives sustain; wheels and pressure pass through; a
        /// program change picks that instrument. Clock and active-sensing
        /// bytes (sent many times a second) are ignored.
        /// </summary>
        public void HandleInput(MidiInMessage m)
        {
            int type = m.Status & 0xF0;
            switch (type)
            {
                case 0x90 when m.Data2 > 0:
                    NoteDown(m.Data1, m.Data2, MidiNoteSource.MidiKeyboard);
                    break;
                case 0x80:
                case 0x90:
                    NoteUp(m.Data1);
                    break;
                case 0xB0 when m.Data1 == CcSustain:
                    SetSustainPedal(m.Data2 >= 64);
                    break;
                case 0xB0 when m.Data1 >= CcAllSoundOff:
                    ReleaseSource(MidiNoteSource.MidiKeyboard);
                    break;
                case 0xB0:
                case 0xA0:
                case 0xD0:
                case 0xE0:
                    lock (_gate) SendLocked((type | _instrument.Channel, m.Data1, m.Data2));
                    break;
                case 0xC0:
                    var instrument = FindInstrument("gm:" + m.Data1);
                    SetInstrument(instrument);
                    InstrumentChangedFromDevice?.Invoke(instrument);
                    break;
            }
        }

        // =================================================================
        //  Sending (+ recording what's sent)
        // =================================================================

        private void SendLocked((int Status, int Data1, int Data2) m)
        {
            _out?.Send(m.Status, m.Data1, m.Data2);
            _recording?.Events.Add(new MidiRecordedEvent(Now - _recordStartMs, (byte)m.Status, (byte)m.Data1, (byte)m.Data2));
        }

        // =================================================================
        //  Recording
        // =================================================================

        public bool IsRecording
        {
            get { lock (_gate) return _recording != null; }
        }

        public long RecordingElapsedMs
        {
            get { lock (_gate) return _recording == null ? 0 : Now - _recordStartMs; }
        }

        /// <summary>Starts recording what's played; the instrument, volume and reverb go first so the file sounds the same.</summary>
        public void StartRecording()
        {
            lock (_gate)
            {
                _recordStartMs = Now;
                _recording = new MidiRecording { InstrumentName = _instrument.Name };
                int ch = _instrument.Channel;
                foreach (var m in new[] { ProgramChange(ch, _instrument.Program), Control(ch, CcVolume, _volume), Control(ch, CcReverb, _reverb) })
                {
                    _out?.Send(m.Status, m.Data1, m.Data2);
                    _recording.Events.Add(new MidiRecordedEvent(0, (byte)m.Status, (byte)m.Data1, (byte)m.Data2));   // exactly 0, even if the clock ticked
                }
            }
        }

        /// <summary>Stops recording. Notes still held get their note-off at the stop time (else they'd ring forever in the file).</summary>
        public MidiRecording? StopRecording()
        {
            lock (_gate)
            {
                var rec = _recording;
                if (rec == null) return null;
                long t = Now - _recordStartMs;
                foreach (var (note, channel) in _sounding)
                    rec.Events.Add(new MidiRecordedEvent(t, (byte)(0x80 | channel), (byte)note, 0));
                rec.DurationMs = t;
                _recording = null;
                return rec;
            }
        }

        // =================================================================
        //  Playback
        // =================================================================

        public bool IsPlaying
        {
            get { lock (_gate) return _playCts != null; }
        }

        /// <summary>
        /// Plays a recording on a background thread with a 1 ms timer, so the
        /// rhythm holds. Keys light up as it plays. Progress reports the time
        /// reached. Afterwards the chosen instrument is set up again (the
        /// recording may have used another).
        /// </summary>
        public Task PlayAsync(MidiRecording recording, IProgress<long>? progress = null, CancellationToken token = default)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_playCts != null) throw new InvalidOperationException("Already playing.");
                cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _playCts = cts;
            }

            var events = recording.Events.OrderBy(e => e.TimeMs).ToList();
            return Task.Factory.StartNew(() =>
            {
                using var resolution = new MidiTimerResolution();
                var watch = Stopwatch.StartNew();
                long lastReport = -1000;
                try
                {
                    foreach (var e in events)
                    {
                        while (true)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            long wait = e.TimeMs - watch.ElapsedMilliseconds;
                            if (wait <= 0) break;
                            cts.Token.WaitHandle.WaitOne((int)Math.Min(wait, 50));
                            if (watch.ElapsedMilliseconds - lastReport >= 100)
                            {
                                lastReport = watch.ElapsedMilliseconds;
                                progress?.Report(lastReport);
                            }
                        }
                        PlayEvent(e);
                    }
                    long rest = recording.DurationMs - watch.ElapsedMilliseconds;
                    if (rest > 0) cts.Token.WaitHandle.WaitOne((int)Math.Min(rest, 5000));
                    progress?.Report(recording.DurationMs);
                }
                finally
                {
                    List<int> lit;
                    lock (_gate)
                    {
                        lit = _playbackNotes.ToList();
                        foreach (int note in lit)
                        {
                            if (_held.ContainsKey(note)) continue;   // someone is holding it now
                            StopLocked(note);
                        }
                        _playbackNotes.Clear();
                        SendSetupLocked(0);
                        SendSetupLocked(MidiInstrumentCatalog.DrumChannel);
                        _playCts = null;
                    }
                    cts.Dispose();
                    RaiseUps(lit);
                }
            }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void PlayEvent(MidiRecordedEvent e)
        {
            int type = e.Status & 0xF0, ch = e.Status & 0x0F;
            bool? lit = null;
            lock (_gate)
            {
                _out?.Send(e.Status, e.Data1, e.Data2);
                if (type == 0x90 && e.Data2 > 0)
                {
                    _sounding[e.Data1] = ch;
                    _playbackNotes.Add(e.Data1);
                    lit = true;
                }
                else if (type == 0x80 || type == 0x90)
                {
                    if (_playbackNotes.Remove(e.Data1) && !_held.ContainsKey(e.Data1)) _sounding.Remove(e.Data1);
                    lit = false;
                }
            }
            if (lit != null) KeyStateChanged?.Invoke(e.Data1, lit.Value);
            if (lit == true) NotePlayed?.Invoke(e.Data1, e.Data2, MidiNoteSource.Playback);
        }

        public void StopPlayback()
        {
            lock (_gate) _playCts?.Cancel();
        }

        // =================================================================
        //  Shutdown
        // =================================================================

        /// <summary>MainWindow closing: stop playback and recording, silence everything, let go of both devices.</summary>
        public void Dispose()
        {
            StopPlayback();
            lock (_gate) _recording = null;
            _in?.Dispose();
            _in = null;
            List<int> released;
            lock (_gate)
            {
                released = SilenceLocked();
                _out?.Dispose();
                _out = null;
            }
            RaiseUps(released);
        }
    }
}
