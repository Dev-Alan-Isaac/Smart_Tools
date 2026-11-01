using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// How long to let ffprobe run on a file: a flat 20 s plus 1 s per 100 MB,
    /// so multi-GB files and slow or network drives aren't reported as
    /// "damaged" just because the probe needed longer than a small clip.
    /// </summary>
    public static class ProbeTimeout
    {
        public static TimeSpan For(string path)
        {
            long bytes = 0;
            try { bytes = new FileInfo(path).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return TimeSpan.FromSeconds(20 + bytes / (100L * 1024 * 1024));
        }
    }
}
