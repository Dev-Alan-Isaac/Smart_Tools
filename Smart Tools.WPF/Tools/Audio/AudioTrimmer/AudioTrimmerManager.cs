using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Audio Trimmer &amp; Joiner - the pure half (no WPF, no processes): cutting, splitting and
    /// trimming the list of clips, undo, and the ffmpeg filter graph that turns the list into one
    /// file. The engine half (ffprobe, waveform, export) is in AudioTrimmerManager.Engine.cs.
    ///
    /// Editing never touches the files: a clip is only "this part of that file", so every change is
    /// a few numbers and undo is cheap. Only Export reads audio and writes a file.
    /// </summary>
    public static partial class AudioTrimmerManager
    {
        /// <summary>Anything shorter than this is not worth a clip.</summary>
        public const double MinClipSeconds = 0.02;

        public const int MaxClips = 200;
        public const double MaxFadeSeconds = 10;

        /// <summary>A very short fade where two pieces meet, so a cut in the middle of a wave doesn't click.</summary>
        public const double JoinFadeSeconds = 0.005;

        /// <summary>Peak normalize aims here (just under full scale, so nothing clips).</summary>
        public const double PeakTargetDb = -1.0;

        // =================================================================
        //  Editing the clip list
        // =================================================================

        public static void Renumber(IList<AudioTrimClip> clips)
        {
            for (int i = 0; i < clips.Count; i++) clips[i].Number = i + 1;
        }

        private static (double A, double B) Order(double a, double b) => a <= b ? (a, b) : (b, a);

        /// <summary>
        /// The clip becomes exactly the range a..b of its file (this can win back audio that was trimmed
        /// off earlier). False, and nothing changed, when the range is too short.
        /// </summary>
        public static bool TrimTo(AudioTrimClip clip, double a, double b)
        {
            (a, b) = Order(a, b);
            a = Math.Max(0, a);
            b = Math.Min(clip.Source.Duration, b);
            if (b - a < MinClipSeconds) return false;
            clip.SetRange(a, b);
            return true;
        }

        /// <summary>The clip is the whole file again.</summary>
        public static void ResetRange(AudioTrimClip clip) => clip.SetRange(0, clip.Source.Duration);

        /// <summary>
        /// Takes a..b out of the clip. In the middle of a clip this leaves two clips (the second is
        /// added right after it); at an end it just shortens it; over all of it the clip goes. Returns the
        /// clip to select next (the first piece, else the second, else a neighbour or null).
        /// <paramref name="changed"/> is false when the range doesn't touch the clip.
        /// </summary>
        public static AudioTrimClip? CutOut(IList<AudioTrimClip> clips, AudioTrimClip clip, double a, double b, out bool changed)
        {
            (a, b) = Order(a, b);
            double ca = Math.Max(a, clip.Start), cb = Math.Min(b, clip.End);
            changed = cb - ca >= MinClipSeconds;
            if (!changed) return clip;

            bool keepLeft = ca - clip.Start >= MinClipSeconds, keepRight = clip.End - cb >= MinClipSeconds;
            int index = clips.IndexOf(clip);

            if (keepLeft && keepRight)
            {
                if (clips.Count >= MaxClips) { changed = false; return clip; }
                var right = new AudioTrimClip(clip.Source, cb, clip.End);
                clip.SetRange(clip.Start, ca);
                clips.Insert(index + 1, right);
                Renumber(clips);
                return clip;
            }
            if (keepLeft) { clip.SetRange(clip.Start, ca); return clip; }
            if (keepRight) { clip.SetRange(cb, clip.End); return clip; }

            clips.RemoveAt(index);
            Renumber(clips);
            return clips.Count == 0 ? null : clips[Math.Min(index, clips.Count - 1)];
        }

        /// <summary>
        /// Splits the clip in two at <paramref name="t"/> (seconds of the file). False when that is too close
        /// to either end or the list is full.
        /// </summary>
        public static bool Split(IList<AudioTrimClip> clips, AudioTrimClip clip, double t, out AudioTrimClip? second)
        {
            second = null;
            if (t - clip.Start < MinClipSeconds || clip.End - t < MinClipSeconds || clips.Count >= MaxClips) return false;
            second = new AudioTrimClip(clip.Source, t, clip.End);
            clip.SetRange(clip.Start, t);
            clips.Insert(clips.IndexOf(clip) + 1, second);
            Renumber(clips);
            return true;
        }

        public static double TotalSeconds(IEnumerable<AudioTrimClip> clips) => clips.Sum(c => c.Duration);

        public static List<AudioTrimClipState> Snapshot(IEnumerable<AudioTrimClip> clips) =>
            clips.Select(c => new AudioTrimClipState(c.Source, c.Start, c.End)).ToList();

        // =================================================================
        //  Undo
        // =================================================================

        /// <summary>
        /// Remembers the clip list before each change. Call <see cref="Push"/> BEFORE changing; Undo and
        /// Redo hand back the list to show (and which clip was selected).
        /// </summary>
        public sealed class History
        {
            public sealed record Entry(IReadOnlyList<AudioTrimClipState> Clips, int Selected);

            private const int Limit = 100;
            private readonly List<Entry> _undo = new();
            private readonly List<Entry> _redo = new();

            public bool CanUndo => _undo.Count > 0;
            public bool CanRedo => _redo.Count > 0;

            public void Push(IReadOnlyList<AudioTrimClipState> clips, int selected)
            {
                _undo.Add(new Entry(clips, selected));
                if (_undo.Count > Limit) _undo.RemoveAt(0);
                _redo.Clear();
            }

            public Entry? Undo(IReadOnlyList<AudioTrimClipState> current, int selected)
            {
                if (_undo.Count == 0) return null;
                var e = _undo[^1];
                _undo.RemoveAt(_undo.Count - 1);
                _redo.Add(new Entry(current, selected));
                return e;
            }

            public Entry? Redo(IReadOnlyList<AudioTrimClipState> current, int selected)
            {
                if (_redo.Count == 0) return null;
                var e = _redo[^1];
                _redo.RemoveAt(_redo.Count - 1);
                _undo.Add(new Entry(current, selected));
                return e;
            }

            public void Clear()
            {
                _undo.Clear();
                _redo.Clear();
            }
        }

        // =================================================================
        //  Export planning
        // =================================================================

        public static string FileExtension(AudioTrimFormat f) => f switch
        {
            AudioTrimFormat.Wav => "wav",
            AudioTrimFormat.Flac => "flac",
            AudioTrimFormat.M4a => "m4a",
            AudioTrimFormat.Ogg => "ogg",
            AudioTrimFormat.Opus => "opus",
            _ => "mp3",
        };

        public static string DialogFilter(AudioTrimFormat f) => f switch
        {
            AudioTrimFormat.Wav => "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            AudioTrimFormat.Flac => "FLAC audio (*.flac)|*.flac|All files (*.*)|*.*",
            AudioTrimFormat.M4a => "M4A audio (*.m4a)|*.m4a|All files (*.*)|*.*",
            AudioTrimFormat.Ogg => "Ogg Vorbis audio (*.ogg)|*.ogg|All files (*.*)|*.*",
            AudioTrimFormat.Opus => "Opus audio (*.opus)|*.opus|All files (*.*)|*.*",
            _ => "MP3 audio (*.mp3)|*.mp3|All files (*.*)|*.*",
        };

        public static bool IsLossless(AudioTrimFormat f) => f is AudioTrimFormat.Wav or AudioTrimFormat.Flac;

        /// <summary>The bitrates offered for a format (empty for lossless ones).</summary>
        public static int[] BitratesFor(AudioTrimFormat f) => f switch
        {
            AudioTrimFormat.Mp3 => new[] { 96, 128, 192, 256, 320 },
            AudioTrimFormat.M4a => new[] { 96, 128, 192, 256 },
            AudioTrimFormat.Ogg => new[] { 96, 128, 192, 256 },
            AudioTrimFormat.Opus => new[] { 64, 96, 128, 192 },
            _ => Array.Empty<int>(),
        };

        public static int DefaultBitrate(AudioTrimFormat f) => f == AudioTrimFormat.Opus ? 128 : 192;

        /// <summary>The bitrate to use: the wanted one when this format offers it, else the nearest offered.</summary>
        public static int ClampBitrate(AudioTrimFormat f, int wanted)
        {
            var all = BitratesFor(f);
            if (all.Length == 0) return 0;
            return all.OrderBy(b => Math.Abs(b - wanted)).First();
        }

        /// <summary>
        /// The sample rate the result gets. Auto = the highest of the files; MP3, Ogg and Opus never go
        /// above 48 kHz (and Opus is always 48 kHz, the only rate its encoder keeps).
        /// </summary>
        public static int ResolveSampleRate(IReadOnlyList<AudioTrimClipState> clips, AudioTrimExportOptions o)
        {
            if (o.Format == AudioTrimFormat.Opus) return 48_000;
            int rate = o.SampleRate > 0 ? o.SampleRate : clips.Select(c => c.Source.SampleRate).DefaultIfEmpty(44_100).Max();
            if (rate < 8_000) rate = 44_100;
            if (o.Format is AudioTrimFormat.Mp3 or AudioTrimFormat.Ogg) rate = Math.Min(rate, 48_000);
            return Math.Min(rate, 192_000);
        }

        /// <summary>Auto = stereo when any file is stereo (or more), else mono.</summary>
        public static int ResolveChannels(IReadOnlyList<AudioTrimClipState> clips, AudioTrimExportOptions o) => o.Channels switch
        {
            AudioTrimChannels.Mono => 1,
            AudioTrimChannels.Stereo => 2,
            _ => clips.Any(c => c.Source.Channels >= 2) ? 2 : 1,
        };

        /// <summary>
        /// The fades, never longer than the audio can hold (together at most the whole length), as
        /// (in, out) in seconds.
        /// </summary>
        public static (double In, double Out) EffectiveFades(double total, double fadeIn, double fadeOut)
        {
            fadeIn = Math.Clamp(fadeIn, 0, MaxFadeSeconds);
            fadeOut = Math.Clamp(fadeOut, 0, MaxFadeSeconds);
            double sum = fadeIn + fadeOut;
            if (sum > total && sum > 0)
            {
                double k = total / sum;
                fadeIn *= k;
                fadeOut *= k;
            }
            return (fadeIn, fadeOut);
        }

        private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>
        /// The ffmpeg -filter_complex text: each clip converted to one common format, tiny fades where
        /// pieces meet, joined, then the whole-result fades and (when asked) the gain / loudness step.
        /// The last label is [out]. <paramref name="analysisOnly"/> ends in volumedetect (a measuring pass).
        /// </summary>
        public static string BuildFilterGraph(IReadOnlyList<AudioTrimClipState> clips, AudioTrimExportOptions o,
                                              double? gainDb, bool analysisOnly)
        {
            int rate = ResolveSampleRate(clips, o), channels = ResolveChannels(clips, o);
            string layout = channels == 1 ? "mono" : "stereo";
            var sb = new StringBuilder();

            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                sb.Append($"[{i}:a:0]aformat=sample_fmts=fltp:sample_rates={rate}:channel_layouts={layout}");
                if (clips.Count > 1)
                {
                    double d = c.Duration;
                    if (i > 0) sb.Append($",afade=t=in:st=0:d={Num(JoinFadeSeconds)}");
                    if (i < clips.Count - 1) sb.Append($",afade=t=out:st={Num(Math.Max(0, d - JoinFadeSeconds))}:d={Num(JoinFadeSeconds)}");
                }
                sb.Append($"[c{i}];");
            }

            string current;
            if (clips.Count > 1)
            {
                for (int i = 0; i < clips.Count; i++) sb.Append($"[c{i}]");
                sb.Append($"concat=n={clips.Count}:v=0:a=1[cat];");
                current = "[cat]";
            }
            else
            {
                current = "[c0]";
            }

            double total = clips.Sum(c => c.Duration);
            var (fin, fout) = EffectiveFades(total, o.FadeInSeconds, o.FadeOutSeconds);
            var post = new List<string>();
            if (fin >= 0.001) post.Add($"afade=t=in:st=0:d={Num(fin)}");
            if (fout >= 0.001) post.Add($"afade=t=out:st={Num(Math.Max(0, total - fout))}:d={Num(fout)}");
            if (!analysisOnly)
            {
                if (o.Normalize == AudioTrimNormalize.Loudness)
                {
                    // loudnorm works at 192 kHz inside; bring the rate back afterwards.
                    post.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
                    post.Add($"aformat=sample_fmts=fltp:sample_rates={rate}:channel_layouts={layout}");
                }
                else if (gainDb is double g && Math.Abs(g) >= 0.01)
                {
                    post.Add($"volume={g.ToString("0.###", CultureInfo.InvariantCulture)}dB");
                }
            }
            if (analysisOnly) post.Add("volumedetect");

            sb.Append(current);
            sb.Append(post.Count == 0 ? "anull" : string.Join(",", post));
            sb.Append("[out]");
            return sb.ToString();
        }

        /// <summary>The ffmpeg arguments for one run (the "-hide_banner" and process setup are added by the caller).</summary>
        public static List<string> BuildArguments(IReadOnlyList<AudioTrimClipState> clips, AudioTrimExportOptions o,
                                                  string outputPath, double? gainDb, bool analysisOnly)
        {
            var args = new List<string> { "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
            foreach (var c in clips)
            {
                args.Add("-ss"); args.Add(Num(c.Start));
                args.Add("-t"); args.Add(Num(c.Duration));
                args.Add("-i"); args.Add(c.Source.Path);
            }
            args.Add("-filter_complex"); args.Add(BuildFilterGraph(clips, o, gainDb, analysisOnly));
            args.Add("-map"); args.Add("[out]");

            if (analysisOnly)
            {
                args.Add("-f"); args.Add("null"); args.Add("-");
                return args;
            }

            int bitrate = ClampBitrate(o.Format, o.BitrateKbps);
            switch (o.Format)
            {
                case AudioTrimFormat.Wav:
                    args.AddRange(new[] { "-c:a", "pcm_s16le", "-f", "wav" });
                    break;
                case AudioTrimFormat.Flac:
                    args.AddRange(new[] { "-c:a", "flac", "-f", "flac" });
                    break;
                case AudioTrimFormat.M4a:
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", bitrate + "k", "-f", "ipod" });
                    break;
                case AudioTrimFormat.Ogg:
                    args.AddRange(new[] { "-c:a", "libvorbis", "-b:a", bitrate + "k", "-f", "ogg" });
                    break;
                case AudioTrimFormat.Opus:
                    args.AddRange(new[] { "-c:a", "libopus", "-b:a", bitrate + "k", "-f", "ogg" });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", bitrate + "k", "-f", "mp3" });
                    break;
            }
            args.Add("-map_metadata"); args.Add("-1");
            args.Add(outputPath);
            return args;
        }

        /// <summary>The loudest point ffmpeg's volumedetect reported, in dB, or null when it saw nothing (silence).</summary>
        public static double? ParseMaxVolume(string ffmpegLog)
        {
            foreach (string line in ffmpegLog.Split('\n'))
            {
                int at = line.IndexOf("max_volume:", StringComparison.Ordinal);
                if (at < 0) continue;
                string rest = line[(at + "max_volume:".Length)..].Trim();
                int space = rest.IndexOf(' ');
                if (space > 0) rest = rest[..space];
                if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)) return v;
            }
            return null;
        }

        /// <summary>The gain (dB) that puts a measured peak at <see cref="PeakTargetDb"/>; null when there's nothing to do or silence.</summary>
        public static double? GainForPeak(double? measuredMaxDb)
        {
            if (measuredMaxDb is not double max) return null;
            return Math.Clamp(PeakTargetDb - max, -60, 40);
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\audio_trimmer.json - the setup only, never files or folders)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "audio_trimmer.json");

        public static AudioTrimSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new AudioTrimSettings();
                var s = JsonSerializer.Deserialize<AudioTrimSettings>(File.ReadAllText(file), SettingsJson) ?? new AudioTrimSettings();
                if (!Enum.IsDefined(s.Format)) s.Format = AudioTrimFormat.Mp3;
                if (!Enum.IsDefined(s.Normalize)) s.Normalize = AudioTrimNormalize.Off;
                if (!Enum.IsDefined(s.Channels)) s.Channels = AudioTrimChannels.Auto;
                s.BitrateKbps = IsLossless(s.Format) ? DefaultBitrate(s.Format) : ClampBitrate(s.Format, s.BitrateKbps);
                s.FadeInSeconds = double.IsFinite(s.FadeInSeconds) ? Math.Clamp(s.FadeInSeconds, 0, MaxFadeSeconds) : 0;
                s.FadeOutSeconds = double.IsFinite(s.FadeOutSeconds) ? Math.Clamp(s.FadeOutSeconds, 0, MaxFadeSeconds) : 0;
                if (s.SampleRate is not (0 or 44_100 or 48_000)) s.SampleRate = 0;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new AudioTrimSettings();
            }
        }

        public static void SaveSettings(AudioTrimSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
