using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Microphone test: the pure parts - decibels, the verdict and settings. No NAudio, no WPF,
    /// so it can be tested on its own.
    /// </summary>
    public sealed partial class MicTestManager
    {
        /// <summary>The quietest level shown. Anything below reads as this.</summary>
        public const double FloorDb = -90;

        /// <summary>A buffer whose loudest sample is at or above this (percent of full scale) counts as clipped.</summary>
        public const double ClipPercent = 99;

        /// <summary>Linear 0-100 to decibels relative to full scale, never below <see cref="FloorDb"/>.</summary>
        public static double ToDb(double percent) =>
            percent <= 0 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(percent / 100));

        /// <summary>Judges the sound heard so far. The first second is "waiting" while the device warms up.</summary>
        public static MicTestVerdict Judge(MicTestSnapshot s)
        {
            if (s.Elapsed < TimeSpan.FromSeconds(1.5)) return MicTestVerdict.Waiting;
            double peakDb = ToDb(s.PeakPercent);
            if (peakDb < -50) return MicTestVerdict.Silent;
            if (s.ClippedBuffers > 0) return MicTestVerdict.Clipping;
            if (peakDb < -24) return MicTestVerdict.TooQuiet;
            if (peakDb > -3) return MicTestVerdict.TooLoud;
            return MicTestVerdict.Good;
        }

        // ---------------- Settings ----------------

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "mic_test.json");

        public static MicTestSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<MicTestSettings>(File.ReadAllText(file), JsonOptions) : null;
                s ??= new MicTestSettings();
                s.DeviceName ??= "";
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new MicTestSettings();
            }
        }

        public static void SaveSettings(MicTestSettings settings, string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A setting that doesn't stick is not worth interrupting the test for.
            }
        }
    }
}
