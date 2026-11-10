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

        /// <summary>The period cut into slices: for each, how long it covered (<see cref="Den"/>) and what each program did (<see cref="Num"/>).</summary>
        public sealed class Slices
        {
            public DateTime[] Times { get; init; } = Array.Empty<DateTime>();
            public double[] Den { get; init; } = Array.Empty<double>();
            public Dictionary<string, double[]> Num { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public static Slices Cut(IReadOnlyList<UsageBucket> buckets, DateTime sinceUtc, TimeSpan period, Func<double[], double> numerator, Func<UsageBucket, double> denominator)
        {
            var den = new double[ChartPoints];
            var num = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            double sliceMinutes = period.TotalMinutes / ChartPoints;
            foreach (var b in buckets)
            {
                int i = (int)Math.Clamp(Math.Floor((b.StartUtc - sinceUtc).TotalMinutes / sliceMinutes), 0, ChartPoints - 1);
                den[i] += denominator(b);
                foreach (var (app, slots) in b.Apps)
                {
                    if (!num.TryGetValue(app, out var arr)) num[app] = arr = new double[ChartPoints];
                    arr[i] += numerator(slots);
                }
            }
            var times = Enumerable.Range(0, ChartPoints).Select(i => sinceUtc.AddMinutes(sliceMinutes * i).ToLocalTime()).ToArray();
            return new Slices { Times = times, Den = den, Num = num };
        }

        /// <summary>Colours of the heaviest programs, then grey for all the others together.</summary>
        public static readonly string[] Palette = { "#3b82f6", "#f59e0b", "#22a06b", "#e5484d", "#8b5cf6", "#14b8a6" };
        public const string OtherColor = "#94a3b8";
        public const int StackedPrograms = 6;

        public static System.Windows.Media.Brush BrushOf(string hex)
        {
            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        /// <summary>The stacked columns: the top programs (first = bottom) and everything else as "Other".</summary>
        public static Controls.StackedData Stack(Slices s, IReadOnlyList<string> topNames, Func<double, string> format, string timeFormat)
        {
            var series = new List<Controls.StackSeries>();
            double[] Per(double[] num) => num.Select((v, i) => s.Den[i] > 0 ? v / s.Den[i] : 0).ToArray();
            for (int k = 0; k < Math.Min(StackedPrograms, topNames.Count); k++)
                if (s.Num.TryGetValue(topNames[k], out var arr))
                    series.Add(new Controls.StackSeries { Name = topNames[k], Brush = BrushOf(Palette[k]), Values = Per(arr) });
            var others = s.Num.Where(kv => !topNames.Take(StackedPrograms).Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            if (others.Count > 0)
            {
                var sum = new double[ChartPoints];
                foreach (var kv in others) for (int i = 0; i < ChartPoints; i++) sum[i] += kv.Value[i];
                series.Add(new Controls.StackSeries { Name = Loc.T("Str_AppUsage_Other", "Other programs"), Brush = BrushOf(OtherColor), Values = Per(sum) });
            }
            return new Controls.StackedData { Times = s.Times, HasData = s.Den, Series = series, Format = format, TimeFormat = timeFormat };
        }

        /// <summary>The slice where all programs together were highest: its time and value.</summary>
        public static (DateTime When, double Value)? Busiest(Slices s)
        {
            (DateTime, double)? best = null;
            for (int i = 0; i < s.Den.Length; i++)
            {
                if (s.Den[i] <= 0) continue;
                double total = s.Num.Values.Sum(a => a[i]) / s.Den[i];
                if (best == null || total > best.Value.Item2) best = (s.Times[i], total);
            }
            return best;
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
