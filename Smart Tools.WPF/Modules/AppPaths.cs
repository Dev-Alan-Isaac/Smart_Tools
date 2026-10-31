using System.IO;

// Root namespace on purpose (like Log): every sub-namespace sees it without an extra using.
namespace Smart_Tools.WPF
{
    /// <summary>
    /// Where the app keeps its settings, logs, and caches. Installed copies use
    /// %AppData%\Smart Tools; a portable copy (a portable.txt file next to the .exe)
    /// keeps everything in a UserData folder beside it, so it can run from a USB drive.
    /// Never call GetFolderPath(ApplicationData) directly - use <see cref="DataRoot"/>.
    /// </summary>
    public static class AppPaths
    {
        public const string PortableMarker = "portable.txt";

        public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

        /// <summary>The folder for settings, logs, and caches.</summary>
        public static string DataRoot { get; } = IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "UserData")   // not "Data": that folder ships oui.txt
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Smart Tools");

        /// <summary>Tesseract's language files (the downloaded ones live here; Engines\ stays read-only).</summary>
        public static string TesseractData { get; } = Path.Combine(DataRoot, "Tesseract", "tessdata");

        /// <summary>Whisper speech models for Speech to Text (downloaded on demand; Engines\ stays read-only).</summary>
        public static string WhisperModels { get; } = Path.Combine(DataRoot, "Whisper", "models");

        /// <summary>Opus-MT translation models for Translate (downloaded on demand, one folder per language pair).</summary>
        public static string TranslateModels { get; } = Path.Combine(DataRoot, "Translate", "models");

        /// <summary>WebView2's cache and cookies: per-user when installed, beside the app when portable.</summary>
        public static string WebViewData { get; } = IsPortable
            ? Path.Combine(DataRoot, "WebView2")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Smart Tools", "WebView2");
    }
}
