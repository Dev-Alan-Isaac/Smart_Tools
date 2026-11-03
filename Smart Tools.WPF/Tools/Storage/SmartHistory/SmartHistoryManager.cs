using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  SMART Trend History: keeps a reading of every drive's SMART numbers
    //  over time (reads them with DriveHealthManager) and flags values that
    //  are getting worse. Only reads the drives; the history file is the
    //  only thing written, replaced atomically.
    // =====================================================================
    public static class SmartHistoryManager
    {
        /// <summary>Opening the tool adds a reading only when the last one is older than this.</summary>
        public static readonly TimeSpan AutoGap = TimeSpan.FromHours(6);

        private const int MaxReadingsPerDrive = 4000;
        private const int TrimTo = 3000;

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static string FilePath => Path.Combine(AppPaths.DataRoot, "smart_history.json");

        // =================================================================
        //  File
        // =================================================================

        /// <summary>The saved history, or an empty one. A damaged file is kept aside as .bad and never blocks the tool.</summary>
        public static SmartHistoryFile Load(string? path = null)
        {
            path ??= FilePath;
            try
            {
                if (!File.Exists(path)) return new SmartHistoryFile();
                var file = JsonSerializer.Deserialize<SmartHistoryFile>(File.ReadAllText(path));
                return file ?? new SmartHistoryFile();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                try { File.Move(path, path + ".bad", overwrite: true); }
                catch (Exception) { /* leave it */ }
                return new SmartHistoryFile();
            }
        }

        public static void Save(SmartHistoryFile file, string? path = null)
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, Json));
        }

        // =================================================================
        //  Recording
        // =================================================================

        /// <summary>
        /// Adds a reading for every drive that reports SMART. Unless <paramref name="force"/>, a drive whose last
        /// reading is newer than <paramref name="minGap"/> is left alone. Returns how many readings were added.
        /// </summary>
        public static int Record(SmartHistoryFile file, IEnumerable<DriveHealthInfo> drives, DateTime now, TimeSpan minGap, bool force)
        {
            int added = 0;
            foreach (var d in drives)
            {
                if (d.Smart is not SmartData s) continue;
                string key = KeyFor(d);
                var history = file.Drives.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (history == null)
                {
                    history = new SmartDriveHistory { Key = key };
                    file.Drives.Add(history);
                }
                history.Name = d.FriendlyName;
                history.IsNvme = d.IsNvme;
                history.IsSsd = d.Kind == DriveKind.Ssd || d.IsNvme;

                var last = history.Readings.LastOrDefault();
                if (!force && last != null && now - last.When < minGap) continue;

                history.Readings.Add(new SmartReading
                {
                    When = now,
                    Temperature = s.TemperatureCelsius,
                    PowerOnHours = s.PowerOnHours,
                    PowerCycles = s.PowerCycles,
                    Reallocated = s.ReallocatedSectors,
                    Pending = s.PendingSectors,
                    Uncorrectable = s.UncorrectableSectors,
                    MediaErrors = s.MediaErrors,
                    UnsafeShutdowns = s.UnsafeShutdowns,
                    BytesWritten = s.BytesWritten,
                    LifeRemaining = s.LifeRemainingPercent,
                    Spare = s.AvailableSparePercent,
                });
                added++;
                if (history.Readings.Count > MaxReadingsPerDrive)
                    history.Readings.RemoveRange(0, history.Readings.Count - TrimTo);   // the oldest go first
            }
            return added;
        }

        public static string KeyFor(DriveHealthInfo d) =>
            !string.IsNullOrWhiteSpace(d.SerialNumber) ? d.SerialNumber.Trim() : d.DeviceId;

        // =================================================================
        //  Reading the history
        // =================================================================

        public static List<(DateTime When, double Value)> Series(SmartDriveHistory h, SmartMetric m) =>
            h.Readings.Where(r => r.Get(m).HasValue).Select(r => (r.When, r.Get(m)!.Value)).ToList();

        public static bool HasMetric(SmartDriveHistory h, SmartMetric m) => h.Readings.Any(r => r.Get(m).HasValue);

        /// <summary>"▲ +3 reallocated" style note on what got worse (or "▼ ..." for temperature changes aren't flagged) since the reading before.</summary>
        public static string DescribeChange(SmartReading now, SmartReading? before)
        {
            if (before == null) return "";
            var parts = new List<string>();
            void Counter(string name, long? a, long? b)
            {
                if (a is long x && b is long y && x > y) parts.Add($"▲ +{x - y:N0} {name}");
            }
            Counter("reallocated", now.Reallocated, before.Reallocated);
            Counter("pending", now.Pending, before.Pending);
            Counter("uncorrectable", now.Uncorrectable, before.Uncorrectable);
            Counter("media errors", now.MediaErrors, before.MediaErrors);
            Counter("unsafe shutdowns", now.UnsafeShutdowns, before.UnsafeShutdowns);
            if (now.LifeRemaining is int l && before.LifeRemaining is int lb && l < lb) parts.Add($"▲ life −{lb - l}%");
            return string.Join(" · ", parts);
        }

        // =================================================================
        //  Warnings
        // =================================================================

        /// <summary>What got worse over the recorded time. Empty when nothing did.</summary>
        public static List<SmartWarning> Analyze(SmartDriveHistory h, DateTime now)
        {
            var list = new List<SmartWarning>();
            if (h.Readings.Count == 0) return list;
            var first = h.Readings[0];
            var last = h.Readings[^1];

            void Counter(string label, Func<SmartReading, long?> get, long dangerAt)
            {
                var pts = h.Readings.Where(r => get(r).HasValue).ToList();
                if (pts.Count < 2) return;
                long from = get(pts[0])!.Value, to = get(pts[^1])!.Value;
                if (to <= from) return;
                // The date it first went up, so the message says when it started.
                var firstRise = pts.Skip(1).First(r => get(r)!.Value > from);
                long delta = to - from;
                var level = delta >= dangerAt ? SmartWarningLevel.Danger : SmartWarningLevel.Warning;
                list.Add(new SmartWarning(level,
                    $"{label} went from {from:N0} to {to:N0} (+{delta:N0}); it first rose on {firstRise.When:yyyy-MM-dd}."));
            }

            Counter("Reallocated sectors", r => r.Reallocated, 20);
            Counter("Pending sectors", r => r.Pending, 1);
            Counter("Uncorrectable sectors", r => r.Uncorrectable, 1);
            Counter("Media errors", r => r.MediaErrors, 1);

            // Unsafe shutdowns: only worth mentioning when several happened recently.
            var recent = h.Readings.Where(r => now - r.When <= TimeSpan.FromDays(30) && r.UnsafeShutdowns.HasValue).ToList();
            if (recent.Count >= 2 && recent[^1].UnsafeShutdowns!.Value - recent[0].UnsafeShutdowns!.Value >= 5)
                list.Add(new SmartWarning(SmartWarningLevel.Warning,
                    $"{recent[^1].UnsafeShutdowns!.Value - recent[0].UnsafeShutdowns!.Value} unsafe shutdowns in the last 30 days - power cuts or forced restarts can damage a drive."));

            // Wear
            if (last.LifeRemaining is int life)
            {
                var old = h.Readings.Where(r => r.LifeRemaining.HasValue && now - r.When >= TimeSpan.FromDays(30)).LastOrDefault();
                if (life <= 10) list.Add(new SmartWarning(SmartWarningLevel.Danger, $"Only {life}% of the drive's life is left. Back up and plan a replacement."));
                else if (life <= 25) list.Add(new SmartWarning(SmartWarningLevel.Warning, $"{life}% of the drive's life is left."));
                if (old?.LifeRemaining is int before && before - life >= 5)
                    list.Add(new SmartWarning(SmartWarningLevel.Warning, $"Life remaining fell {before - life} points since {old.When:yyyy-MM-dd} ({before}% to {life}%)."));
            }
            if (last.Spare is int spare && last.Spare < 20 && h.IsNvme)
                list.Add(new SmartWarning(SmartWarningLevel.Warning, $"Spare capacity is down to {spare}%."));

            // Heat
            int hot = h.IsNvme ? 70 : h.IsSsd ? 65 : 55;
            var week = h.Readings.Where(r => r.Temperature.HasValue && now - r.When <= TimeSpan.FromDays(7)).ToList();
            if (week.Count > 0 && week.Max(r => r.Temperature!.Value) >= hot)
                list.Add(new SmartWarning(SmartWarningLevel.Warning, $"It reached {week.Max(r => r.Temperature!.Value)} °C this week (a drive like this should stay under {hot} °C). Check airflow."));

            return list.OrderByDescending(w => w.Level).ToList();
        }

        /// <summary>Plain facts about the recorded time: span, how much was written per day, and a wear estimate.</summary>
        public static List<string> Facts(SmartDriveHistory h)
        {
            var facts = new List<string>();
            if (h.Readings.Count < 2) return facts;
            var first = h.Readings[0];
            var last = h.Readings[^1];
            double days = (last.When - first.When).TotalDays;
            if (days < 0.5) return facts;

            if (first.BytesWritten is long w0 && last.BytesWritten is long w1 && w1 > w0)
                facts.Add($"About {SmartHistoryText.FormatBytes((long)((w1 - w0) / days))} written per day over {days:0} days.");
            if (first.LifeRemaining is int l0 && last.LifeRemaining is int l1 && l1 < l0 && days >= 14)
            {
                double perMonth = (l0 - l1) / days * 30;
                facts.Add($"Wearing about {perMonth:0.#} points a month - roughly {l1 / perMonth / 12:0.#} years left at this pace.");
            }
            return facts;
        }

        // =================================================================
        //  Export
        // =================================================================

        public static string BuildCsv(SmartDriveHistory h)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("When,Temperature C,Power-on hours,Power cycles,Reallocated,Pending,Uncorrectable,Media errors,Unsafe shutdowns,Bytes written,Life remaining %,Spare %");
            foreach (var r in h.Readings)
            {
                sb.AppendJoin(',', r.When.ToString("yyyy-MM-dd HH:mm:ss", inv), Cell(r.Temperature), Cell(r.PowerOnHours), Cell(r.PowerCycles),
                    Cell(r.Reallocated), Cell(r.Pending), Cell(r.Uncorrectable), Cell(r.MediaErrors), Cell(r.UnsafeShutdowns),
                    Cell(r.BytesWritten), Cell(r.LifeRemaining), Cell(r.Spare)).AppendLine();
            }
            return sb.ToString();

            static string Cell<T>(T? v) where T : struct => v?.ToString() ?? "";
        }
    }
}
