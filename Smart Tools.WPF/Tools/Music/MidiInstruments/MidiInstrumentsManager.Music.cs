using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// MIDI Instruments: the pure parts - note names, the computer-keyboard
    /// layout, the on-screen keys, MIDI messages, the .mid file writer,
    /// instrument search and settings. No devices, no WPF.
    /// </summary>
    public sealed partial class MidiInstrumentsManager
    {
        public const int MinOctave = 1;
        public const int MaxOctave = 7;
        public const int VisibleOctaves = 3;

        private static readonly string[] SharpNames = { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" };
        private static readonly bool[] Black = { false, true, false, true, false, false, true, false, true, false, true, false };

        /// <summary>"C4" for 60 (middle C), "A♯3" for 58 - the usual scientific pitch names.</summary>
        public static string NoteName(int note) =>
            note is < 0 or > 127 ? "" : SharpNames[note % 12] + (note / 12 - 1);

        public static bool IsBlackKey(int note) => Black[((note % 12) + 12) % 12];

        /// <summary>MIDI note of C in an octave (C4 = 60).</summary>
        public static int OctaveStart(int octave) => (octave + 1) * 12;

        // ---------------- Computer keyboard ----------------

        /// <summary>
        /// Tracker-style layout: the bottom letter row plays the chosen octave
        /// (Z = C, S = C♯, X = D ... M = B, then , L . ; / carry on), the top
        /// letter row plays the octave above (Q = C, 2 = C♯, W = D ... U = B,
        /// I 9 O 0 P carry on). Keys are WPF Key names; a few have two names.
        /// </summary>
        private static readonly (string[] Keys, int Offset, string Label)[] KeyLayout =
        {
            (new[] { "Z" }, 0, "Z"), (new[] { "S" }, 1, "S"), (new[] { "X" }, 2, "X"), (new[] { "D" }, 3, "D"),
            (new[] { "C" }, 4, "C"), (new[] { "V" }, 5, "V"), (new[] { "G" }, 6, "G"), (new[] { "B" }, 7, "B"),
            (new[] { "H" }, 8, "H"), (new[] { "N" }, 9, "N"), (new[] { "J" }, 10, "J"), (new[] { "M" }, 11, "M"),
            (new[] { "OemComma" }, 12, ","), (new[] { "L" }, 13, "L"), (new[] { "OemPeriod" }, 14, "."),
            (new[] { "Oem1", "OemSemicolon" }, 15, ";"), (new[] { "Oem2", "OemQuestion" }, 16, "/"),

            (new[] { "Q" }, 12, "Q"), (new[] { "D2" }, 13, "2"), (new[] { "W" }, 14, "W"), (new[] { "D3" }, 15, "3"),
            (new[] { "E" }, 16, "E"), (new[] { "R" }, 17, "R"), (new[] { "D5" }, 18, "5"), (new[] { "T" }, 19, "T"),
            (new[] { "D6" }, 20, "6"), (new[] { "Y" }, 21, "Y"), (new[] { "D7" }, 22, "7"), (new[] { "U" }, 23, "U"),
            (new[] { "I" }, 24, "I"), (new[] { "D9" }, 25, "9"), (new[] { "O" }, 26, "O"), (new[] { "D0" }, 27, "0"),
            (new[] { "P" }, 28, "P"),
        };

        private static readonly Dictionary<string, int> KeyOffsets = KeyLayout
            .SelectMany(k => k.Keys.Select(name => (name, k.Offset)))
            .ToDictionary(k => k.name, k => k.Offset, StringComparer.Ordinal);

        /// <summary>The note a computer key plays in this octave, or null if the key isn't a note key.</summary>
        public static int? NoteForComputerKey(string keyName, int octave)
        {
            if (!KeyOffsets.TryGetValue(keyName, out int offset)) return null;
            int note = OctaveStart(octave) + offset;
            return note is >= 0 and <= 127 ? note : null;
        }

        /// <summary>The label printed on a key ("Z", "Q", ","...) - the lower row wins where both rows reach (C of the next octave shows ","; Q plays it too).</summary>
        public static string ComputerKeyLabel(int note, int octave)
        {
            int offset = note - OctaveStart(octave);
            foreach (var k in KeyLayout)
                if (k.Offset == offset) return k.Label;
            return "";
        }

        /// <summary>The on-screen keys: three octaves from C of <paramref name="octave"/> plus the closing C, clamped to MIDI's range.</summary>
        public static List<MidiPianoKey> BuildKeys(int octave, bool drums, int octaves = VisibleOctaves)
        {
            int first = OctaveStart(octave);
            int last = Math.Min(127, first + octaves * 12);
            var keys = new List<MidiPianoKey>();
            int white = -1;
            for (int note = first; note <= last; note++)
            {
                bool black = IsBlackKey(note);
                if (!black) white++;
                keys.Add(new MidiPianoKey
                {
                    Note = note,
                    IsBlack = black,
                    WhiteIndex = Math.Max(0, white),
                    NoteName = NoteName(note),
                    ComputerKey = ComputerKeyLabel(note, octave),
                    DrumName = drums ? MidiInstrumentCatalog.DrumName(note) : null,
                });
            }
            return keys;
        }

        /// <summary>
        /// Velocity for a click: the slider value, or with "harder lower down"
        /// from 35% of it at the top of the key up to the full value at the
        /// bottom. Always 1-127 (0 would be a note-off).
        /// </summary>
        public static int VelocityFor(MidiTouchMode mode, int baseVelocity, double heightFraction)
        {
            int v = mode == MidiTouchMode.ByClickHeight
                ? (int)Math.Round(baseVelocity * (0.35 + 0.65 * Math.Clamp(heightFraction, 0, 1)))
                : baseVelocity;
            return Math.Clamp(v, 1, 127);
        }

        // ---------------- Messages ----------------

        public static (int Status, int Data1, int Data2) NoteOn(int channel, int note, int velocity) => (0x90 | (channel & 15), note & 127, Math.Clamp(velocity, 1, 127));
        public static (int Status, int Data1, int Data2) NoteOff(int channel, int note) => (0x80 | (channel & 15), note & 127, 0);
        public static (int Status, int Data1, int Data2) ProgramChange(int channel, int program) => (0xC0 | (channel & 15), program & 127, 0);
        public static (int Status, int Data1, int Data2) Control(int channel, int controller, int value) => (0xB0 | (channel & 15), controller & 127, Math.Clamp(value, 0, 127));

        public const int CcVolume = 7;
        public const int CcReverb = 91;
        public const int CcSustain = 64;
        public const int CcAllSoundOff = 120;
        public const int CcAllNotesOff = 123;

        // ---------------- Instruments ----------------

        public static MidiInstrument FindInstrument(string? key) =>
            MidiInstrumentCatalog.All.FirstOrDefault(i => i.Key == key) ?? MidiInstrumentCatalog.Default;

        /// <summary>
        /// Instruments for the list: the chosen family, or - when there's
        /// search text - every instrument whose name or family has all the
        /// words ("elec bass", "sax", "drum").
        /// </summary>
        public static List<MidiInstrument> Filter(string? search, MidiInstrumentFamily? family)
        {
            var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0)
                return MidiInstrumentCatalog.All.Where(i => family == null || i.Family.Index == family.Index).ToList();
            return MidiInstrumentCatalog.All
                .Where(i => words.All(w => (i.Name + " " + i.Family.Name).Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // ---------------- .mid files ----------------

        public const int TicksPerQuarter = 480;
        public const int MicrosecondsPerQuarter = 500_000;   // 120 BPM: 1 tick = 1.0417 ms

        public static long MsToTicks(long ms) => (long)Math.Round(ms * 1000.0 * TicksPerQuarter / MicrosecondsPerQuarter);

        /// <summary>MIDI's variable-length number: 7 bits per byte, high bit = "more follows".</summary>
        public static byte[] VariableLength(long value)
        {
            if (value < 0) value = 0;
            if (value > 0x0FFFFFFF) value = 0x0FFFFFFF;
            var bytes = new Stack<byte>();
            bytes.Push((byte)(value & 0x7F));
            while ((value >>= 7) > 0) bytes.Push((byte)((value & 0x7F) | 0x80));
            return bytes.ToArray();
        }

        /// <summary>
        /// A Standard MIDI File (format 0, one track, 120 BPM, 480 ticks per
        /// beat): track name, then the recorded messages with their timing,
        /// then end-of-track. Any DAW, notation app or media player opens it.
        /// </summary>
        public static byte[] BuildMidiFile(MidiRecording recording)
        {
            var track = new List<byte>();
            void Meta(int type, byte[] data)
            {
                track.Add(0x00);
                track.Add(0xFF);
                track.Add((byte)type);
                track.AddRange(VariableLength(data.Length));
                track.AddRange(data);
            }

            string title = string.IsNullOrWhiteSpace(recording.InstrumentName) ? "Smart Tools recording" : recording.InstrumentName;
            Meta(0x03, Encoding.ASCII.GetBytes(new string(title.Select(c => c < 128 ? c : '?').ToArray())));
            Meta(0x51, new byte[] { (MicrosecondsPerQuarter >> 16) & 0xFF, (MicrosecondsPerQuarter >> 8) & 0xFF, MicrosecondsPerQuarter & 0xFF });
            Meta(0x58, new byte[] { 4, 2, 24, 8 });   // 4/4

            long lastTick = 0;
            foreach (var e in recording.Events.OrderBy(e => e.TimeMs))   // OrderBy is stable: same-time messages keep their order
            {
                long tick = MsToTicks(e.TimeMs);
                track.AddRange(VariableLength(tick - lastTick));
                lastTick = tick;
                track.Add(e.Status);
                track.Add(e.Data1);
                if (!IsTwoByte(e.Status)) track.Add(e.Data2);
            }
            long endTick = Math.Max(lastTick, MsToTicks(recording.DurationMs));
            track.AddRange(VariableLength(endTick - lastTick));
            track.AddRange(new byte[] { 0xFF, 0x2F, 0x00 });

            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("MThd"));
            file.AddRange(BigEndian(6, 4));
            file.AddRange(BigEndian(0, 2));                 // format 0
            file.AddRange(BigEndian(1, 2));                 // one track
            file.AddRange(BigEndian(TicksPerQuarter, 2));
            file.AddRange(Encoding.ASCII.GetBytes("MTrk"));
            file.AddRange(BigEndian(track.Count, 4));
            file.AddRange(track);
            return file.ToArray();
        }

        /// <summary>Program change and channel pressure carry one data byte, the rest two.</summary>
        public static bool IsTwoByte(byte status) => (status & 0xF0) is 0xC0 or 0xD0;

        private static byte[] BigEndian(long value, int bytes)
        {
            var b = new byte[bytes];
            for (int i = bytes - 1; i >= 0; i--, value >>= 8) b[i] = (byte)(value & 0xFF);
            return b;
        }

        /// <summary>Writes through a temp file so a failed save never leaves half a file.</summary>
        public static void SaveMidiFile(MidiRecording recording, string path)
        {
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, BuildMidiFile(recording));
            File.Move(temp, path, overwrite: true);
        }

        public static string SuggestedFileName(DateTime now, string instrumentName)
        {
            string safe = new string(instrumentName.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '/' && c != '\\').ToArray()).Trim();
            return $"{(safe.Length > 0 ? safe : "Recording")} {now:yyyy-MM-dd HHmm}.mid";
        }

        public static string FormatDuration(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        // ---------------- Settings ----------------

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "midi_instruments.json");

        public static MidiInstrumentsSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<MidiInstrumentsSettings>(File.ReadAllText(file), JsonOptions) : null;
                return Sanitize(s ?? new MidiInstrumentsSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new MidiInstrumentsSettings();
            }
        }

        public static MidiInstrumentsSettings Sanitize(MidiInstrumentsSettings s)
        {
            s.Instrument = FindInstrument(s.Instrument).Key;
            s.Octave = Math.Clamp(s.Octave, MinOctave, MaxOctave);
            s.Velocity = Math.Clamp(s.Velocity, 1, 127);
            s.Volume = Math.Clamp(s.Volume, 0, 127);
            s.Reverb = Math.Clamp(s.Reverb, 0, 127);
            if (!Enum.IsDefined(s.Touch)) s.Touch = MidiTouchMode.Fixed;
            s.OutputDevice ??= "";
            s.InputDevice ??= "";
            return s;
        }

        public static void SaveSettings(MidiInstrumentsSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
