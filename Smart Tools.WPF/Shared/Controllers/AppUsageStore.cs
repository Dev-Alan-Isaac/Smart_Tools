using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One minute of readings: for every program, a few running totals (the meaning of each slot belongs to the tool).</summary>
    public sealed class UsageBucket
    {
        /// <summary>Minutes since the Unix epoch, UTC.</summary>
        public long Minute { get; set; }

        /// <summary>How many readings were taken in this minute (to turn sums into averages).</summary>
        public int Samples { get; set; }

        public Dictionary<string, double[]> Apps { get; set; } = new();

        public DateTime StartUtc => DateTime.UnixEpoch.AddMinutes(Minute);
    }

    public sealed class AppUsageFile
    {
        public List<UsageBucket> Buckets { get; set; } = new();
    }

    /// <summary>
    /// Per-minute history of what each program used, kept for a day and saved to a small JSON file. Program names only (no paths,
    /// no window titles). Thread-safe: a recorder writes from its own thread while a page reads copies.
    /// </summary>
    public sealed class AppUsageStore
    {
        private readonly object _gate = new();
        private readonly string _name;
        private readonly int _width;
        private readonly TimeSpan _keep;
        private List<UsageBucket> _buckets = new();
        private bool _dirty;

        public AppUsageStore(string settingsName, int width, TimeSpan keep)
        {
            _name = settingsName;
            _width = width;
            _keep = keep;
        }

        public void Load()
        {
            var file = JsonSettings.Load<AppUsageFile>(_name);
            lock (_gate)
            {
                _buckets = file.Buckets.Where(b => b.Apps != null).OrderBy(b => b.Minute).ToList();
                foreach (var b in _buckets)
                    foreach (string key in b.Apps.Keys.ToList())
                        if (b.Apps[key].Length != _width) { var fixedLength = new double[_width]; Array.Copy(b.Apps[key], fixedLength, Math.Min(_width, b.Apps[key].Length)); b.Apps[key] = fixedLength; }
                PruneLocked(DateTime.UtcNow);
            }
        }

        public void Save()
        {
            AppUsageFile file;
            lock (_gate)
            {
                if (!_dirty) return;
                PruneLocked(DateTime.UtcNow);
                file = new AppUsageFile { Buckets = _buckets.Select(Clone).ToList() };
                _dirty = false;
            }
            JsonSettings.Save(_name, file);
        }

        public void Clear()
        {
            lock (_gate) { _buckets.Clear(); _dirty = true; }
            Save();
        }

        private static long MinuteOf(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);

        private UsageBucket CurrentLocked(DateTime utcNow)
        {
            long minute = MinuteOf(utcNow);
            if (_buckets.Count > 0 && _buckets[^1].Minute == minute) return _buckets[^1];
            var bucket = new UsageBucket { Minute = minute };
            _buckets.Add(bucket);
            PruneLocked(utcNow);
            return bucket;
        }

        private void PruneLocked(DateTime utcNow)
        {
            long oldest = MinuteOf(utcNow - _keep);
            int drop = 0;
            while (drop < _buckets.Count && _buckets[drop].Minute < oldest) drop++;
            if (drop > 0) _buckets.RemoveRange(0, drop);
        }

        /// <summary>Counts one reading in the current minute.</summary>
        public void Tick(DateTime utcNow)
        {
            lock (_gate) { CurrentLocked(utcNow).Samples++; _dirty = true; }
        }

        private double[] SlotsLocked(UsageBucket bucket, string app)
        {
            if (!bucket.Apps.TryGetValue(app, out var slots)) bucket.Apps[app] = slots = new double[_width];
            return slots;
        }

        public void Add(DateTime utcNow, string app, int slot, double value)
        {
            if (value == 0) return;
            lock (_gate) { SlotsLocked(CurrentLocked(utcNow), app)[slot] += value; _dirty = true; }
        }

        public void Peak(DateTime utcNow, string app, int slot, double value)
        {
            lock (_gate)
            {
                var slots = SlotsLocked(CurrentLocked(utcNow), app);
                if (value > slots[slot]) { slots[slot] = value; _dirty = true; }
            }
        }

        public bool IsEmpty { get { lock (_gate) return _buckets.Count == 0; } }

        /// <summary>When the first kept minute began (UTC), or null when there is nothing.</summary>
        public DateTime? FirstUtc { get { lock (_gate) return _buckets.Count == 0 ? null : _buckets[0].StartUtc; } }

        /// <summary>Copies of the minutes since <paramref name="sinceUtc"/> (safe to read while recording goes on).</summary>
        public List<UsageBucket> Since(DateTime sinceUtc)
        {
            long from = MinuteOf(sinceUtc);
            lock (_gate) return _buckets.Where(b => b.Minute >= from).Select(Clone).ToList();
        }

        private static UsageBucket Clone(UsageBucket b) => new()
        {
            Minute = b.Minute,
            Samples = b.Samples,
            Apps = b.Apps.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone()),
        };
    }

    /// <summary>Shared maths and wording for the per-program history pages.</summary>
    public static class UsageMath
    {
        public const int ChartPoints = 60;

        /// <summary>
        /// One chart line for a program: the period cut into <see cref="ChartPoints"/> slices, each the sum of <paramref name="numerator"/>
        /// over the sum of <paramref name="denominator"/> of the minutes inside it (so an average, or a rate per second).
        /// </summary>
        public static List<(DateTime When, double Value)> Series(IReadOnlyList<UsageBucket> buckets, DateTime sinceUtc, TimeSpan period, string? app,
            Func<double[], double> numerator, Func<UsageBucket, double> denominator)
        {
            var num = new double[ChartPoints];
            var den = new double[ChartPoints];
            double sliceMinutes = period.TotalMinutes / ChartPoints;
            foreach (var b in buckets)
            {
                int i = (int)Math.Clamp(Math.Floor((b.StartUtc - sinceUtc).TotalMinutes / sliceMinutes), 0, ChartPoints - 1);
                den[i] += denominator(b);
                if (app == null)
                    foreach (var slots in b.Apps.Values) num[i] += numerator(slots);
                else if (b.Apps.TryGetValue(app, out var mine)) num[i] += numerator(mine);
            }
            var points = new List<(DateTime, double)>();
            for (int i = 0; i < ChartPoints; i++)
                if (den[i] > 0)
                    points.Add((sinceUtc.AddMinutes(sliceMinutes * (i + 0.5)).ToLocalTime(), num[i] / den[i]));
            return points;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Bytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
            return bytes.ToString(i == 0 ? "0" : bytes >= 100 ? "0" : "0.0", Inv) + " " + units[i];
        }

        public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

        public static string Megabytes(double mb) => mb >= 1024 ? (mb / 1024).ToString("0.0", Inv) + " GB" : mb.ToString("0", Inv) + " MB";

        public static string Percent(double v) => v.ToString(v >= 10 ? "0" : "0.0", Inv) + " %";
    }
}
