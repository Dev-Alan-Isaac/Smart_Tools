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
    }
}
