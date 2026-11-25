using System.IO;
using System.Management;
using System.Net.Http;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    public sealed class GameBottleneckSettings
    {
        /// <summary>Compare another PC (picked from the table) instead of this one.</summary>
        public bool UseOtherPc { get; set; }
        public string OtherCpu { get; set; } = "";
        public string OtherGpu { get; set; } = "";
        public double OtherRamGb { get; set; } = 16;

        /// <summary>Where "Update part list" downloads the list from (an https address of a list file).</summary>
        public string ListUrl { get; set; } = GameBottleneckManager.DefaultListUrl;
    }

    public sealed record ListUpdateResult(bool Ok, string Version, int Cpus, int Gpus, ListUpdateError Error);

    public enum ListUpdateError { None, Address, Network, Invalid, TooBig }

    /// <summary>The PC as Windows reports it.</summary>
    public sealed record DetectedPc(string CpuName, IReadOnlyList<string> GpuNames, double RamGb);

    /// <summary>
    /// Detecting this PC (WMI) and looking a game up on the Steam store. The store is only asked when the user presses Look up, and
    /// only the typed game name (or the chosen game's number) is sent. No WPF types.
    /// </summary>
    public static class GameBottleneckManager
    {
        private const string SettingsName = "game_bottleneck";

        public static GameBottleneckSettings LoadSettings(string? path = null) => JsonSettings.Load<GameBottleneckSettings>(SettingsName, path);

        public static void SaveSettings(GameBottleneckSettings settings, string? path = null) => JsonSettings.Save(SettingsName, settings, path);

        // =================================================================
        //  The part list (new processors and cards, without a new release)
        // =================================================================

        /// <summary>The starting list lives in the project's repository; whoever keeps it adds new parts there. The address can be changed on the page.</summary>
        public const string DefaultListUrl = "https://raw.githubusercontent.com/Dev-Alan-Isaac/Smart_Tools/master/Smart%20Tools.WPF/Tools/Performance/GameBottleneck/Data/hardware-scores.json";

        public static string UserListPath { get; } = Path.Combine(AppPaths.DataRoot, "GameBottleneck", "hardware-scores.json");

        /// <summary>True while a downloaded list is in use on top of the built-in table.</summary>
        public static bool UsingDownloadedList { get; private set; }

        /// <summary>Uses the list downloaded earlier, if there is one and it is valid. Nothing is downloaded here.</summary>
        public static bool LoadUserList()
        {
            try
            {
                if (File.Exists(UserListPath) && HardwareScores.TryParseTableFile(File.ReadAllText(UserListPath), out var file, out _))
                {
                    HardwareScores.Apply(file);
                    UsingDownloadedList = true;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Game bottleneck: could not read the saved part list", ex);
            }
            return false;
        }

        /// <summary>
        /// Downloads the part list from <paramref name="url"/> (only when the user pressed the button; https only; at most 2 MB), checks it
        /// as a whole, and only then keeps it in the data folder and uses it. A bad download changes nothing.
        /// </summary>
        public static async Task<ListUpdateResult> UpdateListAsync(string url, CancellationToken token)
        {
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return new ListUpdateResult(false, "", 0, 0, ListUpdateError.Address);
            try
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new ListUpdateResult(false, "", 0, 0, ListUpdateError.Network);
                if (response.Content.Headers.ContentLength is { } length && length > HardwareScores.MaxFileBytes)
                    return new ListUpdateResult(false, "", 0, 0, ListUpdateError.TooBig);

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > HardwareScores.MaxFileBytes) return new ListUpdateResult(false, "", 0, 0, ListUpdateError.TooBig);
                }
                string text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
                if (!HardwareScores.TryParseTableFile(text, out var file, out _)) return new ListUpdateResult(false, "", 0, 0, ListUpdateError.Invalid);

                Directory.CreateDirectory(Path.GetDirectoryName(UserListPath)!);
                AtomicFile.WriteAllText(UserListPath, text);   // kept only after it was checked
                HardwareScores.Apply(file);
                UsingDownloadedList = true;
                return new ListUpdateResult(true, file.Version, file.Cpus.Count, file.Gpus.Count, ListUpdateError.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                token.ThrowIfCancellationRequested();
                Log.Warn("Game bottleneck: the part list download failed", ex);
                return new ListUpdateResult(false, "", 0, 0, ListUpdateError.Network);
            }
        }

        // =================================================================
        //  This PC
        // =================================================================

        public static DetectedPc DetectPc()
        {
            string cpu = "";
            var gpus = new List<string>();
            double ram = 0;
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject o in searcher.Get())
                {
                    cpu = (o["Name"] as string)?.Trim() ?? "";
                    break;
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                Log.Warn("Game bottleneck: could not read the processor", ex);
            }
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (ManagementObject o in searcher.Get())
                    if (o["Name"] is string name && name.Trim().Length > 0 && !IsVirtualAdapter(name)) gpus.Add(name.Trim());
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                Log.Warn("Game bottleneck: could not read the graphics card", ex);
            }
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject o in searcher.Get())
                {
                    if (o["TotalPhysicalMemory"] is ulong bytes) ram = Math.Round(bytes / 1024.0 / 1024 / 1024);
                    break;
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                Log.Warn("Game bottleneck: could not read the memory", ex);
            }
            return new DetectedPc(cpu, gpus, ram);
        }

        private static bool IsVirtualAdapter(string name) =>
            name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Meta Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("DisplayLink", StringComparison.OrdinalIgnoreCase);

        /// <summary>The card a game would use: the fastest one in the table (a laptop with a built-in and a separate card plays on the separate one).</summary>
        public static string BestGpu(IReadOnlyList<string> names) =>
            names.OrderByDescending(n => HardwareScores.ScoreGpu(n)?.Score ?? -1).FirstOrDefault() ?? "";

        // =================================================================
        //  The Steam store
        // =================================================================

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        static GameBottleneckManager()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartTools");   // a generic name: nothing about the user or this PC
        }

        public static async Task<IReadOnlyList<GameHit>> SearchAsync(string term, CancellationToken token)
        {
            string url = "https://store.steampowered.com/api/storesearch/?l=english&cc=us&term=" + Uri.EscapeDataString(term.Trim());
            string json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
            return GameBottleneckMath.ParseSearch(json).Take(12).ToList();
        }

        /// <summary>The game's requirements, or null when the store lists none for it.</summary>
        public static async Task<GameSpec?> FetchSpecAsync(GameHit game, CancellationToken token)
        {
            string url = $"https://store.steampowered.com/api/appdetails?l=english&filters=pc_requirements,basic&appids={game.AppId}";
            string json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
            var spec = GameBottleneckMath.ParseDetails(json, game.AppId);
            return spec == null ? null : spec with { Name = string.IsNullOrWhiteSpace(spec.Name) ? game.Name : spec.Name };
        }
    }
}
