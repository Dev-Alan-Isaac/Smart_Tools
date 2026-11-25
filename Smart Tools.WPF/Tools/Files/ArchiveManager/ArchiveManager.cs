using System.Diagnostics;
using System.IO;
using System.Text;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    public enum ArchiveListState { Ok, NeedsPassword, MissingParts, Failed, NoEngine }

    public sealed record ArchiveListResult(ArchiveListState State, ArchiveListing? Listing, SplitSet Split, string Error);

    public sealed record ArchiveRunResult(bool Ok, bool Cancelled, bool NeedsPassword, string Error, string Warning);

    public sealed record ArchiveCreateResult(bool Ok, bool Cancelled, string Error, IReadOnlyList<string> Files);

    public sealed class ArchiveSettings
    {
        public bool Creating { get; set; }
        public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;
        public ArchiveLevel Level { get; set; } = ArchiveLevel.Normal;
        public bool Split { get; set; }
        public string SplitSize { get; set; } = "700 MB";
        public bool HideNames { get; set; } = true;
        public bool OpenFolderWhenDone { get; set; } = true;
    }

    /// <summary>
    /// Archives through the bundled 7-Zip (Engines\7ZIP). No WPF types. Rules: nothing is ever replaced (extracted files and new archives
    /// get a free name), a run that fails or is stopped removes only what that run created, and 7-Zip can never wait at the keyboard
    /// for a password (its input is closed; no password means it fails at once and the tool asks).
    /// </summary>
    public static class ArchiveManager
    {
        private const string SettingsName = "archive_manager";
        private const int MaxCaptured = 64 * 1024 * 1024;

        public static ArchiveSettings LoadSettings(string? path = null) => JsonSettings.Load<ArchiveSettings>(SettingsName, path);

        public static void SaveSettings(ArchiveSettings settings, string? path = null) => JsonSettings.Save(SettingsName, settings, path);

        public static string ExePath { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engines", "7ZIP", "7z.exe");

        public static bool IsAvailable => File.Exists(ExePath);

        // =================================================================
        //  Running 7-Zip
        // =================================================================

        /// <summary>Runs 7z.exe with these arguments. <paramref name="onText"/> gets the text as it arrives (progress uses carriage returns).</summary>
        public static async Task<(int ExitCode, string Output, bool Cancelled)> RunAsync(IReadOnlyList<string> args, Action<string>? onText, CancellationToken token)
        {
            var info = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) info.ArgumentList.Add(a);

            using var process = new Process { StartInfo = info };
            process.Start();
            process.StandardInput.Close();   // never wait for a password typed at the keyboard
            var output = new StringBuilder();
            object gate = new();

            async Task Pump(StreamReader reader)
            {
                var buffer = new char[4096];
                int n;
                while ((n = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    string chunk = new(buffer, 0, n);
                    lock (gate)
                        if (output.Length < MaxCaptured) output.Append(chunk);
                    onText?.Invoke(chunk);
                }
            }

            using var stop = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // it ended on its own just now
                }
            });
            await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError)).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return (process.ExitCode, output.ToString(), token.IsCancellationRequested);
        }

        // =================================================================
        //  Reading an archive
        // =================================================================

        /// <summary>The parts of <paramref name="picked"/> as they are in its folder (just the file for an ordinary archive).</summary>
        public static SplitSet FindParts(string picked)
        {
            string folder = Path.GetDirectoryName(picked) ?? "";
            IEnumerable<string> names;
            try
            {
                names = Directory.Exists(folder) ? SafeIo.EnumerateFiles(folder, false).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList() : new List<string>();
            }
            catch (Exception ex) when (SafeIo.IsSkippable(ex))
            {
                names = new List<string>();
            }
            var set = ArchiveSplit.Find(Path.GetFileName(picked), names);
            return set with { Start = Path.Combine(folder, set.Start) };
        }

        public static async Task<ArchiveListResult> ListAsync(string picked, string? password, CancellationToken token)
        {
            var split = FindParts(picked);
            if (!IsAvailable) return new ArchiveListResult(ArchiveListState.NoEngine, null, split, "");
            if (split.IsSplit && !split.IsComplete) return new ArchiveListResult(ArchiveListState.MissingParts, null, split, string.Join(", ", split.Missing));
            if (!File.Exists(split.Start)) return new ArchiveListResult(ArchiveListState.Failed, null, split, Path.GetFileName(split.Start));

            var run = await RunAsync(SevenZip.List(split.Start, password), null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var listing = SevenZip.ParseListing(run.Output);
            if (listing != null && !(listing.Entries.Count == 0 && SevenZip.NeedsPassword(run.Output)))
            {
                return new ArchiveListResult(ArchiveListState.Ok, listing, split, "");
            }
            if (SevenZip.NeedsPassword(run.Output)) return new ArchiveListResult(ArchiveListState.NeedsPassword, null, split, "");
            string error = SevenZip.FirstError(run.Output);
            return new ArchiveListResult(ArchiveListState.Failed, null, split, error.Length > 0 ? error : "7-Zip could not read this file as an archive.");
        }

        public static async Task<ArchiveRunResult> TestAsync(string start, string? password, IProgress<int>? progress, CancellationToken token)
        {
            var run = await RunAsync(SevenZip.Test(start, password), text => Report(progress, text), token).ConfigureAwait(false);
            return Describe(run.ExitCode, run.Output, run.Cancelled);
        }

        // =================================================================
        //  Extracting
        // =================================================================

        /// <summary>
        /// Extracts into a NEW folder inside <paramref name="destination"/>, named like the archive (a taken name gets " (2)"), so nothing
        /// spills among other files and nothing is replaced. Returns that folder. A stopped or failed run deletes the folder it made.
        /// </summary>
        public static async Task<(ArchiveRunResult Result, string Folder)> ExtractAsync(string start, string destination, string? password,
            IReadOnlyList<string>? only, IProgress<int>? progress, CancellationToken token)
        {
            Directory.CreateDirectory(destination);
            string folder = UniqueFolder(Path.Combine(destination, ArchiveSplit.Stem(Path.GetFileName(start))));
            Directory.CreateDirectory(folder);
            try
            {
                var run = await RunAsync(SevenZip.Extract(start, folder, password, only), text => Report(progress, text), token).ConfigureAwait(false);
                var result = Describe(run.ExitCode, run.Output, run.Cancelled);
                if (!result.Ok) RemoveQuietly(folder);
                return (result, folder);
            }
            catch
            {
                RemoveQuietly(folder);
                throw;
            }
        }

        // =================================================================
        //  Creating
        // =================================================================

        /// <summary>
        /// Packs <paramref name="inputs"/> into <paramref name="folder"/>. The name is free of any existing file (and of an existing
        /// ".001"), a split makes name.7z.001, .002 ...; a failed or stopped run removes the files it had started.
        /// </summary>
        public static async Task<ArchiveCreateResult> CreateAsync(IReadOnlyList<string> inputs, string folder, string baseName, ArchiveSettings s,
            string? password, IProgress<int>? progress, CancellationToken token)
        {
            long splitBytes = 0;
            if (s.Split && !SevenZip.TryParseSize(s.SplitSize, out splitBytes)) return new ArchiveCreateResult(false, false, "The split size is not a size (try 700 MB or 4 GB).", Array.Empty<string>());
            Directory.CreateDirectory(folder);
            string target = FreeArchivePath(folder, baseName, SevenZip.Extension(s.Format));
            try
            {
                var run = await RunAsync(SevenZip.Create(target, inputs, s.Format, s.Level, splitBytes, password, s.HideNames), text => Report(progress, text), token).ConfigureAwait(false);
                var result = Describe(run.ExitCode, run.Output, run.Cancelled);
                var made = Produced(target);
                if (!result.Ok)
                {
                    foreach (string f in made) SafeFileOps.TryDelete(f);
                    return new ArchiveCreateResult(false, result.Cancelled, result.Error, Array.Empty<string>());
                }
                return new ArchiveCreateResult(true, false, result.Warning, made);
            }
            catch
            {
                foreach (string f in Produced(target)) SafeFileOps.TryDelete(f);
                throw;
            }
        }

        /// <summary>"name.7z", or "name (2).7z" when that - or its first split part - is taken. Never an existing file.</summary>
        public static string FreeArchivePath(string folder, string baseName, string extension)
        {
            baseName = OutputFolderManager.SafeName(baseName);
            for (int n = 1; ; n++)
            {
                string name = n == 1 ? baseName : $"{baseName} ({n})";
                string path = Path.Combine(folder, name + extension);
                if (!File.Exists(path) && !Directory.Exists(path) && !File.Exists(path + ".001")) return path;
            }
        }

        /// <summary>The file 7-Zip made for this target: itself, or its numbered parts.</summary>
        public static IReadOnlyList<string> Produced(string target)
        {
            var files = new List<string>();
            if (File.Exists(target)) files.Add(target);
            for (int n = 1; n < 10000 && File.Exists($"{target}.{n:000}"); n++) files.Add($"{target}.{n:000}");
            return files;
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private static string UniqueFolder(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int n = 2; ; n++)
            {
                string candidate = $"{path} ({n})";
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
        }

        private static void RemoveQuietly(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // something holds a file: it stays and the user can delete the folder
            }
        }

        private static void Report(IProgress<int>? progress, string text)
        {
            if (progress != null && SevenZip.ParsePercent(text) is { } p) progress.Report(p);
        }

        /// <summary>7-Zip's exit codes: 0 fine, 1 finished with warnings (a locked file was skipped...), 2 and up failed, 255 stopped.</summary>
        private static ArchiveRunResult Describe(int exitCode, string output, bool cancelled)
        {
            if (cancelled || exitCode == 255) return new ArchiveRunResult(false, true, false, "", "");
            if (exitCode == 0) return new ArchiveRunResult(true, false, false, "", "");
            string error = SevenZip.FirstError(output);
            if (exitCode == 1) return new ArchiveRunResult(true, false, false, "", error);
            return new ArchiveRunResult(false, false, SevenZip.NeedsPassword(output), error.Length > 0 ? error : $"7-Zip stopped with code {exitCode}.", "");
        }
    }
}
