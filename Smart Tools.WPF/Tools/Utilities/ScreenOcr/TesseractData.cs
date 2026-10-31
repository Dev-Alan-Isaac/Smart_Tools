using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One language Tesseract can download: its file code ("spa"), English name, and own-language name.</summary>
    public sealed record TesseractLanguageInfo(string Code, string Name, string NativeName)
    {
        public string Display => Name == NativeName ? Name : $"{Name} ({NativeName})";
    }

    /// <summary>Pure Tesseract data: the language list and the reader for its TSV output. No I/O, no WPF.</summary>
    public static class TesseractData
    {
        /// <summary>Languages offered for download (tessdata_fast file names). English is bundled.</summary>
        public static readonly IReadOnlyList<TesseractLanguageInfo> Catalog = new TesseractLanguageInfo[]
        {
            new("eng", "English", "English"),
            new("spa", "Spanish", "Español"),
            new("fra", "French", "Français"),
            new("deu", "German", "Deutsch"),
            new("ita", "Italian", "Italiano"),
            new("por", "Portuguese", "Português"),
            new("nld", "Dutch", "Nederlands"),
            new("cat", "Catalan", "Català"),
            new("swe", "Swedish", "Svenska"),
            new("dan", "Danish", "Dansk"),
            new("nor", "Norwegian", "Norsk"),
            new("fin", "Finnish", "Suomi"),
            new("pol", "Polish", "Polski"),
            new("ces", "Czech", "Čeština"),
            new("slk", "Slovak", "Slovenčina"),
            new("hun", "Hungarian", "Magyar"),
            new("ron", "Romanian", "Română"),
            new("bul", "Bulgarian", "Български"),
            new("hrv", "Croatian", "Hrvatski"),
            new("srp", "Serbian", "Српски"),
            new("slv", "Slovenian", "Slovenščina"),
            new("rus", "Russian", "Русский"),
            new("ukr", "Ukrainian", "Українська"),
            new("ell", "Greek", "Ελληνικά"),
            new("tur", "Turkish", "Türkçe"),
            new("heb", "Hebrew", "עברית"),
            new("ara", "Arabic", "العربية"),
            new("fas", "Persian", "فارسی"),
            new("hin", "Hindi", "हिन्दी"),
            new("ben", "Bengali", "বাংলা"),
            new("tam", "Tamil", "தமிழ்"),
            new("tha", "Thai", "ไทย"),
            new("vie", "Vietnamese", "Tiếng Việt"),
            new("ind", "Indonesian", "Bahasa Indonesia"),
            new("msa", "Malay", "Bahasa Melayu"),
            new("chi_sim", "Chinese (Simplified)", "简体中文"),
            new("chi_tra", "Chinese (Traditional)", "繁體中文"),
            new("jpn", "Japanese", "日本語"),
            new("kor", "Korean", "한국어"),
            new("lat", "Latin", "Latina"),
        };

        public static TesseractLanguageInfo? Find(string code) =>
            Catalog.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

        /// <summary>The name to show for a code, even one that isn't in the catalog.</summary>
        public static string NameOf(string code) => Find(code)?.Name ?? code;

        /// <summary>
        /// Reads Tesseract's "tsv" output into lines and words. Only word rows (level 5) with text and a real
        /// confidence count; Tesseract marks boxes that aren't words with -1. Lines group by (block, paragraph, line).
        /// Word boxes are divided by <paramref name="scale"/> so they are in the pixels of the original picture.
        /// </summary>
        public static (List<string> Lines, List<ScreenOcrWord> Words) ParseTsv(string tsv, double scale)
        {
            var lines = new List<string>();
            var words = new List<ScreenOcrWord>();
            if (string.IsNullOrWhiteSpace(tsv)) return (lines, words);
            if (scale <= 0) scale = 1;

            string? currentKey = null;
            var current = new List<string>();
            void Flush()
            {
                if (current.Count > 0) lines.Add(string.Join(' ', current));
                current.Clear();
            }

            foreach (string raw in tsv.Split('\n'))
            {
                string row = raw.TrimEnd('\r');
                string[] c = row.Split('\t');
                if (c.Length < 12 || c[0] != "5") continue;   // also skips the header row ("level")

                string text = c[11].Trim();
                if (text.Length == 0) continue;
                if (!double.TryParse(c[10], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double conf) || conf < 0) continue;
                if (!TryNumber(c[6], out double x) || !TryNumber(c[7], out double y) || !TryNumber(c[8], out double w) || !TryNumber(c[9], out double h)) continue;

                string key = $"{c[2]}.{c[3]}.{c[4]}";
                if (key != currentKey)
                {
                    Flush();
                    currentKey = key;
                }
                current.Add(text);
                words.Add(new ScreenOcrWord(text, x / scale, y / scale, w / scale, h / scale));
            }
            Flush();
            return (lines, words);
        }

        private static bool TryNumber(string s, out double value) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
