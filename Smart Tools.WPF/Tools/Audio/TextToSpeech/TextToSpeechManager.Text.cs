using Smart_Tools.WPF.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Text to Speech: the pure parts - the SSML (with the text safely
    /// escaped and a map back to text positions), file names, timing
    /// estimates, reading text files, settings. No System.Speech, no WPF,
    /// so it's unit-tested on its own.
    /// </summary>
    public sealed partial class TextToSpeechManager
    {
        public const int MinRate = -10, MaxRate = 10;
        public const int MinPitch = -10, MaxPitch = 10;
        public const long MaxTextFileBytes = 5 * 1024 * 1024;

        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Smart Tools", "Speech");

        /// <summary>Pitch slider -10..+10 as an SSML relative change, 5% a step ("+25%"); 0 = no change.</summary>
        public static string PitchPercent(int pitch)
        {
            int p = Math.Clamp(pitch, MinPitch, MaxPitch) * 5;
            return p == 0 ? "+0%" : (p > 0 ? "+" : "") + p + "%";
        }

        /// <summary>
        /// The SSML for a request. The text is XML-escaped character by
        /// character (an "&amp;" or "&lt;" in the text otherwise makes the
        /// SSML invalid and the voice refuses it), characters XML can't hold
        /// at all (control codes from pasted or loaded text) are left out, and
        /// line breaks become spaces. Records where each text character lands
        /// so progress can be mapped back.
        /// </summary>
        public static TextToSpeechPrompt BuildPrompt(string text, string? voiceName, string? culture, int pitch, int textOffset = 0)
        {
            string lang = string.IsNullOrWhiteSpace(culture) ? "en-US" : culture.Trim();
            var sb = new StringBuilder();
            sb.Append("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"").Append(EscapeAttribute(lang)).Append("\">");
            if (!string.IsNullOrWhiteSpace(voiceName)) sb.Append("<voice name=\"").Append(EscapeAttribute(voiceName)).Append("\">");
            bool prosody = Math.Clamp(pitch, MinPitch, MaxPitch) != 0;
            if (prosody) sb.Append("<prosody pitch=\"").Append(PitchPercent(pitch)).Append("\">");

            var map = new int[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                map[i] = sb.Length;
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    map[++i] = map[i - 1];
                    continue;
                }
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '\r': case '\n': case '\t': sb.Append(' '); break;
                    default:
                        if (XmlConvert.IsXmlChar(c)) sb.Append(c);   // control codes and lone surrogates are dropped
                        break;
                }
            }

            if (prosody) sb.Append("</prosody>");
            if (!string.IsNullOrWhiteSpace(voiceName)) sb.Append("</voice>");
            sb.Append("</speak>");
            return new TextToSpeechPrompt { Ssml = sb.ToString(), TextToSsml = map, TextOffset = textOffset };
        }

        private static string EscapeAttribute(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (!XmlConvert.IsXmlChar(c)) continue;
                sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&apos;", _ => c.ToString() });
            }
            return sb.ToString();
        }

        /// <summary>
        /// A position the voice reported (in the SSML) back to a position in
        /// the text box, or -1 when it's inside the markup.
        /// </summary>
        public static int TextPosition(TextToSpeechPrompt prompt, int ssmlPosition)
        {
            var map = prompt.TextToSsml;
            if (map.Length == 0 || ssmlPosition < map[0]) return -1;
            int lo = 0, hi = map.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (map[mid] <= ssmlPosition) lo = mid; else hi = mid - 1;
            }
            return prompt.TextOffset + lo;
        }

        /// <summary>
        /// The word at a text position (for highlighting): from that character
        /// to the next space. Returns (start, length); length 0 when nothing.
        /// </summary>
        public static (int Start, int Length) WordAt(string text, int position)
        {
            if (position < 0 || position >= text.Length) return (0, 0);
            int start = position;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            int end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            return (start, end - start);
        }

        // ---------------- Counting ----------------

        public static int WordCount(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>
        /// Rough listening time: about 150 words a minute at normal speed;
        /// each Rate step is roughly 11% faster or slower (SAPI's scale runs
        /// from about a third to three times normal).
        /// </summary>
        public static TimeSpan EstimatedDuration(string text, int rate)
        {
            int words = WordCount(text);
            if (words == 0) return TimeSpan.Zero;
            double speed = Math.Pow(3, Math.Clamp(rate, MinRate, MaxRate) / 10.0);
            return TimeSpan.FromSeconds(Math.Max(1, words / (150.0 * speed) * 60));
        }

        /// <summary>"about 45 s", "about 3 min", "about 1 h 05 min".</summary>
        public static string DescribeDuration(TimeSpan t)
        {
            if (t <= TimeSpan.Zero) return "";
            if (t.TotalSeconds < 60) return $"about {Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";
            if (t.TotalMinutes < 60) return $"about {(int)Math.Round(t.TotalMinutes)} min";
            return $"about {(int)t.TotalHours} h {t.Minutes:00} min";
        }

        // ---------------- Files ----------------

        /// <summary>"Hello and welcome to.wav" from the first few words; "Speech.wav" when there are none.</summary>
        public static string SuggestFileName(string text)
        {
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new string(w.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '\'').ToArray()))
                .Where(w => w.Length > 0)
                .Take(6);
            string name = string.Join(" ", words);
            if (name.Length > 40) name = name[..40].TrimEnd();
            return (name.Length > 0 ? name : "Speech") + ".wav";
        }

        /// <summary>What the user typed (".wav" added, bad characters replaced), or a name from the text.</summary>
        public static string OutputFileName(string? typed, string text)
        {
            string stem = (typed ?? "").Trim();
            if (stem.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            stem = new string(stem.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (stem.Length > 120) stem = stem[..120];
            return stem.Length > 0 ? stem + ".wav" : SuggestFileName(text);
        }

        public static string UniquePath(string path) => Smart_Tools.WPF.Controllers.SafeFileOps.UniquePath(path);

        /// <summary>
        /// A text file for the box: refuses anything over 5 MB (the old load
        /// read any file whole - a big log froze the window) and files that are
        /// clearly not text (NUL bytes). UTF-8/UTF-16 are recognised by their mark.
        /// </summary>
        public static string ReadTextFile(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTextFileBytes)
                throw new InvalidDataException($"That file is {info.Length / 1024 / 1024} MB - text over 5 MB is too long to read aloud here.");
            string text;
            using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();
            if (text.Contains('\0')) throw new InvalidDataException("That doesn't look like a text file.");
            return text;
        }

        // ---------------- Settings ----------------

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "text_to_speech.json");

        public static TextToSpeechSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                var s = File.Exists(file) ? JsonSerializer.Deserialize<TextToSpeechSettings>(File.ReadAllText(file), JsonOptions) : null;
                return Sanitize(s ?? new TextToSpeechSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new TextToSpeechSettings();
            }
        }

        public static TextToSpeechSettings Sanitize(TextToSpeechSettings s)
        {
            s.Rate = Math.Clamp(s.Rate, MinRate, MaxRate);
            s.Pitch = Math.Clamp(s.Pitch, MinPitch, MaxPitch);
            s.Volume = Math.Clamp(s.Volume, 0, 100);
            s.VoiceName ??= "";
            s.FileName = Path.GetFileName((s.FileName ?? "").Replace('\\', '/'));
            return s;
        }

        public static void SaveSettings(TextToSpeechSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(Sanitize(settings), JsonOptions));
        }

        /// <summary>The remembered voice if still installed, else one matching the PC's language, else the first.</summary>
        public static TextToSpeechVoice? PreferredVoice(IReadOnlyList<TextToSpeechVoice> voices, string? savedName, string currentCulture) =>
            voices.FirstOrDefault(v => v.Name == savedName)
            ?? voices.FirstOrDefault(v => v.Culture.Equals(currentCulture, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault();
    }
}
