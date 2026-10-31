using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Speech to Text backend, split in two:
    /// <list type="bullet">
    /// <item>SpeechToTextManager.cs (this file) - pure logic: the model and language lists, whisper's arguments and output,
    /// subtitle formats, settings. No processes, no WPF.</item>
    /// <item>SpeechToTextManager.Run.cs - the engines (FFmpeg to make 16 kHz audio, the bundled whisper.cpp to read it).</item>
    /// </list>
    /// </summary>
    public static partial class SpeechToTextManager
    {
        // =================================================================
        //  Models and languages
        // =================================================================

        /// <summary>The models offered. Sizes are approximate (the download shows the real one).</summary>
        public static readonly IReadOnlyList<SttModelInfo> Models = new SttModelInfo[]
        {
            new("tiny", "Tiny", 75, false, "Fastest, least accurate"),
            new("base", "Base", 142, false, "Good for clear speech"),
            new("small", "Small", 466, false, "Best balance"),
            new("medium", "Medium", 1500, false, "Most accurate, slow"),
            new("tiny.en", "Tiny (English only)", 75, true, "Fastest, English only"),
            new("base.en", "Base (English only)", 142, true, "Good for clear English"),
            new("small.en", "Small (English only)", 466, true, "Best balance, English only"),
        };

        public static SttModelInfo? FindModel(string id) =>
            Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        private static readonly SttLanguage[] AllLanguages =
        {
            new("af", "Afrikaans"), new("sq", "Albanian"), new("am", "Amharic"), new("ar", "Arabic"), new("hy", "Armenian"),
            new("as", "Assamese"), new("az", "Azerbaijani"), new("ba", "Bashkir"), new("eu", "Basque"), new("be", "Belarusian"),
            new("bn", "Bengali"), new("bs", "Bosnian"), new("br", "Breton"), new("bg", "Bulgarian"), new("my", "Burmese"),
            new("ca", "Catalan"), new("zh", "Chinese"), new("hr", "Croatian"), new("cs", "Czech"), new("da", "Danish"),
            new("nl", "Dutch"), new("et", "Estonian"), new("fo", "Faroese"), new("fi", "Finnish"), new("fr", "French"),
            new("gl", "Galician"), new("ka", "Georgian"), new("de", "German"), new("el", "Greek"), new("gu", "Gujarati"),
            new("ht", "Haitian Creole"), new("ha", "Hausa"), new("haw", "Hawaiian"), new("he", "Hebrew"), new("hi", "Hindi"),
            new("hu", "Hungarian"), new("is", "Icelandic"), new("id", "Indonesian"), new("it", "Italian"), new("ja", "Japanese"),
            new("jw", "Javanese"), new("kn", "Kannada"), new("kk", "Kazakh"), new("km", "Khmer"), new("ko", "Korean"),
            new("lo", "Lao"), new("la", "Latin"), new("lv", "Latvian"), new("ln", "Lingala"), new("lt", "Lithuanian"),
            new("lb", "Luxembourgish"), new("mk", "Macedonian"), new("mg", "Malagasy"), new("ms", "Malay"), new("ml", "Malayalam"),
            new("mt", "Maltese"), new("mi", "Maori"), new("mr", "Marathi"), new("mn", "Mongolian"), new("ne", "Nepali"),
            new("no", "Norwegian"), new("nn", "Norwegian Nynorsk"), new("oc", "Occitan"), new("ps", "Pashto"), new("fa", "Persian"),
            new("pl", "Polish"), new("pt", "Portuguese"), new("pa", "Punjabi"), new("ro", "Romanian"), new("ru", "Russian"),
            new("sa", "Sanskrit"), new("sr", "Serbian"), new("sn", "Shona"), new("sd", "Sindhi"), new("si", "Sinhala"),
            new("sk", "Slovak"), new("sl", "Slovenian"), new("so", "Somali"), new("su", "Sundanese"), new("sw", "Swahili"),
            new("sv", "Swedish"), new("tl", "Tagalog"), new("tg", "Tajik"), new("ta", "Tamil"), new("tt", "Tatar"),
            new("te", "Telugu"), new("th", "Thai"), new("bo", "Tibetan"), new("tr", "Turkish"), new("tk", "Turkmen"),
            new("uk", "Ukrainian"), new("ur", "Urdu"), new("uz", "Uzbek"), new("vi", "Vietnamese"), new("cy", "Welsh"),
            new("yi", "Yiddish"), new("yo", "Yoruba"),
        };

        /// <summary>Auto-detect first, then English and Spanish, then every language Whisper knows (about 100) in alphabetical order.</summary>
        public static readonly IReadOnlyList<SttLanguage> Languages = new SttLanguage[] { new("", "Auto-detect"), new("en", "English"), new("es", "Spanish") }
            .Concat(AllLanguages.Where(l => l.Code != "es")).ToArray();

        /// <summary>The same-size model that also understands other languages ("small.en" gives "small"), or null when it already does.</summary>
        public static string? MultilingualEquivalent(string modelId) =>
            modelId.EndsWith(".en", StringComparison.OrdinalIgnoreCase) ? modelId[..^3] : null;

        /// <summary>
        /// English-only models can't transcribe another language or translate. Returns the model to use for these settings: the chosen
        /// one when it is fine, otherwise the installed multilingual model of the same size, then any installed multilingual model,
        /// and the chosen one again when there is none (the caller then explains what to install).
        /// </summary>
        public static string ModelFor(string modelId, string language, bool translate, IEnumerable<string> installed)
        {
            bool needsMultilingual = translate || (language.Length > 0 && !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase));
            if (!needsMultilingual || FindModel(modelId)?.EnglishOnly != true) return modelId;

            string? same = MultilingualEquivalent(modelId);
            if (same != null && installed.Contains(same)) return same;
            return Models.FirstOrDefault(m => !m.EnglishOnly && installed.Contains(m.Id))?.Id ?? modelId;
        }

        /// <summary>Audio and video files the tool accepts (anything FFmpeg can read the sound from).</summary>
        public static readonly string[] MediaExtensions =
        {
            ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff",
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg", ".ts",
        };

        public static bool IsMedia(string path) =>
            MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        // =================================================================
        //  Command lines
        // =================================================================

        /// <summary>FFmpeg arguments that turn any audio or video into the 16 kHz mono WAV whisper reads.</summary>
        public static List<string> BuildWavArguments(string input, string wavPath) => new()
        {
            "-y", "-nostdin", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", wavPath,
        };

        /// <summary>
        /// whisper-cli arguments. The result goes to <paramref name="outputPrefix"/>.json; "-pp" prints progress.
        /// An empty <see cref="SttOptions.Language"/> is detected from the sound.
        /// </summary>
        public static List<string> BuildWhisperArguments(string modelPath, string wavPath, string outputPrefix, SttOptions options, int threads)
        {
            var args = new List<string>
            {
                "-m", modelPath, "-f", wavPath,
                "-oj", "-of", outputPrefix,
                "-pp",
                "-t", Math.Max(1, threads).ToString(CultureInfo.InvariantCulture),
                "-l", string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language,
            };
            if (options.Translate) args.Add("-tr");
            return args;
        }

        private static readonly Regex ProgressPattern = new(@"progress\s*=\s*(\d{1,3})\s*%", RegexOptions.Compiled);

        /// <summary>The percent in a line like "whisper_print_progress_callback: progress = 25%", or null.</summary>
        public static double? ParseProgress(string line)
        {
            var m = ProgressPattern.Match(line);
            return m.Success && int.TryParse(m.Groups[1].Value, out int p) ? Math.Clamp(p, 0, 100) : null;
        }

        /// <summary>Threads for one transcription: most of the CPU, but leave one core for the app.</summary>
        public static int ThreadCount() => Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

        // =================================================================
        //  Reading whisper's output
        // =================================================================

        /// <summary>
        /// Reads whisper's "-oj" JSON: the language it used and the timed pieces. Null when it isn't that JSON.
        /// Times come from "offsets" (milliseconds).
        /// </summary>
        public static (string Language, List<SttSegment> Segments)? ParseWhisperJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("transcription", out var transcription) || transcription.ValueKind != JsonValueKind.Array) return null;

                string language = root.TryGetProperty("result", out var result) && result.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                    ? lang.GetString() ?? ""
                    : "";

                var segments = new List<SttSegment>();
                foreach (var piece in transcription.EnumerateArray())
                {
                    string text = piece.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim() : "";
                    if (text.Length == 0) continue;
                    double start = 0, end = 0;
                    if (piece.TryGetProperty("offsets", out var offsets))
                    {
                        if (offsets.TryGetProperty("from", out var f) && f.TryGetDouble(out double fromMs)) start = fromMs / 1000.0;
                        if (offsets.TryGetProperty("to", out var to) && to.TryGetDouble(out double toMs)) end = toMs / 1000.0;
                    }
                    segments.Add(new SttSegment(start, Math.Max(start, end), text));
                }
                return (language, segments);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // =================================================================
        //  Formats
        // =================================================================

        /// <summary>"00:01:02,500" (SRT) or "00:01:02.500" (WebVTT). Hours can pass 99.</summary>
        public static string FormatTimestamp(double seconds, bool comma)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            long totalMs = (long)Math.Round(seconds * 1000.0);
            long ms = totalMs % 1000, s = totalMs / 1000 % 60, m = totalMs / 60000 % 60, h = totalMs / 3600000;
            return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{(comma ? ',' : '.')}{ms:000}");
        }

        public static string FormatSrt(IReadOnlyList<SttSegment> segments)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                sb.Append(CultureInfo.InvariantCulture, $"{i + 1}\r\n{FormatTimestamp(s.Start, true)} --> {FormatTimestamp(s.End, true)}\r\n{s.Text}\r\n\r\n");
            }
            return sb.ToString();
        }

        public static string FormatVtt(IReadOnlyList<SttSegment> segments)
        {
            var sb = new StringBuilder("WEBVTT\r\n\r\n");
            foreach (var s in segments)
                sb.Append($"{FormatTimestamp(s.Start, false)} --> {FormatTimestamp(s.End, false)}\r\n{s.Text}\r\n\r\n");
            return sb.ToString();
        }

        /// <summary>The plain transcript: one piece per line, optionally with a [mm:ss] before each.</summary>
        public static string FormatTxt(IReadOnlyList<SttSegment> segments, bool timestamps)
        {
            var sb = new StringBuilder();
            foreach (var s in segments)
            {
                if (timestamps) sb.Append('[').Append(SttText.FormatTime(s.Start)).Append("] ");
                sb.Append(s.Text).Append("\r\n");
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        public static int WordCount(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        // =================================================================
        //  Settings (%AppData%\Smart Tools\speech_to_text.json)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "speech_to_text.json");

        public static SttSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new SttSettings();
                return Sanitize(JsonSerializer.Deserialize<SttSettings>(File.ReadAllText(file), SettingsJson) ?? new SttSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new SttSettings();
            }
        }

        public static SttSettings Sanitize(SttSettings s)
        {
            if (FindModel(s.ModelId) == null) s.ModelId = "base";
            s.Language ??= "";
            if (s.Language.Length > 0 && !Languages.Any(l => l.Code == s.Language)) s.Language = "";
            if (!s.WriteTxt && !s.WriteSrt && !s.WriteVtt) s.WriteTxt = true;
            return s;
        }

        /// <summary>Never throws - a settings file that can't be written isn't worth an error.</summary>
        public static void SaveSettings(SttSettings s, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(Sanitize(s), SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
