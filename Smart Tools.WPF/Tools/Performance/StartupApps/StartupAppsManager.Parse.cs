using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Pure helpers for startup entries: the on/off flag format Windows uses and command-line splitting. No registry, no files, no WPF.</summary>
    public sealed partial class StartupAppsManager
    {
        /// <summary>
        /// Task Manager keeps an entry's on/off flag as 12 bytes under ...\Explorer\StartupApproved: an even first byte
        /// (2 or 6) = on, an odd one (3) = off, followed by the time it was turned off. No value at all = on.
        /// </summary>
        public static bool IsApprovedEnabled(byte[]? flag) => flag == null || flag.Length == 0 || (flag[0] & 1) == 0;

        public static byte[] ApprovedFlag(bool enabled, DateTime nowUtc)
        {
            var flag = new byte[12];
            if (enabled)
            {
                flag[0] = 2;
                return flag;
            }
            flag[0] = 3;
            BitConverter.GetBytes(nowUtc.ToFileTimeUtc()).CopyTo(flag, 4);
            return flag;
        }

        /// <summary>
        /// The paths a command line could start, most likely first: the quoted part, or progressively longer pieces of an
        /// unquoted line ("C:\Program Files\App\app.exe /silent" gives C:\Program, C:\Program Files\App\app.exe, ...).
        /// </summary>
        public static IEnumerable<string> CommandCandidates(string command)
        {
            string text = Environment.ExpandEnvironmentVariables(command.Trim());
            if (text.Length == 0) yield break;

            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                yield return end > 1 ? text[1..end] : text.Trim('"');
                yield break;
            }

            var words = text.Split(' ');
            string path = "";
            for (int i = 0; i < words.Length; i++)
            {
                path = i == 0 ? words[0] : path + " " + words[i];
                yield return path;
                if (!Path.HasExtension(path)) yield return path + ".exe";
            }
        }
    }
}
