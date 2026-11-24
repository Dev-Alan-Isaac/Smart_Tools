using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Voice Changer: the pure parts - finding audio files, the Files card
    /// list, effect summaries, result names, the clipping guard, the
    /// temporary folder and settings. No NAudio, no WPF, so it's
    /// unit-tested on its own.
    /// </summary>
    public sealed partial class VoiceChangerManager
    {
        public static readonly IReadOnlyCollection<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".wma", ".aac", ".flac", ".m4a", ".aiff", ".aif", ".ogg",
        };

        public const string FileDialogFilter = "Audio files|*.wav;*.mp3;*.wma;*.aac;*.flac;*.m4a;*.aiff;*.aif;*.ogg|All files|*.*";

        public static bool IsAudioFile(string path) => AudioExtensions.Contains(Path.GetExtension(path));

        // ---------------- Finding files ----------------

        /// <summary>
        /// Audio files in a folder (and its subfolders if asked), off the UI
        /// thread, sorted by name. A folder Windows won't open is counted and
        /// skipped; a folder that no longer exists is reported as Missing.
        /// The old scan ran on the UI thread and threw on either - one locked
        /// subfolder lost the whole folder, and a deleted folder crashed the page.
        /// </summary>
        public static Task<VoiceChangerScan> ScanFolderAsync(string folder, bool includeSubfolders, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var scan = new VoiceChangerScan();
                if (!Directory.Exists(folder))
                {
                    scan.Missing = true;
                    return scan;
                }
                var pending = new Stack<string>();
                pending.Push(folder);
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    string dir = pending.Pop();
                    try
                    {
                        scan.Files.AddRange(Directory.EnumerateFiles(dir).Where(IsAudioFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                        if (!includeSubfolders) continue;
                        foreach (string sub in Directory.EnumerateDirectories(dir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                        {
                            var attrs = File.GetAttributes(sub);
                            if (!attrs.HasFlag(FileAttributes.ReparsePoint)) pending.Push(sub);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        scan.InaccessibleFolders++;
                    }
                }
                return scan;
            }, token);

        /// <summary>
        /// Picked or dropped files that are audio and exist. The dialog's
        /// "All files" choice let anything into the old queue (a .txt then
        /// failed mid-batch).
        /// </summary>
        public static (List<string> Audio, int Skipped) SupportedFiles(IEnumerable<string> paths)
        {
            var audio = new List<string>();
            int skipped = 0;
            foreach (string p in paths)
            {
                if (IsAudioFile(p) && File.Exists(p)) audio.Add(p);
                else skipped++;
            }
            return (audio, skipped);
        }

        // ---------------- The Files card ----------------

        /// <summary>Adds files not already listed as one Files card row. Returns the new items.</summary>
        public static List<VoiceChangerSource> AddToList(
            IList<VoiceChangerSource> list,
            ICollection<VoiceChangerGroup> groups,
            IEnumerable<string> paths,
            string? folderPath,
            bool isFolder,
            bool includeSubfolders)
        {
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = paths.Where(known.Add).Select(p => new VoiceChangerSource { FullPath = p }).ToList();
            if (added.Count == 0) return added;

            var folders = added.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var group = new VoiceChangerGroup
            {
                FolderPath = folderPath ?? (folders.Count == 1 ? folders[0] : null),
                IsFolder = isFolder,
                IncludeSubfolders = includeSubfolders,
            };
            foreach (var item in added)
            {
                item.Group = group;
                group.Items.Add(item);
                list.Add(item);
            }
            groups.Add(group);
            return added;
        }

        public static void RemoveFromList(IList<VoiceChangerSource> list, ICollection<VoiceChangerGroup> groups, IReadOnlyCollection<VoiceChangerSource> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<VoiceChangerSource>(items);
            foreach (var item in items) list.Remove(item);
            foreach (var group in items.Select(i => i.Group).OfType<VoiceChangerGroup>().Distinct().ToList())
            {
                group.Items.RemoveAll(gone.Contains);
                if (group.Items.Count == 0) groups.Remove(group);
                else group.Refresh();
            }
        }

        /// <summary>After "Include subfolders" changes: files no longer found come off, new ones go at the end, the rest stay.</summary>
        public static (List<VoiceChangerSource> Added, List<VoiceChangerSource> Removed) ApplyRescan(
            IList<VoiceChangerSource> list,
            ICollection<VoiceChangerGroup> groups,
            VoiceChangerGroup group,
            IReadOnlyCollection<string> foundPaths,
            bool includeSubfolders)
        {
            var found = new HashSet<string>(foundPaths, StringComparer.OrdinalIgnoreCase);
            var gone = group.Items.Where(i => !found.Contains(i.FullPath)).ToList();
            foreach (var item in gone)
            {
                list.Remove(item);
                group.Items.Remove(item);
            }
            var known = new HashSet<string>(list.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = foundPaths.Where(known.Add).Select(p => new VoiceChangerSource { FullPath = p, Group = group }).ToList();
            foreach (var item in added)
            {
                group.Items.Add(item);
                list.Add(item);
            }
            group.IncludeSubfolders = includeSubfolders;
            if (group.Items.Count == 0) groups.Remove(group);
            return (added, gone);
        }

        // ---------------- Effect text and names ----------------

        /// <summary>"+5 st · Robot · Echo", or "No effect".</summary>
        public static string Summarize(VoiceChangerEffect e)
        {
            var parts = new List<string>();
            if (Math.Abs(e.PitchSemitones) >= 0.05)
                parts.Add((e.PitchSemitones > 0 ? "+" : "−") + Math.Abs(e.PitchSemitones).ToString("0.#", CultureInfo.InvariantCulture) + " st");
            if (e.Robot) parts.Add("Robot");
            if (e.Echo) parts.Add("Echo");
            return parts.Count == 0 ? "No effect" : string.Join(" · ", parts);
        }

        /// <summary>"No shift", "+5 semitones", "−2.5 semitones" for the pitch slider.</summary>
        public static string DescribePitch(double semitones) =>
            Math.Abs(semitones) < 0.05 ? "No shift"
            : (semitones > 0 ? "+" : "−") + Math.Abs(semitones).ToString("0.#", CultureInfo.InvariantCulture)
              + (Math.Abs(Math.Abs(semitones) - 1) < 0.05 ? " semitone" : " semitones");

        /// <summary>
        /// The result's name: "song (+5 st, Robot).wav" - the old results
        /// were "smarttools-voice-3f2a….wav", so a list of renders couldn't be
        /// told apart and Save As suggested that name.
        /// </summary>
        public static string ResultFileName(string sourcePath, VoiceChangerEffect effect)
        {
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string summary = Summarize(effect).Replace(" · ", ", ").Replace('−', '-');
            string name = $"{stem} ({summary})";
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            if (name.Length > 150) name = name[..150];
            return name + ".wav";
        }

        public static string UniquePath(string path) => Smart_Tools.WPF.Controllers.SafeFileOps.UniquePath(path);

        // ---------------- Clipping guard ----------------

        /// <summary>
        /// Pitch and echo can push samples past full scale; written to a
        /// 16-bit WAV those get cut off flat (a crackle on every loud
        /// syllable). If the loudest sample is over <paramref name="ceiling"/>,
        /// the whole take is turned down to it. Returns the gain used (1 = untouched).
        /// </summary>
        public static double LimitPeak(float[] samples, double ceiling = 0.98)
        {
            float peak = 0;
            foreach (float s in samples)
            {
                float a = Math.Abs(s);
                if (a > peak && !float.IsNaN(a) && !float.IsInfinity(a)) peak = a;
            }
            for (int i = 0; i < samples.Length; i++)
                if (float.IsNaN(samples[i]) || float.IsInfinity(samples[i])) samples[i] = 0;   // a broken effect sample would otherwise be noise
            if (peak <= ceiling) return 1;
            double gain = ceiling / peak;
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)(samples[i] * gain);
            return gain;
        }

        // ---------------- Results folder ----------------

        /// <summary>Results wait here until saved; only files in this folder are ever deleted by the tool.</summary>
        public static string TempFolder => Path.Combine(Path.GetTempPath(), "Smart Tools Voice Changer");

        public static string DefaultSaveFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Smart Tools", "Voice Changer");

        /// <summary>A free path in our temp folder for a result (each render gets its own subfolder so names never collide).</summary>
        public static string NewResultPath(string sourcePath, VoiceChangerEffect effect)
        {
            string dir = Path.Combine(TempFolder, Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, ResultFileName(sourcePath, effect));
        }

        public static bool IsTempResult(string path)
        {
            string full = Path.GetFullPath(path);
            string temp = Path.GetFullPath(TempFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Deletes a result only if it's still in our temp folder (and its now-empty subfolder).</summary>
        public static bool DeleteIfTemp(string path)
        {
            try
            {
                if (!IsTempResult(path) || !File.Exists(path)) return false;
                File.Delete(path);
                string? dir = Path.GetDirectoryName(path);
                if (dir != null && IsTempResult(Path.Combine(dir, "x")) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Saves a result: moved out of the temp folder (or copied if already saved) to the exact path chosen.</summary>
        public static void SaveResultAs(VoiceChangerTake take, string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(take.FilePath), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string old = take.FilePath;
            if (IsTempResult(old))
            {
                File.Move(old, path, overwrite: true);
                string? dir = Path.GetDirectoryName(old);
                try { if (dir != null && IsTempResult(Path.Combine(dir, "x")) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (IOException) { /* best effort */ }
            }
            else
            {
                File.Copy(old, path, overwrite: true);
            }
            take.FilePath = path;
            take.IsSaved = true;
        }

        /// <summary>Saves into a folder under the result's own name, never replacing anything ("(2)" if taken).</summary>
        public static string SaveResultTo(VoiceChangerTake take, string folder)
        {
            string target = UniquePath(Path.Combine(folder, take.DisplayName));
            SaveResultAs(take, target);
            return target;
        }

        /// <summary>Left over after a crash: result subfolders older than a day are removed when the tool starts.</summary>
        public static int CleanOldResults(DateTime now)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(TempFolder)) return 0;
                foreach (var dir in new DirectoryInfo(TempFolder).GetDirectories())
                {
                    if (now - dir.LastWriteTime < TimeSpan.FromDays(1)) continue;
                    try { dir.Delete(recursive: true); removed++; }
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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "voice_changer.json");

        public static VoiceChangerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<VoiceChangerSettings>(File.ReadAllText(file), JsonOptions) : null;
                return Sanitize(s ?? new VoiceChangerSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new VoiceChangerSettings();
            }
        }

        public static VoiceChangerSettings Sanitize(VoiceChangerSettings s)
        {
            if (!Enum.IsDefined(s.Mode)) s.Mode = VoiceChangerMode.Files;
            s.PitchSemitones = Clamp(s.PitchSemitones, -12, 12, 0);
            s.EchoDelayMs = Clamp(s.EchoDelayMs, 50, 600, 220);
            s.EchoFeedback = Clamp(s.EchoFeedback, 0, 0.85, 0.35);
            s.EchoMix = Clamp(s.EchoMix, 0, 1, 0.35);
            s.InputDevice ??= "";
            return s;
        }

        private static double Clamp(double v, double min, double max, double fallback) =>
            double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

        public static void SaveSettings(VoiceChangerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Sanitize(settings), JsonOptions));
            File.Move(temp, file, overwrite: true);
        }

        public static VoiceChangerEffect EffectFrom(VoiceChangerSettings s) =>
            new(s.PitchSemitones, s.Robot, s.Echo, s.EchoDelayMs, s.EchoFeedback, s.EchoMix);

        /// <summary>Number -1 is Windows' default output (the speakers or headphones in use).</summary>
        public const int DefaultOutputNumber = -1;

        public static VoiceChangerDevice DefaultOutput => new(DefaultOutputNumber, "Speakers / headphones (Windows default)");

        /// <summary>
        /// Virtual audio cables: a playback device whose sound comes out of a
        /// matching recording device that other apps pick as their microphone.
        /// VB-Audio's is "CABLE Input"; VoiceMeeter's inputs work the same way.
        /// </summary>
        public static bool IsVirtualCable(string name) =>
            name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase);

        /// <summary>The remembered output if it's still there, else Windows' default (never a cable by surprise).</summary>
        public static VoiceChangerDevice PreferredOutput(IReadOnlyList<VoiceChangerDevice> outputs, string? savedName) =>
            outputs.FirstOrDefault(d => d.Name == savedName && !string.IsNullOrEmpty(savedName))
            ?? outputs.FirstOrDefault(d => d.Number == DefaultOutputNumber)
            ?? DefaultOutput;

        /// <summary>What the other app should pick as its microphone for this output: "CABLE Input" plays into "CABLE Output".</summary>
        public static string MicrophoneNameFor(string outputName)
        {
            if (outputName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                return outputName.Replace("CABLE Input", "CABLE Output", StringComparison.OrdinalIgnoreCase);
            if (outputName.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase))
                return "VoiceMeeter Output";
            if (outputName.Contains("VoiceMeeter Aux Input", StringComparison.OrdinalIgnoreCase))
                return "VoiceMeeter Aux Output";
            return "the cable's recording side";
        }

        /// <summary>The remembered microphone if it's still plugged in, else the first one.</summary>
        /// <remarks>A virtual cable's recording side is never picked by itself: listening to the cable you're playing into would loop.</remarks>
        public static VoiceChangerDevice? PreferredDevice(IReadOnlyList<VoiceChangerDevice> devices, string? savedName) =>
            devices.FirstOrDefault(d => d.Name == savedName)
            ?? devices.FirstOrDefault(d => !IsVirtualCable(d.Name))
            ?? devices.FirstOrDefault();

        /// <summary>Microphone and output are both virtual cables: the voice would feed back into itself.</summary>
        public static bool IsLoop(VoiceChangerDevice input, VoiceChangerDevice output) =>
            IsVirtualCable(input.Name) && IsVirtualCable(output.Name);
    }
}
