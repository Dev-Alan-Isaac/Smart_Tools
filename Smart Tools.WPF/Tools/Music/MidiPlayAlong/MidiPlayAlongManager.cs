using Smart_Tools.WPF.Engines.Midi;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Reads a .mid file keeping its tracks apart, and plays it with a click, muted tracks, a tempo scale and a loop.</summary>
    public sealed class MidiPlayAlongManager : IDisposable
    {
        public const string SettingsName = "midi_playalong";
        public const long MaxFileBytes = 16L * 1024 * 1024;
        private const int MaxEvents = 3_000_000;
        public const int ClickChannel = 9;
        public const int ClickAccentNote = 76;   // GM "Hi Wood Block"
        public const int ClickNote = 77;         // GM "Low Wood Block"

        // =================================================================
        //  Reading
        // =================================================================

        public static MidiSong ReadSong(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException(path);
            if (info.Length > MaxFileBytes) throw new InvalidDataException("The file is too big for a MIDI file.");
            return ParseSong(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        }

        public static MidiSong ParseSong(byte[] data, string name)
        {
            int pos = 0;
            uint U32() { if (pos + 4 > data.Length) throw new InvalidDataException("Unexpected end of file."); uint v = (uint)(data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3]); pos += 4; return v; }
            int U16() { if (pos + 2 > data.Length) throw new InvalidDataException("Unexpected end of file."); int v = data[pos] << 8 | data[pos + 1]; pos += 2; return v; }

            if (data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
            {
                int at = Encoding.ASCII.GetString(data).IndexOf("MThd", StringComparison.Ordinal);
                if (at < 0) throw new InvalidDataException("This is not a MIDI file.");
                pos = at;
            }
            if (pos + 14 > data.Length || data[pos] != 'M' || data[pos + 1] != 'T' || data[pos + 2] != 'h' || data[pos + 3] != 'd')
                throw new InvalidDataException("This is not a MIDI file.");
            pos += 4;
            uint headerLength = U32();
            int format = U16(), trackCount = U16(), division = U16();
            pos += (int)Math.Max(0, (long)headerLength - 6);
            if (format > 1) throw new InvalidDataException("This kind of MIDI file (format 2) is not supported.");
            if (trackCount < 1) throw new InvalidDataException("The file has no tracks.");
            if ((division & 0x8000) != 0) throw new InvalidDataException("Files timed in frames (SMPTE) are not supported here.");
            int ppq = division;
            if (ppq <= 0) throw new InvalidDataException("The time division is not valid.");

            var events = new List<MidiSongEvent>();
            var tempos = new List<(long Tick, int Micros)>();
            var sigs = new List<(long Tick, int Num, int Den)>();
            var tracks = new List<MidiSongTrack>();
            long length = 0;
            int trackNo = 0;

            for (int t = 0; t < trackCount && pos + 8 <= data.Length; t++)
            {
                if (data[pos] != 'M' || data[pos + 1] != 'T' || data[pos + 2] != 'r' || data[pos + 3] != 'k')
                {
                    pos += 4;
                    uint skip = U32();
                    pos += (int)Math.Min(skip, (uint)(data.Length - pos));
                    t--;
                    continue;
                }
                pos += 4;
                int len = (int)Math.Min(U32(), (uint)(data.Length - pos));
                int end = pos + len;
                long tick = 0;
                byte running = 0;
                string title = "";
                var programs = new Dictionary<int, int>();
                var channels = new SortedSet<int>();
                int notes = 0;
                int first = events.Count;

                int VarLen()
                {
                    int value = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (pos >= end) throw new InvalidDataException("Unexpected end of track.");
                        byte b = data[pos++];
                        value = (value << 7) | (b & 0x7F);
                        if ((b & 0x80) == 0) return value;
                    }
                    throw new InvalidDataException("A time value is not valid.");
                }

                try
                {
                    while (pos < end)
                    {
                        tick += VarLen();
                        if (pos >= end) break;
                        byte status = data[pos];
                        if (status < 0x80)
                        {
                            if (running == 0) throw new InvalidDataException("A message has no status.");
                            status = running;
                        }
                        else pos++;

                        if (status == 0xFF)
                        {
                            if (pos >= end) break;
                            byte type = data[pos++];
                            int mlen = VarLen();
                            if (pos + mlen > end) throw new InvalidDataException("A meta event is cut off.");
                            if (type == 0x51 && mlen == 3) tempos.Add((tick, data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]));
                            else if (type == 0x58 && mlen >= 2) sigs.Add((tick, Math.Max(1, (int)data[pos]), 1 << Math.Min(6, (int)data[pos + 1])));
                            else if (type == 0x03 && title.Length == 0) title = Encoding.Latin1.GetString(data, pos, mlen).Trim('\0', ' ');
                            pos += mlen;
                            if (type == 0x2F) break;
                        }
                        else if (status == 0xF0 || status == 0xF7)
                        {
                            int slen = VarLen();
                            pos += Math.Min(slen, end - pos);
                        }
                        else
                        {
                            running = status;
                            int need = MidiInstrumentsManager.IsTwoByte(status) ? 1 : 2;
                            if (pos + need > end) throw new InvalidDataException("A message is cut off.");
                            byte d1 = (byte)(data[pos++] & 0x7F);
                            byte d2 = need == 2 ? (byte)(data[pos++] & 0x7F) : (byte)0;
                            if (events.Count >= MaxEvents) throw new InvalidDataException("The file has too many events.");
                            int ch = status & 0x0F;
                            int type = status & 0xF0;
                            channels.Add(ch);
                            if (type == 0x90 && d2 > 0) notes++;
                            if (type == 0xC0 && !programs.ContainsKey(ch)) programs[ch] = d1;
                            events.Add(new MidiSongEvent(tick, trackNo, status, d1, d2));
                        }
                    }
                }
                catch (InvalidDataException) when (events.Count > 0)
                {
                    // a damaged track end: keep what was read
                }
                pos = end;
                length = Math.Max(length, tick);

                if (notes > 0 || trackNo == 0 || events.Count > first)
                {
                    bool drums = channels.Contains(MidiInstrumentCatalog.DrumChannel) && channels.Count == 1;
                    string instrument = drums ? "Drums"
                        : programs.Count > 0 ? MidiInstrumentCatalog.All[Math.Clamp(programs.Values.First(), 0, 127)].Name : "";
                    tracks.Add(new MidiSongTrack
                    {
                        Index = trackNo,
                        Name = title.Length > 0 ? title : instrument.Length > 0 ? instrument : $"Track {trackNo + 1}",
                        Instrument = instrument,
                        ChannelsText = string.Join(", ", channels.Select(c => c + 1)),
                        NoteCount = notes,
                        IsDrums = drums,
                    });
                }
                trackNo++;
            }

            if (events.Count == 0 || events.All(e => (e.Status & 0xF0) != 0x90)) throw new InvalidDataException("The file has no notes.");
            // Tracks with no messages at all (tempo-only track 1 of a format 1 file) are not listed.
            var listed = tracks.Where(tr => tr.NoteCount > 0 || events.Any(e => e.Track == tr.Index)).ToList();
            var sorted = events.OrderBy(e => e.Tick).ToList();   // stable: same-tick messages keep file order
            if (tempos.Count == 0 || tempos[0].Tick > 0) tempos.Insert(0, (0, 500_000));
            if (sigs.Count == 0 || sigs[0].Tick > 0) sigs.Insert(0, (0, 4, 4));
            return new MidiSong
            {
                Name = name,
                TicksPerQuarter = ppq,
                Events = sorted,
                Tempos = tempos.OrderBy(x => x.Tick).ToList(),
                TimeSignatures = sigs.OrderBy(x => x.Tick).ToList(),
                Tracks = listed,
                LengthTicks = Math.Max(length, sorted[^1].Tick),
            };
        }

        /// <summary>Tick of the start of a bar (1-based), following time signature changes.</summary>
        public static long BarStartTick(MidiSong song, int bar)
        {
            long tick = 0;
            int ppq = song.TicksPerQuarter;
            int index = 0;
            for (int b = 1; b < bar; b++)
            {
                while (index + 1 < song.TimeSignatures.Count && song.TimeSignatures[index + 1].Tick <= tick) index++;
                var s = song.TimeSignatures[index];
                tick += (long)s.Numerator * ppq * 4 / s.Denominator;
            }
            return tick;
        }

        /// <summary>The bar (1-based) and beat (1-based) a tick falls in.</summary>
        public static (int Bar, int Beat) BarAndBeat(MidiSong song, long tick)
        {
            if (tick < 0) return (0, 0);
            long cursor = 0;
            int bar = 1, index = 0, ppq = song.TicksPerQuarter;
            while (true)
            {
                while (index + 1 < song.TimeSignatures.Count && song.TimeSignatures[index + 1].Tick <= cursor) index++;
                var s = song.TimeSignatures[index];
                long beatTicks = (long)ppq * 4 / s.Denominator;
                long barTicks = beatTicks * s.Numerator;
                if (cursor + barTicks > tick) return (bar, (int)((tick - cursor) / beatTicks) + 1);
                cursor += barTicks;
                bar++;
            }
        }

        public static int BarCount(MidiSong song) => BarAndBeat(song, Math.Max(0, song.LengthTicks - 1)).Bar;

        /// <summary>Milliseconds from the start to <paramref name="tick"/> at the song's own tempo.</summary>
        public static double TickToMs(MidiSong song, long tick)
        {
            double ms = 0;
            long prev = 0;
            int micros = song.Tempos.Count > 0 ? song.Tempos[0].Micros : 500_000;
            foreach (var (tTick, tMicros) in song.Tempos)
            {
                if (tTick >= tick) break;
                ms += (tTick - prev) * (micros / 1000.0) / song.TicksPerQuarter;
                prev = tTick;
                micros = tMicros;
            }
            return ms + (tick - prev) * (micros / 1000.0) / song.TicksPerQuarter;
        }

        public static string FormatTime(double ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        // =================================================================
        //  Output and playback
        // =================================================================

        private readonly object _gate = new();
        private IMidiOut? _out;
        private CancellationTokenSource? _cts;
        private Task? _thread;
        private MidiSong? _song;
        private readonly HashSet<(int Track, int Channel, int Note)> _sounding = new();

        private double _positionTick;     // read by the screen
        public double PositionTick { get { lock (_gate) return _positionTick; } }

        public bool IsPlaying => _cts != null;
        public string? OutputName { get { lock (_gate) return _out?.Name; } }

        public volatile bool ClickOn = true;
        public volatile int ClickVolume = 100;
        public volatile int CountInBars = 1;
        private double _tempoScale = 1.0;
        public double TempoScale { get { lock (_gate) return _tempoScale; } set { lock (_gate) _tempoScale = Math.Clamp(value, 0.25, 2.0); } }
        public volatile bool Loop;
        private long _loopStart = -1, _loopEnd = -1;

        public void SetLoopRange(long startTick, long endTick) { lock (_gate) { _loopStart = startTick; _loopEnd = endTick; } }

        /// <summary>Raised on the playing thread when the song ends (not when it loops or is stopped).</summary>
        public event Action? Finished;

        public string? OpenOutput(MidiDeviceInfo? device)
        {
            lock (_gate)
            {
                SilenceLocked();
                _out?.Dispose();
                _out = null;
                if (device == null) return null;
                try { _out = WinMmMidiOut.Open(device); }
                catch (Exception ex) when (ex is MidiDeviceException or DllNotFoundException or EntryPointNotFoundException)
                {
                    return ex is MidiDeviceException ? ex.Message : "MIDI isn't available on this computer.";
                }
            }
            return null;
        }

        private bool Audible(MidiSong song, int track)
        {
            bool anySolo = false;
            foreach (var tr in song.Tracks) if (tr.IsSolo) { anySolo = true; break; }
            foreach (var tr in song.Tracks)
                if (tr.Index == track) return anySolo ? tr.IsSolo : !tr.IsMuted;
            return !anySolo;
        }

        /// <summary>After a mute or solo change: silence the notes of tracks that are now quiet.</summary>
        public void RefreshMutes()
        {
            lock (_gate)
            {
                if (_song == null || _out == null) return;
                foreach (var n in _sounding.Where(n => !Audible(_song, n.Track)).ToList())
                {
                    _out.Send(0x80 | n.Channel, n.Note, 0);
                    _sounding.Remove(n);
                }
            }
        }

        private void SilenceLocked()
        {
            if (_out == null) { _sounding.Clear(); return; }
            foreach (var n in _sounding) _out.Send(0x80 | n.Channel, n.Note, 0);
            _sounding.Clear();
            for (int ch = 0; ch < 16; ch++) { _out.Send(0xB0 | ch, 123, 0); _out.Send(0xB0 | ch, 64, 0); }
        }

        public string? Play(MidiSong song, double fromTick, bool countIn)
        {
            Stop();
            lock (_gate)
            {
                if (_out == null) return "No MIDI output is open.";
                _song = song;
            }
            var cts = new CancellationTokenSource();
            _cts = cts;
            _thread = Task.Factory.StartNew(() => Run(song, fromTick, countIn, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return null;
        }

        public void Stop()
        {
            var cts = _cts;
            if (cts == null) return;
            cts.Cancel();
            try { _thread?.Wait(500); } catch (AggregateException) { }
            _cts = null;
            cts.Dispose();
            lock (_gate) SilenceLocked();
        }

        public void SetPosition(double tick) { lock (_gate) _positionTick = tick; }

        private void Run(MidiSong song, double startTick, bool countIn, CancellationToken token)
        {
            using var resolution = new MidiTimerResolution();
            int ppq = song.TicksPerQuarter;
            var events = song.Events;
            long barTicks0 = (long)song.TimeSignatures[0].Numerator * ppq * 4 / song.TimeSignatures[0].Denominator;
            double pos = startTick;
            if (countIn && CountInBars > 0 && startTick <= 0) pos = -CountInBars * barTicks0;
            int index = 0;
            var offs = new List<(double Due, int Note)>();   // click note-offs, due in tick units
            long nextClick = long.MinValue;

            void Chase(double upTo)
            {
                // Program, controller and pitch-bend messages up to the start point, so every part has its own sound.
                lock (_gate)
                {
                    if (_out == null) return;
                    foreach (var e in events)
                    {
                        if (e.Tick >= upTo) break;
                        int type = e.Status & 0xF0;
                        if (type is 0xC0 or 0xB0 or 0xE0) _out.Send(e.Status, e.Data1, e.Data2);
                    }
                }
            }

            void Seek(double to)
            {
                int lo = 0, hi = events.Count;
                while (lo < hi) { int mid = (lo + hi) / 2; if (events[mid].Tick < to) lo = mid + 1; else hi = mid; }
                index = lo;
                Chase(to);
                // First click at or after the position.
                nextClick = ClickAtOrAfter(song, (long)Math.Ceiling(to));
            }

            Seek(Math.Max(pos, 0));
            if (pos < 0) nextClick = ClickAtOrAfter(song, (long)Math.Ceiling(pos), allowNegative: true, barTicks0);
            int tempoIndex = 0;
            var sw = Stopwatch.StartNew();
            double last = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    double now = sw.Elapsed.TotalMilliseconds;
                    double dt = now - last;
                    last = now;
                    double scale;
                    lock (_gate) scale = _tempoScale;
                    long probe = (long)Math.Max(pos, 0);
                    while (tempoIndex + 1 < song.Tempos.Count && song.Tempos[tempoIndex + 1].Tick <= probe) tempoIndex++;
                    while (tempoIndex > 0 && song.Tempos[tempoIndex].Tick > probe) tempoIndex--;
                    double micros = song.Tempos[tempoIndex].Micros;
                    pos += dt * 1000.0 * ppq / (micros / scale);
                    lock (_gate) _positionTick = pos;

                    long loopStart, loopEnd;
                    lock (_gate) { loopStart = _loopStart; loopEnd = _loopEnd; }
                    long endTick = Loop && loopEnd > 0 ? loopEnd : song.LengthTicks;

                    // Click.
                    while (nextClick != long.MinValue && nextClick <= pos && nextClick < endTick)
                    {
                        if (ClickOn || pos < 0) SendClick(song, nextClick);
                        offs.Add((nextClick + ppq / 8.0, 0));
                        nextClick = NextClick(song, nextClick, barTicks0);
                    }
                    if (offs.Count > 0 && offs[0].Due <= pos)
                    {
                        lock (_gate)
                        {
                            _out?.Send(0x80 | ClickChannel, ClickAccentNote, 0);
                            _out?.Send(0x80 | ClickChannel, ClickNote, 0);
                        }
                        offs.Clear();
                    }

                    // Song messages.
                    if (pos >= 0)
                    {
                        lock (_gate)
                        {
                            while (index < events.Count && events[index].Tick <= pos && events[index].Tick < endTick)
                            {
                                var e = events[index++];
                                if (_out == null) continue;
                                int type = e.Status & 0xF0, ch = e.Status & 0x0F;
                                if (type == 0x90 && e.Data2 > 0)
                                {
                                    if (!Audible(song, e.Track)) continue;
                                    _sounding.Add((e.Track, ch, e.Data1));
                                    _out.Send(e.Status, e.Data1, e.Data2);
                                }
                                else if (type == 0x80 || type == 0x90)
                                {
                                    if (_sounding.Remove((e.Track, ch, e.Data1))) _out.Send(0x80 | ch, e.Data1, 0);
                                }
                                else _out.Send(e.Status, e.Data1, e.Data2);
                            }
                        }
                    }

                    if (pos >= endTick)
                    {
                        if (Loop)
                        {
                            lock (_gate) SilenceLocked();
                            pos = loopStart > 0 ? loopStart : 0;
                            Seek(pos);
                            last = sw.Elapsed.TotalMilliseconds;
                            continue;
                        }
                        lock (_gate) { SilenceLocked(); _positionTick = song.LengthTicks; }
                        _cts = null;
                        Finished?.Invoke();
                        return;
                    }
                    token.WaitHandle.WaitOne(1);
                }
            }
            finally
            {
                lock (_gate) SilenceLocked();
            }
        }

        private void SendClick(MidiSong song, long tick)
        {
            bool accent;
            if (tick < 0)   // count-in: the first click of each bar is the accent
            {
                var s0 = song.TimeSignatures[0];
                long barTicks = (long)s0.Numerator * song.TicksPerQuarter * 4 / s0.Denominator;
                accent = barTicks > 0 && (-tick) % barTicks == 0;
            }
            else accent = BarAndBeat(song, tick).Beat == 1;
            lock (_gate)
            {
                if (_out == null) return;
                _out.Send(0x90 | ClickChannel, accent ? ClickAccentNote : ClickNote, Math.Clamp(ClickVolume, 1, 127));
            }
        }

        // Beat grid: every beat of every bar, following the time signature in force at the bar's start.

        private static long ClickAtOrAfter(MidiSong song, long tick, bool allowNegative = false, long barTicks0 = 0)
        {
            if (tick < 0)
            {
                if (!allowNegative || barTicks0 <= 0) tick = 0;
                else
                {
                    var s0 = song.TimeSignatures[0];
                    long bt = (long)song.TicksPerQuarter * 4 / s0.Denominator;
                    return (long)Math.Ceiling(tick / (double)bt) * bt;
                }
            }
            long cursor = 0;
            int index = 0, ppq = song.TicksPerQuarter;
            while (cursor <= song.LengthTicks + ppq * 16L)
            {
                while (index + 1 < song.TimeSignatures.Count && song.TimeSignatures[index + 1].Tick <= cursor) index++;
                var s = song.TimeSignatures[index];
                long beat = (long)ppq * 4 / s.Denominator;
                long bar = beat * s.Numerator;
                if (cursor + bar > tick)
                {
                    long within = tick - cursor;
                    long k = (within + beat - 1) / beat;
                    long result = cursor + Math.Max(0, k) * beat;
                    return result < cursor + bar ? result : cursor + bar;
                }
                cursor += bar;
            }
            return long.MinValue;
        }

        private static long NextClick(MidiSong song, long clickTick, long barTicks0)
        {
            if (clickTick < 0)
            {
                long bt = (long)song.TicksPerQuarter * 4 / song.TimeSignatures[0].Denominator;
                return clickTick + bt;
            }
            return ClickAtOrAfter(song, clickTick + 1);
        }

        public void Dispose()
        {
            Stop();
            lock (_gate)
            {
                SilenceLocked();
                _out?.Dispose();
                _out = null;
            }
        }
    }
}
