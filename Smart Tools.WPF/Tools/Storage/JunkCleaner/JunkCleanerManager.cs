using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Recycle Bin, through the shell (its folders can't be read directly). Tests use a fake.</summary>
    public interface IRecycleBin
    {
        /// <summary>Items and bytes in every drive's Recycle Bin, or null when Windows won't say.</summary>
        (long Items, long Bytes)? Query();

        /// <summary>Empties it; throws with the reason when it can't.</summary>
        void Empty();
    }

    /// <summary>
    /// Junk File Cleaner backend, split in two:
    /// <list type="bullet">
    /// <item>JunkCleanerManager.cs (this file) - where to look, scanning,
    /// settings.</item>
    /// <item>JunkCleanerManager.Clean.cs - deleting, the Recycle Bin, and
    /// telling apart "in use", "needs admin" and real failures.</item>
    /// </list>
    /// </summary>
    public static partial class JunkCleanerManager
    {
        public static readonly IReadOnlyList<JunkCategory> AllCategories = Enum.GetValues<JunkCategory>();

        public static readonly TimeSpan RecentLimit = TimeSpan.FromHours(24);

        // =================================================================
        //  Where to look
        // =================================================================

        public static string Title(JunkCategory c) => c switch
        {
            JunkCategory.WindowsTemp => "Temporary files",
            JunkCategory.BrowserCache => "Browser caches",
            JunkCategory.ThumbnailCache => "Thumbnail cache",
            JunkCategory.RecycleBin => "Recycle Bin",
            JunkCategory.WindowsUpdateCache => "Windows Update downloads",
            JunkCategory.LogFiles => "Old Windows logs",
            JunkCategory.ErrorReports => "Crash dumps & error reports",
            _ => c.ToString(),
        };

        /// <summary>A Chromium-based browser: name, process, and its "User Data" folder under %LOCALAPPDATA%.</summary>
        private static readonly (string Name, string Process, string UserData)[] ChromiumBrowsers =
        {
            ("Google Chrome", "chrome", @"Google\Chrome\User Data"),
            ("Microsoft Edge", "msedge", @"Microsoft\Edge\User Data"),
            ("Brave", "brave", @"BraveSoftware\Brave-Browser\User Data"),
            ("Vivaldi", "vivaldi", @"Vivaldi\User Data"),
        };

        /// <summary>Cache folders inside a Chromium profile - only caches, never history, cookies or passwords.</summary>
        private static readonly string[] ChromiumCacheFolders = { "Cache", "Code Cache", "GPUCache" };

        private static string Sub(string root, string relative) =>
            Path.Combine(new[] { root }.Concat(relative.Split('\\', '/')).ToArray());

        /// <summary>
        /// Every place a category covers on this PC. Browser caches cover
        /// every installed browser and every profile (the old view looked at
        /// one browser picked in a list, and only its "Default" profile).
        /// </summary>
        public static List<JunkLocation> Locations(JunkCategory category, JunkEnvironment env, bool skipRecent)
        {
            var minAge = skipRecent ? RecentLimit : TimeSpan.Zero;
            switch (category)
            {
                case JunkCategory.WindowsTemp:
                    return new()
                    {
                        new JunkLocation
                        {
                            Category = category,
                            Title = "Temporary files",
                            Roots = Distinct(env.Temp, Sub(env.Windows, "Temp")),
                            MinAge = minAge,
                            RemoveEmptyFolders = true,
                        },
                    };

                case JunkCategory.BrowserCache:
                    var list = new List<JunkLocation>();
                    foreach (var (name, process, userData) in ChromiumBrowsers)
                    {
                        var roots = ChromiumProfiles(Sub(env.LocalAppData, userData))
                            .SelectMany(p => ChromiumCacheFolders.Select(c => Path.Combine(p, c)))
                            .Where(Directory.Exists).ToList();
                        if (roots.Count > 0) list.Add(new JunkLocation { Category = category, Title = name, Roots = roots, ProcessName = process });
                    }
                    foreach (var (name, folder) in new[] { ("Opera", "Opera Stable"), ("Opera GX", "Opera GX Stable") })
                    {
                        string baseDir = Sub(env.LocalAppData, @"Opera Software\" + folder);
                        var roots = new[] { "Cache", @"Default\Cache", "Code Cache", @"Default\Code Cache", "GPUCache" }
                            .Select(r => Sub(baseDir, r)).Where(Directory.Exists).ToList();
                        if (roots.Count > 0) list.Add(new JunkLocation { Category = category, Title = name, Roots = roots, ProcessName = "opera" });
                    }
                    string ff = Sub(env.LocalAppData, @"Mozilla\Firefox\Profiles");
                    var ffRoots = SafeSubfolders(ff).SelectMany(p => new[] { Path.Combine(p, "cache2"), Path.Combine(p, "startupCache") })
                        .Where(Directory.Exists).ToList();
                    if (ffRoots.Count > 0) list.Add(new JunkLocation { Category = category, Title = "Mozilla Firefox", Roots = ffRoots, ProcessName = "firefox" });
                    return list;

                case JunkCategory.ThumbnailCache:
                    // Only the thumbnail databases - the old scan listed everything in that folder.
                    return new()
                    {
                        new JunkLocation
                        {
                            Category = category,
                            Title = "Thumbnail cache",
                            Roots = new() { Sub(env.LocalAppData, @"Microsoft\Windows\Explorer") },
                            Patterns = new() { "thumbcache_*.db" },
                        },
                    };

                case JunkCategory.WindowsUpdateCache:
                    return new()
                    {
                        new JunkLocation
                        {
                            Category = category,
                            Title = "Windows Update downloads",
                            Roots = new() { Sub(env.Windows, @"SoftwareDistribution\Download") },
                            MinAge = minAge,
                            NeedsAdmin = true,
                            RemoveEmptyFolders = true,
                        },
                    };

                case JunkCategory.LogFiles:
                    // Only log files, and only older ones - the old scan offered every file in Windows\Logs.
                    return new()
                    {
                        new JunkLocation
                        {
                            Category = category,
                            Title = "Old Windows logs",
                            Roots = new() { Sub(env.Windows, "Logs") },
                            Patterns = new() { "*.log", "*.etl", "*.old", "*.lo_", "*.bak" },
                            MinAge = TimeSpan.FromDays(7),
                            NeedsAdmin = true,
                        },
                    };

                case JunkCategory.ErrorReports:
                    return new()
                    {
                        new JunkLocation
                        {
                            Category = category,
                            Title = "Crash dumps & error reports",
                            Roots = Distinct(
                                Sub(env.LocalAppData, "CrashDumps"),
                                Sub(env.LocalAppData, @"Microsoft\Windows\WER\ReportArchive"),
                                Sub(env.LocalAppData, @"Microsoft\Windows\WER\ReportQueue"),
                                Sub(env.ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                                Sub(env.ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
                            MinAge = minAge,
                            RemoveEmptyFolders = true,
                        },
                    };

                case JunkCategory.RecycleBin:
                    return new() { new JunkLocation { Category = category, Title = "Recycle Bin" } };

                default:
                    return new();
            }
        }

        /// <summary>"Default", "Profile 1" …, "Guest Profile" under a Chromium User Data folder.</summary>
        public static IEnumerable<string> ChromiumProfiles(string userData) =>
            SafeSubfolders(userData).Where(p =>
            {
                string n = Path.GetFileName(p);
                return n == "Default" || n == "Guest Profile" || n.StartsWith("Profile ", StringComparison.Ordinal);
            });

        private static IEnumerable<string> SafeSubfolders(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetDirectories(folder) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>The same folder twice (TEMP can be Windows\Temp) would list every file twice.</summary>
        private static List<string> Distinct(params string[] folders)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string f in folders)
            {
                string key;
                try
                {
                    key = Path.GetFullPath(f).TrimEnd('\\', '/');
                }
                catch (Exception)
                {
                    continue;
                }
                if (seen.Add(key)) result.Add(f);
            }
            return result;
        }

        /// <summary>Short description of where a location looks, for the result row.</summary>
        public static string DescribeWhere(JunkLocation location, JunkEnvironment env)
        {
            if (location.Category == JunkCategory.RecycleBin) return "Every drive";
            if (location.Category == JunkCategory.BrowserCache)
            {
                int profiles = location.Roots.Select(r => Path.GetDirectoryName(r)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return $"Cache only - history, cookies and passwords stay · {profiles} {JunkText.Plural(profiles, "profile", "profiles")}";
            }
            var parts = location.Roots.Select(r => ShortPath(r, env)).ToList();
            string where = string.Join(" · ", parts);
            if (location.Patterns.Count > 0) where += $" ({string.Join(", ", location.Patterns)})";
            if (location.MinAge >= TimeSpan.FromDays(2)) where += $" · older than {location.MinAge.TotalDays:0} days";
            else if (location.MinAge > TimeSpan.Zero) where += " · older than a day";
            return where;
        }

        private static string ShortPath(string path, JunkEnvironment env)
        {
            foreach (var (root, name) in new[] { (env.Temp, "%TEMP%"), (env.LocalAppData, "%LOCALAPPDATA%"), (env.ProgramData, "%PROGRAMDATA%"), (env.Windows, "%WINDIR%") })
            {
                string r = root.TrimEnd('\\', '/');
                if (r.Length > 0 && path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                    return name + path[r.Length..];
            }
            return path;
        }

        // =================================================================
        //  Scanning
        // =================================================================

        /// <summary>
        /// Lists the junk in the chosen categories, one result row per
        /// location (each browser is its own row). Off the UI thread; a folder
        /// Windows won't open is counted and skipped; files changed recently
        /// are left alone when asked.
        /// </summary>
        public static Task<List<JunkGroup>> ScanAsync(
            JunkScanOptions options,
            JunkEnvironment? env = null,
            IRecycleBin? recycleBin = null,
            IProgress<JunkProgress>? progress = null,
            CancellationToken token = default,
            DateTime? now = null) =>
            Task.Run(() =>
            {
                env ??= JunkEnvironment.Current;
                recycleBin ??= DefaultRecycleBin;
                var clock = now ?? DateTime.Now;
                var categories = AllCategories.Where(options.Categories.Contains).ToList();
                var groups = new List<JunkGroup>();
                for (int i = 0; i < categories.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var category = categories[i];
                    progress?.Report(new JunkProgress(i * 100.0 / categories.Count, $"Looking in {Title(category).ToLowerInvariant()}…"));
                    foreach (var location in Locations(category, env, options.SkipRecent))
                    {
                        var group = new JunkGroup { Location = location, Where = DescribeWhere(location, env) };
                        if (category == JunkCategory.RecycleBin)
                        {
                            var info = recycleBin.Query();
                            if (info is not { Items: > 0 } bin) continue;
                            group.RecycleBinItems = bin.Items;
                            group.RecycleBinBytes = bin.Bytes;
                        }
                        else
                        {
                            ScanLocation(group, clock, token);
                            if (group.Items.Count == 0) continue;
                        }
                        group.Refresh();
                        groups.Add(group);
                    }
                }

                long total = groups.Sum(g => g.Bytes);
                foreach (var g in groups)
                {
                    g.Share = total > 0 ? (double)g.Bytes / total : 0;
                    g.Note = NoteFor(g);
                }
                progress?.Report(new JunkProgress(100, "Done"));
                return groups;
            }, token);

        private static void ScanLocation(JunkGroup group, DateTime now, CancellationToken token)
        {
            var location = group.Location;
            var patterns = location.Patterns.Select(WildcardToRegex).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in location.Roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in WalkFiles(root, group, token))
                {
                    if (patterns.Count > 0 && !patterns.Any(p => p.IsMatch(file.Name))) continue;
                    DateTime modified;
                    long length;
                    try
                    {
                        modified = file.LastWriteTime;
                        length = file.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;   // vanished while listing
                    }
                    if (location.MinAge > TimeSpan.Zero && now - modified < location.MinAge)
                    {
                        group.RecentSkipped++;
                        continue;
                    }
                    if (!seen.Add(file.FullName)) continue;
                    group.Items.Add(new JunkFile
                    {
                        FullPath = file.FullName,
                        RelativePath = Path.GetRelativePath(root, file.FullName),
                        Root = root,
                        SizeBytes = length,
                        Modified = modified,
                        Group = group,
                    });
                }
            }
            group.Items.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));   // biggest first
        }

        /// <summary>
        /// Every file under <paramref name="root"/>, one folder at a time so
        /// a folder Windows won't open is skipped, not fatal. Folder links
        /// (junctions, symlinks) are never followed: the old walk followed
        /// them, so a link inside %TEMP% could lead it into - and delete -
        /// files that aren't junk at all.
        /// </summary>
        private static IEnumerable<FileInfo> WalkFiles(string root, JunkGroup group, CancellationToken token)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = dir.GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    group.BlockedFolders++;
                    continue;
                }
                foreach (var entry in entries)
                {
                    FileAttributes attrs;
                    try
                    {
                        attrs = entry.Attributes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;   // never follow or delete through a link
                    if (entry is DirectoryInfo sub) pending.Push(sub);
                    else if (entry is FileInfo file) yield return file;
                }
            }
        }

        private static Regex WildcardToRegex(string pattern) =>
            new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>What to know about a row before cleaning it.</summary>
        public static string NoteFor(JunkGroup g)
        {
            var notes = new List<string>();
            if (g.Location.ProcessName is string p && IsProcessRunning(p))
                notes.Add($"{g.Title} is open - close it first, or files it's using are skipped");
            if (g.Location.NeedsAdmin && !IsAdministrator())
                notes.Add("Run Smart Tools as administrator to clean this");
            if (g.IsRecycleBin) notes.Add("Empties the Recycle Bin on every drive - those files can't be restored afterwards");
            if (g.RecentSkipped > 0) notes.Add($"{g.RecentSkipped:N0} recent {JunkText.Plural(g.RecentSkipped, "file", "files")} left alone");
            if (g.BlockedFolders > 0) notes.Add($"{g.BlockedFolders:N0} {JunkText.Plural(g.BlockedFolders, "folder", "folders")} couldn't be opened");
            return string.Join(" · ", notes);
        }

        /// <summary>Set by tests; otherwise asks Windows.</summary>
        public static Func<string, bool>? ProcessCheckOverride { get; set; }

        public static bool IsProcessRunning(string name)
        {
            if (ProcessCheckOverride != null) return ProcessCheckOverride(name);
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var p in processes) p.Dispose();
                return processes.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Set by tests; otherwise asks Windows.</summary>
        public static bool? AdministratorOverride { get; set; }

        public static bool IsAdministrator()
        {
            if (AdministratorOverride is bool b) return b;
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // =================================================================
        //  Summaries
        // =================================================================

        public static (int Files, long Bytes, int SelectedFiles, long SelectedBytes) Totals(IEnumerable<JunkGroup> groups)
        {
            int files = 0, selected = 0;
            long bytes = 0, selectedBytes = 0;
            foreach (var g in groups)
            {
                files += g.IsRecycleBin ? (int)Math.Min(int.MaxValue, g.RecycleBinItems) : g.Count;
                bytes += g.Bytes;
                selected += g.IsRecycleBin ? (g.SelectedCount > 0 ? (int)Math.Min(int.MaxValue, g.RecycleBinItems) : 0) : g.SelectedCount;
                selectedBytes += g.SelectedBytes;
            }
            return (files, bytes, selected, selectedBytes);
        }

        public static string DescribeScan(IReadOnlyCollection<JunkGroup> groups)
        {
            if (groups.Count == 0) return "Nothing to clean - all clear.";
            var t = Totals(groups);
            return $"Found {JunkText.FormatBytes(t.Bytes)} in {t.Files:N0} {JunkText.Plural(t.Files, "item", "items")} across {groups.Count} {JunkText.Plural(groups.Count, "place", "places")}.";
        }

        public static string DescribeClean(JunkCleanResult r)
        {
            var parts = new List<string>
            {
                r.Cancelled
                    ? $"Stopped - {JunkText.FormatBytes(r.BytesFreed)} freed before stopping"
                    : $"Freed {JunkText.FormatBytes(r.BytesFreed)} · {r.Deleted:N0} {JunkText.Plural(r.Deleted, "file", "files")} deleted",
            };
            if (r.RecycleBinEmptied) parts.Add("Recycle Bin emptied");
            if (r.RecycleBinError != null) parts.Add("Recycle Bin: " + r.RecycleBinError);
            if (r.FoldersRemoved > 0) parts.Add($"{r.FoldersRemoved:N0} empty {JunkText.Plural(r.FoldersRemoved, "folder", "folders")} removed");
            if (r.InUse > 0) parts.Add($"{r.InUse:N0} in use (skipped)");
            if (r.NeedsAdmin > 0) parts.Add($"{r.NeedsAdmin:N0} need administrator rights");
            if (r.Changed > 0) parts.Add($"{r.Changed:N0} changed since the scan (left alone)");
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed");
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "junk_cleaner.json");

        public static JunkSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new JunkSettings();
                var s = JsonSerializer.Deserialize<JunkSettings>(File.ReadAllText(file), JsonOptions) ?? new JunkSettings();
                s.Categories = (s.Categories ?? new()).Where(c => Enum.IsDefined(c)).Distinct().ToList();
                return s;
            }
            catch (Exception)
            {
                return new JunkSettings();
            }
        }

        public static void SaveSettings(JunkSettings settings, string? path = null) =>
            Smart_Tools.WPF.Controllers.JsonSettings.SaveFile(path ?? SettingsPath, settings, JsonOptions);
    }
}
