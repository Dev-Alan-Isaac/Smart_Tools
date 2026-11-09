using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Reading a Standard MIDI File (.mid) into a <see cref="Models.MidiRecording"/> that the player can play.</summary>
    public sealed partial class MidiInstrumentsManager
    {
        public const long MaxMidiFileBytes = 16L * 1024 * 1024;
        private const int MaxImportedEvents = 3_000_000;
        private const long MaxImportedMs = 4L * 60 * 60 * 1000;

        /// <summary>
        /// Reads format 0 and 1 files (all tracks merged on one timeline, tempo changes followed). Notes, program changes, pitch bend,
        /// controllers and pressure are kept; meta and system-exclusive messages are skipped. Throws <see cref="InvalidDataException"/>
        /// for anything that is not a usable MIDI file.
        /// </summary>
        public static Models.MidiRecording ReadMidiFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException(path);
            if (info.Length > MaxMidiFileBytes) throw new InvalidDataException("The file is too big for a MIDI file.");
            return ParseMidi(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        }

        public static Models.MidiRecording ParseMidi(byte[] data, string name)
        {
            int pos = 0;
            uint U32() { if (pos + 4 > data.Length) throw new InvalidDataException("Unexpected end of file."); uint v = (uint)(data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3]); pos += 4; return v; }
            int U16() { if (pos + 2 > data.Length) throw new InvalidDataException("Unexpected end of file."); int v = data[pos] << 8 | data[pos + 1]; pos += 2; return v; }

            // Some files start with a RIFF wrapper (.rmi): jump to the MThd.
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

            bool smpte = (division & 0x8000) != 0;
            double smpteMsPerTick = 0;
            int ppq = 480;
            if (smpte)
            {
                int fps = 256 - (division >> 8);
                int tpf = division & 0xFF;
                if (fps <= 0 || tpf <= 0) throw new InvalidDataException("The time division is not valid.");
                smpteMsPerTick = 1000.0 / (fps * tpf);
            }
            else
            {
                ppq = division;
                if (ppq <= 0) throw new InvalidDataException("The time division is not valid.");
            }

            var events = new List<(long Tick, int Order, byte Status, byte D1, byte D2)>();
            var tempos = new List<(long Tick, int Micros)> { (0, 500_000) };
            string title = "";
            int order = 0;

            for (int t = 0; t < trackCount && pos + 8 <= data.Length; t++)
            {
                if (data[pos] != 'M' || data[pos + 1] != 'T' || data[pos + 2] != 'r' || data[pos + 3] != 'k')
                {
                    // Skip an unknown chunk.
                    pos += 4;
                    uint skip = U32();
                    pos += (int)Math.Min(skip, (uint)(data.Length - pos));
                    t--;
                    continue;
                }
                pos += 4;
                int length = (int)Math.Min(U32(), (uint)(data.Length - pos));
                int end = pos + length;
                long tick = 0;
                byte running = 0;

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
                            int len = VarLen();
                            if (pos + len > end) throw new InvalidDataException("A meta event is cut off.");
                            if (type == 0x51 && len == 3) tempos.Add((tick, data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]));
                            else if (type == 0x03 && title.Length == 0) title = Encoding.Latin1.GetString(data, pos, len).Trim('\0', ' ');
                            pos += len;
                            if (type == 0x2F) break;
                        }
                        else if (status == 0xF0 || status == 0xF7)
                        {
                            int len = VarLen();
                            pos += Math.Min(len, end - pos);
                        }
                        else
                        {
                            running = status;
                            int need = IsTwoByte(status) ? 1 : 2;
                            if (pos + need > end) throw new InvalidDataException("A message is cut off.");
                            byte d1 = data[pos++];
                            byte d2 = need == 2 ? data[pos++] : (byte)0;
                            if (events.Count >= MaxImportedEvents) throw new InvalidDataException("The file has too many events.");
                            events.Add((tick, order++, status, (byte)(d1 & 0x7F), (byte)(d2 & 0x7F)));
                        }
                    }
                }
                catch (InvalidDataException) when (events.Count > 0)
                {
                    // A damaged track end: keep what was read so far.
                }
                pos = end;
            }

            if (events.Count == 0) throw new InvalidDataException("The file has no notes.");
            events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
            tempos = tempos.OrderBy(x => x.Tick).ToList();

            long lastMs = 0;
            var recording = new Models.MidiRecording { InstrumentName = title.Length > 0 ? title : name };
            // Walking the tempo map for every event is quadratic with many tempo changes; keep a running cursor instead.
            int ti = 0;
            double accMs = 0;
            long accTick = 0;
            int curMicros = 500_000;
            foreach (var e in events)
            {
                long ms;
                if (smpte) ms = (long)Math.Round(e.Tick * smpteMsPerTick);
                else
                {
                    while (ti < tempos.Count && tempos[ti].Tick <= e.Tick)
                    {
                        accMs += (tempos[ti].Tick - accTick) * (curMicros / 1000.0) / ppq;
                        accTick = tempos[ti].Tick;
                        curMicros = tempos[ti].Micros;
                        ti++;
                    }
                    ms = (long)Math.Round(accMs + (e.Tick - accTick) * (curMicros / 1000.0) / ppq);
                }
                if (ms > MaxImportedMs) break;
                recording.Events.Add(new Models.MidiRecordedEvent(ms, e.Status, e.D1, e.D2));
                lastMs = ms;
            }
            recording.DurationMs = lastMs + 500;
            if (recording.IsEmpty) throw new InvalidDataException("The file has no notes.");
            return recording;
        }
    }
}
