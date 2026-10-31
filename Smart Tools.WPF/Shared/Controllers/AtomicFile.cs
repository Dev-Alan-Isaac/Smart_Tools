using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Writes a file through a temp file in the same folder and then moves it over the target, so a crash,
    /// power cut, or full disk mid-write leaves the old file intact instead of a half-written one.
    /// Use it for settings and any data the user would miss; plain exports can keep File.WriteAllText.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string contents, Encoding? encoding = null) =>
            Write(path, temp => File.WriteAllText(temp, contents, encoding ?? new UTF8Encoding(false)));

        public static void WriteAllBytes(string path, byte[] contents) =>
            Write(path, temp => File.WriteAllBytes(temp, contents));

        private static void Write(string path, Action<string> writeTemp)
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // Unique per call, so two saves of the same file can't trample each other's temp file.
            string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                writeTemp(temp);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: the original error is the one to report */ }
                throw;
            }
        }
    }
}
