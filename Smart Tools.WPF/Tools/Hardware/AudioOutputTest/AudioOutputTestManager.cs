using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Audio Output Tester backend - the portable half: channel names, level maths and settings.
    /// The signals are in <see cref="AudioSignalGenerator"/>; the sound card side (WASAPI) is in
    /// <c>AudioOutputTestManager.Audio.cs</c>.
    /// </summary>
    public static partial class AudioOutputTestManager
    {
        public const double MinLevelDb = -60, MaxLevelDb = -6, DefaultLevelDb = -30;

        // Speaker positions in Windows channel-mask order: the Nth set bit is the Nth channel of the stream.
        private static readonly string[] SpeakerNames =
        {
            "Front Left", "Front Right", "Front Center", "Subwoofer", "Back Left", "Back Right",
            "Front Left of Center", "Front Right of Center", "Back Center", "Side Left", "Side Right",
            "Top Center", "Top Front Left", "Top Front Center", "Top Front Right", "Top Back Left", "Top Back Center", "Top Back Right",
        };

        /// <summary>Short labels shown on the channel tiles ("L", "R", "C", "LFE", ...).</summary>
        private static readonly string[] SpeakerShort =
        {
            "FL", "FR", "C", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR", "TC", "TFL", "TFC", "TFR", "TBL", "TBC", "TBR",
        };

        /// <summary>The standard layout Windows uses for a channel count when the device gives no speaker mask.</summary>
        public static uint DefaultMask(int channels) => channels switch
        {
            1 => 0x4,
            2 => 0x3,
            3 => 0x7,
            4 => 0x33,
            5 => 0x37,
            6 => 0x3F,
            7 => 0x13F,
            8 => 0x63F,
            _ => (uint)((1UL << Math.Min(channels, 32)) - 1),
        };

        /// <summary>The full and short name of every channel, in stream order.</summary>
        public static IReadOnlyList<(string Name, string Short)> ChannelNames(int channels, uint speakerMask)
        {
            if (channels == 2 && (speakerMask == 0 || speakerMask == 0x3))
                return new[] { ("Left", "L"), ("Right", "R") };
            if (channels == 1) return new[] { ("Mono", "M") };

            uint mask = speakerMask;
            if (mask == 0 || BitCount(mask) != channels) mask = DefaultMask(channels);

            var list = new List<(string, string)>();
            for (int bit = 0; bit < 32 && list.Count < channels; bit++)
            {
                if ((mask & (1u << bit)) == 0) continue;
                list.Add(bit < SpeakerNames.Length ? (SpeakerNames[bit], SpeakerShort[bit]) : ($"Channel {list.Count + 1}", $"{list.Count + 1}"));
            }
            while (list.Count < channels) list.Add(($"Channel {list.Count + 1}", $"{list.Count + 1}"));
            return list;
        }

        private static int BitCount(uint v)
        {
            int n = 0;
            for (; v != 0; v &= v - 1) n++;
            return n;
        }

        public static string FormatDb(double db) => string.Create(CultureInfo.CurrentCulture, $"{db:0} dB");

        public static string FormatFrequency(double hz) => hz >= 1000
            ? string.Create(CultureInfo.CurrentCulture, $"{hz / 1000:0.##} kHz")
            : string.Create(CultureInfo.CurrentCulture, $"{hz:0} Hz");

        /// <summary>Slider position 0..1 to a pitch, 20 Hz to 20 kHz, so each octave gets the same width.</summary>
        public static double SliderToFrequency(double t) =>
            AudioSignalGenerator.SweepLow * Math.Pow(AudioSignalGenerator.SweepHigh / AudioSignalGenerator.SweepLow, Math.Clamp(t, 0, 1));

        public static double FrequencyToSlider(double hz) =>
            Math.Log(Math.Clamp(hz, AudioSignalGenerator.SweepLow, AudioSignalGenerator.SweepHigh) / AudioSignalGenerator.SweepLow)
            / Math.Log(AudioSignalGenerator.SweepHigh / AudioSignalGenerator.SweepLow);

        /// <summary>All channels on, as a mask.</summary>
        public static uint AllChannels(int channels) => channels >= 32 ? uint.MaxValue : (1u << channels) - 1;

        // =================================================================
        //  Settings (how the tool is set up - never the device)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "audio_output_test.json");

        public static AudioOutputTestSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new AudioOutputTestSettings();
                var s = JsonSerializer.Deserialize<AudioOutputTestSettings>(File.ReadAllText(file), JsonOptions) ?? new AudioOutputTestSettings();
                if (!Enum.IsDefined(s.Kind)) s.Kind = AudioTestKind.Channels;
                if (!Enum.IsDefined(s.Waveform)) s.Waveform = AudioWaveform.Sine;
                if (!Enum.IsDefined(s.Phase)) s.Phase = AudioPhaseMode.Alternate;
                if (!Enum.IsDefined(s.Noise)) s.Noise = AudioNoiseColor.Pink;
                s.LevelDb = Math.Clamp(s.LevelDb, MinLevelDb, MaxLevelDb);   // a saved loud level never surprises the next session
                s.Frequency = Math.Clamp(s.Frequency, AudioSignalGenerator.SweepLow, AudioSignalGenerator.SweepHigh);
                s.SweepSeconds = Math.Clamp(s.SweepSeconds, 4, 40);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return new AudioOutputTestSettings();
            }
        }

        public static void SaveSettings(AudioOutputTestSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
