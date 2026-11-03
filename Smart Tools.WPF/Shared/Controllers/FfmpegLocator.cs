using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Where the bundled FFmpeg tools live. They ship under Engines\FFMPEG next to the .exe (see the
    /// Content items in the .csproj), so the path is based on the app's folder, not the project's.
    /// </summary>
    public static class FfmpegLocator
    {
        public static string Folder { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engines", "FFMPEG");

        public static string FfmpegExe { get; } = Path.Combine(Folder, "ffmpeg.exe");

        public static string FfprobeExe { get; } = Path.Combine(Folder, "ffprobe.exe");

        public static bool IsAvailable => File.Exists(FfmpegExe);

        /// <summary>
        /// Finds a tool: the test override if one is set (a missing path means "not installed"), else the
        /// bundled Engines\FFMPEG copy, else one on PATH, else null. <paramref name="name"/> is "ffmpeg" or "ffprobe".
        /// </summary>
        public static string? Resolve(string name, string? overridePath = null)
        {
            if (!string.IsNullOrEmpty(overridePath)) return File.Exists(overridePath) ? overridePath : null;

            string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
            string bundled = Path.Combine(Folder, exe);
            if (File.Exists(bundled)) return bundled;

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // bad PATH entry
                }
            }
            return null;
        }
    }
}
