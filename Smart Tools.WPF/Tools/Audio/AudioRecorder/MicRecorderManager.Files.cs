using Smart_Tools.WPF.Models;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Audio Recorder: the pure parts - level meter maths, file names, the
    /// temporary folder, durations and settings. No NAudio, no WPF, so it's
    /// unit-tested on its own.
    /// </summary>
    public sealed partial class MicRecorderManager
    {
        public static readonly int[] SampleRates = { 16000, 22050, 44100, 48000 };
        public static readonly int[] BitDepths = { 16, 24 };

        /// <summary>The remembered microphone if it's still plugged in, else the first one.</summary>
        public static MicRecorderDevice? PreferredDevice(IReadOnlyList<MicRecorderDevice> devices, string? savedName) =>
            devices.FirstOrDefault(d => d.Name == savedName) ?? devices.FirstOrDefault();

        /// <summary>Music\Smart Tools\Recordings - used until the user picks another folder this session.</summary>
        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Smart Tools", "Recordings");

        /// <summary>Where takes live until they're saved. Its own folder, so cleaning up never touches anything else.</summary>
        public static string TempFolder => Path.Combine(Path.GetTempPath(), "Smart Tools Recorder");

        public static string NewTempPath() => Path.Combine(TempFolder, $"take-{Guid.NewGuid():N}.wav");

        /// <summary>True only for files in our own temporary folder - the only files this tool ever deletes.</summary>
        public static bool IsTempTake(string path)
        {
            string full = Path.GetFullPath(path);
            string temp = Path.GetFullPath(TempFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Loudest sample in a buffer as 0-100 for the level meter, for 16-bit,
        /// 24-bit and 32-bit float. The old meter only understood 16-bit (it
        /// sat at 0 when recording in 24-bit), and read -32768 as -32768 after
        /// "making it positive", so a clipping signal could show as silence.
        /// </summary>
        public static double PeakLevel(byte[] buffer, int count, int bitsPerSample, bool isFloat = false)
        {
            double max = 0;
            count = Math.Min(count, buffer.Length);
            if (isFloat && bitsPerSample == 32)
            {
                for (int i = 0; i + 3 < count; i += 4)
                    max = Math.Max(max, Math.Abs(BitConverter.ToSingle(buffer, i)));
            }
            else if (bitsPerSample == 16)
            {
                for (int i = 0; i + 1 < count; i += 2)
                    max = Math.Max(max, Math.Abs((int)BitConverter.ToInt16(buffer, i)) / 32768.0);
            }
            else if (bitsPerSample == 24)
            {
                for (int i = 0; i + 2 < count; i += 3)
                {
                    int sample = buffer[i] | buffer[i + 1] << 8 | (sbyte)buffer[i + 2] << 16;
                    max = Math.Max(max, Math.Abs(sample) / 8388608.0);
                }
            }
            else if (bitsPerSample == 32)
            {
                for (int i = 0; i + 3 < count; i += 4)
                    max = Math.Max(max, Math.Abs((long)BitConverter.ToInt32(buffer, i)) / 2147483648.0);
            }
            return Math.Min(100, max * 100);
        }

        /// <summary>The meter in decibels feels right to the ear; this maps -60..0 dBFS onto 0..100.</summary>
        public static double MeterPercent(double peakPercent)
        {
            if (peakPercent <= 0) return 0;
            double db = 20 * Math.Log10(peakPercent / 100);
            return Math.Clamp((db + 60) / 60 * 100, 0, 100);
        }

        /// <summary>Length from the bytes actually written (the old one used the wall clock, which counted the device warming up).</summary>
        public static TimeSpan DurationOf(long dataBytes, MicRecorderFormat format) =>
            format.BytesPerSecond <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)dataBytes / format.BytesPerSecond);

        /// <summary>
        /// The file name for a take: what the user typed (".wav" added, bad
        /// characters replaced), or "Recording 2026-10-05 14-03-22.wav".
        /// </summary>
        public static string TakeFileName(string? typed, DateTime now)
        {
            string stem = (typed ?? "").Trim();
            if (stem.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];   // "take.v2" keeps its dot
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            stem = new string(stem.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (stem.Length == 0) stem = $"Recording {now:yyyy-MM-dd HH-mm-ss}";
            if (stem.Length > 120) stem = stem[..120];
            return stem + ".wav";
        }

        /// <summary>
        /// The path itself if free, else "name (2).wav", "name (3).wav"...
        /// The old auto-save used the date and time to the second and
        /// overwrote: two takes saved in the same second (or a same-named
        /// file already there) silently replaced the earlier one.
        /// </summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "", stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Moves a take into a folder under a free name. A temporary take is
        /// moved (the old auto-save copied it and left the temp file behind,
        /// one per recording); a take already saved elsewhere is copied.
        /// </summary>
        public static string SaveTake(MicRecorderTake take, string folder, string? typedName, DateTime now)
        {
            Directory.CreateDirectory(folder);
            string target = UniquePath(Path.Combine(folder, TakeFileName(typedName, now)));
            if (IsTempTake(take.FilePath)) File.Move(take.FilePath, target);
            else File.Copy(take.FilePath, target);
            take.FilePath = target;
            take.IsSaved = true;
            return target;
        }

        /// <summary>Save as: to the exact path the user picked in the Save dialog (which already asked about replacing).</summary>
        public static void SaveTakeAs(MicRecorderTake take, string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(take.FilePath), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (IsTempTake(take.FilePath)) File.Move(take.FilePath, path, overwrite: true);
            else File.Copy(take.FilePath, path, overwrite: true);
            take.FilePath = path;
            take.IsSaved = true;
        }

        /// <summary>Deletes a take's file only if it's one of our temporary ones (a saved recording is the user's file).</summary>
        public static bool DeleteIfTemp(string path)
        {
            try
            {
                if (!IsTempTake(path) || !File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Leftover temporary takes from a crash or power cut: removed when the tool starts, if older than a day.</summary>
        public static int CleanOldTempTakes(DateTime now)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(TempFolder)) return 0;
                foreach (var file in new DirectoryInfo(TempFolder).GetFiles("take-*.wav"))
                {
                    if (now - file.LastWriteTime < TimeSpan.FromDays(1)) continue;
                    try { file.Delete(); removed++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // nothing to clean
            }
            return removed;
        }

        // ---------------- Settings ----------------

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "mic_recorder.json");

        public static MicRecorderSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<MicRecorderSettings>(File.ReadAllText(file), JsonOptions) : null;
                return Sanitize(s ?? new MicRecorderSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new MicRecorderSettings();
            }
        }

        public static MicRecorderSettings Sanitize(MicRecorderSettings s)
        {
            if (!SampleRates.Contains(s.SampleRate)) s.SampleRate = 44100;
            if (s.Channels is not (1 or 2)) s.Channels = 1;
            if (!BitDepths.Contains(s.BitsPerSample)) s.BitsPerSample = 16;
            s.DeviceName ??= "";
            s.FileName = Path.GetFileName(s.FileName ?? "");   // a name, never a path
            return s;
        }

        public static void SaveSettings(MicRecorderSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(Sanitize(settings), JsonOptions));
        }
    }
}
