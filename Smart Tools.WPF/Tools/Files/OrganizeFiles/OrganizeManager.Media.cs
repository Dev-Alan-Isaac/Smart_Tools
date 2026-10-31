using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>ffprobe for the video and audio rules.</summary>
    public sealed partial class OrganizeManager
    {
        /// <summary>Set by tests to use a specific ffprobe.</summary>
        public static string? FfprobePathOverride { get; set; }

        public static string FfmpegFolder => Smart_Tools.WPF.Controllers.FfmpegLocator.Folder;

        /// <summary>The bundled Engines\FFMPEG copy, else one on PATH, else null.</summary>
        public static string? FfprobeExe
        {
            get
            {
                if (!string.IsNullOrEmpty(FfprobePathOverride)) return File.Exists(FfprobePathOverride) ? FfprobePathOverride : null;
                string exe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
                string bundled = Path.Combine(FfmpegFolder, exe);
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

        private const int ProbeTimeoutMs = 15_000;

        /// <summary>One ffprobe call per file; a hung ffprobe is stopped after 15 s and the file counts as unknown.</summary>
        public static OrganizeMediaFacts? ProbeWithFfprobe(string path)
        {
            string? exe = FfprobeExe;
            if (exe == null) return null;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(ProbeTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // already gone
                }
                return null;
            }
            process.WaitForExit();

            string json;
            lock (stdout) json = stdout.ToString();
            return string.IsNullOrWhiteSpace(json) ? null : ParseFfprobeJson(json);
        }
    }
}
